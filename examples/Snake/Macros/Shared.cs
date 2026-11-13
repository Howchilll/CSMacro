using System.Linq;
using CSMacro;

namespace Snake.Macros;

/// <summary>几个宏共用的小工具。宏文件之间可以互相引用（它们一起先编译），但不能引用游戏本身的类型。</summary>
internal static class Shared
{
    internal const string SupportedTypes = "int, long, float, double, bool, string, enums";

    /// <summary>把字符串表达式 <paramref name="text"/> 解析成 <paramref name="type"/> 的代码；不支持的类型返回 null。</summary>
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

    /// <summary>所在类型声明了 <c>OnPropertyChanged(string)</c> 时，返回调用它的语句。</summary>
    internal static string? NotifyCall() =>
        Macro.ContainingTypeInfo?.MembersNamed("OnPropertyChanged")
            .Any(m => m.Kind == MacroMemberKind.Method && m.Parameters.Count == 1 && m.Parameters[0].Type == "string") == true
            ? $"OnPropertyChanged(nameof({Macro.Name}));"
            : null;
}
