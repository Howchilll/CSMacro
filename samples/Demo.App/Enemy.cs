using static Demo.Macros.M;

namespace Demo.App;

public class Enemy
{
    // 片段宏：测试时想跟踪这些字段的变化，用 #region @LOG_ALL 包起来就行，测完删掉这两行即恢复原样。
    #region @LOG_ALL
    public int Hp = 50;
    public int Attack = 8;
    public string State = "Idle";
    public readonly int Id = 1;                      // readonly：跳过
    public int Shield = @GETSET(DataCenter.PlayerData.Mp); // 已经被 @GETSET 展开：跳过（宏不能包含宏）
    #endregion
}
