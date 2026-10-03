using System;
using System.Collections.Generic;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using ZwSoft.ZwCAD.Runtime;
using CadApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif

namespace GMAnnotation
{
    /// <summary>插件入口：注册双击编辑、安装菜单、输出加载信息。</summary>
    public sealed class PluginEntry : IExtensionApplication
    {
        private const string EditCommandName = "GM_PZ_EDIT";
        /// <summary>双击目标交给 GM_PZ_EDIT 后的有效期：超时未被领取则作废，避免之后手动执行 GM_PZ_EDIT 时误用旧目标。</summary>
        private static readonly TimeSpan HandoffLifetime = TimeSpan.FromSeconds(15);
        /// <summary>发出 GM_PZ_EDIT 后等待其启动的宽限期；超时且没有命令在运行，就判定命令被吞掉并恢复系统变量。</summary>
        private static readonly TimeSpan HandoffGrace = TimeSpan.FromSeconds(3);

        /// <summary>双击编辑—待在 Idle 中发送命令的文档与实体（避免在双击事件上下文中直接执行命令）。</summary>
        private static Document _pendingDocument;
        private static ObjectId _pendingId = ObjectId.Null;
        /// <summary>双击编辑—已发送 GM_PZ_EDIT、等待其领取的目标。通过静态字段传递而不是预选集，
        /// 这样命令前可以加 ^C^C 取消 CAD 原生双击编辑器（^C 会清空预选集）。</summary>
        private static Document _handoffDocument;
        private static ObjectId _handoffId = ObjectId.Null;
        private static DateTime _handoffSentAt;
        /// <summary>GM_PZ_EDIT 正在执行（含编辑窗口打开期间）。</summary>
        private static bool _editRunning;
        /// <summary>是否已注册 Idle 回调</summary>
        private static bool _idleAttached;
        private static Database _observedDatabase;
        private static bool _listRefreshPending;
        /// <summary>双击编辑期间临时保存 CAD 的快捷特性模式，避免与插件编辑窗口同时弹出。</summary>
        private static object _savedQuickPropertiesMode;
        private static bool _quickPropertiesSuppressed;
        /// <summary>双击编辑期间临时关闭 CAD 原生双击编辑（DBLCLKEDIT），避免原生 MTEDIT/PEDIT 抢先打开。</summary>
        private static object _savedDoubleClickEditMode;
        private static bool _doubleClickEditSuppressed;
        /// <summary>监听命令结束/取消/失败的文档（兜底恢复系统变量）。</summary>
        private static Document _commandWatchDocument;
        /// <summary>监听 UNDO/REDO 类命令以刷新批注列表的文档（始终是当前活动文档）。</summary>
        private static Document _undoWatchDocument;
        private static readonly HashSet<string> UndoCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "U", "UNDO", "REDO", "MREDO", "OOPS" };
        /// <summary>settings.xml 中记录"双击编辑期间临时改动、尚未恢复的系统变量"的节点名（CAD 异常退出后下次加载时恢复）。</summary>
        private const string PendingRestoreKey = "PendingSysvarRestore";

        public void Initialize()
        {
            // 启动即写日志：构建时间 + DLL 实际加载路径。排查"改了没生效"时，
            // 看日志最新一条的构建时间就知道 CAD 里跑的是哪个版本（NETLOAD 无法替换运行中的同名程序集）。
            var asm = typeof(PluginEntry).Assembly;
            var buildTime = System.IO.File.GetLastWriteTime(asm.Location);
            PluginLog.Info("Initialize", "构建于 " + buildTime.ToString("yyyy-MM-dd HH:mm:ss") + "，加载自 " + asm.Location);
            // .NET 8（AutoCAD 2025+）默认不含 GBK 等代码页，CSV 导入识别 GBK 前必须先注册。
            DataFiles.EnsureEncodings();
            // 上次双击编辑期间 CAD 异常退出：QPMODE/DBLCLKEDIT 还停留在临时关闭状态，这里恢复。
            RecoverPendingSystemVariables();
            // 上次绘制批注（选点/定位）期间 CAD 异常退出：正交/捕捉还停在临时关闭状态，这里恢复。
            AnnotationService.RecoverDrawingAids();
            // BeginDoubleClick 是应用级事件，覆盖之后新建/打开的所有文档，无需逐文档注册。
            CadApplication.BeginDoubleClick += OnBeginDoubleClick;
            CadApplication.DocumentManager.DocumentActivated += OnDocumentActivated;
            CadApplication.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            var menuReady = MenuInstaller.EnsureWithRetry();
            // 浮动快捷栏已取消：CAD 原生工具栏是单字按钮的唯一入口。加载时就建好，但启动时不自动显示；
            // 建不出来会自动安排一次 Idle 重试，仍不行可用 GM_PZ_TOOLBAR 手动建/显隐。
            // 需要时用菜单「显示/隐藏工具栏」或 GM_PZ_TOOLBAR_SHOW / GMPANEL 打开（只影响本次会话）。
            var toolbarReady = ToolbarInstaller.EnsureWithRetry();
            AnnotationService.SyncNextNumber(doc);
            ObserveDatabase(doc?.Database);
            WatchUndo(doc);
            doc?.Editor.WriteMessage(
                "\nGM批注已加载。" + (menuReady ? " 菜单已就绪。" : " 菜单稍后自动挂上（或用 GM_PZ_MENU）。") +
                (toolbarReady
                    ? " GM批注工具栏已就绪（启动时不自动显示，菜单「显示/隐藏工具栏」或 GM_PZ_TOOLBAR_SHOW 打开）。"
                    : " GM批注工具栏未就绪（将自动重试，或用 GM_PZ_TOOLBAR / GMPANEL）。") +
                " 命令: GM_PZ_DRAW / GM_PZ_NOTE / GM_PZ_EDIT / GM_PZ_MOVE /" +
                " GM_PZ_DELETE / GM_PZ_HIDE / GM_PZ_SHOW / GM_PZ_MERGE / GM_PZ_FILTER / GM_PZ_FORMAT / GM_PZ_REFRESH / GM_PZ_CLOUD / GM_PZ_ADDCLOUD / GM_PZ_LIST /" +
                " GM_PZ_SUMMARY / GM_PZ_LEGEND / GM_PZ_WORD / GM_PZ_HISTORY / GM_PZ_KB /" +
                " GM_PZ_EXPORT / GM_PZ_IMPORT / GM_PZ_REPAIR /" +
                " GM_PZ_TOOLBAR / GM_PZ_TOOLBAR_SHOW / GM_PZ_TOOLBAR_HIDE / GMPANEL / GM_PZ_SETTINGS /" +
                " GM_PZ_INSTALL_AUTOLOAD / GM_PZ_UNINSTALL_AUTOLOAD / GM_PZ_AUTOLOAD / GM_PZ_MENU / GM_PZ_ABOUT");
            if (doc != null && SettingsStore.IsCorrupt)
                doc.Editor.WriteMessage("\n警告：设置文件 settings.xml 无法解析，当前使用默认设置" +
                    (string.IsNullOrEmpty(SettingsStore.CorruptBackupPath) ? "" : "（原文件已备份为 " + SettingsStore.CorruptBackupPath + "）") +
                    "；原文件不会被自动覆盖，在 GM_PZ_SETTINGS 中点「保存设置」后才会重建。");
        }

        public void Terminate()
        {
            CadApplication.BeginDoubleClick -= OnBeginDoubleClick;
            CadApplication.DocumentManager.DocumentActivated -= OnDocumentActivated;
            try { CadApplication.DocumentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed; } catch { /* 退出阶段 */ }
            ObserveDatabase(null);
            WatchUndo(null);
            if (_idleAttached) { CadApplication.Idle -= OnIdle; _idleAttached = false; }
            MenuInstaller.Detach();
            ToolbarInstaller.Detach();
            ClearDoubleClickState();
            RestoreSuppressedSystemVariables();
        }

        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            try
            {
                AnnotationService.SyncNextNumber(e.Document);
                ObserveDatabase(e.Document?.Database);
                WatchUndo(e.Document);
                GMAnnotation.Views.AnnotationPanel.HandleDocumentActivated(e.Document);
                GMAnnotation.Views.AnnotationListPanel.RefreshIfOpen();
            }
            catch (System.Exception ex) { PluginLog.Error("Document.Activated", ex); }
        }

        /// <summary>只监听当前活动文档的数据库（批注被删除/撤销时刷新列表）。
        /// 解除旧监听失败（文档已关闭、数据库已释放）不影响挂上新监听。</summary>
        private static void ObserveDatabase(Database database)
        {
            if(_observedDatabase==database)return;
            var old=_observedDatabase;_observedDatabase=null;
            if(old!=null)
            {
                try{if(!old.IsDisposed)old.ObjectErased-=OnObjectErased;}
                catch(System.Exception ex){PluginLog.Warning("Database.Unobserve",ex.Message);}
            }
            if(database==null)return;
            try{database.ObjectErased+=OnObjectErased;_observedDatabase=database;}
            catch(System.Exception ex){PluginLog.Warning("Database.Observe",ex.Message);}
        }

        /// <summary>文档即将关闭：解除对它的全部监听并清掉指向它的双击状态，避免持有已释放对象。</summary>
        private static void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            try
            {
                var doc=e.Document;if(doc==null)return;
                if(_observedDatabase!=null&&_observedDatabase==doc.Database)ObserveDatabase(null);
                if(_undoWatchDocument==doc)WatchUndo(null);
                if(_commandWatchDocument==doc)UnwatchCommands();
                if(_pendingDocument==doc){_pendingDocument=null;_pendingId=ObjectId.Null;}
                if(_handoffDocument==doc)ClearHandoff();
            }
            catch(System.Exception ex){PluginLog.Warning("Document.ToBeDestroyed",ex.Message);}
        }

        /// <summary>监听活动文档的 U/UNDO/REDO/MREDO/OOPS：撤销/重做后批注可能出现或消失，空闲时刷新列表。</summary>
        private static void WatchUndo(Document doc)
        {
            if(_undoWatchDocument==doc)return;
            var old=_undoWatchDocument;_undoWatchDocument=null;
            if(old!=null)
            {
                try{old.CommandEnded-=OnUndoCommandEnded;}
                catch(System.Exception ex){PluginLog.Warning("UndoWatch.Unhook",ex.Message);}
            }
            if(doc==null)return;
            try{doc.CommandEnded+=OnUndoCommandEnded;_undoWatchDocument=doc;}
            catch(System.Exception ex){PluginLog.Warning("UndoWatch.Hook",ex.Message);}
        }

        private static void OnUndoCommandEnded(object sender, CommandEventArgs e)
        {
            try
            {
                if(!UndoCommands.Contains(e.GlobalCommandName??""))return;
                _listRefreshPending=true;
                AttachIdle();
            }
            catch(System.Exception ex){PluginLog.Warning("UndoWatch",ex.Message);}
        }

        private static void OnObjectErased(object sender,ObjectErasedEventArgs e)
        {
            try
            {
                if(!(e.DBObject is Entity entity)||entity.GetXDataForApplication(AnnotationCodec.AppName)==null)return;
                _listRefreshPending=true;
                AttachIdle();
            }
            catch(System.Exception ex){PluginLog.Error("AnnotationList.ObjectErased",ex);}
        }

        // ============ 双击编辑 ============

        /// <summary>
        /// 双击事件：按双击位置做命中测试（优先选出 GM 批注成员），命中失败再退回预选集；
        /// 确认是 GM 批注后，临时关闭 QPMODE/DBLCLKEDIT，在 Idle 中以 "^C^C GM_PZ_EDIT" 启动编辑，
        /// 目标实体经静态字段交给 GM_PZ_EDIT（不依赖预选集）。
        /// </summary>
        private static void OnBeginDoubleClick(object sender, BeginDoubleClickEventArgs e)
        {
            try
            {
                if (!SettingsStore.Load().DoubleClickEdit) { PluginLog.Info("DoubleClick", "设置中已关闭双击编辑，交给 CAD 原生处理。"); return; }
                var doc = CadApplication.DocumentManager.MdiActiveDocument;
                if (doc == null) { PluginLog.Info("DoubleClick", "没有活动文档，忽略。"); return; }
                if (_editRunning) { PluginLog.Info("DoubleClick", "GM_PZ_EDIT 正在执行，忽略本次双击。"); return; }

                var id = HitTestAnnotation(doc, e.Location, out var hitCount, out var hitTestOk, out var annotationNumber);
                var source = "双击位置命中";
                if (id.IsNull)
                {
                    if (hitTestOk && hitCount > 0)
                    {
                        PluginLog.Info("DoubleClick", "双击位置命中 " + hitCount + " 个对象，均不是 GM批注，交给 CAD 原生处理。");
                        return;
                    }
                    id = FindAnnotationInImpliedSelection(doc, out var reason, out annotationNumber);
                    if (id.IsNull)
                    {
                        PluginLog.Info("DoubleClick", (hitTestOk ? "双击位置未命中对象" : "命中测试不可用") + "，" + reason + "，交给 CAD 原生处理。");
                        return;
                    }
                    source = "预选集";
                }

                PluginLog.Info("DoubleClick", "经" + source + "识别到批注 " + annotationNumber + "，空间 CVPORT=" + ReadSystemVariable("CVPORT") +
                    "，CMDACTIVE=" + ReadSystemVariable("CMDACTIVE") + "，将启动 GM_PZ_EDIT。");
                // BeginDoubleClick 无法取消 CAD 的原生后续处理：临时关闭快捷特性与原生双击编辑，
                // 清掉当前预选，防止快捷特性面板或 MTEDIT/PEDIT 抢占前台；Idle 中再用 ^C^C 兜底取消。
                SuppressSystemVariables();
                try { doc.Editor.SetImpliedSelection(new ObjectId[0]); }
                catch (System.Exception ex) { PluginLog.Warning("DoubleClick", "清除预选失败：" + ex.Message); }
                _pendingDocument = doc; _pendingId = id;
                WatchCommands(doc);
                AttachIdle();
            }
            catch (System.Exception ex) { PluginLog.Error("DoubleClick",ex); }
        }

        /// <summary>
        /// 在双击点（WCS）周围按拾取框大小做窗交选择，返回其中第一个 GM 批注成员。
        /// 不用 SelectAtPoint：ZWCAD 的 Editor 没有该方法；SelectCrossingWindow 两家都有。
        /// </summary>
        private static ObjectId HitTestAnnotation(Document doc, Point3d wcsPoint, out int hitCount, out bool hitTestOk, out string annotationNumber)
        {
            hitCount = 0; hitTestOk = false; annotationNumber = null;
            var ed = doc.Editor;
            try
            {
                var half = PickHalfSize();
                if (half <= 0) { PluginLog.Info("DoubleClick", "无法计算拾取框大小，跳过命中测试。"); return ObjectId.Null; }
                var ucsPoint = wcsPoint.TransformBy(ed.CurrentUserCoordinateSystem.Inverse());
                var offset = new Vector3d(half, half, 0);
                var result = ed.SelectCrossingWindow(ucsPoint - offset, ucsPoint + offset);
                hitTestOk = true;
                if (result.Status != PromptStatus.OK || result.Value == null || result.Value.Count == 0) return ObjectId.Null;
                var ids = result.Value.GetObjectIds();
                hitCount = ids.Length;
                return FirstAnnotation(doc, ids, out annotationNumber, out _);
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning("DoubleClick.HitTest", "命中测试失败，改用预选集：" + ex.Message);
                return ObjectId.Null;
            }
        }

        /// <summary>拾取框半边长（当前视口的图形单位）= PICKBOX 像素 × VIEWSIZE / 视口像素高度。</summary>
        private static double PickHalfSize()
        {
            var pickBox = 3.0;
            try { pickBox = Convert.ToDouble(CadApplication.GetSystemVariable("PICKBOX")); } catch { }
            if (pickBox < 3) pickBox = 3;
            var viewSize = Convert.ToDouble(CadApplication.GetSystemVariable("VIEWSIZE"));
            double screenHeight = 0;
            var screen = CadApplication.GetSystemVariable("SCREENSIZE");
            if (screen is Point2d p2) screenHeight = p2.Y;
            else if (screen is Point3d p3) screenHeight = p3.Y;
            if (viewSize <= 0) return 0;
            if (screenHeight <= 0) return viewSize / 200;
            return pickBox * viewSize / screenHeight;
        }

        /// <summary>退回预选集：只有当预选集中恰好包含一条批注（可以是它的多个成员）时才采用，避免编辑错对象。</summary>
        private static ObjectId FindAnnotationInImpliedSelection(Document doc, out string reason, out string annotationNumber)
        {
            annotationNumber = null;
            var selected = doc.Editor.SelectImplied();
            if (selected.Status != PromptStatus.OK || selected.Value == null || selected.Value.Count == 0)
            {
                reason = "且没有预选对象（PICKFIRST=" + ReadSystemVariable("PICKFIRST") + "）";
                return ObjectId.Null;
            }
            var id = FirstAnnotation(doc, selected.Value.GetObjectIds(), out annotationNumber, out var distinct);
            if (id.IsNull) { reason = "预选集中的 " + selected.Value.Count + " 个对象均不是 GM批注"; return ObjectId.Null; }
            if (distinct > 1) { reason = "预选集中包含 " + distinct + " 条不同的批注，无法确定双击目标"; annotationNumber = null; return ObjectId.Null; }
            reason = null;
            return id;
        }

        /// <summary>返回 ids 中第一个 GM 批注成员；distinct 为其中不同批注的条数。</summary>
        private static ObjectId FirstAnnotation(Document doc, IEnumerable<ObjectId> ids, out string annotationNumber, out int distinct)
        {
            annotationNumber = null; distinct = 0;
            var first = ObjectId.Null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                foreach (var oid in ids)
                {
                    if (oid.IsNull || !oid.IsValid || oid.IsErased) continue;
                    var entity = tr.GetObject(oid, OpenMode.ForRead, false) as Entity;
                    if (entity == null || !AnnotationService.TryReadFromEntity(tr, entity, out var data)) continue;
                    if (seen.Add(data.Id ?? oid.ToString())) distinct++;
                    if (first.IsNull) { first = oid; annotationNumber = data.Number; }
                }
                tr.Commit();
            }
            return first;
        }

        private static void AttachIdle()
        {
            if (_idleAttached) return;
            CadApplication.Idle += OnIdle;
            _idleAttached = true;
        }

        private static void OnIdle(object sender, EventArgs e)
        {
            if(_listRefreshPending)
            {
                _listRefreshPending=false;
                try{GMAnnotation.Views.AnnotationListPanel.RefreshIfOpen();}
                catch(System.Exception ex){PluginLog.Error("AnnotationList.Refresh",ex);}
            }

            var doc = _pendingDocument; var id = _pendingId; _pendingDocument = null; _pendingId = ObjectId.Null;
            if (doc != null && !id.IsNull)
            {
                if (doc.IsDisposed || CadApplication.DocumentManager.MdiActiveDocument != doc)
                {
                    PluginLog.Info("DoubleClick", "Idle 时活动文档已切换，放弃本次双击编辑。");
                    RestoreSuppressedSystemVariables();
                }
                else
                {
                    try
                    {
                        _handoffDocument = doc; _handoffId = id; _handoffSentAt = DateTime.Now;
                        // ^C^C 先取消 CAD 原生双击动作（MTEDIT/PEDIT 等）可能已经发起的命令，再启动 GM_PZ_EDIT。
                        doc.SendStringToExecute("\x03\x03" + EditCommandName + " ", true, false, false);
                    }
                    catch (System.Exception ex)
                    {
                        ClearHandoff();
                        RestoreSuppressedSystemVariables();
                        PluginLog.Error("DoubleClick.Idle",ex);
                    }
                }
            }

            // 兜底：命令被吞掉（没有启动 GM_PZ_EDIT）时，宽限期过后恢复 QPMODE/DBLCLKEDIT，不永久改写用户偏好。
            if (HasSuppressedSystemVariables && !_editRunning && _pendingId.IsNull)
            {
                if (_handoffId.IsNull)
                {
                    RestoreSuppressedSystemVariables();
                }
                else if (DateTime.Now - _handoffSentAt > HandoffGrace && ReadInt("CMDACTIVE", 0) == 0)
                {
                    PluginLog.Warning("DoubleClick", "GM_PZ_EDIT 在 " + HandoffGrace.TotalSeconds + " 秒内未启动，已放弃本次双击编辑并恢复系统变量。");
                    ClearHandoff();
                    RestoreSuppressedSystemVariables();
                }
            }

            var keep = _listRefreshPending || !_pendingId.IsNull || HasSuppressedSystemVariables;
            if (!keep && _idleAttached) { CadApplication.Idle -= OnIdle; _idleAttached = false; }
        }

        /// <summary>GM_PZ_EDIT 开始时调用：标记命令运行中（兜底逻辑不会在编辑窗口打开期间提前恢复系统变量）。</summary>
        internal static void BeginEditCommand() { _editRunning = true; }

        /// <summary>GM_PZ_EDIT 结束时调用（finally）：清除标记并恢复用户原有的 QPMODE/DBLCLKEDIT。</summary>
        internal static void EndEditCommand()
        {
            _editRunning = false;
            RestoreSuppressedSystemVariables();
        }

        /// <summary>领取双击传来的编辑目标：仅当文档一致、未过期且实体仍有效时返回 true。领取后即清除。</summary>
        internal static bool TryTakeDoubleClickTarget(Document doc, out ObjectId id)
        {
            id = ObjectId.Null;
            var target = _handoffId; var targetDoc = _handoffDocument; var sentAt = _handoffSentAt;
            ClearHandoff();
            if (target.IsNull || targetDoc == null) return false;
            if (targetDoc != doc) { PluginLog.Info("DoubleClick", "双击目标属于其他文档，GM_PZ_EDIT 改为手动选择。"); return false; }
            if (DateTime.Now - sentAt > HandoffLifetime) { PluginLog.Info("DoubleClick", "双击目标已过期，GM_PZ_EDIT 改为手动选择。"); return false; }
            if (!target.IsValid || target.IsErased) { PluginLog.Info("DoubleClick", "双击目标实体已失效，GM_PZ_EDIT 改为手动选择。"); return false; }
            id = target;
            return true;
        }

        private static void ClearHandoff() { _handoffDocument = null; _handoffId = ObjectId.Null; }

        private static void ClearDoubleClickState()
        {
            _pendingDocument = null; _pendingId = ObjectId.Null;
            ClearHandoff();
            _editRunning = false;
        }

        private static void WatchCommands(Document doc)
        {
            if (_commandWatchDocument == doc) return;
            UnwatchCommands();
            try
            {
                doc.CommandEnded += OnCommandFinished;
                doc.CommandCancelled += OnCommandFinished;
                doc.CommandFailed += OnCommandFinished;
                _commandWatchDocument = doc;
            }
            catch (System.Exception ex) { PluginLog.Warning("DoubleClick.WatchCommands", ex.Message); }
        }

        private static void UnwatchCommands()
        {
            var doc = _commandWatchDocument; _commandWatchDocument = null;
            if (doc == null) return;
            try
            {
                doc.CommandEnded -= OnCommandFinished;
                doc.CommandCancelled -= OnCommandFinished;
                doc.CommandFailed -= OnCommandFinished;
            }
            catch { /* 文档可能已关闭 */ }
        }

        /// <summary>命令结束/取消/失败：GM_PZ_EDIT 结束即恢复；其他命令（如被 ^C 取消的原生 MTEDIT）在仍有待领取目标时不处理。</summary>
        private static void OnCommandFinished(object sender, CommandEventArgs e)
        {
            try
            {
                var isEdit = string.Equals(e.GlobalCommandName, EditCommandName, StringComparison.OrdinalIgnoreCase);
                if (isEdit) _editRunning = false;
                if (!isEdit && (!_handoffId.IsNull || !_pendingId.IsNull || _editRunning)) return;
                if (HasSuppressedSystemVariables) RestoreSuppressedSystemVariables();
            }
            catch (System.Exception ex) { PluginLog.Error("DoubleClick.CommandFinished", ex); }
        }

        private static bool HasSuppressedSystemVariables => _quickPropertiesSuppressed || _doubleClickEditSuppressed;

        private static void SuppressSystemVariables()
        {
            if (!_quickPropertiesSuppressed)
            {
                try
                {
                    var mode = CadApplication.GetSystemVariable("QPMODE");
                    if (Convert.ToInt32(mode) != 0)
                    {
                        _savedQuickPropertiesMode = mode;
                        CadApplication.SetSystemVariable("QPMODE", ZeroLike(mode));
                        _quickPropertiesSuppressed = true;
                    }
                }
                catch (System.Exception ex) { PluginLog.Warning("DoubleClick.SuppressQuickProperties", ex.Message); }
            }
            if (!_doubleClickEditSuppressed)
            {
                try
                {
                    var mode = CadApplication.GetSystemVariable("DBLCLKEDIT");
                    if (IsOn(mode))
                    {
                        _savedDoubleClickEditMode = mode;
                        CadApplication.SetSystemVariable("DBLCLKEDIT", mode is string ? (object)"OFF" : ZeroLike(mode));
                        _doubleClickEditSuppressed = true;
                    }
                }
                catch (System.Exception ex) { PluginLog.Warning("DoubleClick.SuppressDblClkEdit", ex.Message); }
            }
            PersistPendingRestore();
        }

        /// <summary>把"临时关闭前的原值"写进 settings.xml：CAD 在双击编辑期间崩溃/被结束时，下次加载据此恢复。</summary>
        private static void PersistPendingRestore()
        {
            try
            {
                var parts=new List<string>();
                if(_quickPropertiesSuppressed&&_savedQuickPropertiesMode!=null)parts.Add("QPMODE="+EncodeSysvar(_savedQuickPropertiesMode));
                if(_doubleClickEditSuppressed&&_savedDoubleClickEditMode!=null)parts.Add("DBLCLKEDIT="+EncodeSysvar(_savedDoubleClickEditMode));
                SettingsStore.SaveExtra(PendingRestoreKey,parts.Count>0?string.Join(";",parts):null);
            }
            catch(System.Exception ex){PluginLog.Warning("DoubleClick.PersistRestore",ex.Message);}
        }

        private static string EncodeSysvar(object value)
        {
            var kind=value is string?"S":value is short?"H":"I";
            return kind+":"+Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture);
        }

        private static object DecodeSysvar(string text)
        {
            if(string.IsNullOrEmpty(text)||text.Length<2||text[1]!=':')return null;
            var raw=text.Substring(2);
            switch(text[0])
            {
                case 'S':return raw;
                case 'H':return short.TryParse(raw,out var h)?(object)h:null;
                default:return int.TryParse(raw,out var i)?(object)i:null;
            }
        }

        /// <summary>加载时检查上次是否有未恢复的系统变量；只有当前值仍是插件临时设置的"关闭"状态时才恢复（用户之后手动改过的不动）。</summary>
        private static void RecoverPendingSystemVariables()
        {
            try
            {
                var pending=SettingsStore.LoadExtra(PendingRestoreKey);
                if(string.IsNullOrWhiteSpace(pending))return;
                foreach(var part in pending.Split(new[]{';'},StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq=part.IndexOf('=');if(eq<=0)continue;
                    var name=part.Substring(0,eq).Trim();var value=DecodeSysvar(part.Substring(eq+1).Trim());
                    if(value==null||(name!="QPMODE"&&name!="DBLCLKEDIT"))continue;
                    try
                    {
                        var current=CadApplication.GetSystemVariable(name);
                        if(IsOn(current)){PluginLog.Info("DoubleClick.Recover",name+" 当前已是开启状态（"+current+"），不再恢复。");continue;}
                        CadApplication.SetSystemVariable(name,value);
                        PluginLog.Warning("DoubleClick.Recover","上次双击编辑期间 CAD 未正常结束，已把 "+name+" 恢复为 "+value+"。");
                    }
                    catch(System.Exception ex){PluginLog.Warning("DoubleClick.Recover",name+"："+ex.Message);}
                }
                SettingsStore.SaveExtra(PendingRestoreKey,null);
            }
            catch(System.Exception ex){PluginLog.Warning("DoubleClick.Recover",ex.Message);}
        }

        /// <summary>恢复双击编辑期间临时改动的 QPMODE / DBLCLKEDIT（幂等，可重复调用），不永久改写 CAD 偏好。</summary>
        internal static void RestoreSuppressedSystemVariables()
        {
            var hadSuppressed = HasSuppressedSystemVariables;
            if (_quickPropertiesSuppressed)
            {
                var mode = _savedQuickPropertiesMode;
                _savedQuickPropertiesMode = null;
                _quickPropertiesSuppressed = false;
                try { CadApplication.SetSystemVariable("QPMODE", mode); }
                catch (System.Exception ex) { PluginLog.Error("DoubleClick.RestoreQuickProperties", ex); }
            }
            if (_doubleClickEditSuppressed)
            {
                var mode = _savedDoubleClickEditMode;
                _savedDoubleClickEditMode = null;
                _doubleClickEditSuppressed = false;
                try { CadApplication.SetSystemVariable("DBLCLKEDIT", mode); }
                catch (System.Exception ex) { PluginLog.Error("DoubleClick.RestoreDblClkEdit", ex); }
            }
            if (hadSuppressed) SettingsStore.SaveExtra(PendingRestoreKey, null);
            if (_handoffId.IsNull && _pendingId.IsNull && !_editRunning) UnwatchCommands();
        }

        private static bool IsOn(object mode)
        {
            if (mode == null) return false;
            if (mode is string s) return !(s.Equals("OFF", StringComparison.OrdinalIgnoreCase) || s == "0" || s.Length == 0);
            return Convert.ToInt32(mode) != 0;
        }

        /// <summary>与原值同类型的 0（short/int 等），避免宿主对系统变量类型校验过严。</summary>
        private static object ZeroLike(object mode)
        {
            try { return mode == null ? (object)0 : Convert.ChangeType(0, mode.GetType()); }
            catch { return 0; }
        }

        private static string ReadSystemVariable(string name)
        {
            try { return Convert.ToString(CadApplication.GetSystemVariable(name)); }
            catch { return "?"; }
        }

        private static int ReadInt(string name, int fallback)
        {
            try { return Convert.ToInt32(CadApplication.GetSystemVariable(name)); }
            catch { return fallback; }
        }
    }
}
