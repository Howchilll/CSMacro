using System;
using HowMacro;

namespace Demo.Macros
{
    /// <summary>
    /// Attribute macro <c>[@Save]</c> / <c>[@Save("key")]</c>: registers the field with the save container.
    /// It produces startup code, which the engine collects into a generated startup script that runs when the program starts. The save container needs no hand-written registration code.
    /// </summary>
    [AttributeMacro(MacroTarget.Field | MacroTarget.Property)]
    public sealed class Save : Attribute
    {
        public Save(string? key = null)
        {
            var k = Macro.ArgCount > 0 ? (string)Macro.ArgValue(0)! : Macro.Name;
            var owner = "global::" + Macro.ContainingType;
            Macro.Startup(
                $"global::Demo.App.SaveContainer.Register<{owner}>({Macro.Quote(k)}, " +
                $"o => o.{Macro.Name}, (o, v) => o.{Macro.Name} = ({Macro.Type})v!);");
        }
    }

    /// <summary>
    /// Attribute macro <c>[@Track]</c>: registers the member on the "track" channel, collected by the method macro <c>@DUMP_TRACKED</c>.
    /// Fragments carry the member name, containing type and so on, so no code is needed here.
    /// </summary>
    [AttributeMacro(MacroTarget.Field | MacroTarget.Property)]
    public sealed class Track : Attribute
    {
        public Track() => Macro.Fragment("track");
    }
}
