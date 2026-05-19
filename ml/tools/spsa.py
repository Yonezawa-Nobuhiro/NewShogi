"""
SPSA (Simultaneous Perturbation Stochastic Approximation) による
αβ パラメータ自動最適化

チューニング対象:
  変成将棋固有駒の価値 (竪行・騎兵・麒麟・鳳凰) ＋ 評価重み 3つ = 7パラメータ
  ※標準将棋の駒価値(歩・香・桂・銀・金・角・飛)は固定

使用法:
  python spsa.py [--iters 200] [--games 20] [--depth 3] [--parallel 4] [--resume]

事前準備:
  cd プロジェクトルート
  dotnet build 変成将棋.Tuner -c Release

参考: Spall (1998) https://www.jhuapl.edu/spsa/
"""

import argparse
import json
import math
import os
import random
import subprocess
import sys
import tempfile
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

# ── パス ─────────────────────────────────────────────────────────────────────
SCRIPT_DIR  = Path(__file__).resolve().parent
PROJECT_DIR = SCRIPT_DIR.parent.parent
TUNER_DLL   = PROJECT_DIR / "変成将棋.Tuner/bin/Release/net8.0-windows/変成将棋.Tuner.dll"
BASE_PARAMS = PROJECT_DIR / "変成将棋.AI/αβパラメータ.json"
CKPT_DIR    = PROJECT_DIR / "ml/checkpoints"
SAVE_PATH   = CKPT_DIR / "spsa_state.json"

# ── チューニング対象パラメータ ────────────────────────────────────────────────
# (名前, 初期値, 最小値, 最大値, c=摂動幅)
# 初期値は直近の SPSA 結果 + 新規評価パラメータを反映
PARAMS = [
    # 標準将棋の駒価値
    ("歩兵",                98,   50,  200, 15),
    ("香車",               399,  200,  700, 30),
    ("桂馬",               448,  200,  700, 30),
    ("銀将",               602,  350,  900, 30),
    ("金将",               701,  400, 1000, 30),
    ("角行",               798,  500, 1200, 40),
    ("飛車",              1000,  600, 1400, 40),
    ("と金",               601,  300,  900, 30),
    ("龍馬",              1051,  700, 1400, 40),
    ("龍王",              1200,  800, 1600, 40),
    # 変成将棋固有の成り駒
    ("竪行",               702,  400, 1100, 40),
    ("騎兵",               650,  400, 1100, 40),
    ("麒麟",               801,  500, 1300, 40),
    ("鳳凰",               849,  500, 1300, 40),
    # 評価関数の重み（旧パラメータ）
    ("王危険度重み",         76,   10,  300, 10),
    ("位置ボーナス重み",     45,    5,  100,  5),
    ("持ち駒ボーナス重み",   20,    5,  100,  8),
    # 評価関数の重み（新パラメータ）
    ("攻め込み重み",         15,    2,   60,  5),
    ("打ち込みポテンシャル重み", 8,  1,   40,  3),
]

PARAM_NAMES  = [p[0] for p in PARAMS]
PARAM_INIT   = [p[1] for p in PARAMS]
PARAM_MIN    = [p[2] for p in PARAMS]
PARAM_MAX    = [p[3] for p in PARAMS]
PARAM_C      = [p[4] for p in PARAMS]  # 摂動幅

N = len(PARAMS)

# ── SPSA ハイパーパラメータ (Spall 推奨値) ────────────────────────────────────
ALPHA = 0.602   # a_k のディケイ指数
GAMMA = 0.101   # c_k のディケイ指数

def a_k(k, a_coef, big_a):
    return a_coef / (k + 1 + big_a) ** ALPHA

def c_k(k, c_vec):
    """各パラメータ固有の摂動幅をスケールするベクトル"""
    scale = 1.0 / (k + 1) ** GAMMA
    return [c * scale for c in c_vec]

# ── パラメータ → JSON 変換 ────────────────────────────────────────────────────

def load_base() -> dict:
    """αβパラメータ.json を読み込んで辞書で返す"""
    with open(BASE_PARAMS, encoding='utf-8') as f:
        return json.load(f)

# 駒価値 辞書に入らないトップレベル重みの名前セット
_TOP_LEVEL_WEIGHTS = {
    "王危険度重み", "位置ボーナス重み", "持ち駒ボーナス重み",
    "攻め込み重み", "打ち込みポテンシャル重み",
}

def make_params_json(theta: list[float], base: dict, depth: int) -> dict:
    """theta を αβパラメータ の辞書に埋め込む"""
    p = json.loads(json.dumps(base))  # deep copy
    p["探索深さ"] = depth
    for i, name in enumerate(PARAM_NAMES):
        val = round(theta[i])
        if name in _TOP_LEVEL_WEIGHTS:
            p[name] = val
        else:
            p["駒価値"][name] = val
    return p

def write_tmp(params_dict: dict) -> str:
    """一時ファイルに書いてパスを返す"""
    fd, path = tempfile.mkstemp(suffix='.json')
    with os.fdopen(fd, 'w', encoding='utf-8') as f:
        json.dump(params_dict, f, ensure_ascii=False)
    return path

# ── ゲーム実行 ─────────────────────────────────────────────────────────────────

def run_games(params_a: dict, params_b: dict, num_games: int, seed: int = 0) -> dict:
    """
    C# Tuner を呼び出して numGames 局対局し、
    {"wins": W, "draws": D, "losses": L} を返す (A視点)
    """
    path_a = write_tmp(params_a)
    path_b = write_tmp(params_b)
    try:
        cmd = ["dotnet", str(TUNER_DLL), path_a, path_b, str(num_games), str(seed)]
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=600)
        if result.returncode != 0:
            print(f"[ERROR] Tuner stderr: {result.stderr[:200]}", file=sys.stderr)
            return {"wins": 0, "draws": num_games, "losses": 0}
        return json.loads(result.stdout.strip())
    finally:
        os.unlink(path_a)
        os.unlink(path_b)

def win_rate(result: dict) -> float:
    """引き分けは0.5点として勝率を計算"""
    w, d, l = result["wins"], result["draws"], result["losses"]
    total = w + d + l
    return (w + 0.5 * d) / total if total > 0 else 0.5

# ── SPSA 本体 ─────────────────────────────────────────────────────────────────

def clip(theta: list[float]) -> list[float]:
    return [min(max(theta[i], PARAM_MIN[i]), PARAM_MAX[i]) for i in range(N)]

def spsa(args):
    base = load_base()
    CKPT_DIR.mkdir(parents=True, exist_ok=True)

    # ─ 状態の初期化 or 再開 ──────────────────────────────────────────────────
    if args.resume and SAVE_PATH.exists():
        state = json.loads(SAVE_PATH.read_text(encoding='utf-8'))
        theta  = state["theta"]
        k_start = state["iter"]
        log    = state.get("log", [])
        print(f"[再開] iter={k_start} から継続")
    else:
        theta  = [float(v) for v in PARAM_INIT]
        k_start = 0
        log    = []

    # a の初期ステップサイズ設定
    # 第1イテレーションで θ が約 PARAM_RANGE * 0.01 動くように設定
    # a_0 = a / (1 + A)^alpha → a = a_0 * (1 + A)^alpha
    big_a  = max(5, int(0.05 * args.iters))
    a_init = 10.0   # 第1ステップでの平均更新量 (適宜調整)
    a_coef = a_init * (1 + big_a) ** ALPHA

    print(f"=== SPSA 開始 ===")
    print(f"  パラメータ数 : {N}")
    print(f"  反復回数     : {args.iters}")
    print(f"  1反復あたり  : {args.games} 局")
    print(f"  総ゲーム数   : {args.iters * args.games} 局")
    print(f"  探索深さ     : {args.depth}")
    print(f"  並列数       : {args.parallel}")
    print(f"  big_A        : {big_a},  a_coef : {a_coef:.2f}")
    print()
    print(f"{'iter':>5}  {'win%':>6}  " + "  ".join(f"{n[:4]:>6}" for n in PARAM_NAMES))
    print("-" * (5 + 8 + 8 * N))

    t0 = time.time()

    for k in range(k_start, args.iters):
        ak = a_k(k, a_coef, big_a)
        ck = c_k(k, PARAM_C)  # c_k は各パラメータ固有

        # ±1 Bernoulli 摂動ベクトル
        delta = [random.choice([-1, 1]) for _ in range(N)]

        # θ± を作成
        theta_plus  = clip([theta[i] + ck[i] * delta[i] for i in range(N)])
        theta_minus = clip([theta[i] - ck[i] * delta[i] for i in range(N)])

        p_plus  = make_params_json(theta_plus,  base, args.depth)
        p_minus = make_params_json(theta_minus, base, args.depth)

        # ── 並列ゲーム実行 ────────────────────────────────────────────────────
        # games を parallel 個のバッチに均等分配し全コアを使う
        # 偶数バッチ: p_plus を A として実行
        # 奇数バッチ: p_minus を A として実行 → 集計時に反転
        seed_base = k * 1000
        n_workers = min(args.parallel, args.games)
        base_size = args.games // n_workers
        extra     = args.games  % n_workers

        # (pa, pb, n_games, seed, plus_is_a)
        tasks = []
        for w in range(n_workers):
            n = base_size + (1 if w < extra else 0)
            plus_is_a = (w % 2 == 0)
            pa, pb = (p_plus, p_minus) if plus_is_a else (p_minus, p_plus)
            tasks.append((pa, pb, n, seed_base + w, plus_is_a))

        def _run(t):
            pa, pb, n, s, _ = t
            return run_games(pa, pb, n, s)

        with ThreadPoolExecutor(max_workers=n_workers) as ex:
            results = list(ex.map(_run, tasks))

        # θ+ 視点に統合
        wins_t = draws_t = losses_t = 0
        for (_, _, _, _, plus_is_a), r in zip(tasks, results):
            if plus_is_a:
                wins_t += r["wins"];   draws_t += r["draws"]; losses_t += r["losses"]
            else:
                wins_t += r["losses"]; draws_t += r["draws"]; losses_t += r["wins"]

        combined = {"wins": wins_t, "draws": draws_t, "losses": losses_t}
        wr = win_rate(combined)

        # 勾配推定 (SPSA)
        g_hat = [(2 * wr - 1) / (2 * ck[i] * delta[i]) for i in range(N)]

        # パラメータ更新 (勾配上昇)
        theta = clip([theta[i] + ak * g_hat[i] for i in range(N)])

        elapsed = time.time() - t0
        entry = {
            "iter":  k + 1,
            "theta": [round(v) for v in theta],
            "win_rate": round(wr, 3),
            "elapsed": round(elapsed, 1),
        }
        log.append(entry)

        vals = "  ".join(f"{round(theta[i]):>6}" for i in range(N))
        print(f"{k+1:>5}  {wr*100:>5.1f}%  {vals}  ({elapsed:.0f}s)")

        # 定期保存
        if (k + 1) % 10 == 0 or k + 1 == args.iters:
            state = {"iter": k + 1, "theta": theta, "log": log}
            SAVE_PATH.write_text(json.dumps(state, ensure_ascii=False, indent=2),
                                 encoding='utf-8')
            # 最新の αβパラメータ.json を更新
            best = make_params_json(theta, base, base.get("探索深さ", 4))
            out = PROJECT_DIR / "変成将棋.AI/αβパラメータ.json"
            out.write_text(json.dumps(best, ensure_ascii=False, indent=2),
                           encoding='utf-8')
            print(f"  → 保存: {SAVE_PATH.name}, αβパラメータ.json 更新")

    print("\n=== 完了 ===")
    print("最終パラメータ:")
    for i, name in enumerate(PARAM_NAMES):
        print(f"  {name:12}: {round(theta[i])}")

# ── エントリーポイント ─────────────────────────────────────────────────────────

if __name__ == "__main__":
    if not TUNER_DLL.exists():
        print("[ERROR] Tuner DLL が見つかりません。先にビルドしてください:")
        print(f"  dotnet build 変成将棋.Tuner -c Release")
        sys.exit(1)

    parser = argparse.ArgumentParser(description="SPSA パラメータ最適化")
    parser.add_argument("--iters",    type=int, default=200,  help="反復回数")
    parser.add_argument("--games",    type=int, default=20,   help="1反復あたりのゲーム数")
    parser.add_argument("--depth",    type=int, default=3,    help="探索深さ (3=高速)")
    parser.add_argument("--parallel", type=int, default=1,    help="並列スレッド数")
    parser.add_argument("--resume",   action="store_true",    help="前回の続きから再開")
    args = parser.parse_args()

    spsa(args)
