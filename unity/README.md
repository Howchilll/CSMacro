# HowMacro for Unity (experimental)

Unity 2022.3+ integration through an **ILPostProcessor**. Unity compiles the unexpanded code as usual (it is valid C# by
design); the post processor then expands the macros and recompiles that assembly, and Unity uses the result.
Tested with Unity 2022.3 (macOS, editor + Mono) and Unity 6000.4 (Windows, editor + Mono + IL2CPP); see [`WINDOWS-TEST.md`](WINDOWS-TEST.md).

## Install

In **Window → Package Manager → + → Add package from git URL**:

```
https://github.com/Howchilll/CSMacro.git#upm
```

or pin a release with `#upm/v<version>`, e.g. `#upm/v0.1.3-preview`. Then:

1. Put your macros in their own asmdef (they are ordinary C# using the `HowMacro` API; `HowMacro.Abstractions` is referenced automatically).
2. Reference that asmdef from the assemblies that use the macros, and add `using static YourMacros;`.

Two samples come with the package (Package Manager → HowMacro for Unity → Samples):

| Sample | Shows |
|---|---|
| [Demo](com.howmacro.unity/Samples~/Demo/) | `[@Command]` buttons registered by generated startup code, a `[@Watch]` overlay, `@ASSERT` compiled out of release builds, `@LOG_VALUE`. Menu **HowMacro → Create Demo Scene**. |
| [Arena](com.howmacro.unity/Samples~/Arena/README.md) | `@CLAMPED` properties over serialized fields, PlayerPrefs saving with `[@Save]`, tag and layer names checked at compile time. Needs the tag `Enemy` and the layers `Ground` and `Enemy`. Menu **HowMacro → Create Arena Scene**. |

## Writing macros for Unity

- Generated code must be **C# 9** (Unity's language version). There is no `field` keyword, and a macro may not add names,
  so a member macro that needs state stores it in a field the user declares:
  `[SerializeField] int hp; public int Hp = @CLAMPED(nameof(hp), 0, 100);` (see Arena).
- `Macro.Startup` code runs at `RuntimeInitializeLoadType.SubsystemRegistration`, before any user code.
- Macro assemblies (any assembly declaring a `[Macro]`, `[AttributeMacro]` or `[RegionMacro]`) and `HowMacro.Abstractions`
  are left out of player builds: after expansion nothing references them. Keep runtime code out of macro assemblies.
- If a macro reads something Unity doesn't treat as a script input (like `ProjectSettings/TagManager.asset`), use
  **HowMacro → Recompile All Scripts** after changing it.
- Expansion log: **HowMacro → Open Expansion Log** (`Library/HowMacro/ilpp.log`).

## How it works

| Part | Where | Runs in |
|---|---|---|
| ILPostProcessor shell | [`com.howmacro.unity/Editor/CodeGen/`](com.howmacro.unity/Editor/CodeGen/) | Unity's ILPP runner (.NET 6 in 2022.3, .NET 8 in Unity 6) |
| Bridge: reads sources and options from the PDB, expands, recompiles with Roslyn | [`../src/HowMacro.Unity.Bridge`](../src/HowMacro.Unity.Bridge), shipped in the package's `Bridge~/` | its own AssemblyLoadContext inside the runner |
| Build filter and menu | [`com.howmacro.unity/Editor/`](com.howmacro.unity/Editor/) | the editor |

The bridge is found in the package (embedded, package cache, or a `file:` package) or, for a manual install, in
`Assets/HowMacro/Bridge~`. It is loaded from bytes, so its files stay unlocked while Unity runs. `Editor/CodeGen/BridgeStamp.cs`
holds a hash of the bridge: a new bridge changes the post processor, so Unity expands everything again.

## Developing the package

main holds the package sources without binaries. To assemble the full package (needs the .NET SDK 8+):

```bash
python unity/pack_upm.py
```

This writes `artifacts/upm/com.howmacro.unity`. Use it from a test project with
`"com.howmacro.unity": "file:<path>"` in `Packages/manifest.json`. Pushing a `v*` tag runs [`upm.yml`](../.github/workflows/upm.yml),
which publishes the assembled package to the `upm` branch.
