using System.Text.RegularExpressions;
using HowMacro;

namespace Demo.Macros
{
    /// <summary>Region macro samples: process the members between #region @NAME … #endregion.</summary>
    public static class Regions
    {
        /// <summary>
        /// Per-member replacement: turns every writable field in the region into a property that logs its changes.
        /// Wrap a group of fields with it while testing; delete the two #region lines afterwards to restore them.
        /// <code>
        /// #region @LOG_ALL
        /// public int Hp = 50;
        /// public string State = "Idle";
        /// #endregion
        /// </code>
        /// Skips readonly / const fields, fields already expanded by another macro (such as @GETSET), properties and methods.
        /// </summary>
        [RegionMacro]
        public static void LOG_ALL()
        {
            var owner = Macro.ContainingTypeInfo?.Name;
            foreach (var m in Macro.RegionMembers)
            {
                if (!m.IsField || !m.CanReplace || m.Declaration?.CanWrite != true) continue;

                var label = Macro.Quote($"[{owner}.{m.Name}] ");
                var initializer = m.Initializer != null ? $" = {m.Initializer};" : "";
                m.Replace(
                    $"{m.Attributes} {m.Modifiers} {m.Type} {m.Name} {{ get; set {{ " +
                    $"if (global::System.Collections.Generic.EqualityComparer<{m.Type}>.Default.Equals(field, value)) return; " +
                    $"global::System.Console.WriteLine({label} + field + \" -> \" + value); field = value; }} }}{initializer}");
            }
        }

        /// <summary>
        /// Whole-text replacement: changes every private in the region to public (e.g. to let test code reach internal state temporarily).
        /// Shows plain text matching and replacement with Macro.RegionText + Macro.Emit: line by line, same number of lines.
        /// Names and types are unchanged, so it passes the core principle checks.
        /// </summary>
        [RegionMacro]
        public static void PUBLIC_ALL()
        {
            Macro.Emit(Regex.Replace(Macro.RegionText, @"\bprivate\b", "public"));
        }
    }
}
