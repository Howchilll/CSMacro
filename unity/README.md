# HowMacro for Unity (experimental)

Unity 2022.3+ integration through an **ILPostProcessor**. Unity compiles the unexpanded code as usual (it is valid C# by
design); the post processor then expands the macros and recompiles that assembly, and Unity uses the result.

| Part | Where | Runs in |
|---|---|---|
| ILPostProcessor shell | [`HowMacro.Unity.CodeGen/`](HowMacro.Unity.CodeGen/) — goes into the Unity project | Unity's ILPP runner (.NET 6) |
| Bridge: reads sources and options from the PDB, expands, recompiles | [`../src/HowMacro.Unity.Bridge`](../src/HowMacro.Unity.Bridge) | its own AssemblyLoadContext inside the runner |

Setting up a Unity project by hand (until there is a UPM package):

1. `dotnet publish src/HowMacro.Unity.Bridge -c Release -o <UnityProject>/Assets/HowMacro/Bridge~`
   (the `~` keeps Unity from importing it)
2. Copy `HowMacro.Unity.CodeGen/` to `<UnityProject>/Assets/HowMacro/Editor/CodeGen/`
   (the assembly must be named `Unity.*.CodeGen`, or Unity won't give it the ILPP API)
3. Copy `HowMacro.Abstractions.dll` (netstandard2.0) to `<UnityProject>/Assets/HowMacro/Plugins/`
4. Put macros in their own asmdef; reference it from the assemblies that use the macros.

Notes:
- Generated code must be C# 9 (Unity's language version).
- `Macro.Startup` code runs at `RuntimeInitializeLoadType.SubsystemRegistration`, before any user code.
- Expansion log: `<UnityProject>/Library/HowMacro/ilpp.log`.

## Demo

[`Demo/`](Demo/) is a small scene that shows the macros in a Unity project:

- `[@Command("Spawn cube")]` on a static method → a button in the debug console (registered by generated startup code)
- `[@Watch]` on a field or property → a live row in the watch overlay, listed by `@WATCH_PANEL()`
- `@ASSERT(...)` → a check in the editor and development builds, nothing in release builds
- `@LOG_VALUE(x)` → logs `x = <value>` with the expression's source text

To run it, set up HowMacro as above, copy `Demo/` to `<UnityProject>/Assets/Demo/`, then use the menu
**HowMacro → Create Demo Scene** and press Play. The macros are in [`Demo/Macros/DemoMacros.cs`](Demo/Macros/DemoMacros.cs),
the scene's code in [`Demo/Game/DemoController.cs`](Demo/Game/DemoController.cs).
`Demo/Editor/SpikeRunner.cs` holds headless checks (`-executeMethod SpikeRunner.Run` / `SpikeRunner.BuildPlayer`).
