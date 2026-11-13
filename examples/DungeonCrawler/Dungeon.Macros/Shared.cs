using System.Globalization;
using System.Linq;
using CSMacro;

namespace Dungeon.Macros
{
    /// <summary>几个宏共用的小工具：按类型生成解析代码、字面量。</summary>
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

        /// <summary>给人看的类型名：枚举列出可选值，其余用 C# 关键字。</summary>
        internal static string Label(MacroType type) =>
            type.Kind == MacroTypeKind.Enum ? string.Join("|", type.EnumValues).ToLowerInvariant() : type.FullName;

        internal static string Literal(object? value, MacroType type) => value switch
        {
            null => "default",
            string s => Macro.Quote(s),
            bool b => b ? "true" : "false",
            float f => f.ToString("R", CultureInfo.InvariantCulture) + "f",
            double d => d.ToString("R", CultureInfo.InvariantCulture) + "d",
            _ when type.Kind == MacroTypeKind.Enum => $"({type.CodeName}){System.Convert.ToInt64(value, CultureInfo.InvariantCulture)}",
            System.IFormattable n => n.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString()!,
        };

        /// <summary>所在类型声明了 <c>OnPropertyChanged(string)</c> 时，返回调用它的语句。</summary>
        internal static string? NotifyCall()
        {
            var has = Macro.ContainingTypeInfo?.MembersNamed("OnPropertyChanged")
                .Any(m => m.Kind == MacroMemberKind.Method && m.Parameters.Count == 1 && m.Parameters[0].Type == "string") == true;
            return has ? $"OnPropertyChanged(nameof({Macro.Name}));" : null;
        }
    }
}
