using System;
using System.Linq;
using System.Text;
using CSMacro;

namespace Showcase.Macros
{
    /// <summary>
    /// 特性宏 <c>[@Persist]</c>：标记要存档的字段/属性。编译期检查类型是否支持、成员是否可写。
    /// 片段汇总到 "persist" 频道，由 <see cref="Persistence.WRITE_PERSISTED"/> / <see cref="Persistence.READ_PERSISTED"/> 生成读写代码。
    /// </summary>
    [AttributeMacro(MacroTarget.Field | MacroTarget.Property)]
    public sealed class Persist : Attribute
    {
        public Persist()
        {
            var member = Macro.Declaration!;
            if (Persistence.Codec(member.TypeInfo, "x") == null)
            {
                Macro.Error($"{member.Name} has type {member.Type}, which [@Persist] can't store; supported: {Persistence.Supported}");
                return;
            }
            if (!member.CanWrite)
            {
                Macro.Error($"{member.Name} is read-only, so [@Persist] couldn't load it back");
                return;
            }
            Macro.Fragment("persist");
        }
    }

    public static class Persistence
    {
        internal const string Supported = "int, long, float, double, bool, string, enums";

        /// <summary>
        /// 写出所在类型全部 <c>[@Persist]</c> 成员：<c>public void Save(BinaryWriter w) { @WRITE_PERSISTED(w); }</c>
        /// 先写一个编译期算好的格式指纹，再按声明顺序强类型写出每个成员。没有反射。
        /// </summary>
        [Macro(MacroSite.Statement)]
        public static void WRITE_PERSISTED(object writer)
        {
            if (!Check("System.IO.BinaryWriter")) return;
            var w = Macro.Arg(0);
            Macro.Emit($"{w}.Write({Fingerprint()});");
            foreach (var m in Members())
                Macro.Emit(Codec(m.TypeInfo, "this." + m.Name)!.Value.Write.Replace("$w", w) + ";");
        }

        /// <summary>
        /// 读回全部 <c>[@Persist]</c> 成员：<c>public void Load(BinaryReader r) { @READ_PERSISTED(r); }</c>
        /// 指纹不一致（存档后增删改过存档字段）时明确报错，而不是读出错位的数据。
        /// </summary>
        [Macro(MacroSite.Statement)]
        public static void READ_PERSISTED(object reader)
        {
            if (!Check("System.IO.BinaryReader")) return;
            var r = Macro.Arg(0);
            var schema = Macro.Quote(Schema());
            Macro.Emit($"if ({r}.ReadInt32() != {Fingerprint()}) throw new global::System.IO.InvalidDataException(\"save format changed; this build expects: \" + {schema});");
            foreach (var m in Members())
                Macro.Emit($"this.{m.Name} = {Codec(m.TypeInfo, "")!.Value.Read.Replace("$r", r)};");
        }

        /// <summary>(写出表达式, 读回表达式)；不支持的类型返回 null。$w / $r 是 writer / reader 的占位。</summary>
        internal static (string Write, string Read)? Codec(MacroType? type, string expr)
        {
            if (type == null) return null;
            if (type.Kind == MacroTypeKind.Enum) return ($"$w.Write((int){expr})", $"({type.CodeName})$r.ReadInt32()");
            return type.FullName switch
            {
                "int" => ($"$w.Write({expr})", "$r.ReadInt32()"),
                "long" => ($"$w.Write({expr})", "$r.ReadInt64()"),
                "float" => ($"$w.Write({expr})", "$r.ReadSingle()"),
                "double" => ($"$w.Write({expr})", "$r.ReadDouble()"),
                "bool" => ($"$w.Write({expr})", "$r.ReadBoolean()"),
                "string" or "string?" => ($"$w.Write({expr} ?? \"\")", "$r.ReadString()"),
                _ => ((string, string)?)null,
            };
        }

        private static bool Check(string expected)
        {
            var type = Macro.ArgTypeInfo(0);
            if (type != null && type.Is(expected)) return true;
            Macro.Error($"`{Macro.Arg(0)}` must be a {expected}, but it is {type?.FullName ?? "unknown"}");
            return false;
        }

        private static MacroMember[] Members() =>
            Macro.Fragments("persist")
                .Where(f => f.ContainingType == Macro.ContainingType && f.Declaration != null)
                .Select(f => f.Declaration!)
                .ToArray();

        /// <summary>存档格式描述：成员名和类型，按声明顺序。</summary>
        private static string Schema() => string.Join(", ", Members().Select(m => $"{m.Name}:{m.Type}"));

        /// <summary>
        /// 格式指纹。用 FNV-1a 而不是 string.GetHashCode：后者在 .NET 里每个进程随机化，不能用于持久化。
        /// </summary>
        private static string Fingerprint()
        {
            unchecked
            {
                var hash = (int)2166136261;
                foreach (var b in Encoding.UTF8.GetBytes(Macro.ContainingType + "|" + Schema()))
                    hash = (hash ^ b) * 16777619;
                return hash.ToString();
            }
        }
    }
}
