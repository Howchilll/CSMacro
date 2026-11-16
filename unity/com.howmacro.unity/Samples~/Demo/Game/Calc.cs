using System;
using System.Collections.Generic;
using Demo.Macros;
using UnityEngine;
using static Demo.Macros.DemoMacros;

namespace Demo.Game
{
    public static class CommandConsole
    {
        static readonly Dictionary<string, Action> s_commands = new Dictionary<string, Action>();
        public static void Register(string name, Action action) => s_commands[name] = action;
        public static int Count => s_commands.Count;
        public static IEnumerable<string> Names { get { var names = new List<string>(s_commands.Keys); names.Sort(); return names; } }
        public static bool Run(string name) { if (!s_commands.TryGetValue(name, out var a)) return false; a(); return true; }
    }

    public static class Calc
    {
        public static int Area(int side) => @SQUARE(side + 1);

        public static string Where() => @WHERE();

        public static void Report(int v)
        {
            @LOG_VALUE(v * 2);
        }

        public static string[] AllCommands() => @COMMANDS();

        [@Command("hello")] public static void Hello() => Debug.Log("[spike] hello command ran");
        [@Command("crash")] public static void Crash() => Fail();

        static void Fail()
        {
            var nine = @SQUARE(3);
            @LOG_VALUE(nine);
            throw new InvalidOperationException("boom"); // line 38: the stack trace should point here
        }
    }

    public static class SpikeChecks
    {
        public static string Run()
        {
            var lines = new List<string>
            {
                "Area(4) = " + Calc.Area(4) + " (expanded: 25, unexpanded: 0)",
                "Where() = \"" + Calc.Where() + "\" (expanded: Calc.cs:22)",
                "AllCommands() = [" + string.Join(", ", Calc.AllCommands()) + "] (expanded: Clear, crash, hello, Rain x20, Spawn cube, Spawn sphere)",
                "registered commands = " + CommandConsole.Count + " (startup code ran: 6; 0 under -executeMethod, which runs outside Play mode)",
            };
            Calc.Report(21);
            try { Calc.Crash(); }
            catch (Exception e) { lines.Add("stack trace:\n" + e.StackTrace); }
            return string.Join("\n", lines);
        }

        // In a player build started with -howmacro-checks: run the checks once and quit
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void InPlayer()
        {
            if (Application.isEditor || System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-howmacro-checks") < 0) return;
            Debug.Log("[spike] player\n" + Run());
            Application.Quit();
        }
    }
}
