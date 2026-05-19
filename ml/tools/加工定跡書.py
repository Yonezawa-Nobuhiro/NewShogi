"""
変成将棋用 定跡書フィルタリングツール

やねうら王 standard_book.db 形式の定跡ファイルを読み込み、
変成将棋で問題になる局面・手を除外して出力する。

除外対象：
  [局面] 盤上に竪行(+L/+l)・騎兵(+N/+n)・麒麟(+S/+s) が存在する局面
         ※ 鳳凰(+G)・獅王(+K) は標準将棋に存在しないため事実上不要
  [手]   香車(L)・桂馬(N)・銀将(S) が成る手（末尾 + で動きが変成将棋と異なる）

使用例：
  python 加工定跡書.py --input 定跡書.db --output 変成将棋_定跡書.db
  python 加工定跡書.py  (デフォルト: checkpoints/定跡書.db → checkpoints/変成将棋_定跡書.db)
"""

import argparse
import re
import sys
from pathlib import Path


# 盤上に存在すると無効な成り駒（SFEN盤面部分に含まれる文字列）
INVALID_BOARD_PIECES = {'+l', '+L', '+n', '+N', '+s', '+S', '+g', '+G', '+k', '+K'}

# 成ると変成将棋と動きが異なる駒（大文字＝先手、小文字＝後手）
# ※ 角行(B→龍馬)・飛車(R→龍王)・歩兵(P→と金)は標準将棋と同じなので除外不要
INVALID_PROMOTE_PIECES = {'L', 'N', 'S', 'l', 'n', 's'}


def has_invalid_piece(sfen_board: str) -> bool:
    """盤面部分に変成将棋で問題になる成り駒が含まれているか確認する。"""
    return any(p in sfen_board for p in INVALID_BOARD_PIECES)


def get_piece_at(sfen_board: str, col: int, rank: int) -> str | None:
    """
    SFEN盤面から指定升の駒文字を返す（見つからない場合 None）。
    col: 1-9（将棋の列、右が1）
    rank: 1-9（段、上が1）
    """
    rows = sfen_board.split('/')
    if rank < 1 or rank > 9:
        return None
    row = rows[rank - 1]

    # SFEN の列は9→1の順（左端=9列、右端=1列）
    # 指定列に対応する文字位置を探す
    current_col = 9  # 現在処理中の列番号（9から始まる）
    i = 0
    while i < len(row):
        ch = row[i]
        if ch == '+':
            # 成り駒: '+' + 次の文字
            if i + 1 < len(row):
                piece = '+' + row[i + 1]
                if current_col == col:
                    return piece
                current_col -= 1
                i += 2
            else:
                break
        elif ch.isdigit():
            # 数字: 空升の連続
            empty = int(ch)
            if current_col - empty < col <= current_col:
                return None  # 空升
            current_col -= empty
            i += 1
        else:
            # 通常駒
            if current_col == col:
                return ch
            current_col -= 1
            i += 1

    return None


def is_invalid_promotion_move(sfen: str, usi_move: str) -> bool:
    """
    指定の USI 手が変成将棋で問題になる成り手かどうか判定する。
    問題になる: 香(L/l)・桂(N/n)・銀(S/s) が成る手

    usi_move 例:
      "7g7f"   通常移動（成りなし）
      "3i4h+"  成り移動
      "P*5e"   打ち手（成りなし）
    """
    if not usi_move.endswith('+'):
        return False  # 成りではない

    # 打ち手は成れない
    if len(usi_move) >= 2 and usi_move[1] == '*':
        return False

    # 移動元の升を取得: "7g7f+" → from=(7,'g')
    if len(usi_move) < 4:
        return False

    try:
        from_col = int(usi_move[0])
        from_rank = ord(usi_move[1]) - ord('a') + 1  # 'a'=1, 'i'=9
    except (ValueError, IndexError):
        return False

    board = sfen.split(' ')[0]
    piece = get_piece_at(board, from_col, from_rank)
    if piece is None:
        return False

    # 成り駒でない元の駒が L/N/S なら問題
    return piece.upper() in {'L', 'N', 'S'}


def normalize_sfen(sfen: str) -> str:
    """SFEN から手数フィールドを除いた正規化形式を返す。"""
    parts = sfen.split(' ')
    return ' '.join(parts[:3]) if len(parts) >= 3 else sfen


# 初期局面の canonical ホームスクエア (col=1-9, rank='a'-'i') -> 期待駒文字
_CANONICAL_HOME: dict[tuple[int, str], str] = {}
for _col, _pc in zip(range(9, 0, -1), 'LNSGKGSNL'):
    _CANONICAL_HOME[(_col, 'i')] = _pc
_CANONICAL_HOME[(8, 'h')] = 'B'
_CANONICAL_HOME[(2, 'h')] = 'R'
for _col in range(1, 10):
    _CANONICAL_HOME[(_col, 'g')] = 'P'
for _col, _pc in zip(range(9, 0, -1), 'lnsgkgsnl'):
    _CANONICAL_HOME[(_col, 'a')] = _pc
_CANONICAL_HOME[(8, 'b')] = 'r'
_CANONICAL_HOME[(2, 'b')] = 'b'
for _col in range(1, 10):
    _CANONICAL_HOME[(_col, 'c')] = 'p'


def count_moved_pieces(sfen_raw: str) -> int:
    """
    初期位置から離れた駒数を返す。
    ホームスクエアが空・別駒 → +1、持ち駒 → 既にホームスクエアが空なので重複なし。
    成り駒は必ず移動済みなので +1（ホームスクエアの不一致とは独立に加算）。
    """
    parts = sfen_raw.split(' ')
    board_str = parts[0]
    hands_str = parts[2] if len(parts) >= 3 else '-'

    # --- ボード解析 ---
    board: dict[tuple[int, str], str] = {}
    promoted_count = 0
    rank_ch = 'a'
    for rank_part in board_str.split('/'):
        col = 9
        i = 0
        while i < len(rank_part):
            ch = rank_part[i]
            if ch == '+':
                promoted_count += 1
                if i + 1 < len(rank_part):
                    board[(col, rank_ch)] = '+' + rank_part[i + 1]
                    col -= 1
                    i += 2
                else:
                    i += 1
            elif ch.isdigit():
                col -= int(ch)
                i += 1
            else:
                board[(col, rank_ch)] = ch
                col -= 1
                i += 1
        rank_ch = chr(ord(rank_ch) + 1)

    # --- ホームスクエアが期待どおりでないマス数 ---
    moved = promoted_count
    for (col, rank), expected in _CANONICAL_HOME.items():
        actual = board.get((col, rank))
        if actual != expected:
            moved += 1

    return moved


MAX_MOVED_PIECES = 20  # これ以上の駒が初期位置を離れている局面は除外


def process_book(input_path: Path, output_path: Path) -> tuple[int, int, int, int, int]:
    """
    定跡書をフィルタリングして出力する。
    Returns: (入力局面数, 出力局面数, 除外局面数(変成駒), 除外局面数(深度超過), 除外手数)
    """
    total_positions = 0
    output_positions = 0
    skipped_positions = 0
    skipped_deep = 0
    skipped_moves = 0

    current_sfen_raw: str | None = None
    current_sfen_board: str | None = None
    current_moves: list[str] = []
    skip_current = False

    def flush(out):
        nonlocal output_positions
        if current_sfen_raw and current_moves and not skip_current:
            out.write(f'sfen {current_sfen_raw}\n')
            out.writelines(current_moves)
            out.write('\n')
            output_positions += 1

    with (
        open(input_path, 'r', encoding='utf-8') as fin,
        open(output_path, 'w', encoding='utf-8') as fout
    ):
        for lineno, line in enumerate(fin, 1):
            line_stripped = line.rstrip('\n')

            if line_stripped.startswith('sfen '):
                flush(fout)
                total_positions += 1
                current_moves = []

                raw = line_stripped[5:].strip()
                board = raw.split(' ')[0]

                if has_invalid_piece(board):
                    skip_current = True
                    skipped_positions += 1
                    current_sfen_raw = None
                    current_sfen_board = None
                elif count_moved_pieces(raw) >= MAX_MOVED_PIECES:
                    skip_current = True
                    skipped_deep += 1
                    current_sfen_raw = None
                    current_sfen_board = None
                else:
                    skip_current = False
                    current_sfen_raw = raw
                    current_sfen_board = board

            elif not skip_current and current_sfen_raw is not None:
                stripped = line_stripped.strip()
                if not stripped or stripped.startswith('#'):
                    continue

                parts = stripped.split()
                if not parts:
                    continue

                usi_move = parts[0]

                # DB2016形式: "move none score depth count"
                # 旧形式:     "move score depth count"
                # "none" は bestmove フィールド（フィルタ不要）なので読み飛ばす
                if len(parts) >= 2 and parts[1] == 'none':
                    # DB2016: move none score depth count
                    pass  # usi_move はすでに取得済み

                if is_invalid_promotion_move(current_sfen_raw, usi_move):
                    skipped_moves += 1
                    continue

                current_moves.append(line)

            if lineno % 500_000 == 0:
                print(f'  {lineno:,} 行処理済み... (出力 {output_positions:,} 局面)')

        flush(fout)  # 最後のエントリ

    return total_positions, output_positions, skipped_positions, skipped_deep, skipped_moves


def main():
    default_base = Path(__file__).parent.parent / 'checkpoints'

    parser = argparse.ArgumentParser(description='変成将棋用定跡書フィルタリング')
    parser.add_argument('--input',  default=str(default_base / '定跡書.db'),
                        help='入力定跡ファイル（.db形式）')
    parser.add_argument('--output', default=str(default_base / '変成将棋_定跡書.db'),
                        help='出力定跡ファイル')
    args = parser.parse_args()

    input_path  = Path(args.input)
    output_path = Path(args.output)

    if not input_path.exists():
        print(f'エラー: 入力ファイルが見つかりません: {input_path}', file=sys.stderr)
        sys.exit(1)

    print(f'入力: {input_path}')
    print(f'出力: {output_path}')
    print('フィルタリング中...')

    total, kept, skip_pos, skip_deep, skip_mov = process_book(input_path, output_path)

    print(f'\n完了:')
    print(f'  入力局面数   : {total:>10,}')
    print(f'  出力局面数   : {kept:>10,}  ({kept/total*100:.2f}%)')
    print(f'  除外(変成駒) : {skip_pos:>10,}  (変成将棋固有成り駒あり)')
    print(f'  除外(深度)   : {skip_deep:>10,}  (初期位置から{MAX_MOVED_PIECES}駒以上移動)')
    print(f'  除外手数     : {skip_mov:>10,}  (L/N/S の成り手)')


if __name__ == '__main__':
    main()
