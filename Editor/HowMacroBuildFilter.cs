using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HowMacro;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Compilation;
using UnityEngine;

namespace HowMacro.Unity.Editor
{
    /// <summary>
    /// Keeps macro assemblies out of player builds. Unity compiles them for the player too (the unexpanded code needs them
    /// to compile), but after expansion nothing references them, so they would only add dead weight to the build.
    /// An assembly is a macro assembly if it declares a [Macro], [AttributeMacro] or [RegionMacro]; HowMacro.Abstractions
    /// goes too. As a safety net, an assembly is kept if any remaining assembly still mentions its name.
    /// </summary>
    sealed class HowMacroBuildFilter : IFilterBuildAssemblies
    {
        public int callbackOrder => 0;

        public string[] OnFilterAssemblies(BuildOptions buildOptions, string[] assemblies)
        {
            var macroAssemblies = MacroAssemblyNames();
            var candidates = assemblies.Where(a => macroAssemblies.Contains(Path.GetFileNameWithoutExtension(a))).ToList();
            if (candidates.Count == 0) return assemblies;

            var kept = assemblies.Except(candidates).ToList();
            var removed = new List<string>();
            foreach (var candidate in candidates)
            {
                var name = Path.GetFileNameWithoutExtension(candidate);
                var user = kept.FirstOrDefault(k => Mentions(k, name));
                if (user != null)
                {
                    Debug.LogWarning("HowMacro: keeping " + name + " in the build because " + Path.GetFileName(user) +
                                     " still references it (macro assemblies should hold only macros).");
                    kept.Add(candidate);
                }
                else removed.Add(name);
            }
            Debug.Log("HowMacro: left macro assemblies out of the player build: " + string.Join(", ", removed));
            return assemblies.Where(kept.Contains).ToArray();
        }

        static HashSet<string> MacroAssemblyNames()
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HowMacro.Abstractions" };
            foreach (var m in TypeCache.GetMethodsWithAttribute<MacroAttribute>()) names.Add(m.DeclaringType.Assembly.GetName().Name);
            foreach (var m in TypeCache.GetMethodsWithAttribute<RegionMacroAttribute>()) names.Add(m.DeclaringType.Assembly.GetName().Name);
            foreach (var t in TypeCache.GetTypesWithAttribute<AttributeMacroAttribute>()) names.Add(t.Assembly.GetName().Name);
            return names;
        }

        /// <summary>Whether the file contains the assembly name as UTF-8 (where metadata keeps assembly references). False positives only keep an assembly.</summary>
        static bool Mentions(string path, string assemblyName)
        {
            try
            {
                var bytes = File.ReadAllBytes(path);
                var needle = Encoding.UTF8.GetBytes(assemblyName + "\0");
                for (var i = 0; i <= bytes.Length - needle.Length; i++)
                {
                    var j = 0;
                    while (j < needle.Length && bytes[i + j] == needle[j]) j++;
                    if (j == needle.Length) return true;
                }
                return false;
            }
            catch (IOException) { return true; }
        }
    }

    static class HowMacroMenu
    {
        /// <summary>
        /// Recompiles every script without the build cache, so all macros expand again. Needed when a macro reads something
        /// Unity doesn't track as a script input, e.g. tags and layers in ProjectSettings/TagManager.asset.
        /// </summary>
        [MenuItem("HowMacro/Recompile All Scripts")]
        static void RecompileAll() => CompilationPipeline.RequestScriptCompilation(RequestScriptCompilationOptions.CleanBuildCache);

        [MenuItem("HowMacro/Open Expansion Log")]
        static void OpenLog()
        {
            var log = Path.GetFullPath("Library/HowMacro/ilpp.log");
            if (File.Exists(log)) EditorUtility.OpenWithDefaultApp(log);
            else Debug.Log("HowMacro: no expansion log yet (" + log + ")");
        }
    }
}
