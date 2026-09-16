using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;
#if ZWCAD
using UiApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
using CadDocument = ZwSoft.ZwCAD.ApplicationServices.Document;
#else
using UiApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CadDocument = Autodesk.AutoCAD.ApplicationServices.Document;
#endif

namespace GMAnnotation
{
    /// <summary>WPF 模态窗口适配层：优先调用 CAD 原生 ShowModalWindow，失败则回退为 Window.ShowDialog。</summary>
    internal static class CadDialog
    {
        public static bool? ShowModal(Window window)
        {
            // 挂主窗口失败不应阻断弹窗：仅记日志，继续走后续流程（窗口会退回居中屏幕显示）。
            try { AttachToCadMainWindow(window); }
            catch (Exception ex) { PluginLog.Error("WPF.AttachToCadMainWindow", ex); }
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

        /// <summary>打开批注窗口，并处理"拾取图面文字"请求。
        /// 批注窗口是 CAD 模态窗口，显示期间无法与图形交互，所以"拾取文字"的流程是：
        /// 窗口把已填内容存回数据对象后关闭 → 在图上点选文字 → 重开窗口（已填内容原样带回）。</summary>
        public static bool ShowAnnotation(AnnotationData data, bool editing)
        {
            CadDocument doc = null;
            try { doc = UiApplication.DocumentManager.MdiActiveDocument; }
            catch (Exception ex) { PluginLog.Error("AnnotationDialog.Document", ex); }

            while (true)
            {
                var form = new Views.AnnotationWindow(data, editing);
                var result = ShowModal(form);
                if (form.AppendCloudRequested)
                {
                    // 「增补云线」：窗口已把已填内容存回 data 并关闭。编辑器现在可交互，
                    // 执行连续框选的增补会话（图层/颜色/线宽照抄原批注，外形与云线样式按当前设置），随后重开窗口继续编辑。
                    if (doc == null || doc.IsDisposed)
                    {
                        PluginLog.Warning("AnnotationDialog.AppendCloud", "当前没有可用的图纸，已取消增补。");
                        return false;
                    }
                    try { Commands.AppendCloudSession(doc, data); }
                    catch (Exception ex)
                    {
                        PluginLog.Error("AnnotationDialog.AppendCloud", ex);
                        doc.Editor.WriteMessage("\n增补云线失败: " + ex.Message);
                    }
                    continue;
                }
                if (!form.PickTextRequested) return result == true;
                if (doc == null || doc.IsDisposed)
                {
                    PluginLog.Warning("AnnotationDialog.PickText", "当前没有可用的图纸，已取消拾取。");
                    return false;
                }
                try
                {
                    var picked = AnnotationService.PickText(doc);
                    if (!string.IsNullOrEmpty(picked))
                    {
                        data.DrawingNo = picked;
                        doc.Editor.WriteMessage("\n已拾取图号: " + picked);
                    }
                }
                catch (Exception ex)
                {
                    PluginLog.Error("AnnotationDialog.PickText", ex);
                    doc.Editor.WriteMessage("\n拾取图号文字失败: " + ex.Message);
                }
            }
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
