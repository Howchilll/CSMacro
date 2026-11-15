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
        const string PackageName = "com.howmacro.unity";
        static MethodInfo s_process;
        static string s_loadError;

        public override ILPostProcessor GetInstance() => this;

        public override bool WillProcess(ICompiledAssembly compiledAssembly) =>
            !compiledAssembly.Name.EndsWith(".CodeGen", StringComparison.Ordinal) &&
            compiledAssembly.Name != "HowMacro.Unity.Editor" &&
            compiledAssembly.References.Any(r => Path.GetFileNameWithoutExtension(r) == "HowMacro.Abstractions");

        public override ILPostProcessResult Process(ICompiledAssembly compiledAssembly)
        {
            var projectDir = Directory.GetCurrentDirectory();
            var messages = new List<DiagnosticMessage>();
            var original = compiledAssembly.InMemoryAssembly;
            try
            {
                var process = LoadBridge(projectDir);
                if (process == null)
                {
                    messages.Add(Error(null, 0, 0, "HowMacro: couldn't load the bridge: " + s_loadError));
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

        static readonly object s_logLock = new object();

        /// <summary>
        /// Appends to Library/HowMacro/ilpp.log. Unity post-processes assemblies in parallel (threads, and sometimes several
        /// runner processes), so writes are serialized here and retried briefly when another process holds the file.
        /// </summary>
        static void Log(string line)
        {
            var text = DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine;
            lock (s_logLock)
            {
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    try
                    {
                        Directory.CreateDirectory("Library/HowMacro");
                        File.AppendAllText("Library/HowMacro/ilpp.log", text);
                        return;
                    }
                    catch (IOException) { System.Threading.Thread.Sleep(25); }
                }
            }
        }

        /// <summary>
        /// Where the bridge is, in order: an embedded package, a git/registry package in the package cache, a local
        /// <c>file:</c> package named in Packages/manifest.json, or a manual install under Assets/HowMacro.
        /// </summary>
        static string FindBridge(string projectDir)
        {
            var embedded = Path.Combine(projectDir, "Packages", PackageName, "Bridge~");
            if (Directory.Exists(embedded)) return embedded;

            var cache = Path.Combine(projectDir, "Library", "PackageCache");
            if (Directory.Exists(cache))
            {
                var cached = Directory.GetDirectories(cache, PackageName + "@*")
                    .Select(d => Path.Combine(d, "Bridge~")).Where(Directory.Exists)
                    .OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
                if (cached != null) return cached;
            }

            var manifest = Path.Combine(projectDir, "Packages", "manifest.json");
            if (File.Exists(manifest))
            {
                var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(manifest),
                    "\"" + System.Text.RegularExpressions.Regex.Escape(PackageName) + "\"\\s*:\\s*\"file:([^\"]+)\"");
                if (m.Success)
                {
                    var local = Path.Combine(Path.GetFullPath(Path.Combine(projectDir, "Packages", m.Groups[1].Value.Replace("\\\\", "\\"))), "Bridge~");
                    if (Directory.Exists(local)) return local;
                }
            }

            var manual = Path.Combine(projectDir, "Assets", "HowMacro", "Bridge~");
            if (Directory.Exists(manual)) return manual;
            throw new DirectoryNotFoundException("no Bridge~ folder found for " + PackageName +
                                                 " (looked in Packages/, Library/PackageCache/, file: packages in Packages/manifest.json and Assets/HowMacro/)");
        }

        /// <summary>
        /// Loads every assembly of the bridge folder into one new AssemblyLoadContext, once per runner process.
        /// Loaded from bytes, not from the path, so the files stay unlocked: on Windows a locked DLL would stop the
        /// package manager from replacing the package and make rebuilding the bridge fail while Unity is open.
        /// </summary>
        static MethodInfo LoadBridge(string projectDir)
        {
            if (s_process != null || s_loadError != null) return s_process;
            try
            {
                var folder = FindBridge(projectDir);
                var alcType = Type.GetType("System.Runtime.Loader.AssemblyLoadContext, System.Runtime.Loader", throwOnError: true);
                var alc = Activator.CreateInstance(alcType, "HowMacro", false);
                var loadFromStream = alcType.GetMethod("LoadFromStream", new[] { typeof(Stream), typeof(Stream) });
                var framework = FrameworkVersions();

                Assembly bridge = null;
                foreach (var dll in Directory.GetFiles(folder, "*.dll"))
                {
                    var name = AssemblyName.GetAssemblyName(dll);
                    // The runtime already has an equal or newer copy (e.g. System.Runtime.CompilerServices.Unsafe): use that
                    if (framework.TryGetValue(name.Name, out var v) && v >= name.Version) continue;
                    var pdb = Path.ChangeExtension(dll, ".pdb");
                    var asm = (Assembly)loadFromStream.Invoke(alc, new object[]
                    {
                        new MemoryStream(File.ReadAllBytes(dll)),
                        File.Exists(pdb) ? new MemoryStream(File.ReadAllBytes(pdb)) : null,
                    });
                    if (name.Name == "HowMacro.Unity.Bridge") bridge = asm;
                }
                if (bridge == null) throw new FileNotFoundException("HowMacro.Unity.Bridge.dll not found in " + folder);
                Log("bridge " + BridgeStamp.Value + " loaded from " + folder + " (runtime " + Environment.Version + ")");
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
