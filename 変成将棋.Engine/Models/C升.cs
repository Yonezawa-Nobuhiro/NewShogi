namespace 変成将棋.Models;

public class C升
{
    public int 列 { get; }
    public int 段 { get; }
    public C駒? 駒 { get; set; }

    public C升(int 列, int 段)
    {
        this.列 = 列;
        this.段 = 段;
    }
}
