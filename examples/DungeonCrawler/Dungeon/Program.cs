using Dungeon;

// 用法：
//   dotnet run                         交互模式（需要真实终端）
//   dotnet run -- --demo [--seed N]    机器人自动玩一局（固定种子，结果可复现）
//   dotnet run -- --write-settings     生成带说明的默认 dungeon.ini
//   dotnet run -- --seed N --settings path/to/dungeon.ini

var demo = args.Contains("--demo");
var seed = ArgValue("--seed") is { } s ? int.Parse(s) : demo ? 7 : Environment.TickCount & 0xFFFF;
var settingsPath = ArgValue("--settings") ?? Path.Combine(AppContext.BaseDirectory, "dungeon.ini");
var savePath = Path.Combine(AppContext.BaseDirectory, "dungeon.sav");

var settings = GameSettings.Load(settingsPath, problem => Console.WriteLine($"[settings] {Path.GetFileName(settingsPath)}: {problem}"));

if (args.Contains("--write-settings"))
{
    File.WriteAllText(settingsPath, settings.Template());
    Console.WriteLine($"wrote {settingsPath}:\n");
    Console.Write(settings.Template());
    return;
}

if (!demo && Console.IsInputRedirected)
{
    Console.WriteLine("No interactive terminal; running the demo instead (use --demo to do this on purpose).");
    demo = true;
}

var renderer = new Renderer();
var game = new Game(settings, seed, savePath);
game.Message += renderer.AddMessage;
if (demo)
{
    Console.WriteLine($"Dungeon demo, seed {seed}. A bot plays; console commands are injected along the way.");
    game.Message += m => Console.WriteLine($"  [t{game.State.Turn,3}] {m}");
    TraceLog.Sink = m => Console.WriteLine($"  [t{game.State.Turn,3}] {m}");
    game.Start();
    DemoRunner.Run(game, renderer, maxTurns: 600);
}
else
{
    TraceLog.Sink = game.Log;
    game.Start();
    InteractiveRunner.Run(game, renderer);
}

string? ArgValue(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
