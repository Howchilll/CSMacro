using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using CSMacro;
using CSMacro.Engine;

// 用法：csmacro @args.rsp
//   -s:<源文件>     参与编译的源文件（MSBuild Compile 项）
//   -r:<程序集>     编译引用（MSBuild ReferencePath），宏程序集也从这里找
//   -d:<A;B;C>      预处理符号
//   --langversion:<v>
//   --nullable:<enable|disable|warnings|annotations>
//   --output-type:<Exe|WinExe|Library>
//   --out:<目录>        展开后的文件写到这里
//   --originals:<文件>  清单：被替换掉的原 Compile 项
//   --expanded:<文件>   清单：替换进来的展开文件（含引擎生成的辅助脚本）
//   --runtime-unused:<文件>  清单：运行时已经不需要的宏程序集（MSBuild 据此从引用和输出中移除）
//   --startup-attribute:<特性>  启动辅助脚本入口上的特性（默认 ModuleInitializer）
//   --no-verify         不对宏生成的代码做编译检查
//   --dump:<目录>          另外把展开结果写到这个目录（逐行对齐的源文件 + 辅助脚本 + _expansions.txt 报告），用来排查问题
//   --dump-files:<a;b>     只写出这些文件（相对路径或文件名，支持 * 和 **）
//   --dump-macros:<a;b>    dump 时只展开这些宏，其余保持原样（不影响真正参与编译的结果）

var sources = new List<string>();
var references = new List<string>();
var defines = new List<string>();
string? outDir = null, originalsFile = null, expandedFile = null, langVersion = null, startupAttribute = null, nullable = null, outputType = null;
var verify = true;
string? dumpDir = null, runtimeUnusedFile = null;
List<string>? dumpFiles = null, dumpMacros = null;

foreach (var arg in ExpandResponseFiles(args))
{
    if (TryValue(arg, "-s:", out var v)) sources.Add(v);
    else if (TryValue(arg, "-r:", out v)) references.Add(v);
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
    else if (arg.Length > 0) { Console.Error.WriteLine($"csmacro: unknown argument '{arg}'"); return 2; }
}

if (outDir == null || originalsFile == null || expandedFile == null)
{
    Console.Error.WriteLine("csmacro: --out, --originals and --expanded are required");
    return 2;
}

// 保险：dump 目录（有标记文件）里的副本绝不能再被当成源码，从编译里拿掉
var dumpCopies = sources.Where(InsideDumpDirectory).ToList();
var inputs = sources
    .Except(dumpCopies)
    .Select(id => new SourceFile(id, Path.GetFullPath(id), File.ReadAllText(id)))
    .ToList();

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

// dump 只是另外写一份给人看，不影响真正参与编译的展开结果；展开出错时也照样写，方便排查。
if (dumpDir != null)
{
    var dumpResult = dumpMacros is { Count: > 0 } ? MacroExpander.Expand(inputs, MakeOptions(dumpMacros), invoker) : result;
    try
    {
        var written = MacroDump.Write(dumpDir, inputs, dumpResult, dumpFiles, dumpMacros);
        Console.WriteLine($"csmacro: dumped {written.Count} file(s) to {Path.GetFullPath(dumpDir)} (report: {MacroDump.ReportFileName})");
    }
    catch (IOException e)
    {
        Console.WriteLine($"csmacro: warning CSM020: dump failed: {e.Message}");
    }
}

if (result.HasErrors) return 1;

Directory.CreateDirectory(outDir);
var originals = new List<string>();
var expanded = new List<string>();
foreach (var file in result.Files)
{
    var dest = Path.GetFullPath(Path.Combine(outDir, SafeRelativePath(file.Id)));
    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
    WriteIfChanged(dest, file.Text); // 内容不变就不碰时间戳，让 CoreCompile 的增量编译还能生效
    originals.Add(file.Id);
    expanded.Add(dest);
}
originals.AddRange(dumpCopies); // 只出现在 originals 清单里 = 从 @(Compile) 中移除
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

Console.WriteLine($"csmacro: expanded macros in {result.Files.Count} of {inputs.Count} file(s)" +
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

// 列表可以用 ; , 或空格分隔（命令行 -p: 里 ; 和 , 有特殊含义，用空格最方便："LOGGED Save"）
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

/// <summary>在独立的加载上下文里加载宏程序集并反射调用宏方法。</summary>
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
    /// 框架程序集和 CSMacro.Abstractions 交给默认上下文（共享同一个 Macro 静态上下文），
    /// 其余依赖按名字从编译引用里找。
    /// </summary>
    private sealed class MacroLoadContext : AssemblyLoadContext
    {
        private readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase);

        public MacroLoadContext(IEnumerable<string> referencePaths) : base("CSMacro", isCollectible: true)
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
