# Arena sample

A small Unity scene (enemies fall, click them for gold, a stats panel) built around three macro patterns that fit
Unity projects. **Before importing it** (Package Manager → HowMacro for Unity → Samples → Arena), add the tag `Enemy` and
the layers `Ground` (6) and `Enemy` (7) in **Project Settings → Tags and Layers**: the sample checks them at compile time,
so without them it doesn't compile (the error says what's missing). Then use **HowMacro → Create Arena Scene** and press Play.

```csharp
[SerializeField] int hp = 100;                                   // Inspector-edited, serialized state
[@Save("hp")] public int Hp = @CLAMPED(nameof(hp), 0, 100);      // clamped property + change callback + saved

public void SaveTo(string prefix) { @SAVE_ALL(prefix); }         // PlayerPrefs code for every [@Save] member

go.tag = @TAG("Enemy");                                          // checked against TagManager.asset
Physics.Raycast(ray, out var hit, 100f, @LAYER_MASK("Enemy"));   // becomes the constant 128
```

| Macro | Becomes | Compile-time errors |
|---|---|---|
| `@CLAMPED(nameof(field), min, max)` | a property over `field`, clamping assignments, calling `OnStatChanged(name)` if declared | field missing or of another type, min > max, non-numeric |
| `[@Save("key")]` + `@SAVE_ALL(prefix)` / `@LOAD_ALL(prefix)` / `@SAVE_KEYS()` | typed `PlayerPrefs.Set*/Get*` per member | unsupported type, read-only member, duplicate key |
| `@LAYER("name")`, `@LAYER_MASK("a", "b")`, `@TAG("name")` | an int constant / the string | name not defined in the project's tags and layers |

**Why `nameof(field)`:** generated code must be C# 9 (Unity's version), so there is no `field` keyword, and a macro may not
add names, so it can't declare its own backing field. The state lives in a field you declare, which is also what Unity
serializes and shows in the Inspector.

**Limitation:** `@LAYER`/`@TAG` read `ProjectSettings/TagManager.asset` while compiling. Unity doesn't recompile scripts when
only tags or layers change, so after editing them use **HowMacro → Recompile All Scripts**.

Headless checks (results are logged; the editor run exits with 1 on failure):

```
Unity.exe -batchmode -projectPath <proj> -executeMethod ArenaSetup.RunChecks -logFile <log>
Unity.exe -batchmode -projectPath <proj> -executeMethod ArenaSetup.BuildWindows -backend mono|il2cpp [-release] -logFile <log>
Build/arena-<backend>-<dev|release>/Arena.exe -arena-checks -batchmode -nographics -logFile <log>
```
