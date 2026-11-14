using System;
using System.Linq;
using HowMacro;

namespace Snake.Macros;

/// <summary>
/// Key binding: <c>[@Key("Turn up", ConsoleKey.W, ConsoleKey.UpArrow)] void Up() => Turn(Dir.Up);</c>
/// Put it on a parameterless method. <c>@DISPATCH_KEYS(key)</c> generates the switch that dispatches keys, <c>@KEY_HELP()</c> generates the key help.
/// Binding the same key twice is a compile-time error.
/// </summary>
[AttributeMacro(MacroTarget.Method)]
public sealed class Key : Attribute
{
    internal const char Separator = '\u001f';

    public Key(string help, params ConsoleKey[] keys)
    {
        var method = Macro.Declaration!;
        if (method.Parameters.Count != 0)
        {
            Macro.Error($"[@Key] method {method.Name} must have no parameters, it is called when the key is pressed");
            return;
        }
        if (Macro.ArgCount < 2)
        {
            Macro.Error($"[@Key] on {method.Name} needs at least one key, e.g. [@Key(\"{Macro.ArgValue(0)}\", ConsoleKey.W)]");
            return;
        }

        // Fragment: method name | help | key code 1,key code 2 | key name 1,key name 2
        var indices = Enumerable.Range(1, Macro.ArgCount - 1).ToArray();
        Macro.Fragment("keys", string.Join(Separator.ToString(),
            method.Name,
            (string)Macro.ArgValue(0)!,
            string.Join(",", indices.Select(i => Convert.ToInt32(Macro.ArgValue(i)))),
            string.Join(",", indices.Select(i => Macro.Arg(i).Split('.').Last()))));
    }
}

public static class Keys
{
    /// <summary>
    /// <c>@DISPATCH_KEYS(key);</c> where key is a ConsoleKey. Generates one switch over all [@Key] methods of the containing type.
    /// </summary>
    [Macro(MacroSite.Statement)]
    public static void DISPATCH_KEYS(ConsoleKey key)
    {
        var bindings = Bindings();
        foreach (var dup in bindings.SelectMany(b => b.Codes.Zip(b.Names, (c, n) => (Code: c, Name: n, b.Method)))
                     .GroupBy(k => k.Code).Where(g => g.Count() > 1))
            Macro.Error($"key {dup.First().Name} is bound more than once: {string.Join(" and ", dup.Select(d => d.Method))}");

        Macro.Emit($"switch ({Macro.Arg(0)}) {{");
        foreach (var b in bindings)
            Macro.Emit(string.Concat(b.Codes.Select(c => $"case (global::System.ConsoleKey){c}: ")) + $"{b.Method}(); break;");
        Macro.Emit("}");
    }

    /// <summary><c>@KEY_HELP()</c>: key help for the containing type, e.g. "W/UpArrow turn up · P/Spacebar pause".</summary>
    [Macro]
    public static string KEY_HELP()
    {
        Macro.Emit(Macro.Quote(string.Join("  ", Bindings().Select(b => $"[{string.Join("/", b.Names)}] {b.Help}"))));
        return "";
    }

    private static (string Method, string Help, string[] Codes, string[] Names)[] Bindings() =>
        Macro.Fragments("keys")
            .Where(f => f.ContainingType == Macro.ContainingType)
            .Select(f => f.Code.Split(Key.Separator))
            .Select(p => (p[0], p[1], p[2].Split(','), p[3].Split(',')))
            .ToArray();
}
