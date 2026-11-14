using static Demo.Macros.M;

namespace Demo.App;

public class Enemy
{
    // Region macro: to trace these fields while testing, wrap them in #region @LOG_ALL; delete the two lines afterwards to restore them.
    #region @LOG_ALL
    public int Hp = 50;
    public int Attack = 8;
    public string State = "Idle";
    public readonly int Id = 1;                      // readonly: skipped
    public int Shield = @GETSET(DataCenter.PlayerData.Mp); // already expanded by @GETSET: skipped (macros can't contain macros)
    #endregion
}
