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
    // Method macro: the IDE sees a field, the compiler gets a forwarding property.
    // Attribute macro [@Track] and method macro @GETSET on the same line; each takes effect.
    [@Track] public int Hp = @GETSET(DataCenter.PlayerData.Hp);
    public int Mp = @GETSET(DataCenter.PlayerData.Mp);

    // One shared rule: these properties log every change.
    // [@Save] registers the field with the save container; [@Save("lv")] sets the key used in the save.
    [@Track, @Save] public int Gold = @LOGGED(100);
    [@Save("lv")] public int Level = @LOGGED(1);
    public string Title = @LOGGED("Novice");
}
