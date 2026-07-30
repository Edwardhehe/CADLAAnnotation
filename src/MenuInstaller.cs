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
        /// <summary>
        /// 批注菜单名称。
        /// </summary>
        /// <remarks>
        /// CAD 宿主在同一个菜单组中不允许存在同名弹出菜单。链式动态加载或重复 NETLOAD 时，
        /// 如果再次直接调用 Add 创建同名菜单，会触发"菜单组中存在弹出菜单"的 COM 异常。
        /// </remarks>
        private const string MenuName = "LA批注";

        /// <summary>
        /// 添加或刷新批注菜单。
        /// </summary>
        /// <remarks>
        /// 该方法支持重复调用：如果菜单已经存在，则复用已有菜单并重建菜单项；
        /// 如果菜单尚不存在，则创建新菜单。这样可以避免插件链式加载时重复创建同名菜单导致宿主报错。
        /// </remarks>
        public static bool Ensure(out string message)
        {
            try
            {
                var appType = typeof(UiApplication);
                var menuBar = GetStatic(appType, "MenuBar");
                var menuGroups = GetStatic(appType, "MenuGroups");
                if (menuBar == null || menuGroups == null) { message = "当前 CAD 未公开菜单栏接口。"; return false; }

#if ZWCAD
                var menus = menuGroups.InvokeMethod("Item", 0).GetProperty("Menus");
#else
                var menus = menuGroups.InvokeMethod("Item", "Acad").GetProperty("Menus");
#endif
                var menu = GetOrCreateMenu(menus);

                // 重复加载时复用旧菜单，因此先清空旧菜单项，再按当前代码重新构建完整菜单。
                while (Convert.ToInt32(menu.GetProperty("Count")) > 0)
                {
                    menu.InvokeMethod("Item", 0).InvokeMethod("Delete");
                }

                var index = 0;
                AddCommandMenuItem(menu, index++, "绘制批注", "LA_PZ_NOTE");
                AddCommandMenuItem(menu, index++, "编辑批注", "LA_PZ_EDIT");
                AddCommandMenuItem(menu, index++, "移动批注", "LA_PZ_MOVE");
                AddCommandMenuItem(menu, index++, "删除批注", "LA_PZ_DELETE");
                AddCommandMenuItem(menu, index++, "单绘云线", "LA_PZ_CLOUD");
                AddCommandMenuItem(menu, index++, "增补云线", "LA_PZ_ADDCLOUD");
                AddCommandMenuItem(menu, index++, "批注列表", "LA_PZ_LIST");
                menu.InvokeMethod("AddSeparator", index++);
                AddCommandMenuItem(menu, index++, "批注设置", "LA_PZ_SETTINGS");
                AddCommandMenuItem(menu, index++, "设置自动加载", "LA_PZ_AUTOLOAD");
                AddCommandMenuItem(menu, index++, "重新加载菜单", "LA_PZ_MENU");

                // 已经在菜单栏上的弹出菜单不能重复插入，否则部分 CAD 宿主会抛出 COM 反射异常。
                if (!IsMenuOnMenuBar(menu))
                {
                    menu.InvokeMethod("InsertInMenuBar", Convert.ToInt32(menuBar.GetProperty("Count")) + 1);
                }
                message = "LA批注菜单已创建或刷新。"; return true;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Menu", ex);
                message = "菜单创建失败: " + ex.Message; return false;
            }
        }

        /// <summary>
        /// 获取已经存在的批注菜单；如果不存在，则创建后再返回。
        /// </summary>
        /// <param name="menus">CAD 宿主菜单集合 COM 对象。</param>
        /// <returns>批注弹出菜单 COM 对象。</returns>
        private static object GetOrCreateMenu(object menus)
        {
            var menu = TryGetMenu(menus);
            if (menu != null)
            {
                return menu;
            }

            // 只有确认不存在同名菜单时才调用 Add，避免"菜单组中存在弹出菜单"异常。
            menus.InvokeMethod("Add", MenuName);

            return menus.InvokeMethod("Item", MenuName);
        }

        /// <summary>
        /// 尝试从菜单集合中按名称查找批注菜单。
        /// </summary>
        /// <param name="menus">CAD 宿主菜单集合 COM 对象。</param>
        /// <returns>找到时返回菜单 COM 对象；找不到或宿主 COM 查询失败时返回 <c>null</c>。</returns>
        private static object TryGetMenu(object menus)
        {
            try
            {
                return menus.InvokeMethod("Item", MenuName);
            }
            catch (TargetInvocationException)
            {
                // COM 反射调用会把宿主内部异常包装为 TargetInvocationException；
                // 对于按名称查询菜单的场景，查询失败等价于"菜单不存在"。
                return null;
            }
            catch (ArgumentException)
            {
                // 部分宿主在 Item(name) 找不到对象时直接抛 ArgumentException。
                return null;
            }
        }

        /// <summary>
        /// 判断菜单是否已经插入到 CAD 菜单栏。
        /// </summary>
        /// <param name="menu">批注弹出菜单 COM 对象。</param>
        /// <returns>如果已经显示在菜单栏上则返回 <c>true</c>；否则返回 <c>false</c>。</returns>
        private static bool IsMenuOnMenuBar(object menu)
        {
            try
            {
                return Convert.ToBoolean(menu.GetProperty("OnMenuBar"));
            }
            catch (TargetInvocationException)
            {
                // OnMenuBar 在部分宿主或特定菜单状态下可能不可读；读取失败时按未插入处理。
                return false;
            }
            catch (ArgumentException)
            {
                // 兼容 COM 属性不存在或宿主返回参数异常的情况。
                return false;
            }
        }

        private static void AddCommandMenuItem(object menu, int index, string displayName, string commandName)
        {
            menu.InvokeMethod("AddMenuItem", index, displayName, CreateMenuMacro(commandName));
        }

        private static string CreateMenuMacro(string commandName)
        {
            // 前缀为两个 Ctrl+C 连按两次取消当前命令，_ 前缀保证命令名在本地化宿主中仍然有效。
            return new string((char)3, 2) + "_" + commandName + " ";
        }

        private static object GetStatic(Type type, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            return property?.GetValue(null, null);
        }
    }

    /// <summary>
    /// 反射工具
    /// </summary>
    internal static class ReflectionTool
    {
        /// <summary>
        /// com接口获取对象属性值，类似VisualLisp的vlax-get-property函数
        /// </summary>
        /// <param name="obj">对象</param>
        /// <param name="key">属性名称</param>
        /// <returns>属性值</returns>
        public static object GetProperty(this object obj, string key)
        {
            return obj.GetType().InvokeMember(key, BindingFlags.GetProperty, null, obj, null);
        }

        /// <summary>
        /// com接口使用com方法，类似VisualLisp的vlax-invoke-method函数
        /// </summary>
        /// <param name="obj">对象</param>
        /// <param name="method">方法名</param>
        /// <param name="objArray">方法需要的参数</param>
        /// <returns>方法的返回值</returns>
        public static object InvokeMethod(this object obj, string method, params object[] objArray)
        {
            return obj.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, obj, objArray);
        }
    }
}
