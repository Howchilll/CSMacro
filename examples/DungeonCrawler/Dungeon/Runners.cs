namespace Dungeon;

/// <summary>交互模式：读键盘。</summary>
public static class InteractiveRunner
{
    public static void Run(Game game, Renderer renderer)
    {
        Console.CursorVisible = false;
        try
        {
            while (!game.Over)
            {
                renderer.Draw(game);
                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.W or ConsoleKey.UpArrow: game.Move(new Pos(0, -1)); break;
                    case ConsoleKey.S when key.Modifiers.HasFlag(ConsoleModifiers.Shift): game.SaveGame(); break;
                    case ConsoleKey.S or ConsoleKey.DownArrow: game.Move(new Pos(0, 1)); break;
                    case ConsoleKey.A or ConsoleKey.LeftArrow: game.Move(new Pos(-1, 0)); break;
                    case ConsoleKey.D or ConsoleKey.RightArrow: game.Move(new Pos(1, 0)); break;
                    case ConsoleKey.L when key.Modifiers.HasFlag(ConsoleModifiers.Shift): game.LoadGame(); break;
                    case ConsoleKey.Q: game.DrinkPotion(); break;
                    case ConsoleKey.OemPeriod or ConsoleKey.Spacebar: game.Wait(); break;
                    case ConsoleKey.Escape: return;
                    default:
                        if (key.KeyChar == ':')
                        {
                            Console.CursorVisible = true;
                            Console.Write(": ");
                            var line = Console.ReadLine() ?? "";
                            Console.CursorVisible = false;
                            game.Log(": " + line);
                            DebugConsole.Execute(line, game.Log);
                        }
                        break;
                }
            }
            renderer.Draw(game);
            Console.WriteLine(game.Won ? "Congratulations!" : "Game over.");
        }
        finally
        {
            Console.CursorVisible = true;
        }
    }
}

/// <summary>
/// demo 模式：一个寻路机器人自动玩，中途按回合插入控制台命令（帮助、状态、配置、存档读档……）。
/// 随机种子固定，所以每次运行结果相同，适合在不能交互的环境里验证整个游戏。
/// </summary>
public static class DemoRunner
{
    private static readonly Dictionary<int, string[]> Script = new()
    {
        [1] = new[] { "help" },
        [4] = new[] { "status", "set view_radius 99", "set monsters_per_level lots", "set speed 3", "set view_radius 8" },
        [40] = new[] { "save", "gold 500", "status", "load", "status" },
        [60] = new[] { "spawn troll", "god" },
        [63] = new[] { "god" },
    };

    public static void Run(Game game, Renderer renderer, int maxTurns)
    {
        var done = new HashSet<int>();
        var lastDepth = 0;
        while (!game.Over && game.State.Turn < maxTurns)
        {
            if (game.State.Depth != lastDepth)
            {
                lastDepth = game.State.Depth;
                Console.WriteLine();
                Console.Write(renderer.Frame(game, messages: 0));
            }
            if (Script.TryGetValue(game.State.Turn, out var commands) && done.Add(game.State.Turn))
            {
                foreach (var command in commands)
                {
                    Console.WriteLine($"  > :{command}");
                    DebugConsole.Execute(command, line => Console.WriteLine("    " + line.Replace("\n", "\n    ")));
                }
                continue;
            }
            Bot.Act(game);
        }
        Console.WriteLine();
        Console.Write(renderer.Frame(game, messages: 0));
        Console.WriteLine(game.Over
            ? (game.Won ? "Demo finished: the bot won." : "Demo finished: the bot died.")
            : $"Demo stopped after {maxTurns} turns.");
    }
}

/// <summary>demo 用的简单机器人：血少就喝药；否则打最近的可见怪物、捡可见的道具、最后去楼梯。用 BFS 寻路。</summary>
public static class Bot
{
    public static void Act(Game game)
    {
        var p = game.Player;
        if (p.Hp * 3 <= p.MaxHp && p.Potions > 0) { game.DrinkPotion(); return; }

        var adjacent = game.Monsters.FirstOrDefault(m => m.Pos.DistanceTo(p.Pos) <= 1);
        if (adjacent != null) { game.Move(new Pos(adjacent.Pos.X - p.Pos.X, adjacent.Pos.Y - p.Pos.Y)); return; }

        var targets = game.Monsters.Where(m => game.Visible(m.Pos)).Select(m => m.Pos)
            .Concat(game.Items.Where(i => game.Visible(i.Pos) && !(i.Kind == ItemKind.Potion && p.Potions >= 9)).Select(i => i.Pos))
            .ToList();
        var step = FirstStep(game, targets) ?? FirstStep(game, new List<Pos> { game.Map.Stairs });
        if (step is { } s) game.Move(s);
        else game.Wait();
    }

    /// <summary>BFS 到最近的目标，返回第一步的方向。</summary>
    private static Pos? FirstStep(Game game, List<Pos> targets)
    {
        if (targets.Count == 0) return null;
        var goal = new HashSet<Pos>(targets);
        var start = game.Player.Pos;
        var cameFrom = new Dictionary<Pos, Pos> { [start] = start };
        var queue = new Queue<Pos>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (goal.Contains(cur) && cur != start)
            {
                while (cameFrom[cur] != start) cur = cameFrom[cur];
                return new Pos(cur.X - start.X, cur.Y - start.Y);
            }
            foreach (var d in Pos.Directions)
            {
                var next = cur + d;
                if (cameFrom.ContainsKey(next) || !game.Map.Walkable(next)) continue;
                if (game.MonsterAt(next) != null && !goal.Contains(next)) continue;
                cameFrom[next] = cur;
                queue.Enqueue(next);
            }
        }
        return null;
    }
}
