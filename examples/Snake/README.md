# Snake：CSMacro 接入案例

一个终端贪吃蛇。游戏逻辑刻意保持简单，让篇幅留给宏。它是一个独立项目，像任何外部项目一样**通过 NuGet 包接入 CSMacro**，
宏直接写在项目自己的 `Macros/` 文件夹里，不需要单独建宏库。

```
examples/Snake/
├── Snake.csproj            接入 CSMacro：两行配置
├── nuget.config            包源（CSMacro 还没发布到 nuget.org，指向本地生成的包）
├── Directory.Build.props   CSMacro 版本号
├── Macros/                 这个游戏自己的宏（约 250 行普通 C#）
├── Game.cs                 蛇、食物、分数、按键
├── GameSettings.cs         snake.ini 配置
├── Record.cs               最高分存档
├── Program.cs              交互模式 + demo 机器人
└── QuickStart/             一键构建 / demo / 游玩的 Python 脚本
```

## 第 1 步：拿到 CSMacro 包

CSMacro 还没有发布到 nuget.org，先在 CSMacro 仓库根目录（`DotNet/`）生成本地包：

```bash
dotnet pack pack/CSMacro -c Release
```

包会生成在 `artifacts/packages/`。发布到 nuget.org 之后，这一步和下一步都不需要了。

## 第 2 步：配置包源

[nuget.config](nuget.config) 把本地包目录加为包源（路径相对 `nuget.config` 本身）：

```xml
<packageSources>
  <add key="csmacro-local" value="../../artifacts/packages" />
  <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
</packageSources>
```

## 第 3 步：项目配置

[Snake.csproj](Snake.csproj) 里加两行：

```xml
<ItemGroup>
  <PackageReference Include="CSMacro" Version="0.1.1" PrivateAssets="all" />
  <CSMacroDefinition Include="Macros/**/*.cs" />
</ItemGroup>
```

第一行引用 CSMacro 包，第二行告诉它宏写在 `Macros/` 文件夹里。就这些：

- 不用导入 targets，NuGet 会自动导入。
- 不用装 IDE 插件，不用建宏库项目。

## 第 4 步：写宏

宏就是普通的 C# 方法或特性类，写在 `Macros/` 里：

| 文件 | 宏 | 作用 |
|---|---|---|
| [Properties.cs](Macros/Properties.cs) | `@OBSERVABLE`、`@CLAMP` | 变更通知属性、范围约束属性 |
| [Keys.cs](Macros/Keys.cs) | `[@Key]`、`@DISPATCH_KEYS`、`@KEY_HELP` | 按键绑定：生成按键分发的 switch 和按键说明 |
| [Settings.cs](Macros/Settings.cs) | `[@Setting]`、`@APPLY_SETTINGS`、`@SETTINGS_TEMPLATE` | 配置文件绑定：解析、校验、错误提示、默认模板 |
| [Persistence.cs](Macros/Persistence.cs) | `[@Persist]`、`@WRITE_PERSISTED`、`@READ_PERSISTED` | 强类型存档 + 格式指纹 |
| [Diagnostics.cs](Macros/Diagnostics.cs) | `@ASSERT`、`#region @TRACE_STATE` | Debug 才有的断言、按需打开的状态跟踪 |

比如按键绑定宏的核心就几行（[Keys.cs](Macros/Keys.cs)）：

```csharp
[Macro(MacroSite.Statement)]
public static void DISPATCH_KEYS(ConsoleKey key)
{
    // 同一个键绑定了两次 → 编译期报错
    ...
    Macro.Emit($"switch ({Macro.Arg(0)}) {{");
    foreach (var b in Bindings())   // 所在类型里所有标了 [@Key] 的方法
        Macro.Emit(string.Concat(b.Codes.Select(c => $"case (global::System.ConsoleKey){c}: ")) + $"{b.Method}(); break;");
    Macro.Emit("}");
}
```

**宏文件的约束**：它们先于游戏编译，所以只能用 CSMacro 的 API 和标准库，不能用游戏自己的类型（比如 `Game`、`Pos`）。
宏输出的代码里当然可以引用它们，那只是字符串。

## 第 5 步：在游戏里用宏

[Game.cs](Game.cs)：

```csharp
public int Score = @OBSERVABLE(0);        // 分数变化 → OnPropertyChanged：破纪录提示、每 5 分加速
public int Tick = @CLAMP(30, 500, 120);   // 速度有范围，加速再多也不会低于 30ms

[@Key("up", ConsoleKey.W, ConsoleKey.UpArrow)] private void Up() => Turn(Dir.Up);
[@Key("pause", ConsoleKey.P, ConsoleKey.Spacebar)] private void TogglePause() => Paused = !Paused;
...
public void HandleKey(ConsoleKey key) { @DISPATCH_KEYS(key); }
public static string KeyHelp => @KEY_HELP();   // "[W/UpArrow] up  [P/Spacebar] pause  ..."

@ASSERT(_body.Distinct().Count() == _body.Count, "the snake overlaps itself");
```

[GameSettings.cs](GameSettings.cs)：

```csharp
[@Setting("width", "Board width")] public int Width = @CLAMP(10, 60, 30);
[@Setting("wrap_walls", "Leave on one side, come back on the other")] public bool WrapWalls = false;
public void Apply(IEnumerable<KeyValuePair<string, string>> values, Action<string> report) { @APPLY_SETTINGS(values, report); }
```

[Record.cs](Record.cs)：

```csharp
[@Persist] public int Best;
[@Persist] public int GamesPlayed;
public void Save(BinaryWriter writer) { @WRITE_PERSISTED(writer); }
```

`using static Snake.Macros.Properties;` 之后，IDE 能补全 `@CLAMP`、F12 跳到宏的实现、参数写错直接标红。

## 第 6 步：构建和运行

在 `examples/Snake/` 下：

```bash
dotnet build
```

```bash
dotnet run -- --demo
```

```bash
dotnet run
```

- `--demo`：一个贪心机器人自动玩一局，种子固定，每次结果相同。机器人的按键也会经过宏生成的按键分发代码。
- 不带参数：交互模式，自己玩。需要真实终端（macOS 终端、Windows Terminal、VS Code 集成终端都可以）。
  `wasd` / 方向键转向，`P` / 空格暂停，`Esc` / `Q` 退出。
- `dotnet run -- --write-settings`：重新生成带说明的 `snake.ini`。

也可以用 [QuickStart/](QuickStart/) 里的脚本，在任意目录下运行都行：`python3 QuickStart/build.py`、`python3 QuickStart/demo.py`（可以带 `--seed 5` 之类的参数）、`python3 QuickStart/play.py`。

demo 的输出（节选，棋盘省略了空行）：

```
Snake demo. Keys: [W/UpArrow] up  [S/DownArrow] down  [A/LeftArrow] left  [D/RightArrow] right  [P/Spacebar] pause  [Escape/Q] quit
...
Score 21   Best 21   Speed 12/s
Game over: the snake bit itself. Score 31 (new best!)
+------------------------------+
|              o               |
|              o               |
|       oooooo o               |
|       o@oooo o               |
|       oooooo o        *      |
|            oooo              |
+------------------------------+
Games played 1, food eaten 31, best 31.
```

再跑一次（换个种子 `--seed 5`），最高分是从存档里读回来的：

```
New best! (old best 31)
Game over: the snake bit itself. Score 34 (new best!)
Games played 2, food eaten 65, best 34.
```

## 第 7 步：确认接入成功

构建输出里有这一行，说明宏已经展开：

```
csmacro: expanded macros in 3 of 7 file(s); not needed at runtime: CSMacro.Abstractions, Snake.CSMacroLocal
```

这一行说明了两件事：

- 3 个文件里有宏，被展开了。
- `Macros/` 编译出的临时程序集和 CSMacro 的 API 运行时都用不到，已经从输出中移除。

输出目录里只有游戏自己的文件，没有任何 `CSMacro.*.dll`（玩过之后还会多一个存档 `snake.sav`）：

```
Snake  Snake.deps.json  Snake.dll  Snake.pdb  Snake.runtimeconfig.json  snake.ini
```

## 宏替你写了什么

数据来自 `dotnet build -p:CSMacroDump=true` 生成的展开报告，按格式化后的行数统计：

| 宏 | 用了几处 | 生成的代码（行） |
|---|---|---|
| `@APPLY_SETTINGS` + `@SETTINGS_TEMPLATE` | 2 | 64 |
| `@CLAMP` | 4 | 45 |
| `[@Key]` + `@DISPATCH_KEYS` + `@KEY_HELP` | 6 + 2 | 30 |
| `@OBSERVABLE` | 1 | 11 |
| `[@Persist]` + `@WRITE_PERSISTED` / `@READ_PERSISTED` | 3 + 2 | 13 |
| `@ASSERT` | 2 | 8 |
| **合计** | **游戏里 24 行** | **171 行** |

游戏本身一共 250 行（含交互模式和 demo 机器人），不用宏大约要写到 400 行。

更重要的是改需求时只改一处：

- **加一个按键**：只写一个方法加一个 `[@Key]`。分发代码和按键说明自动更新，键冲突编译期就报。
- **加一个配置项**：只写一行 `[@Setting]`。解析、校验、错误提示、默认模板自动跟上。
- **加一个存档字段**：只加一个 `[@Persist]`。读写顺序和格式指纹自动跟上，读到旧存档会明确报错而不是读出错位的数据。

## 写错了会怎样：编译期就报错

在这个项目里故意写错时的真实输出，每条都报在写错的那一行上：

```
error CSM100: @DISPATCH_KEYS: key W is bound more than once: Jump and Fire
error CSM100: [@Key]: [@Key] method Move must have no parameters, it is called when the key is pressed
error CSM100: @CLAMP: initial value 20 of Speed is outside [1, 10]
error CSM100: [@Setting]: Colors has type System.Collections.Generic.List<string>, which [@Setting] can't parse; supported: int, long, float, double, bool, string, enums
error CSM020: CS0236: A field initializer cannot reference the non-static field, method, or property 'BadSettings.Size' -- this code is only valid after @CLAMP is expanded, so the IDE shows it as an error. ...
```

宏文件本身写错了也一样：比如宏里的变量名冲突，会报 `CSM018`，并指到 `Macros/` 里出错的那一行。

运行时的配置错误也有清楚的提示（`snake.ini` 里写了越界值、错误格式、不存在的键）：

```
[snake] width = 200 is out of range, using 60
[snake] height: can't read 'tall' as int
[snake] unknown setting 'speed'; known: width, height, tick_ms, wrap_walls
```

## 同一份源码，三种构建

```bash
dotnet build                          # Debug：有 @ASSERT 检查，没有状态跟踪
dotnet build -c Release               # Release：@ASSERT 展开成空语句，零开销
dotnet build -p:TraceState=true       # 排查：Heading 和 Food 的每次变化都打出来
```

`-p:TraceState=true` 构建后跑 demo：

```
[trace] Food: Pos { X = 0, Y = 0 } -> Pos { X = 21, Y = 3 }
[trace] Heading: Right -> Up
[trace] Heading: Up -> Right
```

源码一个字都不用改，排查完也不用记得删日志。

## 看展开结果

```bash
dotnet build -p:CSMacroDump=true                                      # 全部
dotnet build -p:CSMacroDumpFiles=Game.cs -p:CSMacroDumpMacros="Key DISPATCH_KEYS"
```

结果在 `csmacro-dump/`：展开后的文件和源码逐行对齐，`_expansions.txt` 是格式化成多行的展开报告。

## 常见问题

- **重新生成了同版本的 CSMacro 包但没生效**：NuGet 会缓存同一版本的包。改 `Directory.Build.props` 里的 `CSMacroVersion`，
  或者删掉 `~/.nuget/packages/csmacro/` 后重新构建。
- **构建报找不到展开工具 / 运行时版本不对**：展开工具运行在 .NET 10 上，构建机器需要装 .NET 10 及以上的 SDK。
- **交互模式没反应**：需要真实终端。输入被重定向时（比如在 CI 里）会自动改跑 demo。
