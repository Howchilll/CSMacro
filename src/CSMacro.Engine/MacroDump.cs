using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CSMacro.Engine
{
    /// <summary>
    /// 把展开结果写到一个目标文件夹，方便排查展开后的问题：
    /// <list type="bullet">
    /// <item>展开后的源文件：和原文件逐行对齐（第 N 行就是源码第 N 行展开后的样子），可以直接对照着看。</item>
    /// <item><c>generated/</c>：引擎生成的辅助脚本（启动代码）。</item>
    /// <item><c>_expansions.txt</c>：展开报告，列出每一次展开的源码和格式化成多行的输出，以及诊断。</item>
    /// </list>
    /// 展开出错时也可以写出（已经展开的部分照样写），这正是最需要看展开结果的时候。
    /// </summary>
    public static class MacroDump
    {
        public const string ReportFileName = "_expansions.txt";
        /// <summary>dump 目录里的标记文件：有它的目录由 csmacro 管理，每次 dump 会清空，且其中的 .cs 不会被当作源码编译。</summary>
        public const string MarkerFileName = ".csmacro-dump";

        /// <param name="directory">目标文件夹。不存在会创建；已存在且不是 csmacro 创建的非空文件夹会拒绝写入，避免误删。</param>
        /// <param name="sources">参与展开的全部源文件（用来把绝对路径换回相对路径）。</param>
        /// <param name="result">展开结果。</param>
        /// <param name="filePatterns">只写出匹配的文件：按相对路径或文件名匹配，支持 * 和 **；为空表示全部。</param>
        /// <param name="macroFilter">本次只展开了哪些宏，只用于写进报告。</param>
        /// <returns>写出的文件（相对 <paramref name="directory"/>）。</returns>
        public static IReadOnlyList<string> Write(
            string directory,
            IReadOnlyList<SourceFile> sources,
            ExpandResult result,
            IReadOnlyCollection<string>? filePatterns = null,
            IReadOnlyCollection<string>? macroFilter = null)
        {
            PrepareDirectory(directory);

            var matches = FileMatcher(filePatterns);
            var idByPath = sources.ToDictionary(s => s.Path, s => s.Id, StringComparer.Ordinal);
            string Relative(string path) => idByPath.TryGetValue(path, out var id) ? id.Replace('\\', '/') : path;

            var written = new List<string>();
            var selectedPaths = new HashSet<string>(StringComparer.Ordinal);

            foreach (var file in result.Files)
            {
                var id = file.Id.Replace('\\', '/');
                if (!matches(id)) continue;
                selectedPaths.Add(file.Path);
                written.Add(WriteFile(directory, SafeRelativePath(id), StripLineHeader(file.Text)));
            }

            foreach (var file in result.GeneratedFiles)
                written.Add(WriteFile(directory, "generated/" + file.Name, file.Text));

            var report = BuildReport(result, selectedPaths, Relative, filePatterns, macroFilter);
            written.Add(WriteFile(directory, ReportFileName, report));
            return written;
        }

        /// <summary>把文件模式（相对路径或文件名，支持 * ? **）编译成匹配函数。</summary>
        public static Func<string, bool> FileMatcher(IReadOnlyCollection<string>? patterns)
        {
            var list = patterns?.Select(p => p.Trim().Replace('\\', '/')).Where(p => p.Length > 0).ToList();
            if (list == null || list.Count == 0) return _ => true;

            var regexes = list.Select(p => new Regex(
                "^" + Regex.Escape(p).Replace(@"\*\*/", "(.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]") + "$",
                RegexOptions.IgnoreCase)).ToList();

            return id =>
            {
                var name = id.Substring(id.LastIndexOf('/') + 1);
                return regexes.Any(r => r.IsMatch(id) || r.IsMatch(name));
            };
        }

        private static string BuildReport(ExpandResult result, HashSet<string> selectedPaths, Func<string, string> relative,
            IReadOnlyCollection<string>? filePatterns, IReadOnlyCollection<string>? macroFilter)
        {
            var sb = new StringBuilder();
            const string Rule = "------------------------------------------------------------------------";

            sb.Append("CSMacro expansion report\n");
            sb.Append("macros: ").Append(macroFilter is { Count: > 0 } ? string.Join(", ", macroFilter) : "(all)").Append('\n');
            sb.Append("files:  ").Append(filePatterns is { Count: > 0 } ? string.Join(", ", filePatterns) : "(all)").Append('\n');
            sb.Append("Expanded files are line-aligned with their sources: line N is source line N after expansion.\n");

            var diagnostics = result.Diagnostics
                .Where(d => selectedPaths.Count == 0 || selectedPaths.Contains(d.FilePath) || !result.Files.Any(f => f.Path == d.FilePath))
                .ToList();
            if (diagnostics.Count > 0)
            {
                sb.Append('\n').Append("diagnostics:\n");
                foreach (var d in diagnostics)
                    sb.Append("  ").Append(relative(d.FilePath)).Append('(').Append(d.Line).Append(',').Append(d.Column).Append("): ")
                      .Append(d.Level == DiagnosticLevel.Error ? "error " : "warning ").Append(d.Code).Append(": ").Append(d.Message).Append('\n');
            }

            // 报告只覆盖被选中的文件（辅助脚本 generated/ 总是完整写出）。
            var expansions = result.Expansions.Where(e => selectedPaths.Contains(e.FilePath)).ToList();

            sb.Append('\n').Append(expansions.Count).Append(" expansion(s)\n");
            foreach (var e in expansions)
            {
                sb.Append(Rule).Append('\n');
                sb.Append(relative(e.FilePath)).Append(':').Append(e.Line).Append("  ").Append(e.Macro).Append("  (").Append(e.Kind).Append(")\n");
                sb.Append("  source:\n").Append(Indent(e.Source, "    ")).Append('\n');
                sb.Append("  output:\n").Append(Indent(e.Formatted, "    ")).Append('\n');
            }
            return sb.ToString();
        }

        private static void PrepareDirectory(string directory)
        {
            var marker = Path.Combine(directory, MarkerFileName);
            if (Directory.Exists(directory))
            {
                if (!File.Exists(marker) && Directory.EnumerateFileSystemEntries(directory).Any())
                    throw new IOException($"dump directory '{directory}' is not empty and was not created by csmacro; refusing to overwrite it");
                // 只清理上次 dump 写出的内容（有标记文件才会走到这里）
                foreach (var f in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.Delete(f);
                foreach (var d in Directory.EnumerateDirectories(directory)) Directory.Delete(d, recursive: true);
            }
            Directory.CreateDirectory(directory);
            File.WriteAllText(marker, "This folder is written by csmacro --dump and is cleared on every dump.\n");
        }

        private static string WriteFile(string directory, string relativePath, string text)
        {
            var path = Path.Combine(directory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
            return relativePath;
        }

        private static string SafeRelativePath(string id) =>
            Path.IsPathRooted(id) || id.Split('/').Contains("..") ? Path.GetFileName(id) : id;

        private static string StripLineHeader(string text) =>
            text.StartsWith("#line 1 ", StringComparison.Ordinal) ? text.Substring(text.IndexOf('\n') + 1) : text;

        private static string Indent(string text, string prefix) =>
            string.Join("\n", text.Replace("\r", "").Split('\n').Select(l => prefix + l));
    }
}
