# HowMacro for Unity (experimental)

Unity 2022.3+ integration through an **ILPostProcessor**. Unity compiles the unexpanded code as usual (it is valid C# by
design); the post processor then expands the macros and recompiles that assembly, and Unity uses the result.
Tested with Unity 2022.3 (macOS, editor + Mono) and Unity 6000.4 (Windows, editor + Mono + IL2CPP); see [`WINDOWS-TEST.md`](WINDOWS-TEST.md).

## Install

In **Window → Package Manager → + → Add package from git URL**:

```
https://github.com/Howchilll/HowMacro.git#upm
```

or pin a release with `#upm/v<version>`, e.g. `#upm/v0.1.6-preview`. Then:

1. Put your macros in their own asmdef (they are ordinary C# using the `HowMacro` API; `HowMacro.Abstractions` is referenced automatically).
2. Reference that asmdef from the assemblies that use the macros, and add `using static YourMacros;`.

Three samples come with the package (Package Manager → HowMacro for Unity → Samples):

| Sample | Shows |
|---|---|
| [ColorLog](com.howmacro.unity/Samples~/ColorLog/README.md) | Everyday macros on one MonoBehaviour: `@COLORLOG` turns a field into a property that logs each change in color, `[@Get]` + `@GET_ALL()` fills components, `[@Save]` + `@SAVE_ALL()`/`@LOAD_ALL()` saves to PlayerPrefs, `[@Watch]` + `@WATCH_GUI()` draws a live debug box. Add `ColorLogDemo` to a Cube. |
| [Demo](com.howmacro.unity/Samples~/Demo/) | `[@Command]` buttons registered by generated startup code, a `[@Watch]` overlay, `@ASSERT` compiled out of release builds, `@LOG_VALUE`. Menu **HowMacro → Create Demo Scene**. |
| [Arena](com.howmacro.unity/Samples~/Arena/README.md) | `@CLAMPED` properties over serialized fields, PlayerPrefs saving with `[@Save]`, tag and layer names checked at compile time. Needs the tag `Enemy` and the layers `Ground` and `Enemy`. Menu **HowMacro → Create Arena Scene**. |

## Writing macros for Unity

- Generated code must be **C# 9** (Unity's language version). There is no `field` keyword, and a macro may not add names,
  so a member macro that needs state stores it in a field the user declares:
  `[SerializeField] int hp; public int Hp = @CLAMPED(nameof(hp), 0, 100);` (see Arena).
- Macros can use Unity's **managed** types and math while they run: `Vector2/3/4`, `Vector2Int/3Int`, `Color`, `Color32`, `Rect`,
  `RectInt`, `BoundsInt`, `Mathf.Clamp/Lerp/Sqrt/SmoothStep`, `Color.HSVToRGB`, and so on, in their signatures and bodies alike
  (`public static Vector2Int[] MAP_PATH(string file)`). Engine calls implemented in native code (`Quaternion.Euler`, `Matrix4x4.TRS`,
  `Mathf.PerlinNoise`, `Random`, `AnimationCurve`, ...) don't exist while macros expand; calling one is reported as
  `error HMC005: ... UnityEngine.Quaternion.Euler is implemented in Unity's native engine code ...`. Emit code that makes the call
  at runtime instead. (The bridge loads a rewritten copy of the engine assemblies, cached in `Library/HowMacro/engine`: native
  methods throw that error, and native-backed finalizers, which would otherwise crash the compiler process, do nothing.)
- `Debug.Log` / `LogWarning` / `LogError` / `LogException` inside a macro body become compile warnings at the macro's call site
  (`warning HMC101: @MY_MACRO: Debug.Log: ...`), for printing while you debug a macro. Use `Macro.Error` to fail the build.
- `Macro.Startup` code runs at `RuntimeInitializeLoadType.SubsystemRegistration`, before any user code.
- **Keep macros local to their assembly.** HowMacro is for convenience inside the code you are writing, not an API
  boundary. A member a macro turns from a field into a property (e.g. `@COLORLOG`) is only a property inside its own
  assembly: other assemblies compile against Unity's reference assembly of the *unexpanded* code, where it is still a
  field, so using it from there fails at runtime with `MissingFieldException`. If another assembly needs the value,
  bridge it in your own code (an ordinary method or property that wraps the member).
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

The `upm` branch is just the assembled package at the repository root, one commit per release, so it can also be made by hand:

```bash
python unity/pack_upm.py --out ../upm-out --version 0.1.6-preview
git worktree add --detach ../upm-branch
cd ../upm-branch
git checkout --orphan upm        # first time only; later: git checkout upm
git rm -rqf .
cp -r ../upm-out/. .
git add -A
git commit -m "com.howmacro.unity 0.1.6-preview"
git tag upm/v0.1.6-preview
git push origin upm upm/v0.1.6-preview
```

Before pushing, a test project can install the local branch through git, exactly like the GitHub URL
(spaces in the path as `%20`): `"com.howmacro.unity": "git+file:///D:/path/to/HowMacro#upm"`.
