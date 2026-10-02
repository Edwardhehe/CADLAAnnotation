using System;
using System.Reflection;
#if ZWCAD
using UiApplication = ZwSoft.ZwCAD.ApplicationServices.Application;
using CadApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
#elif ACAD_CORE
using UiApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using UiApplication = Autodesk.AutoCAD.ApplicationServices.Application;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif

namespace GMAnnotation
{
    /// <summary>
    /// CAD 菜单安装器（对齐「批量打印」CadMenuInstaller）：
    /// COM 反射操作菜单；重复加载时复用同名菜单并原地重建菜单项；
    /// 已在菜单栏上的弹出菜单不重复 InsertInMenuBar。
    /// </summary>
    internal static class MenuInstaller
    {
        private const string MenuName = "GM批注";
#if ZWCAD
        private const string PreferredMenuGroupName = "ZWCAD";
#else
        private const string PreferredMenuGroupName = "ACAD";
#endif


        private static bool _retryAttached;

        /// <summary>
        /// 加载时创建/刷新菜单；若宿主界面尚未就绪则挂一次 Idle 重试。
        /// NETLOAD / 启动自动加载时都会走这里，无需再手动执行 GM_PZ_MENU。
        /// </summary>
        public static bool EnsureWithRetry()
        {
            if (Ensure(out var message))
            {
                PluginLog.Info("Menu", message);
                return true;
            }

            PluginLog.Warning("Menu", message ?? "菜单首次安装失败，将在 Idle 时重试。");
            if (_retryAttached)
            {
                return false;
            }

            try
            {
                CadApplication.Idle += OnRetryIdle;
                _retryAttached = true;
            }
            catch (Exception ex)
            {
                _retryAttached = false;
                PluginLog.Error("Menu.Retry", ex);
            }

            return false;
        }

        /// <summary>卸载插件时摘掉 Idle 重试回调。</summary>
        public static void Detach()
        {
            if (!_retryAttached)
            {
                return;
            }

            _retryAttached = false;
            try
            {
                CadApplication.Idle -= OnRetryIdle;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Menu.Detach", ex);
            }
        }

        private static void OnRetryIdle(object sender, EventArgs e)
        {
            Detach();
            if (Ensure(out var message))
            {
                PluginLog.Info("Menu.Retry", message);
                try
                {
                    var doc = CadApplication.DocumentManager.MdiActiveDocument;
                    doc?.Editor.WriteMessage("\n" + message);
                }
                catch
                {
                }
            }
            else
            {
                PluginLog.Warning("Menu.Retry", message ?? "菜单 Idle 重试仍失败。");
            }
        }
        public static bool Ensure(out string message)
        {
            try
            {
                ShowMenuBar();

                object menuBar;
                object menuGroups;
#if ACAD_CORE
                // AutoCAD 2025+ Core：MenuBar / MenuGroups 走 COM 反射
                var acadApplication = GetAcadApplication();
                menuBar = acadApplication == null ? null : GetComProperty(acadApplication, "MenuBar");
                menuGroups = acadApplication == null ? null : GetComProperty(acadApplication, "MenuGroups");
#else
                var appType = typeof(UiApplication);
                menuBar = GetStatic(appType, "MenuBar");
                menuGroups = GetStatic(appType, "MenuGroups");
#endif
                if (menuBar == null || menuGroups == null)
                {
                    message = "当前 CAD 未公开菜单栏接口。";
                    return false;
                }

                var menuGroup = InvokeItem(menuGroups, PreferredMenuGroupName)
                    ?? InvokeItem(menuGroups, 0);
                if (menuGroup == null)
                {
                    message = "未取得默认菜单组。";
                    return false;
                }

                var menus = GetComProperty(menuGroup, "Menus");
                if (menus == null)
                {
                    message = "未取得菜单集合。";
                    return false;
                }

                var menu = GetOrCreateMenu(menus);
                if (menu == null)
                {
                    message = "菜单创建失败。";
                    return false;
                }

                var oldCount = Convert.ToInt32(GetComProperty(menu, "Count") ?? 0);
                for (var i = 0; i < oldCount; i++)
                {
                    var first = TryInvoke(menu, "Item", 0);
                    if (first == null)
                    {
                        break;
                    }

                    TryInvoke(first, "Delete");
                }

                var index = 0;
                AddCommandMenuItem(menu, index++, "绘制批注（面板）", "GM_PZ_NOTE");
                AddCommandMenuItem(menu, index++, "绘制批注", "GM_PZ_DRAW");
                AddCommandMenuItem(menu, index++, "编辑批注", "GM_PZ_EDIT");
                AddCommandMenuItem(menu, index++, "移动批注", "GM_PZ_MOVE");
                AddCommandMenuItem(menu, index++, "删除批注", "GM_PZ_DELETE");
                AddCommandMenuItem(menu, index++, "隐藏批注", "GM_PZ_HIDE");
                AddCommandMenuItem(menu, index++, "显示批注", "GM_PZ_SHOW");
                AddCommandMenuItem(menu, index++, "合并批注", "GM_PZ_MERGE");
                AddCommandMenuItem(menu, index++, "过滤批注", "GM_PZ_FILTER");
                AddCommandMenuItem(menu, index++, "格式刷", "GM_PZ_FORMAT");
                AddCommandMenuItem(menu, index++, "刷新批注文字", "GM_PZ_REFRESH");
                AddCommandMenuItem(menu, index++, "单绘云线", "GM_PZ_CLOUD");
                AddCommandMenuItem(menu, index++, "增补云线", "GM_PZ_ADDCLOUD");
                AddCommandMenuItem(menu, index++, "批注列表", "GM_PZ_LIST");
                AddCommandMenuItem(menu, index++, "批注汇总", "GM_PZ_SUMMARY");
                AddCommandMenuItem(menu, index++, "批注清单", "GM_PZ_LEGEND");
                AddCommandMenuItem(menu, index++, "批注历史", "GM_PZ_HISTORY");
                AddCommandMenuItem(menu, index++, "知识库", "GM_PZ_KB");
                AddCommandMenuItem(menu, index++, "导出Word", "GM_PZ_WORD");
                AddCommandMenuItem(menu, index++, "导出批注", "GM_PZ_EXPORT");
                AddCommandMenuItem(menu, index++, "导入批注", "GM_PZ_IMPORT");
                AddCommandMenuItem(menu, index++, "修复批注", "GM_PZ_REPAIR");
                AddCommandMenuItem(menu, index++, "显示/隐藏工具栏", "GM_PZ_TOOLBAR");
                TryInvoke(menu, "AddSeparator", index++);
                AddCommandMenuItem(menu, index++, "批注设置", "GM_PZ_SETTINGS");
                AddCommandMenuItem(menu, index++, "安装自动加载", "GM_PZ_INSTALL_AUTOLOAD");
                AddCommandMenuItem(menu, index++, "卸载自动加载", "GM_PZ_UNINSTALL_AUTOLOAD");
                AddCommandMenuItem(menu, index++, "重新加载菜单", "GM_PZ_MENU");
                TryInvoke(menu, "AddSeparator", index++);
                AddCommandMenuItem(menu, index++, "关于", "GM_PZ_ABOUT");

                if (!IsMenuOnMenuBar(menu))
                {
                    var menuBarCount = Convert.ToInt32(GetComProperty(menuBar, "Count") ?? 0);
                    TryInvoke(menu, "InsertInMenuBar", menuBarCount + 1);
                }

                if (!IsMenuOnMenuBar(menu))
                {
                    message = "GM批注菜单已创建，但未能插入菜单栏。";
                    return false;
                }

                message = "GM批注菜单已创建或刷新。";
                return true;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Menu", ex);
                message = "菜单创建失败: " + ex.Message;
                return false;
            }
        }

        private static void ShowMenuBar()
        {
            try
            {
                UiApplication.SetSystemVariable("MENUBAR", 1);
            }
            catch
            {
            }
        }

        private static object GetOrCreateMenu(object menus)
        {
            var menu = TryGetMenu(menus);
            if (menu != null)
            {
                return menu;
            }

            return TryInvoke(menus, "Add", MenuName);
        }

        private static object TryGetMenu(object menus)
        {
            try
            {
                return menus.GetType().InvokeMember(
                    "Item",
                    BindingFlags.InvokeMethod,
                    null,
                    menus,
                    new object[] { MenuName });
            }
            catch (TargetInvocationException)
            {
                return null;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static bool IsMenuOnMenuBar(object menu)
        {
            try
            {
                return Convert.ToBoolean(
                    menu.GetType().InvokeMember(
                        "OnMenuBar",
                        BindingFlags.GetProperty,
                        null,
                        menu,
                        null));
            }
            catch (TargetInvocationException)
            {
                return false;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        private static void AddCommandMenuItem(object menu, int index, string displayName, string commandName)
        {
            TryInvoke(menu, "AddMenuItem", index, displayName, CreateMenuMacro(commandName));
        }

        internal static string CreateMenuMacro(string commandName)
        {
            return new string((char)3, 2) + "_" + commandName + " ";
        }

        internal static object GetStatic(Type type, string name)
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            return property?.GetValue(null, null);
        }

#if ACAD_CORE
        private static object GetAcadApplication()
        {
            var applicationTypeNames = new[]
            {
                "Autodesk.AutoCAD.ApplicationServices.Application, AcMgd",
                "Autodesk.AutoCAD.ApplicationServices.Core.Application, AcCoreMgd"
            };

            foreach (var typeName in applicationTypeNames)
            {
                var type = Type.GetType(typeName, throwOnError: false);
                if (type == null)
                {
                    continue;
                }

                var acadApplication = GetStatic(type, "AcadApplication");
                if (acadApplication != null)
                {
                    return acadApplication;
                }
            }

            return null;
        }
#endif

        private static object GetComProperty(object target, string name)
        {
            try
            {
                return target.GetType().InvokeMember(
                    name,
                    BindingFlags.GetProperty,
                    null,
                    target,
                    null);
            }
            catch
            {
                return null;
            }
        }

        private static object InvokeItem(object collection, object key)
        {
            return TryInvoke(collection, "Item", key);
        }

        private static object TryInvoke(object target, string method, params object[] args)
        {
            try
            {
                return target.GetType().InvokeMember(
                    method,
                    BindingFlags.InvokeMethod,
                    null,
                    target,
                    args);
            }
            catch
            {
                return null;
            }
        }
    }

    internal static class ReflectionTool
    {
        public static object GetProperty(this object obj, string key)
        {
            return obj.GetType().InvokeMember(key, BindingFlags.GetProperty, null, obj, null);
        }

        public static object InvokeMethod(this object obj, string method, params object[] objArray)
        {
            return obj.GetType().InvokeMember(method, BindingFlags.InvokeMethod, null, obj, objArray);
        }
    }
}