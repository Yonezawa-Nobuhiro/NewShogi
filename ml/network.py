"""
変成将棋ニューラルネット (AlphaZero 方式)
  入力   : (B, 47, 9, 9)
  Policy : (B, 18873)  ロジット（softmax 前）
  Value  : (B,)        tanh → -1〜+1
"""

import torch
import torch.nn as nn
import torch.nn.functional as F
import numpy as np
from pathlib import Path


# ── 定数 ─────────────────────────────────────────────────────────────
IN_CHANNELS = 47
ACTION_SIZE  = 18_873
BOARD_H, BOARD_W = 9, 9


# ── 残差ブロック ──────────────────────────────────────────────────────
class ResBlock(nn.Module):
    def __init__(self, ch: int):
        super().__init__()
        self.net = nn.Sequential(
            nn.Conv2d(ch, ch, 3, padding=1, bias=False),
            nn.BatchNorm2d(ch),
            nn.ReLU(inplace=True),
            nn.Conv2d(ch, ch, 3, padding=1, bias=False),
            nn.BatchNorm2d(ch),
        )

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return F.relu(x + self.net(x), inplace=True)


# ── メインネット ──────────────────────────────────────────────────────
class ShogiNet(nn.Module):
    def __init__(self, num_blocks: int = 10, channels: int = 128):
        super().__init__()
        # 入力畳み込み
        self.stem = nn.Sequential(
            nn.Conv2d(IN_CHANNELS, channels, 3, padding=1, bias=False),
            nn.BatchNorm2d(channels),
            nn.ReLU(inplace=True),
        )
        # 残差スタック
        self.body = nn.Sequential(*[ResBlock(channels) for _ in range(num_blocks)])

        flat = BOARD_H * BOARD_W  # 81

        # Policy ヘッド: 2ch の 1×1 conv → Linear
        self.p_head = nn.Sequential(
            nn.Conv2d(channels, 2, 1, bias=False),
            nn.BatchNorm2d(2),
            nn.ReLU(inplace=True),
            nn.Flatten(),
            nn.Linear(2 * flat, ACTION_SIZE),
        )

        # Value ヘッド: 1ch の 1×1 conv → 256 → 1
        self.v_head = nn.Sequential(
            nn.Conv2d(channels, 1, 1, bias=False),
            nn.BatchNorm2d(1),
            nn.ReLU(inplace=True),
            nn.Flatten(),
            nn.Linear(flat, 256),
            nn.ReLU(inplace=True),
            nn.Linear(256, 1),
            nn.Tanh(),
        )

        # KataGo: Global Pooling（board-wide context → 補助ヘッドに提供）
        self.global_pool_fc = nn.Linear(channels, channels)

        # Ownership ヘッド（KataGo 補助タスク）: 各升の帰属 (-1〜+1)
        self.ownership_head = nn.Sequential(
            nn.Conv2d(channels, 2, 1, bias=False),
            nn.BatchNorm2d(2),
            nn.ReLU(inplace=True),
            nn.Flatten(),
            nn.Linear(2 * flat, flat),
            nn.Tanh(),   # (B, 81)
        )

        # Value prefix ヘッド（n-step ブートストラップ価値）
        self.value_prefix_head = nn.Sequential(
            nn.Conv2d(channels, 1, 1, bias=False),
            nn.BatchNorm2d(1),
            nn.ReLU(inplace=True),
            nn.Flatten(),
            nn.Linear(flat, 128),
            nn.ReLU(inplace=True),
            nn.Linear(128, 1),
            nn.Tanh(),
        )

    def forward(self, x: torch.Tensor, return_aux: bool = False):
        h = self.body(self.stem(x))

        p = self.p_head(h)
        v = self.v_head(h).squeeze(1)

        if not return_aux:
            return p, v

        # Global pooling で補助ヘッドに盤全体の文脈を付与
        g = F.relu(self.global_pool_fc(h.mean(dim=(2, 3))))  # (B, ch)
        g = g.unsqueeze(2).unsqueeze(3)                      # (B, ch, 1, 1)
        h_aux = h + g                                         # broadcast

        own = self.ownership_head(h_aux)         # (B, 81)
        vp  = self.value_prefix_head(h_aux).squeeze(1)  # (B,)
        return p, v, own, vp

    # ── 推論ヘルパー（勾配不要） ──────────────────────────────────────
    @torch.no_grad()
    def predict_batch(self, tensors: np.ndarray, device: torch.device):
        """
        バッチ推論。tensors: (B, 47, 9, 9) → (policies (B, ACTION_SIZE), values (B,))
        GPU を効率的に使うための核心メソッド。
        """
        self.eval()
        x = torch.from_numpy(tensors).float().to(device)
        p_logits, v = self(x)
        policies = F.softmax(p_logits, dim=1).cpu().numpy()
        values   = v.cpu().numpy()
        return policies, values

    @torch.no_grad()
    def predict(self, tensor: np.ndarray, device: torch.device):
        """
        numpy (47,9,9) → (policy_probs, value)
          policy_probs : np.ndarray shape (ACTION_SIZE,), 確率（softmax済み）
          value        : float (-1〜+1)
        """
        self.eval()
        x = torch.from_numpy(tensor).float().unsqueeze(0).to(device)
        p_logit, v = self(x)
        policy = F.softmax(p_logit, dim=1).squeeze(0).cpu().numpy()
        return policy, float(v.item())

    # ── 保存・読み込み ────────────────────────────────────────────────
    def save(self, path: str | Path):
        torch.save(self.state_dict(), path)

    def load(self, path: str | Path, device: torch.device):
        self.load_state_dict(torch.load(path, map_location=device, weights_only=True))
        return self


# アーキテクチャ定数（変更すると過去の重みと互換性がなくなる）
NUM_BLOCKS = 10
CHANNELS   = 128

def build_net(num_blocks: int = NUM_BLOCKS, channels: int = CHANNELS,
              device: torch.device | None = None) -> tuple["ShogiNet", torch.device]:
    """ネットを構築してデバイスに送り返す。CUDA 非対応 GPU は自動的に CPU に切り替える。"""
    if device is None:
        if torch.cuda.is_available():
            try:
                torch.zeros(1, device="cuda")          # 実際に動くか確認
                device = torch.device("cuda")
            except RuntimeError:
                print("警告: CUDA が利用できません (GPU のコンピュート能力不足)。CPU を使用します。")
                device = torch.device("cpu")
        else:
            device = torch.device("cpu")
    net = ShogiNet(num_blocks, channels).to(device)
    total = sum(p.numel() for p in net.parameters())
    print(f"ShogiNet  blocks={num_blocks}  ch={channels}  params={total:,}  device={device}")
    return net, device
