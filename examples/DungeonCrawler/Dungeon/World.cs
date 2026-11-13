using static Dungeon.Macros.Diagnostics;

namespace Dungeon;

public readonly record struct Pos(int X, int Y)
{
    public static Pos operator +(Pos a, Pos b) => new(a.X + b.X, a.Y + b.Y);
    public int DistanceTo(Pos o) => Math.Max(Math.Abs(X - o.X), Math.Abs(Y - o.Y)); // 切比雪夫距离：8 方向移动
    public static readonly Pos[] Directions =
        { new(0, -1), new(0, 1), new(-1, 0), new(1, 0), new(-1, -1), new(1, -1), new(-1, 1), new(1, 1) };
    public override string ToString() => $"({X},{Y})";
}

public enum Tile : byte { Wall, Floor, Stairs }

public readonly record struct Room(int X, int Y, int W, int H)
{
    public Pos Center => new(X + W / 2, Y + H / 2);
    public bool Overlaps(Room o) => X - 1 < o.X + o.W && X + W + 1 > o.X && Y - 1 < o.Y + o.H && Y + H + 1 > o.Y;
    public Pos RandomPoint(Random rng) => new(rng.Next(X, X + W), rng.Next(Y, Y + H));
}

public sealed class Map
{
    public const int Width = 60, Height = 20;

    private readonly Tile[,] _tiles = new Tile[Width, Height];
    private readonly bool[,] _seen = new bool[Width, Height];

    public List<Room> Rooms { get; } = new();
    public Pos Stairs { get; private set; }

    public Tile this[Pos p] => InBounds(p) ? _tiles[p.X, p.Y] : Tile.Wall;
    public bool InBounds(Pos p) => p.X >= 0 && p.Y >= 0 && p.X < Width && p.Y < Height;
    public bool Walkable(Pos p) => this[p] != Tile.Wall;
    public bool Seen(Pos p) => InBounds(p) && _seen[p.X, p.Y];
    public void MarkSeen(Pos p) { if (InBounds(p)) _seen[p.X, p.Y] = true; }

    /// <summary>随机放置互不重叠的房间，按顺序用 L 形走廊连起来；最后一个房间放楼梯。</summary>
    public static Map Generate(Random rng, int maxRooms)
    {
        var map = new Map();
        for (var attempt = 0; attempt < 300 && map.Rooms.Count < maxRooms; attempt++)
        {
            int w = rng.Next(5, 13), h = rng.Next(3, 7);
            var room = new Room(rng.Next(1, Width - w - 1), rng.Next(1, Height - h - 1), w, h);
            if (map.Rooms.Any(r => r.Overlaps(room))) continue;

            map.Carve(room);
            if (map.Rooms.Count > 0) map.Corridor(map.Rooms[^1].Center, room.Center, rng.Next(2) == 0);
            map.Rooms.Add(room);
        }

        // 不变量检查：Debug 构建里才生成，Release 构建里展开成空语句
        @ASSERT(map.Rooms.Count >= 2, "a level needs at least two rooms");

        map.Stairs = map.Rooms[^1].Center;
        map._tiles[map.Stairs.X, map.Stairs.Y] = Tile.Stairs;

        @ASSERT(map.Walkable(map.Rooms[0].Center));
        @ASSERT(map.Stairs != map.Rooms[0].Center, "stairs must not be in the starting room's center");
        return map;
    }

    private void Carve(Room r)
    {
        for (var x = r.X; x < r.X + r.W; x++)
        for (var y = r.Y; y < r.Y + r.H; y++)
            _tiles[x, y] = Tile.Floor;
    }

    private void Corridor(Pos a, Pos b, bool horizontalFirst)
    {
        var corner = horizontalFirst ? new Pos(b.X, a.Y) : new Pos(a.X, b.Y);
        Line(a, corner);
        Line(corner, b);
    }

    /// <summary>a、b 在同一行或同一列（L 形走廊的一段）。</summary>
    private void Line(Pos a, Pos b)
    {
        for (var x = Math.Min(a.X, b.X); x <= Math.Max(a.X, b.X); x++) _tiles[x, a.Y] = Tile.Floor;
        for (var y = Math.Min(a.Y, b.Y); y <= Math.Max(a.Y, b.Y); y++) _tiles[b.X, y] = Tile.Floor;
    }
}
