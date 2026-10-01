using System;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using CadApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif

namespace GMAnnotation
{
    /// <summary>
    /// 模型/布局空间的识别、切换与现场恢复。
    /// 截图、定位只在批注"自己的空间"里改视图：模型空间批注切到模型标签，图纸空间批注切到所属布局并激活图纸空间，
    /// 从不进入或修改布局视口的视图（避免改坏视口比例）。
    /// </summary>
    internal static class CadSpaces
    {
        /// <summary>模型空间对应的布局名（布局字典中的内部名，与界面语言无关）。</summary>
        internal const string ModelLayoutName = "Model";

        /// <summary>切换前的现场：当前布局、TILEMODE、CVPORT。</summary>
        internal sealed class SpaceState
        {
            public string Layout;
            public int TileMode;
            public int CvPort;
            public override string ToString() => $"布局={Layout} TILEMODE={TileMode} CVPORT={CvPort}";
        }

        /// <summary>界面上显示的空间名：模型空间显示"模型"，其余为布局名。</summary>
        internal static string DisplayName(bool isModel, string layoutName) => isModel ? "模型" : (string.IsNullOrEmpty(layoutName) ? "未知空间" : layoutName);

        internal static int TileMode => ReadInt("TILEMODE", 1);
        internal static int CvPort => ReadInt("CVPORT", 2);

        internal static SpaceState Capture()
        {
            return new SpaceState { Layout = LayoutManager.Current.CurrentLayout, TileMode = TileMode, CvPort = CvPort };
        }

        /// <summary>当前是否已处在目标空间（模型标签 / 指定布局的图纸空间）。</summary>
        internal static bool IsActive(bool isModel, string layoutName)
        {
            if (isModel) return TileMode == 1;
            return TileMode == 0 && CvPort == 1 && SameName(LayoutManager.Current.CurrentLayout, layoutName);
        }

        /// <summary>
        /// 激活目标空间：模型空间 → 切到模型标签；图纸空间 → 切到所属布局并确保图纸空间激活（CVPORT=1）。
        /// 需要在命令上下文或已锁定文档的情况下调用。返回切换后是否确实处于目标空间。
        /// </summary>
        internal static bool Activate(Document doc, bool isModel, string layoutName)
        {
            if (doc == null || (!isModel && string.IsNullOrEmpty(layoutName))) return false;
            if (IsActive(isModel, layoutName)) return true;
            var manager = LayoutManager.Current;
            if (isModel)
            {
                if (TileMode != 1) manager.CurrentLayout = ModelLayoutName;
                return TileMode == 1;
            }
            if (!SameName(manager.CurrentLayout, layoutName)) manager.CurrentLayout = layoutName;
            if (CvPort != 1) doc.Editor.SwitchToPaperSpace();
            return IsActive(false, layoutName);
        }

        /// <summary>恢复切换前的现场：先回到原布局，再恢复图纸空间/视口激活状态（CVPORT）。视图由调用方随后恢复。</summary>
        internal static bool Restore(Document doc, SpaceState state)
        {
            if (doc == null || state == null || string.IsNullOrEmpty(state.Layout)) return false;
            var manager = LayoutManager.Current;
            if (!SameName(manager.CurrentLayout, state.Layout)) manager.CurrentLayout = state.Layout;
            if (state.TileMode == 0)
            {
                if (state.CvPort == 1)
                {
                    if (CvPort != 1) doc.Editor.SwitchToPaperSpace();
                }
                else if (state.CvPort > 1)
                {
                    if (CvPort == 1) doc.Editor.SwitchToModelSpace();
                    if (CvPort != state.CvPort) TrySetCvPort(state.CvPort);
                }
            }
            else if (CvPort != state.CvPort)
            {
                TrySetCvPort(state.CvPort); // 模型标签下多个平铺视口时回到原激活视口
            }
            return IsState(state);
        }

        /// <summary>当前现场是否与保存的现场一致（用于判断能否安全恢复原视图）。</summary>
        internal static bool IsState(SpaceState state)
        {
            if (state == null) return false;
            return SameName(LayoutManager.Current.CurrentLayout, state.Layout) && TileMode == state.TileMode && CvPort == state.CvPort;
        }

        private static void TrySetCvPort(int value)
        {
            try { CadApplication.SetSystemVariable("CVPORT", value); }
            catch (System.Exception ex) { PluginLog.Warning("CadSpaces.CVPORT", "恢复 CVPORT=" + value + " 失败：" + ex.Message); }
        }

        private static bool SameName(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);

        private static int ReadInt(string name, int fallback)
        {
            try { return Convert.ToInt32(CadApplication.GetSystemVariable(name)); }
            catch { return fallback; }
        }
    }
}
