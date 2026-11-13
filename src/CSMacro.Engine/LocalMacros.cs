using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace CSMacro.Engine
{
    /// <summary>
    /// 项目内的宏：宏不必单独建一个宏库项目，可以直接写在项目自己的文件里（MSBuild 里用 CSMacroDefinition 指定）。
    /// <list type="bullet">
    /// <item>IDE 里这些文件和普通源码一样参与编译：宏照样能补全、跳转、类型检查。</item>
    /// <item>真正构建时，先把它们单独编译成一个临时程序集（展开时从这里加载宏），再从正式编译里拿掉。</item>
    /// <item>约束：宏文件先于项目编译，所以只能用 CSMacro 的 API 和项目引用的库，不能用本项目自己的类型。</item>
    /// </list>
    /// </summary>
    public static class LocalMacros
    {
        /// <summary>
        /// 编译宏文件到 <paramref name="outputPath"/>。内容不变时不重写文件。
        /// 返回诊断（有错误时不会产出程序集）。
        /// </summary>
        public static List<MacroDiagnostic> Compile(IReadOnlyList<SourceFile> files, ExpandOptions options, string assemblyName, string outputPath)
        {
            var parseOptions = new CSharpParseOptions(
                LanguageVersionFacts.TryParse(options.LanguageVersion, out var v) ? v : LanguageVersion.Preview,
                DocumentationMode.Parse,
                SourceCodeKind.Regular,
                options.Defines);

            var trees = files.Select(f => CSharpSyntaxTree.ParseText(SourceText.From(f.Text, Encoding.UTF8), parseOptions, f.Path)).ToArray();
            var compilation = CSharpCompilation.Create(
                assemblyName,
                trees,
                options.References.Where(File.Exists).Select(p => MetadataReference.CreateFromFile(p)),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true,
                    nullableContextOptions: System.Enum.TryParse<NullableContextOptions>(options.Nullable, true, out var n) ? n : NullableContextOptions.Disable));

            var diagnostics = new List<MacroDiagnostic>();
            using var stream = new MemoryStream();
            var emit = compilation.Emit(stream);
            foreach (var d in emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                var span = d.Location.GetLineSpan();
                var projectType = d.Id is "CS0246" or "CS0103" or "CS0234" or "CS0400";
                diagnostics.Add(new MacroDiagnostic(DiagnosticLevel.Error, "CSM018",
                    (projectType
                        ? "macro definition files are compiled before the rest of the project, so they can only use CSMacro and referenced libraries, not this project's own types: "
                        : "macro definition file doesn't compile: ") + $"{d.Id}: {d.GetMessage()}",
                    span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1));
            }
            if (!emit.Success) return diagnostics;

            var bytes = stream.ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            if (!File.Exists(outputPath) || !File.ReadAllBytes(outputPath).SequenceEqual(bytes))
                File.WriteAllBytes(outputPath, bytes);
            return diagnostics;
        }
    }
}
