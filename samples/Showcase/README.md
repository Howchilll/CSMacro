# Showcase: an RPG character module

This sample uses a very common game module to show the problem HowMacro solves: **boilerplate that design patterns can't remove**.
Every project writes this kind of code, it looks almost the same every time, and it is easy to forget a piece or get it wrong.

```bash
dotnet build samples/Showcase/Showcase.App && dotnet samples/Showcase/Showcase.App/bin/Debug/net10.0/Showcase.App.dll
```

## The code you write

`Hero.cs`: a data class that binds to UI, clamps its values and gets saved.

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
    public int Combo = @CLAMP(0, 10, 0); // runtime state only, not saved

    public void Save(BinaryWriter writer) { @WRITE_PERSISTED(writer); }
    public void Load(BinaryReader reader) { @READ_PERSISTED(reader); }
}
```

`Cheats.cs`: debug commands. Write a static method, mark it with `[@Command]`, done.

```csharp
[@Command("heal", "Restore HP (clamped to 100)")]
public static void Heal(int amount) => Hero.Hp += amount;

[@Command("class", "Change class")]
public static void SetClass(HeroClass value) => Hero.Class = value;

[@Command("level", "Set level; defaults to max")]
public static void SetLevel(int value = 99) => Hero.Level = value;
```

Every member and every command takes one line: name, type, initial value, range and whether it is saved, all visible at a glance.
Following the core principle, these names and types are declared in source, so IDE completion, navigation and rename all keep working.

## The code macros write for you

Numbers come from the expansion report produced by `-p:HowMacroDump=true`, counted in formatted lines:

| Macro | Uses | Generated code (lines) | What it does |
|---|---|---|---|
| `@CLAMP` | 4 | 48 | Backing field, clamping, equality check; also notifies when the type supports change notification |
| `@OBSERVABLE` | 2 | 22 | Backing field, equality check, change notification |
| `[@Persist]` + `@WRITE_PERSISTED` / `@READ_PERSISTED` | 5 + 2 | 17 | Strongly typed binary read/write, save-format fingerprint |
| `[@Command]` + `@COMMAND_HELP` | 7 + 1 | 49 | Argument parsing, optional arguments, usage messages, registration at startup, help table |
| **Total** | **16 source lines** | **136 lines** | |

In other words, **16 lines of source use macros, and they generate 136 lines you would otherwise write by hand**.

Maintenance cost matters even more. By hand, adding a saved field to Hero means touching four places at once: the declaration, Save, Load and the version number.
Missing any of them is not a compile error; you only find out at runtime when the data is read out of order. With macros, you add one `[@Persist]` to the declaration.

The generated code looks roughly like this (excerpts from the report):

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
        OnPropertyChanged(nameof(Level));   // added automatically because Hero has OnPropertyChanged
    }
} = 1;

// @READ_PERSISTED(reader)
{
    if (reader.ReadInt32() != 1610718114)   // save-format fingerprint computed at compile time from the saved fields
        throw new InvalidDataException("save format changed; this build expects: Name:string, Class:Showcase.App.HeroClass, Level:int, Hp:int, Gold:int");
    this.Name = reader.ReadString();
    this.Class = (global::Showcase.App.HeroClass)reader.ReadInt32();
    this.Level = reader.ReadInt32();
    ...
}

// [@Command("level", ...)]: generated into the startup script
DebugConsole.Register("level", "level [value:int=99]", "Set level; defaults to max", a =>
{
    if (a.Length < 0 || a.Length > 1) throw new ArgumentException();
    Cheats.SetLevel(a.Length > 0 ? int.Parse(a[0], CultureInfo.InvariantCulture) : 99);
    return null;
});
```

## Mistakes are caught at compile time

Macros can use semantic information to check how they are used. Here is the real output from deliberate mistakes, each reported on the offending line:

```
error HMC100: @CLAMP: initial value 150 of Mana is outside [0, 100]
error HMC100: @CLAMP: @CLAMP needs a numeric member, but Title is string
error HMC100: @OBSERVABLE: Bad must declare `void OnPropertyChanged(string propertyName)` for @OBSERVABLE to call
error HMC100: [@Persist]: Items has type System.Collections.Generic.List<int>, which [@Persist] can't store; supported: int, long, float, double, bool, string, enums
error HMC100: [@Persist]: Seed is read-only, so [@Persist] couldn't load it back
error HMC100: [@Command]: [@Command] method Dance must be static: commands are registered at startup, before any instance exists
error HMC100: [@Command]: parameter 'h' of Spawn has type Showcase.App.Hero, which [@Command] can't parse; supported: int, long, float, double, bool, string, enums
error HMC100: @COMMAND_HELP: command 'heal' is registered more than once: Showcase.App.MoreCheats.FullHeal (Bad.cs:5) and Showcase.App.Cheats.Heal (Cheats.cs:13)
```

With handwritten code or reflection, most of these problems only surface at runtime, and some never surface at all (for example two commands with the same name silently overriding each other).

## Why not something else

| Approach | Problem |
|---|---|
| Design patterns such as inheritance or composition | They share logic, but each property's backing field, setter and save/load lines still have to be written one by one |
| Runtime reflection (automatic saving, automatic command registration) | Runtime cost; IL2CPP / AOT trimming removes methods that aren't referenced directly, so commands disappear; errors only show up at runtime |
| Source generators | Can only add code, not rewrite existing code, so they can't "turn a field into a property with logic"; require `partial` everywhere; high barrier to writing them |
| HowMacro | Generates plain strongly typed code with no reflection; the names and types the IDE sees are always correct; a macro is just an ordinary C# method, so the barrier is low |

Also, these macros are tailored to this project (for example `[@Command]` registers with this project's `DebugConsole`). That is exactly how HowMacro is meant to be used:
**the project defines its own rules**, instead of adopting a general-purpose framework's conventions.

## See the expansion yourself

```bash
# Everything that was expanded
dotnet build samples/Showcase/Showcase.App -p:HowMacroDump=true

# Only the save-related macros in Hero.cs
dotnet build samples/Showcase/Showcase.App -p:HowMacroDumpFiles=Hero.cs -p:HowMacroDumpMacros="Persist WRITE_PERSISTED READ_PERSISTED"
```

The output goes to `samples/Showcase/Showcase.App/howmacro-dump/`; the expansion report is `_expansions.txt` inside it.

The macro implementations are in `Showcase.Macros/`: `Properties.cs` (OBSERVABLE / CLAMP), `Persistence.cs` (saving) and `Commands.cs` (debug commands).
