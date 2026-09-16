# 提示词：AutoCAD/ZWCAD 插件实现"CAD 区域预览截图 + 导出 Word"

> 用途：在任意 C# 编写的 AutoCAD / ZWCAD 托管插件项目中，实现"把图纸指定区域截图成 PNG 预览图"及"按记录导出带截图的 Word 文档"。
> 本提示词可直接粘贴给 Claude 使用，代码参考自 LA批注插件（WordExporter.cs），可在不同宿主、不同业务下复用。

---

## 一、任务目标

在 C# 编写的 AutoCAD / ZWCAD 托管插件中实现以下功能：

1. **区域预览截图（核心）**：给定一批图纸上的目标区域（WCS 三维包围盒 `Extents3d`，例如批注云线的范围），逐个把当前视图缩放到该区域并截取 CAD 绘图窗口画面，输出 PNG 图片字节。此能力可用于生成批注预览图、图纸缩略图、区域快照等。
2. **导出 Word（可选扩展）**：把每个区域的预览图按"时间行 + 截图行 + 文字行"的格式嵌入 .docx 文档。

**硬性约束：**
- 不依赖 Office COM、docx 第三方库、网络服务、后端渲染服务——全部用 .NET 标准库完成（`System.Drawing` 截图 + `System.IO.Packaging` + `System.Xml` 打包 Word）。
- 一套源码同时兼容 AutoCAD（`Autodesk.AutoCAD.*`）与 ZWCAD（`ZwSoft.ZwCAD.*`），用 `#if ZWCAD` 条件编译切换命名空间。
- 截图完全复用宿主 CAD 自身的渲染结果（非自绘、非 canvas）。

## 二、核心实现方案

### 1. 视图缩放 ZoomToExtents（截图前把视图切到目标区域）

把当前视图的中心点和宽高设为目标 WCS 范围的 **DCS 包围盒**，并外扩 1.15 倍，让区域边界本身完整进入画面：

```csharp
private static void ZoomToExtents(Document doc, Extents3d ext)
{
    var ed = doc.Editor;
    using (var view = ed.GetCurrentView())
    {
        // WCS → DCS 变换：含视图方向、Target 位移、视图旋转
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
        const double margin = 1.15; // 略微外扩，让区域边界本身进入截图
        view.CenterPoint = new Point2d((minX + maxX) / 2, (minY + maxY) / 2);
        view.Width = width * margin;
        view.Height = height * margin;
        ed.SetCurrentView(view);
    }
    ed.Regen(); // 必须刷新画面
}
```

### 2. 视图备份与恢复（截图前后视图一致）

截图前 clone 当前 `ViewTableRecord`，全部截图完成后在 `finally` 中恢复并刷新屏幕；恢复失败不影响导出结果：

```csharp
ViewTableRecord originalView = null;
using (var view = ed.GetCurrentView()) originalView = (ViewTableRecord)view.Clone();
try
{
    // ... 逐个区域 ZoomToExtents + 截图 ...
}
finally
{
    try
    {
        if (originalView != null)
        {
            ed.SetCurrentView(originalView);
            ed.UpdateScreen();
        }
        originalView?.Dispose();
    }
    catch { /* 恢复视图失败不影响结果 */ }
}
```

### 3. 窗口截图 CaptureWindowPng（GDI BitBlt 抓取客户区）

取 `doc.Window.Handle` 窗口句柄，用 `user32.GetClientRect / GetDC` + `gdi32.BitBlt` 把 CAD 绘图窗口客户区拷进 24bpp 位图：

```csharp
private static byte[] CaptureWindowPng(Document doc)
{
    var hwnd = doc.Window.Handle;
    if (hwnd == IntPtr.Zero) return null;
    if (!NativeMethods.GetClientRect(hwnd, out var rect)) return null;
    var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
    if (width < 10 || height < 10) return null;

    var hdcSource = NativeMethods.GetDC(hwnd);
    if (hdcSource == IntPtr.Zero) return null;
    try
    {
        using (var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var hdcTarget = graphics.GetHdc();
            NativeMethods.BitBlt(hdcTarget, 0, 0, width, height, hdcSource, 0, 0, NativeMethods.SrcCopy);
            graphics.ReleaseHdc(hdcTarget);
            using (var scaled = Downscale(bitmap, 1600))
            using (var stream = new MemoryStream())
            {
                scaled.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }
    }
    finally
    {
        NativeMethods.ReleaseDC(hwnd, hdcSource);
    }
}

// 宽度超过 maxWidth(1600px) 时等比缩小，保持清晰度可控
private static Bitmap Downscale(Bitmap source, int maxWidth)
{
    if (source.Width <= maxWidth) return source;
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
    public const int SrcCopy = 0x00CC0020;
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out Rect rect);
    [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height, IntPtr hdcSource, int xSource, int ySource, int rop);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
}
```

### 4. 主流程（逐区域截图，单条失败不中断）

```csharp
// 外层：每条记录；内层：每条记录的多个区域（多对一）
foreach (var extents in entry.Regions)
{
    try
    {
        ZoomToExtents(doc, extents);
        var png = CaptureWindowPng(doc);
        if (png != null && png.Length > 0) images.Add(png);
    }
    catch (Exception ex) { PluginLog.Error("Capture", ex); } // 只记日志，继续
}
```

### 5. 导出 Word（可选扩展）：System.IO.Packaging 手工打包 docx

无 docx 库，直接写 OPC 容器（`Package.Open` + `XmlWriter`）：

- **文档结构**：`/word/document.xml` + `/word/media/imageN.png`（图片部件）+ 部件关系（`rIdN`），图片以 `<w:drawing><wp:inline>` 内联嵌入，`a:blip r:embed=rId`。
- **页面**：A4（11906×16838 twip）+ 四边 2cm 边距（1134 twip）。
- **排图**：**每行最多 2 张**，多余分批为独立段落；单张全宽 / 两张各半宽，可用宽度 16.5cm（`EmuPerCm = 360000`）。
- **尺寸换算**：按 96dpi 换算像素自然尺寸（`EmuPerPixel = 914400.0 / 96.0`），不超自然尺寸，高度上限 12cm，超出则等比缩。
- 每条记录三行：时间行 → 截图行（可多段）→ 文字行 → 空行。

```csharp
// 先写入全部图片部件并记录 rId，再生成 document.xml
imageCounter++;
var imageUri = new Uri("/word/media/image" + imageCounter + ".png", UriKind.Relative);
var imagePart = package.CreatePart(imageUri, "image/png");
using (var stream = imagePart.GetStream()) stream.Write(png, 0, png.Length);
var relationship = documentPart.CreateRelationship(imageUri, TargetMode.Internal,
    ImageRelationship, "rId" + imageCounter);

// 每行最多两张图片，分批写入独立段落
const int perRow = 2;
for (var rowStart = 0; rowStart < images.Count; rowStart += perRow)
{
    var rowEnd = Math.Min(rowStart + perRow, images.Count);
    var rowCount = rowEnd - rowStart;
    var slotWidth = UsableWidthEmu / rowCount; // 单张=全宽，两张=各半宽
    writer.WriteStartElement("w", "p", W);
    for (var i = rowStart; i < rowEnd; i++)
    {
        // 尺寸：min(槽位宽, 像素自然尺寸)；超 12cm 高再等比缩
        var cx = Math.Min(slotWidth, pixelSize.Width * EmuPerPixel);
        var cy = cx * pixelSize.Height / Math.Max(1, pixelSize.Width);
        if (cy > MaxImageHeightEmu) { cx *= MaxImageHeightEmu / cy; cy = MaxImageHeightEmu; }
        WriteImageRun(writer, relationshipIds[i], ++docPrId, (long)cx, (long)cy, "区域" + docPrId);
    }
    writer.WriteEndElement(); // p
}
```

## 三、注意事项（务必遵守）

1. **必须在 CAD UI 主线程执行**——`GetDC/BitBlt` 依赖窗口句柄，别在后台线程调用；命令入口即主线程，若从事件/线程发起需 `Document.SendStringToExecute` 或 `Application.DocumentManager` 同步。
2. 每次 `SetCurrentView` 后**必须 `Regen()`**，否则截到的是旧画面。
3. 截到的是**窗口当前显示内容**（含当前图层开关、背景色、屏幕上的 UI 元素）——如果 UI 浮窗遮挡绘图区，截图前需先隐藏、截完恢复。
4. 单条区域截图失败**只记日志继续**，不能让一条坏数据中断整批。
5. 位图统一 24bpp、宽度上限 1600px，避免 Word 文档体积过大。
6. 注意回收：`Bitmap/Graphics/DC/ViewTableRecord` 全部 `using`/`Dispose`，否则 CAD 会话长期运行会内存泄漏。

## 四、验收标准

1. 对任意 `Extents3d` 区域能生成包含该区域全部内容、外扩约 1.15 倍的 PNG 预览图。
2. 截图流程结束后，用户当前视图**完全恢复**（中心、缩放、视角一致）。
3. 高分辨率窗口下输出图片宽度 ≤ 1600px，比例不变、画质清晰。
4. 多区域导出：Word 中每条记录 = 时间行 + 截图行（每行 ≤2 张）+ 文字行，A4 排版不溢出。
5. 整个流程零第三方依赖，AutoCAD 与 ZWCAD 两套宿主编译运行一致。

## 五、迁移到其他项目的适配点

| 原项目 | 换项目时改成 |
|---|---|
| `doc.Window.Handle` | 对应宿主的文档窗口句柄（ZWCAD 同名；其他宿主找其窗口句柄获取方式） |
| 目标区域来源 = 批注云线 `GeometricExtents` | 任何你想预览的实体范围：选择集、图框块、视图命名视图、矩形范围 |
| 区域来源命名组 `LA_PZ_NOTE_*` 的 XData/DBDictionary | 换成新项目自己的数据存储 |
| 截图后写 Word | 若只要预览图，可省略 DocxBuilder 部分，直接保存/展示 PNG 字节 |
