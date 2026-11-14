using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.Loader;
using System.Text;
using HowMacro.Engine;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace HowMacro.Unity;

/// <summary>
/// Entry point called (via reflection) by the HowMacro ILPostProcessor inside Unity.
/// Unity has already compiled the unexpanded sources (they are valid C# by design); this reads the sources and
/// compile options back from that assembly's portable PDB, expands the macros and recompiles with Roslyn.
/// Only primitive types cross the boundary, because the caller lives in a different AssemblyLoadContext.
/// </summary>
public static class Bridge
{
    // Portable PDB custom debug information kinds (see the Portable PDB spec)
    static readonly Guid EmbeddedSourceKind = new("0E8A571B-6926-466E-B4AD-8AB04611F5FE");
    static readonly Guid CompilationOptionsKind = new("B5FEEC05-8CD0-4A83-96DA-466284BB4BD8");

    /// <summary>
    /// Returns null when nothing in the assembly needs expanding (keep Unity's assembly as is).
    /// Otherwise [byte[] pe (null on error), byte[] pdb, string[] diagnostics, string[] log];
    /// a diagnostic is "error|warning \t file \t line \t column \t message".
    /// </summary>
    public static object[]? Process(string assemblyName, byte[] pe, byte[] pdb, string[] references, string[] defines, string projectDir)
    {
        var log = new List<string>();
        var diagnostics = new List<string>();

        var refs = references.Select(r => Path.IsPathRooted(r) ? r : Path.GetFullPath(Path.Combine(projectDir, r))).ToList();
        // Cheap check first: Unity runs this for every assembly that references HowMacro.Abstractions, which (as an
        // auto-referenced plugin) includes every package. Only assemblies that can see a macro library can contain macro calls.
        if (!refs.Any(IsMacroLibrary)) return null;

        var (documents, compileOptions) = ReadPdb(pdb);
        log.Add($"{assemblyName}: {documents.Count} document(s); options: {string.Join(", ", compileOptions.Select(kv => kv.Key + "=" + kv.Value))}");

        var sources = new List<SourceFile>();
        var generated = new List<(string Name, string Text)>(); // e.g. Unity's own source generator output, embedded in the PDB
        foreach (var (name, embedded) in documents)
        {
            if (!name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
            var full = Path.IsPathRooted(name) ? name : Path.GetFullPath(Path.Combine(projectDir, name));
            if (File.Exists(full)) sources.Add(new SourceFile(name, full, File.ReadAllText(full)));
            else if (embedded != null) generated.Add((name, embedded));
            else log.Add($"  missing source, not embedded either: {name}");
        }

        compileOptions.TryGetValue("language-version", out var langVersion);
        compileOptions.TryGetValue("nullable", out var nullable);
        var options = new ExpandOptions
        {
            References = refs,
            Defines = defines,
            LanguageVersion = langVersion,
            Nullable = nullable,
            OutputType = "Library",
            // Earliest point Unity offers, so registrations are in place before any user code (including AfterSceneLoad / Awake) runs
            StartupAttribute = "global::UnityEngine.RuntimeInitializeOnLoadMethod(global::UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)",
        };

        using var invoker = new UnityInvoker(refs);
        var result = MacroExpander.Expand(sources, options, invoker);
        foreach (var d in result.Diagnostics)
            diagnostics.Add(string.Join("\t", d.Level == DiagnosticLevel.Error ? "error" : "warning", d.FilePath, d.Line, d.Column, $"{d.Code}: {d.Message}"));
        if (result.HasErrors) return new object[] { null!, null!, diagnostics.ToArray(), log.ToArray() };
        if (result.Files.Count == 0 && result.GeneratedFiles.Count == 0) return null;
        log.Add($"  expanded {result.Files.Count} of {sources.Count} file(s)" +
                (result.GeneratedFiles.Count > 0 ? $", generated {string.Join(", ", result.GeneratedFiles.Select(f => f.Name))}" : ""));

        // Recompile: expanded text where a file changed, the original elsewhere. Same document names as Unity used,
        // so the new PDB points at the user's original files (expansion keeps line numbers).
        var parseOptions = new CSharpParseOptions(ParseLanguageVersion(langVersion), DocumentationMode.Parse, preprocessorSymbols: defines);
        var expanded = result.Files.ToDictionary(f => f.Id, f => f.Text);
        var trees = new List<SyntaxTree>();
        var embeddedTexts = new List<EmbeddedText>();
        foreach (var s in sources)
            trees.Add(Parse(expanded.TryGetValue(s.Id, out var text) ? text : s.Text, s.Id, parseOptions, embed: false, embeddedTexts));
        foreach (var g in generated)
            trees.Add(Parse(g.Text, g.Name, parseOptions, embed: true, embeddedTexts));
        foreach (var g in result.GeneratedFiles)
            trees.Add(Parse(g.Text, Path.Combine("HowMacro.Generated", g.Name), parseOptions, embed: true, embeddedTexts));

        var compilation = CSharpCompilation.Create(assemblyName, trees,
            refs.Where(File.Exists).Select(r => MetadataReference.CreateFromFile(r)),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                allowUnsafe: true,
                optimizationLevel: compileOptions.TryGetValue("optimization", out var opt) && opt.StartsWith("release", StringComparison.OrdinalIgnoreCase)
                    ? OptimizationLevel.Release : OptimizationLevel.Debug,
                nullableContextOptions: ParseNullable(nullable),
                deterministic: true));

        using var peStream = new MemoryStream();
        using var pdbStream = new MemoryStream();
        var emit = compilation.Emit(peStream, pdbStream,
            options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb, pdbFilePath: assemblyName + ".pdb"),
            embeddedTexts: embeddedTexts);
        foreach (var d in emit.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
        {
            var span = d.Location.GetMappedLineSpan();
            diagnostics.Add(string.Join("\t", "error", span.Path, span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1,
                $"HMC013: recompiling the expanded code failed: {d.Id}: {d.GetMessage()}"));
        }
        if (!emit.Success) return new object[] { null!, null!, diagnostics.ToArray(), log.ToArray() };
        return new object[] { peStream.ToArray(), pdbStream.ToArray(), diagnostics.ToArray(), log.ToArray() };
    }

    static readonly Dictionary<string, (DateTime Stamp, bool IsMacroLibrary)> s_macroLibraries = new();

    /// <summary>A macro library is any assembly (other than the API itself) that references HowMacro.Abstractions.</summary>
    static bool IsMacroLibrary(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name == "HowMacro.Abstractions" || name.StartsWith("Unity", StringComparison.Ordinal) || name.StartsWith("System", StringComparison.Ordinal)
            || name is "mscorlib" or "netstandard" || !File.Exists(path)) return false;
        var stamp = File.GetLastWriteTimeUtc(path);
        lock (s_macroLibraries)
            if (s_macroLibraries.TryGetValue(path, out var cached) && cached.Stamp == stamp) return cached.IsMacroLibrary;
        bool result;
        try
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(File.ReadAllBytes(path)));
            var md = pe.GetMetadataReader();
            result = md.AssemblyReferences.Any(h => md.GetString(md.GetAssemblyReference(h).Name) == "HowMacro.Abstractions");
        }
        catch (BadImageFormatException) { result = false; }
        lock (s_macroLibraries) s_macroLibraries[path] = (stamp, result);
        return result;
    }

    static SyntaxTree Parse(string text, string path, CSharpParseOptions options, bool embed, List<EmbeddedText> embeddedTexts)
    {
        var source = SourceText.From(text, Encoding.UTF8);
        if (embed) embeddedTexts.Add(EmbeddedText.FromSource(path, source));
        return CSharpSyntaxTree.ParseText(source, options, path);
    }

    static LanguageVersion ParseLanguageVersion(string? value) =>
        value != null && LanguageVersionFacts.TryParse(value, out var v) ? v : LanguageVersion.CSharp9;

    static NullableContextOptions ParseNullable(string? value) =>
        Enum.TryParse<NullableContextOptions>(value, ignoreCase: true, out var n) ? n : NullableContextOptions.Disable;

    /// <summary>Document names (with embedded text when present) and the compiler's recorded options.</summary>
    static (List<(string Name, string? Embedded)> Documents, Dictionary<string, string> Options) ReadPdb(byte[] pdb)
    {
        using var provider = MetadataReaderProvider.FromPortablePdbImage(System.Collections.Immutable.ImmutableArray.Create(pdb));
        var reader = provider.GetMetadataReader();
        var documents = new List<(string, string?)>();
        foreach (var handle in reader.Documents)
        {
            string? embedded = null;
            foreach (var cdi in reader.GetCustomDebugInformation(handle))
            {
                var info = reader.GetCustomDebugInformation(cdi);
                if (reader.GetGuid(info.Kind) == EmbeddedSourceKind) embedded = DecodeEmbedded(reader.GetBlobBytes(info.Value));
            }
            documents.Add((reader.GetString(reader.GetDocument(handle).Name), embedded));
        }

        var options = new Dictionary<string, string>();
        foreach (var cdi in reader.GetCustomDebugInformation(EntityHandle.ModuleDefinition))
        {
            var info = reader.GetCustomDebugInformation(cdi);
            if (reader.GetGuid(info.Kind) != CompilationOptionsKind) continue;
            var parts = Encoding.UTF8.GetString(reader.GetBlobBytes(info.Value)).Split('\0');
            for (var i = 0; i + 1 < parts.Length; i += 2) options[parts[i]] = parts[i + 1];
        }
        return (documents, options);
    }

    static string DecodeEmbedded(byte[] blob)
    {
        var format = BitConverter.ToInt32(blob, 0);
        if (format == 0) return Encoding.UTF8.GetString(blob, 4, blob.Length - 4);
        using var deflate = new DeflateStream(new MemoryStream(blob, 4, blob.Length - 4), CompressionMode.Decompress);
        using var sr = new StreamReader(deflate, Encoding.UTF8);
        return sr.ReadToEnd();
    }

    /// <summary>Loads macro assemblies in a collectible context per run, so a changed macro assembly is picked up next time.</summary>
    sealed class UnityInvoker : IMacroInvoker, IDisposable
    {
        readonly MacroLoadContext _alc;
        readonly Dictionary<string, Assembly> _assemblies = new(StringComparer.OrdinalIgnoreCase);

        public UnityInvoker(IEnumerable<string> referencePaths) => _alc = new MacroLoadContext(referencePaths);

        public void Invoke(MacroMethod m, MacroContext context)
        {
            var path = m.AssemblyPath ?? throw new InvalidOperationException($"macro assembly for {m.TypeName} has no file on disk");
            if (!_assemblies.TryGetValue(path, out var asm))
                // From bytes: Unity rewrites the file in place, and loading from a path would keep it locked on Windows
                _assemblies[path] = asm = _alc.LoadFromStream(new MemoryStream(File.ReadAllBytes(path)));
            MacroReflection.Invoke(asm, m, context);
        }

        public void Dispose() => _alc.Unload();

        sealed class MacroLoadContext : AssemblyLoadContext
        {
            static readonly Assembly Abstractions = typeof(HowMacro.Macro).Assembly;
            readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase);

            // The runtime's own assemblies (netstandard, mscorlib, System.*): Unity's compile references include
            // reference-only copies of these, which must never be loaded for execution
            static readonly HashSet<string> Framework = new(
                ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "")
                    .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(Path.GetFileNameWithoutExtension)!,
                StringComparer.OrdinalIgnoreCase);

            public MacroLoadContext(IEnumerable<string> referencePaths) : base("HowMacro.Macros", isCollectible: true)
            {
                foreach (var p in referencePaths) _byName.TryAdd(Path.GetFileNameWithoutExtension(p), p);
            }

            protected override Assembly? Load(AssemblyName name)
            {
                // The engine's copy of the API, so macros and engine share the static Macro context
                if (name.Name == Abstractions.GetName().Name) return Abstractions;
                // Unity's engine assemblies etc. are not loadable on .NET 6 and macros shouldn't need them at expansion time;
                // other user assemblies come from the compile references
                if (name.Name != null && !Framework.Contains(name.Name)
                    && !name.Name.StartsWith("UnityEngine", StringComparison.Ordinal) && !name.Name.StartsWith("UnityEditor", StringComparison.Ordinal)
                    && _byName.TryGetValue(name.Name, out var path) && File.Exists(path))
                    return LoadFromStream(new MemoryStream(File.ReadAllBytes(path)));
                return null;
            }
        }
    }
}
