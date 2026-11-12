using Demo.Macros;
using static Demo.Macros.M;

namespace Demo.App;

public static class DataCenter
{
    public static PlayerData PlayerData = new();
}

public class PlayerData
{
    public int Hp = 100;
    public int Mp = 50;
}

public class Player
{
    // 方法宏：IDE 眼里这是个字段，编译器拿到的是转发属性。
    // 特性宏 [@Track] 和方法宏 @GETSET 写在同一行，各自生效。
    [@Track] public int Hp = @GETSET(DataCenter.PlayerData.Hp);
    public int Mp = @GETSET(DataCenter.PlayerData.Mp);

    // 统一规则：这些属性改变时自动打 log。
    // [@Save] 让字段自动进入存档容器，[@Save("lv")] 指定存档里的键名。
    [@Track, @Save] public int Gold = @LOGGED(100);
    [@Save("lv")] public int Level = @LOGGED(1);
    public string Title = @LOGGED("Novice");
}
