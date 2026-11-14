using System;
using System.Linq;
using System.Text;
using HowMacro;

namespace Snake.Macros;

/// <summary>
/// <c>[@Persist]</c>: marks a field to save. <c>@WRITE_PERSISTED(writer)</c> / <c>@READ_PERSISTED(reader)</c>
/// generate strongly typed binary read/write code with a format fingerprint computed at compile time: if the saved fields change, reading an old save fails clearly instead of reading misaligned data.
/// </summary>
[AttributeMacro(MacroTarget.Field | MacroTarget.Property)]
public sealed class Persist : Attribute
{
    public Persist()
    {
        var member = Macro.Declaration!;
        if (!new[] { "int", "long", "bool", "string" }.Contains(member.Type))
            Macro.Error($"{member.Name} has type {member.Type}, which [@Persist] can't store; supported: int, long, bool, string");
        else if (!member.CanWrite)
            Macro.Error($"{member.Name} is read-only, so [@Persist] couldn't load it back");
        else
            Macro.Fragment("persist");
    }
}

public static class Persistence
{
    [Macro(MacroSite.Statement)]
    public static void WRITE_PERSISTED(object writer)
    {
        var w = Macro.Arg(0);
        Macro.Emit($"{w}.Write({Fingerprint()});");
        foreach (var m in Members()) Macro.Emit($"{w}.Write(this.{m.Name}{(m.Type == "string" ? " ?? \"\"" : "")});");
    }

    [Macro(MacroSite.Statement)]
    public static void READ_PERSISTED(object reader)
    {
        var r = Macro.Arg(0);
        Macro.Emit($"if ({r}.ReadInt32() != {Fingerprint()}) throw new global::System.IO.InvalidDataException(" +
                   $"\"save format changed; this build expects \" + {Macro.Quote(Schema())});");
        foreach (var m in Members())
        {
            var read = m.Type switch { "int" => "ReadInt32", "long" => "ReadInt64", "bool" => "ReadBoolean", _ => "ReadString" };
            Macro.Emit($"this.{m.Name} = {r}.{read}();");
        }
    }

    private static MacroMember[] Members() =>
        Macro.Fragments("persist").Where(f => f.ContainingType == Macro.ContainingType && f.Declaration != null)
            .Select(f => f.Declaration!).ToArray();

    private static string Schema() => string.Join(", ", Members().Select(m => $"{m.Name}:{m.Type}"));

    /// <summary>FNV-1a: string.GetHashCode is randomized per process in .NET, so it can't be used for persistence.</summary>
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
