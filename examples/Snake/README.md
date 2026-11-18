# Snake: a HowMacro integration case study

A terminal Snake game. The game logic is deliberately kept simple so the macros get the space. It is a standalone project that, like any external project, **consumes HowMacro through the NuGet package**,
with its macros written directly in the project's own `Macros/` folder; no separate macro library is needed.

```
examples/Snake/
├── Snake.csproj            HowMacro integration: two lines of configuration
├── nuget.config            Package source (HowMacro isn't on nuget.org yet, so it points at locally built packages)
├── Directory.Build.props   HowMacro version
├── Macros/                 This game's own macros (about 250 lines of ordinary C#)
├── Game.cs                 Snake, food, score, keys
├── GameSettings.cs         snake.ini settings
├── Record.cs               Best-score save data
├── Program.cs              Interactive mode + demo bot
└── QuickStart/             Python scripts to build / run the demo / play in one step
```

## Step 1: Get the HowMacro package

HowMacro isn't on nuget.org yet, so first build the package locally from the HowMacro repository root (`DotNet/`):

```bash
dotnet pack pack/HowMacro -c Release
```

The package is written to `artifacts/packages/`. Once HowMacro is published to nuget.org, neither this step nor the next one is needed.

## Step 2: Configure the package source

[nuget.config](nuget.config) adds the local package folder as a package source (the path is relative to `nuget.config` itself):

```xml
<packageSources>
  <add key="howmacro-local" value="../../artifacts/packages" />
  <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
</packageSources>
```

## Step 3: Project configuration

Add two lines to [Snake.csproj](Snake.csproj):

```xml
<ItemGroup>
  <PackageReference Include="HowMacro" Version="0.1.7-preview" PrivateAssets="all" />
  <HowMacroDefinition Include="Macros/**/*.cs" />
</ItemGroup>
```

The first line references the HowMacro package; the second tells it the macros live in the `Macros/` folder. That's all:

- No targets to import; NuGet imports them automatically.
- No IDE plugin to install, no macro library project to create.

## Step 4: Write the macros

A macro is an ordinary C# method or attribute class, written in `Macros/`:

| File | Macros | Purpose |
|---|---|---|
| [Properties.cs](Macros/Properties.cs) | `@OBSERVABLE`, `@CLAMP` | Change-notifying properties, range-clamped properties |
| [Keys.cs](Macros/Keys.cs) | `[@Key]`, `@DISPATCH_KEYS`, `@KEY_HELP` | Key bindings: generate the key-dispatch switch and the key help text |
| [Settings.cs](Macros/Settings.cs) | `[@Setting]`, `@APPLY_SETTINGS`, `@SETTINGS_TEMPLATE` | Settings-file binding: parsing, validation, error messages, default template |
| [Persistence.cs](Macros/Persistence.cs) | `[@Persist]`, `@WRITE_PERSISTED`, `@READ_PERSISTED` | Strongly typed save data + format fingerprint |
| [Diagnostics.cs](Macros/Diagnostics.cs) | `@ASSERT`, `#region @TRACE_STATE` | Debug-only assertions, on-demand state tracing |

For example, the core of the key-binding macro is just a few lines ([Keys.cs](Macros/Keys.cs)):

```csharp
[Macro(MacroSite.Statement)]
public static void DISPATCH_KEYS(ConsoleKey key)
{
    // the same key bound twice → compile-time error
    ...
    Macro.Emit($"switch ({Macro.Arg(0)}) {{");
    foreach (var b in Bindings())   // every method in the containing type marked with [@Key]
        Macro.Emit(string.Concat(b.Codes.Select(c => $"case (global::System.ConsoleKey){c}: ")) + $"{b.Method}(); break;");
    Macro.Emit("}");
}
```

**Constraint on macro files**: they are compiled before the game, so they can only use the HowMacro API and the standard library, not the game's own types (such as `Game` or `Pos`).
The code a macro emits can of course refer to them; that is just a string.

## Step 5: Use the macros in the game

[Game.cs](Game.cs):

```csharp
public int Score = @OBSERVABLE(0);        // score changes → OnPropertyChanged: new-best message, speed-up every 5 points
public int Tick = @CLAMP(30, 500, 120);   // speed has a range: no matter how much it speeds up, never below 30ms

[@Key("up", ConsoleKey.W, ConsoleKey.UpArrow)] private void Up() => Turn(Dir.Up);
[@Key("pause", ConsoleKey.P, ConsoleKey.Spacebar)] private void TogglePause() => Paused = !Paused;
...
public void HandleKey(ConsoleKey key) { @DISPATCH_KEYS(key); }
public static string KeyHelp => @KEY_HELP();   // "[W/UpArrow] up  [P/Spacebar] pause  ..."

@ASSERT(_body.Distinct().Count() == _body.Count, "the snake overlaps itself");
```

[GameSettings.cs](GameSettings.cs):

```csharp
[@Setting("width", "Board width")] public int Width = @CLAMP(10, 60, 30);
[@Setting("wrap_walls", "Leave on one side, come back on the other")] public bool WrapWalls = false;
public void Apply(IEnumerable<KeyValuePair<string, string>> values, Action<string> report) { @APPLY_SETTINGS(values, report); }
```

[Record.cs](Record.cs):

```csharp
[@Persist] public int Best;
[@Persist] public int GamesPlayed;
public void Save(BinaryWriter writer) { @WRITE_PERSISTED(writer); }
```

After `using static Snake.Macros.Properties;`, the IDE completes `@CLAMP`, F12 jumps to the macro's implementation, and wrong arguments are underlined right away.

## Step 6: Build and run

From `examples/Snake/`:

```bash
dotnet build
```

```bash
dotnet run -- --demo
```

```bash
dotnet run
```

- `--demo`: a greedy bot plays one game with a fixed seed, so the result is the same every time. The bot's key presses also go through the macro-generated key dispatch.
- No arguments: interactive mode, you play. Needs a real terminal (macOS Terminal, Windows Terminal and the VS Code integrated terminal all work).
  `wasd` / arrow keys to turn, `P` / Space to pause, `Esc` / `Q` to quit.
- `dotnet run -- --write-settings`: regenerates a commented `snake.ini`.

You can also use the scripts in [QuickStart/](QuickStart/), runnable from any directory: `python3 QuickStart/build.py`, `python3 QuickStart/demo.py` (accepts arguments such as `--seed 5`), `python3 QuickStart/play.py`.

Demo output (excerpt, empty board rows omitted):

```
Snake demo. Keys: [W/UpArrow] up  [S/DownArrow] down  [A/LeftArrow] left  [D/RightArrow] right  [P/Spacebar] pause  [Escape/Q] quit
...
Score 21   Best 21   Speed 12/s
Game over: the snake bit itself. Score 31 (new best!)
+------------------------------+
|              o               |
|              o               |
|       oooooo o               |
|       o@oooo o               |
|       oooooo o        *      |
|            oooo              |
+------------------------------+
Games played 1, food eaten 31, best 31.
```

Run it again (with another seed, `--seed 5`) and the best score is read back from the save file:

```
New best! (old best 31)
Game over: the snake bit itself. Score 34 (new best!)
Games played 2, food eaten 65, best 34.
```

## Step 7: Confirm the integration works

This line in the build output means the macros were expanded:

```
howmacro: expanded macros in 3 of 7 file(s); not needed at runtime: HowMacro.Abstractions, Snake.HowMacroLocal
```

It tells you two things:

- 3 files contained macros and were expanded.
- Neither the temporary assembly compiled from `Macros/` nor the HowMacro API is needed at runtime, so both were removed from the output.

The output folder contains only the game's own files, with no `HowMacro.*.dll` (after you play there is also a `snake.sav` save file):

```
Snake  Snake.deps.json  Snake.dll  Snake.pdb  Snake.runtimeconfig.json  snake.ini
```

## What the macros wrote for you

Numbers come from the expansion report produced by `dotnet build -p:HowMacroDump=true`, counted in formatted lines:

| Macro | Uses | Generated code (lines) |
|---|---|---|
| `@APPLY_SETTINGS` + `@SETTINGS_TEMPLATE` | 2 | 64 |
| `@CLAMP` | 4 | 45 |
| `[@Key]` + `@DISPATCH_KEYS` + `@KEY_HELP` | 6 + 2 | 30 |
| `@OBSERVABLE` | 1 | 11 |
| `[@Persist]` + `@WRITE_PERSISTED` / `@READ_PERSISTED` | 3 + 2 | 13 |
| `@ASSERT` | 2 | 8 |
| **Total** | **24 lines in the game** | **171 lines** |

The game itself is 250 lines (including interactive mode and the demo bot); without macros it would be around 400.

More importantly, a change in requirements touches only one place:

- **Add a key**: write one method with one `[@Key]`. The dispatch code and key help update themselves, and key conflicts are reported at compile time.
- **Add a setting**: write one `[@Setting]` line. Parsing, validation, error messages and the default template follow automatically.
- **Add a saved field**: add one `[@Persist]`. The read/write order and format fingerprint follow automatically, and reading an old save fails clearly instead of reading data out of order.

## Mistakes are caught at compile time

Real output from deliberate mistakes in this project, each reported on the offending line:

```
error HMC100: @DISPATCH_KEYS: key W is bound more than once: Jump and Fire
error HMC100: [@Key]: [@Key] method Move must have no parameters, it is called when the key is pressed
error HMC100: @CLAMP: initial value 20 of Speed is outside [1, 10]
error HMC100: [@Setting]: Colors has type System.Collections.Generic.List<string>, which [@Setting] can't parse; supported: int, long, float, double, bool, string, enums
error HMC020: CS0236: A field initializer cannot reference the non-static field, method, or property 'BadSettings.Size' -- this code is only valid after @CLAMP is expanded, so the IDE shows it as an error. ...
```

Mistakes in a macro file itself work the same way: for example, a variable-name clash inside a macro reports `HMC018` pointing at the offending line in `Macros/`.

Settings mistakes at runtime get clear messages too (`snake.ini` with an out-of-range value, a malformed value and an unknown key):

```
[snake] width = 200 is out of range, using 60
[snake] height: can't read 'tall' as int
[snake] unknown setting 'speed'; known: width, height, tick_ms, wrap_walls
```

## Same source, three builds

```bash
dotnet build                          # Debug: @ASSERT checks, no state tracing
dotnet build -c Release               # Release: @ASSERT expands to an empty statement, zero cost
dotnet build -p:TraceState=true       # Debugging: every change to Heading and Food is printed
```

Running the demo after a `-p:TraceState=true` build:

```
[trace] Food: Pos { X = 0, Y = 0 } -> Pos { X = 21, Y = 3 }
[trace] Heading: Right -> Up
[trace] Heading: Up -> Right
```

You don't change a single character of source, and you don't have to remember to remove the logging afterwards.

## Inspect the expansion

```bash
dotnet build -p:HowMacroDump=true                                      # everything
dotnet build -p:HowMacroDumpFiles=Game.cs -p:HowMacroDumpMacros="Key DISPATCH_KEYS"
```

The output goes to `howmacro-dump/`: the expanded files are line-aligned with the sources, and `_expansions.txt` is the expansion report with each expansion formatted over multiple lines.

## FAQ

- **I rebuilt the HowMacro package with the same version and nothing changed**: NuGet caches each package version. Change `HowMacroVersion` in `Directory.Build.props`,
  or delete `~/.nuget/packages/howmacro/` and build again.
- **The build can't find the expansion tool / wrong runtime version**: the expansion tool runs on .NET 10, so the build machine needs the .NET 10 SDK or later.
- **Interactive mode doesn't respond**: it needs a real terminal. When input is redirected (for example in CI) it runs the demo instead.
