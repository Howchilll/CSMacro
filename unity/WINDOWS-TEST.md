# Windows 测试交接

这份文档用于在 Windows 上复测 Unity 集成。2026-11-16 已在 Windows 上测通，并按确认过的设计做成了 UPM 包
（`com.howmacro.unity`，源码在 [`com.howmacro.unity/`](com.howmacro.unity/)），下面的步骤已改为用这个包。

## 现状

- 已验证（macOS）：编辑器里宏展开、Demo 场景、Mono Player 包、报错行号指回原始源码、启动注册代码先于用户代码执行。
- 已验证（Windows 11，Unity 6000.4.0f1，2026-11-16）：见下方「Windows 测试结果」。
- 未验证：关闭 Domain Reload 的 Play 模式、Windows 上的 Unity 2022.3（ILPP runner 为 .NET 6；6000.4 是 .NET 8）。
- 原理见 [`README.md`](README.md)：Unity 先编译未展开的代码 → ILPostProcessor（`Unity.HowMacro.CodeGen`）
  通过反射调用 Bridge → Bridge 从 PDB 读出源码和编译选项，展开宏，用 Roslyn 重新编译，把新程序集交回 Unity。

## 准备

1. 安装 Unity 2022.3 或更新版本，以及 .NET SDK 8 或更新版本。SDK 只用来编译 Bridge 和 Abstractions，Unity 运行时不需要。
2. 新建一个空的 3D Unity 工程（下面记作 `<工程>`）。
3. 拉代码并打包（PowerShell）：

   ```powershell
   git clone https://github.com/Howchilll/CSMacro.git
   cd CSMacro
   python unity/pack_upm.py
   ```

   输出在 `artifacts/upm/com.howmacro.unity`。
4. 在 `<工程>/Packages/manifest.json` 的 `dependencies` 里加一行（路径换成你的绝对路径，用正斜杠）：
   `"com.howmacro.unity": "file:D:/.../CSMacro/artifacts/upm/com.howmacro.unity"`。
   发布之后改用 git URL `https://github.com/Howchilll/CSMacro.git#upm`。
5. Package Manager → HowMacro for Unity → Samples → 导入 **Demo**（Arena 需要先加 Tag 和 Layer，见它的 README）。

## 测试清单

1. 打开工程，Console 里没有编译错误。
2. 菜单 **HowMacro → Create Demo Scene**，然后 Play，点左边的按钮：
   - 物体能掉下来，右边 Watch 面板的数字跟着变。
   - 点 **Clear**：Console 打印 `removed = N`。
   - 点 **crash**：异常堆栈指向 `Calc.cs` 第 38 行（`throw` 那一行）。
   - 左边应该有 6 个按钮：Clear、crash、hello、Rain x20、Spawn cube、Spawn sphere。
3. `Library/HowMacro/ilpp.log` 里有 `Demo.Game: replaced`。
4. 打一个 Windows Player 包（File → Build Settings，场景已自动加入），运行效果和编辑器里一样。
5. 负面测试：把 `DemoController.cs` 里某个 `[@Command]` 方法从 `internal` 改成 `private`，
   应该得到编译错误 `[@Command] method ... is private`。测完改回来。
6. 可选：菜单外的无界面检查 `Unity.exe -batchmode -projectPath <工程> -executeMethod SpikeRunner.Run -quit -logFile <绝对路径>`，
   日志里 `[spike] editor` 下应为 `Area(4) = 25`、`Where() = "Calc.cs:22"`。`registered commands` 在 `-executeMethod` 下是 0
   （`RuntimeInitializeOnLoadMethod` 只在 Play 模式和 Player 里执行），Player 里用 `-howmacro-checks` 启动应为 6。
   （`SpikeRunner.BuildPlayer` 写死了 macOS 目标，Windows 上不用它。）

## Windows 上最可能出的问题

- **Bridge DLL 被锁（已解决）。** 原来用 `LoadFromAssemblyPath`，Unity 开着时 DLL 被锁（Windows 上已复现）。
  现在改为从字节加载，Unity 开着时也不锁（已验证）。
- **只改 Bridge 不会触发重新展开（已解决）。** 打包时把 Bridge 的哈希写进 `BridgeStamp.cs`，换了 Bridge 后 ILPP 程序集也跟着变，Unity 会重新展开。
  不经打包、直接改 `Bridge~` 时仍需 **HowMacro → Recompile All Scripts**。
- **路径（已验证）。** 盘符、反斜杠、带空格的路径都没问题。
- **ILPP 运行时差异。** ILPP runner 是 Unity 自带的 .NET 6；Bridge 在自己的 AssemblyLoadContext 里加载 Roslyn 5，
  Windows 版 runner 自带的 System.* 版本若与 macOS 不同，可能出现加载错误，看 `ilpp.log`。

## 出问题时收集

- Unity Console 的完整报错（含堆栈）。
- `<工程>/Library/HowMacro/ilpp.log`。
- `Editor.log`（`%LOCALAPPDATA%\Unity\Editor\Editor.log`）里搜 `HowMacro` / `ILPostProcessor` 的部分。
- Player 问题：`%USERPROFILE%\AppData\LocalLow\<公司名>\<产品名>\Player.log`。

## Windows 测试结果（2026-11-16）

环境：Windows 11、Unity 6000.4.0f1（不是 2022.3）、.NET SDK 8（Bridge 和 Abstractions 用它即可编译）、MSVC 14.41。

| 项目 | 编辑器 | Mono dev | Mono release | IL2CPP dev | IL2CPP release |
|---|---|---|---|---|---|
| 宏展开（`replaced`）、`Area(4) = 25`、`@LOG_VALUE` | ✅ | ✅ | ✅ | ✅ | ✅ |
| 启动注册代码（registered = 6） | n/a | ✅ | ✅ | ✅ | ✅ |
| 堆栈行号指回原始源码（`Calc.cs:38`） | ✅ | ✅ | 无行号（正常） | ✅（需开启 IL2CPP Stacktrace = Method, File, Line） | 无行号（正常） |
| `@ASSERT` 在 release 中完全消失 | — | 存在 | ✅ 无 | 存在 | ✅ 无 |

- 负面测试（`[@Command]` 方法改为 `private`）：`error HMC100: ... is private`，定位到正确的行。
- 盘符、反斜杠、带空格的路径均没有问题；Bridge 在 Unity 6 自带的 .NET 8 ILPP runner 中加载正常。
- 新样例 [`Arena/`](Arena/)（成员宏 `@CLAMPED`、`[@Save]` + PlayerPrefs、编译期检查 Tag/Layer）在编辑器、Mono dev、IL2CPP release 中 15 项检查全部通过，6 个负面用例都给出正确的编译错误。
- 发现：宏程序集（如 `Demo.Macros.dll`）和 `HowMacro.Abstractions.dll` 会被打进 Player（Mono 和 IL2CPP，包括 release），
  虽然展开后的游戏程序集已不再引用它们。做 UPM 时用 `IFilterBuildAssemblies` 把它们从打包中排除。

## UPM 包验证（2026-11-16，同一台 Windows 机器）

用 `pack_upm.py` 打出的包，在新工程里分别以 tarball（解压到只读的 `Library/PackageCache`，与 git 安装相同）和本地文件夹
（`file:`）两种方式安装，并导入两个样例：

- Bridge 在 `Library/PackageCache/com.howmacro.unity@…/Bridge~` 和 `file:` 路径下都能找到；没有缺少 `.meta` 的警告。
- 编辑器、Mono dev、IL2CPP release：Arena 15 项检查和 Demo 检查全部通过。
- 打包过滤：`Arena.Macros`、`Demo.Macros`、`HowMacro.Abstractions` 不再出现在 Mono 和 IL2CPP 包里（包括 IL2CPP 生成的 C++）。
- DLL 锁：编辑器开着时，旧的按路径加载会锁住 `Microsoft.CodeAnalysis.dll`，新的按字节加载不会。
- `BridgeStamp` 改变后 Unity 会重新展开，不改则不会。
- 修复：并行展开时 `ilpp.log` 会丢行（写入冲突被静默忽略），现在加锁并重试。
- 没测：真正从 GitHub 用 git URL 安装（需要先推 tag 跑 `upm.yml`）、Unity 2022.3 上的包。
