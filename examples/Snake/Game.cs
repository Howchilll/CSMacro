using System.Text;
using Snake.Macros;
using static Snake.Macros.Diagnostics;
using static Snake.Macros.Keys;
using static Snake.Macros.Properties;

namespace Snake;

public readonly record struct Pos(int X, int Y)
{
    public static Pos operator +(Pos a, Pos b) => new(a.X + b.X, a.Y + b.Y);
}

public enum Dir { Up, Down, Left, Right }

public sealed class Game
{
    private readonly GameSettings _settings;
    private readonly Random _rng;
    private readonly List<Pos> _body = new(); // _body[0] is the head
    private Dir _next = Dir.Right;
    private bool _newBestAnnounced;

    public Game(GameSettings settings, Record record, int seed)
    {
        _settings = settings;
        _rng = new Random(seed);
        Record = record;
        Tick = settings.TickMs;
        var center = new Pos(settings.Width / 2, settings.Height / 2);
        for (var i = 0; i < 3; i++) _body.Add(center + new Pos(-i, 0));
        PlaceFood();
    }

    public Record Record { get; }
    public int Width => _settings.Width;
    public int Height => _settings.Height;
    public bool WrapWalls => _settings.WrapWalls;
    public IReadOnlyList<Pos> Body => _body;
    public bool Over { get; private set; }
    public bool Paused { get; private set; }
    public bool Quit { get; private set; }
    public event Action<string>? Message;

    // ---- Macros: notify when the score changes; keep the speed in range ------
    public int Score = @OBSERVABLE(0);
    public int Tick = @CLAMP(30, 500, 120); // milliseconds per step; smaller is faster

    private void OnPropertyChanged(string propertyName)
    {
        if (propertyName != nameof(Score)) return;
        if (Score > Record.Best && Record.Best > 0 && !_newBestAnnounced)
        {
            _newBestAnnounced = true;
            Message?.Invoke($"New best! (old best {Record.Best})");
        }
        if (Score % 5 == 0) Tick -= 10; // speed up every 5 points; @CLAMP keeps it at 30 or more
    }

    // ---- Macros: build with -p:TraceState=true to log every change of these two fields ----
    #region @TRACE_STATE
    public Dir Heading = Dir.Right;
    public Pos Food;
    #endregion

    // ---- Macros: key bindings. The key dispatch switch and the key help are generated ----
    [@Key("up", ConsoleKey.W, ConsoleKey.UpArrow)] private void Up() => Turn(Dir.Up);
    [@Key("down", ConsoleKey.S, ConsoleKey.DownArrow)] private void Down() => Turn(Dir.Down);
    [@Key("left", ConsoleKey.A, ConsoleKey.LeftArrow)] private void Left() => Turn(Dir.Left);
    [@Key("right", ConsoleKey.D, ConsoleKey.RightArrow)] private void Right() => Turn(Dir.Right);
    [@Key("pause", ConsoleKey.P, ConsoleKey.Spacebar)] private void TogglePause() => Paused = !Paused;
    [@Key("quit", ConsoleKey.Escape, ConsoleKey.Q)] private void QuitGame() => Quit = true;

    public void HandleKey(ConsoleKey key) { @DISPATCH_KEYS(key); }
    public static string KeyHelp => @KEY_HELP();

    // ---- Game logic -----------------------------------------------------------

    private void Turn(Dir d)
    {
        var reverse = (Heading, d) is (Dir.Up, Dir.Down) or (Dir.Down, Dir.Up) or (Dir.Left, Dir.Right) or (Dir.Right, Dir.Left);
        if (!reverse) _next = d;
    }

    public static Pos Delta(Dir d) => d switch
    {
        Dir.Up => new(0, -1), Dir.Down => new(0, 1), Dir.Left => new(-1, 0), _ => new(1, 0),
    };

    public void Step()
    {
        if (Over || Paused) return;
        Heading = _next;
        var head = _body[0] + Delta(Heading);

        if (_settings.WrapWalls) head = new Pos((head.X + _settings.Width) % _settings.Width, (head.Y + _settings.Height) % _settings.Height);
        else if (head.X < 0 || head.Y < 0 || head.X >= _settings.Width || head.Y >= _settings.Height) { Die("hit the wall"); return; }

        var eats = head == Food;
        if (_body.Take(eats ? _body.Count : _body.Count - 1).Contains(head)) { Die("bit itself"); return; }

        _body.Insert(0, head);
        if (eats)
        {
            Score++;
            Record.TotalFood++;
            PlaceFood();
        }
        else _body.RemoveAt(_body.Count - 1);

        // Invariants: checked in Debug builds; these two lines disappear entirely in Release builds
        @ASSERT(_body.Distinct().Count() == _body.Count, "the snake overlaps itself");
        @ASSERT(!_body.Contains(Food), "food is under the snake");
    }

    private void PlaceFood()
    {
        var free = Enumerable.Range(0, _settings.Width * _settings.Height)
            .Select(i => new Pos(i % _settings.Width, i / _settings.Width))
            .Where(p => !_body.Contains(p))
            .ToList();
        if (free.Count == 0) { Die("filled the whole board. You win"); return; }
        Food = free[_rng.Next(free.Count)];
    }

    private void Die(string reason)
    {
        Over = true;
        Record.GamesPlayed++;
        var best = Score > Record.Best;
        if (best) Record.Best = Score;
        Message?.Invoke($"Game over: the snake {reason}. Score {Score} ({(best ? "new best!" : $"best {Record.Best}")})");
    }

    public string Render()
    {
        var sb = new StringBuilder();
        var border = "+" + new string('-', _settings.Width) + "+";
        sb.AppendLine(border);
        for (var y = 0; y < _settings.Height; y++)
        {
            sb.Append('|');
            for (var x = 0; x < _settings.Width; x++)
            {
                var p = new Pos(x, y);
                sb.Append(p == _body[0] ? '@' : _body.Contains(p) ? 'o' : p == Food ? '*' : ' ');
            }
            sb.AppendLine("|");
        }
        sb.AppendLine(border);
        sb.AppendLine($"Score {Score}   Best {Math.Max(Record.Best, Score)}   Speed {1000 / Tick}/s{(Paused ? "   [PAUSED]" : "")}   ");
        return sb.ToString();
    }
}
