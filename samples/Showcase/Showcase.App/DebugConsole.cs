using static Showcase.Macros.Commands;

namespace Showcase.App;

/// <summary>
/// 调试控制台。命令由各处方法上的 [@Command] 在启动时自动注册，这里不维护任何命令表；
/// 帮助文本在编译期生成（命令名重复会在编译期报错）。
/// </summary>
public static class DebugConsole
{
    private static readonly Dictionary<string, (string Usage, Func<string[], object?> Run)> s_commands = new();

    public static void Register(string name, string usage, string help, Func<string[], object?> run) =>
        s_commands[name] = (usage, run);

    public static string Help => @COMMAND_HELP();

    public static void Execute(string line)
    {
        Console.WriteLine($"> {line}");
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        if (parts[0] == "help")
        {
            Console.WriteLine(Help);
            return;
        }
        if (!s_commands.TryGetValue(parts[0], out var command))
        {
            Console.WriteLine($"  unknown command '{parts[0]}', type help");
            return;
        }

        try
        {
            var result = command.Run(parts[1..]);
            if (result != null) Console.WriteLine($"  {result}");
        }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException)
        {
            Console.WriteLine($"  usage: {command.Usage}");
        }
    }
}
