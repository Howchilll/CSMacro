using Dungeon.Macros;
using static Dungeon.Macros.Properties;
using static Dungeon.Macros.SettingsMacros;

namespace Dungeon;

/// <summary>
/// dungeon.ini 里的配置。每一项一行：配置键、说明、类型、默认值、范围。
/// 解析、类型转换、越界提示、未知键提示、默认配置文件模板全部由宏生成，没有反射。
/// </summary>
public sealed class GameSettings
{
    [@Setting("player_name", "Your hero's name")] public string PlayerName = "Hero";
    [@Setting("view_radius", "How many tiles you can see")] public int ViewRadius = @CLAMP(3, 20, 8);
    [@Setting("max_rooms", "Rooms per level")] public int MaxRooms = @CLAMP(2, 12, 8);
    [@Setting("monsters_per_level", "Monsters on depth 1; one more per depth")] public int MonstersPerLevel = @CLAMP(1, 30, 4);
    [@Setting("potions_per_level", "Potions lying around per level")] public int PotionsPerLevel = @CLAMP(0, 10, 2);
    [@Setting("win_depth", "Reach this depth to win")] public int WinDepth = @CLAMP(2, 20, 5);
    [@Setting("color", "Use colors in the terminal")] public bool Color = true;

    public void Apply(IEnumerable<KeyValuePair<string, string>> values, Action<string> report) { @APPLY_SETTINGS(values, report); }

    public string Template() => @SETTINGS_TEMPLATE(this);

    public static GameSettings Load(string path, Action<string> report)
    {
        var settings = new GameSettings();
        if (File.Exists(path)) settings.Apply(ParseIni(File.ReadAllLines(path), report), report);
        return settings;
    }

    private static IEnumerable<KeyValuePair<string, string>> ParseIni(IEnumerable<string> lines, Action<string> report)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq < 0) { report($"ignoring line without '=': {line}"); continue; }
            yield return KeyValuePair.Create(line[..eq], line[(eq + 1)..]);
        }
    }
}
