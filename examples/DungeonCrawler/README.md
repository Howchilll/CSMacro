# Dungeon Crawler：CSMacro 接入案例

一个完整、独立、真的能玩的终端地牢游戏。它像任何外部项目一样，**通过 NuGet 包接入 CSMacro**，
不引用 CSMacro 仓库里的任何源码。这篇文档按顺序演示接入的全过程：拿到包 → 配置 → 写宏 → 用宏 → 构建运行 → 确认接入成功。

游戏本身包括：

- 随机生成的地牢，可以移动、打怪、捡道具、升级、下楼，下到第 5 层获胜
- 存档读档
- 按 `:` 打开作弊控制台
- `dungeon.ini` 配置文件

```
examples/DungeonCrawler/
├── DungeonCrawler.slnx       解决方案
├── nuget.config              包源（CSMacro 还没发布到 nuget.org 时，指向本地生成的包）
├── Directory.Build.props     CSMacro 版本号、语言版本
├── Dungeon.Macros/           这个游戏自己的宏库（规则为这个项目定制）
└── Dungeon/                  游戏本体
```

## 第 1 步：拿到 CSMacro 包

CSMacro 还没有发布到 nuget.org，先在 CSMacro 仓库里生成本地包。在仓库根目录（`DotNet/`）运行：

```bash
dotnet pack pack/CSMacro -c Release
```

包会生成在 `artifacts/packages/CSMacro.0.1.0.nupkg`。里面有三样东西：

- 写宏用的 API（只作编译期引用）
- NuGet 会自动导入的构建配置
- 预编译好的展开工具

发布到 nuget.org 之后，这一步就不需要了。

## 第 2 步：配置包源

在项目根目录放一个 [nuget.config](nuget.config)，把本地包目录加为包源：

```xml
<configuration>
  <packageSources>
    <add key="csmacro-local" value="../../artifacts/packages" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
```

路径是相对 `nuget.config` 本身的。你自己的项目里，改成指向 CSMacro 仓库的 `artifacts/packages` 即可。

## 第 3 步：项目配置

**宏库** [Dungeon.Macros.csproj](Dungeon.Macros/Dungeon.Macros.csproj)：一个普通的 netstandard2.0 类库，引用 CSMacro 包。

```xml
<PropertyGroup>
  <TargetFramework>netstandard2.0</TargetFramework>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="CSMacro" Version="0.1.0" PrivateAssets="all" />
</ItemGroup>
```

**游戏** [Dungeon.csproj](Dungeon/Dungeon.csproj)：引用 CSMacro 包和宏库。

```xml
<ItemGroup>
  <PackageReference Include="CSMacro" Version="0.1.0" PrivateAssets="all" />
  <ProjectReference Include="../Dungeon.Macros/Dungeon.Macros.csproj" />
</ItemGroup>
```

**[Directory.Build.props](Directory.Build.props)**：统一 CSMacro 的版本号（上面的 `0.1.0` 在本项目里写成 `$(CSMacroVersion)`），
并打开较新的 C# 语法（netstandard2.0 默认是 C# 7.3，写宏时一般会用到新语法）。

配置就这些：

- 不用导入任何 targets，NuGet 会自动导入。
- 不用装 IDE 插件，不用改源码结构。

> **另一种写法**：宏不多时可以不建宏库，直接写在游戏项目的一个文件夹里：
> 在 `Dungeon.csproj` 里加 `<CSMacroDefinition Include="Macros/**/*.cs" />`。
> 这个游戏的宏比较多，而且自成一套，所以放在了单独的宏库里。

## 第 4 步：写宏

宏就是普通的 C# 方法或特性类。例如 [Properties.cs](Dungeon.Macros/Properties.cs) 里的 `@CLAMP`（简化过，完整代码见文件）：

```csharp
[Macro(MacroSite.Member)]
public static T CLAMP<T>(T min, T max, T initial)
{
    // 编译期检查：初始值必须在范围内
    if (Macro.Args.All(a => a.HasConstant) && (/* initial 不在 [min, max] 里 */))
        Macro.Error($"initial value {Macro.Arg(2)} of {Macro.Name} is outside [{Macro.Arg(0)}, {Macro.Arg(1)}]");

    // 输出：带夹值 setter 的属性
    Macro.Emit($"{Macro.Modifiers} {Macro.Type} {Macro.Name} {{ get; set {{ ... }} }} = {Macro.Arg(2)};");
    return default!;
}
```

这个游戏的宏库一共约 370 行：

| 文件 | 宏 |
|---|---|
| [Properties.cs](Dungeon.Macros/Properties.cs) | `@OBSERVABLE`、`@CLAMP` |
| [Persistence.cs](Dungeon.Macros/Persistence.cs) | `[@Persist]`、`@WRITE_PERSISTED`、`@READ_PERSISTED` |
| [Commands.cs](Dungeon.Macros/Commands.cs) | `[@Command]`、`@COMMAND_HELP` |
| [Settings.cs](Dungeon.Macros/Settings.cs) | `[@Setting]`、`@APPLY_SETTINGS`、`@SETTINGS_TEMPLATE` |
| [Diagnostics.cs](Dungeon.Macros/Diagnostics.cs) | `@ASSERT`、`@DUMP`、`#region @TRACE_AI` |

## 第 5 步：在游戏里用宏

```csharp
// Entities.cs：玩家属性，每个一行
[@Persist] public int Hp = @CLAMP(0, MaxHp, 20);
[@Persist] public int Gold = @CLAMP(0, 999_999, 0);
public void Save(BinaryWriter writer) { @WRITE_PERSISTED(writer); }

// DebugConsole.cs：作弊命令，写一个方法就完成
[@Command("spawn", "Spawn a monster next to you")]
public static string Spawn(MonsterKind kind) => G.SpawnNear(kind);

// GameSettings.cs：配置文件绑定
[@Setting("view_radius", "How many tiles you can see")] public int ViewRadius = @CLAMP(3, 20, 8);
```

`using static Dungeon.Macros.Properties;` 之后，IDE 就能补全 `@CLAMP`，F12 能跳到宏的实现，写错参数会直接标红。

## 第 6 步：构建和运行

在 `examples/DungeonCrawler/` 下：

```bash
# 构建（第一次会从本地包源还原 CSMacro）
dotnet build

# 机器人自动玩一局，中途插入控制台命令和存档读档。种子固定，每次结果相同
dotnet run --project Dungeon -- --demo

# 交互模式：自己玩。需要真实终端（macOS 终端、Windows Terminal、VS Code 集成终端都可以）
dotnet run --project Dungeon

# 生成带说明的默认配置文件
dotnet run --project Dungeon -- --write-settings
```

操作：`wasd` / 方向键移动（撞到怪物就是攻击），`q` 喝药，`.` 原地等待，`S` 存档，`L` 读档，`:` 打开控制台（输入 `help` 看所有命令），`Esc` 退出。

## 第 7 步：确认接入成功

**构建输出里有这样一行**，说明宏已经被展开了：

```
csmacro: expanded macros in 4 of 11 file(s), generated CSMacro.Startup.g.cs; not needed at runtime: CSMacro.Abstractions, Dungeon.Macros
```

这一行说明了三件事：

- 4 个文件里有宏，被展开了。
- 生成了汇总启动代码的辅助脚本，`[@Command]` 的自动注册就在里面。
- 宏库和 CSMacro 的 API 运行时都用不到，已经从输出中移除。

**输出目录里只有游戏自己的文件**：

```
Dungeon  Dungeon.deps.json  Dungeon.dll  Dungeon.pdb  Dungeon.runtimeconfig.json  dungeon.ini
```

没有 `CSMacro.*.dll`，也没有 `Dungeon.Macros.dll`，`Dungeon.deps.json` 里也不提到它们。宏只存在于编译期。

## 常见问题

- **重新生成了同版本的 CSMacro 包，但项目里没生效**：NuGet 会缓存同一版本的包。
  可以把 `Directory.Build.props` 里的 `CSMacroVersion` 改成新版本号，或者删掉缓存目录 `~/.nuget/packages/csmacro/` 后重新构建。
- **找不到展开工具 / 运行时版本不对**：展开工具运行在 .NET 10 上，构建机器需要装 .NET 10 及以上的 SDK。
- **交互模式没反应**：需要真实终端。输入被重定向时（比如在 CI 里），程序会自动改跑 demo。
- **dump 时宏库目录下也多了一个 `csmacro-dump/`**：命令行上的 `-p:` 属性会传给所有被构建的项目，宏库也会写一份（里面只有报告）。
  它已被 `.gitignore` 排除，可以忽略。

## demo 节选

```
  > :help
      descend                            Go down one level
      god                                Toggle invulnerability
      gold <amount:int>                  Give gold
      heal [amount:int=999]              Restore HP
      spawn <kind:rat|goblin|orc|troll>  Spawn a monster next to you
      status                             Show hero stats
      ...
  > :set view_radius 99
  [t  4] view_radius = 99 is out of range, using 20
  > :set monsters_per_level lots
  [t  4] monsters_per_level: can't read 'lots' as int
  > :set speed 3
  [t  4] unknown setting 'speed'; known: player_name, view_radius, max_rooms, monsters_per_level, potions_per_level, win_depth, color
  > :set view_radius 8
  [t 18] You kill the goblin. (+4 xp)
  > :save
  [t 40] Game saved (57 bytes).
  > :gold 500
  > :load
  [t 40] Game loaded: depth 1, turn 40.
  [t 70] *** Level up! You are now level 2.
  ...
  [t447] You reach depth 5 and find the Amulet of CSMacro. You win!
               #############       #######  ########
               #...........#########.....####......#
    ############...................................#
    ...
Hero  HP 39/40  Atk 7  Lv 5 (13/50 xp)  Gold 200  Potions 3  Depth 5/5
Demo finished: the bot won.
```

## 宏用在了哪里

每个宏都对应一个真实的工程需求：

| 需求 | 你写的 | 宏生成的 | 位置 |
|---|---|---|---|
| 数值要有范围（血量上限是另一个属性） | `public int Hp = @CLAMP(0, MaxHp, 20);` | 夹值的 setter，变化时发通知 | [Entities.cs](Dungeon/Entities.cs) |
| 界面要知道数值变了 | `public int Xp = @OBSERVABLE(0);` | 相等判断 + 变更通知，驱动状态栏重绘和升级提示 | [Entities.cs](Dungeon/Entities.cs)、[Game.cs](Dungeon/Game.cs) |
| 存档 | 字段上标 `[@Persist]`，`Save` 里写 `@WRITE_PERSISTED(writer)` | 强类型的二进制读写 + 存档格式指纹 | [Entities.cs](Dungeon/Entities.cs) |
| 作弊控制台 | 静态方法上标 `[@Command("spawn", "...")]` | 参数解析（含枚举、默认值）、启动时注册、帮助表 | [DebugConsole.cs](Dungeon/DebugConsole.cs) |
| 配置文件 | 字段上标 `[@Setting("view_radius", "...")]` | 解析、类型转换、越界 / 格式错误 / 未知键提示、默认配置文件模板 | [GameSettings.cs](Dungeon/GameSettings.cs) |
| 地图生成的不变量 | `@ASSERT(map.Rooms.Count >= 2, "...");` | Debug 构建：检查代码；Release 构建：什么都没有 | [World.cs](Dungeon/World.cs) |
| 排查怪物 AI | `#region @TRACE_AI` 包住三个字段 | 加 `-p:TraceAI=true` 构建时，生成状态变化跟踪日志；平时什么都不生成 | [Entities.cs](Dungeon/Entities.cs) |
| 查看状态 | `@DUMP(G.Player)` | 编译期遍历成员生成的拼接代码 | [DebugConsole.cs](Dungeon/DebugConsole.cs) |

宏的实现都在 [Dungeon.Macros](Dungeon.Macros/)，一共约 370 行普通 C#。

## 价值：数字

数据来自 `dotnet build Dungeon -p:CSMacroDump=true` 生成的展开报告，按格式化后的行数统计：

| 宏 | 用了几处 | 生成的代码（行） |
|---|---|---|
| `@CLAMP` | 10 | 115 |
| `@APPLY_SETTINGS` + `@SETTINGS_TEMPLATE` | 2 | 103 |
| `[@Command]` + `@COMMAND_HELP` | 12 | 73 |
| `[@Persist]` + `@WRITE_PERSISTED` / `@READ_PERSISTED` | 12 + 4 | 38 |
| `@OBSERVABLE` | 3 | 33 |
| `@ASSERT` | 3 | 12 |
| `@DUMP` | 1 | 1（但会随类型的成员自动变化） |
| **合计** | **源码里 42 行** | **375 行** |

游戏本体一共 772 行。如果不用宏，大约要写到 1100 行，**多出来的全是最容易漏改、写错的样板代码**。

## 价值：改需求的时候

比如给玩家加一个"护甲"属性，要求有范围、界面要能看到、要存档。

**用宏**：加一行就完了。

```csharp
[@Persist] public int Armor = @CLAMP(0, 20, 0);
```

**不用宏**，要改这几个地方，而且漏掉任何一处都不会编译报错：

1. 写后备字段和带夹值、相等判断、通知的属性，大约 12 行
2. `Save` 里加一行写出
3. `Load` 里加一行读回，**顺序必须和 Save 完全一致**
4. 把存档版本号加一，否则旧存档会读出错位的数据
5. `status` 命令的输出里加上它

同样，加一个配置项只需要一行 `[@Setting(...)]`。解析、校验、错误提示、默认配置文件模板都会自动跟上。
加一个作弊命令也只需要写一个方法，帮助表自动更新，命令名重复会在编译期报错。

## 写错了会怎样：编译期就报错

下面是在这个项目里故意写错时的真实输出，每条都报在写错的那一行上：

```
error CSM100: @CLAMP: initial value 25 of A is outside [3, 20]
error CSM100: [@Setting]: Table has type System.Collections.Generic.List<int>, which [@Setting] can't parse; supported: int, long, float, double, bool, string, enums
error CSM100: @APPLY_SETTINGS: setting key 'view_radius' is bound twice: A and B
error CSM100: [@Persist]: Where has type Dungeon.Pos, which [@Persist] can't store; supported: int, long, float, double, bool, string, enums
error CSM100: @OBSERVABLE: BadPlayer must declare `void OnPropertyChanged(string propertyName)` for @OBSERVABLE to call
error CSM100: @COMMAND_HELP: command 'heal' is registered more than once: Dungeon.BadCheats.Heal2 (Bad.cs:13) and Dungeon.Cheats.Heal (DebugConsole.cs:49)
error CSM017: Gold is turned into a property by @CLAMP, so it can't be passed as `out`. The IDE still sees a field here, but the compiled code has a property
```

## 同一份源码，三种构建

```bash
dotnet build Dungeon                     # 普通 Debug：有 @ASSERT 检查，没有 AI 跟踪
dotnet build Dungeon -c Release          # Release：@ASSERT 展开成空语句，零开销
dotnet build Dungeon -p:TraceAI=true     # 排查 AI：怪物状态的每次变化都写一条日志
```

用 `-p:TraceAI=true` 构建后跑 demo，会多出这样的日志（一局约 400 条）：

```
  [t  0] [trace] goblin#3.Pos: (0,0) -> (44,11)
  [t  0] [trace] goblin#3.Hp: 0 -> 6
  ...
```

源码一个字都不用改，也不用担心排查完忘了删日志。

## 自己看展开结果

```bash
dotnet build Dungeon -p:CSMacroDump=true                                  # 全部
dotnet build Dungeon -p:CSMacroDumpFiles=GameSettings.cs                  # 只看配置
dotnet build Dungeon -p:CSMacroDumpFiles="Entities.cs" -p:CSMacroDumpMacros="Persist WRITE_PERSISTED READ_PERSISTED"
```

结果在 `Dungeon/csmacro-dump/`，展开报告是其中的 `_expansions.txt`。

## 设计上的取舍

- **存档只存进度，不存关卡**：关卡由 (Seed, Depth) 确定性生成，所以存档只有几十个字节。读档会回到当前层的初始状态（怪物重新生成），这是有意的简化。
- **交互模式需要真实终端**：输入被重定向时（比如在 CI 里），程序会自动改跑 demo。
