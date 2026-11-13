using System.Text.RegularExpressions;
using CSMacro;

namespace Demo.Macros
{
    /// <summary>片段宏示例：处理 #region @NAME … #endregion 之间的成员。</summary>
    public static class Regions
    {
        /// <summary>
        /// 逐成员替换：把片段里的每个可写字段都变成"改变时自动打 log"的属性。
        /// 测试时用它包住一组字段就行，测完删掉 #region 那两行即可恢复原样。
        /// <code>
        /// #region @LOG_ALL
        /// public int Hp = 50;
        /// public string State = "Idle";
        /// #endregion
        /// </code>
        /// 跳过：readonly / const 字段、已经被其他宏展开的字段（比如 @GETSET）、属性和方法。
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
        /// 整段文本替换：把片段里的 private 全部换成 public（比如临时让测试代码访问内部状态）。
        /// 演示 Macro.RegionText + Macro.Emit 的纯文本匹配替换：逐行替换，行数不变。
        /// 名字和类型没变，所以通过核心原则检查。
        /// </summary>
        [RegionMacro]
        public static void PUBLIC_ALL()
        {
            Macro.Emit(Regex.Replace(Macro.RegionText, @"\bprivate\b", "public"));
        }
    }
}
