using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using HowMacro;
using HowMacro.Hosting;
using HowMacro.Unity;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace HowMacro.Unity.Bridge.Tests;

/// <summary>
/// A fake engine module shaped like UnityEngine.CoreModule: managed math next to internal calls, and a class whose
/// finalizer calls native code (like AnimationCurve). Loaded as is on .NET, calling the native parts throws an unhelpful
/// SecurityException, and the finalizer kills the process; rewritten, both are safe.
/// </summary>
public class EngineRewriterTests
{
    const string FakeEngine = """
        using System.Runtime.CompilerServices;
        namespace UnityEngine
        {
            public struct Vector2 { public float x, y; public Vector2(float x, float y) { this.x = x; this.y = y; } public float Length() => (float)System.Math.Sqrt(x * x + y * y); }
            public static class Rotations
            {
                public static float Euler(float deg) => Internal_FromEuler(deg * 0.0174532924f);
                [MethodImpl(MethodImplOptions.InternalCall)] static extern float Internal_FromEuler(float rad);
            }
            public enum LogType { Error, Assert, Warning, Log, Exception }
            public static class Debug
            {
                public static void Log(object message) => DebugLogHandler.Internal_Log(LogType.Log, 0, message?.ToString(), null);
                public static void LogError(object message) => DebugLogHandler.Internal_Log(LogType.Error, 0, message?.ToString(), null);
                public static void LogException(System.Exception e) => DebugLogHandler.Internal_LogException(e, null);
            }
            internal static class DebugLogHandler
            {
                [MethodImpl(MethodImplOptions.InternalCall)] internal static extern void Internal_Log(LogType level, int options, string msg, object obj);
                [MethodImpl(MethodImplOptions.InternalCall)] internal static extern void Internal_LogException(System.Exception ex, object obj);
            }
            public sealed class Curve
            {
                System.IntPtr m_Ptr;
                public Curve() { m_Ptr = Init(); }
                ~Curve() { Cleanup(m_Ptr); }
                [MethodImpl(MethodImplOptions.InternalCall)] static extern System.IntPtr Init();
                [MethodImpl(MethodImplOptions.InternalCall)] static extern void Cleanup(System.IntPtr p);
            }
        }
        """;

    static byte[] Compile()
    {
        var tree = CSharpSyntaxTree.ParseText(FakeEngine);
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("UnityEngine.FakeModule", new[] { tree }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        return ms.ToArray();
    }

    static Assembly LoadRewritten(out AssemblyLoadContext alc)
    {
        alc = new AssemblyLoadContext("fake-engine-" + Guid.NewGuid(), isCollectible: true);
        return alc.LoadFromStream(new MemoryStream(EngineRewriter.Rewrite(Compile())));
    }

    [Fact]
    public void ManagedCodeStillWorks()
    {
        var asm = LoadRewritten(out var alc);
        var v = Activator.CreateInstance(asm.GetType("UnityEngine.Vector2")!, 3f, 4f)!;
        Assert.Equal(5f, (float)v.GetType().GetMethod("Length")!.Invoke(v, null)!);
        alc.Unload();
    }

    [Fact]
    public void NativeCallThrowsAndIsNamedByThePublicApi()
    {
        var asm = LoadRewritten(out var alc);
        var euler = asm.GetType("UnityEngine.Rotations")!.GetMethod("Euler")!;
        var e = Assert.Throws<TargetInvocationException>(() => euler.Invoke(null, new object[] { 90f }));
        Assert.IsType<NotSupportedException>(e.InnerException);
        Assert.StartsWith(EngineRewriter.Marker + "UnityEngine.Rotations.Internal_FromEuler", e.InnerException!.Message);

        var named = NativeEngineCallException.From(e);
        Assert.NotNull(named);
        Assert.Equal("UnityEngine.Rotations.Euler", named!.Api); // the method the macro called, not the internal helper
        Assert.Contains("native engine code", named.Message);
        alc.Unload();
    }

    [Fact]
    public void FinalizerOfANativeBackedClassDoesNotCrash()
    {
        var asm = LoadRewritten(out var alc);
        CreateAndDrop(asm.GetType("UnityEngine.Curve")!);
        GC.Collect();
        GC.WaitForPendingFinalizers(); // without the rewrite the finalizer's native call is unhandled and ends the process
        GC.Collect();
        alc.Unload();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void CreateAndDrop(Type curve)
    {
        var e = Assert.Throws<TargetInvocationException>(() => Activator.CreateInstance(curve));
        Assert.Equal("UnityEngine.Curve.ctor", NativeEngineCallException.From(e)!.Api);
    }

    [Fact]
    public void DebugLogBecomesAMacroWarning()
    {
        var asm = LoadRewritten(out var alc);
        var debug = asm.GetType("UnityEngine.Debug")!;
        var context = new MacroContext { MacroName = "TEST" };
        MacroHost.Run(context, () =>
        {
            debug.GetMethod("Log")!.Invoke(null, new object[] { "hello from a macro" });
            debug.GetMethod("LogError")!.Invoke(null, new object[] { "bad" });
            debug.GetMethod("LogException")!.Invoke(null, new object[] { new InvalidOperationException("boom") });
        });
        Assert.Equal(new[] { "Debug.Log: hello from a macro", "Debug.LogError: bad", "Debug.LogException: InvalidOperationException: boom" },
                     context.Messages.Select(m => m.Text));
        Assert.All(context.Messages, m => Assert.Equal(MacroMessageSeverity.Warning, m.Severity));

        debug.GetMethod("Log")!.Invoke(null, new object[] { "outside a macro run: dropped, no exception" });
        alc.Unload();
    }

    [Fact]
    public void OtherExceptionsAreLeftAlone() => Assert.Null(NativeEngineCallException.From(new InvalidOperationException("x")));

    [Fact]
    public void RewriteIsCachedByContent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "howmacro-engine-" + Guid.NewGuid().ToString("N"));
        var dll = Path.Combine(dir, "UnityEngine.FakeModule.dll");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(dll, Compile());
        try
        {
            var first = EngineRewriter.Load(dll, Path.Combine(dir, "cache"));
            var cached = Directory.GetFiles(Path.Combine(dir, "cache"));
            Assert.Single(cached);
            Assert.Equal(first, EngineRewriter.Load(dll, Path.Combine(dir, "cache")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
