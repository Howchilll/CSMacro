using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace HowMacro.Engine
{
    /// <summary>
    /// Writes the expansion result to a target folder to help debug problems after expansion:
    /// <list type="bullet">
    /// <item>Expanded source files: line-aligned with the originals (line N is source line N after expansion), so you can compare them side by side.</item>
    /// <item><c>generated/</c>: the generated startup script (startup code).</item>
    /// <item><c>_expansions.txt</c>: the expansion report, listing each expansion's source and its output formatted over multiple lines, plus diagnostics.</item>
    /// </list>
    /// It can be written even when expansion fails (whatever did expand is still written) — exactly when you most need to see the expansion.
    /// </summary>
    public static class MacroDump
    {
        public const string ReportFileName = "_expansions.txt";
        /// <summary>Marker file in a dump folder: folders with it are managed by howmacro, cleared on every dump, and their .cs files are never compiled as sources.</summary>
        public const string MarkerFileName = ".howmacro-dump";

        /// <param name="directory">Target folder. Created if missing; a non-empty folder that howmacro didn't create is refused, to avoid deleting someone's files.</param>
        /// <param name="sources">All sources that took part in the expansion (used to turn absolute paths back into relative ones).</param>
        /// <param name="result">The expansion result.</param>
        /// <param name="filePatterns">Only write matching files: matched by relative path or file name, * and ** supported; empty means all.</param>
        /// <param name="macroFilter">Which macros this run expanded; only used for the report.</param>
        /// <returns>The files written (relative to <paramref name="directory"/>).</returns>
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

        /// <summary>Compiles file patterns (relative paths or file names, * ? ** supported) into a matcher.</summary>
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

            sb.Append("HowMacro expansion report\n");
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

            // The report only covers the selected files (the generated/ startup script is always written in full).
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
                    throw new IOException($"dump directory '{directory}' is not empty and was not created by howmacro; refusing to overwrite it");
                // Only clear what the previous dump wrote (we only get here when the marker file exists)
                foreach (var f in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.Delete(f);
                foreach (var d in Directory.EnumerateDirectories(directory)) Directory.Delete(d, recursive: true);
            }
            Directory.CreateDirectory(directory);
            File.WriteAllText(marker, "This folder is written by howmacro --dump and is cleared on every dump.\n");
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
