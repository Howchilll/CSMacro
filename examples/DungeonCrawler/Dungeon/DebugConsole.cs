using Dungeon.Macros;
using static Dungeon.Macros.Commands;
using static Dungeon.Macros.Diagnostics;

namespace Dungeon;

/// <summary>
/// 作弊控制台（游戏里按 : 打开）。命令由各处方法上的 [@Command] 在启动时自动注册，这里不维护命令表；
/// 帮助文本在编译期生成，命令名重复会在编译期报错。
/// </summary>
public static class DebugConsole
{
    private static readonly Dictionary<string, (string Usage, Func<string[], object?> Run)> s_commands = new();

    public static void Register(string name, string usage, Func<string[], object?> run) => s_commands[name] = (usage, run);

    public static string Help => @COMMAND_HELP();

    public static void Execute(string line, Action<string> output)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        if (parts[0] == "help")
        {
            foreach (var l in Help.Split('\n')) output(l);
            return;
        }
        if (!s_commands.TryGetValue(parts[0], out var command))
        {
            output($"unknown command '{parts[0]}', type help");
            return;
        }
        try
        {
            if (command.Run(parts[1..]) is { } result) output(result.ToString()!);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
        {
            output($"usage: {command.Usage}");
        }
    }
}

/// <summary>作弊命令：一个普通静态方法 + [@Command]。参数解析、用法提示、注册、帮助全由宏生成。</summary>
public static class Cheats
{
    private static Game G => Game.Current ?? throw new InvalidOperationException("no game running");

    [@Command("heal", "Restore HP")]
    public static string Heal(int amount = 999) { G.Player.Hp += amount; return $"HP {G.Player.Hp}/{G.Player.MaxHp}"; }

    [@Command("gold", "Give gold")]
    public static void Gold(int amount) => G.Player.Gold += amount;

    [@Command("god", "Toggle invulnerability")]
    public static string God() => (G.Player.God = !G.Player.God) ? "god mode ON" : "god mode OFF";

    [@Command("spawn", "Spawn a monster next to you")]
    public static string Spawn(MonsterKind kind) => G.SpawnNear(kind);

    [@Command("reveal", "Reveal the whole level")]
    public static void Reveal() => G.RevealAll();

    [@Command("descend", "Go down one level")]
    public static void Descend() => G.Descend();

    [@Command("teleport", "Teleport to a floor tile")]
    public static string Teleport(int x, int y) => G.Teleport(new Pos(x, y));

    [@Command("status", "Show hero stats")]
    public static string Status() => @DUMP(G.Player) + $"; Depth={G.State.Depth}, Turn={G.State.Turn}, Kills={G.State.Kills}";

    [@Command("set", "Change a setting, e.g. set view_radius 12")]
    public static void Set(string key, string value) => G.Settings.Apply(new[] { KeyValuePair.Create(key, value) }, G.Log);

    [@Command("save", "Save the game")]
    public static void Save() => G.SaveGame();

    [@Command("load", "Load the saved game")]
    public static void Load() => G.LoadGame();
}
