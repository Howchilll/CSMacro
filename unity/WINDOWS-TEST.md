# Windows 测试交接

Unity 集成目前只在 macOS 上验证过（Unity 2022.3.56，编辑器 + Mono Player）。这份文档用于在 Windows 上复测。
测通之后下一步是做成 UPM 包（`com.howmacro.unity`），开始前要先和项目作者确认设计。

## 现状

- 已验证（macOS）：编辑器里宏展开、Demo 场景、Mono Player 包、报错行号指回原始源码、启动注册代码先于用户代码执行。
- 未验证：**Windows**、IL2CPP、关闭 Domain Reload 的 Play 模式、Release 包里 `@ASSERT` 是否真的不产生代码。
- 原理见 [`README.md`](README.md)：Unity 先编译未展开的代码 → ILPostProcessor（`Unity.HowMacro.CodeGen`）
  通过反射调用 Bridge → Bridge 从 PDB 读出源码和编译选项，展开宏，用 Roslyn 重新编译，把新程序集交回 Unity。

## 准备

1. 安装 Unity 2022.3（最好同为 2022.3.56）和 .NET 10 SDK。SDK 只用来编译 Bridge 和 Abstractions，Unity 运行时不需要。
2. 新建一个空的 3D Unity 工程（下面记作 `<工程>`），先关掉 Unity。
3. 拉代码并编译（PowerShell）：

   ```powershell
   git clone https://github.com/Howchilll/CSMacro.git
   cd CSMacro
   dotnet publish src/HowMacro.Unity.Bridge -c Release -o <工程>/Assets/HowMacro/Bridge~
   dotnet build src/HowMacro.Abstractions -c Release
   ```

4. 拷文件进 Unity 工程：

   | 从 | 到 |
   |---|---|
   | `unity/HowMacro.Unity.CodeGen/`（整个文件夹的内容） | `<工程>/Assets/HowMacro/Editor/CodeGen/` |
   | `src/HowMacro.Abstractions/bin/Release/netstandard2.0/HowMacro.Abstractions.dll` | `<工程>/Assets/HowMacro/Plugins/` |
   | `unity/Demo/` | `<工程>/Assets/Demo/` |

## 测试清单

1. 打开工程，Console 里没有编译错误。
2. 菜单 **HowMacro → Create Demo Scene**，然后 Play，点左边的按钮：
   - 物体能掉下来，右边 Watch 面板的数字跟着变。
   - 点 **Clear**：Console 打印 `removed = N`。
   - 点 **crash**：异常堆栈指向 `Calc.cs` 第 37 行（`throw` 那一行）。
   - 左边应该有 6 个按钮：Clear、crash、hello、Rain x20、Spawn cube、Spawn sphere。
3. `Library/HowMacro/ilpp.log` 里有 `Demo.Game: replaced`。
4. 打一个 Windows Player 包（File → Build Settings，场景已自动加入），运行效果和编辑器里一样。
5. 负面测试：把 `DemoController.cs` 里某个 `[@Command]` 方法从 `internal` 改成 `private`，
   应该得到编译错误 `[@Command] method ... is private`。测完改回来。
6. 可选：菜单外的无界面检查 `Unity.exe -batchmode -projectPath <工程> -executeMethod SpikeRunner.Run -quit -logFile <绝对路径>`，
   日志里 `[spike] editor` 下应为 `Area(4) = 25`、`Where() = "Calc.cs:21"`、`registered commands = 2`。
   （`SpikeRunner.BuildPlayer` 写死了 macOS 目标，Windows 上不用它。）

## Windows 上最可能出的问题

- **Bridge DLL 被锁。** ILPP 进程用 `LoadFromAssemblyPath` 加载 `Bridge~` 里的 DLL，Unity 开着时无法覆盖，
  重新 publish 前要先关 Unity。已知问题，做 UPM 时处理（改为从字节加载或影子拷贝）。
- **只改 Bridge 不会触发重新展开。** Bee 按内容缓存 ILPP 结果。换了 Bridge 后要改一下 Demo 里的源码（或删 `Library/Bee`）才会重跑。
- **路径。** PDB 里的文档路径是绝对路径，代码用了 `Path.Combine`，但还没在盘符 / 反斜杠路径下跑过。
- **ILPP 运行时差异。** ILPP runner 是 Unity 自带的 .NET 6；Bridge 在自己的 AssemblyLoadContext 里加载 Roslyn 5，
  Windows 版 runner 自带的 System.* 版本若与 macOS 不同，可能出现加载错误，看 `ilpp.log`。

## 出问题时收集

- Unity Console 的完整报错（含堆栈）。
- `<工程>/Library/HowMacro/ilpp.log`。
- `Editor.log`（`%LOCALAPPDATA%\Unity\Editor\Editor.log`）里搜 `HowMacro` / `ILPostProcessor` 的部分。
- Player 问题：`%USERPROFILE%\AppData\LocalLow\<公司名>\<产品名>\Player.log`。
