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

**核心原则：不展开宏，项目在静态上也必须正确。** 名字和类型只能由源码声明，宏只改实现。
所以 IDE 看到的永远是真相，不需要任何 IDE 插件。

**想看它能带来什么价值，先看 [samples/Showcase](samples/Showcase/README.md)**：一个 RPG 角色模块，
源码里 16 行用到了宏，替你生成了 136 行原本要手写的代码（变更通知、数值约束、强类型存档、调试命令），
并且在编译期就能拦下 8 类常见错误。

## 快速开始

需要 .NET 10 SDK。

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
| `src/CSMacro.Testing` | 给宏作者用的测试工具 |
| `build/CSMacro.targets` | MSBuild 接入 |
| `samples/Showcase` | 价值展示：RPG 角色模块 |
| `samples/Demo.*` | 各项功能的基础示例 |
| `tests/` | 示例宏的单元测试 |

完整设计、规则、API 和诊断码见 [DESIGN.md](DESIGN.md)。

## 状态

原型阶段：普通 .NET（控制台）已可用，Unity 接入在计划中。
