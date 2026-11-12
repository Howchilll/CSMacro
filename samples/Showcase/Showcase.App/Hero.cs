using System.ComponentModel;
using Showcase.Macros;
using static Showcase.Macros.Persistence;
using static Showcase.Macros.Properties;

namespace Showcase.App;

public enum HeroClass { Warrior, Mage, Rogue }

/// <summary>
/// 一个典型的游戏数据类：要绑定 UI（变更通知）、要约束数值范围、要存档。
/// 不用宏时，每个属性都要手写后备字段、相等判断、夹值、通知，再手写存档的读写和版本检查。
/// 用宏后：每个成员一行，名字、类型、初值、范围、要不要存档，一眼看完。
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
    public int Combo = @CLAMP(0, 10, 0); // 只是运行时状态，不存档

    public void Save(BinaryWriter writer) { @WRITE_PERSISTED(writer); }
    public void Load(BinaryReader reader) { @READ_PERSISTED(reader); }

    public override string ToString() => $"{Name} the {Class}, Lv {Level}, HP {Hp}/100, {Gold} gold, combo {Combo}";
}
