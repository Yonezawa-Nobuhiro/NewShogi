"""
棋譜ファイル (.kf) を学習サンプルに変換するローダー。

.kf ファイルは1行1SFENの形式（# 始まりはコメント）。
各局面について:
  - tensor    : (47,9,9) の盤面テンソル
  - policy    : 実際に指した手のワンホット分布
  - value     : 対局結果（現手番視点 ±1）
  - value_prefix: 5手先ブートストラップ（データなければ同値）
  - ownership : 各升の帰属

使い方:
    from kifu_loader import load_kifu_to_buffer
    from native_game_env import NativeGameEnv
    from self_play import PrioritizedReplayBuffer

    env = NativeGameEnv()
    buf = PrioritizedReplayBuffer(...)
    load_kifu_to_buffer('my_game.kf', env, buf)
"""

import numpy as np
from pathlib import Path
from self_play import Sample, PrioritizedReplayBuffer

VALUE_PREFIX_N = 5


def load_kifu(kf_path: str, env) -> list[Sample]:
    """
    .kf ファイルを読み込んでサンプルリストを返す。
    指し手は legal_moves を全適用して一致する SFEN を探すことで特定する。
    """
    lines = Path(kf_path).read_text(encoding='utf-8-sig').splitlines()
    sfens = [l.strip() for l in lines
             if l.strip() and not l.startswith('#')]

    if len(sfens) < 2:
        print(f"警告: {kf_path} のSFEN数が少なすぎます ({len(sfens)})")
        return []

    # 終局局面の手番側が負け
    final_sfen = sfens[-1]
    final_player = env.current_player(final_sfen)
    # 合法手なし = 手番側の負け
    result = -1 if final_player == "先手" else 1  # 先手負け=-1, 後手負け=+1

    T = len(sfens) - 1  # 最終局面は手を指せないので除く
    history = []  # (tensor, policy, player, ownership)

    print(f"棋譜変換中: {Path(kf_path).name}  {T}手  結果={'先手勝ち' if result==1 else '後手勝ち'}")

    for t in range(T):
        sfen_t    = sfens[t]
        sfen_next = sfens[t + 1]

        try:
            tensor    = env.to_tensor(sfen_t)
            ownership = env.ownership(sfen_t)
        except Exception as e:
            print(f"  警告: 手{t+1} テンソル変換失敗（スキップ）: {e}")
            history.append(None)
            continue

        player = env.current_player(sfen_t)

        # 指した手を特定（合法手を全適用して次SFENと一致するものを探す）
        legal = env.legal_moves(sfen_t)
        move_idx = None
        for m in legal:
            try:
                if env.apply(sfen_t, m) == sfen_next:
                    move_idx = m
                    break
            except Exception:
                continue

        if move_idx is None:
            history.append(None)
            continue

        policy = np.zeros(env.ACTION_SIZE, dtype=np.float32)
        policy[move_idx] = 1.0

        history.append((tensor, policy, player, ownership))

        if (t + 1) % 200 == 0:
            print(f"  {t + 1}/{T} 手変換済み...")

    samples = []
    for t, entry in enumerate(history):
        if entry is None:
            continue
        tensor, policy, player, ownership = entry

        # value: 対局結果を現手番視点に変換
        if player == "先手":
            v = float(result)
        else:
            v = float(-result)

        # value prefix: N手先の値（同じ棋譜内から取得）
        vp = v
        for n in range(1, VALUE_PREFIX_N + 1):
            if t + n < len(history) and history[t + n] is not None:
                future_player = history[t + n][2]
                # 将来局面が同手番なら同符号、逆手番なら反転
                vp = v if player == future_player else -v
                break

        own_signed = ownership * (1.0 if v >= 0 else -1.0)
        samples.append(Sample(tensor, policy, v, vp,
                               own_signed.astype(np.float32)))

    print(f"  → {len(samples)} サンプル生成完了")
    return samples


def load_kifu_to_buffer(kf_path: str, env,
                        buf: PrioritizedReplayBuffer,
                        repeat: int = 1):
    """
    .kf ファイルを読み込んでリプレイバッファに追加する。
    repeat: 同じ棋譜を何回追加するか（少ないデータを増幅したい場合）
    """
    samples = load_kifu(kf_path, env)
    if not samples:
        return
    for _ in range(repeat):
        buf.add_game(samples)
    print(f"バッファに {len(samples) * repeat:,} サンプル追加 (repeat={repeat})")


def load_all_kifu_in_dir(kifu_dir: str, env,
                         buf: PrioritizedReplayBuffer,
                         repeat: int = 1):
    """ディレクトリ内の全 .kf ファイルを読み込む。"""
    paths = list(Path(kifu_dir).glob('*.kf'))
    if not paths:
        print(f"警告: {kifu_dir} に .kf ファイルがありません")
        return
    for p in sorted(paths):
        load_kifu_to_buffer(str(p), env, buf, repeat=repeat)
