namespace Dungeon;

/// <summary>游戏规则：关卡、移动、战斗、怪物 AI、存档读档。这里是普通业务代码，宏的价值体现在它不需要写的那些东西上。</summary>
public sealed class Game
{
    private readonly string _savePath;
    private Random _combat = new();

    public Game(GameSettings settings, int seed, string savePath)
    {
        Current = this;
        Settings = settings;
        _savePath = savePath;
        State.Seed = seed;
        Player.Name = settings.PlayerName;
        Player.Changed += OnPlayerChanged; // @OBSERVABLE / @CLAMP 生成的属性在值变化时调用
    }

    /// <summary>进入第一层（在订阅 Message 之后调用，这样开局消息不会丢）。</summary>
    public void Start() => EnterLevel();

    public static Game? Current { get; private set; }

    public GameSettings Settings { get; }
    public GameState State { get; } = new();
    public Player Player { get; } = new();
    public Map Map { get; private set; } = null!;
    public List<Monster> Monsters { get; } = new();
    public List<Item> Items { get; } = new();

    public bool Over { get; private set; }
    public bool Won { get; private set; }

    /// <summary>状态栏需要重绘（由玩家属性的变更通知设置）。</summary>
    public bool HudDirty { get; set; } = true;

    public int GoldCollected { get; private set; }

    public event Action<string>? Message;
    public void Log(string text) => Message?.Invoke(text);

    // ------------------------------------------------------------------ 关卡

    private void EnterLevel()
    {
        var rng = new Random(State.Seed * 1000 + State.Depth); // 关卡由 (Seed, Depth) 确定，所以存档很小
        _combat = new Random(State.Seed * 7919 + State.Depth * 31 + State.Turn);
        Map = Map.Generate(rng, Settings.MaxRooms);
        Monsters.Clear();
        Items.Clear();
        Player.Pos = Map.Rooms[0].Center;

        var count = Settings.MonstersPerLevel + State.Depth - 1;
        for (var i = 0; i < count; i++)
            if (FreeSpot(rng) is { } p) Monsters.Add(new Monster(Bestiary.ForDepth(rng, State.Depth), p));
        for (var i = 0; i < Settings.PotionsPerLevel; i++)
            if (FreeSpot(rng) is { } p) Items.Add(new Item(ItemKind.Potion, p, 1));
        for (var i = 0; i < 3; i++)
            if (FreeSpot(rng) is { } p) Items.Add(new Item(ItemKind.Gold, p, rng.Next(5, 15) * State.Depth));
        if (State.Depth >= 2 && rng.Next(2) == 0 && FreeSpot(rng) is { } s) Items.Add(new Item(ItemKind.Sword, s, 1));

        HudDirty = true;
        UpdateVision();
        Log($"You enter depth {State.Depth} of {Settings.WinDepth}.");
    }

    /// <summary>起始房间以外、没有被占用的随机地板。</summary>
    private Pos? FreeSpot(Random rng)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var p = Map.Rooms[1 + rng.Next(Map.Rooms.Count - 1)].RandomPoint(rng);
            if (Map[p] == Tile.Floor && MonsterAt(p) == null && Items.All(i => i.Pos != p)) return p;
        }
        return null;
    }

    public Monster? MonsterAt(Pos p) => Monsters.FirstOrDefault(m => m.Pos == p);
    public bool Visible(Pos p) => p.DistanceTo(Player.Pos) <= Settings.ViewRadius;

    private void UpdateVision()
    {
        var r = Settings.ViewRadius;
        for (var dx = -r; dx <= r; dx++)
        for (var dy = -r; dy <= r; dy++)
            Map.MarkSeen(Player.Pos + new Pos(dx, dy));
    }

    // ------------------------------------------------------------------ 玩家行动（每个都消耗一回合）

    public void Move(Pos direction)
    {
        if (Over) return;
        var target = Player.Pos + direction;
        if (MonsterAt(target) is { } monster) Attack(monster);
        else if (Map.Walkable(target))
        {
            Player.Pos = target;
            PickUp();
            if (Map[target] == Tile.Stairs) { Descend(); return; }
        }
        else return; // 撞墙不消耗回合
        EndTurn();
    }

    public void Wait()
    {
        if (!Over) EndTurn();
    }

    public void DrinkPotion()
    {
        if (Over) return;
        if (Player.Potions == 0) { Log("You have no potions."); return; }
        Player.Potions--;
        var before = Player.Hp;
        Player.Hp += 10;
        Log($"You drink a potion: HP {before} -> {Player.Hp}.");
        EndTurn();
    }

    private void PickUp()
    {
        foreach (var item in Items.Where(i => i.Pos == Player.Pos).ToList())
        {
            Items.Remove(item);
            switch (item.Kind)
            {
                case ItemKind.Potion:
                    if (Player.Potions >= 9) { Log("Your bag is full of potions."); Items.Add(item); continue; }
                    Player.Potions++;
                    Log("You pick up a potion.");
                    break;
                case ItemKind.Gold:
                    Player.Gold += item.Amount;
                    Log($"You pick up {item.Amount} gold.");
                    break;
                case ItemKind.Sword:
                    Player.Attack += 2;
                    Log($"You find a better sword! Attack {Player.Attack}.");
                    break;
            }
        }
    }

    private void Attack(Monster monster)
    {
        var damage = _combat.Next(1, Player.Attack + 1);
        monster.Hp -= damage;
        if (monster.Hp > 0)
        {
            Log($"You hit the {monster.Info.Name} for {damage}.");
            return;
        }
        Monsters.Remove(monster);
        State.Kills++;
        Log($"You kill the {monster.Info.Name}. (+{monster.Info.Xp} xp)");
        Player.Xp += monster.Info.Xp;
        while (Player.Xp >= Player.XpToNext)
        {
            Player.Xp -= Player.XpToNext;
            Player.Level++;           // 变更通知 → 升级提示（见 OnPlayerChanged）
            Player.MaxHp += 5;
            Player.Hp = Player.MaxHp;
            Player.Attack++;
        }
    }

    // ------------------------------------------------------------------ 回合结束：怪物行动

    private void EndTurn()
    {
        State.Turn++;
        foreach (var monster in Monsters.ToList())
        {
            var distance = monster.Pos.DistanceTo(Player.Pos);
            monster.State = monster.Hp * 3 < monster.Info.Hp ? MonsterState.Fleeing
                          : Visible(monster.Pos) ? MonsterState.Chasing
                          : MonsterState.Idle;

            if (distance <= 1 && monster.State != MonsterState.Fleeing) MonsterAttack(monster);
            else if (monster.State == MonsterState.Chasing) Step(monster, toward: true);
            else if (monster.State == MonsterState.Fleeing) Step(monster, toward: false);
            if (Over) return;
        }
        UpdateVision();
    }

    private void Step(Monster monster, bool toward)
    {
        var current = monster.Pos.DistanceTo(Player.Pos);
        var best = Pos.Directions
            .Select(d => monster.Pos + d)
            .Where(p => Map.Walkable(p) && p != Player.Pos && MonsterAt(p) == null)
            .OrderBy(p => toward ? p.DistanceTo(Player.Pos) : -p.DistanceTo(Player.Pos))
            .FirstOrDefault(p => toward ? p.DistanceTo(Player.Pos) < current : p.DistanceTo(Player.Pos) > current);
        if (best != default) monster.Pos = best;
    }

    private void MonsterAttack(Monster monster)
    {
        if (Player.God) { Log($"The {monster.Info.Name} hits you, but you are a god."); return; }
        var damage = _combat.Next(1, monster.Info.Attack + 1);
        Player.Hp -= damage;
        Log($"The {monster.Info.Name} hits you for {damage}.");
        if (!Player.Dead) return;
        Over = true;
        Log($"You die on depth {State.Depth}, killed by a {monster.Info.Name}.");
    }

    public void Descend()
    {
        State.Depth++;
        if (State.Depth >= Settings.WinDepth)
        {
            Over = Won = true;
            Log($"You reach depth {State.Depth} and find the Amulet of CSMacro. You win!");
            return;
        }
        EnterLevel();
    }

    // ------------------------------------------------------------------ 玩家属性变化（由宏生成的属性触发）

    private void OnPlayerChanged(string property)
    {
        HudDirty = true;
        switch (property)
        {
            case nameof(Player.Level):
                Log($"*** Level up! You are now level {Player.Level}.");
                break;
            case nameof(Player.Gold):
                GoldCollected = Math.Max(GoldCollected, Player.Gold);
                break;
        }
    }

    // ------------------------------------------------------------------ 存档读档（读写代码由宏生成）

    public void SaveGame()
    {
        using (var writer = new BinaryWriter(File.Create(_savePath)))
        {
            State.Save(writer);
            Player.Save(writer);
        }
        Log($"Game saved ({new FileInfo(_savePath).Length} bytes).");
    }

    public void LoadGame()
    {
        if (!File.Exists(_savePath)) { Log("No saved game."); return; }
        try
        {
            using (var reader = new BinaryReader(File.OpenRead(_savePath)))
            {
                State.Load(reader);
                Player.Load(reader);
            }
            Over = Won = false;
            EnterLevel(); // 关卡按 (Seed, Depth) 重新生成
            Log($"Game loaded: depth {State.Depth}, turn {State.Turn}.");
        }
        catch (InvalidDataException e)
        {
            Log($"Can't load: {e.Message}");
        }
    }

    // ------------------------------------------------------------------ 作弊命令用到的操作

    public string SpawnNear(MonsterKind kind)
    {
        var spot = Pos.Directions.Select(d => Player.Pos + d).FirstOrDefault(p => Map[p] == Tile.Floor && MonsterAt(p) == null);
        if (spot == default) return "no room to spawn";
        var monster = new Monster(kind, spot);
        Monsters.Add(monster);
        return $"spawned {monster} at {spot}";
    }

    public void RevealAll()
    {
        for (var x = 0; x < Map.Width; x++)
        for (var y = 0; y < Map.Height; y++)
            Map.MarkSeen(new Pos(x, y));
    }

    public string Teleport(Pos p)
    {
        if (!Map.Walkable(p) || MonsterAt(p) != null) return $"{p} is not a free floor tile";
        Player.Pos = p;
        UpdateVision();
        return $"teleported to {p}";
    }
}
