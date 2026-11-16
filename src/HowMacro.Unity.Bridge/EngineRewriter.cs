using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace HowMacro.Unity;

/// <summary>
/// Makes Unity's engine assemblies safe to load while macros expand. Their managed parts (Vector3, Color, Mathf, ...) work
/// as they are, but a lot of the engine is native code that doesn't exist outside the Unity player/editor:
/// <list type="bullet">
/// <item>Calling an internal call (<c>extern</c> + <c>[MethodImpl(InternalCall)]</c>) or a P/Invoke fails, on .NET 6 with an
///   unhelpful SecurityException. They are given a body that throws <see cref="NativeEngineCallException"/> instead.</item>
/// <item>Unity's log (<c>Debug.Log</c>, <c>LogWarning</c>, <c>LogError</c>, <c>LogException</c>, ...) is the exception: its two
///   native entry points are rerouted to <see cref="EngineLog"/>, which reports the message as a compile warning at the
///   macro call site, so macro authors can print while they debug a macro.</item>
/// <item>Finalizers of engine classes (AnimationCurve, Gradient, ...) call native cleanup code on the finalizer thread, where
///   the failure is unhandled and kills the whole post processor process. They are emptied.</item>
/// </list>
/// The rewritten copy is cached under <c>Library/HowMacro/engine</c>, keyed by the original's content.
/// </summary>
public static class EngineRewriter
{
    /// <summary>Bump when the rewrite changes, so cached copies are rebuilt.</summary>
    const string Version = "2";

    /// <summary>Marks the exception thrown by rewritten native methods; see <see cref="NativeEngineCallException"/>.</summary>
    public const string Marker = "HowMacro.NativeEngineCall:";

    public static bool Applies(string assemblyName) =>
        assemblyName.StartsWith("UnityEngine", StringComparison.Ordinal) || assemblyName.StartsWith("UnityEditor", StringComparison.Ordinal);

    /// <summary>The rewritten assembly's bytes, from the cache when possible.</summary>
    public static byte[] Load(string path, string? cacheDir, IEnumerable<string>? searchDirectories = null)
    {
        var original = File.ReadAllBytes(path);
        string? cached = null;
        if (cacheDir != null)
        {
            var hash = Convert.ToHexString(SHA256.HashData(original), 0, 8).ToLowerInvariant();
            cached = Path.Combine(cacheDir, Path.GetFileNameWithoutExtension(path) + "-" + hash + "-v" + Version + ".dll");
            if (File.Exists(cached))
                try { return File.ReadAllBytes(cached); } catch (IOException) { }
        }

        var rewritten = Rewrite(original, new[] { Path.GetDirectoryName(path)! }.Concat(searchDirectories ?? Enumerable.Empty<string>()));

        if (cached != null)
            try
            {
                Directory.CreateDirectory(cacheDir!);
                var temp = cached + "." + Guid.NewGuid().ToString("N") + ".tmp"; // parallel ILPP runs may write the same file
                File.WriteAllBytes(temp, rewritten);
                File.Move(temp, cached, overwrite: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        return rewritten;
    }

    /// <param name="searchDirectories">where the assemblies it references are (writing some modules needs them)</param>
    public static byte[] Rewrite(byte[] original, IEnumerable<string>? searchDirectories = null)
    {
        using var resolver = new DefaultAssemblyResolver();
        foreach (var dir in (searchDirectories ?? Enumerable.Empty<string>()).Distinct()) resolver.AddSearchDirectory(dir);
        using var input = new MemoryStream(original);
        // deferred: custom attribute blobs are copied as is, without resolving the assemblies their argument types live in
        using var module = ModuleDefinition.ReadModule(input, new ReaderParameters { ReadSymbols = false, ReadingMode = ReadingMode.Deferred, AssemblyResolver = resolver });

        // new System.NotSupportedException(string), referenced through the module's own core library (mscorlib/netstandard)
        var exceptionType = new TypeReference("System", "NotSupportedException", module, module.TypeSystem.CoreLibrary);
        var exceptionCtor = new MethodReference(".ctor", module.TypeSystem.Void, exceptionType) { HasThis = true };
        exceptionCtor.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));

        var log = LogSink(module);

        foreach (var type in AllTypes(module))
            foreach (var method in type.Methods)
            {
                if (type.FullName == "UnityEngine.DebugLogHandler" && method.Name == "Internal_Log" && method.Parameters.Count == 4)
                    Forward(method, log.Message, 0, 2); // (LogType level, LogOption options, string msg, Object obj) -> Message(level, msg)
                else if (type.FullName == "UnityEngine.DebugLogHandler" && method.Name == "Internal_LogException" && method.Parameters.Count == 2)
                    Forward(method, log.Exception, 0); // (Exception ex, Object obj) -> Exception(ex)
                else if (method.IsInternalCall || method.IsPInvokeImpl || (method.IsRuntime && !type.IsDelegate()))
                    Throwing(method, type, exceptionCtor);
                else if (IsFinalizer(method) && method.HasBody)
                    Empty(method);
            }

        using var output = new MemoryStream();
        module.Write(output);
        return output.ToArray();
    }

    static void Throwing(MethodDefinition method, TypeDefinition type, MethodReference exceptionCtor)
    {
        method.IsInternalCall = false;
        method.IsRuntime = false;
        method.IsIL = true;
        method.IsManaged = true;
        if (method.IsPInvokeImpl)
        {
            method.IsPInvokeImpl = false;
            method.PInvokeInfo = null;
        }
        method.IsPreserveSig = false;
        var body = new MethodBody(method);
        var il = body.GetILProcessor();
        il.Emit(OpCodes.Ldstr, Marker + type.FullName.Replace('/', '.') + "." + method.Name);
        il.Emit(OpCodes.Newobj, exceptionCtor);
        il.Emit(OpCodes.Throw);
        method.Body = body;
    }

    /// <summary>References to <see cref="EngineLog"/>'s methods, as seen from the engine module.</summary>
    static (MethodReference Message, MethodReference Exception) LogSink(ModuleDefinition module)
    {
        var name = typeof(EngineLog).Assembly.GetName();
        var scope = new AssemblyNameReference(name.Name, name.Version);
        module.AssemblyReferences.Add(scope);
        var sink = new TypeReference(typeof(EngineLog).Namespace, nameof(EngineLog), module, scope);
        var message = new MethodReference(nameof(EngineLog.Message), module.TypeSystem.Void, sink);
        message.Parameters.Add(new ParameterDefinition(module.TypeSystem.Int32));
        message.Parameters.Add(new ParameterDefinition(module.TypeSystem.String));
        var exception = new MethodReference(nameof(EngineLog.Exception), module.TypeSystem.Void, sink);
        exception.Parameters.Add(new ParameterDefinition(new TypeReference("System", "Exception", module, module.TypeSystem.CoreLibrary)));
        return (message, exception);
    }

    /// <summary>Replaces a native method with a call to <paramref name="target"/>, passing the given arguments (static methods).</summary>
    static void Forward(MethodDefinition method, MethodReference target, params int[] args)
    {
        if (!method.IsInternalCall) return; // a Unity version where this isn't native any more: leave it alone
        method.IsInternalCall = false;
        method.IsIL = true;
        method.IsManaged = true;
        var body = new MethodBody(method);
        var il = body.GetILProcessor();
        foreach (var a in args) il.Emit(OpCodes.Ldarg, method.Parameters[a]);
        il.Emit(OpCodes.Call, target);
        il.Emit(OpCodes.Ret);
        method.Body = body;
    }

    static bool IsFinalizer(MethodDefinition m) =>
        m.Name == "Finalize" && m.IsVirtual && !m.IsStatic && m.Parameters.Count == 0 && m.ReturnType.MetadataType == MetadataType.Void;

    static void Empty(MethodDefinition method)
    {
        // just `ret`: base finalizers of engine classes are emptied too, and object.Finalize does nothing
        var body = new MethodBody(method);
        body.GetILProcessor().Emit(OpCodes.Ret);
        method.Body = body;
    }

    static bool IsDelegate(this TypeDefinition t) => t.BaseType?.FullName is "System.MulticastDelegate" or "System.Delegate";

    static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        var stack = new Stack<TypeDefinition>(module.Types);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            yield return t;
            foreach (var n in t.NestedTypes) stack.Push(n);
        }
    }
}

/// <summary>
/// Where Unity's log goes while macros expand (the rewritten engine calls it): each message becomes a compile warning at
/// the call site of the macro that logged it, prefixed with its kind. Logging outside a macro run is dropped.
/// </summary>
public static class EngineLog
{
    // UnityEngine.LogType: Error = 0, Assert = 1, Warning = 2, Log = 3, Exception = 4
    static readonly string[] Kinds = { "Debug.LogError", "Debug.LogAssertion", "Debug.LogWarning", "Debug.Log", "Debug.LogException" };

    public static void Message(int logType, string message)
    {
        if (!HowMacro.Macro.IsExpanding) return;
        var kind = logType >= 0 && logType < Kinds.Length ? Kinds[logType] : "Debug.Log";
        HowMacro.Macro.Warning(kind + ": " + message);
    }

    public static void Exception(Exception exception)
    {
        if (!HowMacro.Macro.IsExpanding) return;
        HowMacro.Macro.Warning("Debug.LogException: " + exception.GetType().Name + ": " + exception.Message);
    }
}

/// <summary>A macro called a Unity API that is implemented in the engine's native code, which doesn't exist while macros expand.</summary>
public sealed class NativeEngineCallException : NotSupportedException
{
    public NativeEngineCallException(string api, Exception inner) : base(
        api + " is implemented in Unity's native engine code, which isn't available while macros expand. " +
        "Macros can use Unity's managed types and math (Vector2/3/4, Vector2Int, Color, Color32, Rect, Mathf.Clamp/Lerp/Sqrt, ...), " +
        "but not engine calls; compute the value another way or emit code that calls " + api + " at runtime instead", inner) => Api = api;

    public string Api { get; }

    /// <summary>
    /// If <paramref name="e"/> came from a rewritten native method, the same failure named by the Unity API the macro called
    /// (e.g. Quaternion.Euler rather than its internal helper Quaternion.Internal_FromEulerRad); otherwise null.
    /// </summary>
    public static NativeEngineCallException? From(Exception e)
    {
        var native = e;
        while (native != null && !(native is NotSupportedException && native.Message.StartsWith(EngineRewriter.Marker, StringComparison.Ordinal)))
            native = native.InnerException;
        if (native == null) return null;

        // innermost frame first: [native helper, ..., public engine API, macro code, ...]; take the last engine frame
        var api = native.Message.Substring(EngineRewriter.Marker.Length);
        foreach (var frame in new System.Diagnostics.StackTrace(native, false).GetFrames())
        {
            var m = frame.GetMethod();
            var asm = m?.DeclaringType?.Assembly.GetName().Name;
            if (asm == null) continue;
            if (!EngineRewriter.Applies(asm)) break;
            api = (m!.DeclaringType!.FullName ?? m.DeclaringType.Name).Replace('+', '.') + "." + (m is System.Reflection.ConstructorInfo ? "ctor" : m.Name);
        }
        return new NativeEngineCallException(api, native);
    }
}
