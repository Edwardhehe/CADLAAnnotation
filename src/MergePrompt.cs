using System;
using System.Linq;
using System.Windows;
#if ZWCAD
using CadApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
#else
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif

namespace GMAnnotation
{
    /// <summary>「合并批注」执行前的确认框：先统计本图内容完全相同的批注分组，
    /// 把要动的东西（保留哪条、并入多少条、删掉什么）讲清楚再让用户决定。
    /// 命令（GM_PZ_MERGE）与过滤窗口的按钮共用同一段文案。</summary>
    internal static class MergePrompt
    {
        /// <summary>返回用户是否确认合并。<paramref name="owner"/> 可为 null（命令入口没有窗口，此时退回无 owner 的 MessageBox）。</summary>
        internal static bool Confirm(Window owner)
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                Show(owner, "当前没有打开的 CAD 图纸。", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            var groups = AnnotationService.GetContentGroups(doc).Where(g => g.Count > 1).ToList();
            if (groups.Count == 0)
            {
                Show(owner, "图中没有内容完全相同的批注，无需合并。", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            var total = groups.Sum(g => g.Count);
            var preview = string.Join("\n", groups.Take(8).Select(g => "  " + g.Numbers + " × " + g.Preview).ToArray());
            if (groups.Count > 8) preview += "\n  …（其余 " + (groups.Count - 8) + " 组省略）";

            var message = "检测到 " + groups.Count + " 组内容完全相同的批注（共 " + total + " 条）：\n\n" + preview +
                          "\n\n合并后每组只保留编号最小的一条（共 " + groups.Count + " 条）：其余 " + (total - groups.Count) +
                          " 条的云线与引线并入保留的那条批注，重复批注的文字与文字框删除。\n" +
                          "图形支持 UNDO 撤销。\n\n是否继续？";
            return Show(owner, message, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        /// <summary>统一弹框入口：**owner 为 null 时绝不能传给 MessageBox.Show(Window,…)**——
        /// WPF 内部会 new WindowInteropHelper(window) 直接抛 ArgumentNullException（命令入口无窗口，必踩）。
        /// 有 owner 就带 owner（对话框居中并归它管），没有就走无 owner 重载。</summary>
        private static MessageBoxResult Show(Window owner, string message, MessageBoxButton button, MessageBoxImage icon)
        {
            if (owner != null) { try { return MessageBox.Show(owner, message, Caption, button, icon); } catch { /* owner 不可用则走无 owner 弹框 */ } }
            try
            {
                var main = System.Windows.Application.Current != null ? System.Windows.Application.Current.MainWindow : null;
                if (main != null) return MessageBox.Show(main, message, Caption, button, icon);
            }
            catch { /* 主窗口不可用则走无 owner 弹框 */ }
            return MessageBox.Show(message, Caption, button, icon);
        }

        private const string Caption = "合并批注";
    }
}
