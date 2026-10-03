using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#endif

namespace GMAnnotation
{
    /// <summary>
    /// 批注导出/导入：把图中批注导出为 CSV（UTF-8 BOM，Excel 可直接打开编辑），
    /// 再导回同一张或另一张图纸。导入时按"编号"匹配：
    /// 已有同编号批注 → 更新其信息（编号/图号/日期/专业/批注人/角色/状态/内容），保留其图上位置；
    /// 没有的 → 新建。新建位置有两种：CSV 里带<b>原图坐标</b>（布局/云线两角点/文字锚点）的条目默认
    /// 按原坐标还原到原布局（导出再导到别的图纸，批注落在与原图完全相同的位置）；
    /// 没有坐标的条目才需要用户在图上指定基点、按网格自动排布。
    /// </summary>
    internal static class AnnotationExchange
    {
        /// <summary>CSV 列顺序（导入时按表头名称识别，列顺序可变、可缺列）。
        /// "图面*" 五列是创建时算好的图面实测尺寸：导入到其他 DWG 时照抄这些值，
        /// 保证样式与原图一致（否则会按本机当前设置重算，比例/字高全变）。旧 CSV 没有这几列也能导入。
        /// 末尾的"布局/云线X1…/文字X/Y/标高"是<b>原图坐标</b>（WCS）：导入到其他 DWG 时按这些坐标把批注
        /// 还原到原位置（连布局一起还原），而不是排到用户指定的基点网格上；没有这些列则退回旧行为。</summary>
        private static readonly string[] Columns =
        {
            "编号", "图号", "图名", "日期", "专业", "批注人", "角色", "状态", "批注内容",
            "图面字高", "图面首行字高", "图面次行字高", "图面云线半径", "图面线宽",
            "布局", "云线X1", "云线Y1", "云线X2", "云线Y2", "文字X", "文字Y", "标高"
        };

        // ==================== 导出 ====================

        /// <summary>导出批注信息到 CSV。only 为空导出全图，否则只导出指定实体对应的批注。</summary>
        public static string Export(Document doc, string filePath, IList<ObjectId> only = null)
        {
            var items = AnnotationService.ReadAnnotations(doc, only);
            if (items.Count == 0) return "当前图中没有可导出的 GM批注。";
            var drawing = SafeDrawingName(doc);
            Dictionary<string, AnnotationService.AnnotationPlacement> placements;
            try { placements = AnnotationService.ReadPlacements(doc); }
            catch (Exception ex) { PluginLog.Warning("Exchange.ReadPlacements", ex.Message); placements = new Dictionary<string, AnnotationService.AnnotationPlacement>(); }
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", Columns));
            foreach (var item in items.OrderBy(i => i.Data.Number, StringComparer.OrdinalIgnoreCase))
            {
                var d = item.Data;
                placements.TryGetValue(d.Id ?? "", out var place);
                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(d.Number), Csv(d.DrawingNo), Csv(drawing), Csv(d.Date), Csv(d.Discipline),
                    Csv(d.Author), Csv(d.Role), Csv(d.Status), Csv(d.Content),
                    Num(d.RenderTextHeight), Num(d.RenderHeaderHeight), Num(d.RenderSecondLineHeight),
                    Num(d.RenderCloudRadius), Num(d.RenderLineWidth),
                    Csv(place?.Layout), Coord(place != null && place.HasCloud ? place.MinX : double.NaN),
                    Coord(place != null && place.HasCloud ? place.MinY : double.NaN),
                    Coord(place != null && place.HasCloud ? place.MaxX : double.NaN),
                    Coord(place != null && place.HasCloud ? place.MaxY : double.NaN),
                    Coord(place != null && place.HasText ? place.TextX : double.NaN),
                    Coord(place != null && place.HasText ? place.TextY : double.NaN),
                    Coord(place?.Z ?? double.NaN)
                }));
            }
            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
            var withCoord = items.Count(i => placements.TryGetValue(i.Data.Id ?? "", out var p) && p.HasCloud);
            return "已导出 " + items.Count + " 条批注到:\n" + filePath +
                   "\n其中 " + withCoord + " 条带原图坐标（布局/云线范围/文字锚点），可导入到其他图纸按原位置还原。";
        }

        // ==================== 导入 ====================

        /// <summary>从 CSV 导入批注信息：编号匹配的更新（只更新与图上现值确有差异的），未匹配的在指定基点新建。</summary>
        public static string Import(Document doc, string filePath)
        {
            var ed = doc.Editor;
            List<Dictionary<string, string>> rows;
            string encodingName, headerWarning;
            try { rows = ParseFile(filePath, out encodingName, out headerWarning); }
            catch (InvalidDataException ex) { return "无法导入：" + ex.Message; }
            catch (Exception ex) { return "读取文件失败: " + ex.Message; }
            var skipped = 0;
            if (rows.Count == 0) return "文件中没有可导入的批注数据（请确认表头包含「编号」和「批注内容」列）。";

            // 先全图修复复制/粘贴产生的孤儿批注：副本不在编组里的话，既读不全列表，也无法就地更新。
            try { AnnotationService.RepairOrphans(doc); }
            catch (Exception ex) { PluginLog.Warning("Exchange.RepairBeforeImport", ex.Message); }

            var existing = AnnotationService.ReadAnnotations(doc);
            // 同一编号可能对应多条（图内复制出来的副本），全部纳入更新目标，保证再次导入后内容一致。
            var byNumber = new Dictionary<string, List<AnnotationService.AnnotationRef>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in existing)
            {
                var key = (item.Data.Number ?? "").Trim();
                if (key.Length == 0) continue;
                if (!byNumber.TryGetValue(key, out var list)) { list = new List<AnnotationService.AnnotationRef>(); byNumber[key] = list; }
                list.Add(item);
            }

            var settings = SettingsStore.Load();
            var toCreate = new List<AnnotationData>();
            var updated = 0; var unchanged = 0; var failed = 0;
            var details = new List<string>();
            // 自动补号：从图中下一个可用编号起递增，并用已占用集合兜底，避免与图内已有编号重号。
            var usedNumbers = new HashSet<string>(byNumber.Keys, StringComparer.OrdinalIgnoreCase);
            var autoSeq = Math.Max(0, AnnotationService.GetNextNumber(doc) - 1);

            foreach (var row in rows)
            {
                var number = Get(row, "编号");
                var content = Get(row, "批注内容");
                if (string.IsNullOrWhiteSpace(number) && string.IsNullOrWhiteSpace(content)) { skipped++; continue; }

                if (number.Length > 0 && byNumber.TryGetValue(number, out var targets))
                {
                    foreach (var target in targets)
                    {
                        // 复制产生的批注编组不随实体迁移，先按需修复；修复会给批注换发新 Id，故必须重新读取一次。
                        AnnotationService.AutoRepairIfNeeded(doc, target.Id);
                        var current = target.Data;
                        var refreshed = AnnotationService.ReadAnnotations(doc, new[] { target.Id });
                        if (refreshed.Count > 0) current = refreshed[0].Data;
                        var before = Snapshot(current); // 先留底再改，用于判断"和原来的不同"
                        ApplyRow(current, row);         // 就地更新：保留原 Id 与渲染参数，只覆盖信息字段
                        var changes = AnnotationHistoryStore.Diff(before, current);
                        if (changes.Length == 0) { unchanged++; continue; } // 与图上现值一致，无需更新
                        if (AnnotationService.Update(doc, target.Id, current))
                        {
                            updated++;
                            details.Add((current.Number ?? "").Trim() + "：" + (changes.Length > 120 ? changes.Substring(0, 120) + "…" : changes));
                        }
                        else failed++;
                    }
                    continue;
                }

                var data = new AnnotationData();
                ApplyRow(data, row);
                if (string.IsNullOrWhiteSpace(data.Number))
                {
                    string candidate;
                    do { candidate = "GM-" + (++autoSeq).ToString("000"); } while (!usedNumbers.Add(candidate));
                    data.Number = candidate;
                }
                else usedNumbers.Add(data.Number);
                toCreate.Add(data);
            }

            var created = 0; var createdAtCoord = 0;
            var missingLayouts = new List<string>();
            if (toCreate.Count > 0)
            {
                // 带原图坐标的行（新版导出）默认按原坐标还原；没有坐标的行（旧 CSV / 手工表）仍走"指定基点 + 网格排布"。
                var placed = toCreate.Where(d => d.Placed).ToList();
                var grid = toCreate.Where(d => !d.Placed).ToList();
                var useCoordinates = placed.Count > 0;
                if (placed.Count > 0 && grid.Count > 0)
                {
                    var options = new PromptKeywordOptions("\n新建批注位置 [按原图坐标(P)/指定基点(B)] <P>: ");
                    options.Keywords.Add("P"); options.Keywords.Add("B"); options.Keywords.Default = "P"; options.AllowNone = true;
                    var answer = ed.GetKeywords(options);
                    if (answer.Status != PromptStatus.OK && answer.Status != PromptStatus.None)
                    {
                        skipped += toCreate.Count; placed.Clear(); grid.Clear(); useCoordinates = false;   // Esc：全部不新建
                    }
                    else if (answer.Status == PromptStatus.OK && !string.Equals(answer.StringResult, "P", StringComparison.OrdinalIgnoreCase))
                    {
                        grid.AddRange(placed); placed.Clear(); useCoordinates = false;                     // 选了 B：全部走网格排布
                    }
                }

                if (useCoordinates && placed.Count > 0)
                    createdAtCoord = CreateAtStoredPositions(doc, settings, placed, missingLayouts);
                if (grid.Count > 0)
                {
                    var basePoint = ed.GetPoint("\n指定新建批注的放置基点（回车跳过新建）: ");
                    if (basePoint.Status == PromptStatus.OK)
                        created = CreateGrid(doc, settings, grid, basePoint.Value.TransformBy(AnnotationService.GetUcsMatrix(doc)));
                    else skipped += grid.Count;
                }
            }

#if !ZWCAD
            // 图面立即刷新，便于当场核对导入结果；ZWCAD 分支不调用，避免对宿主 API 产生额外假设。
            if (updated > 0 || created > 0 || createdAtCoord > 0) { try { ed.Regen(); } catch { } }
#endif

            var report = "导入完成：更新 " + updated + " 条，未变化 " + unchanged + " 条，新建 " + (created + createdAtCoord) + " 条。"
                + "（文件编码：" + encodingName + "）";
            if (!string.IsNullOrEmpty(headerWarning)) report += "\n" + headerWarning;
            if (createdAtCoord > 0) report += "\n其中 " + createdAtCoord + " 条按 CSV 记录的原图坐标（含布局）还原到原位置。";
            if (created > 0) report += "\n其中 " + created + " 条因 CSV 无坐标，按指定基点排布。";
            if (missingLayouts.Count > 0)
                report += "\n提示：CSV 记录的布局「" + string.Join("」「", missingLayouts) + "」在本图中不存在，这些批注已放在当前空间。";
            if (details.Count > 0)
            {
                report += "\n更新明细：";
                foreach (var line in details.Take(20)) report += "\n  " + line;
                if (details.Count > 20) report += "\n  …（共 " + details.Count + " 条）";
            }
            if (failed > 0) report += "\n更新失败 " + failed + " 条（批注关联异常，可先运行「修复批注」）。";
            if (skipped > 0) report += "\n跳过 " + skipped + " 条（编号与内容均为空，或用户取消了新建）。";
            return report;
        }

        /// <summary>按 CSV 里记录的原图坐标还原批注：云线两角点与文字锚点直接用 WCS 坐标，逐条落到它原来所在的布局。
        /// 样式仍走每条自己的"图面实测尺寸"（<see cref="AbsoluteSettings"/>），所以导到别的图纸后位置与外观都与原图一致。
        /// 布局在本图中不存在时退回当前空间（坐标可能对不上，报告里会提示）。</summary>
        private static int CreateAtStoredPositions(Document doc, AnnotationSettings settings, List<AnnotationData> items, List<string> missingLayouts)
        {
            var created = 0;
            // 按布局分组：同一布局只查一次空间 Id。
            var order = new List<string>();
            var byLayout = new Dictionary<string, List<AnnotationData>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                var layout = (item.PlacedLayout ?? "").Trim();
                if (!byLayout.TryGetValue(layout, out var list)) { list = new List<AnnotationData>(); byLayout[layout] = list; order.Add(layout); }
                list.Add(item);
            }
            foreach (var layout in order)
            {
                var spaceId = AnnotationService.FindLayoutSpace(doc.Database, layout);
                if (spaceId.IsNull && layout.Length > 0 && !missingLayouts.Contains(layout)) missingLayouts.Add(layout);
                foreach (var data in byLayout[layout])
                {
                    var si = AbsoluteSettings(settings, data);
                    var z = data.PlacedZ;
                    var first = new Point3d(data.PlacedX1, data.PlacedY1, z);
                    var second = new Point3d(data.PlacedX2, data.PlacedY2, z);
                    var textX = data.PlacedText ? data.PlacedTextX : data.PlacedX1;
                    var textY = data.PlacedText ? data.PlacedTextY : FallbackTextY(doc, data, si, data.PlacedY1);
                    try
                    {
                        if (AnnotationService.Create(doc, data, si, first, second, new Point3d(textX, textY, z), spaceId)) created++;
                        else PluginLog.Warning("Exchange.CreateAtStoredPosition", "创建失败：" + (data.Number ?? ""));
                    }
                    catch (Exception ex) { PluginLog.Warning("Exchange.CreateAtStoredPosition", ex.Message); }
                }
            }
            return created;
        }

        /// <summary>CSV 只给了云线范围、没给文字锚点时的兜底：按创建时的规则推算文字框左下角
        /// （云线底边下方一个字高净空，再往下让出文字框自身高度——文字框以左下角为锚点向上生长）。</summary>
        private static double FallbackTextY(Document doc, AnnotationData data, AnnotationSettings si, double cloudBottom)
        {
            var gap = Math.Max(si.TextHeight, 0.1);
            try
            {
                var requestedWidth = si.FixedWidth ? si.FixedWidthValue : Math.Max(55.0, si.TextHeight * 18.0);
                AnnotationService.MeasureTextBox(doc, data, si, requestedWidth, out _, out var height);
                return cloudBottom - gap - height;
            }
            catch { return cloudBottom - gap - si.TextHeight * 3.0; }
        }

        /// <summary>在基点处按网格排列新建批注（云线 + 文字框 + 引线），返回成功条数。
        /// 每条批注用各自的"图面实测尺寸"（CSV 的 图面* 列）：这样导入到其他 DWG 后样式与原图一致，
        /// 不会按本机当前设置重算；旧 CSV 没有实测值的条目退回当前全局设置（历史行为）。</summary>
        private static int CreateGrid(Document doc, AnnotationSettings settings, List<AnnotationData> items, Point3d baseWcs)
        {
            var perItem = new AnnotationSettings[items.Count];
            var textWidth = new double[items.Count];
            var cloudW = new double[items.Count];
            var cloudH = new double[items.Count];
            var boxHeight = new double[items.Count];
            var cellW = 0.0;

            for (var i = 0; i < items.Count; i++)
            {
                var si = AbsoluteSettings(settings, items[i]);
                perItem[i] = si;
                var t = Math.Max(si.TextHeight, 0.1);
                // 云线尺寸（Create 用 firstPoint/secondPoint 作云线对角）
                cloudW[i] = si.FixedWidth ? Math.Max(si.FixedWidthValue, t * 6) : Math.Max(55.0, t * 18.0);
                cloudH[i] = Math.Max(t * 4.0, 1.0);
                // 文字宽度必须与 Create 内部取的 requestedWidth 完全一致，否则换行行数不同、量出的框高会偏小
                textWidth[i] = si.FixedWidth ? si.FixedWidthValue : Math.Max(55.0, t * 18.0);

                double width, height;
                AnnotationService.MeasureTextBox(doc, items[i], si, textWidth[i], out width, out height);
                boxHeight[i] = height;
                cellW = Math.Max(cellW, cloudW[i] + t * 6.0);
                cellW = Math.Max(cellW, width + t * 4.0); // 兜住文字框比云线宽的情况，避免左右相邻批注粘连
            }

            const int maxColumns = 5;
            var t0 = Math.Max(settings.TextHeight, 0.1);
            var gap = t0;             // 云线底边与文字框顶边之间的净空
            var rowGap = t0 * 2.0;    // 上下两行批注之间的净空
            var columns = Math.Min(maxColumns, Math.Max(1, items.Count));

            var created = 0;
            var rowTop = baseWcs.Y;
            for (var start = 0; start < items.Count; start += columns)
            {
                var end = Math.Min(start + columns, items.Count);
                var rowBoxHeight = 0.0;
                for (var i = start; i < end; i++) rowBoxHeight = Math.Max(rowBoxHeight, boxHeight[i]);
                for (var i = start; i < end; i++)
                {
                    var origin = new Point3d(baseWcs.X + (i - start) * cellW, rowTop, baseWcs.Z);
                    var second = new Point3d(origin.X + cloudW[i], origin.Y + cloudH[i], origin.Z);
                    // 同一行内所有文字框的顶边统一贴齐云线下方 gap 处，行内看起来整齐
                    var textLocation = new Point3d(origin.X, origin.Y - gap - boxHeight[i], origin.Z);
                    try { if (AnnotationService.Create(doc, items[i], perItem[i], origin, second, textLocation)) created++; }
                    catch (Exception ex) { PluginLog.Warning("Exchange.CreateGrid", ex.Message); }
                }
                rowTop -= (cloudH[start] + gap + rowBoxHeight + rowGap);
            }
            return created;
        }

        /// <summary>把全局设置换成"该条批注的绝对尺寸版"：CSV 里带了图面实测值时，
        /// 比例置 1、关掉两个自适应、字高/弧瓣/线宽用实测值——Create 再怎么算都是恒等变换。
        /// 没带实测值（旧 CSV）时返回全局设置，保持旧导入行为。</summary>
        private static AnnotationSettings AbsoluteSettings(AnnotationSettings global, AnnotationData d)
        {
            if (d.RenderTextHeight <= 0 && d.RenderCloudRadius <= 0 && d.RenderLineWidth <= 0) return global;
            var s = global.Clone();
            s.ScaleRatio = 1.0;
            s.FontAutoFit = false;
            s.CloudAutoFit = false;
            if (d.RenderTextHeight > 0)
            {
                s.TextHeight = d.RenderTextHeight;
                if (d.RenderHeaderHeight > 0) s.HeaderHeight = d.RenderHeaderHeight;
                if (d.RenderSecondLineHeight > 0) s.SecondLineHeight = d.RenderSecondLineHeight;
            }
            if (d.RenderCloudRadius > 0) s.CloudRadius = d.RenderCloudRadius;
            if (d.RenderLineWidth > 0) s.LineWidth = d.RenderLineWidth;
            return s;
        }

        /// <summary>复制一份批注数据（含渲染参数），用于更新前留底做差异比较。</summary>
        private static AnnotationData Snapshot(AnnotationData d)
        {
            return new AnnotationData
            {
                Id = d.Id, Number = d.Number, DrawingNo = d.DrawingNo, Date = d.Date,
                Discipline = d.Discipline, Author = d.Author, Role = d.Role, Status = d.Status, Content = d.Content,
                RenderTextHeight = d.RenderTextHeight, RenderHeaderHeight = d.RenderHeaderHeight,
                RenderSecondLineHeight = d.RenderSecondLineHeight, RenderCloudRadius = d.RenderCloudRadius,
                RenderLineWidth = d.RenderLineWidth
            };
        }

        /// <summary>把 CSV 行的字段写入批注数据。策略：列不存在 → 保留图上原值；列存在 → 按单元格取值（空单元格即清空，属用户显式意图）。
        /// 编号是匹配键，仅在非空时更新，避免把编号写空导致之后再也匹配不上。</summary>
        private static void ApplyRow(AnnotationData data, Dictionary<string, string> row)
        {
            var number = Get(row, "编号"); if (number.Length > 0) data.Number = number;
            if (row.ContainsKey("图号")) data.DrawingNo = Get(row, "图号");
            if (row.ContainsKey("日期")) data.Date = Get(row, "日期");
            if (row.ContainsKey("专业")) data.Discipline = Get(row, "专业");
            if (row.ContainsKey("批注人")) data.Author = Get(row, "批注人");
            if (row.ContainsKey("角色")) data.Role = Get(row, "角色");
            if (row.ContainsKey("状态")) data.Status = Get(row, "状态");
            if (row.ContainsKey("批注内容")) data.Content = Get(row, "批注内容");
            // 图面实测尺寸（旧 CSV 没有这些列时保持 0 = 用当前设置重算）。
            if (TryGetNum(row, "图面字高", out var th)) data.RenderTextHeight = th;
            if (TryGetNum(row, "图面首行字高", out var hh)) data.RenderHeaderHeight = hh;
            if (TryGetNum(row, "图面次行字高", out var sh)) data.RenderSecondLineHeight = sh;
            if (TryGetNum(row, "图面云线半径", out var cr)) data.RenderCloudRadius = cr;
            if (TryGetNum(row, "图面线宽", out var lw)) data.RenderLineWidth = lw;
            // 原图坐标（WCS）：云线两角点齐全即视为"可按原位置还原"；文字锚点与标高可缺（缺则按创建规则推算）。
            if (TryReadCoord(row, "云线X1", out var x1) && TryReadCoord(row, "云线Y1", out var y1) &&
                TryReadCoord(row, "云线X2", out var x2) && TryReadCoord(row, "云线Y2", out var y2))
            {
                data.Placed = true;
                data.PlacedX1 = x1; data.PlacedY1 = y1; data.PlacedX2 = x2; data.PlacedY2 = y2;
                data.PlacedLayout = Get(row, "布局");
                if (TryReadCoord(row, "文字X", out var tx) && TryReadCoord(row, "文字Y", out var ty))
                { data.PlacedText = true; data.PlacedTextX = tx; data.PlacedTextY = ty; }
                if (TryReadCoord(row, "标高", out var pz)) data.PlacedZ = pz;
            }
        }

        private static string Num(double value)
            => value > 0 ? value.ToString("0.####", CultureInfo.InvariantCulture) : "";

        /// <summary>坐标单元格：0 与负数都是合法坐标（Num 把 0 写成空、TryGetNum 把 0 判为无效，坐标不能沿用那套约定）。</summary>
        private static string Coord(double value)
            => double.IsNaN(value) || double.IsInfinity(value) ? "" : value.ToString("0.####", CultureInfo.InvariantCulture);

        private static bool TryReadCoord(Dictionary<string, string> row, string key, out double value)
        {
            value = 0;
            var raw = Get(row, key);
            if (raw.Length == 0) return false;
            return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryGetNum(Dictionary<string, string> row, string key, out double value)
        {
            value = 0;
            var raw = Get(row, key);
            if (raw.Length == 0) return false;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) value = 0;
            return value > 0;
        }

        private static string Get(Dictionary<string, string> row, string key) => row.TryGetValue(key, out var v) ? (v ?? "").Trim() : "";

        private static string SafeDrawingName(Document doc)
        {
            try { var name = doc.Name ?? ""; return name.Length > 0 ? Path.GetFileName(name) : "(未命名)"; }
            catch { return "(未命名)"; }
        }

        // ==================== CSV 读写 ====================

        private static string Csv(string value)
        {
            // 先把 CRLF / CR 统一成 LF 再转 \P：否则 "a\r\nb" 会变成 "a \Pb"，
            // 导出再导入后每行末尾多出一个空格（内容与原来不一致）。
            value = (value ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\P"); // MText 换行符，避免破坏行结构
            return value.IndexOfAny(new[] { ',', '"' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        private static string UnCsv(string value) => (value ?? "").Replace("\\P", "\n");

        /// <summary>读取并解析 CSV。编码自动识别（UTF-8 BOM → 严格 UTF-8 → GBK），Excel 中文版"另存为 CSV"的 GBK 文件也能直接导入。
        /// 表头既没有「编号」也没有「批注内容」时抛 <see cref="InvalidDataException"/>（多半是选错文件或编码异常）；
        /// 只缺「编号」时照常导入（全部按新建处理），并通过 <paramref name="headerWarning"/> 提示。</summary>
        private static List<Dictionary<string, string>> ParseFile(string filePath, out string encodingName, out string headerWarning)
        {
            var rows = new List<Dictionary<string, string>>();
            headerWarning = "";
            var text = DataFiles.DecodeDetect(File.ReadAllBytes(filePath), out encodingName);
            var lines = SplitRecords(text); // 已处理带引号的多行内容
            if (lines.Count == 0) return rows;
            var header = ParseLine(lines[0]).Select(h => (h ?? "").Trim().TrimStart('\uFEFF')).ToArray();
            var hasNumber = header.Any(h => string.Equals(h, "编号", StringComparison.OrdinalIgnoreCase));
            var hasContent = header.Any(h => string.Equals(h, "批注内容", StringComparison.OrdinalIgnoreCase));
            if (!hasNumber && !hasContent)
                throw new InvalidDataException("表头中找不到「编号」或「批注内容」列（识别到的编码：" + encodingName
                    + "；首行：" + (lines[0].Length > 60 ? lines[0].Substring(0, 60) + "…" : lines[0])
                    + "）。请使用本插件导出的 CSV 作为模板。");
            if (!hasNumber) headerWarning = "提示：文件没有「编号」列，所有行都按新建处理（不会更新图中已有批注）。";
            else if (!hasContent) headerWarning = "提示：文件没有「批注内容」列，已有批注的内容保持不变。";
            for (var i = 1; i < lines.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) continue;
                var cells = ParseLine(lines[i]);
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (var c = 0; c < header.Length && c < cells.Count; c++) row[header[c]] = UnCsv(cells[c]);
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>按 CSV 规则切分记录：引号内的换行不作为行结束。</summary>
        private static List<string> SplitRecords(string text)
        {
            var records = new List<string>(); var sb = new StringBuilder(); var inQuotes = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"')
                {
                    inQuotes = !inQuotes; sb.Append(c); continue;
                }
                if (!inQuotes && (c == '\n' || c == '\r'))
                {
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    records.Add(sb.ToString()); sb.Clear(); continue;
                }
                sb.Append(c);
            }
            if (sb.Length > 0) records.Add(sb.ToString());
            return records;
        }

        private static List<string> ParseLine(string line)
        {
            var cells = new List<string>(); var sb = new StringBuilder(); var inQuotes = false;
            for (var i = 0; i < line.Length; i++)
            {
                var c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else sb.Append(c);
                }
                else if (c == '"') inQuotes = true;
                else if (c == ',') { cells.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
            cells.Add(sb.ToString());
            return cells;
        }
    }
}
