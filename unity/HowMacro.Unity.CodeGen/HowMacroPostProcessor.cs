using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Unity.CompilationPipeline.Common.Diagnostics;
using Unity.CompilationPipeline.Common.ILPostProcessing;

namespace HowMacro.Unity.CodeGen
{
    /// <summary>
    /// Expands HowMacro macros in every assembly that references HowMacro.Abstractions.
    /// Unity has already compiled the unexpanded code (always valid C#); this hands that assembly to the bridge,
    /// which expands the macros and recompiles, and gives Unity the new assembly instead.
    ///
    /// Runs in Unity's ILPP runner (.NET 6, out of process). This assembly is compiled against Unity's API profile,
    /// which has no AssemblyLoadContext, so the bridge's own load context is created via reflection. It holds Roslyn
    /// and the engine, isolated from the runner's older System.Collections.Immutable.
    /// </summary>
    sealed class HowMacroPostProcessor : ILPostProcessor
    {
        const string BridgeFolder = "Assets/HowMacro/Bridge~";
        static MethodInfo s_process;
        static string s_loadError;

        public override ILPostProcessor GetInstance() => this;

        public override bool WillProcess(ICompiledAssembly compiledAssembly) =>
            !compiledAssembly.Name.EndsWith(".CodeGen", StringComparison.Ordinal) &&
            compiledAssembly.References.Any(r => Path.GetFileNameWithoutExtension(r) == "HowMacro.Abstractions");

        public override ILPostProcessResult Process(ICompiledAssembly compiledAssembly)
        {
            var projectDir = Directory.GetCurrentDirectory();
            var messages = new List<DiagnosticMessage>();
            var original = compiledAssembly.InMemoryAssembly;
            try
            {
                var process = LoadBridge(Path.Combine(projectDir, BridgeFolder));
                if (process == null)
                {
                    messages.Add(Error(null, 0, 0, "HowMacro: couldn't load the bridge from " + BridgeFolder + ": " + s_loadError));
                    return new ILPostProcessResult(original, messages);
                }

                var started = DateTime.UtcNow;
                var result = (object[])process.Invoke(null, new object[]
                {
                    compiledAssembly.Name, original.PeData, original.PdbData,
                    compiledAssembly.References, compiledAssembly.Defines, projectDir,
                });
                if (result == null)
                {
                    Log(compiledAssembly.Name + ": nothing to expand (" + (DateTime.UtcNow - started).TotalMilliseconds.ToString("0") + " ms)");
                    return new ILPostProcessResult(original, messages);
                }

                foreach (var line in (string[])result[3]) Log(line);
                foreach (var d in (string[])result[2])
                {
                    var p = d.Split('\t');
                    messages.Add(new DiagnosticMessage
                    {
                        DiagnosticType = p[0] == "error" ? DiagnosticType.Error : DiagnosticType.Warning,
                        File = p[1], Line = int.Parse(p[2]), Column = int.Parse(p[3]), MessageData = p[4],
                    });
                }
                var pe = (byte[])result[0];
                Log(compiledAssembly.Name + ": " + (pe != null ? "replaced" : "failed") + " in " + (DateTime.UtcNow - started).TotalMilliseconds.ToString("0") + " ms");
                return new ILPostProcessResult(pe != null ? new InMemoryAssembly(pe, (byte[])result[1]) : original, messages);
            }
            catch (Exception e)
            {
                var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
                Log(compiledAssembly.Name + ": " + inner);
                messages.Add(Error(null, 0, 0, "HowMacro: expanding " + compiledAssembly.Name + " crashed: " + inner));
                return new ILPostProcessResult(original, messages);
            }
        }

        static DiagnosticMessage Error(string file, int line, int column, string message) =>
            new DiagnosticMessage { DiagnosticType = DiagnosticType.Error, File = file ?? "", Line = line, Column = column, MessageData = message };

        static void Log(string line)
        {
            try
            {
                Directory.CreateDirectory("Library/HowMacro");
                File.AppendAllText("Library/HowMacro/ilpp.log", DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine);
            }
            catch (IOException) { }
        }

        /// <summary>Loads every assembly of the bridge folder into one new AssemblyLoadContext, once per runner process.</summary>
        static MethodInfo LoadBridge(string folder)
        {
            if (s_process != null || s_loadError != null) return s_process;
            try
            {
                var alcType = Type.GetType("System.Runtime.Loader.AssemblyLoadContext, System.Runtime.Loader", throwOnError: true);
                var alc = Activator.CreateInstance(alcType, "HowMacro", false);
                var loadFromPath = alcType.GetMethod("LoadFromAssemblyPath", new[] { typeof(string) });
                var framework = FrameworkVersions();

                Assembly bridge = null;
                foreach (var dll in Directory.GetFiles(folder, "*.dll"))
                {
                    var name = AssemblyName.GetAssemblyName(dll);
                    // The runtime already has an equal or newer copy (e.g. System.Runtime.CompilerServices.Unsafe): use that
                    if (framework.TryGetValue(name.Name, out var v) && v >= name.Version) continue;
                    var asm = (Assembly)loadFromPath.Invoke(alc, new object[] { dll });
                    if (name.Name == "HowMacro.Unity.Bridge") bridge = asm;
                }
                if (bridge == null) throw new FileNotFoundException("HowMacro.Unity.Bridge.dll not found in " + folder);
                Log("bridge loaded from " + folder + " (runtime " + Environment.Version + ")");
                return s_process = bridge.GetType("HowMacro.Unity.Bridge", throwOnError: true).GetMethod("Process");
            }
            catch (Exception e)
            {
                s_loadError = e.ToString();
                Log("bridge load failed: " + e);
                return null;
            }
        }

        static Dictionary<string, Version> FrameworkVersions()
        {
            var result = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
            var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                try { var n = AssemblyName.GetAssemblyName(path); result[n.Name] = n.Version; }
                catch (Exception) { }
            }
            return result;
        }
    }
}
