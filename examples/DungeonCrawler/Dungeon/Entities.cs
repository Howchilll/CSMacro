using Dungeon.Macros;
using static Dungeon.Macros.Persistence;
using static Dungeon.Macros.Properties;

namespace Dungeon;

/// <summary>
/// 玩家。界面要知道数值变了没有（重绘状态栏、升级提示、统计），数值要有范围，还要存档。
/// 每个属性一行写清楚：名字、类型、初值、范围、要不要存档。
/// </summary>
public sealed class Player
{
    public event Action<string>? Changed;
    private void OnPropertyChanged(string propertyName) => Changed?.Invoke(propertyName);

    public Pos Pos;
    public bool God; // 作弊状态，不存档

    [@Persist] public string Name = @OBSERVABLE("Hero");
    [@Persist] public int MaxHp = @OBSERVABLE(20);
    [@Persist] public int Hp = @CLAMP(0, MaxHp, 20);       // 上限是另一个属性，不是常量
    [@Persist] public int Attack = @CLAMP(1, 99, 3);
    [@Persist] public int Level = @CLAMP(1, 50, 1);
    [@Persist] public int Xp = @OBSERVABLE(0);
    [@Persist] public int Gold = @CLAMP(0, 999_999, 0);
    [@Persist] public int Potions = @CLAMP(0, 9, 1);

    public int XpToNext => Level * 10;
    public bool Dead => Hp <= 0;

    public void Save(BinaryWriter writer) { @WRITE_PERSISTED(writer); }
    public void Load(BinaryReader reader) { @READ_PERSISTED(reader); }
}

/// <summary>游戏进度。关卡由 (Seed, Depth) 确定性生成，所以存档只需要这几个数。</summary>
public sealed class GameState
{
    [@Persist] public int Seed;
    [@Persist] public int Depth = 1;
    [@Persist] public int Turn;
    [@Persist] public int Kills;

    public void Save(BinaryWriter writer) { @WRITE_PERSISTED(writer); }
    public void Load(BinaryReader reader) { @READ_PERSISTED(reader); }
}

public enum MonsterKind { Rat, Goblin, Orc, Troll }

public enum MonsterState { Idle, Chasing, Fleeing }

public sealed record MonsterInfo(char Glyph, string Name, int Hp, int Attack, int Xp);

public static class Bestiary
{
    public static MonsterInfo Of(MonsterKind kind) => kind switch
    {
        MonsterKind.Rat => new('r', "rat", 3, 1, 2),
        MonsterKind.Goblin => new('g', "goblin", 6, 2, 4),
        MonsterKind.Orc => new('o', "orc", 10, 3, 8),
        MonsterKind.Troll => new('T', "troll", 16, 5, 15),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>越深越可能出现强怪。</summary>
    public static MonsterKind ForDepth(Random rng, int depth) => rng.Next(depth + 2) switch
    {
        0 or 1 => MonsterKind.Rat,
        2 => MonsterKind.Goblin,
        3 or 4 => MonsterKind.Orc,
        _ => MonsterKind.Troll,
    };
}

public sealed class Monster
{
    private static int s_nextId;

    public Monster(MonsterKind kind, Pos pos)
    {
        Kind = kind;
        Pos = pos;
        Hp = Info.Hp;
    }

    public int Id { get; } = ++s_nextId;
    public MonsterKind Kind { get; }
    public MonsterInfo Info => Bestiary.Of(Kind);

    // 排查 AI 时：dotnet build -p:TraceAI=true，这几个字段的每次变化都会写一条跟踪日志。
    // 平时这个片段宏什么都不改，零开销；也不用担心排查完忘了删日志。
    #region @TRACE_AI
    public Pos Pos;
    public int Hp;
    public MonsterState State = MonsterState.Idle;
    #endregion

    public override string ToString() => $"{Info.Name}#{Id}";
}

public enum ItemKind { Potion, Gold, Sword }

public sealed record Item(ItemKind Kind, Pos Pos, int Amount)
{
    public char Glyph => Kind switch { ItemKind.Potion => '!', ItemKind.Gold => '$', _ => '/' };
}

/// <summary>@TRACE_AI 生成的代码写到这里；由程序决定输出到哪。</summary>
public static class TraceLog
{
    public static Action<string>? Sink { get; set; }
    public static void Write(object owner, string member, object? from, object? to) => Sink?.Invoke($"[trace] {owner}.{member}: {from} -> {to}");
}
