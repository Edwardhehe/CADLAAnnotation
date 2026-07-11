using System;
using System.Linq;
using System.Reflection;
using System.Windows;
#if ZWCAD
using UiApplication = ZwSoft.ZwCAD.ApplicationServices.Application;
#else
using UiApplication = Autodesk.AutoCAD.ApplicationServices.Application;
#endif

namespace LAAnnotation
{
    /// <summary>WPF 模态窗口适配层：优先调用 CAD 原生 ShowModalWindow，失败则回退为 Window.ShowDialog。</summary>
    internal static class CadDialog
    {
        public static bool? ShowModal(Window window)
        {
            window.WindowStartupLocation=WindowStartupLocation.CenterScreen;
            try
            {
                var method=typeof(UiApplication).GetMethods(BindingFlags.Public|BindingFlags.Static).FirstOrDefault(m=>m.Name=="ShowModalWindow"&&m.GetParameters().Length==1&&m.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(Window)));
                if(method!=null){var result=method.Invoke(null,new object[]{window});if(result is bool b)return b;return window.DialogResult;}
            }
            catch(Exception ex){PluginLog.Error("WPF.ShowModalWindow",ex);}
            return window.ShowDialog();
        }
    }
}
