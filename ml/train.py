"""
AlphaZero 学習ループ
  1 イテレーション = 自己対局 N 局 → 学習 K ステップ
"""

import torch
import torch.nn.functional as F
from torch.optim import Adam
from torch.utils.tensorboard import SummaryWriter
from pathlib import Path
import json, time

from network   import ShogiNet, build_net
from self_play import PrioritizedReplayBuffer, generate_games

# NativeAOT が使えればそちらを優先、なければ Python.NET にフォールバック
try:
    from native_game_env   import NativeGameEnv   as _Env
    from batched_mcts_native import BatchedNativeMCTS as MCTS
    _USE_NATIVE = True
except Exception as _e:
    from game_env     import GameEnv              as _Env   # type: ignore
    from batched_mcts import BatchedGumbelMCTS    as MCTS   # type: ignore
    _USE_NATIVE = False

# ── ハイパーパラメータ ─────────────────────────────────────────────
HP = dict(
    # ネット構造（固定：変更すると過去の重みと互換性がなくなる）
    num_blocks   = 10,
    channels     = 128,

    # MCTS - Batched Gumbel AlphaZero（環境に合わせて変更可）
    num_sims        = 400,    # CPU: 32、GPU(T4): 400
    mcts_batch_size = 8,      # CPU: 1〜2、GPU(T4): 8〜16

    # 自己対局
    games_per_iter = 50,      # CPU: 20、GPU(T4): 100

    # 学習
    batch_size     = 256,
    train_steps    = 200,     # 1 イテレーションあたりの勾配更新回数
    lr             = 1e-3,
    weight_decay   = 1e-4,
    buffer_size    = 200_000,
    min_buffer     = 500,     # 学習開始に必要な最低サンプル数

    # KataGo / PER / Value Prefix の重み
    lambda_ownership = 0.15,  # ownership 損失の重み
    lambda_vprefix   = 0.5,   # value prefix 損失の重み
    per_alpha        = 0.6,   # PER: 優先度の鋭さ
    per_beta         = 0.4,   # PER: importance sampling 補正強度

    # 実行
    num_iters      = 100,
    save_every     = 5,       # 何イテレーションごとにチェックポイントを保存するか
    checkpoint_dir = "checkpoints",
)


# ── 学習ステップ（KataGo + PER + Value Prefix 対応）───────────────────
def train_step(net: ShogiNet, optimizer: torch.optim.Optimizer,
               tensors, policies, values, vprefixes, ownerships,
               weights, device: torch.device,
               λ_own: float = 0.15, λ_vp: float = 0.5):
    """
    1 バッチの勾配更新。
    返り値: (policy_loss, value_loss, own_loss, vp_loss, total_loss, value_errors)
    value_errors は PER の優先度更新に使用。
    """
    net.train()
    x   = torch.from_numpy(tensors   ).float().to(device)
    pi  = torch.from_numpy(policies  ).float().to(device)
    z   = torch.from_numpy(values    ).float().to(device)
    zp  = torch.from_numpy(vprefixes ).float().to(device)
    own = torch.from_numpy(ownerships).float().to(device)
    w   = torch.from_numpy(weights   ).float().to(device)  # PER 重み

    p_logit, v, own_pred, vp_pred = net(x, return_aux=True)

    # Policy: cross-entropy（PER 重み付き）
    log_p       = F.log_softmax(p_logit, dim=1)
    policy_loss = (w * -(pi * log_p).sum(dim=1)).mean()

    # Value: MSE（PER 重み付き）
    value_err   = v - z                           # TD 誤差（PER 更新に使用）
    value_loss  = (w * value_err.pow(2)).mean()

    # Value Prefix: MSE
    vp_loss    = (w * (vp_pred - zp).pow(2)).mean()

    # Ownership: MSE（KataGo 補助タスク）
    own_loss   = (w * (own_pred - own).pow(2).mean(dim=1)).mean()

    loss = policy_loss + value_loss + λ_vp * vp_loss + λ_own * own_loss
    optimizer.zero_grad()
    loss.backward()
    optimizer.step()

    return (policy_loss.item(), value_loss.item(),
            own_loss.item(), vp_loss.item(), loss.item(),
            value_err.detach().abs().cpu().numpy())


# ── メインループ ──────────────────────────────────────────────────────
def run(hp: dict | None = None, pretrained_path: str | None = None,
        initial_buffer=None, start_iter: int = 0):
    cfg = HP | (hp or {})

    env = _Env()
    print(f"ゲームエンジン: {'NativeAOT (ctypes)' if _USE_NATIVE else 'Python.NET (fallback)'}")
    net, device  = build_net(cfg["num_blocks"], cfg["channels"])

    if pretrained_path:
        net.load(pretrained_path, device)
        print(f"継続学習: {pretrained_path} から重みをロード")
    optimizer    = Adam(net.parameters(), lr=cfg["lr"],
                        weight_decay=cfg["weight_decay"])
    mcts         = MCTS(env, net, device,
                        n_sims=cfg["num_sims"],
                        batch_size=cfg.get("mcts_batch_size", 1))
    buf          = initial_buffer or PrioritizedReplayBuffer(
                        max_size=cfg["buffer_size"],
                        alpha=cfg.get("per_alpha", 0.6),
                        beta=cfg.get("per_beta",  0.4))

    ckpt_dir = Path(cfg["checkpoint_dir"])
    ckpt_dir.mkdir(exist_ok=True)
    runs_dir = cfg.get("tensorboard_dir") or str(ckpt_dir / "runs")
    writer = SummaryWriter(log_dir=runs_dir)

    # 既存の log.json を読み込んで引き継ぐ
    log_path = ckpt_dir / "log.json"
    if log_path.exists() and start_iter > 0:
        with open(log_path, encoding="utf-8") as f:
            log = json.load(f)
    else:
        log = []

    total_iters = start_iter + cfg["num_iters"]
    print(f"\n{'='*60}")
    print(f"変成将棋 AlphaZero 学習開始  デバイス={device}"
          + (f"  (iter {start_iter+1}〜{total_iters})" if start_iter > 0 else ""))
    print(f"{'='*60}\n")

    for it in range(start_iter + 1, total_iters + 1):
        t0 = time.time()
        print(f"[Iter {it}/{total_iters}]")

        # ── 自己対局 ────────────────────────────────────────────────
        generate_games(env, mcts, cfg["games_per_iter"], buf)

        if len(buf) < cfg["min_buffer"]:
            print(f"  buffer not enough ({len(buf)} < {cfg['min_buffer']}), skip training")
            continue

        # ── 学習 ────────────────────────────────────────────────────
        pl_sum = vl_sum = own_sum = vp_sum = 0.0
        λ_own = cfg.get("lambda_ownership", 0.15)
        λ_vp  = cfg.get("lambda_vprefix",   0.5)

        for _ in range(cfg["train_steps"]):
            tensors, policies, values, vprefixes, ownerships, weights, indices \
                = buf.sample(cfg["batch_size"])
            pl, vl, own_l, vp_l, _, v_errs = train_step(
                net, optimizer,
                tensors, policies, values, vprefixes, ownerships,
                weights, device, λ_own=λ_own, λ_vp=λ_vp
            )
            pl_sum  += pl
            vl_sum  += vl
            own_sum += own_l
            vp_sum  += vp_l
            buf.update_priorities(indices, v_errs)

        k   = cfg["train_steps"]
        rec = dict(iter=it,
                   policy_loss   =round(pl_sum /k, 4),
                   value_loss    =round(vl_sum /k, 4),
                   ownership_loss=round(own_sum/k, 4),
                   vprefix_loss  =round(vp_sum /k, 4),
                   buffer_size=len(buf),
                   elapsed    =round(time.time()-t0, 1))
        log.append(rec)
        print(f"  p={rec['policy_loss']:.4f}  v={rec['value_loss']:.4f}"
              f"  own={rec['ownership_loss']:.4f}  vp={rec['vprefix_loss']:.4f}"
              f"  buf={len(buf):,}  {rec['elapsed']}s")

        writer.add_scalar("loss/policy",    rec["policy_loss"],    it)
        writer.add_scalar("loss/value",     rec["value_loss"],     it)
        writer.add_scalar("loss/ownership", rec["ownership_loss"], it)
        writer.add_scalar("loss/vprefix",   rec["vprefix_loss"],   it)
        writer.add_scalar("buffer_size",    len(buf),              it)
        writer.flush()

        # ── チェックポイント保存 ─────────────────────────────────────
        if it % cfg["save_every"] == 0:
            ckpt = ckpt_dir / f"model_iter{it:04d}.pt"
            net.save(ckpt)
            print(f"  saved → {ckpt}")

        with open(ckpt_dir / "log.json", "w", encoding="utf-8") as f:
            json.dump(log, f, ensure_ascii=False, indent=2)

    # 最終モデルを保存
    net.save(ckpt_dir / "model_final.pt")
    writer.close()
    print("\n学習完了。model_final.pt を保存しました。")
    return net


if __name__ == "__main__":
    run()
