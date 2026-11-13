using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using CSMacro.Engine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSMacro.Testing
{
    /// <summary>
    /// 在测试里展开一段源码：
    /// <code>
    /// var r = MacroTest.Expand(@"
    ///     using static Demo.Macros.M;
    ///     class Player { public int Gold = @LOGGED(100); }", typeof(M).Assembly);
    /// r.AssertSuccess();
    /// Assert.Contains("set {", r.Line(3));
    /// </code>
    /// 宏在测试进程内执行（测试项目本来就引用了宏库），不需要先构建项目。
    /// </summary>
    public static class MacroTest
    {
        /// <summary>展开一个源文件。<paramref name="references"/> 填宏库（以及源码用到的其他程序集），例如 <c>typeof(M).Assembly</c>。</summary>
        public static MacroTestResult Expand(string source, params Assembly[] references) =>
            Expand(new[] { source }, new MacroTestOptions { References = references.ToList() });

        public static MacroTestResult Expand(string source, MacroTestOptions options) =>
            Expand(new[] { source }, options);

        /// <summary>展开多个源文件（文件名依次为 Test0.cs、Test1.cs……）。</summary>
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

        /// <summary>运行 <paramref name="action"/> 并返回它写到控制台的内容（测 LOG、LOGGED 这类打日志的宏）。
        /// 会临时替换 Console.Out，测试框架并行跑测试时请关闭并行。</summary>
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
        /// 编译引用：运行时框架 + 当前进程已加载的程序集 + 显式指定的程序集。
        /// 显式指定可以确保宏库已加载（.NET 按需加载程序集）。
        /// </summary>
        private static IReadOnlyList<string> ReferencePaths(IEnumerable<Assembly> explicitReferences)
        {
            var paths = new List<string>();
            if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
                paths.AddRange(tpa.Split(Path.PathSeparator));

            paths.AddRange(explicitReferences.Concat(AppDomain.CurrentDomain.GetAssemblies())
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Select(a => a.Location));

            // 同名程序集只保留一个，优先保留框架版本
            return paths
                .Where(File.Exists)
                .GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();
        }

        /// <summary>宏库已在测试进程里：按路径找到已加载的程序集直接调用，CSMacro.Abstractions 自然是同一份。</summary>
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
        /// <summary>宏库和源码用到的程序集。当前进程已加载的程序集会自动加入，这里填的主要是确保宏库被加载。</summary>
        public List<Assembly> References { get; set; } = new List<Assembly>();

        public List<string> Defines { get; set; } = new List<string>();
        public string LanguageVersion { get; set; } = "latest";
        public string Nullable { get; set; } = "enable";

        /// <summary>展开后编译检查宏生成的代码（CSM013）。</summary>
        public bool Verify { get; set; } = true;

        public string? StartupAttribute { get; set; }

        /// <summary>
        /// 运行时清理（去掉导入宏库的无用 using、判定运行时不需要的宏程序集，见 <see cref="MacroTestResult.RuntimeUnused"/>）。
        /// 要额外做完整的语义分析，比较慢，测试里默认关闭。
        /// </summary>
        public bool RuntimeCleanup { get; set; }

        /// <summary>只展开这些宏（LOGGED、@LOGGED、Save、[@Save] 都可以），其余保持原样；为空表示全部。</summary>
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

        /// <summary>每个源文件展开后的文本（没有宏的文件就是原文）。</summary>
        public IReadOnlyList<string> Outputs { get; }

        /// <summary>第一个源文件展开后的文本。</summary>
        public string Output => Outputs[0];

        /// <summary>汇总启动代码的辅助脚本；没有启动代码时为 null。</summary>
        public string? Startup { get; }

        /// <summary>运行时清理判定为"运行时不需要"的宏程序集（磁盘路径）。</summary>
        public IReadOnlyList<string> RuntimeUnused => _result.RuntimeUnusedReferences;

        /// <summary>每一次宏展开的记录：宏、位置、源码、输出、格式化后的输出。</summary>
        public IReadOnlyList<MacroExpansion> Expansions => _result.Expansions;

        /// <summary>
        /// 把展开结果写到 <paramref name="directory"/>（逐行对齐的源文件 + 辅助脚本 + _expansions.txt 报告），
        /// 用来排查展开后的问题。展开出错时也能写。返回写出的文件。
        /// </summary>
        public IReadOnlyList<string> DumpTo(string directory, params string[] filePatterns) =>
            MacroDump.Write(directory, _inputs, _result, filePatterns, _options.Macros);

        /// <summary>
        /// 展开后第 <paramref name="line"/> 行（从 1 开始）。宏展开保持行号不变，
        /// 所以这就是源码第 <paramref name="line"/> 行展开后的样子。
        /// </summary>
        public string Line(int line, int file = 0)
        {
            var lines = Outputs[file].Split('\n');
            if (line < 1 || line > lines.Length)
                throw new MacroTestException($"line {line} is out of range: Test{file}.cs has {lines.Length} line(s)");
            return lines[line - 1].TrimEnd('\r');
        }

        /// <summary>断言展开成功（没有任何错误）。</summary>
        public MacroTestResult AssertSuccess()
        {
            if (!Success) throw new MacroTestException("expected macro expansion to succeed, but it reported errors:\n" + Describe() + "\n--- output ---\n" + Output);
            return this;
        }

        /// <summary>断言有一个指定诊断码的错误（可选：消息包含某段文字、在某一行）。返回这个诊断。</summary>
        public MacroDiagnostic AssertError(string code, string? contains = null, int? line = null) =>
            AssertDiagnostic(DiagnosticLevel.Error, code, contains, line);

        public MacroDiagnostic AssertWarning(string code, string? contains = null, int? line = null) =>
            AssertDiagnostic(DiagnosticLevel.Warning, code, contains, line);

        /// <summary>断言没有任何警告。</summary>
        public MacroTestResult AssertNoWarnings()
        {
            if (Warnings.Any()) throw new MacroTestException("expected no warnings, but got:\n" + Describe());
            return this;
        }

        /// <summary>把展开结果（含辅助脚本）编译成程序集并加载，用来测试宏生成代码的运行行为。启动代码会立即执行。</summary>
        public CompiledMacroTest Compile()
        {
            AssertSuccess();

            var parseOptions = new CSharpParseOptions(
                LanguageVersionFacts.TryParse(_options.LanguageVersion, out var v) ? v : LanguageVersion.Preview,
                preprocessorSymbols: _options.Defines);
            var trees = _withHeaders.Select((t, i) => CSharpSyntaxTree.ParseText(t, parseOptions, $"Test{i}.expanded.cs")).ToList();
            if (Startup != null) trees.Add(CSharpSyntaxTree.ParseText(Startup, parseOptions, MacroExpander.StartupFileName));

            var compilation = CSharpCompilation.Create(
                "CSMacroTest_" + Guid.NewGuid().ToString("N"),
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
            RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle); // 立即执行启动代码
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

    /// <summary>编译加载后的展开结果，提供简单的反射辅助方法来驱动生成的代码。</summary>
    public sealed class CompiledMacroTest
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        internal CompiledMacroTest(Assembly assembly) => Assembly = assembly;

        public Assembly Assembly { get; }

        public Type Type(string fullName) =>
            Assembly.GetType(fullName) ?? throw new MacroTestException($"type '{fullName}' not found in the expanded code");

        /// <summary>创建一个实例：<c>var p = compiled.Create("Player");</c></summary>
        public object Create(string typeName, params object?[] args) => Activator.CreateInstance(Type(typeName), args)!;

        /// <summary>读字段或属性（实例传对象，静态传类型名字符串）。</summary>
        public object? Get(object target, string member)
        {
            var (type, instance) = Resolve(target);
            return type.GetProperty(member, All)?.GetValue(instance)
                ?? (type.GetField(member, All) is FieldInfo f ? f.GetValue(instance) : MissingMember(type, member));
        }

        /// <summary>写字段或属性（实例传对象，静态传类型名字符串）。</summary>
        public void Set(object target, string member, object? value)
        {
            var (type, instance) = Resolve(target);
            if (type.GetProperty(member, All) is PropertyInfo p) p.SetValue(instance, value);
            else if (type.GetField(member, All) is FieldInfo f) f.SetValue(instance, value);
            else MissingMember(type, member);
        }

        /// <summary>调用方法（实例传对象，静态传类型名字符串）。</summary>
        public object? Call(object target, string method, params object?[] args)
        {
            var (type, instance) = Resolve(target);
            var m = type.GetMethods(All).FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length)
                    ?? throw new MacroTestException($"method '{method}' with {args.Length} argument(s) not found on {type.FullName}");
            try { return m.Invoke(instance, args); }
            catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
        }

        /// <summary>成员在展开后是字段还是属性：Member 宏会把字段变成属性。</summary>
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
