using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace HowMacro.Engine
{
    /// <summary>
    /// In-project macros: macros don't need a separate macro library project; they can live in the project's own files (selected with HowMacroDefinition in MSBuild).
    /// <list type="bullet">
    /// <item>In the IDE these files compile like any other source, so macros still get completion, go-to-definition and type checking.</item>
    /// <item>In a real build they are first compiled on their own into a temporary assembly (macros are loaded from it during expansion), then removed from the real compilation.</item>
    /// <item>Constraint: macro files are compiled before the project, so they can only use the HowMacro API and the project's references, not the project's own types.</item>
    /// </list>
    /// </summary>
    public static class LocalMacros
    {
        /// <summary>
        /// Compiles the macro files to <paramref name="outputPath"/>. The file is not rewritten when its content is unchanged.
        /// Returns diagnostics (no assembly is produced when there are errors).
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
                diagnostics.Add(new MacroDiagnostic(DiagnosticLevel.Error, "HMC018",
                    (projectType
                        ? "macro definition files are compiled before the rest of the project, so they can only use HowMacro and referenced libraries, not this project's own types: "
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
