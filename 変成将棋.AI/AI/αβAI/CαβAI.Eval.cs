using 変成将棋.Models;
using 変成将棋.NNUE;

namespace 変成将棋.AI.αβAI;

partial class CαβAI
{
    // ── 評価・加算器ヘルパー ──────────────────────────────────────────

    private int Eval(int 加算器深度, C盤面 盤面)
    {
        if (_加算器dirty != null && _加算器dirty[加算器深度])
            Refresh加算器(加算器深度, 盤面);

        if (_nnue_i8 != null)
        {
            return _nnue_i8.加算器から評価(
                _加算器_先手_i8![加算器深度],
                _加算器_後手_i8![加算器深度],
                盤面.手番);
        }
        if (_nnue != null)
        {
            return _nnue.加算器から評価(
                _加算器_先手![加算器深度],
                _加算器_後手![加算器深度],
                盤面.手番);
        }
        return C評価関数.Evaluate(盤面, _p, _駒価値);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private void SetDirty(int 子深さ, S手 手, S取消情報 取消)
    {
        if (_加算器dirty == null) return;
        _加算器dirty[子深さ] = true;
        _lazy手![子深さ]   = 手;
        _lazy取消![子深さ] = 取消;
    }

    private void Refresh加算器(int 子深さ, C盤面 盤面)
    {
        int 親深さ = 子深さ - 1;
        Copy加算器(親深さ);
        Update加算器(盤面, 親深さ, _lazy手![子深さ], _lazy取消![子深さ]);
        _加算器dirty![子深さ] = false;
    }

    private void Init加算器Root(C盤面 盤面)
    {
        if (_局面区分_先手 == null) return;
        var 先手玉種 = 盤面.Get駒(盤面.Find玉(E手番.先手))!.種類;
        var 後手玉種 = 盤面.Get駒(盤面.Find玉(E手番.後手))!.種類;
        _局面区分_先手[0] = CNNUE評価器.局面区分番号取得(先手玉種, 後手玉種);
        _局面区分_後手![0] = CNNUE評価器.局面区分番号取得(後手玉種, 先手玉種);

        if (_nnue_i8 != null)
        {
            _nnue_i8.加算器計算(盤面, E手番.先手, _局面区分_先手[0], _加算器_先手_i8![0]);
            _nnue_i8.加算器計算(盤面, E手番.後手, _局面区分_後手[0], _加算器_後手_i8![0]);
        }
        else if (_nnue != null)
        {
            _nnue.加算器計算(盤面, E手番.先手, _局面区分_先手[0], _加算器_先手![0]);
            _nnue.加算器計算(盤面, E手番.後手, _局面区分_後手[0], _加算器_後手![0]);
        }
        if (_加算器dirty != null) _加算器dirty[0] = false;
    }

    private void Copy加算器(int 親深さ)
    {
        if (_nnue_i8 != null)
        {
            Array.Copy(_加算器_先手_i8![親深さ], _加算器_先手_i8[親深さ + 1], CNNUE評価器HalfKPInt8.L1数);
            Array.Copy(_加算器_後手_i8![親深さ], _加算器_後手_i8[親深さ + 1], CNNUE評価器HalfKPInt8.L1数);
        }
        else if (_nnue != null)
        {
            Array.Copy(_加算器_先手![親深さ], _加算器_先手[親深さ + 1], CNNUE評価器.L1数);
            Array.Copy(_加算器_後手![親深さ], _加算器_後手[親深さ + 1], CNNUE評価器.L1数);
        }
    }

    private void Update加算器(C盤面 盤面, int 親深さ, S手 手, S取消情報 取消)
    {
        if (_局面区分_先手 == null) return;
        int 子深さ = 親深さ + 1;
        var 先手玉種 = 盤面.Get駒(盤面.Find玉(E手番.先手))!.種類;
        var 後手玉種 = 盤面.Get駒(盤面.Find玉(E手番.後手))!.種類;
        int 新先手区分 = CNNUE評価器.局面区分番号取得(先手玉種, 後手玉種);
        int 新後手区分 = CNNUE評価器.局面区分番号取得(後手玉種, 先手玉種);

        if (_nnue_i8 != null)
        {
            _nnue_i8.加算器更新(盤面, E手番.先手, _局面区分_先手[親深さ], 新先手区分, _加算器_先手_i8![子深さ], 手, 取消);
            _nnue_i8.加算器更新(盤面, E手番.後手, _局面区分_後手![親深さ], 新後手区分, _加算器_後手_i8![子深さ], 手, 取消);
        }
        else if (_nnue != null)
        {
            _nnue.加算器更新(盤面, E手番.先手, _局面区分_先手[親深さ], 新先手区分, _加算器_先手![子深さ], 手, 取消);
            _nnue.加算器更新(盤面, E手番.後手, _局面区分_後手![親深さ], 新後手区分, _加算器_後手![子深さ], 手, 取消);
        }
        _局面区分_先手[子深さ] = 新先手区分;
        _局面区分_後手![子深さ] = 新後手区分;
    }
}
