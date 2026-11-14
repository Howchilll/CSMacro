using System.Linq;
using HowMacro;

namespace Snake.Macros;

/// <summary>Small helpers shared by several macros. Macro files can use each other (they are compiled together first), but not the game's own types.</summary>
internal static class Shared
{
    internal const string SupportedTypes = "int, long, float, double, bool, string, enums";

    /// <summary>Code that parses the string expression <paramref name="text"/> into <paramref name="type"/>; null for unsupported types.</summary>
    internal static string? Parse(MacroType? type, string text)
    {
        if (type == null) return null;
        if (type.Kind == MacroTypeKind.Enum) return $"({type.CodeName})global::System.Enum.Parse(typeof({type.CodeName}), {text}, true)";
        const string inv = "global::System.Globalization.CultureInfo.InvariantCulture";
        return type.FullName switch
        {
            "int" => $"int.Parse({text}, {inv})",
            "long" => $"long.Parse({text}, {inv})",
            "float" => $"float.Parse({text}, {inv})",
            "double" => $"double.Parse({text}, {inv})",
            "bool" => $"bool.Parse({text})",
            "string" => text,
            _ => null,
        };
    }

    /// <summary>The statement that calls <c>OnPropertyChanged(string)</c>, when the containing type declares it.</summary>
    internal static string? NotifyCall() =>
        Macro.ContainingTypeInfo?.MembersNamed("OnPropertyChanged")
            .Any(m => m.Kind == MacroMemberKind.Method && m.Parameters.Count == 1 && m.Parameters[0].Type == "string") == true
            ? $"OnPropertyChanged(nameof({Macro.Name}));"
            : null;
}
