using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using CSMacro;
using CSMacro.Hosting;

namespace CSMacro.Engine
{
    /// <summary>在已加载的宏程序集里找到宏方法并在给定上下文中执行。各宿主只负责"怎么加载程序集"。</summary>
    public static class MacroReflection
    {
        public static void Invoke(Assembly assembly, MacroMethod m, MacroContext context)
        {
            var type = assembly.GetType(m.TypeName, throwOnError: true)!;

            if (m.MethodName == MacroMethod.Constructor)
            {
                // 特性宏：构造函数体就是展开逻辑。
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

            // 签名只服务于 IDE；展开时实参一律传默认值，宏通过 Macro.Arg(i) 读源码原文。
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
