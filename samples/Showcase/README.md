# Showcase：一个 RPG 角色模块

这个示例用一个很常见的游戏模块，展示 CSMacro 能解决的问题：**设计模式消除不了的样板代码**。
这类代码每个项目都要写，写起来几乎一样，而且很容易漏掉或写错。

```bash
dotnet build samples/Showcase/Showcase.App && dotnet samples/Showcase/Showcase.App/bin/Debug/net10.0/Showcase.App.dll
```

## 你写的代码

`Hero.cs`：一个要绑定 UI、要约束数值范围、要存档的数据类。

```csharp
public class Hero : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new(propertyName));

    [@Persist] public string Name = @OBSERVABLE("Nameless");
    [@Persist] public HeroClass Class = @OBSERVABLE(HeroClass.Warrior);
    [@Persist] public int Level = @CLAMP(1, 99, 1);
    [@Persist] public int Hp = @CLAMP(0, 100, 100);
    [@Persist] public int Gold = @CLAMP(0, 999_999, 0);
    public int Combo = @CLAMP(0, 10, 0); // 只是运行时状态，不存档

    public void Save(BinaryWriter writer) { @WRITE_PERSISTED(writer); }
    public void Load(BinaryReader reader) { @READ_PERSISTED(reader); }
}
```

`Cheats.cs`：调试命令。写一个静态方法，标上 `[@Command]` 就完成了。

```csharp
[@Command("heal", "Restore HP (clamped to 100)")]
public static void Heal(int amount) => Hero.Hp += amount;

[@Command("class", "Change class")]
public static void SetClass(HeroClass value) => Hero.Class = value;

[@Command("level", "Set level; defaults to max")]
public static void SetLevel(int value = 99) => Hero.Level = value;
```

每个成员、每个命令都只占一行：名字、类型、初值、范围、要不要存档，一眼就能看完。
按核心原则，这些名字和类型都由源码声明，IDE 的补全、跳转、重命名都照常可用。

## 宏替你写的代码

数据来自 `-p:CSMacroDump=true` 生成的展开报告，按格式化后的行数统计：

| 宏 | 用了几处 | 生成的代码（行） | 做了什么 |
|---|---|---|---|
| `@CLAMP` | 4 | 48 | 后备字段、夹值、相等判断；所在类支持通知时顺带发通知 |
| `@OBSERVABLE` | 2 | 22 | 后备字段、相等判断、变更通知 |
| `[@Persist]` + `@WRITE_PERSISTED` / `@READ_PERSISTED` | 5 + 2 | 17 | 强类型的二进制读写、存档格式指纹 |
| `[@Command]` + `@COMMAND_HELP` | 7 + 1 | 49 | 参数解析、可选参数、用法提示、启动时注册、帮助表 |
| **合计** | **16 行源码** | **136 行** | |

也就是说，**源码里 16 行用到了宏，替你生成了 136 行原本要手写的代码**。

更重要的是维护成本。手写时，给 Hero 加一个存档字段要同时改四个地方：声明、Save、Load、版本号。
漏掉任何一处都不会编译报错，只会在运行时读出错位的数据。用了宏，只需要在声明上加一个 `[@Persist]`。

生成的代码大致是这样（摘自报告）：

```csharp
// @CLAMP(1, 99, 1)
public int Level
{
    get;
    set
    {
        var __clamped = value < (1) ? (1) : value > (99) ? (99) : value;
        if (field == __clamped)
            return;
        field = __clamped;
        OnPropertyChanged(nameof(Level));   // Hero 有 OnPropertyChanged，所以自动加上
    }
} = 1;

// @READ_PERSISTED(reader)
{
    if (reader.ReadInt32() != 1610718114)   // 编译期根据存档字段算出的格式指纹
        throw new InvalidDataException("save format changed; this build expects: Name:string, Class:Showcase.App.HeroClass, Level:int, Hp:int, Gold:int");
    this.Name = reader.ReadString();
    this.Class = (global::Showcase.App.HeroClass)reader.ReadInt32();
    this.Level = reader.ReadInt32();
    ...
}

// [@Command("level", ...)]：生成到启动辅助脚本里
DebugConsole.Register("level", "level [value:int=99]", "Set level; defaults to max", a =>
{
    if (a.Length < 0 || a.Length > 1) throw new ArgumentException();
    Cheats.SetLevel(a.Length > 0 ? int.Parse(a[0], CultureInfo.InvariantCulture) : 99);
    return null;
});
```

## 写错了会怎样：编译期就报错

宏可以借助语义信息检查用法。下面是故意写错时的真实输出，每条都报在写错的那一行上：

```
error CSM100: @CLAMP: initial value 150 of Mana is outside [0, 100]
error CSM100: @CLAMP: @CLAMP needs a numeric member, but Title is string
error CSM100: @OBSERVABLE: Bad must declare `void OnPropertyChanged(string propertyName)` for @OBSERVABLE to call
error CSM100: [@Persist]: Items has type System.Collections.Generic.List<int>, which [@Persist] can't store; supported: int, long, float, double, bool, string, enums
error CSM100: [@Persist]: Seed is read-only, so [@Persist] couldn't load it back
error CSM100: [@Command]: [@Command] method Dance must be static: commands are registered at startup, before any instance exists
error CSM100: [@Command]: parameter 'h' of Spawn has type Showcase.App.Hero, which [@Command] can't parse; supported: int, long, float, double, bool, string, enums
error CSM100: @COMMAND_HELP: command 'heal' is registered more than once: Showcase.App.MoreCheats.FullHeal (Bad.cs:5) and Showcase.App.Cheats.Heal (Cheats.cs:13)
```

这些问题如果用手写代码或反射实现，大多要到运行时才会暴露，有的甚至根本不会暴露（比如两个同名命令互相覆盖）。

## 为什么不用别的办法

| 办法 | 问题 |
|---|---|
| 继承 / 组合等设计模式 | 能共享逻辑，但每个属性的后备字段、setter、存档读写那几行还是要逐个写 |
| 运行时反射（自动存档、自动注册命令） | 有运行时开销；IL2CPP / AOT 裁剪会把"没被直接引用"的方法删掉，命令就丢了；错误只能在运行时发现 |
| 源码生成器（Source Generator） | 只能新增代码，不能改写已有代码，做不到"把字段变成带逻辑的属性"；要求到处加 `partial`；写起来门槛高 |
| CSMacro | 生成的是普通的强类型代码，没有反射；IDE 看到的名字和类型永远正确；宏本身就是普通 C# 方法，门槛低 |

另外，这些宏是为这个项目定制的（比如 `[@Command]` 注册到本项目的 `DebugConsole`），这正是 CSMacro 的用法：
**规则由项目自己定义**，而不是使用一套通用的框架约定。

## 自己动手看展开结果

```bash
# 全部展开结果
dotnet build samples/Showcase/Showcase.App -p:CSMacroDump=true

# 只看 Hero.cs 里存档相关的宏
dotnet build samples/Showcase/Showcase.App -p:CSMacroDumpFiles=Hero.cs -p:CSMacroDumpMacros="Persist WRITE_PERSISTED READ_PERSISTED"
```

结果在 `samples/Showcase/Showcase.App/csmacro-dump/`，展开报告是其中的 `_expansions.txt`。

宏的实现在 `Showcase.Macros/`：`Properties.cs`（OBSERVABLE / CLAMP）、`Persistence.cs`（存档）、`Commands.cs`（调试命令）。
