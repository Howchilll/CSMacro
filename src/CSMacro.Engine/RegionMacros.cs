using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using CSMacro;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CSMacro.Engine
{
    /// <summary>
    /// 片段宏：<c>#region @NAME</c> … <c>#endregion</c>，处理片段里的成员。
    /// <list type="bullet">
    /// <item>识别：#region 后面的文字以 @ 开头。它对 IDE 只是注释，所以按名字在被引用的程序集里找带 [RegionMacro] 的静态方法。</item>
    /// <item>范围：目前只支持类型内部（包裹成员）。</item>
    /// <item>两种处理方式：逐成员替换（行号精确、逐个检查核心原则），或整段文本替换（输出行数不能超过原文）。</item>
    /// <item>与方法宏同一阶段执行（在全部特性宏之后）。片段宏不能嵌套，也不能替换已经含有宏的成员（宏不能包含宏）。</item>
    /// </list>
    /// </summary>
    public static partial class MacroExpander
    {
        private const string RegionMacroAttributeName = "CSMacro.RegionMacroAttribute";
        private static readonly Regex RegionMacroPattern = new Regex(@"^@([A-Za-z_][A-Za-z0-9_]*)\s*(\(.*\))?\s*$");

        private sealed class RegionUse
        {
            public RegionUse(RegionDirectiveTriviaSyntax start, EndRegionDirectiveTriviaSyntax end, SyntaxTrivia message,
                string name, ArgumentListSyntax? args, TypeDeclarationSyntax type, List<MemberDeclarationSyntax> members, TextSpan content)
            {
                Start = start;
                End = end;
                Message = message;
                Name = name;
                Args = args;
                Type = type;
                Members = members;
                Content = content;
            }

            public RegionDirectiveTriviaSyntax Start { get; }
            public EndRegionDirectiveTriviaSyntax End { get; }

            /// <summary>#region 后面的文字（PreprocessingMessage trivia）。</summary>
            public SyntaxTrivia Message { get; }

            public string Name { get; }
            public ArgumentListSyntax? Args { get; }
            public TypeDeclarationSyntax Type { get; }
            public List<MemberDeclarationSyntax> Members { get; }

            /// <summary>#region 那一行之后、#endregion 那一行之前的全部文本。</summary>
            public TextSpan Content { get; }
        }

        // ================================================================== 发现

        private static string RegionMessage(RegionDirectiveTriviaSyntax region) =>
            region.EndOfDirectiveToken.LeadingTrivia.FirstOrDefault(t => t.IsKind(SyntaxKind.PreprocessingMessageTrivia)).ToString().Trim();

        private static IEnumerable<RegionDirectiveTriviaSyntax> RegionMacroDirectives(SyntaxTree tree) =>
            tree.GetRoot().DescendantTrivia(descendIntoTrivia: true)
                .Select(t => t.GetStructure())
                .OfType<RegionDirectiveTriviaSyntax>()
                .Where(r => RegionMessage(r).StartsWith("@", StringComparison.Ordinal));

        /// <summary>只做语法层面的发现和校验（位置、配对、嵌套）。</summary>
        private static List<RegionUse> FindRegionMacros(SyntaxTree tree, List<MacroDiagnostic> diagnostics, out bool failed)
        {
            failed = false;
            var result = new List<RegionUse>();
            var text = tree.GetText();

            foreach (var start in RegionMacroDirectives(tree))
            {
                var message = RegionMessage(start);
                var messageTrivia = start.EndOfDirectiveToken.LeadingTrivia.First(t => t.IsKind(SyntaxKind.PreprocessingMessageTrivia));

                bool Fail(string code, string msg)
                {
                    diagnostics.Add(Diag(DiagnosticLevel.Error, code, msg, tree, start));
                    return false;
                }

                var match = RegionMacroPattern.Match(message);
                if (!match.Success)
                {
                    failed |= !Fail("CSM015", $"can't read region macro '{message}'; write it as `#region @NAME` or `#region @NAME(args)`");
                    continue;
                }
                var name = match.Groups[1].Value;
                var args = match.Groups[2].Success ? SyntaxFactory.ParseArgumentList(match.Groups[2].Value) : null;
                if (args != null && FirstError(args) is string argError)
                {
                    failed |= !Fail("CSM015", $"can't parse the arguments of #region @{name}: {argError}");
                    continue;
                }

                if (!(start.GetRelatedDirectives().OfType<EndRegionDirectiveTriviaSyntax>().FirstOrDefault() is { } end))
                {
                    failed |= !Fail("CSM015", $"#region @{name} has no matching #endregion");
                    continue;
                }

                var type = TypeBodyOf(start.ParentTrivia.Token);
                if (type == null || TypeBodyOf(end.ParentTrivia.Token) != type)
                {
                    failed |= !Fail("CSM003", $"#region @{name} must start and end directly inside the same class/struct body " +
                                              "(region macros in method bodies or at namespace level are not supported yet)");
                    continue;
                }

                var members = type.Members.Where(m => m.SpanStart > start.SpanStart && m.SpanStart < end.SpanStart).ToList();
                var contentStart = start.FullSpan.End;
                var contentEnd = text.Lines.GetLineFromPosition(end.SpanStart).Start;
                result.Add(new RegionUse(start, end, messageTrivia, name, args, type, members,
                    TextSpan.FromBounds(contentStart, Math.Max(contentStart, contentEnd))));
            }

            // 片段宏不能嵌套（宏不能包含宏）
            foreach (var inner in result)
            {
                var outer = result.FirstOrDefault(o => o != inner && o.Content.Contains(inner.Start.Span));
                if (outer == null) continue;
                diagnostics.Add(Diag(DiagnosticLevel.Error, "CSM012",
                    $"macros can't contain macros: #region @{inner.Name} is inside #region @{outer.Name}", tree, inner.Start));
                failed = true;
            }
            return result;
        }

        /// <summary>
        /// 指令 trivia 挂在某个 token 上。如果那个 token 是类型里某个成员的第一个 token，或者是类型的右花括号，
        /// 指令就位于这个类型的成员之间，返回该类型；否则（方法体内、命名空间层级）返回 null。
        /// </summary>
        private static TypeDeclarationSyntax? TypeBodyOf(SyntaxToken token)
        {
            if (token.IsKind(SyntaxKind.CloseBraceToken) && token.Parent is TypeDeclarationSyntax closing) return closing;

            SyntaxNode? node = token.Parent;
            while (node?.Parent != null && node.Parent.GetFirstToken() == token) node = node.Parent;
            return node is MemberDeclarationSyntax && node.Parent is TypeDeclarationSyntax type ? type : null;
        }

        /// <summary>按名字在被引用的程序集里找片段宏（只看引用了 CSMacro.Abstractions 的程序集）。</summary>
        private static Dictionary<string, List<IMethodSymbol>> RegionMacroIndex(CSharpCompilation compilation)
        {
            var index = new Dictionary<string, List<IMethodSymbol>>(StringComparer.Ordinal);

            void Walk(INamespaceOrTypeSymbol container)
            {
                foreach (var member in container.GetMembers())
                {
                    if (member is INamespaceOrTypeSymbol nested) Walk(nested);
                    if (member is IMethodSymbol { IsStatic: true } method &&
                        method.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == RegionMacroAttributeName))
                    {
                        if (!index.TryGetValue(method.Name, out var list)) index[method.Name] = list = new List<IMethodSymbol>();
                        list.Add(method);
                    }
                }
            }

            foreach (var reference in compilation.References)
            {
                if (compilation.GetAssemblyOrModuleSymbol(reference) is IAssemblySymbol assembly &&
                    assembly.Modules.Any(m => m.ReferencedAssemblySymbols.Any(r => r.Name == "CSMacro.Abstractions")))
                    Walk(assembly.GlobalNamespace);
            }
            return index;
        }

        // ================================================================== 展开

        private static bool ExpandRegion(
            RegionUse use,
            SyntaxTree tree,
            SemanticModel model,
            CSharpCompilation compilation,
            IMacroInvoker invoker,
            IReadOnlyCollection<string> defines,
            MacroSemantics semantics,
            HashSet<AttributeSyntax> macroAttributes,
            IReadOnlyDictionary<string, IReadOnlyList<MacroFragment>> fragments,
            List<MacroCall> calls,
            List<AttributeUse> attributeUses,
            Dictionary<string, List<IMethodSymbol>> index,
            List<TextChange> changes,
            Dictionary<int, string> lines,
            List<MacroExpansion> records,
            Dictionary<ISymbol, string> transformed,
            List<MacroDiagnostic> diagnostics)
        {
            var display = "#region @" + use.Name;
            var text = tree.GetText();
            var parseOptions = (CSharpParseOptions)tree.Options;

            bool Fail(string code, string message, SyntaxNode? at = null)
            {
                diagnostics.Add(at == null
                    ? Diag(DiagnosticLevel.Error, code, message, tree, use.Start)
                    : Diag(DiagnosticLevel.Error, code, message, tree, at));
                return false;
            }

            // ---- 找到宏方法 ----
            if (!index.TryGetValue(use.Name, out var candidates) || candidates.Count == 0)
                return Fail("CSM015", $"no region macro named '{use.Name}' was found in the referenced assemblies " +
                                      "(a region macro is a static method marked [RegionMacro] in a macro library)");
            if (candidates.Count > 1)
                return Fail("CSM015", $"region macro name '{use.Name}' is ambiguous: " +
                                      string.Join(", ", candidates.Select(c => c.ContainingType.ToDisplayString() + "." + c.Name)));

            var def = candidates[0];
            var reference = ResolveMacroAssembly(def.ContainingType, compilation);
            if (reference == null)
                return Fail("CSM004", $"{display} is declared in the same project that uses it; macros must live in a referenced assembly");

            // ---- 片段里的成员 ----
            var members = new List<MacroRegionMember>();
            var syntaxOf = new Dictionary<MacroRegionMember, MemberDeclarationSyntax>();
            foreach (var m in use.Members)
            {
                var region = DescribeRegionMember(m, model, semantics, macroAttributes, calls);
                if (region == null) continue; // 嵌套类型等不在 RegionMembers 里（整段文本模式仍能看到）
                members.Add(region);
                syntaxOf[region] = m;
            }

            // ---- 执行 ----
            var pos = tree.GetLineSpan(use.Start.Span).StartLinePosition;
            var context = new MacroContext
            {
                MacroName = use.Name,
                FilePath = tree.FilePath,
                Line = pos.Line + 1,
                Column = pos.Character + 1,
                Args = use.Args?.Arguments.Select(a => LiteralArg(a.Expression)).ToArray() ?? Array.Empty<MacroArg>(),
                ContainingType = model.GetDeclaredSymbol(use.Type)?.ToDisplayString(),
                ContainingTypeInfo = semantics.Type(model.GetDeclaredSymbol(use.Type)),
                TypeLookup = semantics.Find,
                Defines = defines,
                AvailableFragments = fragments,
                RegionMembers = members,
                RegionText = text.ToString(use.Content),
            };
            var method = new MacroMethod(reference, MetadataName(def.ContainingType), def.Name, def.Arity, def.Parameters.Length);
            if (!Run(invoker, method, context, display, tree, use.Start, diagnostics)) return false;

            var replaced = members.Where(r => r.Replacement != null).ToList();
            var output = context.Output.ToString();
            var ok = true;

            if (replaced.Count > 0 && output.Trim().Length > 0)
                return Fail("CSM016", $"{display} used both member replacement (Replace) and whole-text output (Emit); use one of them");

            if (output.Trim().Length > 0)
            {
                // ---- 整段文本替换 ----
                var inside = calls.Select(c => (SyntaxNode)c.Node).Concat(attributeUses.Select(a => a.Node))
                    .FirstOrDefault(node => use.Content.Contains(node.Span));
                if (inside != null)
                    return Fail("CSM012", $"macros can't contain macros: {display} replaces the region text as a whole, " +
                                          "but the region contains another macro; use member replacement and skip that member instead", inside);

                var original = text.ToString(use.Content);
                var newText = output.TrimEnd();
                var originalNewlines = original.Count(c => c == '\n');
                var outputNewlines = newText.Count(c => c == '\n');
                if (outputNewlines + 1 > originalNewlines)
                    return Fail("CSM007", $"{display} emitted {outputNewlines + 1} line(s) but the region has {originalNewlines}; " +
                                          "region text output can't add lines, or every line after it would shift");

                var padded = newText + new string('\n', originalNewlines - outputNewlines);
                var principle = CheckRegionText(original, padded, parseOptions);
                if (principle != null) return Fail(principle.Value.Code, $"{display}: {principle.Value.Message}");

                changes.Add(new TextChange(use.Content, padded));
                var first = text.Lines.GetLineFromPosition(use.Content.Start).LineNumber + 1;
                for (var l = 0; l < originalNewlines; l++) AddLine(lines, first + l, display);
                records.Add(new MacroExpansion(tree.FilePath, first, display, "Region:Text", original.Trim(), newText, newText));
            }
            else
            {
                // ---- 逐成员替换 ----
                foreach (var r in replaced)
                {
                    var syntax = syntaxOf[r];
                    var oneLine = ToSingleLine(r.Replacement!, parseOptions, out var compressError);
                    if (oneLine == null) { ok = Fail("CSM007", $"{display} → {r.Name}: {compressError}", syntax); continue; }

                    var memberError = CheckMember(oneLine, r.Name, r.Type, parseOptions);
                    if (memberError != null) { ok = Fail(memberError.Value.Code, $"{display} → {r.Name}: {memberError.Value.Message} -- emitted: {oneLine}", syntax); continue; }

                    var newlines = text.ToString(syntax.Span).Count(c => c == '\n');
                    changes.Add(new TextChange(syntax.Span, oneLine + new string('\n', newlines)));
                    if (syntax is FieldDeclarationSyntax field && SyntaxFactory.ParseMemberDeclaration(oneLine) is PropertyDeclarationSyntax &&
                        model.GetDeclaredSymbol(field.Declaration.Variables[0]) is { } symbol)
                        transformed[symbol] = display;
                    AddLine(lines, r.Line, display);
                    records.Add(new MacroExpansion(tree.FilePath, r.Line, display, "Region:Member", r.Source.Replace("\n", " "),
                        oneLine, Format(oneLine, MacroSite.Member)));
                }
            }

            // #region @NAME → #region [expanded] @NAME：保留可读性，同时不再被识别为宏
            changes.Add(new TextChange(use.Message.Span, "[expanded] " + use.Message.ToString().Trim()));
            return ok;
        }

        private static MacroRegionMember? DescribeRegionMember(MemberDeclarationSyntax m, SemanticModel model, MacroSemantics semantics,
            HashSet<AttributeSyntax> macroAttributes, List<MacroCall> calls)
        {
            var attributes = string.Join(" ", m.AttributeLists.Select(l => WithoutMacroAttributes(l, macroAttributes)).Where(s => s.Length > 0));
            var modifiers = m.Modifiers.ToString();
            var line = m.SyntaxTree.GetLineSpan(m.Span).StartLinePosition.Line + 1;

            string name, type = "";
            string? initializer = null, cannotReplace = null;
            MacroMemberKind kind;
            ISymbol? symbol;

            switch (m)
            {
                case FieldDeclarationSyntax f:
                    kind = MacroMemberKind.Field;
                    type = f.Declaration.Type.ToString();
                    name = string.Join(", ", f.Declaration.Variables.Select(v => v.Identifier.ValueText));
                    symbol = model.GetDeclaredSymbol(f.Declaration.Variables[0]);
                    if (f.Declaration.Variables.Count == 1) initializer = f.Declaration.Variables[0].Initializer?.Value.ToString();
                    else cannotReplace = "it declares several variables";
                    break;
                case PropertyDeclarationSyntax p:
                    kind = MacroMemberKind.Property;
                    type = p.Type.ToString();
                    name = p.Identifier.ValueText;
                    initializer = p.Initializer?.Value.ToString();
                    symbol = model.GetDeclaredSymbol(p);
                    break;
                case MethodDeclarationSyntax md:
                    kind = MacroMemberKind.Method;
                    type = md.ReturnType.ToString();
                    name = md.Identifier.ValueText;
                    symbol = model.GetDeclaredSymbol(md);
                    cannotReplace = "only fields and properties can be replaced";
                    break;
                case ConstructorDeclarationSyntax c:
                    kind = MacroMemberKind.Constructor;
                    name = c.Identifier.ValueText;
                    symbol = model.GetDeclaredSymbol(c);
                    cannotReplace = "only fields and properties can be replaced";
                    break;
                case EventFieldDeclarationSyntax e:
                    kind = MacroMemberKind.Event;
                    type = e.Declaration.Type.ToString();
                    name = string.Join(", ", e.Declaration.Variables.Select(v => v.Identifier.ValueText));
                    symbol = model.GetDeclaredSymbol(e.Declaration.Variables[0]);
                    cannotReplace = "only fields and properties can be replaced";
                    break;
                case EventDeclarationSyntax e:
                    kind = MacroMemberKind.Event;
                    type = e.Type.ToString();
                    name = e.Identifier.ValueText;
                    symbol = model.GetDeclaredSymbol(e);
                    cannotReplace = "only fields and properties can be replaced";
                    break;
                default:
                    return null;
            }

            // 宏不能包含宏：成员里已经有宏调用时不能再替换它
            var inside = calls.FirstOrDefault(c => m.Span.Contains(c.Node.Span));
            string? expandedBy = null;
            if (inside != null)
            {
                if (m is FieldDeclarationSyntax { Declaration: { Variables: { Count: 1 } vars } } && vars[0].Initializer?.Value == inside.Node &&
                    (inside.Sites & MacroSite.Member) != 0)
                    expandedBy = inside.Method.Name;
                cannotReplace = expandedBy != null
                    ? $"it is already expanded by @{expandedBy} (macros can't contain macros)"
                    : $"it contains @{inside.Method.Name} (macros can't contain macros)";
            }

            return new MacroRegionMember(name, kind, type, modifiers, attributes, initializer, m.ToString(), line,
                semantics.Member(symbol), expandedBy, cannotReplace);
        }

        /// <summary>
        /// 整段文本替换后的核心原则检查：字段和属性的名字、类型不变，所有成员的名字集合不变。
        /// </summary>
        private static (string Code, string Message)? CheckRegionText(string original, string replaced, CSharpParseOptions options)
        {
            var after = SyntaxFactory.ParseCompilationUnit("class __CSMacroCheck {\n" + replaced + "\n}", options: options);
            if (FirstError(after) is string parseError)
                return ("CSM011", $"the replaced region text is not valid member declarations: {parseError}");
            var before = SyntaxFactory.ParseCompilationUnit("class __CSMacroCheck {\n" + original + "\n}", options: options);

            static List<(string Name, string? Type)> Declared(CompilationUnitSyntax unit) =>
                unit.Members.OfType<ClassDeclarationSyntax>().Single().Members.SelectMany(m => m switch
                {
                    FieldDeclarationSyntax f => f.Declaration.Variables.Select(v => (v.Identifier.ValueText, (string?)Normalize(f.Declaration.Type.ToString()))),
                    PropertyDeclarationSyntax p => new[] { (p.Identifier.ValueText, (string?)Normalize(p.Type.ToString())) },
                    MethodDeclarationSyntax md => new[] { (md.Identifier.ValueText, (string?)null) },
                    EventFieldDeclarationSyntax e => e.Declaration.Variables.Select(v => (v.Identifier.ValueText, (string?)null)),
                    BaseTypeDeclarationSyntax t => new[] { (t.Identifier.ValueText, (string?)null) },
                    _ => Array.Empty<(string, string?)>(),
                }).OrderBy(x => x.Item1, StringComparer.Ordinal).ToList();

            static string Normalize(string s) => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

            var a = Declared(before);
            var b = Declared(after);
            var removed = a.Select(x => x.Name).Except(b.Select(x => x.Name)).ToList();
            var added = b.Select(x => x.Name).Except(a.Select(x => x.Name)).ToList();
            if (removed.Count > 0 || added.Count > 0)
                return ("CSM008", "region text output may not introduce or remove names" +
                                  (removed.Count > 0 ? $"; removed: {string.Join(", ", removed)}" : "") +
                                  (added.Count > 0 ? $"; added: {string.Join(", ", added)}" : ""));

            var retyped = a.Zip(b, (x, y) => (x.Name, Before: x.Type, After: y.Type))
                .FirstOrDefault(t => t.Before != null && t.After != null && t.Before != t.After);
            if (retyped.Name != null)
                return ("CSM009", $"region text output changed the type of '{retyped.Name}' from {retyped.Before} to {retyped.After}");
            return null;
        }

        private static MacroArg LiteralArg(ExpressionSyntax expression) =>
            expression is LiteralExpressionSyntax literal
                ? new MacroArg(expression.ToString(), null, true, literal.Token.Value)
                : new MacroArg(expression.ToString(), null, false, null);

        private static void AddLine(Dictionary<int, string> lines, int line, string macro) =>
            lines[line] = lines.TryGetValue(line, out var names) ? names + ", " + macro : macro;
    }
}
