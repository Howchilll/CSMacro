using System;
using System.Linq;
using System.Text;
using CSMacro;

namespace Dungeon.Macros
{
    /// <summary>
    /// <c>[@Persist]</c>：标记要存档的字段/属性。编译期检查类型是否支持、是否可写。
    /// 由 <c>@WRITE_PERSISTED(writer)</c> / <c>@READ_PERSISTED(reader)</c> 生成强类型的二进制读写代码。
    /// </summary>
    [AttributeMacro(MacroTarget.Field | MacroTarget.Property)]
    public sealed class Persist : Attribute
    {
        public Persist()
        {
            var member = Macro.Declaration!;
            if (Persistence.Codec(member.TypeInfo, "x") == null)
                Macro.Error($"{member.Name} has type {member.Type}, which [@Persist] can't store; supported: {Shared.SupportedTypes}");
            else if (!member.CanWrite)
                Macro.Error($"{member.Name} is read-only, so [@Persist] couldn't load it back");
            else
                Macro.Fragment("persist");
        }
    }

    public static class Persistence
    {
        /// <summary>写出所在类型全部 [@Persist] 成员：先写编译期算好的格式指纹，再按声明顺序逐个写。没有反射。</summary>
        [Macro(MacroSite.Statement)]
        public static void WRITE_PERSISTED(object writer)
        {
            if (!Check("System.IO.BinaryWriter")) return;
            var w = Macro.Arg(0);
            Macro.Emit($"{w}.Write({Fingerprint()});");
            foreach (var m in Members())
                Macro.Emit(Codec(m.TypeInfo, "this." + m.Name)!.Value.Write.Replace("$w", w) + ";");
        }

        /// <summary>读回全部 [@Persist] 成员。指纹不一致（存档后增删改过存档字段）时明确报错。</summary>
        [Macro(MacroSite.Statement)]
        public static void READ_PERSISTED(object reader)
        {
            if (!Check("System.IO.BinaryReader")) return;
            var r = Macro.Arg(0);
            Macro.Emit($"if ({r}.ReadInt32() != {Fingerprint()}) throw new global::System.IO.InvalidDataException(" +
                       $"\"save format changed; this build expects \" + {Macro.Quote(Schema())});");
            foreach (var m in Members())
                Macro.Emit($"this.{m.Name} = {Codec(m.TypeInfo, "")!.Value.Read.Replace("$r", r)};");
        }

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
            Macro.Fragments("persist").Where(f => f.ContainingType == Macro.ContainingType && f.Declaration != null)
                .Select(f => f.Declaration!).ToArray();

        private static string Schema() => string.Join(", ", Members().Select(m => $"{m.Name}:{m.Type}"));

        /// <summary>FNV-1a：string.GetHashCode 在 .NET 里每个进程随机化，不能用于持久化。</summary>
        private static string Fingerprint()
        {
            unchecked
            {
                var hash = (int)2166136261;
                foreach (var b in Encoding.UTF8.GetBytes(Macro.ContainingType + "|" + Schema())) hash = (hash ^ b) * 16777619;
                return hash.ToString();
            }
        }
    }
}
