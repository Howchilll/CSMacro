using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HowMacro.Engine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace HowMacro.Testing
{
    /// <summary>
    /// Expands a piece of source in a test:
    /// <code>
    /// var r = MacroTest.Expand(@"
    ///     using static Demo.Macros.M;
    ///     class Player { public int Gold = @LOGGED(100); }", typeof(M).Assembly);
    /// r.AssertSuccess();
    /// Assert.Contains("set {", r.Line(3));
    /// </code>
    /// Macros run inside the test process (the test project references the macro library anyway), so no project build is needed.
    /// </summary>
    public static class MacroTest
    {
        /// <summary>Expands one source file. Pass the macro library (and any other assembly the source uses) in <paramref name="references"/>, e.g. <c>typeof(M).Assembly</c>.</summary>
        public static MacroTestResult Expand(string source, params Assembly[] references) =>
            Expand(new[] { source }, new MacroTestOptions { References = references.ToList() });

        public static MacroTestResult Expand(string source, MacroTestOptions options) =>
            Expand(new[] { source }, options);

        /// <summary>Expands several source files (named Test0.cs, Test1.cs, ...).</summary>
        public static MacroTestResult Expand(IReadOnlyList<string> sources, MacroTestOptions? options = null)
        {
            options ??= new MacroTestOptions();
            var referencePaths = ReferencePaths(options.References);

            var inputs = sources
                .Select((text, i) => new SourceFile($"Test{i}.cs", $"Test{i}.cs", text))
                .ToList();

            var expandOptions = new ExpandOptions
            {
                References = referencePaths,
                Defines = options.Defines,
                LanguageVersion = options.LanguageVersion,
                Nullable = options.Nullable,
                Verify = options.Verify,
                Macros = options.Macros.Count > 0 ? options.Macros : null,
                RuntimeCleanup = options.RuntimeCleanup,
            };
            if (options.StartupAttribute != null) expandOptions.StartupAttribute = options.StartupAttribute;

            var result = MacroExpander.Expand(inputs, expandOptions, new InProcessInvoker());
            return new MacroTestResult(inputs, result, referencePaths, options);
        }

        /// <summary>Runs <paramref name="action"/> and returns what it wrote to the console (for macros that log, such as LOG or LOGGED).
        /// Temporarily replaces Console.Out, so disable parallel test execution.</summary>
        public static string CaptureConsole(Action action)
        {
            var previous = Console.Out;
            var writer = new StringWriter();
            Console.SetOut(writer);
            try { action(); }
            finally { Console.SetOut(previous); }
            return writer.ToString();
        }

        /// <summary>
        /// Compile references: the runtime framework + assemblies already loaded in this process + explicitly passed assemblies.
        /// Passing them explicitly makes sure the macro library is loaded (.NET loads assemblies on demand).
        /// </summary>
        private static IReadOnlyList<string> ReferencePaths(IEnumerable<Assembly> explicitReferences)
        {
            var paths = new List<string>();
            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
                paths.AddRange(tpa.Split(Path.PathSeparator));

            paths.AddRange(explicitReferences.Concat(AppDomain.CurrentDomain.GetAssemblies())
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => a.Location));

            // Keep only one assembly per name, preferring the framework copy
            return paths
                .Where(File.Exists)
                .GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>The macro library is already in the test process: find the loaded assembly by path and call it directly, so HowMacro.Abstractions is naturally the same instance.</summary>
        private sealed class InProcessInvoker : IMacroInvoker
        {
            public void Invoke(MacroMethod method, MacroContext context)
            {
                var path = method.AssemblyPath ?? throw new InvalidOperationException($"macro assembly for {method.TypeName} has no file on disk");
                var assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => !a.IsDynamic && string.Equals(a.Location, path, StringComparison.OrdinalIgnoreCase))
                    ?? Assembly.LoadFrom(path);
                MacroReflection.Invoke(assembly, method, context);
            }
        }
    }

    public sealed class MacroTestOptions
    {
        /// <summary>Assemblies the macro library and the source use. Assemblies already loaded in the process are added automatically; this mainly makes sure the macro library is loaded.</summary>
        public List<Assembly> References { get; set; } = new List<Assembly>();

        public List<string> Defines { get; set; } = new List<string>();
        public string LanguageVersion { get; set; } = "latest";
        public string Nullable { get; set; } = "enable";

        /// <summary>Compile-check the macro-generated code after expansion (HMC013).</summary>
        public bool Verify { get; set; } = true;

        public string? StartupAttribute { get; set; }

        /// <summary>
        /// Runtime cleanup (remove unused usings that import macro libraries, find macro assemblies not needed at runtime; see <see cref="MacroTestResult.RuntimeUnused"/>).
        /// It needs an extra full semantic analysis and is slow, so it is off by default in tests.
        /// </summary>
        public bool RuntimeCleanup { get; set; }

        /// <summary>Only expand these macros (LOGGED, @LOGGED, Save or [@Save] all work); others stay as they are. Empty means all.</summary>
        public List<string> Macros { get; set; } = new List<string>();
    }

    public sealed class MacroTestResult
    {
        private readonly ExpandResult _result;
        private readonly List<SourceFile> _inputs;
        private readonly IReadOnlyList<string> _referencePaths;
        private readonly MacroTestOptions _options;
        private readonly List<string> _withHeaders = new List<string>();

        internal MacroTestResult(List<SourceFile> inputs, ExpandResult result, IReadOnlyList<string> referencePaths, MacroTestOptions options)
        {
            _result = result;
            _inputs = inputs;
            _referencePaths = referencePaths;
            _options = options;

            var outputs = new List<string>();
            foreach (var input in inputs)
            {
                var expanded = result.Files.FirstOrDefault(f => f.Id == input.Id);
                _withHeaders.Add(expanded?.Text ?? input.Text);
                outputs.Add(expanded == null ? input.Text : StripLineHeader(expanded.Text));
            }
            Outputs = outputs;
            Startup = result.GeneratedFiles.FirstOrDefault(f => f.Name == MacroExpander.StartupFileName)?.Text;
        }

        public IReadOnlyList<MacroDiagnostic> Diagnostics => _result.Diagnostics;
        public IEnumerable<MacroDiagnostic> Errors => Diagnostics.Where(d => d.Level == DiagnosticLevel.Error);
        public IEnumerable<MacroDiagnostic> Warnings => Diagnostics.Where(d => d.Level == DiagnosticLevel.Warning);
        public bool Success => !_result.HasErrors;

        /// <summary>Expanded text of each source file (the original text for files without macros).</summary>
        public IReadOnlyList<string> Outputs { get; }

        /// <summary>Expanded text of the first source file.</summary>
        public string Output => Outputs[0];

        /// <summary>The generated startup script; null when there is no startup code.</summary>
        public string? Startup { get; }

        /// <summary>Macro assemblies (disk paths) that runtime cleanup found not needed at runtime.</summary>
        public IReadOnlyList<string> RuntimeUnused => _result.RuntimeUnusedReferences;

        /// <summary>Record of every macro expansion: macro, location, source, output and formatted output.</summary>
        public IReadOnlyList<MacroExpansion> Expansions => _result.Expansions;

        /// <summary>
        /// Writes the expansion result to <paramref name="directory"/> (line-aligned sources + startup script + _expansions.txt report)
        /// to help debug problems after expansion. Works even when expansion fails. Returns the files written.
        /// </summary>
        public IReadOnlyList<string> DumpTo(string directory, params string[] filePatterns) =>
            MacroDump.Write(directory, _inputs, _result, filePatterns, _options.Macros);

        /// <summary>
        /// Line <paramref name="line"/> (starting at 1) after expansion. Expansion keeps line numbers unchanged,
        /// so this is what source line <paramref name="line"/> looks like after expansion.
        /// </summary>
        public string Line(int line, int file = 0)
        {
            var lines = Outputs[file].Split('\n');
            if (line < 1 || line > lines.Length)
                throw new MacroTestException($"line {line} is out of range: Test{file}.cs has {lines.Length} line(s)");
            return lines[line - 1].TrimEnd('\r');
        }

        /// <summary>Asserts that expansion succeeded (no errors at all).</summary>
        public MacroTestResult AssertSuccess()
        {
            if (!Success) throw new MacroTestException("expected macro expansion to succeed, but it reported errors:\n" + Describe() + "\n--- output ---\n" + Output);
            return this;
        }

        /// <summary>Asserts there is an error with the given code (optionally: its message contains some text, and it is on a given line). Returns that diagnostic.</summary>
        public MacroDiagnostic AssertError(string code, string? contains = null, int? line = null) =>
            AssertDiagnostic(DiagnosticLevel.Error, code, contains, line);

        public MacroDiagnostic AssertWarning(string code, string? contains = null, int? line = null) =>
            AssertDiagnostic(DiagnosticLevel.Warning, code, contains, line);

        /// <summary>Asserts there are no warnings.</summary>
        public MacroTestResult AssertNoWarnings()
        {
            if (Warnings.Any()) throw new MacroTestException("expected no warnings, but got:\n" + Describe());
            return this;
        }

        /// <summary>Compiles the expansion result (including the startup script) into an assembly and loads it, to test the runtime behavior of generated code. Startup code runs immediately.</summary>
        public CompiledMacroTest Compile()
        {
            AssertSuccess();

            var parseOptions = new CSharpParseOptions(
                LanguageVersionFacts.TryParse(_options.LanguageVersion, out var v) ? v : LanguageVersion.Preview,
                preprocessorSymbols: _options.Defines);
            var trees = _withHeaders.Select((t, i) => CSharpSyntaxTree.ParseText(t, parseOptions, $"Test{i}.expanded.cs")).ToList();
            if (Startup != null) trees.Add(CSharpSyntaxTree.ParseText(Startup, parseOptions, MacroExpander.StartupFileName));

            var compilation = CSharpCompilation.Create(
                "HowMacroTest_" + Guid.NewGuid().ToString("N"),
                trees,
                _referencePaths.Select(p => MetadataReference.CreateFromFile(p)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                    nullableContextOptions: Enum.TryParse<NullableContextOptions>(_options.Nullable, true, out var n) ? n : NullableContextOptions.Disable));

            using var stream = new MemoryStream();
            var emit = compilation.Emit(stream);
            if (!emit.Success)
            {
                var errors = string.Join("\n", emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
                throw new MacroTestException($"expanded code doesn't compile:\n{errors}\n--- expanded ---\n{string.Join("\n--- next file ---\n", Outputs)}");
            }

            var assembly = Assembly.Load(stream.ToArray());
            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle); // run the startup code right away
            return new CompiledMacroTest(assembly);
        }

        private MacroDiagnostic AssertDiagnostic(DiagnosticLevel level, string code, string? contains, int? line)
        {
            var match = Diagnostics.FirstOrDefault(d =>
                d.Level == level && d.Code == code &&
                (contains == null || d.Message.IndexOf(contains, StringComparison.Ordinal) >= 0) &&
                (line == null || d.Line == line));
            if (match != null) return match;

            var what = $"{level.ToString().ToLowerInvariant()} {code}" +
                       (contains != null ? $" containing \"{contains}\"" : "") +
                       (line != null ? $" on line {line}" : "");
            throw new MacroTestException($"expected {what}, but got:\n{Describe()}");
        }

        private string Describe() =>
            Diagnostics.Count == 0 ? "  (no diagnostics)" : string.Join("\n", Diagnostics.Select(d => "  " + d));

        private static string StripLineHeader(string text) =>
            text.StartsWith("#line 1 ", StringComparison.Ordinal) ? text.Substring(text.IndexOf('\n') + 1) : text;

        public override string ToString() => Describe() + "\n--- output ---\n" + Output;
    }

    /// <summary>The compiled and loaded expansion result, with simple reflection helpers to drive the generated code.</summary>
    public sealed class CompiledMacroTest
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        internal CompiledMacroTest(Assembly assembly) => Assembly = assembly;

        public Assembly Assembly { get; }

        public Type Type(string fullName) =>
            Assembly.GetType(fullName) ?? throw new MacroTestException($"type '{fullName}' not found in the expanded code");

        /// <summary>Creates an instance: <c>var p = compiled.Create("Player");</c></summary>
        public object Create(string typeName, params object?[] args) => Activator.CreateInstance(Type(typeName), args)!;

        /// <summary>Reads a field or property (pass an object for instances, a type name string for statics).</summary>
        public object? Get(object target, string member)
        {
            var (type, instance) = Resolve(target);
            return type.GetProperty(member, All)?.GetValue(instance)
                ?? (type.GetField(member, All) is FieldInfo f ? f.GetValue(instance) : MissingMember(type, member));
        }

        /// <summary>Writes a field or property (pass an object for instances, a type name string for statics).</summary>
        public void Set(object target, string member, object? value)
        {
            var (type, instance) = Resolve(target);
            if (type.GetProperty(member, All) is PropertyInfo p) p.SetValue(instance, value);
            else if (type.GetField(member, All) is FieldInfo f) f.SetValue(instance, value);
            else MissingMember(type, member);
        }

        /// <summary>Calls a method (pass an object for instances, a type name string for statics).</summary>
        public object? Call(object target, string method, params object?[] args)
        {
            var (type, instance) = Resolve(target);
            var m = type.GetMethods(All).FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length)
                    ?? throw new MacroTestException($"method '{method}' with {args.Length} argument(s) not found on {type.FullName}");
            try { return m.Invoke(instance, args); }
            catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
        }

        /// <summary>Whether the member is a field or a property after expansion: Member macros turn fields into properties.</summary>
        public bool IsProperty(string typeName, string member) => Type(typeName).GetProperty(member, All) != null;

        private (Type Type, object? Instance) Resolve(object target) =>
            target is string typeName ? (Type(typeName), null) : (target.GetType(), target);

        private static object? MissingMember(Type type, string member) =>
            throw new MacroTestException($"field or property '{member}' not found on {type.FullName}");
    }

    public sealed class MacroTestException : Exception
    {
        public MacroTestException(string message) : base(message) { }
    }
}
