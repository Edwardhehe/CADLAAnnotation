using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Packaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#endif

namespace GMAnnotation
{
    /// <summary>一条云线的截图区域：WCS 范围 + 所在空间（模型空间 / 某布局的图纸空间）。</summary>
    internal sealed class AnnotationWordCloud
    {
        public Extents3d Extents { get; set; }
        /// <summary>云线所属的块表记录（模型空间或布局块表记录）。</summary>
        public ObjectId SpaceId { get; set; }
        public bool IsModel { get; set; }
        /// <summary>所在布局名（模型空间为 "Model"）；取不到时为 null，此时无法截图。</summary>
        public string LayoutName { get; set; }
    }

    /// <summary>一条待导出的批注：业务文字 + 每条云线（多对一有多条）的截图区域。</summary>
    internal sealed class AnnotationWordEntry
    {
        public string Number { get; set; }
        public string Date { get; set; }
        public string Content { get; set; }
        public List<AnnotationWordCloud> Clouds { get; set; } = new List<AnnotationWordCloud>();
    }

    /// <summary>一处云线的截图结果：成功时为 PNG，失败时为写入 Word 的失败原因。</summary>
    internal sealed class WordCapture
    {
        public byte[] Png { get; set; }
        public string FailureReason { get; set; }
        public bool Succeeded => Png != null && Png.Length > 0;
        public static WordCapture Failed(string reason) => new WordCapture { FailureReason = reason };
    }

    /// <summary>
    /// 批注导出 Word：按批注所在空间分组，切到该空间（模型标签 / 布局图纸空间）后逐条缩放到云线范围并抓取绘图窗口位图，
    /// 再按"时间 / 云线截图 / 批注文字"三行格式手工打包生成 docx（不依赖 Office）。
    /// 不进入、不修改任何布局视口的视图；导出结束先恢复原布局与 CVPORT，再恢复原视图。
    /// </summary>
    internal static class WordExporter
    {
        private sealed class CaptureJob
        {
            public int EntryIndex;
            public int CloudIndex;
            public AnnotationWordEntry Entry;
            public AnnotationWordCloud Cloud;
            public string SpaceKey => Cloud.IsModel ? "\u0001Model" : (Cloud.LayoutName ?? "");
        }

        public static void Export(Document doc, List<AnnotationWordEntry> entries)
        {
            var ed = doc.Editor;
            if (entries == null || entries.Count == 0)
            {
                ed.WriteMessage("\n没有可导出的批注。");
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出批注到 Word",
                Filter = "Word 文档 (*.docx)|*.docx",
                FileName = "批注导出.docx",
                AddExtension = true,
                DefaultExt = ".docx"
            };
            if (dialog.ShowDialog() != true) return;

            var captures = entries.Select(entry => entry.Clouds.Select(_ => (WordCapture)null).ToList()).ToList();
            var jobs = new List<CaptureJob>();
            for (var i = 0; i < entries.Count; i++)
                for (var j = 0; j < entries[i].Clouds.Count; j++)
                    jobs.Add(new CaptureJob { EntryIndex = i, CloudIndex = j, Entry = entries[i], Cloud = entries[i].Clouds[j] });

            // 截图前记住现场（布局 / TILEMODE / CVPORT / 视图），导出完成后恢复。
            var originalState = CadSpaces.Capture();
            ViewTableRecord originalView = null;
            using (var view = ed.GetCurrentView()) originalView = (ViewTableRecord)view.Clone();
            PluginLog.Info("WordExport", $"开始导出 {entries.Count} 条批注、{jobs.Count} 处云线；现场：{originalState}");

            var done = 0;
            var failed = 0;
            try
            {
                // 按空间分组：当前所在空间排在最前，减少布局切换；组内保持批注编号顺序。
                var groups = jobs.GroupBy(job => job.SpaceKey, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => IsCurrentSpace(group.First().Cloud) ? 0 : 1)
                    .ToList();
                foreach (var group in groups)
                {
                    var cloud = group.First().Cloud;
                    var spaceName = CadSpaces.DisplayName(cloud.IsModel, cloud.LayoutName);
                    if (!cloud.IsModel && string.IsNullOrEmpty(cloud.LayoutName))
                    {
                        foreach (var job in group) { captures[job.EntryIndex][job.CloudIndex] = WordCapture.Failed("无法确定云线所在的空间"); failed++; done++; }
                        PluginLog.Warning("WordExport", $"{group.Count()} 处云线无法确定所在空间（不在模型空间或布局中），已写入截图失败占位。");
                        continue;
                    }

                    bool activated;
                    try { activated = CadSpaces.Activate(doc, cloud.IsModel, cloud.LayoutName); }
                    catch (System.Exception ex) { PluginLog.Error("WordExport.SwitchSpace", ex); activated = false; }
                    if (!activated)
                    {
                        foreach (var job in group) { captures[job.EntryIndex][job.CloudIndex] = WordCapture.Failed("无法切换到「" + spaceName + "」"); failed++; done++; }
                        PluginLog.Warning("WordExport", $"无法切换到「{spaceName}」，该空间 {group.Count()} 处云线已写入截图失败占位。");
                        continue;
                    }

                    // 记住该空间自己的视图，组内截图完成后还原，不把别的空间留在缩放后的状态。
                    ViewTableRecord spaceView = null;
                    try { using (var view = ed.GetCurrentView()) spaceView = (ViewTableRecord)view.Clone(); }
                    catch (System.Exception ex) { PluginLog.Warning("WordExport", "读取「" + spaceName + "」当前视图失败：" + ex.Message); }
                    try
                    {
                        foreach (var job in group)
                        {
                            done++;
                            ed.WriteMessage($"\r正在截图 {done}/{jobs.Count}（{spaceName}）    ");
                            var label = $"批注 {job.Entry.Number} 第 {job.CloudIndex + 1} 处云线（{spaceName}）";
                            WordCapture capture;
                            try
                            {
                                ZoomToExtents(doc, job.Cloud.Extents);
                                capture = CaptureWindowPng(doc, label);
                            }
                            catch (System.Exception ex)
                            {
                                PluginLog.Error("WordExport.Capture", ex);
                                capture = WordCapture.Failed(ex.Message);
                            }
                            if (!capture.Succeeded) failed++;
                            captures[job.EntryIndex][job.CloudIndex] = capture;
                        }
                    }
                    finally
                    {
                        if (spaceView != null)
                        {
                            try { ed.SetCurrentView(spaceView); }
                            catch (System.Exception ex) { PluginLog.Warning("WordExport", "还原「" + spaceName + "」视图失败：" + ex.Message); }
                            spaceView.Dispose();
                        }
                    }
                }
            }
            finally
            {
                // 先回到原布局并恢复 CVPORT（视图属于那个布局/视口），再恢复原视图。
                var restored = false;
                try { restored = CadSpaces.Restore(doc, originalState); }
                catch (System.Exception ex) { PluginLog.Error("WordExport.RestoreSpace", ex); }
                try
                {
                    if (originalView != null)
                    {
                        if (restored || CadSpaces.IsState(originalState)) ed.SetCurrentView(originalView);
                        else PluginLog.Warning("WordExport", $"未能完全恢复原空间（期望 {originalState}），为免改错其他空间的视图，未恢复原视图。");
                        ed.UpdateScreen();
                    }
                }
                catch (System.Exception ex) { PluginLog.Warning("WordExport", "恢复原视图失败：" + ex.Message); }
                originalView?.Dispose();
            }

            DocxBuilder.Build(dialog.FileName, entries, captures);
            ed.WriteMessage($"\n已导出 {entries.Count} 条批注到: {dialog.FileName}");
            if (failed > 0) ed.WriteMessage($"\n其中 {failed} 处云线截图失败，Word 中已写入「截图失败」占位，详见日志 {AppPaths.DataFolder}\\Logs\\GMAnnotation.log");
            PluginLog.Info("WordExport", $"导出完成：{dialog.FileName}，云线 {jobs.Count} 处，截图失败 {failed} 处。");
        }

        private static bool IsCurrentSpace(AnnotationWordCloud cloud)
        {
            try { return CadSpaces.IsActive(cloud.IsModel, cloud.LayoutName); }
            catch { return false; }
        }

        /// <summary>缩放当前视图到指定 WCS 范围（与 ZoomToAnnotation 相同的 DCS 变换）。</summary>
        private static void ZoomToExtents(Document doc, Extents3d ext)
        {
            var ed = doc.Editor;
            using (var view = ed.GetCurrentView())
            {
                var wcsToDcs = Matrix3d.PlaneToWorld(view.ViewDirection);
                wcsToDcs = Matrix3d.Displacement(view.Target - Point3d.Origin) * wcsToDcs;
                wcsToDcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) * wcsToDcs;
                wcsToDcs = wcsToDcs.Inverse();
                var min = ext.MinPoint; var max = ext.MaxPoint;
                var dcsCorners = new[]
                {
                    new Point3d(min.X,min.Y,min.Z),new Point3d(max.X,min.Y,min.Z),new Point3d(max.X,max.Y,min.Z),new Point3d(min.X,max.Y,min.Z),
                    new Point3d(min.X,min.Y,max.Z),new Point3d(max.X,min.Y,max.Z),new Point3d(max.X,max.Y,max.Z),new Point3d(min.X,max.Y,max.Z)
                }.Select(point => point.TransformBy(wcsToDcs)).ToArray();
                var minX = dcsCorners.Min(point => point.X); var maxX = dcsCorners.Max(point => point.X);
                var minY = dcsCorners.Min(point => point.Y); var maxY = dcsCorners.Max(point => point.Y);
                var width = Math.Max(maxX - minX, 1); var height = Math.Max(maxY - minY, 1);
                const double margin = 1.15; // 略微外扩，让云线边界本身也进入截图
                view.CenterPoint = new Point2d((minX + maxX) / 2, (minY + maxY) / 2);
                view.Width = width * margin;
                view.Height = height * margin;
                ed.SetCurrentView(view);
            }
            ed.Regen();
        }

        /// <summary>
        /// 抓取当前文档绘图窗口的客户区位图，返回 PNG 字节（宽度超过 1600px 时等比缩小）。
        /// 主路径使用 CAD 自带的 CapturePreviewImage，仅读取当前视图渲染结果；
        /// 后备路径也只请求 CAD 文档窗口自身重绘。两者都不允许抓取桌面像素，防止输入法或其他程序混入 Word。
        /// 多次重试后画面仍接近纯色时判定失败（Word 中写入"截图失败"占位），不再把空白图写进文档。
        /// </summary>
        private static WordCapture CaptureWindowPng(Document doc, string label)
        {
            var hwnd = doc.Window.Handle;
            if (hwnd == IntPtr.Zero) return Fail(label, "取不到 CAD 文档窗口句柄");
            const int maxAttempts = 3;
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                RefreshViewportForCapture(doc, hwnd, attempt);
                if (!NativeMethods.GetClientRect(hwnd, out var rect)) return Fail(label, "读取 CAD 窗口尺寸失败");
                var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
                if (width < 10 || height < 10) return Fail(label, $"CAD 绘图窗口过小（{width}×{height}）");

                using (var bitmap = CaptureCadView(doc, hwnd, width, height))
                {
                    if (bitmap == null) return Fail(label, "CAD 未返回画面");
                    if (!LooksNearlyUniform(bitmap))
                    {
                        using (var scaled = Downscale(bitmap, 1600))
                        using (var stream = new MemoryStream())
                        {
                            scaled.Save(stream, ImageFormat.Png);
                            return new WordCapture { Png = stream.ToArray() };
                        }
                    }
                }
                if (attempt < maxAttempts - 1)
                    PluginLog.Warning("WordExport.Capture", $"{label}：第 {attempt + 1} 次截图接近纯色，等待 CAD 完成渲染后重试。");
            }
            return Fail(label, $"重试 {maxAttempts} 次后画面仍接近纯色（该区域可能没有可见内容，或 CAD 未完成渲染）");
        }

        private static WordCapture Fail(string label, string reason)
        {
            PluginLog.Warning("WordExport.Capture", $"{label}：{reason}，Word 中写入「截图失败」占位。");
            return WordCapture.Failed(reason);
        }

        private static Bitmap CaptureCadView(Document doc, IntPtr hwnd, int width, int height)
        {
            try
            {
                // CAD 原生预览图由宿主的图形管线产生，不受窗口遮挡、输入法或桌面上其他程序影响。
                var native = doc.CapturePreviewImage((uint)width, (uint)height);
                if (native != null) return native;
            }
            catch (System.Exception ex)
            {
                PluginLog.Warning("WordExport.CapturePreviewImage", "CAD 原生视图截图失败，改用文档窗口重绘：" + ex.Message);
            }
            return CaptureCadWindow(hwnd, width, height);
        }

        private static Bitmap CaptureCadWindow(IntPtr hwnd, int width, int height)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            var captured = false;
            using (var graphics = Graphics.FromImage(bitmap))
            {
                var hdc = graphics.GetHdc();
                try
                {
                    // PrintWindow 让 CAD 自己绘制客户区，不会把覆盖在上面的其他窗口复制进来。
                    captured = NativeMethods.PrintWindow(
                        hwnd, hdc,
                        NativeMethods.PwClientOnly | NativeMethods.PwRenderFullContent);
                }
                finally { graphics.ReleaseHdc(hdc); }
            }
            if (!captured) { bitmap.Dispose(); return null; }
            return bitmap;
        }

        /// <summary>让缩放后的 CAD 视图完成再生、窗口重绘和 DWM 合成，再读取屏幕像素。</summary>
        private static void RefreshViewportForCapture(Document doc, IntPtr hwnd, int attempt)
        {
            var ed = doc.Editor;
            ed.UpdateScreen();
            NativeMethods.RedrawWindow(
                hwnd, IntPtr.Zero, IntPtr.Zero,
                NativeMethods.RdwInvalidate | NativeMethods.RdwAllChildren | NativeMethods.RdwUpdateNow);
            NativeMethods.UpdateWindow(hwnd);
            NativeMethods.TryFlushDwm();

            // Regen 在硬件加速宿主中可能先返回，实际绘图由渲染线程继续完成。
            // 首次只给少量等待；若还是纯色，后续重试逐次延长，避免每张图都无条件慢速。
            Thread.Sleep(120 + attempt * 160);
            ed.UpdateScreen();
            NativeMethods.UpdateWindow(hwnd);
            NativeMethods.TryFlushDwm();
        }

        /// <summary>
        /// 判断画面是否接近纯色：以粗网格采样的众数颜色为背景色，逐像素统计与背景差异明显的像素数。
        /// 阈值取总像素的 1/2000（且不少于 50 个）：单像素宽的云线外框（约占画面周长）也能远超阈值，
        /// 避免把"只有一圈细云线"的正常截图误判为失败。
        /// </summary>
        private static bool LooksNearlyUniform(Bitmap bitmap)
        {
            var width = bitmap.Width; var height = bitmap.Height;
            if (width <= 0 || height <= 0) return true;
            var data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[width * 4];
                var counts = new Dictionary<int, int>();
                var stepX = Math.Max(1, width / 160);
                var stepY = Math.Max(1, height / 100);
                for (var y = 0; y < height; y += stepY)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    for (var x = 0; x < width; x += stepX)
                    {
                        var o = x * 4;
                        var key = (row[o + 2] << 16) | (row[o + 1] << 8) | row[o];
                        counts.TryGetValue(key, out var count);
                        counts[key] = count + 1;
                    }
                }
                var background = counts.OrderByDescending(pair => pair.Value).First().Key;
                int br = (background >> 16) & 0xFF, bg = (background >> 8) & 0xFF, bb = background & 0xFF;

                long different = 0;
                long threshold = Math.Max(50L, (long)width * height / 2000);
                for (var y = 0; y < height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    for (var o = 0; o < row.Length; o += 4)
                    {
                        if (Math.Abs(row[o + 2] - br) + Math.Abs(row[o + 1] - bg) + Math.Abs(row[o] - bb) > 24)
                        {
                            if (++different >= threshold) return false;
                        }
                    }
                }
                return true;
            }
            finally { bitmap.UnlockBits(data); }
        }

        private static Bitmap Downscale(Bitmap source, int maxWidth)
        {
            // 返回独立位图，避免调用方释放缩放结果时误将原始截图一起释放。
            if (source.Width <= maxWidth) return new Bitmap(source);
            var scale = (double)maxWidth / source.Width;
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));
            var target = new Bitmap(maxWidth, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(target))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, 0, 0, maxWidth, height);
            }
            return target;
        }

        private static class NativeMethods
        {
            public const uint RdwInvalidate = 0x0001;
            public const uint RdwAllChildren = 0x0080;
            public const uint RdwUpdateNow = 0x0100;
            public const uint PwClientOnly = 0x00000001;
            public const uint PwRenderFullContent = 0x00000002;

            [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out Rect rect);
            [DllImport("user32.dll")] public static extern bool UpdateWindow(IntPtr hWnd);
            [DllImport("user32.dll")] public static extern bool RedrawWindow(IntPtr hWnd, IntPtr updateRect, IntPtr updateRegion, uint flags);
            [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
            [DllImport("dwmapi.dll")] private static extern int DwmFlush();

            public static void TryFlushDwm()
            {
                try { DwmFlush(); }
                catch (DllNotFoundException) { }
                catch (EntryPointNotFoundException) { }
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct Rect
            {
                public int Left;
                public int Top;
                public int Right;
                public int Bottom;
            }
        }

        /// <summary>用 System.IO.Packaging 手工打包最小 docx：段落文字 + 内嵌 PNG 图片。</summary>
        private static class DocxBuilder
        {
            private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
            private const string WP = "http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing";
            private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
            private const string PIC = "http://schemas.openxmlformats.org/drawingml/2006/picture";
            private const string PictureUri = "http://schemas.openxmlformats.org/drawingml/2006/picture";
            private const string ImageRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/image";
            private const string DocumentRelationship = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument";
            private const string DocumentContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml";

            private const double EmuPerCm = 360000.0;
            // A4 纸 + 左右各 2cm 边距，可用宽度约 17cm，留一点余量按 16.5cm 排图。
            private const double UsableWidthEmu = 16.5 * EmuPerCm;
            private const double MaxImageHeightEmu = 12.0 * EmuPerCm;
            private const double EmuPerPixel = 914400.0 / 96.0; // 按 96dpi 换算像素自然尺寸

            public static void Build(string path, List<AnnotationWordEntry> entries, List<List<WordCapture>> capturesPerEntry)
            {
                using (var file = new FileStream(path, FileMode.Create))
                using (var package = Package.Open(file, FileMode.Create))
                {
                    var documentUri = new Uri("/word/document.xml", UriKind.Relative);
                    var documentPart = package.CreatePart(documentUri, DocumentContentType);
                    package.CreateRelationship(documentUri, TargetMode.Internal, DocumentRelationship);

                    // 先写入全部图片部件并记录关系 ID，再生成 document.xml。截图失败的云线不写图片，改写文字占位。
                    var imagesPerEntry = capturesPerEntry
                        .Select(captures => captures.Where(capture => capture != null && capture.Succeeded).Select(capture => capture.Png).ToList())
                        .ToList();
                    var relationshipIds = new List<List<string>>();
                    var imageCounter = 0;
                    for (var i = 0; i < entries.Count; i++)
                    {
                        var ids = new List<string>();
                        relationshipIds.Add(ids);
                        foreach (var png in imagesPerEntry[i])
                        {
                            imageCounter++;
                            var imageUri = new Uri("/word/media/image" + imageCounter + ".png", UriKind.Relative);
                            var imagePart = package.CreatePart(imageUri, "image/png");
                            using (var stream = imagePart.GetStream()) stream.Write(png, 0, png.Length);
                            var relationship = documentPart.CreateRelationship(imageUri, TargetMode.Internal, ImageRelationship, "rId" + imageCounter);
                            ids.Add(relationship.Id);
                        }
                    }

                    package.PackageProperties.Title = "GM批注导出";

                    using (var stream = documentPart.GetStream())
                    {
                        var settings = new XmlWriterSettings { Encoding = System.Text.Encoding.UTF8, Indent = false };
                        using (var writer = XmlWriter.Create(stream, settings))
                        {
                            writer.WriteStartDocument();
                            writer.WriteStartElement("w", "document", W);
                            writer.WriteStartElement("w", "body", W);
                            var docPrId = 0;
                            for (var i = 0; i < entries.Count; i++)
                            {
                                // 第一行：时间；第二行：云线范围截图（多对一放多张）；第三行：批注文字。
                                WriteTextParagraph(writer, entries[i].Date);
                                if (relationshipIds[i].Count > 0)
                                    WriteImagesParagraph(writer, imagesPerEntry[i], relationshipIds[i], ref docPrId);
                                var captures = capturesPerEntry[i];
                                for (var k = 0; k < captures.Count; k++)
                                {
                                    var capture = captures[k];
                                    if (capture != null && capture.Succeeded) continue;
                                    var where = captures.Count > 1 ? "第 " + (k + 1) + " 处云线：" : "";
                                    WriteTextParagraph(writer, "【截图失败】" + where + (capture?.FailureReason ?? "未截图"));
                                }
                                WriteTextParagraph(writer, entries[i].Content);
                                WriteTextParagraph(writer, "");
                            }
                            WriteSectionProperties(writer);
                            writer.WriteEndElement(); // body
                            writer.WriteEndElement(); // document
                            writer.WriteEndDocument();
                        }
                    }
                }
            }

            private static void WriteTextParagraph(XmlWriter writer, string text)
            {
                writer.WriteStartElement("w", "p", W);
                writer.WriteStartElement("w", "r", W);
                var lines = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (i > 0) writer.WriteElementString("w", "br", W, null);
                    writer.WriteStartElement("w", "t", W);
                    writer.WriteAttributeString("xml", "space", null, "preserve");
                    writer.WriteString(lines[i]);
                    writer.WriteEndElement();
                }
                writer.WriteEndElement(); // r
                writer.WriteEndElement(); // p
            }

            private static void WriteImagesParagraph(XmlWriter writer, List<byte[]> images, List<string> relationshipIds, ref int docPrId)
            {
                // 每行最多两张图片，分批写入独立段落。
                const int perRow = 2;
                for (var rowStart = 0; rowStart < images.Count; rowStart += perRow)
                {
                    var rowEnd = Math.Min(rowStart + perRow, images.Count);
                    var rowCount = rowEnd - rowStart;
                    var slotWidth = UsableWidthEmu / rowCount;
                    writer.WriteStartElement("w", "p", W);
                    for (var i = rowStart; i < rowEnd; i++)
                    {
                        Size pixelSize;
                        using (var stream = new MemoryStream(images[i]))
                        using (var bitmap = new Bitmap(stream)) pixelSize = bitmap.Size;
                        var cx = Math.Min(slotWidth, pixelSize.Width * EmuPerPixel);
                        var cy = cx * pixelSize.Height / Math.Max(1, pixelSize.Width);
                        if (cy > MaxImageHeightEmu)
                        {
                            cx *= MaxImageHeightEmu / cy;
                            cy = MaxImageHeightEmu;
                        }
                        docPrId++;
                        WriteImageRun(writer, relationshipIds[i], docPrId, (long)cx, (long)cy, "云线范围" + docPrId);
                    }
                    writer.WriteEndElement(); // p
                }
            }

            private static void WriteImageRun(XmlWriter writer, string relationshipId, int docPrId, long cx, long cy, string name)
            {
                writer.WriteStartElement("w", "r", W);
                writer.WriteStartElement("w", "drawing", W);
                writer.WriteStartElement("wp", "inline", WP);
                writer.WriteAttributeString("distT", "0");
                writer.WriteAttributeString("distB", "0");
                writer.WriteAttributeString("distL", "0");
                writer.WriteAttributeString("distR", "0");

                writer.WriteStartElement("wp", "extent", WP);
                writer.WriteAttributeString("cx", cx.ToString());
                writer.WriteAttributeString("cy", cy.ToString());
                writer.WriteEndElement();

                writer.WriteStartElement("wp", "docPr", WP);
                writer.WriteAttributeString("id", docPrId.ToString());
                writer.WriteAttributeString("name", name);
                writer.WriteEndElement();

                writer.WriteStartElement("a", "graphic", A);
                writer.WriteStartElement("a", "graphicData", A);
                writer.WriteAttributeString("uri", PictureUri);

                writer.WriteStartElement("pic", "pic", PIC);
                writer.WriteStartElement("pic", "nvPicPr", PIC);
                writer.WriteStartElement("pic", "cNvPr", PIC);
                writer.WriteAttributeString("id", "0");
                writer.WriteAttributeString("name", name);
                writer.WriteEndElement();
                writer.WriteStartElement("pic", "cNvPicPr", PIC);
                writer.WriteEndElement();
                writer.WriteEndElement(); // nvPicPr

                writer.WriteStartElement("pic", "blipFill", PIC);
                writer.WriteStartElement("a", "blip", A);
                writer.WriteAttributeString("r", "embed", R, relationshipId);
                writer.WriteEndElement();
                writer.WriteStartElement("a", "stretch", A);
                writer.WriteStartElement("a", "fillRect", A);
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement(); // blipFill

                writer.WriteStartElement("pic", "spPr", PIC);
                writer.WriteStartElement("a", "xfrm", A);
                writer.WriteStartElement("a", "off", A);
                writer.WriteAttributeString("x", "0");
                writer.WriteAttributeString("y", "0");
                writer.WriteEndElement();
                writer.WriteStartElement("a", "ext", A);
                writer.WriteAttributeString("cx", cx.ToString());
                writer.WriteAttributeString("cy", cy.ToString());
                writer.WriteEndElement();
                writer.WriteEndElement(); // xfrm
                writer.WriteStartElement("a", "prstGeom", A);
                writer.WriteAttributeString("prst", "rect");
                writer.WriteStartElement("a", "avLst", A);
                writer.WriteEndElement();
                writer.WriteEndElement();
                writer.WriteEndElement(); // spPr

                writer.WriteEndElement(); // pic
                writer.WriteEndElement(); // graphicData
                writer.WriteEndElement(); // graphic
                writer.WriteEndElement(); // inline
                writer.WriteEndElement(); // drawing
                writer.WriteEndElement(); // r
            }

            private static void WriteSectionProperties(XmlWriter writer)
            {
                writer.WriteStartElement("w", "sectPr", W);
                writer.WriteStartElement("w", "pgSz", W);
                writer.WriteAttributeString("w", "w", W, "11906"); // A4
                writer.WriteAttributeString("w", "h", W, "16838");
                writer.WriteEndElement();
                writer.WriteStartElement("w", "pgMar", W);
                writer.WriteAttributeString("w", "top", W, "1134"); // 2cm
                writer.WriteAttributeString("w", "right", W, "1134");
                writer.WriteAttributeString("w", "bottom", W, "1134");
                writer.WriteAttributeString("w", "left", W, "1134");
                writer.WriteAttributeString("w", "header", W, "720");
                writer.WriteAttributeString("w", "footer", W, "720");
                writer.WriteAttributeString("w", "gutter", W, "0");
                writer.WriteEndElement();
                writer.WriteEndElement(); // sectPr
            }
        }
    }
}
