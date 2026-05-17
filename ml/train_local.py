"""
ローカル CPU 学習用スクリプト
  32 sims / batch=2 / 20局 / 200ステップ
  約 8分/iter × 20iter = 2.5 時間で完走

棋譜取り込み:
  ml/kifu/ フォルダに .kf ファイルを置くと学習開始前に自動でバッファに追加される。
"""

from train import run

hp = dict(
    num_sims        = 32,
    mcts_batch_size = 5,
    games_per_iter  = 10,
    train_steps     = 200,
    num_iters       = 20,
    min_buffer      = 300,
    save_every      = 1,
    checkpoint_dir  = "checkpoints",
)

if __name__ == "__main__":
    import pathlib, glob
    from native_game_env import NativeGameEnv
    from self_play import PrioritizedReplayBuffer
    from kifu_loader import load_all_kifu_in_dir

    ckpt_dir = pathlib.Path(hp["checkpoint_dir"])

    # model_final.pt → model_iterNNNN.pt の順に最新チェックポイントを探す
    pretrained = None
    final = ckpt_dir / "model_final.pt"
    if final.exists():
        pretrained = str(final)
    else:
        iters = sorted(glob.glob(str(ckpt_dir / "model_iter*.pt")))
        iters = [p for p in iters if "_v1_" not in p]
        if iters:
            pretrained = iters[-1]

    print(f"継続学習: {pathlib.Path(pretrained).name}" if pretrained else "新規学習")

    # kifu/ フォルダの棋譜を事前バッファに追加
    kifu_dir = pathlib.Path(__file__).parent / "kifu"
    kifu_dir.mkdir(exist_ok=True)
    kifu_files = list(kifu_dir.glob("*.kf"))
    if kifu_files:
        print(f"\n棋譜ファイル {len(kifu_files)} 件をバッファに取り込みます...")
        env = NativeGameEnv()
        kifu_buf = PrioritizedReplayBuffer(
            max_size=hp["buffer_size"],
            alpha=hp.get("per_alpha", 0.6),
            beta=hp.get("per_beta", 0.4))
        load_all_kifu_in_dir(str(kifu_dir), env, kifu_buf, repeat=3)
        print(f"棋譜バッファ: {len(kifu_buf):,} サンプル\n")
        run(hp, pretrained_path=pretrained, initial_buffer=kifu_buf)
    else:
        print("kifu/ フォルダに .kf ファイルが見つかりません。通常学習を開始します。")
        run(hp, pretrained_path=pretrained)
