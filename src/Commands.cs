using System;
using System.Collections.Generic;
using LAAnnotation.Views;
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

namespace LAAnnotation
{
    /// <summary>CAD 命令集合：绘制/编辑/删除批注、设置、重载菜单。</summary>
    public sealed class Commands
    {
        /// <summary>打开浮动批注面板（面板内可选择类型/形式/设置，支持连续批注）。</summary>
        [CommandMethod("LA_PZ_NOTE", CommandFlags.Modal)]
        public void CreateAnnotation()
        {
            AnnotationPanel.ShowOrActivate();
        }

        /// <summary>面板排队使用的内部命令，让选点流程在 CAD 命令上下文中执行。</summary>
        [CommandMethod("LA_PZ_RUN", CommandFlags.Modal)]
        public void RunAnnotationFromPanel()
        {
            AnnotationPanel.RunQueuedAnnotation();
        }

        /// <summary>单绘云线：仅绘制云线范围框，不生成文字和引线。</summary>
        [CommandMethod("LA_PZ_CLOUD", CommandFlags.Modal)]
        public void CloudOnly()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            var settings = SettingsStore.Load();
            var preview = settings.Clone();
            if (!AnnotationService.PromptCloudOnly(doc, preview, out var first, out var second)) return;
            try
            {
                var (_,width,height)=AnnotationService.UcsAlignedExtents(doc,first,second);
                var effective = AnnotationService.ResolveEffectiveSettings(doc, settings, new AnnotationData(), Math.Sqrt(width * width + height * height));
                if (effective.FontAutoFit) doc.Editor.WriteMessage($"\n云线半径: {effective.CloudRadius:0.###}");
                var id = AnnotationService.CreateCloudOnly(doc, effective, first, second);
                doc.Editor.WriteMessage("\n云线已创建: " + id);
            }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\n云线创建失败: " + ex.Message); }
        }

        /// <summary>增补云线：点选既有批注后连续框选新云线范围，每个范围自动生成连到原文字框的引出线，回车/空格结束。</summary>
        [CommandMethod("LA_PZ_ADDCLOUD", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void AddCloudToAnnotation()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            var implied = doc.Editor.SelectImplied();
            ObjectId id;
            if (implied.Status == PromptStatus.OK && implied.Value.Count > 0) id = implied.Value.GetObjectIds()[0];
            else
            {
                var result = doc.Editor.GetEntity("\n选择要增补云线的 LA批注: "); if (result.Status != PromptStatus.OK) return; id = result.ObjectId;
            }
            AnnotationData data;
            try
            {
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null || !AnnotationService.TryReadFromEntity(tr, entity, out data)) { doc.Editor.WriteMessage("\n所选对象不是有效的 LA批注，或批注数据已损坏。"); return; }
                }
            }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\n读取批注失败: " + ex.Message); return; }

            var preview = SettingsStore.Load();
            var historyFirsts = new List<Point3d>();
            var historySeconds = new List<Point3d>();
            var added = 0;
            while (true)
            {
                var prompt = AnnotationService.PromptCloudOrFinish(doc, preview, historyFirsts, historySeconds, out var first, out var second);
                if (prompt == CloudPromptResult.Finished) break;
                if (prompt == CloudPromptResult.Cancelled)
                {
                    if (added == 0) doc.Editor.WriteMessage("\n已取消增补云线。");
                    break;
                }
                try
                {
                    if (!AnnotationService.AppendCloud(doc, data, first, second)) { doc.Editor.WriteMessage("\n增补失败：批注编组已不存在。"); break; }
                    historyFirsts.Add(first); historySeconds.Add(second); added++;
                    doc.Editor.WriteMessage($"\n已增补 {added} 条云线，可继续框选，回车/空格结束。");
                }
                catch (System.Exception ex) { doc.Editor.WriteMessage("\n增补云线失败: " + ex.Message); }
            }
            if (added > 0) doc.Editor.WriteMessage($"\n批注 {data.Number} 共增补 {added} 条云线。");
        }

        /// <summary>编辑批注：优先使用已选实体，否则让用户点选。</summary>
        [CommandMethod("LA_PZ_EDIT", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void EditAnnotation()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) { PluginEntry.RestoreQuickPropertiesMode(); return; }
            try
            {
                var implied = doc.Editor.SelectImplied();
                ObjectId id;
                if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
                {
                    id = implied.Value.GetObjectIds()[0];
                    // 双击路径会在 Idle 中重建预选；取得 ID 后立即清除，避免恢复 QPMODE 时再弹快捷特性。
                    doc.Editor.SetImpliedSelection(new ObjectId[0]);
                }
                else
                {
                    var options = new PromptEntityOptions("\n选择要编辑的 LA批注: ");
                    var result = doc.Editor.GetEntity(options); if (result.Status != PromptStatus.OK) return; id = result.ObjectId;
                }
                EditById(doc, id);
            }
            finally { PluginEntry.RestoreQuickPropertiesMode(); }
        }

        /// <summary>删除批注：删除整个实体组（云线+引线+文字+边框），支持 UNDO。</summary>
        [CommandMethod("LA_PZ_DELETE", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void DeleteAnnotation()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            var implied = doc.Editor.SelectImplied(); ObjectId target;
            if (implied.Status == PromptStatus.OK && implied.Value.Count > 0) target = implied.Value.GetObjectIds()[0];
            else { var result = doc.Editor.GetEntity("\n选择要删除的 LA批注: "); if (result.Status != PromptStatus.OK) return; target = result.ObjectId; }
            try
            {
                if (!AnnotationService.Delete(doc, target)) { doc.Editor.WriteMessage("\n所选对象不是有效的 LA批注。"); return; }
                doc.Editor.WriteMessage("\nLA批注已删除，可使用 UNDO 恢复。");
                AnnotationListPanel.RefreshIfOpen();
            }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\n删除失败: " + ex.Message); }
        }

        /// <summary>移动批注文字框、文字和引线末端，云线保持原位。</summary>
        [CommandMethod("LA_PZ_MOVE", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void MoveAnnotation()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            var implied = doc.Editor.SelectImplied();
            ObjectId target;
            if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
            {
                target = implied.Value.GetObjectIds()[0];
            }
            else
            {
                var result = doc.Editor.GetEntity(
                    "\n选择要移动文字框的 LA批注: ");
                if (result.Status != PromptStatus.OK)
                {
                    return;
                }

                target = result.ObjectId;
            }

            try
            {
                if (!AnnotationService.MoveAnnotation(doc, target))
                {
                    doc.Editor.WriteMessage(
                        "\n所选对象不是有效的完整 LA批注。");
                    return;
                }

                doc.Editor.WriteMessage(
                    "\n批注文字框、文字和引线已移动，云线位置未改变。");
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage("\n移动批注失败: " + ex.Message);
            }
        }

        /// <summary>打开设置窗口，同时从当前 DWG 获取可用文字样式列表供下拉选择。</summary>
        [CommandMethod("LA_PZ_SETTINGS", CommandFlags.Modal)]
        public void Settings()
        {
            var styles = new List<string>();
            try
            {
                var doc = CadApplication.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    using (var tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        var table = (TextStyleTable)tr.GetObject(doc.Database.TextStyleTableId, OpenMode.ForRead);
                        foreach (ObjectId id in table) { if (id.IsValid && !id.IsErased && tr.GetObject(id, OpenMode.ForRead) is TextStyleTableRecord r && !string.IsNullOrWhiteSpace(r.Name)) styles.Add(r.Name); }
                    }
                }
            }
            catch { /* 获取样式失败时使用默认列表 */ }
            CadDialog.ShowModal(new SettingsWindow(SettingsStore.Load(), styles));
        }

        /// <summary>打开批注列表面板（左侧停靠）。</summary>
        [CommandMethod("LA_PZ_LIST", CommandFlags.Modal)]
        public void AnnotationList()
        {
            AnnotationListPanel.ShowOrActivate();
        }

        /// <summary>批注汇总：框选批注后，在点击位置绘制日期+内容汇总表，并从各批注框引线指向表位。</summary>
        [CommandMethod("LA_PZ_SUMMARY", CommandFlags.Modal)]
        public void AnnotationSummary()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            try
            {
                AnnotationService.SummarizeAnnotations(doc);
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage("\n批注汇总失败: " + ex.Message);
                PluginLog.Error("Summary", ex);
            }
        }

        /// <summary>导出批注到 Word：每条批注输出时间、云线范围截图和批注文字。</summary>
        [CommandMethod("LA_PZ_WORD", CommandFlags.Modal)]
        public void ExportWord()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            try
            {
                AnnotationService.ExportAnnotationsToWord(doc);
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage("\n导出 Word 失败: " + ex.Message);
                PluginLog.Error("WordExport", ex);
            }
        }

        /// <summary>切换当前 CAD 宿主的启动自动加载。</summary>
        [CommandMethod("LA_PZ_AUTOLOAD", CommandFlags.Modal)]
        public void ToggleAutoload()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            try
            {
                if (AutoloadManager.IsInstalled(out var registeredPath))
                {
                    var removed = AutoloadManager.Uninstall();
                    doc?.Editor.WriteMessage(
                        $"\n已关闭 LA批注自动加载，清理 {removed} 个注册表项。" +
                        $" 原加载路径: {registeredPath}");
                    return;
                }

                var roots = AutoloadManager.Install();
                doc?.Editor.WriteMessage(
                    $"\n已开启 LA批注自动加载，共设置 {roots.Count} 个注册表位置。" +
                    $" DLL: {AutoloadManager.CurrentDllPath}");
            }
            catch (System.Exception ex)
            {
                doc?.Editor.WriteMessage("\n设置自动加载失败: " + ex.Message);
                PluginLog.Error("Autoload.Toggle", ex);
            }
        }

        [CommandMethod("LA_PZ_MENU", CommandFlags.Modal)]
        public void ReloadMenu()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            MenuInstaller.Ensure(out var message);
            doc?.Editor.WriteMessage("\n" + message);
        }

        internal static bool EditById(Document doc, ObjectId id)
        {
            AnnotationData data;
            try
            {
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null || !AnnotationService.TryReadFromEntity(tr, entity, out data)) { doc.Editor.WriteMessage("\n所选对象不是有效的 LA批注，或批注数据已损坏。"); return false; }
                }
                var form = new AnnotationWindow(data, true);if (CadDialog.ShowModal(form) != true) return false;
                if (!AnnotationService.Update(doc, id, data)) { doc.Editor.WriteMessage("\n批注更新失败：对象关联已改变。"); return false; }
                AnnotationListPanel.RefreshIfOpen();
                doc.Editor.WriteMessage("\nLA批注已更新: " + data.Number); return true;
            }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\n编辑失败: " + ex.Message); return false; }
        }
    }
}
