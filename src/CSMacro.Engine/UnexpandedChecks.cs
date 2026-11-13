using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace CSMacro.Engine
{
    /// <summary>
    /// 核心原则的兜底检查：不展开宏，项目在静态上也必须正确。
    /// 宏调用处的代码如果只有展开后才合法（IDE 会一直标红），就在构建时报 CSM020，而不是让它悄悄通过。
    /// 典型例子：<c>public int Hp = @CLAMP(0, MaxHp, 20);</c>
    /// 展开前这是一个字段初始值，不能引用实例成员 MaxHp（CS0236）；展开后 MaxHp 进了 setter，于是能编译。
    /// </summary>
    public static partial class MacroExpander
    {
        /// <summary>
        /// 不在这里报的错误：找不到名字/类型/成员。它们展开后同样会被编译器报出来，
        /// 而且在这里报的话，依赖源码生成器产出类型的项目会误报（展开用的编译不跑源码生成器）。
        /// </summary>
        private static readonly HashSet<string> SkippedIds = new HashSet<string> { "CS0103", "CS0246", "CS0234", "CS1061", "CS0117" };

        private static bool CheckUnexpandedSource(
            SyntaxTree tree,
            SemanticModel model,
            List<MacroCall> calls,
            List<AttributeUse> attributes,
            List<MacroDiagnostic> diagnostics)
        {
            var sites = calls.Select(c => (Span: c.Node.Span, Name: "@" + c.Method.Name))
                .Concat(attributes.Select(a => (Span: a.Node.Span, Name: "[@" + a.Ctor.ContainingType.Name + "]")))
                .ToList();
            if (sites.Count == 0) return true;

            var ok = true;
            var reported = new HashSet<(int, string)>();
            foreach (var (span, name) in sites)
            {
                foreach (var d in model.GetDiagnostics(span))
                {
                    if (d.Severity != DiagnosticSeverity.Error || SkippedIds.Contains(d.Id)) continue;
                    if (!reported.Add((d.Location.SourceSpan.Start, d.Id))) continue;

                    var pos = d.Location.GetLineSpan().StartLinePosition;
                    diagnostics.Add(new MacroDiagnostic(DiagnosticLevel.Error, "CSM020",
                        $"{d.Id}: {d.GetMessage()} -- this code is only valid after {name} is expanded, so the IDE shows it as an error. " +
                        "The unexpanded source must stay statically correct; rewrite the call so it is valid C# on its own",
                        tree.FilePath, pos.Line + 1, pos.Character + 1));
                    ok = false;
                }
            }
            return ok;
        }
    }
}
