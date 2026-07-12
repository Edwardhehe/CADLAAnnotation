using System;
using System.Reflection;
#if ZWCAD
using UiApplication = ZwSoft.ZwCAD.ApplicationServices.Application;
#else
using UiApplication = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

namespace LAAnnotation
{
    /// <summary>通过反射操作 CAD 菜单栏，创建"LA批注"下拉菜单及其子项。</summary>
    internal static class MenuInstaller
    {
        private const string MenuName = "LA批注";

        /// <summary>确保菜单存在：不存在则创建，已存在则跳过。</summary>
        public static bool Ensure(out string message)
        {
            try
            {
                var appType = typeof(UiApplication);
                var menuBar = GetStatic(appType, "MenuBar");
                var menuGroups = GetStatic(appType, "MenuGroups");
                if (menuBar == null || menuGroups == null) { message = "当前 CAD 未公开菜单栏接口。"; return false; }

                dynamic bar = menuBar;
                for (var i = 0; i < Convert.ToInt32(bar.Count); i++)
                {
                    dynamic existing = bar.Item(i);
                    if (string.Equals(Convert.ToString(existing.Name), MenuName, StringComparison.OrdinalIgnoreCase))
                    { message = "LA批注菜单已存在。"; return true; }
                }

                dynamic groups = menuGroups;
                dynamic group = groups.Item(0);
                dynamic menu = group.Menus.Add(MenuName);
                var index = 0;
                menu.AddMenuItem(index++, "绘制批注", "LA_PZ_NOTE ");
                menu.AddMenuItem(index++, "编辑批注", "LA_PZ_EDIT ");
                menu.AddMenuItem(index++, "删除批注", "LA_PZ_DELETE ");
                menu.AddMenuItem(index++, "单绘云线", "LA_PZ_CLOUD ");
                menu.AddMenuItem(index++, "批注列表", "LA_PZ_LIST ");
                TryAddSeparator(menu, index++);
                menu.AddMenuItem(index++, "批注设置", "LA_PZ_SETTINGS ");
                menu.AddMenuItem(index++, "重新加载菜单", "LA_PZ_MENU ");
                menu.InsertInMenuBar(Convert.ToInt32(bar.Count));
                message = "LA批注菜单已创建。"; return true;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Menu",ex);
                message = "菜单创建失败: " + ex.Message; return false;
            }
        }

        private static object GetStatic(Type type, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            return property?.GetValue(null, null);
        }

        private static void TryAddSeparator(dynamic menu, int index)
        {
            try { menu.AddSeparator(index); } catch { }
        }
    }
}
