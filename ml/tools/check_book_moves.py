moves = [
    ("2g2f",-44), ("6i7h",-44), ("1g1f",-45), ("9g9f",-66),
    ("3i4h",-73), ("7g7f",-75), ("3i3h",-75), ("4i5h",-84),
    ("7i6h",-85), ("5i6h",-96), ("3g3f",-105), ("4i4h",-110),
    ("4i3h",-148),
]

def flip(u):
    fc = chr(ord('0') + 10 - int(u[0]))
    fr = chr(ord('i') - (ord(u[1]) - ord('a')))
    tc = chr(ord('0') + 10 - int(u[2]))
    tr = chr(ord('i') - (ord(u[3]) - ord('a')))
    return fc + fr + tc + tr

# 後手の初期駒 (ランクa=1段目, 後手の後段)
# col: 9=l(香), 8=n(桂), 7=s(銀), 6=g(金), 5=k(玉), 4=g(金), 3=s(銀), 2=n(桂), 1=l(香)
# rankb: col8=r(飛), col2=b(角)
# rankc: 全列=p(歩)
piece_names = {
    ('a','9'):'9一香', ('a','8'):'8一桂', ('a','7'):'7一銀', ('a','6'):'6一金',
    ('a','5'):'5一玉', ('a','4'):'4一金', ('a','3'):'3一銀', ('a','2'):'2一桂', ('a','1'):'1一香',
    ('b','8'):'8二飛', ('b','2'):'8二角',
    ('c','9'):'9三歩', ('c','8'):'8三歩', ('c','7'):'7三歩', ('c','6'):'6三歩', ('c','5'):'5三歩',
    ('c','4'):'4三歩', ('c','3'):'3三歩', ('c','2'):'2三歩', ('c','1'):'1三歩',
}

cutoff = -94  # maxScore(-44) - threshold(50)

print("canonical  actual  from-piece        score  weight  OK?")
print("-" * 60)
for m, s in moves:
    a = flip(m)
    fr = a[1]; fc = a[0]
    piece = piece_names.get((fr, fc), f"{fc}{fr}?")
    w = max(0, s - cutoff)
    ok = "YES" if w > 0 else "no "
    print(f"  {m}  ->  {a}  [{piece:8}]  {s:5}  {w:4}  {ok}")
