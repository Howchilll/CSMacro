using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using HowMacro;
using HowMacro.Hosting;

namespace HowMacro.Engine
{
    /// <summary>Finds a macro method in a loaded macro assembly and runs it in the given context. Hosts only decide how assemblies are loaded.</summary>
    public static class MacroReflection
    {
        public static void Invoke(Assembly assembly, MacroMethod m, MacroContext context)
        {
            var type = assembly.GetType(m.TypeName, throwOnError: true)!;

            if (m.MethodName == MacroMethod.Constructor)
            {
                // Attribute macro: the constructor body is the expansion logic.
                var ctor = type
                    .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(c => c.GetParameters().Length == m.ParameterCount)
                    ?? throw new MissingMethodException(m.TypeName, MacroMethod.Constructor);
                var ctorArgs = ctor.GetParameters().Select(p => DefaultOf(p.ParameterType)).ToArray();
                Run(context, () => ctor.Invoke(ctorArgs));
                return;
            }

            var method = type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(x => x.Name == m.MethodName
                                     && x.GetGenericArguments().Length == m.Arity
                                     && x.GetParameters().Length == m.ParameterCount)
                ?? throw new MissingMethodException(m.TypeName, m.MethodName);

            // The signature only serves the IDE; during expansion every argument is a default value and the macro reads the source text via Macro.Arg(i).
            if (method.IsGenericMethodDefinition)
                method = method.MakeGenericMethod(Enumerable.Repeat(typeof(object), m.Arity).ToArray());

            var args = method.GetParameters().Select(p => DefaultOf(p.ParameterType)).ToArray();
            Run(context, () => method.Invoke(null, args));
        }

        private static object? DefaultOf(Type t) => t.IsValueType ? Activator.CreateInstance(t) : null;

        private static void Run(MacroContext context, Action body) =>
            MacroHost.Run(context, () =>
            {
                try { body(); }
                catch (TargetInvocationException e) when (e.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                }
            });
    }
}
