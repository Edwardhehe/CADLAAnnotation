using System;
using System.Collections.Generic;
using System.Linq;
using GMAnnotation.Views;
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
    /// <summary>CAD 命令集合：绘制/编辑/删除批注、设置、重载菜单。</summary>
    public sealed class Commands
    {
        /// <summary>打开浮动批注面板（面板内可选择类型/形式/设置，支持连续批注）。</summary>
        [CommandMethod("GM_PZ_NOTE", CommandFlags.Modal)]
        public void CreateAnnotation()
        {
            AnnotationPanel.ShowOrActivate();
        }

        /// <summary>面板排队使用的内部命令，让选点流程在 CAD 命令上下文中执行。</summary>
        [CommandMethod("GM_PZ_RUN", CommandFlags.Modal)]
        public void RunAnnotationFromPanel()
        {
            AnnotationPanel.RunQueuedAnnotation();
        }

        /// <summary>绘制批注（工具栏的"批"键，原称"直接绘制批注"）：<b>不弹出批注面板</b>，按当前设置直接进入 CAD 交互——
        /// 框选云线范围 → 拖放文字框位置 → 填写批注内容 → 生成。
        /// 编号自增、留痕、知识库入库与面板流程完全一致（都走 AnnotationService.Create）。</summary>
        [CommandMethod("GM_PZ_DRAW", CommandFlags.Modal)]
        public void DrawAnnotationDirectly()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            try
            {
                var settings = SettingsStore.Load();
                var data = new AnnotationData
                {
                    Author = settings.DefaultAuthor,
                    Discipline = settings.DefaultDiscipline,
                    Role = settings.DefaultRole
                };
                if (settings.AutoNumber)
                {
                    settings.NextNumber = AnnotationService.GetNextNumber(doc);
                    data.Number = "GM-" + settings.NextNumber.ToString("D3");
                }

                if (!AnnotationService.PromptGeometry(doc, settings.Clone(), out var first, out var second))
                {
                    doc.Editor.WriteMessage("\n已取消绘制批注。");
                    return;
                }

                var (_, width, height) = AnnotationService.UcsAlignedExtents(doc, first, second);
                var diagonal = Math.Sqrt(width * width + height * height);
                var effective = AnnotationService.ResolveEffectiveSettings(doc, settings, data, diagonal, true);

                // "仅绘云线"设置：不选文字框、不填内容，画完即结束。
                if (settings.CloudOnly)
                {
                    doc.Editor.WriteMessage($"\n云线对角线: {diagonal:0.#}  云线半径: {effective.CloudRadius:0.###}");
                    AnnotationService.CreateCloudOnly(doc, effective, first, second);
                    doc.Editor.WriteMessage("\n云线已创建。");
                    return;
                }

                // 始终报出本次实际采用的尺寸，方便核对比例选型算出来的图面值。
                doc.Editor.WriteMessage($"\n云线对角线: {diagonal:0.#}  字高: {effective.TextHeight:0.###}  云线半径: {effective.CloudRadius:0.###}");

                var placement = AnnotationService.PromptPlacement(
                    doc, effective, settings, new[] { first }, new[] { second }, null, second, PlacementGeometryKind.Region);
                if (placement.Status == AnnotationService.InteractionStatus.Cancelled)
                {
                    doc.Editor.WriteMessage("\n已取消批注框定位。");
                    return;
                }
                if (placement.Status != AnnotationService.InteractionStatus.Accepted)
                {
                    PluginLog.Warning("GM_PZ_DRAW", placement.Stage + ":" + placement.PromptStatus);
                    doc.Editor.WriteMessage($"\n批注框定位失败：{placement.PromptStatus}。");
                    return;
                }

                if (!CadDialog.ShowAnnotation(data, false))
                {
                    doc.Editor.WriteMessage("\n已在填写内容阶段取消批注。");
                    return;
                }

                AnnotationService.Create(doc, data, effective, first, second, placement.Point);
                if (settings.AutoNumber) { settings.NextNumber++; SettingsStore.Save(settings); }
                doc.Editor.WriteMessage("\nGM批注已创建: " + data.Number);
                AnnotationListPanel.RefreshIfOpen();
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage("\n绘制批注失败: " + ex.Message);
                PluginLog.Error("GM_PZ_DRAW", ex);
            }
        }

        /// <summary>单绘云线：仅绘制云线范围框，不生成文字和引线。</summary>
        [CommandMethod("GM_PZ_CLOUD", CommandFlags.Modal)]
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
        [CommandMethod("GM_PZ_ADDCLOUD", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void AddCloudToAnnotation()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            var implied = doc.Editor.SelectImplied();
            ObjectId id;
            if (implied.Status == PromptStatus.OK && implied.Value.Count > 0) id = implied.Value.GetObjectIds()[0];
            else
            {
                var result = doc.Editor.GetEntity("\n选择要增补云线的 GM批注: "); if (result.Status != PromptStatus.OK) return; id = result.ObjectId;
            }
            AnnotationData data;
            try
            {
                // 复制/粘贴后的批注编组不跟随实体，先按需自动修复再读取。
                AnnotationService.AutoRepairIfNeeded(doc, id);
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null || !AnnotationService.TryReadFromEntity(tr, entity, out data)) { doc.Editor.WriteMessage("\n所选对象不是有效的 GM批注，或批注数据已损坏。"); return; }
                }
            }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\n读取批注失败: " + ex.Message); return; }

            AppendCloudSession(doc, data);
        }

        /// <summary>增补云线会话：按当前设置的样式连续框选新云线范围（回车/空格结束）。
        /// 供 GM_PZ_ADDCLOUD 命令与编辑批注窗口的「增补云线」按钮共用。</summary>
        internal static void AppendCloudSession(Document doc, AnnotationData data)
        {
            // 预览就用当前全局设置的样式，这样选点阶段看到的云线大小与最终补出来的完全一致。
            var preview = AnnotationService.AppendCloudStyle(doc, data);
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
            if (added > 0)
            {
                doc.Editor.WriteMessage($"\n批注 {data.Number} 共增补 {added} 条云线。");
                AnnotationListPanel.RefreshIfOpen();
            }
        }

        /// <summary>合并批注：把全图<b>内容完全一致</b>的批注并成一条——每组保留编号最小的一条，
        /// 其余批注的云线与引线并入它（引线改接到它的文字框），重复的文字与文字框删除。
        /// 执行前弹确认框列出要合并的分组，整个操作支持 UNDO。</summary>
        [CommandMethod("GM_PZ_MERGE", CommandFlags.Modal)]
        public void MergeSameAnnotations()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            try
            {
                if (!MergePrompt.Confirm(null)) return;
                doc.Editor.WriteMessage("\n" + AnnotationService.MergeDuplicates(doc));
                AnnotationListPanel.RefreshIfOpen();
                FilterWindow.RefreshIfOpen();
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage("\n合并批注失败: " + ex.Message);
                PluginLog.Error("GM_PZ_MERGE", ex);
            }
        }

        /// <summary>过滤批注：按「批注内容完全一致」过滤显示——打开批注过滤窗口挑一组；
        /// 若执行前已选中某条批注，则直接按它的内容过滤（其余批注隐藏，数据保留在图内）。</summary>
        [CommandMethod("GM_PZ_FILTER", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void FilterAnnotations()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            try
            {
                string key = null;
                var implied = doc.Editor.SelectImplied();
                if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
                {
                    var picked = implied.Value.GetObjectIds()[0];
                    doc.Editor.SetImpliedSelection(new ObjectId[0]);
                    using (var tr = doc.Database.TransactionManager.StartTransaction())
                    {
                        var entity = tr.GetObject(picked, OpenMode.ForRead, false) as Entity;
                        if (entity != null && AnnotationService.TryReadFromEntity(tr, entity, out var data))
                            key = AnnotationService.ContentKey(data.Content);
                    }
                }
                if (!string.IsNullOrEmpty(key)) doc.Editor.WriteMessage("\n" + AnnotationService.FilterByContent(doc, key));
                FilterWindow.ShowOrActivate(key);
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage("\n过滤批注失败: " + ex.Message);
                PluginLog.Error("GM_PZ_FILTER", ex);
            }
        }

        /// <summary>格式刷：选一条源批注 → 弹出样式对话框（首行/次行/批注字高、文字样式、云线样式、图层与颜色，
        /// 已按源批注实测值预填）→ 调整后连续点选其他批注套用。只刷外观，内容/编号等业务数据不变。</summary>
        [CommandMethod("GM_PZ_FORMAT", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void FormatBrush()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            try
            {
                // 源批注：优先用预选对象，否则提示点选。
                ObjectId sourceId = ObjectId.Null;
                var implied = doc.Editor.SelectImplied();
                if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
                {
                    sourceId = implied.Value.GetObjectIds()[0];
                    doc.Editor.SetImpliedSelection(new ObjectId[0]);
                }
                if (sourceId.IsNull)
                {
                    var pick = doc.Editor.GetEntity("\n选择源批注（作为格式来源）: ");
                    if (pick.Status != PromptStatus.OK) return;
                    sourceId = pick.ObjectId;
                }

                var spec = AnnotationService.BuildFormatSpec(doc, sourceId);
                if (spec == null) { doc.Editor.WriteMessage("\n所选对象不是 GM批注。"); return; }

                var window = new Views.FormatBrushWindow(spec);
                if (CadDialog.ShowModal(window) != true) { doc.Editor.WriteMessage("\n已取消格式刷。"); return; }

                var applied = 0;
                while (true)
                {
                    var options = new PromptEntityOptions("\n选择要应用格式的批注（回车结束）: ");
                    options.AllowNone = true;
                    options.SetRejectMessage("\n该对象不是批注，请重新选择。");
                    var result = doc.Editor.GetEntity(options);
                    if (result.Status == PromptStatus.None) break;      // 回车结束
                    if (result.Status != PromptStatus.OK) return;       // Esc 取消整个命令
                    try
                    {
                        if (AnnotationService.ApplyFormatBrush(doc, result.ObjectId, spec))
                        {
                            applied++;
                            doc.Editor.WriteMessage("\n已应用格式（共 " + applied + " 条），可继续选择，回车结束。");
                        }
                        else doc.Editor.WriteMessage("\n该对象不是 GM批注，已跳过。");
                    }
                    catch (System.Exception ex)
                    {
                        PluginLog.Error("GM_PZ_FORMAT.Apply", ex);
                        doc.Editor.WriteMessage("\n刷格式失败: " + ex.Message);
                    }
                }
                doc.Editor.WriteMessage("\n格式刷完成，共更新 " + applied + " 条批注。");
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage("\n格式刷失败: " + ex.Message);
                PluginLog.Error("GM_PZ_FORMAT", ex);
            }
        }

        /// <summary>编辑批注：优先使用已选实体，否则让用户点选。</summary>
        [CommandMethod("GM_PZ_EDIT", CommandFlags.Modal | CommandFlags.UsePickSet)]
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
                    var options = new PromptEntityOptions("\n选择要编辑的 GM批注: ");
                    var result = doc.Editor.GetEntity(options); if (result.Status != PromptStatus.OK) return; id = result.ObjectId;
                }
                EditById(doc, id);
            }
            finally { PluginEntry.RestoreQuickPropertiesMode(); }
        }

        /// <summary>导出批注：预选了批注则只导出所选，否则导出全图，输出为 CSV（Excel 可直接编辑）。</summary>
        [CommandMethod("GM_PZ_EXPORT", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void ExportAnnotations()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            try
            {
                List<ObjectId> only = null;
                var implied = doc.Editor.SelectImplied();
                if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
                {
                    only = implied.Value.GetObjectIds().Where(oid => oid.IsValid && !oid.IsErased).ToList();
                    doc.Editor.SetImpliedSelection(new ObjectId[0]);
                }
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "导出批注",
                    Filter = "CSV 文件 (*.csv)|*.csv",
                    FileName = "批注信息_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv"
                };
                if (dialog.ShowDialog() != true) return;
                var report = AnnotationExchange.Export(doc, dialog.FileName, only);
                doc.Editor.WriteMessage("\n" + report);
            }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\n导出批注失败: " + ex.Message); PluginLog.Error("GM_PZ_EXPORT", ex); }
        }

        /// <summary>导入批注：按编号匹配，已有则更新、缺失则在新基点处新建，来源为 CSV。</summary>
        [CommandMethod("GM_PZ_IMPORT", CommandFlags.Modal)]
        public void ImportAnnotations()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "导入批注",
                    Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*"
                };
                if (dialog.ShowDialog() != true) return;
                doc.Editor.WriteMessage("\n正在导入批注...");
                var report = AnnotationExchange.Import(doc, dialog.FileName);
                doc.Editor.WriteMessage("\n" + report);
                AnnotationListPanel.RefreshIfOpen();
            }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\n导入批注失败: " + ex.Message); PluginLog.Error("GM_PZ_IMPORT", ex); }
        }

        /// <summary>打开批注历史记录窗口：留痕查询（按图名/编号/内容等过滤）与 CSV 导出。</summary>
        [CommandMethod("GM_PZ_HISTORY", CommandFlags.Modal)]
        public void ShowHistory()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            try
            {
                var drawing = "";
                try { drawing = System.IO.Path.GetFileName(doc.Name ?? ""); } catch { }
                var window = new Views.HistoryWindow(drawing);
                CadDialog.ShowModal(window);
            }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\n打开批注历史失败: " + ex.Message); PluginLog.Error("GM_PZ_HISTORY", ex); }
        }

        /// <summary>打开知识库窗口：批注条目 + 常用批注语。
        /// 注意：知识库是独立体系，与「批注历史记录」（GM_PZ_HISTORY，留痕）不是同一个窗口、也不是同一份数据。</summary>
        [CommandMethod("GM_PZ_KB", CommandFlags.Modal)]
        public void ShowKnowledgeBase()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            try
            {
                var window = new Views.KnowledgeWindow();
                CadDialog.ShowModal(window);
            }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\n打开知识库失败: " + ex.Message); PluginLog.Error("GM_PZ_KB", ex); }
        }

        /// <summary>修复复制/粘贴后的批注：全图扫描脱离编组的批注实体，重建编组并恢复识别。</summary>
        [CommandMethod("GM_PZ_REPAIR", CommandFlags.Modal)]
        public void RepairAnnotations()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            try
            {
                doc.Editor.WriteMessage("\n正在扫描并修复复制/粘贴产生的批注...");
                var report = AnnotationService.RepairOrphans(doc);
                doc.Editor.WriteMessage("\n" + report);
                AnnotationListPanel.RefreshIfOpen();
            }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\n修复失败: " + ex.Message); PluginLog.Error("GM_PZ_REPAIR", ex); }
        }

        /// <summary>删除批注：删除整个实体组（云线+引线+文字+边框），支持 UNDO。</summary>
        [CommandMethod("GM_PZ_DELETE", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void DeleteAnnotation()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            var implied = doc.Editor.SelectImplied(); ObjectId target;
            if (implied.Status == PromptStatus.OK && implied.Value.Count > 0) target = implied.Value.GetObjectIds()[0];
            else { var result = doc.Editor.GetEntity("\n选择要删除的 GM批注: "); if (result.Status != PromptStatus.OK) return; target = result.ObjectId; }
            AnnotationService.AutoRepairIfNeeded(doc, target);
            try
            {
                if (!AnnotationService.Delete(doc, target)) { doc.Editor.WriteMessage("\n所选对象不是有效的 GM批注。"); return; }
                doc.Editor.WriteMessage("\nGM批注已删除，可使用 UNDO 恢复。");
                AnnotationListPanel.RefreshIfOpen();
            }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\n删除失败: " + ex.Message); }
        }

        /// <summary>隐藏批注：把批注整体设为不可见（数据保留在图内）。预选了批注则只隐藏所选，否则隐藏全图批注。</summary>
        [CommandMethod("GM_PZ_HIDE", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void HideAnnotations()
        {
            ToggleAnnotationVisibility(false);
        }

        /// <summary>显示批注：恢复批注的可见性。预选了批注则只显示所选，否则显示全图批注。</summary>
        [CommandMethod("GM_PZ_SHOW", CommandFlags.Modal | CommandFlags.UsePickSet)]
        public void ShowAnnotations()
        {
            ToggleAnnotationVisibility(true);
        }

        /// <summary>隐藏/显示批注的公共流程：取预选（若有）后交给 AnnotationService 切换可见性。</summary>
        private void ToggleAnnotationVisibility(bool visible)
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            try
            {
                List<ObjectId> only = null;
                var implied = doc.Editor.SelectImplied();
                if (implied.Status == PromptStatus.OK && implied.Value.Count > 0)
                {
                    only = implied.Value.GetObjectIds().Where(oid => oid.IsValid && !oid.IsErased).ToList();
                    doc.Editor.SetImpliedSelection(new ObjectId[0]);
                }
                doc.Editor.WriteMessage("\n" + AnnotationService.SetVisibility(doc, visible, only));
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage(visible ? "\n显示批注失败: " + ex.Message : "\n隐藏批注失败: " + ex.Message);
                PluginLog.Error(visible ? "GM_PZ_SHOW" : "GM_PZ_HIDE", ex);
            }
        }

        /// <summary>刷新批注文字：按当前"批注框内显示内容"等设置重写全图批注版式（含文字框与引线端点），供旧图套用新版式。</summary>
        [CommandMethod("GM_PZ_REFRESH", CommandFlags.Modal)]
        public void RefreshAnnotationText()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            try
            {
                var count = AnnotationService.RefreshText(doc);
                doc.Editor.Regen();
                doc.Editor.WriteMessage(count > 0
                    ? "\n已按当前设置刷新 " + count + " 条批注的文字版式。"
                    : "\n当前图纸中没有可刷新的 GM批注。");
                AnnotationListPanel.RefreshIfOpen();
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage("\n刷新批注文字失败: " + ex.Message);
                PluginLog.Error("GM_PZ_REFRESH", ex);
            }
        }

        /// <summary>移动批注文字框、文字和引线末端，云线保持原位。</summary>
        [CommandMethod("GM_PZ_MOVE", CommandFlags.Modal | CommandFlags.UsePickSet)]
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
                    "\n选择要移动文字框的 GM批注: ");
                if (result.Status != PromptStatus.OK)
                {
                    return;
                }

                target = result.ObjectId;
            }

            AnnotationService.AutoRepairIfNeeded(doc, target);
            try
            {
                if (!AnnotationService.MoveAnnotation(doc, target))
                {
                    doc.Editor.WriteMessage(
                        "\n所选对象不是有效的完整 GM批注。");
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
        [CommandMethod("GM_PZ_SETTINGS", CommandFlags.Modal)]
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
        [CommandMethod("GM_PZ_LIST", CommandFlags.Modal)]
        public void AnnotationList()
        {
            AnnotationListPanel.ShowOrActivate();
        }

        /// <summary>批注汇总：框选批注后，在点击位置绘制日期+内容汇总表，并从各批注框引线指向表位。</summary>
        [CommandMethod("GM_PZ_SUMMARY", CommandFlags.Modal)]
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

        /// <summary>批注清单：把本图全部批注整理成一张清单表画到指定位置（不框选、不画引线），列与批注列表一致。</summary>
        [CommandMethod("GM_PZ_LEGEND", CommandFlags.Modal)]
        public void AnnotationLegend()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument; if (doc == null) return;
            try
            {
                AnnotationService.DrawLegend(doc);
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage("\n生成批注清单失败: " + ex.Message);
                PluginLog.Error("Legend", ex);
            }
        }

        /// <summary>导出批注到 Word：每条批注输出时间、云线范围截图和批注文字。</summary>
        [CommandMethod("GM_PZ_WORD", CommandFlags.Modal)]
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

        /// <summary>显示/隐藏「GM批注」CAD 原生工具栏（首次调用会创建它）。
        /// 浮动快捷栏已取消，工具栏是单字按钮的唯一入口。</summary>
        [CommandMethod("GM_PZ_TOOLBAR", CommandFlags.Modal)]
        public void ToggleToolbar()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            ToolbarInstaller.ToggleVisible(out var message);
            doc?.Editor.WriteMessage("\n" + message);
        }

        /// <summary>切换当前 CAD 宿主的启动自动加载。</summary>
        [CommandMethod("GM_PZ_AUTOLOAD", CommandFlags.Modal)]
        public void ToggleAutoload()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            try
            {
                if (AutoloadManager.IsInstalled(out var registeredPath))
                {
                    var removed = AutoloadManager.Uninstall();
                    doc?.Editor.WriteMessage(
                        $"\n已关闭 GM批注自动加载，清理 {removed} 个注册表项。" +
                        $" 原加载路径: {registeredPath}");
                    return;
                }

                var roots = AutoloadManager.Install();
                doc?.Editor.WriteMessage(
                    $"\n已开启 GM批注自动加载，共设置 {roots.Count} 个注册表位置。" +
                    $" DLL: {AutoloadManager.CurrentDllPath}");
            }
            catch (System.Exception ex)
            {
                doc?.Editor.WriteMessage("\n设置自动加载失败: " + ex.Message);
                PluginLog.Error("Autoload.Toggle", ex);
            }
        }

        [CommandMethod("GM_PZ_MENU", CommandFlags.Modal)]
        public void ReloadMenu()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            MenuInstaller.Ensure(out var message);
            doc?.Editor.WriteMessage("\n" + message);
        }

        /// <summary>打开「关于 GM批注」对话框。</summary>
        [CommandMethod("GM_PZ_ABOUT", CommandFlags.Modal)]
        public void About()
        {
            CadDialog.ShowModal(new AboutWindow());
        }

        internal static bool EditById(Document doc, ObjectId id)
        {
            AnnotationData data;
            try
            {
                // 复制/粘贴后的批注编组不跟随实体，先按需自动修复再读取。
                AnnotationService.AutoRepairIfNeeded(doc, id);
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    if (entity == null || !AnnotationService.TryReadFromEntity(tr, entity, out data)) { doc.Editor.WriteMessage("\n所选对象不是有效的 GM批注，或批注数据已损坏。"); return false; }
                }
                if (!CadDialog.ShowAnnotation(data, true)) return false;
                if (!AnnotationService.Update(doc, id, data)) { doc.Editor.WriteMessage("\n批注更新失败：对象关联已改变。"); return false; }
                AnnotationListPanel.RefreshIfOpen();
                doc.Editor.WriteMessage("\nGM批注已更新: " + data.Number); return true;
            }
            catch (System.Exception ex) { doc.Editor.WriteMessage("\n编辑失败: " + ex.Message); return false; }
        }
    }
}
