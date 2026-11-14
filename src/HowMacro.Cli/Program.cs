using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using HowMacro;
using HowMacro.Engine;

// Usage: howmacro @args.rsp
//   -s:<source>     a source file to compile (MSBuild Compile item)
//   -r:<assembly>   a compile reference (MSBuild ReferencePath); macro assemblies are found here too
//   -d:<A;B;C>      preprocessor symbols
//   --langversion:<v>
//   --nullable:<enable|disable|warnings|annotations>
//   --output-type:<Exe|WinExe|Library>
//   --out:<dir>         expanded files are written here
//   --originals:<file>  manifest: original Compile items that were replaced
//   --expanded:<file>   manifest: expanded files swapped in (including the generated startup script)
//   --runtime-unused:<file>  manifest: macro assemblies no longer needed at runtime (MSBuild removes them from references and output)
//   --startup-attribute:<attr>  attribute on the generated startup script's entry point (default: ModuleInitializer)
//   --no-verify         skip compiling macro-generated code for checks
//   --dump:<dir>           also write the expanded result here (line-aligned sources + startup script + _expansions.txt report) for debugging
//   --dump-files:<a;b>     only write these files (relative paths or file names, * and ** supported)
//   --dump-macros:<a;b>    only expand these macros in the dump, leave the rest as is (does not affect what is actually compiled)
//   -m:<file>              an in-project macro definition file (MSBuild HowMacroDefinition): compiled first on its own, then removed from the real compilation
//   --local-macros-out:<dll>  path of the temporary assembly the in-project macros are compiled into

var sources = new List<string>();
var references = new List<string>();
var defines = new List<string>();
string? outDir = null, originalsFile = null, expandedFile = null, langVersion = null, startupAttribute = null, nullable = null, outputType = null;
var verify = true;
string? dumpDir = null, runtimeUnusedFile = null;
List<string>? dumpFiles = null, dumpMacros = null;
var macroDefinitions = new List<string>();
string? localMacrosOut = null;

foreach (var arg in ExpandResponseFiles(args))
{
    if (TryValue(arg, "-s:", out var v)) sources.Add(v);
    else if (TryValue(arg, "-r:", out v)) references.Add(v);
    else if (TryValue(arg, "-m:", out v)) macroDefinitions.Add(v);
    else if (TryValue(arg, "--local-macros-out:", out v)) localMacrosOut = v;
    else if (TryValue(arg, "-d:", out v)) defines.AddRange(v.Split(';', ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    else if (TryValue(arg, "--langversion:", out v)) langVersion = v;
    else if (TryValue(arg, "--nullable:", out v)) nullable = v;
    else if (TryValue(arg, "--output-type:", out v)) outputType = v;
    else if (TryValue(arg, "--out:", out v)) outDir = v;
    else if (TryValue(arg, "--originals:", out v)) originalsFile = v;
    else if (TryValue(arg, "--expanded:", out v)) expandedFile = v;
    else if (TryValue(arg, "--startup-attribute:", out v)) startupAttribute = v;
    else if (arg == "--no-verify") verify = false;
    else if (TryValue(arg, "--runtime-unused:", out v)) runtimeUnusedFile = v;
    else if (TryValue(arg, "--dump:", out v)) dumpDir = v.Length > 0 ? v : null;
    else if (TryValue(arg, "--dump-files:", out v)) dumpFiles = SplitList(v);
    else if (TryValue(arg, "--dump-macros:", out v)) dumpMacros = SplitList(v);
    else if (arg.Length > 0) { Console.Error.WriteLine($"howmacro: unknown argument '{arg}'"); return 2; }
}

if (outDir == null || originalsFile == null || expandedFile == null)
{
    Console.Error.WriteLine("howmacro: --out, --originals and --expanded are required");
    return 2;
}

// Safety net: copies inside a dump folder (marked by a marker file) must never be treated as sources again; remove them from the compilation
var dumpCopies = sources.Where(InsideDumpDirectory).ToList();

// In-project macro definition files: not expanded; compiled first into a temporary assembly (macros are loaded from it), then removed from the real compilation
var definitionPaths = new HashSet<string>(macroDefinitions.Select(Path.GetFullPath));
var definitionSources = sources.Where(s => definitionPaths.Contains(Path.GetFullPath(s))).ToList();

var inputs = sources
    .Except(dumpCopies)
    .Except(definitionSources)
    .Select(id => new SourceFile(id, Path.GetFullPath(id), File.ReadAllText(id)))
    .ToList();

if (macroDefinitions.Count > 0)
{
    if (localMacrosOut == null) { Console.Error.WriteLine("howmacro: -m requires --local-macros-out"); return 2; }
    localMacrosOut = Path.GetFullPath(localMacrosOut);
    var definitions = macroDefinitions.Select(f => new SourceFile(f, Path.GetFullPath(f), File.ReadAllText(f))).ToList();
    var localDiagnostics = LocalMacros.Compile(definitions, MakeOptions(null), Path.GetFileNameWithoutExtension(localMacrosOut), localMacrosOut);
    foreach (var d in localDiagnostics) Console.WriteLine(d);
    if (localDiagnostics.Any(d => d.Level == DiagnosticLevel.Error)) return 1;
    references.Add(localMacrosOut);
}

var invoker = new ReflectionInvoker(references);
ExpandOptions MakeOptions(IReadOnlyCollection<string>? macros) => new ExpandOptions
    {
        Macros = macros,
        References = references,
        Defines = defines,
        LanguageVersion = langVersion,
        Nullable = nullable,
        OutputType = outputType,
        Verify = verify,
        StartupAttribute = string.IsNullOrWhiteSpace(startupAttribute) ? new ExpandOptions().StartupAttribute : startupAttribute,
    };

var result = MacroExpander.Expand(inputs, MakeOptions(null), invoker);
foreach (var d in result.Diagnostics) Console.WriteLine(d);

// The dump is just an extra copy for humans and doesn't affect what is compiled; it is written even when expansion fails, to help debugging.
if (dumpDir != null)
{
    var dumpResult = dumpMacros is { Count: > 0 } ? MacroExpander.Expand(inputs, MakeOptions(dumpMacros), invoker) : result;
    try
    {
        var written = MacroDump.Write(dumpDir, inputs, dumpResult, dumpFiles, dumpMacros);
        Console.WriteLine($"howmacro: dumped {written.Count} file(s) to {Path.GetFullPath(dumpDir)} (report: {MacroDump.ReportFileName})");
    }
    catch (IOException e)
    {
        Console.WriteLine($"howmacro: warning HMC020: dump failed: {e.Message}");
    }
}

if (result.HasErrors) return 1;

// Macro definition files exist only at compile time: expanded code must not depend on them (e.g. by using a plain helper type from a macro file)
if (localMacrosOut != null && result.RuntimeAnalysisReliable && !result.RuntimeUnusedReferences.Any(p => Path.GetFullPath(p) == localMacrosOut))
{
    Console.WriteLine($"{Path.GetFullPath(macroDefinitions[0])}(1,1): error HMC019: code in this project still uses types from the macro definition files " +
                      $"({string.Join(", ", macroDefinitions.Select(Path.GetFileName))}). Macro definition files exist only at compile time; " +
                      "move shared helpers into a regular source file, or into a separate macro library");
    return 1;
}

Directory.CreateDirectory(outDir);
var originals = new List<string>();
var expanded = new List<string>();
foreach (var file in result.Files)
{
    var dest = Path.GetFullPath(Path.Combine(outDir, SafeRelativePath(file.Id)));
    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
    WriteIfChanged(dest, file.Text); // keep the timestamp when content is unchanged so CoreCompile's incremental build still works
    originals.Add(file.Id);
    expanded.Add(dest);
}
originals.AddRange(dumpCopies);        // listed only in the originals manifest = removed from @(Compile)
originals.AddRange(definitionSources); // macro definition files are removed from the real compilation too
foreach (var file in result.GeneratedFiles)
{
    var dest = Path.GetFullPath(Path.Combine(outDir, "generated", file.Name));
    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
    WriteIfChanged(dest, file.Text);
    expanded.Add(dest);
}
WriteIfChanged(originalsFile, string.Join('\n', originals));
WriteIfChanged(expandedFile, string.Join('\n', expanded));
if (runtimeUnusedFile != null) WriteIfChanged(runtimeUnusedFile, string.Join('\n', result.RuntimeUnusedReferences));

Console.WriteLine($"howmacro: expanded macros in {result.Files.Count} of {inputs.Count} file(s)" +
                  (result.GeneratedFiles.Count > 0 ? $", generated {string.Join(", ", result.GeneratedFiles.Select(f => f.Name))}" : "") +
                  (result.RuntimeUnusedReferences.Count > 0
                      ? $"; not needed at runtime: {string.Join(", ", result.RuntimeUnusedReferences.Select(Path.GetFileNameWithoutExtension))}"
                      : ""));
return 0;

static bool InsideDumpDirectory(string id)
{
    var root = Directory.GetCurrentDirectory();
    for (var dir = Path.GetDirectoryName(Path.GetFullPath(id)); dir != null && dir.StartsWith(root, StringComparison.Ordinal); dir = Path.GetDirectoryName(dir))
        if (File.Exists(Path.Combine(dir, MacroDump.MarkerFileName))) return true;
    return false;
}

// Lists may be separated by ; , or spaces (; and , are special in -p: on the command line, so spaces are easiest: "LOGGED Save")
static List<string> SplitList(string value) =>
    value.Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

static bool TryValue(string arg, string prefix, out string value)
{
    if (arg.StartsWith(prefix, StringComparison.Ordinal)) { value = arg[prefix.Length..]; return true; }
    value = "";
    return false;
}

static IEnumerable<string> ExpandResponseFiles(IEnumerable<string> args)
{
    foreach (var a in args)
    {
        if (a.StartsWith('@'))
            foreach (var line in File.ReadAllLines(a[1..])) yield return line.Trim();
        else
            yield return a;
    }
}

static string SafeRelativePath(string id)
{
    if (!Path.IsPathRooted(id) && !id.Split('/', '\\').Contains("..")) return id;
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..8];
    return $"{Path.GetFileNameWithoutExtension(id)}.{hash}.cs";
}

static void WriteIfChanged(string path, string text)
{
    if (File.Exists(path) && File.ReadAllText(path) == text) return;
    File.WriteAllText(path, text);
}

/// <summary>Loads macro assemblies in an isolated load context and invokes macro methods via reflection.</summary>
sealed class ReflectionInvoker : IMacroInvoker
{
    private readonly MacroLoadContext _alc;
    private readonly Dictionary<string, Assembly> _assemblies = new(StringComparer.OrdinalIgnoreCase);

    public ReflectionInvoker(IEnumerable<string> referencePaths) => _alc = new MacroLoadContext(referencePaths);

    public void Invoke(MacroMethod m, MacroContext context)
    {
        var path = m.AssemblyPath
            ?? throw new InvalidOperationException($"macro assembly for {m.TypeName} has no file on disk");
        if (!_assemblies.TryGetValue(path, out var asm))
            _assemblies[path] = asm = _alc.LoadFromAssemblyPath(path);

        MacroReflection.Invoke(asm, m, context);
    }

    /// <summary>
    /// Framework assemblies and HowMacro.Abstractions are left to the default context (so the static Macro context is shared);
    /// other dependencies are resolved by name from the compile references.
    /// </summary>
    private sealed class MacroLoadContext : AssemblyLoadContext
    {
        private readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase);

        public MacroLoadContext(IEnumerable<string> referencePaths) : base("HowMacro", isCollectible: true)
        {
            foreach (var p in referencePaths) _byName.TryAdd(Path.GetFileNameWithoutExtension(p), p);
            Resolving += (_, name) =>
                name.Name != null && _byName.TryGetValue(name.Name, out var path) && File.Exists(path)
                    ? LoadFromAssemblyPath(path)
                    : null;
        }

        protected override Assembly? Load(AssemblyName name) => null;
    }
}
