using 変成将棋.Models;

namespace 変成将棋.AI;

public interface IプレイヤーAI : IDisposable
{
    S手? Get手(C盤面 盤面);
    void 対局開始() { }
}
