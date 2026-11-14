using System;
using System.Linq;
using System.Text;
using HowMacro;

namespace Showcase.Macros
{
    /// <summary>
    /// Attribute macro <c>[@Persist]</c>: marks a field/property to save. Checks at compile time that the type is supported and the member is writable.
    /// Fragments go to the "persist" channel; <see cref="Persistence.WRITE_PERSISTED"/> / <see cref="Persistence.READ_PERSISTED"/> generate the read/write code.
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
        /// Writes all <c>[@Persist]</c> members of the containing type: <c>public void Save(BinaryWriter w) { @WRITE_PERSISTED(w); }</c>
        /// First writes a format fingerprint computed at compile time, then each member in declaration order, strongly typed. No reflection.
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
        /// Reads back all <c>[@Persist]</c> members: <c>public void Load(BinaryReader r) { @READ_PERSISTED(r); }</c>
        /// A fingerprint mismatch (saved fields changed since the save) fails clearly instead of reading misaligned data.
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

        /// <summary>(write expression, read expression); null for unsupported types. $w / $r are placeholders for the writer / reader.</summary>
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

        /// <summary>Save format description: member names and types in declaration order.</summary>
        private static string Schema() => string.Join(", ", Members().Select(m => $"{m.Name}:{m.Type}"));

        /// <summary>
        /// Format fingerprint. Uses FNV-1a rather than string.GetHashCode, which is randomized per process in .NET and can't be used for persistence.
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
