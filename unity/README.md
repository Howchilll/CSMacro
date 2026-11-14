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
