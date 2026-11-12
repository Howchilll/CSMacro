using System.Collections.Generic;
using CSMacro;
using Microsoft.CodeAnalysis;

namespace CSMacro.Engine
{
    public sealed class SourceFile
    {
        public SourceFile(string id, string path, string text)
        {
            Id = id;
            Path = path;
            Text = text;
        }

        /// <summary>宿主用来识别文件的原始标识（MSBuild 里就是 Compile 项的 Identity）。</summary>
        public string Id { get; }

        /// <summary>原文件的绝对路径，会写进 <c>#line</c>，报错和断点都指回这里。</summary>
        public string Path { get; }

        public string Text { get; }
    }

    public sealed class ExpandedFile
    {
        public ExpandedFile(string id, string path, string text)
        {
            Id = id;
            Path = path;
            Text = text;
        }

        public string Id { get; }
        public string Path { get; }

        /// <summary>展开后的完整文本，首行为 <c>#line 1 "原路径"</c>。</summary>
        public string Text { get; }
    }

    /// <summary>引擎额外生成的文件（例如汇总启动代码的辅助脚本）。</summary>
    public sealed class GeneratedFile
    {
        public GeneratedFile(string name, string text)
        {
            Name = name;
            Text = text;
        }

        public string Name { get; }
        public string Text { get; }
    }

    public enum DiagnosticLevel { Warning, Error }

    public sealed class MacroDiagnostic
    {
        public MacroDiagnostic(DiagnosticLevel level, string code, string message, string filePath, int line, int column)
        {
            Level = level;
            Code = code;
            Message = message;
            FilePath = filePath;
            Line = line;
            Column = column;
        }

        public DiagnosticLevel Level { get; }
        public string Code { get; }
        public string Message { get; }
        public string FilePath { get; }
        public int Line { get; }
        public int Column { get; }

        /// <summary>MSBuild 规范格式，IDE 和 Exec 任务都能识别。</summary>
        public override string ToString() =>
            $"{FilePath}({Line},{Column}): {(Level == DiagnosticLevel.Error ? "error" : "warning")} {Code}: {Message}";
    }

    /// <summary>定位一个宏方法（或特性宏构造函数）所需的运行时信息。</summary>
    public sealed class MacroMethod
    {
        public const string Constructor = ".ctor";

        public MacroMethod(MetadataReference reference, string typeName, string methodName, int arity, int parameterCount)
        {
            Reference = reference;
            TypeName = typeName;
            MethodName = methodName;
            Arity = arity;
            ParameterCount = parameterCount;
        }

        /// <summary>
        /// 宏程序集的引用。命令行构建里是磁盘上的 DLL（<see cref="PortableExecutableReference"/>），
        /// IDE 里项目引用通常是内存中的编译（<see cref="CompilationReference"/>），需要宿主自己 Emit。
        /// </summary>
        public MetadataReference Reference { get; }

        /// <summary>引用对应的磁盘路径；内存中的项目引用为 null。</summary>
        public string? AssemblyPath => (Reference as PortableExecutableReference)?.FilePath;

        /// <summary>反射用的类型元数据名（嵌套类型用 +）。</summary>
        public string TypeName { get; }

        /// <summary>方法名；特性宏为 <see cref="Constructor"/>。</summary>
        public string MethodName { get; }

        public int Arity { get; }
        public int ParameterCount { get; }
    }

    /// <summary>
    /// 宿主负责真正执行宏（加载程序集、反射调用）。
    /// 引擎本身保持 netstandard2.0，不依赖 AssemblyLoadContext 之类的平台 API。
    /// </summary>
    public interface IMacroInvoker
    {
        /// <summary>在 <paramref name="context"/> 中执行宏；宏体内的异常直接抛出。</summary>
        void Invoke(MacroMethod method, MacroContext context);
    }

    public sealed class ExpandOptions
    {
        public IReadOnlyList<string> References { get; set; } = new string[0];
        public IReadOnlyList<string> Defines { get; set; } = new string[0];
        public string? LanguageVersion { get; set; }

        /// <summary>项目的 Nullable 设置（enable / disable / warnings / annotations），影响语义信息里的 IsNullable。</summary>
        public string? Nullable { get; set; }

        /// <summary>
        /// 启动辅助脚本的入口方法上加的特性。普通 .NET 用 ModuleInitializer；
        /// Unity 等宿主可以换成 <c>global::UnityEngine.RuntimeInitializeOnLoadMethod</c>。
        /// </summary>
        public string StartupAttribute { get; set; } = "global::System.Runtime.CompilerServices.ModuleInitializer";

        /// <summary>展开后对宏生成的代码做一次编译检查，出错时报出是哪个宏生成的。</summary>
        public bool Verify { get; set; } = true;

        /// <summary>
        /// 只展开这些宏（名字可写成 LOGGED、@LOGGED、Save、[@Save]）；null 表示全部。
        /// 用于排查问题：其余宏保持原样（它们本身就是合法的方法调用和特性）。
        /// 注意：没选中的特性宏不会执行，依赖它们片段的方法宏会拿到更少的片段。
        /// </summary>
        public IReadOnlyCollection<string>? Macros { get; set; }
    }

    /// <summary>一次宏展开的记录，用于展开报告和测试。</summary>
    public sealed class MacroExpansion
    {
        public MacroExpansion(string filePath, int line, string macro, string kind, string source, string output, string formatted)
        {
            FilePath = filePath;
            Line = line;
            Macro = macro;
            Kind = kind;
            Source = source;
            Output = output;
            Formatted = formatted;
        }

        public string FilePath { get; }
        public int Line { get; }

        /// <summary><c>@LOGGED</c> 或 <c>[@Save]</c>。</summary>
        public string Macro { get; }

        /// <summary>方法宏：Expression / Statement / Member；特性宏：Fragment:频道 / Startup。</summary>
        public string Kind { get; }

        /// <summary>被替换的源码（方法宏）或特性原文（特性宏）。</summary>
        public string Source { get; }

        /// <summary>实际写进展开文件的一行代码（方法宏）或产出的片段（特性宏）。</summary>
        public string Output { get; }

        /// <summary>格式化成多行的输出，方便阅读。</summary>
        public string Formatted { get; }
    }

    public sealed class ExpandResult
    {
        public ExpandResult(List<ExpandedFile> files, List<GeneratedFile> generatedFiles, List<MacroDiagnostic> diagnostics,
            IReadOnlyList<MacroExpansion>? expansions = null)
        {
            Expansions = expansions ?? new MacroExpansion[0];
            Files = files;
            GeneratedFiles = generatedFiles;
            Diagnostics = diagnostics;
        }

        /// <summary>只包含发生了展开的文件；没有宏的文件原样参与编译。</summary>
        public List<ExpandedFile> Files { get; }

        /// <summary>引擎额外生成、需要加入编译的文件。</summary>
        public List<GeneratedFile> GeneratedFiles { get; }

        public List<MacroDiagnostic> Diagnostics { get; }

        /// <summary>每一次宏展开的记录（按文件、行排序）。</summary>
        public IReadOnlyList<MacroExpansion> Expansions { get; }

        public bool HasErrors => Diagnostics.Exists(d => d.Level == DiagnosticLevel.Error);
    }

    /// <summary><see cref="MacroExpander.ExpandTrees"/> 的结果。</summary>
    public sealed class ExpansionResult
    {
        public ExpansionResult(
            IReadOnlyList<SyntaxTree> expanded,
            Dictionary<SyntaxTree, Dictionary<int, string>> expansionLines,
            string? startupCode,
            IReadOnlyList<MacroFragment> startupFragments,
            IReadOnlyList<MacroExpansion> expansions)
        {
            Expansions = expansions;
            Expanded = expanded;
            ExpansionLines = expansionLines;
            StartupCode = startupCode;
            StartupFragments = startupFragments;
        }

        /// <summary>与输入一一对应；没有宏的文件就是原树本身。</summary>
        public IReadOnlyList<SyntaxTree> Expanded { get; }

        /// <summary>每个展开后的树里，哪些行是宏生成的（行号 → 宏名）。</summary>
        public Dictionary<SyntaxTree, Dictionary<int, string>> ExpansionLines { get; }

        /// <summary>汇总启动代码的辅助脚本；没有启动代码时为 null。</summary>
        public string? StartupCode { get; }

        public IReadOnlyList<MacroFragment> StartupFragments { get; }

        public IReadOnlyList<MacroExpansion> Expansions { get; }
    }
}
