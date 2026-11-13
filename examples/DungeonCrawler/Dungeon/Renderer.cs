using System.Text;

namespace Dungeon;

/// <summary>画地图、状态栏和最近的消息。状态栏文本只在玩家属性变化（HudDirty）时重建。</summary>
public sealed class Renderer
{
    private readonly List<string> _messages = new();
    private string _hud = "";

    public void AddMessage(string text)
    {
        _messages.Add(text);
        if (_messages.Count > 50) _messages.RemoveAt(0);
    }

    private string Hud(Game g)
    {
        if (!g.HudDirty) return _hud;
        var p = g.Player;
        _hud = $"{p.Name}  HP {p.Hp}/{p.MaxHp}  Atk {p.Attack}  Lv {p.Level} ({p.Xp}/{p.XpToNext} xp)  " +
               $"Gold {p.Gold}  Potions {p.Potions}  Depth {g.State.Depth}/{g.Settings.WinDepth}" + (p.God ? "  [GOD]" : "");
        g.HudDirty = false;
        return _hud;
    }

    private static char Glyph(Game g, Pos p, out ConsoleColor color)
    {
        color = ConsoleColor.Gray;
        if (p == g.Player.Pos) { color = ConsoleColor.Yellow; return '@'; }
        var visible = g.Visible(p);
        if (visible && g.MonsterAt(p) is { } m) { color = ConsoleColor.Red; return m.Info.Glyph; }
        if (visible && g.Items.FirstOrDefault(i => i.Pos == p) is { } item) { color = ConsoleColor.Cyan; return item.Glyph; }
        if (!g.Map.Seen(p)) return ' ';
        if (!visible) color = ConsoleColor.DarkGray;
        return g.Map[p] switch
        {
            Tile.Wall => HasFloorNeighbor(g.Map, p) ? '#' : ' ',
            Tile.Stairs => '>',
            _ => '.',
        };
    }

    private static bool HasFloorNeighbor(Map map, Pos p) => Pos.Directions.Any(d => map.Walkable(p + d));

    /// <summary>整帧文本（不带颜色），demo 模式直接打印它。</summary>
    public string Frame(Game g, int messages = 5)
    {
        var sb = new StringBuilder();
        for (var y = 0; y < Map.Height; y++)
        {
            for (var x = 0; x < Map.Width; x++) sb.Append(Glyph(g, new Pos(x, y), out _));
            sb.Append('\n');
        }
        sb.Append(Hud(g)).Append('\n');
        foreach (var m in _messages.TakeLast(messages)) sb.Append("  ").Append(m).Append('\n');
        return sb.ToString();
    }

    /// <summary>交互模式：清屏后带颜色地画一帧。</summary>
    public void Draw(Game g)
    {
        Console.Clear();
        var color = Console.ForegroundColor;
        for (var y = 0; y < Map.Height; y++)
        {
            for (var x = 0; x < Map.Width; x++)
            {
                var c = Glyph(g, new Pos(x, y), out var fg);
                if (g.Settings.Color && fg != color) Console.ForegroundColor = color = fg;
                Console.Write(c);
            }
            Console.WriteLine();
        }
        Console.ResetColor();
        Console.WriteLine(Hud(g));
        foreach (var m in _messages.TakeLast(5)) Console.WriteLine("  " + m);
        Console.WriteLine("[wasd/arrows] move  [q] potion  [.] wait  [S]ave [L]oad  [:] console  [Esc] quit");
    }
}
