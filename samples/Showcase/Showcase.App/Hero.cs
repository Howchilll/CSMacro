using System.ComponentModel;
using Showcase.Macros;
using static Showcase.Macros.Persistence;
using static Showcase.Macros.Properties;

namespace Showcase.App;

public enum HeroClass { Warrior, Mage, Rogue }

/// <summary>
/// A typical game data class: it binds to UI (change notification), clamps values, and is saved.
/// Without macros every property needs a hand-written backing field, equality check, clamping and notification, plus hand-written save/load code and version checks.
/// With macros: one line per member; name, type, initial value, range and whether it is saved, all at a glance.
/// </summary>
public class Hero : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    [@Persist] public string Name = @OBSERVABLE("Nameless");
    [@Persist] public HeroClass Class = @OBSERVABLE(HeroClass.Warrior);
    [@Persist] public int Level = @CLAMP(1, 99, 1);
    [@Persist] public int Hp = @CLAMP(0, 100, 100);
    [@Persist] public int Gold = @CLAMP(0, 999_999, 0);
    public int Combo = @CLAMP(0, 10, 0); // runtime state only, not saved

    public void Save(BinaryWriter writer) { @WRITE_PERSISTED(writer); }
    public void Load(BinaryReader reader) { @READ_PERSISTED(reader); }

    public override string ToString() => $"{Name} the {Class}, Lv {Level}, HP {Hp}/100, {Gold} gold, combo {Combo}";
}
