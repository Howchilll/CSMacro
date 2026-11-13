# CSMacro

给 C# 用的编译期宏。宏就是普通的 C# 代码：IDE 把它当作普通的方法调用或特性，可以补全、做类型检查、跳转到实现；
编译前，宏的方法体会被执行并展开成代码，运行时只剩展开后的结果。

```csharp
public class Player
{
    // IDE 看到的是字段；编译器拿到的是转发属性
    public int Hp = @GETSET(DataCenter.PlayerData.Hp);

    // 统一规则：值改变时自动打 log；[@Save] 让字段自动进入存档容器
    [@Save] public int Gold = @LOGGED(100);
    [@Save("lv")] public int Level = @LOGGED(1);
}
```

还可以用 `#region @LOG_ALL` … `#endregion` 包住一组成员统一处理，比如测试时给所有字段加自动 log。

**核心原则：不展开宏，项目在静态上也必须正确。** 名字和类型只能由源码声明，宏只改实现。
所以 IDE 看到的永远是真相，不需要任何 IDE 插件。

**想看它放进真实项目是什么样子，看 [examples/DungeonCrawler](examples/DungeonCrawler/README.md)**：
一个真的能玩的终端地牢游戏，作为独立项目通过 NuGet 包接入 CSMacro。它的说明文档从导入、配置一直写到运行，可以当作接入教程。
游戏源码里 42 行用到宏，生成了 375 行代码：数值约束、变更通知、存档、作弊控制台、配置文件绑定、
Debug 才有的断言、按需打开的 AI 跟踪。运行时程序里没有任何宏的痕迹。

更小的例子见 [samples/Showcase](samples/Showcase/README.md)：一个 RPG 角色模块。

## 安装到你的项目

CSMacro 以 NuGet 包的形式使用。**引用包、加一行配置，就可以开始写自己的宏。**

### 1. 引用包，指定宏写在哪

```xml
<ItemGroup>
  <PackageReference Include="CSMacro" Version="0.1.0" PrivateAssets="all" />
  <CSMacroDefinition Include="Macros/**/*.cs" />
</ItemGroup>
```

### 2. 在 `Macros/` 里写宏

```csharp
using CSMacro;

namespace MyApp.Macros;

public static class MyMacros
{
    [Macro(MacroSite.Statement)]
    public static void SHOW(object value) =>
        Macro.Emit($"System.Console.WriteLine({Macro.Quote(Macro.Arg(0) + " = ")} + ({Macro.Arg(0)}));");
}
```

### 3. 在代码里用

```csharp
using static MyApp.Macros.MyMacros;

var x = 21;
@SHOW(x * 2);   // 输出：x * 2 = 42
```

就这些：

- 不用改源码结构，不用 IDE 插件。
- IDE 照常能补全宏、跳转到宏的实现。
- 编译出的程序里没有任何 CSMacro 的痕迹：输出目录、`deps.json`、程序集引用里都没有。

**宏多了、要在多个项目间共享时**，把它们挪到一个单独的宏库项目（netstandard2.0，同样引用 `CSMacro` 包），
其他项目引用这个宏库并引用 `CSMacro` 包即可，不需要 `CSMacroDefinition`。

**给宏写单元测试**：测试项目引用 `CSMacro.Testing` 包，用法见 [DESIGN.md](DESIGN.md#给宏作者用的测试工具)。

**要求**：构建机器上装有 .NET 10 及以上的 SDK（展开工具运行在 .NET 10 上）；项目是 SDK 风格的 MSBuild 项目。
被展开的项目本身的目标框架不限。

**宏文件的约束**：`Macros/` 里的文件先于项目编译，所以只能用 CSMacro 的 API 和项目引用的库，不能用本项目自己的类型。
违反时会报 CSM018；业务代码反过来用到宏文件里的普通类型时会报 CSM019。

### 从源码生成包

还没有发布到 nuget.org 时，可以在本地生成：

```bash
dotnet pack pack/CSMacro -c Release
```

```bash
dotnet pack src/CSMacro.Testing -c Release
```

包会生成在 `artifacts/packages/`。在你的项目旁边放一个 `nuget.config`，把这个目录加为包源：

```xml
<configuration>
  <packageSources>
    <add key="csmacro-local" value="/path/to/CSMacro/DotNet/artifacts/packages" />
  </packageSources>
</configuration>
```

## 在这个仓库里构建

需要 .NET 10 SDK。`samples/` 和 `tests/` 直接引用源码（`build/CSMacro.targets` 会从源码构建展开工具），不需要先打包。
`examples/DungeonCrawler` 是独立项目，有自己的解决方案，通过 NuGet 包接入，需要先运行 `dotnet pack pack/CSMacro -c Release`。

```bash
# 构建并运行示例
dotnet build samples/Demo.App && dotnet samples/Demo.App/bin/Debug/net10.0/Demo.App.dll

# 运行宏的单元测试
dotnet test tests/Demo.Macros.Tests

# 把展开结果写到 csmacro-dump/ 排查问题（可以只看部分文件、部分宏）
dotnet build samples/Demo.App -p:CSMacroDumpFiles=Player.cs -p:CSMacroDumpMacros="LOGGED Save"
```

## 目录

| 路径 | 作用 |
|---|---|
| `src/CSMacro.Abstractions` | 宏作者引用的 API：`[Macro]`、`[AttributeMacro]`、`Macro.*`、语义信息模型 |
| `src/CSMacro.Engine` | 展开引擎（Roslyn） |
| `src/CSMacro.Cli` | 构建时被 MSBuild 调用的展开工具 |
| `src/CSMacro.Testing` | 给宏作者用的测试工具（NuGet 包 `CSMacro.Testing`） |
| `pack/CSMacro` | 打包项目：生成 NuGet 包 `CSMacro` |
| `build/CSMacro.targets` | MSBuild 接入 |
| `examples/DungeonCrawler` | 接入案例：一个能玩的终端地牢游戏，独立项目，通过 NuGet 包接入（不在主解决方案里） |
| `samples/Showcase` | 价值展示：RPG 角色模块 |
| `samples/Demo.*` | 各项功能的基础示例 |
| `tests/` | 示例宏的单元测试 |

完整设计、规则、API 和诊断码见 [DESIGN.md](DESIGN.md)。

## 状态

原型阶段：普通 .NET（控制台）已可用，Unity 接入在计划中。
