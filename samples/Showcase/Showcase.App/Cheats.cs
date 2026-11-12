using Showcase.Macros;

namespace Showcase.App;

/// <summary>
/// 调试命令：写一个普通静态方法，标上 [@Command] 就完成了。
/// 参数解析、可选参数、用法提示、注册、帮助文本全部由宏生成。
/// </summary>
public static class Cheats
{
    public static Hero Hero { get; } = new();

    [@Command("heal", "Restore HP (clamped to 100)")]
    public static void Heal(int amount) => Hero.Hp += amount;

    [@Command("hurt", "Take damage")]
    public static void Hurt(int amount) => Hero.Hp -= amount;

    [@Command("gold", "Give gold")]
    public static void Gold(int amount) => Hero.Gold += amount;

    [@Command("class", "Change class")]
    public static void SetClass(HeroClass value) => Hero.Class = value;

    [@Command("level", "Set level; defaults to max")]
    public static void SetLevel(int value = 99) => Hero.Level = value;

    [@Command("rename", "Rename the hero")]
    public static void Rename(string name) => Hero.Name = name;

    [@Command("status", "Show the hero")]
    public static string Status() => Hero.ToString();
}
