using System;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Runtime;
using CadApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif

namespace GMAnnotation
{
    /// <summary>插件入口：注册双击编辑、安装菜单、输出加载信息。</summary>
    public sealed class PluginEntry : IExtensionApplication
    {
        /// <summary>双击编辑—待处理的文档引用（避免 Idle 时文档已切换）</summary>
        private static Document _pendingDocument;
        /// <summary>双击编辑—待处理的实体 ID</summary>
        private static ObjectId _pendingId = ObjectId.Null;
        /// <summary>是否已注册 Idle 回调</summary>
        private static bool _idleAttached;
        private static Database _observedDatabase;
        private static bool _listRefreshPending;
        /// <summary>双击编辑期间临时保存 CAD 的快捷特性模式，避免与插件编辑窗口同时弹出。</summary>
        private static object _savedQuickPropertiesMode;
        private static bool _quickPropertiesSuppressed;

        public void Initialize()
        {
            // 启动即写日志：构建时间 + DLL 实际加载路径。排查"改了没生效"时，
            // 看日志最新一条的构建时间就知道 CAD 里跑的是哪个版本（NETLOAD 无法替换运行中的同名程序集）。
            var asm = typeof(PluginEntry).Assembly;
            var buildTime = System.IO.File.GetLastWriteTime(asm.Location);
            PluginLog.Info("Initialize", "构建于 " + buildTime.ToString("yyyy-MM-dd HH:mm:ss") + "，加载自 " + asm.Location);
            CadApplication.BeginDoubleClick += OnBeginDoubleClick;
            CadApplication.DocumentManager.DocumentActivated += OnDocumentActivated;
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            var menuReady = MenuInstaller.EnsureWithRetry();
            // 浮动快捷栏已取消：CAD 原生工具栏是单字按钮的唯一入口。加载时就建好并显示；
            // 建不出来会自动安排一次 Idle 重试，仍不行可用 GM_PZ_TOOLBAR 手动建/显隐。
            var toolbarReady = ToolbarInstaller.EnsureWithRetry();
            AnnotationService.SyncNextNumber(doc);
            ObserveDatabase(doc?.Database);
            doc?.Editor.WriteMessage(
                "\nGM批注已加载。" + (menuReady ? " 菜单已就绪。" : " 菜单稍后自动挂上（或用 GM_PZ_MENU）。") +
                (toolbarReady ? " GM批注工具栏已就绪。" : " GM批注工具栏未就绪（将自动重试，或用 GM_PZ_TOOLBAR）。") +
                " 命令: GM_PZ_DRAW / GM_PZ_NOTE / GM_PZ_EDIT / GM_PZ_MOVE /" +
                " GM_PZ_DELETE / GM_PZ_HIDE / GM_PZ_SHOW / GM_PZ_MERGE / GM_PZ_FILTER / GM_PZ_REFRESH / GM_PZ_CLOUD / GM_PZ_LIST /" +
                " GM_PZ_SUMMARY / GM_PZ_LEGEND / GM_PZ_WORD / GM_PZ_HISTORY / GM_PZ_KB /" +
                " GM_PZ_EXPORT / GM_PZ_IMPORT / GM_PZ_REPAIR /" +
                " GM_PZ_TOOLBAR / GM_PZ_SETTINGS / GM_PZ_INSTALL_AUTOLOAD / GM_PZ_UNINSTALL_AUTOLOAD / GM_PZ_AUTOLOAD / GM_PZ_MENU / GM_PZ_ABOUT");
        }

        public void Terminate()
        {
            CadApplication.BeginDoubleClick -= OnBeginDoubleClick;
            CadApplication.DocumentManager.DocumentActivated -= OnDocumentActivated;
            ObserveDatabase(null);
            if (_idleAttached) CadApplication.Idle -= OnIdle;
            MenuInstaller.Detach();
            ToolbarInstaller.Detach();
            RestoreQuickPropertiesMode();
        }

        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            try
            {
                AnnotationService.SyncNextNumber(e.Document);
                ObserveDatabase(e.Document?.Database);
                GMAnnotation.Views.AnnotationPanel.HandleDocumentActivated(e.Document);
                GMAnnotation.Views.AnnotationListPanel.RefreshIfOpen();
            }
            catch (System.Exception ex) { PluginLog.Error("Document.Activated", ex); }
        }

        private static void ObserveDatabase(Database database)
        {
            if(_observedDatabase==database)return;
            if(_observedDatabase!=null)_observedDatabase.ObjectErased-=OnObjectErased;
            _observedDatabase=database;
            if(_observedDatabase!=null)_observedDatabase.ObjectErased+=OnObjectErased;
        }

        private static void OnObjectErased(object sender,ObjectErasedEventArgs e)
        {
            try
            {
                if(!(e.DBObject is Entity entity)||entity.GetXDataForApplication(AnnotationCodec.AppName)==null)return;
                _listRefreshPending=true;
                if(!_idleAttached){CadApplication.Idle+=OnIdle;_idleAttached=true;}
            }
            catch(System.Exception ex){PluginLog.Error("AnnotationList.ObjectErased",ex);}
        }

        /// <summary>双击事件：检测选中实体是否为 GM 批注，若是则在 Idle 时触发编辑命令（避免事件上下文中直接执行命令）。</summary>
        private static void OnBeginDoubleClick(object sender, BeginDoubleClickEventArgs e)
        {
            try
            {
                if(!SettingsStore.Load().DoubleClickEdit)return;
                var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
                var selected = doc.Editor.SelectImplied(); if (selected.Status != PromptStatus.OK || selected.Value.Count == 0) return;
                var id = selected.Value.GetObjectIds()[0];
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null || !AnnotationService.TryReadFromEntity(tr, entity, out var ignored)) return;
                }
                // BeginDoubleClick 事件无法取消 CAD 的原生后续处理。
                // 仅在命中 GM 批注时临时关闭 QPMODE，并清掉当前预选，防止快捷特性面板抢占前台。
                SuppressQuickPropertiesMode();
                doc.Editor.SetImpliedSelection(new ObjectId[0]);
                _pendingDocument = doc; _pendingId = id;
                if (!_idleAttached) { CadApplication.Idle += OnIdle; _idleAttached = true; }
            }
            catch (System.Exception ex) { PluginLog.Error("DoubleClick",ex); }
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            CadApplication.Idle -= OnIdle; _idleAttached = false;
            if(_listRefreshPending)
            {
                _listRefreshPending=false;
                try{GMAnnotation.Views.AnnotationListPanel.RefreshIfOpen();}
                catch(System.Exception ex){PluginLog.Error("AnnotationList.Refresh",ex);}
            }
            var doc = _pendingDocument; var id = _pendingId; _pendingDocument = null; _pendingId = ObjectId.Null;
            if (doc == null || id.IsNull || doc.IsDisposed || CadApplication.DocumentManager.MdiActiveDocument != doc)
            {
                RestoreQuickPropertiesMode();
                return;
            }
            try
            {
                doc.Editor.SetImpliedSelection(new[] { id });
                doc.SendStringToExecute("GM_PZ_EDIT ", true, false, false);
            }
            catch (System.Exception ex)
            {
                RestoreQuickPropertiesMode();
                PluginLog.Error("DoubleClick.Idle",ex);
            }
        }

        private static void SuppressQuickPropertiesMode()
        {
            if (_quickPropertiesSuppressed) return;
            try
            {
                var mode = CadApplication.GetSystemVariable("QPMODE");
                if (Convert.ToInt32(mode) == 0) return;
                _savedQuickPropertiesMode = mode;
                CadApplication.SetSystemVariable("QPMODE", 0);
                _quickPropertiesSuppressed = true;
            }
            catch (System.Exception ex) { PluginLog.Error("DoubleClick.SuppressQuickProperties", ex); }
        }

        /// <summary>编辑命令取得实体后恢复用户原有的 QPMODE，不永久改写 CAD 偏好。</summary>
        internal static void RestoreQuickPropertiesMode()
        {
            if (!_quickPropertiesSuppressed) return;
            var mode = _savedQuickPropertiesMode;
            _savedQuickPropertiesMode = null;
            _quickPropertiesSuppressed = false;
            try { CadApplication.SetSystemVariable("QPMODE", mode); }
            catch (System.Exception ex) { PluginLog.Error("DoubleClick.RestoreQuickProperties", ex); }
        }
    }
}
