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

namespace LAAnnotation
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

        public void Initialize()
        {
            CadApplication.BeginDoubleClick += OnBeginDoubleClick;
            CadApplication.DocumentManager.DocumentActivated += OnDocumentActivated;
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            MenuInstaller.Ensure(out var menuMessage);
            AnnotationService.SyncNextNumber(doc);
            ObserveDatabase(doc?.Database);
            doc?.Editor.WriteMessage("\nLA批注已加载。" + menuMessage + " 命令: LA_PZ_NOTE / LA_PZ_EDIT / LA_PZ_DELETE / LA_PZ_CLOUD / LA_PZ_LIST / LA_PZ_SETTINGS / LA_PZ_MENU");
        }

        public void Terminate()
        {
            CadApplication.BeginDoubleClick -= OnBeginDoubleClick;
            CadApplication.DocumentManager.DocumentActivated -= OnDocumentActivated;
            ObserveDatabase(null);
            if (_idleAttached) CadApplication.Idle -= OnIdle;
        }

        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            try
            {
                AnnotationService.SyncNextNumber(e.Document);
                ObserveDatabase(e.Document?.Database);
                LAAnnotation.Views.AnnotationPanel.HandleDocumentActivated(e.Document);
                LAAnnotation.Views.AnnotationListPanel.RefreshIfOpen();
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

        /// <summary>双击事件：检测选中实体是否为 LA 批注，若是则在 Idle 时触发编辑命令（避免事件上下文中直接执行命令）。</summary>
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
                try{LAAnnotation.Views.AnnotationListPanel.RefreshIfOpen();}
                catch(System.Exception ex){PluginLog.Error("AnnotationList.Refresh",ex);}
            }
            var doc = _pendingDocument; var id = _pendingId; _pendingDocument = null; _pendingId = ObjectId.Null;
            if (doc == null || id.IsNull || doc.IsDisposed || CadApplication.DocumentManager.MdiActiveDocument != doc) return;
            try
            {
                doc.Editor.SetImpliedSelection(new[] { id });
                doc.SendStringToExecute("LA_PZ_EDIT ", true, false, false);
            }
            catch (System.Exception ex) { PluginLog.Error("DoubleClick.Idle",ex); }
        }
    }
}
