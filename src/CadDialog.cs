using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
#if ZWCAD
using UiApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
#else
using UiApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif

namespace LAAnnotation
{
    /// <summary>WPF 模态窗口适配层：优先调用 CAD 原生 ShowModalWindow，失败则回退为 Window.ShowDialog。</summary>
    internal static class CadDialog
    {
        public static bool? ShowModal(Window window)
        {
            AttachToCadMainWindow(window);
            window.Loaded += BringToForeground;
            try
            {
                var method=typeof(UiApplication).GetMethods(BindingFlags.Public|BindingFlags.Static).FirstOrDefault(m=>m.Name=="ShowModalWindow"&&m.GetParameters().Length==1&&m.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(Window)));
                if(method!=null){var result=method.Invoke(null,new object[]{window});if(result is bool b)return b;return window.DialogResult;}
            }
            catch(Exception ex){PluginLog.Error("WPF.ShowModalWindow",ex);}
            try { return window.ShowDialog(); }
            finally { window.Loaded -= BringToForeground; }
        }

        /// <summary>显式把 WPF 窗口挂到 CAD 主窗口，避免命令从 Idle 启动时编辑窗口落到 CAD 后面。</summary>
        private static void AttachToCadMainWindow(Window window)
        {
            var handle=GetCadMainWindowHandle();
            if(handle!=IntPtr.Zero)
            {
                new WindowInteropHelper(window).Owner=handle;
                window.WindowStartupLocation=WindowStartupLocation.CenterOwner;
            }
            else window.WindowStartupLocation=WindowStartupLocation.CenterScreen;
        }

        private static IntPtr GetCadMainWindowHandle()
        {
            try
            {
                var mainWindowProperty=typeof(UiApplication).GetProperty("MainWindow",BindingFlags.Public|BindingFlags.Static);
                var mainWindow=mainWindowProperty?.GetValue(null,null);
                var handleProperty=mainWindow?.GetType().GetProperty("Handle",BindingFlags.Public|BindingFlags.Instance);
                var value=handleProperty?.GetValue(mainWindow,null);
                if(value is IntPtr handle&&handle!=IntPtr.Zero)return handle;
            }
            catch(Exception ex){PluginLog.Error("WPF.MainWindowHandle",ex);}
            return Process.GetCurrentProcess().MainWindowHandle;
        }

        private static void BringToForeground(object sender,RoutedEventArgs e)
        {
            var window=(Window)sender;
            window.Activate();
            window.Focus();
        }
    }
}
