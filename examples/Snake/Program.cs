using Snake;

// dotnet run                        interactive mode (needs a real terminal)
// dotnet run -- --demo [--seed N]   a bot plays one game (fixed seed, reproducible)
// dotnet run -- --write-settings    writes a commented default snake.ini

var settingsPath = Path.Combine(AppContext.BaseDirectory, "snake.ini");
var recordPath = Path.Combine(AppContext.BaseDirectory, "snake.sav");
var report = (string m) => Console.WriteLine($"[snake] {m}");

var settings = GameSettings.Load(settingsPath, report);
if (args.Contains("--write-settings"))
{
    File.WriteAllText(settingsPath, settings.Template());
    Console.Write(settings.Template());
    return;
}

var demo = args.Contains("--demo") || Console.IsInputRedirected;
var seed = Array.IndexOf(args, "--seed") is var i and >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1]) : demo ? 1 : Environment.TickCount;
var progress = Record.LoadFrom(recordPath, report);
var game = new Game(settings, progress, seed);
game.Message += m => Console.WriteLine(m);

if (demo) Demo.Run(game);
else Interactive.Run(game);

progress.SaveTo(recordPath);
Console.WriteLine($"Games played {progress.GamesPlayed}, food eaten {progress.TotalFood}, best {progress.Best}.");

static class Interactive
{
    public static void Run(Game game)
    {
        Console.Clear();
        Console.CursorVisible = false;
        try
        {
            while (!game.Over && !game.Quit)
            {
                while (Console.KeyAvailable) game.HandleKey(Console.ReadKey(intercept: true).Key);
                game.Step();
                Console.SetCursorPosition(0, 0);
                Console.Write(game.Render());
                Console.WriteLine(Game.KeyHelp);
                Thread.Sleep(game.Tick);
            }
        }
        finally { Console.CursorVisible = true; }
    }
}

/// <summary>Demo: a greedy bot heads for the food and avoids deadly cells. Its keys also go through the generated dispatch code.</summary>
static class Demo
{
    public static void Run(Game game)
    {
        Console.WriteLine($"Snake demo. Keys: {Game.KeyHelp}");
        for (var step = 0; step < 2000 && !game.Over; step++)
        {
            game.HandleKey(Choose(game));
            game.Step();
            if (step % 150 == 0 || game.Over) Console.Write(game.Render());
        }
    }

    private static ConsoleKey Choose(Game game)
    {
        var head = game.Body[0];
        var options = new[] { (Dir.Up, ConsoleKey.UpArrow), (Dir.Down, ConsoleKey.DownArrow), (Dir.Left, ConsoleKey.LeftArrow), (Dir.Right, ConsoleKey.RightArrow) }
            .Select(o => (o.Item2, Next: head + Game.Delta(o.Item1)))
            .Where(o => o.Next != game.Body[1])                       // no reversing
            .OrderBy(o => Safe(game, o.Next) ? 0 : 1)                 // stay alive first
            .ThenBy(o => Math.Abs(o.Next.X - game.Food.X) + Math.Abs(o.Next.Y - game.Food.Y)); // then get closer to the food
        return options.First().Item1;
    }

    private static bool Safe(Game game, Pos p) =>
        (game.WrapWalls || (p.X >= 0 && p.Y >= 0 && p.X < game.Width && p.Y < game.Height))
        && !game.Body.Take(game.Body.Count - 1).Contains(p);
}
