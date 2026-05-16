"""
NativeAOT DLL のビルドスクリプト。
python build_native.py で実行（ローカル Windows 用）。

Linux (Colab) の場合は colab_train.ipynb のビルドセルを参照。
"""
import subprocess, sys, os
from pathlib import Path

PROJECT = Path(__file__).parent.parent / "変成将棋.Engine.Native"
DOTNET  = "dotnet"


def build(rid: str):
    cmd = [
        DOTNET, "publish", str(PROJECT),
        "-r", rid,
        "-c", "Release",
        "--self-contained",
    ]
    print(f"ビルド中: {' '.join(cmd)}")
    result = subprocess.run(cmd, capture_output=True, text=True)
    print(result.stdout[-1000:])
    if result.returncode != 0:
        print("ERROR:", result.stderr[-500:])
        sys.exit(1)

    out_dir = PROJECT / "bin" / "Release" / "net8.0" / rid / "publish"
    libs = list(out_dir.glob("shogi_engine.*"))
    print(f"\nビルド完了: {out_dir}")
    for lib in libs:
        print(f"  {lib.name}  ({lib.stat().st_size // 1024} KB)")


if __name__ == "__main__":
    # デフォルトは現在の OS に合わせる
    import platform
    rid = "linux-x64" if platform.system() == "Linux" else "win-x64"
    if len(sys.argv) > 1:
        rid = sys.argv[1]
    build(rid)
