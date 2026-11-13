# CSMacro 设计（控制台 / 普通 .NET 阶段）

## 一句话

宏就是**真实存在的 C# 代码**：方法宏是带 `[Macro]` 的方法，特性宏是带 `[AttributeMacro]` 的特性类，片段宏是带 `[RegionMacro]` 的方法。
IDE 把 `@GETSET(...)`、`[@Save]` 当作普通的方法调用和特性（可以补全、做类型检查、F12 跳进宏实现）；
编译前，展开器**执行宏的方法体（或构造函数体）**拿到代码；运行时只剩展开后的代码。

```csharp
// 宏库 Demo.Macros
[Macro(MacroSite.Member)]
public static T GETSET<T>(T target)
{
    var path = Macro.Arg(0);
    Macro.Emit($"{Macro.Modifiers} {Macro.Type} {Macro.Name} {{ get => {path}; set => {path} = value; }}");
    return default!;
}

// 业务代码
public int Hp = @GETSET(DataCenter.PlayerData.Hp);
// 编译器实际拿到：
public int Hp { get => DataCenter.PlayerData.Hp; set => DataCenter.PlayerData.Hp = value; }
```

## 核心原则：不展开宏，项目静态上也必须正确

**名字和类型只能由源码声明，宏只改实现。**

宏解决的是设计模式解决不了的样板代码问题，比如"Player 的属性改变时都要打 log"。
用宏统一封装，就不用在每个 setter 里写几乎一样的代码：

```csharp
public int Gold = @LOGGED(100);
public int Level = @LOGGED(1);
public string Title = @LOGGED("Novice");
```

IDE 看到的是三个字段，编译器拿到的是三个带 log 的属性。对所有使用 `Gold` 的代码来说两者等价，
所以 IDE 的补全、跳转、重命名、类型检查永远可信，**不需要任何 IDE 插件或源码生成器**。

引擎强制执行这条原则：

| 场景 | 规则 | 违反时 |
|---|---|---|
| Member 方法宏 | 展开结果必须正好是一个字段或属性，名字和类型与源码声明一致 | CSM008（名字）/ CSM009（类型） |
| Statement 方法宏 | 展开结果自动包进 `{ }`，宏内声明的局部变量出不了这个块 | 不会发生 |
| Expression 方法宏 | 本来就不引入名字 | 不会发生 |
| 特性宏 | 不改它标记的声明；产出的片段只能引用源码里已有的名字 | 引用了不存在的名字时报 CSM013 |
| 片段宏 | 逐成员替换：同 Member 方法宏；整段文本替换：字段和属性的名字、类型不变，成员名字集合不变 | CSM008 / CSM009 |

宏生成的新名字 IDE 看不见，按这条原则这种宏一律不允许，不去事后补救。

**字段变成属性后的用法检查（CSM017）**：Member 宏和片段宏会把字段变成同名同类型的属性。
对绝大多数代码这两者等价，但有几种用法只对字段成立。IDE 看到的是字段，不会报错，展开后才编译失败，
而且编译器的报错和源码对不上。引擎会在整个项目里找出这些用法，在用法所在的行报错：

- `ref` / `out` 实参，比如 `Interlocked.Increment(ref p.Gold)`、`int.TryParse(s, out Hp)`
- `ref` 表达式，比如 `ref int hp = ref Hp;`
- 取地址 `&p.Gold`
- 修改结构体类型成员的内部，比如 `p.Pos.X = 1`、`p.Pos.X++`：属性返回的是副本，修改会丢失

```
error CSM017: Gold is turned into a property by @LOGGED, so it can't be passed as `ref`. The IDE still sees a field here, but the compiled code has a property
```

## 三种宏

| | 方法宏 `@X(...)` | 特性宏 `[@X]` | 片段宏 `#region @X` |
|---|---|---|---|
| 定义 | 带 `[Macro(MacroSite...)]` 的静态方法 | 带 `[AttributeMacro(MacroTarget...)]` 的特性类 | 带 `[RegionMacro]` 的静态方法 |
| 展开逻辑 | 方法体 | 构造函数体 | 方法体 |
| 作用 | **原地展开**，替换调用处 | **产出辅助代码片段**，不改它标记的声明 | **处理片段里的一组成员** |
| 输出 API | `Macro.Emit(code)` | `Macro.Fragment` / `Macro.Startup` | 逐个 `member.Replace(code)`，或 `Macro.Emit(全文)` |
| 展开后 | 调用处被替换 | 特性从源码中删除 | 成员被替换；`#region` 变成 `#region [expanded] @X` |
| IDE 支持 | 补全、跳转、类型检查 | 补全、跳转 | 无（`#region` 后的文字对 IDE 只是注释） |
| 例子 | `@GETSET`、`@LOGGED`、`@LOG` | `[@Save]`：字段自动进入存档容器 | `@LOG_ALL`：给一组字段加自动 log |

`[@Save]` 本身就是合法的 C#：`@` 让编译器只查找名为 `Save` 的特性类，不会自动补 `Attribute` 后缀。

### 特性宏的片段怎么落地

**A. 由方法宏消费**：特性宏用 `Macro.Fragment("track")` 把片段产出到某个频道，
方法宏用 `Macro.Fragments("track")` 读取后拼进代码。每个片段都自带被标记声明的信息（所在类型、名字、类型、位置）。

```csharp
[@Track] public int Hp = @GETSET(...);
[@Track] public int Gold = @LOGGED(100);

Console.WriteLine(@DUMP_TRACKED(p));   // → ("Hp=" + p.Hp + ", " + "Gold=" + p.Gold)
```

**B. 生成独立的辅助脚本**：特性宏用 `Macro.Startup(code)` 产出启动代码。
引擎把所有启动代码汇总成 `CSMacro.Startup.g.cs`，在程序启动时自动执行，不需要任何手写代码：

```csharp
[@Save] public int Gold = @LOGGED(100);
[@Save("lv")] public int Level = @LOGGED(1);

// 引擎生成（每条语句都用 #line 映射回对应的 [@Save]）：
[ModuleInitializer] internal static void Run()
{
    { SaveContainer.Register<Player>("Gold", o => o.Gold, (o, v) => o.Gold = (int)v!); }
    { SaveContainer.Register<Player>("lv", o => o.Level, (o, v) => o.Level = (int)v!); }
}
```

入口上的特性可以通过 `CSMacroStartupAttribute` 配置。普通 .NET 默认用 `ModuleInitializer`，
Unity 以后可以换成 `RuntimeInitializeOnLoadMethod`。

## 片段宏

用 `#region @NAME` … `#endregion` 包住一组成员，交给一个片段宏统一处理。
典型场景：测试时想给 Player 的所有字段加自动 log，包起来就行，测完删掉这两行即恢复原样。

```csharp
public class Enemy
{
    #region @LOG_ALL
    public int Hp = 50;
    public string State = "Idle";
    public readonly int Id = 1;                         // readonly：宏跳过
    public int Shield = @GETSET(DataCenter.PlayerData.Mp); // 已被 @GETSET 展开：宏跳过
    #endregion
}
```

### 两种处理方式（二选一）

**逐成员替换**：`Macro.RegionMembers` 列出片段里的每个成员，包括名字、类型、修饰符、普通特性、初值原文、语义信息、
`ExpandedBy`（是否已被其他宏展开）、`CanReplace`。调用 `member.Replace(code)` 替换，没替换的保持原样。
每个替换结果留在成员原来的那一行，并且逐个按 Member 方法宏的规则检查（同名同类型的字段或属性）。

```csharp
[RegionMacro]
public static void LOG_ALL()
{
    foreach (var m in Macro.RegionMembers)
    {
        if (!m.IsField || !m.CanReplace || m.Declaration?.CanWrite != true) continue;
        m.Replace($"{m.Attributes} {m.Modifiers} {m.Type} {m.Name} {{ get; set {{ ...log...; field = value; }} }} = {m.Initializer};");
    }
}
```

**整段文本替换**：`Macro.RegionText` 是 `#region` 与 `#endregion` 之间的原文，用 `Macro.Emit` 输出替换后的全文。
适合正则替换这类纯文本处理。输出原样使用（不压成一行），所以：

- 输出的行数**不能超过**原文，否则后面的行号会错位（CSM007）；少了会自动补齐。
- 替换后整体检查：字段和属性的名字、类型不变，成员名字集合不变（CSM008 / CSM009）。
- 片段里不能有其他宏，因为整段替换会把它们一起替换掉（CSM012）。

```csharp
[RegionMacro]
public static void PUBLIC_ALL() => Macro.Emit(Regex.Replace(Macro.RegionText, @"\bprivate\b", "public"));
```

### 规则

- **识别**：`#region` 后面的文字以 `@` 开头就是片段宏，可以带常量实参，比如 `#region @NAME("x", 3)`。
  按名字在被引用的程序集里查找带 `[RegionMacro]` 的静态方法。找不到或有歧义时**报错并停止编译**（CSM015）。
  普通的 `#region` 不受影响。
- **范围**：目前只支持类型内部，`#region` 和 `#endregion` 必须直接位于同一个类/结构的成员之间。
  放在方法体里或命名空间层级会报 CSM003。
- **执行顺序**：和方法宏处于同一阶段，都在全部特性宏之后，所以片段宏也能用 `Macro.Fragments` 读取片段。
- **宏不能包含宏**：片段宏不能嵌套（CSM012）；已经含有宏的成员不能被替换。
  这时 `CanReplace` 为 false，硬要替换会报错，并说明它已被哪个宏展开。
- **只能选一种方式**：同时使用逐成员替换和整段文本替换会报 CSM016。
- **IDE 支持**：`#region` 后的文字对 IDE 只是注释，所以片段宏没有补全，也不能跳转到实现。这是它和另外两种宏的区别。

## 语义信息

宏不只能看到文本，还能查询类型和成员的语义信息。这套模型定义在 Abstractions 里（`MacroType` / `MacroMember` /
`MacroParameter` / `MacroAttributeInfo`），由引擎用 Roslyn 实现，**宏库不需要依赖 Roslyn**。

- **IDE 视角**：基于展开前的源码。`public int Gold = @LOGGED(100)` 在这里是一个 int 字段，
  `ExpandedBy == "LOGGED"` 标注了它实际会被哪个宏展开。宏和 IDE 看到的是同一份事实，这与核心原则一致。
- **懒加载**：只有用到的部分才会计算，不用就没有开销。

| 入口 | 得到什么 |
|---|---|
| `Macro.ArgSymbol(i)` | 实参指向的符号：`@GETSET(DataCenter.PlayerData.Hp)` → 字段 Hp |
| `Macro.ArgTypeInfo(i)` | 实参表达式的类型 |
| `Macro.Declaration` | 被声明或被标记的成员（方法宏中 `x = @M()` 的 x；特性宏标记的字段、属性、方法） |
| `Macro.TypeInfo` | 被声明成员的类型 |
| `Macro.ContainingTypeInfo` | 所在类型（特性宏标记在类型上时就是该类型本身） |
| `Macro.FindType(name)` | 按元数据名查找任意类型 |
| `MacroFragment.Declaration` | 片段对应成员的语义信息 |

能查到的信息：

- **类型**：`Name` / `FullName` / `CodeName`（生成代码用的 `global::...` 写法）、`Kind`、`IsValueType`、`IsNullable`、
  `TypeArguments`、`ElementType`、`BaseType`、`Interfaces`、`Members`、`Attributes`、`EnumValues`，
  以及便捷方法 `Is(fullName)`、`Member(name)`、`Fields` / `Properties` / `Methods`、`HasAttribute(name)`。
- **成员**：`Kind`、`Type` / `TypeInfo`、`Accessibility`、`IsStatic`、`IsConst`、`IsReadOnly`、`IsInitOnly`、
  `CanRead` / `CanWrite`、`Parameters`（含默认值和 ref 种类）、`ConstantValue`、`Attributes`（含实参，并标明哪些是特性宏）、
  `ExpandedBy`、源码位置。

示例（见 `samples/Demo.Macros/M.cs`）：

```csharp
// GETSET 在展开前先检查目标：
var symbol = Macro.ArgSymbol(0);
if (!symbol.CanWrite) Macro.Error($"`{path}` is read-only, so {Macro.Name}'s setter can't forward to it");
// → Player.cs(32,20): error CSM100: @GETSET: `h.RO` is read-only, so A's setter can't forward to it

// DUMP 遍历类型的成员，在编译期生成拼接代码，运行时不用反射：
@DUMP(p)   // → "Player { Hp=70, Mp=7, Gold=150, Level=2, Title=Hero }"
```

## 展开规则

1. **先特性宏，再方法宏和片段宏**。所有特性宏按文件、行的顺序执行完，方法宏和片段宏才开始执行，
   所以它们读到的片段一定是完整的。
2. **宏不能包含宏**：方法宏的实参里不能有宏，宏的输出里也不能有宏，否则报 CSM012。
   需要组合时，先展开到一个局部变量：`var sq = @SQUARE(4); @LOG(sq);`。每个宏只展开一次，行为可预测。
3. **一行可以有多个宏**，各自展开：`[@Track, @Save] public int Gold = @LOGGED(100);`。
   宏之间如果产生冲突，属于宏作者没有覆盖到的未定义行为，由宏作者自己把握。
4. **展开过程出错就停止编译并报错**。所有宏都会执行一遍，把错误一次全部报出来，而不是遇到第一个错误就停下。
5. **展开后编译出错也会报错**。展开成功后，引擎会编译检查宏生成的那些行和辅助脚本。
   出错时报 CSM013，并指明是哪个宏生成的代码，比如
   `Player.cs(6,21): error CSM013: code generated by @BAD_GEN doesn't compile: CS0103 ...`。
   业务代码自己的错误不在这里检查，留给编译器照常报。可以用 `CSMacroVerify=false` 关闭这一步。

## 打包与安装

### NuGet 包

| 包 | 谁引用 | 内容 |
|---|---|---|
| `CSMacro` | 写宏的项目、用宏的项目 | `ref/netstandard2.0/CSMacro.Abstractions.dll`：写宏用的 API，**只作编译期引用**，不会成为程序的运行时依赖<br>`build/CSMacro.targets`：NuGet 自动导入，在编译前展开宏<br>`tools/net10.0/`：预编译好的展开工具及其依赖（Roslyn） |
| `CSMacro.Testing` | 宏的测试项目 | 测试工具、引擎、API；依赖 `Microsoft.CodeAnalysis.CSharp` |

生成方式：`dotnet pack pack/CSMacro -c Release` 和 `dotnet pack src/CSMacro.Testing -c Release`，包输出到 `artifacts/packages/`。
版本号在 `Directory.Build.props` 的 `CSMacroVersion` 里。

`CSMacro.targets` 会自己判断运行方式：

- **包模式**：在包里时，`tools/` 下有预编译好的展开工具，直接使用。
- **源码模式**：在这个仓库里时，从源码构建展开工具，所以仓库里的示例可以直接引用源码，不需要先打包。

### 两种写宏的方式

**项目内的宏**：适合起步和小项目。

```xml
<PackageReference Include="CSMacro" Version="0.1.0" PrivateAssets="all" />
<CSMacroDefinition Include="Macros/**/*.cs" />
```

- IDE 里，`Macros/` 下的文件和普通源码一样参与编译，所以宏照样能补全、跳转、类型检查。
- 真正构建时，展开工具先把它们单独编译成一个临时程序集（`obj/.../csmacro/<项目>.CSMacroLocal.dll`），
  从这里加载宏去展开项目，然后把这些文件从正式编译里拿掉。
- 约束：宏文件先于项目编译，只能用 CSMacro 的 API 和项目引用的库，不能用本项目自己的类型（CSM018）。
  反过来，业务代码也不能用宏文件里的普通类型，因为宏文件只存在于编译期（CSM019）。
  CSM019 依赖运行时清理的分析，分析不可靠时（比如项目依赖源码生成器产出的类型）会跳过这项检查。

**单独的宏库**：适合宏多了、要在多个项目间共享。

```xml
<!-- MyMacros.csproj -->
<TargetFramework>netstandard2.0</TargetFramework>
<LangVersion>latest</LangVersion>
<PackageReference Include="CSMacro" Version="0.1.0" PrivateAssets="all" />

<!-- 用宏的项目 -->
<PackageReference Include="CSMacro" Version="0.1.0" PrivateAssets="all" />
<ProjectReference Include="../MyMacros/MyMacros.csproj" />
```

宏库也会被运行时清理识别出来，不会出现在程序的输出里。

两种方式都在一个仓库外的全新项目里，用本地包源从零验证过。

## 运行时清理

宏库只在编译期有用，展开后程序不应该再依赖它。每次构建时引擎会：

1. **去掉导入宏库、且已经用不到的 using**，比如 `using static Demo.Macros.M;`。只清空那一行，行号不变。
   不去掉的话，编译器会为 PDB 的导入信息在程序集里保留对宏库类型的引用。
2. **判断宏程序集是否还被用到**：用 Roslyn 的 `GetUsedAssemblyReferences` 检查宏库和 `CSMacro.Abstractions`，
   并确认没有其他运行时依赖引用它们。
3. **从引用和输出中移除**：MSBuild 把运行时不需要的宏程序集从编译引用和"复制到输出"的列表中拿掉。
   于是编译出的程序集元数据、`deps.json`、输出目录里都不再有它们。

```
csmacro: expanded macros in 3 of 7 file(s), generated CSMacro.Startup.g.cs; not needed at runtime: CSMacro.Abstractions, Demo.Macros
```

几点说明：

- **保守**：展开后的代码如果仍然用到宏库里的普通类型（不是宏，比如运行时调用了 `CSMacro.Macro.Quote`），对应的程序集会照常保留。
  分析用的编译只要有任何错误，就什么都不去掉。
- **分析和真实编译要一致**：项目的 `OutputType`、`Nullable` 会传给引擎；源码按 `DocumentationMode.Parse` 解析，
  否则 Roslyn 无法判断文档注释里的 `cref` 引用了什么，会把所有引用都算作用到。
- **可以关闭**：`-p:CSMacroRuntimeCleanup=false`。测试工具里默认关闭（比较慢），可以用 `MacroTestOptions.RuntimeCleanup = true` 打开，
  结果在 `r.RuntimeUnused`。

## 排查展开后的问题：dump 到文件夹

可以把**一部分脚本**、或者**一部分宏**的展开结果写到一个文件夹里，对照着排查问题。这只是另外写一份给人看，不影响真正参与编译的展开结果。

```bash
# 全部展开结果 → 项目下的 csmacro-dump/
dotnet build samples/Demo.App -p:CSMacroDump=true

# 只看 Player.cs，而且只展开 LOGGED 和 Save（其余宏保持原样）
dotnet build samples/Demo.App -p:CSMacroDumpFiles=Player.cs -p:CSMacroDumpMacros="LOGGED Save"

# 换个目录
dotnet build samples/Demo.App -p:CSMacroDump=true -p:CSMacroDumpDir=/tmp/expanded
```

| 属性 | 含义 |
|---|---|
| `CSMacroDump=true` | 开启 dump。设置了下面两个筛选属性也会自动开启 |
| `CSMacroDumpDir` | 目标文件夹，默认是项目下的 `csmacro-dump`（相对项目目录） |
| `CSMacroDumpFiles` | 只写出这些文件：相对路径或文件名，支持 `*` 和 `**`，多个用空格分隔 |
| `CSMacroDumpMacros` | 只展开这些宏（`LOGGED`、`Save`，带不带 `@` 和 `[]` 都可以），多个用空格分隔 |

文件夹里有这些内容：

- **展开后的源文件**：和原文件**逐行对齐**，第 N 行就是源码第 N 行展开后的样子，可以直接左右对比。
- **`generated/`**：引擎生成的辅助脚本（启动代码）。
- **`_expansions.txt`**：展开报告。列出诊断，以及每一次展开的位置、宏、源码，
  并把**格式化成多行**的输出也列出来（实际展开结果压在一行里，不方便读）：

```
Player.cs:26  @LOGGED  (Member)
  source:
    [@Track, @Save] public int Gold = @LOGGED(100);
  output:
    public int Gold
    {
        get;
        set
        {
            if (global::System.Collections.Generic.EqualityComparer<int>.Default.Equals(field, value))
                return;
            ...
```

几点说明：

- **展开出错时也会写出**，比如 CSM013（宏生成的代码编译不过）。这正是最需要看展开结果的时候。
- **dump 目录永远不会被当作源码编译**：它被加进了 `DefaultItemExcludes`，命令行构建和 IDE 都会排除；
  另外展开器会跳过任何带 `.csmacro-dump` 标记文件的目录。
- **每次 dump 会清空目标文件夹**，但只清空 csmacro 自己创建的文件夹（带标记文件）。
  目标是一个别人的非空文件夹时会拒绝写入，避免误删。
- 建议把 `csmacro-dump/` 加进 `.gitignore`。
- 只展开部分宏时，没选中的特性宏不会执行，依赖它们片段的方法宏会拿到更少的片段。

## 给宏作者用的测试工具

`CSMacro.Testing` 让宏作者在单元测试里直接展开一段源码。它不绑定测试框架，断言失败时抛 `MacroTestException`，
xUnit、NUnit、MSTest 都能识别为测试失败。宏在测试进程内执行，不需要先构建项目。示例见 `tests/Demo.Macros.Tests`。

```csharp
var r = MacroTest.Expand(source, typeof(M).Assembly);

// 1. 测展开结果：展开保持行号不变，所以源码第 6 行就是 r.Line(6)
r.AssertSuccess();
Assert.Equal("public class Player { public int Hp { get => Data.Hp; set => Data.Hp = value; } }", r.Line(6));

// 2. 测诊断：错误码、消息、行号
r.AssertError("CSM100", contains: "read-only", line: 6);

// 3. 测运行行为：编译加载展开结果（启动代码会立即执行），再调用生成的代码
var compiled = r.Compile();
var player = compiled.Create("Demo.App.Player");
var log = MacroTest.CaptureConsole(() => compiled.Set(player, "Gold", 150));
Assert.Equal("[Player.Gold] 100 -> 150" + Environment.NewLine, log);
```

| API | 用途 |
|---|---|
| `MacroTest.Expand(source(s), references / options)` | 展开；`MacroTestOptions` 可设置 `Macros`（只展开部分宏）、`Defines`、`Nullable` 等 |
| `r.Output` / `r.Outputs` / `r.Line(n)` / `r.Startup` | 展开后的文本、某一行、辅助脚本 |
| `r.Diagnostics` / `r.AssertSuccess()` / `r.AssertError(code, contains, line)` / `r.AssertWarning(...)` / `r.AssertNoWarnings()` | 诊断断言 |
| `r.Expansions` | 每一次展开的记录（宏、位置、源码、输出、格式化后的输出） |
| `r.DumpTo(dir, filePatterns)` | 和构建时一样写出 dump 文件夹，测试失败时方便查看 |
| `r.Compile()` → `Create` / `Get` / `Set` / `Call` / `IsProperty` | 编译加载展开结果，并通过反射驱动生成的代码 |
| `MacroTest.CaptureConsole(action)` | 捕获控制台输出（测 LOG、LOGGED 这类打日志的宏；需要关闭测试并行） |

运行示例测试：

```bash
dotnet test tests/Demo.Macros.Tests
```

## 目录

| 路径 | 作用 |
|---|---|
| `src/CSMacro.Abstractions` | 宏作者唯一引用的库：`[Macro]`、`[AttributeMacro]`、`Macro.*` API。netstandard2.0 |
| `src/CSMacro.Engine` | 展开引擎，纯 Roslyn，不关心宿主。netstandard2.0（以后可直接进 Unity Editor） |
| `src/CSMacro.Cli` | 宿主：加载宏程序集、反射执行宏、写出展开文件和辅助脚本、dump。被 MSBuild 调用 |
| `src/CSMacro.Testing` | 给宏作者用的测试工具。netstandard2.0，不绑定测试框架 |
| `build/CSMacro.targets` | MSBuild 接入：截流 `@(Compile)` |
| `samples/Demo.Macros` | 示例宏（`M.cs` 是方法宏，`Attributes.cs` 是特性宏） |
| `samples/Demo.App` | 示例控制台程序 |
| `tests/Demo.Macros.Tests` | 用 CSMacro.Testing 给示例宏写的 xUnit 测试 |
| `examples/DungeonCrawler` | 接入案例：能玩的终端地牢游戏，独立项目，通过 NuGet 包接入，有自己的宏库 |
| `pack/CSMacro` | 打包项目：生成 NuGet 包 `CSMacro` |

构建并运行：

```bash
dotnet build samples/Demo.App && dotnet samples/Demo.App/bin/Debug/net10.0/Demo.App.dll
```

展开结果在 `samples/Demo.App/obj/Debug/net10.0/csmacro/src/`，辅助脚本在其中的 `generated/` 目录下。

## 关键决定

### 1. 截流编译输入，不改源文件
`CSMacro.targets` 在 `CoreCompile` 之前运行：把含宏的文件展开到 `obj/.../csmacro/src/`，
再把 `@(Compile)` 里的原文件换成展开副本，并加入辅助脚本。源文件一个字都不动，因此不需要"复原"，崩溃也不会留下脏文件。
IDE 的 design-time build 会跳过这一步，所以 IDE 看到的永远是原文件。

接入方式见下一节"打包与安装"。展开工具 `CSMacro.Cli` 不需要使用方引用，也不会出现在程序的输出目录里。

### 2. 识别宏靠语义，`@` 是约定
展开器问 Roslyn："这个调用实际调用的方法带不带 `[Macro]`？这个特性类带不带 `[AttributeMacro]`？"，而不是搜索 `@` 文本。
因此 `@"字符串"`、`@class` 永远不会被误伤。漏写 `@` 会给出警告 CSM010，保证读代码的人一眼看出是宏。

### 3. 方法宏的三种位置
| `MacroSite` | 替换范围 | 例子 |
|---|---|---|
| `Expression`（默认） | 调用表达式本身，自动加括号 | `var a = @SQUARE(x);` |
| `Statement` | 整条语句，结果包进 `{ }` | `@LOG(x);` |
| `Member` | 整条字段声明，结果必须是同名同类型的字段或属性 | `public int Hp = @GETSET(...);` |

`Member` 宏可以读取被声明的 `Name` / `Type` / `Modifiers` / `Attributes`，
所以"字段声明"本身就成了给宏传参的语法。`Attributes` 只包含普通特性，特性宏已经被去掉。

### 4. 特性宏的目标
`[AttributeMacro(MacroTarget.Field | MacroTarget.Property)]` 声明可以标记哪些声明（`Field` / `Property` / `Method` / `Type`）。
特性宏可以读取 `Target`、`Name`、`Type`、`Modifiers`、`ContainingType`，以及特性上写的实参（`Macro.ArgValue(0)`）。

### 5. 宏输出先做语法检查
方法宏的输出、特性宏的启动代码在使用前都会先做语法检查。写错了就在宏的调用处报 CSM011，
而不是让编译器在展开副本里报一个莫名其妙的错误。

### 6. 行号：压成一行 + 补换行 + `#line`
- 宏输出被重新分词、去注释、拼成一行，落在调用所在的那一行。
- 宏调用本身跨了 N 行，就在展开结果后补 N 个换行，后面的代码行号不变。
- 展开文件首行写 `#line 1 "原文件路径"`，编译错误、`[CallerLineNumber]`、异常栈、断点都指回原文件。
- 辅助脚本里的每条启动语句前都写一行 `#line`，指回产出它的那个特性宏。

示例里的 `line check` 和 `stack frame` 两行就是在验证这一点。

### 7. 宏必须先于使用它的代码编译
宏要先编译、再执行，才能展开使用它的项目。有两种满足方式：

- 宏写在一个被引用的宏库里。
- 宏写在项目内、用 `CSMacroDefinition` 标出的文件里。展开工具会先单独编译这些文件，见"打包与安装"。

除此之外，和调用方混在同一次编译里的宏会报 CSM004。Unity 里对应的是一个单独的 asmdef。

### 8. 宏运行时永远不会被执行
展开时实参一律传默认值，宏通过 `Macro.Arg(i)` 读源码原文、`Macro.ArgValue(i)` 读常量值。
万一真在运行时被执行（展开步骤没启用），`Macro.*` 会抛出清晰的异常，而不是静默返回错误结果。

## 宏 API 速查（`CSMacro.Macro`）

| 成员 | 方法宏 | 特性宏 | 含义 |
|---|---|---|---|
| `Arg(i)` / `ArgType(i)` / `ArgValue(i)` / `ArgCount` | ✓ | ✓ | 实参原文 / 推断类型 / 常量值 / 个数 |
| `Name` / `Type` / `Modifiers` | ✓ | ✓ | 被声明的名字、类型、修饰符 |
| `Attributes` | ✓ | | 字段上的普通特性（已去掉特性宏） |
| `ContainingType` / `File` / `FileName` / `Line` / `Self` | ✓ | ✓ | 位置信息 |
| `Site` | ✓ | | 实际匹配到的位置 |
| `Target` | | ✓ | 被标记的声明种类 |
| `IsDefined(symbol)` | ✓ | ✓ | 是否定义了某个预处理符号（如 DEBUG） |
| `ArgSymbol(i)` / `ArgTypeInfo(i)` | ✓ | ✓ | 实参的语义信息 |
| `Declaration` / `TypeInfo` / `ContainingTypeInfo` | ✓ | ✓ | 被声明的成员、它的类型、所在类型的语义信息 |
| `FindType(name)` | ✓ | ✓ | 按元数据名查找类型 |
| `Emit(code)` | ✓ | | 追加原地展开的代码（可多次调用） |
| `Fragments(channel)` | ✓ | | 读取特性宏产出的片段 |
| `Fragment(channel, code)` | | ✓ | 向频道产出片段 |
| `Startup(code)` | | ✓ | 产出启动代码，汇总进辅助脚本 |
| `Error(msg)` / `Warning(msg)` | ✓ | ✓ | 报告到调用处的编译诊断 |
| `Quote(text)` | ✓ | ✓ | 转成 C# 字符串字面量 |
| `RegionMembers` / `RegionText` / `IsRegionMacro` | 片段宏 | | 片段里的成员 / 原文；片段宏也可以使用方法宏那一列的 `Emit`、`Fragments`、语义信息 |

调用了不属于当前宏种类的 API 会报 CSM005，并说明应该用哪个 API。

## 诊断码

| 码 | 含义 |
|---|---|
| CSM003 | 宏用在了不允许的位置或声明上 |
| CSM004 | 宏和调用方在同一个项目 |
| CSM005 | 宏抛出了异常（包括调用了不属于当前宏种类的 API） |
| CSM006 | 表达式宏没有输出代码 |
| CSM007 | 输出无法压成一行（含 `#` 指令或跨行字面量） |
| CSM008 | Member 宏改变了名字，或多出/去掉了成员 |
| CSM009 | Member 宏改变了类型 |
| CSM010 | 没有写 `@`（警告） |
| CSM011 | 宏输出的代码语法错误 |
| CSM012 | 宏包含宏（在实参里或输出里） |
| CSM013 | 宏生成的代码编译不过（会指明是哪个宏） |
| CSM014 | 两个宏替换的代码重叠 |
| CSM015 | 片段宏找不到、有歧义、写法无法识别，或缺少 `#endregion` |
| CSM016 | 片段宏同时使用了逐成员替换和整段文本替换 |
| CSM017 | 对被宏变成属性的字段使用了只对字段成立的写法（ref/out、ref 表达式、取地址、修改结构体副本） |
| CSM018 | 项目内的宏文件编译不过（常见原因：用了本项目自己的类型） |
| CSM019 | 业务代码用了项目内宏文件里的普通类型（宏文件只存在于编译期） |
| CSM100/101 | 宏自己报的错误/警告 |

## 已知问题与下一步

1. **"字段 vs 属性"的剩余差别**：编译期就能发现的用法已经由 CSM017 检查。剩下两种情况：
   - 运行时按字段查找的反射或序列化（比如只序列化字段的序列化器）会找不到这个成员。
   - 片段宏的整段文本替换模式暂时不参与 CSM017 检查，逐成员替换模式会参与。
2. **展开用的编译不跑源码生成器**：调用方依赖源码生成器产出的类型时，实参类型可能推断不出来，
   CSM013 检查也可能误报。需要用 `GeneratorDriver` 先跑一遍 `@(Analyzer)`。
3. **语义信息的范围**：目前只覆盖类型和成员的声明信息，不包括方法体内部的控制流、数据流分析。
   成员列表只包含源码里显式声明的成员，不含继承来的成员（可以通过 `BaseType` 自己往上查）。
4. **调试**：展开后的代码在一行里，无法单步进入宏展开的内部。部分调试器可能提示源文件校验和不匹配（因为 PDB 记录的是展开副本的校验和）。
5. **性能**：每次构建都会做一次完整语义分析，外加对宏生成代码的编译检查和运行时清理（又是两遍完整分析）。
   后续可以按"源文件 + 宏程序集哈希"做缓存。
6. **Unity**：按"截流编译器输入"的思路接入，复用 `CSMacro.Engine`；启动脚本的入口特性换成 Unity 的。
