using System;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace GMAnnotation
{
    /// <summary>设置持久化：将 AnnotationSettings 读写到 %AppData%/GMAnnotation/settings.xml。</summary>
    internal static class SettingsStore
    {
        private static readonly string Folder = AppPaths.DataFolder;
        private static readonly string PathName = Path.Combine(Folder, "settings.xml");

        /// <summary>已为哪个"损坏版本"（修改时间+长度）做过备份，避免每次读取都重复备份。</summary>
        private static string _backedUpSignature;

        /// <summary>最近一次读取时 settings.xml 是否损坏（无法解析）。损坏期间只有用户在设置窗口主动保存才会覆盖原文件。</summary>
        public static bool IsCorrupt { get; private set; }

        /// <summary>最近一次为损坏文件生成的备份路径（settings.xml.bad-时间戳），没有则为 null。</summary>
        public static string CorruptBackupPath { get; private set; }

        public static AnnotationSettings Load()
        {
            var s = new AnnotationSettings();
            try
            {
                if (!File.Exists(PathName))
                {
                    // 另一处正在原子替换 settings.xml 时可能一瞬间看不到文件：稍等再看一次；仍不见但留有 .tmp 时按"暂时读不到"处理。
                    System.Threading.Thread.Sleep(60);
                    if (!File.Exists(PathName))
                    {
                        IsCorrupt = false;
                        if (File.Exists(PathName + ".tmp")) MarkFallback(s, "settings.xml 正在被替换");
                        return s; // 首次使用（真的没有文件）：默认值可以正常保存
                    }
                }
                XElement x = null;
                for (var attempt = 0; x == null; attempt++)
                {
                    try { x = XElement.Load(PathName); }
                    catch (System.Xml.XmlException ex)
                    {
                        // 文件损坏：先备份，再用默认值运行；原文件保持不动，直到用户在设置窗口点「保存设置」。
                        IsCorrupt = true;
                        if (BackupCorruptFile()) PluginLog.Error("Settings.Load.Corrupt", ex); // 同一损坏版本只记一次，避免高频读取刷屏
                        return s;
                    }
                    catch (Exception ex)
                    {
                        // 被占用 / 无权限 / 替换中找不到：属于暂时读不到，不是损坏。重试几次，仍失败就用默认值运行，
                        // 但给对象打上标记禁止自动保存——否则编号同步、面板写回等会把默认值（比例 1:1 等）写进 settings.xml。
                        if (attempt < 4) { System.Threading.Thread.Sleep(60); continue; }
                        IsCorrupt = false;
                        MarkFallback(s, ex.Message);
                        return s;
                    }
                }
                IsCorrupt = false;
                Read(x, s);
            }
            catch (Exception ex) { PluginLog.Error("Settings.Load",ex); MarkFallback(s, ex.Message); }
            return s;
        }

        /// <summary>读不到设置时使用的默认值对象：打标记，<see cref="Save"/> 会拒绝自动写回。</summary>
        private static void MarkFallback(AnnotationSettings s, string reason)
        {
            s.LoadedFromFallback = true;
            PluginLog.Warning("Settings.Load", "settings.xml 暂时读不到，本次按默认值运行且不会自动保存：" + reason);
        }

        /// <summary>从 XML 根节点读取全部设置项（缺项用默认值），读完做范围校验。</summary>
        private static void Read(XElement x, AnnotationSettings s)
        {
            {
                s.Shape = Get(x, "Shape", s.Shape); s.CloudStyle = Get(x, "CloudStyle", s.CloudStyle);
                s.LayerName = Get(x, "LayerName", s.LayerName);
                // 改名前版本的默认图层名为 "LA-批注"：升级后统一按新默认名 "GM-批注" 使用（用户自定义过的图层名不受影响）。
                if (string.Equals(s.LayerName, "LA-批注", StringComparison.OrdinalIgnoreCase)) s.LayerName = "GM-批注";
                s.TextStyleName = Get(x, "TextStyleName", s.TextStyleName);
                s.ColorIndex = ParseShort(Get(x, "ColorIndex", s.ColorIndex.ToString()), s.ColorIndex);
                s.CloudColor = ParseShort(Get(x,"CloudColor","6"),6); s.LeaderColor=ParseShort(Get(x,"LeaderColor","6"),6); s.TextColor=ParseShort(Get(x,"TextColor","6"),6);s.BoxColor=ParseShort(Get(x,"BoxColor","6"),6); s.ReplyColor=ParseShort(Get(x,"ReplyColor","6"),6);s.ScreenshotBackgroundColor=ParseShort(Get(x,"ScreenshotBackgroundColor","7"),7);s.PassColor=ParseShort(Get(x,"PassColor","6"),6);s.CheckColor=ParseShort(Get(x,"CheckColor","6"),6);s.ScreenshotBackgroundOnceReply=ParseBool(Get(x,"ScreenshotBackgroundOnceReply","false"),false);
                s.TextHeight = ParseDouble(Get(x, "TextHeight", "3"), s.TextHeight);
                s.HeaderHeight=ParseDouble(Get(x,"HeaderHeight","3"),3);s.SecondLineHeight=ParseDouble(Get(x,"SecondLineHeight","3"),3);
                s.CloudRadius = ParseDouble(Get(x, "CloudRadius", "2"), s.CloudRadius);
                s.LineWidth=ParseDouble(Get(x,"LineWidth","0.2"),0.2);s.ScaleRatio=ParseDouble(Get(x,"ScaleRatio","1"),1);s.CloudAutoFit=ParseBool(Get(x,"CloudAutoFit","true"),true);s.FontAutoFit=ParseBool(Get(x,"FontAutoFit","true"),true);s.FixedWidth=ParseBool(Get(x,"FixedWidth","false"),false);s.FixedWidthValue=ParseDouble(Get(x,"FixedWidthValue","55"),55);
                s.DefaultAuthor = Get(x, "DefaultAuthor", s.DefaultAuthor);
                s.DefaultDiscipline = Get(x, "DefaultDiscipline", s.DefaultDiscipline);
                s.DefaultRole=Get(x,"DefaultRole",s.DefaultRole);
                s.AutoNumber = ParseBool(Get(x, "AutoNumber", "true"), true);
                s.NextNumber = ParseInt(Get(x, "NextNumber", "1"), 1);
                s.AutoCloseOrtho=ParseBool(Get(x,"AutoCloseOrtho","true"),true);s.AutoCloseSnap=ParseBool(Get(x,"AutoCloseSnap","true"),true);s.ViewTopIsNorth=ParseBool(Get(x,"ViewTopIsNorth","true"),true);s.DoubleClickEdit=ParseBool(Get(x,"DoubleClickEdit","true"),true);s.ContinuousAnnotation=ParseBool(Get(x,"ContinuousAnnotation","true"),true);s.CloudOnly=ParseBool(Get(x,"CloudOnly","false"),false);s.SameColors=ParseBool(Get(x,"SameColors","true"),true);s.LayerAppendDate=ParseBool(Get(x,"LayerAppendDate","true"),true);s.LayerAppendName=ParseBool(Get(x,"LayerAppendName","false"),false);s.DateBeforeName=ParseBool(Get(x,"DateBeforeName","true"),true);s.Connector=Get(x,"Connector","-");s.Plottable=ParseBool(Get(x,"Plottable","false"),false);s.CheckHeight=ParseDouble(Get(x,"CheckHeight","8"),8);
                s.AutoTextViewPercent=ParseDouble(Get(x,"AutoTextViewPercent","5"),5);
                s.ShowDiscipline=ParseBool(Get(x,"ShowDiscipline","true"),true);s.ShowAuthor=ParseBool(Get(x,"ShowAuthor","true"),true);s.ShowRole=ParseBool(Get(x,"ShowRole","true"),true);s.ShowDate=ParseBool(Get(x,"ShowDate","true"),true);s.ShowStatus=ParseBool(Get(x,"ShowStatus","true"),true);s.ShowDrawingNo=ParseBool(Get(x,"ShowDrawingNo","true"),true);
                s.ArchiveOnCreate=ParseBool(Get(x,"ArchiveOnCreate","true"),true);
                s.ContentSuggest=ParseBool(Get(x,"ContentSuggest","true"),true);
                s.CloudMarkerEnabled=ParseBool(Get(x,"CloudMarkerEnabled","false"),false);s.CloudMarkerText=Get(x,"CloudMarkerText","A");
                s.CloudMarkerAutoIncrement=ParseBool(Get(x,"CloudMarkerAutoIncrement","false"),false);
                s.CloudMarkerTextHeight=ParseDouble(Get(x,"CloudMarkerTextHeight","2.5"),2.5);s.CloudMarkerBoxHeight=ParseDouble(Get(x,"CloudMarkerBoxHeight","5"),5);s.CloudMarkerBoxWidth=ParseDouble(Get(x,"CloudMarkerBoxWidth","10"),10);
                s.CloudMarkerShape=CloudMarker.Normalize(Get(x,"CloudMarkerShape",CloudMarker.DefaultShape));s.CloudMarkerColor=ParseShort(Get(x,"CloudMarkerColor","-1"),(short)-1);
                s.SeparateColors=Get(x,"SeparateColors","");
            }
            Sanitize(s);
        }

        /// <summary>读入值的范围校验：明显非法（非数字、≤0、越界、未知选项）的项回退为默认值，避免在运行时才出错。
        /// 颜色只纠正 0~256 以外的值（0=随块、256=随层 原样保留）。</summary>
        private static void Sanitize(AnnotationSettings s)
        {
            var d = new AnnotationSettings();
            double Pos(double v, double f) => double.IsNaN(v) || double.IsInfinity(v) || v <= 0 ? f : v;
            short Aci(short v, short f) => v < 0 || v > 256 ? f : v;
            s.TextHeight = Pos(s.TextHeight, d.TextHeight); s.HeaderHeight = Pos(s.HeaderHeight, d.HeaderHeight); s.SecondLineHeight = Pos(s.SecondLineHeight, d.SecondLineHeight);
            s.CloudRadius = Pos(s.CloudRadius, d.CloudRadius);
            if (double.IsNaN(s.LineWidth) || double.IsInfinity(s.LineWidth) || s.LineWidth < 0) s.LineWidth = d.LineWidth;
            s.ScaleRatio = Pos(s.ScaleRatio, d.ScaleRatio);
            s.AutoTextViewPercent = Math.Min(10, Math.Max(0.1, Pos(s.AutoTextViewPercent, d.AutoTextViewPercent)));
            if (double.IsNaN(s.FixedWidthValue) || s.FixedWidthValue < 1) s.FixedWidthValue = d.FixedWidthValue;
            s.CheckHeight = Pos(s.CheckHeight, d.CheckHeight);
            s.CloudMarkerTextHeight = Pos(s.CloudMarkerTextHeight, d.CloudMarkerTextHeight);
            s.CloudMarkerBoxHeight = Pos(s.CloudMarkerBoxHeight, d.CloudMarkerBoxHeight);
            s.CloudMarkerBoxWidth = Pos(s.CloudMarkerBoxWidth, d.CloudMarkerBoxWidth);
            if (s.NextNumber < 1) s.NextNumber = 1;
            if (s.Shape != "矩形" && s.Shape != "菱形" && s.Shape != "椭圆") s.Shape = d.Shape;
            if (s.CloudStyle != "等宽" && s.CloudStyle != "渐变") s.CloudStyle = d.CloudStyle;
            if (string.IsNullOrWhiteSpace(s.LayerName)) s.LayerName = d.LayerName;
            if (s.Connector == null) s.Connector = d.Connector;
            s.ColorIndex = Aci(s.ColorIndex, d.ColorIndex); s.CloudColor = Aci(s.CloudColor, d.CloudColor); s.LeaderColor = Aci(s.LeaderColor, d.LeaderColor);
            s.TextColor = Aci(s.TextColor, d.TextColor); s.BoxColor = Aci(s.BoxColor, d.BoxColor); s.ReplyColor = Aci(s.ReplyColor, d.ReplyColor);
            s.PassColor = Aci(s.PassColor, d.PassColor); s.CheckColor = Aci(s.CheckColor, d.CheckColor); s.ScreenshotBackgroundColor = Aci(s.ScreenshotBackgroundColor, d.ScreenshotBackgroundColor);
            if (s.SeparateColors == null) s.SeparateColors = "";
        }

        /// <summary>导出设置到指定文件（与 settings.xml 同格式，不含工具栏显隐等运行状态）。</summary>
        public static void ExportTo(AnnotationSettings s, string path)
        {
            var text = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" + BuildRoot(s).ToString();
            DataFiles.WriteAllTextAtomic(path, text, new System.Text.UTF8Encoding(false));
        }

        /// <summary>从导出的设置文件读取（不写入本机设置，由设置窗口填入界面、用户保存后才生效）。</summary>
        public static bool TryLoadFrom(string path, out AnnotationSettings s, out string error)
        {
            s = new AnnotationSettings(); error = null;
            try
            {
                var x = XElement.Load(path);
                if (x.Name.LocalName != "Settings") { error = "不是 GM批注 设置文件（根节点不是 Settings）。"; return false; }
                Read(x, s);
                return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        /// <summary>把损坏的 settings.xml 复制为 settings.xml.bad-yyyyMMdd-HHmmss（同一损坏版本只备份一次）。</summary>
        private static bool BackupCorruptFile()
        {
            try
            {
                var info = new FileInfo(PathName);
                if (!info.Exists) return false;
                var signature = info.LastWriteTimeUtc.Ticks + ":" + info.Length;
                if (signature == _backedUpSignature) return false;
                var target = PathName + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                if (!File.Exists(target)) File.Copy(PathName, target, false);
                _backedUpSignature = signature;
                CorruptBackupPath = target;
                PluginLog.Warning("Settings.Backup", "settings.xml 无法解析，已备份为 " + target + "，当前使用默认设置。");
                return true;
            }
            catch (Exception ex) { PluginLog.Warning("Settings.Backup", ex.Message); return false; }
        }

        /// <summary>磁盘上的 settings.xml 当前是否无法解析。</summary>
        private static bool FileIsCorrupt()
        {
            try { if (!File.Exists(PathName)) return false; XElement.Load(PathName); return false; }
            catch { return true; }
        }

        /// <summary>保存设置（跨进程加锁 + 临时文件原子替换）。内容与磁盘上完全相同时不写盘。
        /// settings.xml 损坏时：自动保存（编号同步、开关记忆等）一律跳过并返回 false，不覆盖原文件；
        /// 只有 <paramref name="userConfirmed"/>=true（用户在设置窗口点「保存设置」）才会在备份后重建文件。</summary>
        public static bool Save(AnnotationSettings s, bool userConfirmed = false)
        {
            if (s != null && s.LoadedFromFallback && !userConfirmed)
            {
                // 这份设置是"读不到文件时的默认值"，自动保存会把用户的比例、字高等冲掉：跳过。
                PluginLog.Warning("Settings.Save", "当前设置来自读取失败时的默认值，自动保存已跳过，settings.xml 保持不变。");
                return false;
            }
            using (DataFiles.Lock(false))
            {
                if (FileIsCorrupt())
                {
                    IsCorrupt = true;
                    BackupCorruptFile();
                    if (!userConfirmed)
                    {
                        PluginLog.Warning("Settings.Save", "settings.xml 已损坏，自动保存已跳过（请在 GM_PZ_SETTINGS 中点「保存设置」重建）。");
                        return false;
                    }
                }
                var root = BuildRoot(s);
                // 不属于 AnnotationSettings 的节点（工具栏显隐、待恢复的系统变量）由各自模块单独读写：整文件重写时原样保留，
                // 防止面板/设置窗口长时间持有的旧设置对象把这些状态覆盖掉。
                foreach (var key in ExtraKeys)
                {
                    var value = ReadElement(key);
                    if (value != null) root.Add(new XElement(key, value));
                }
                WriteIfChanged(root);
                IsCorrupt = false;
                s.LoadedFromFallback = false; // 用户已确认保存：此后就是正式设置
                return true;
            }
        }

        private static XElement BuildRoot(AnnotationSettings s)
        {
            return new XElement("Settings",
                new XElement("Shape",s.Shape),new XElement("CloudStyle",s.CloudStyle),
                new XElement("LayerName", s.LayerName), new XElement("ColorIndex", s.ColorIndex),
                new XElement("TextStyleName",s.TextStyleName),new XElement("CloudColor",s.CloudColor),new XElement("LeaderColor",s.LeaderColor),new XElement("TextColor",s.TextColor),new XElement("BoxColor",s.BoxColor),new XElement("ReplyColor",s.ReplyColor),new XElement("ScreenshotBackgroundColor",s.ScreenshotBackgroundColor),new XElement("PassColor",s.PassColor),new XElement("CheckColor",s.CheckColor),new XElement("ScreenshotBackgroundOnceReply",s.ScreenshotBackgroundOnceReply),
                new XElement("TextHeight", s.TextHeight.ToString(CultureInfo.InvariantCulture)),
                new XElement("HeaderHeight",s.HeaderHeight.ToString(CultureInfo.InvariantCulture)),new XElement("SecondLineHeight",s.SecondLineHeight.ToString(CultureInfo.InvariantCulture)),
                new XElement("CloudRadius", s.CloudRadius.ToString(CultureInfo.InvariantCulture)),
                new XElement("LineWidth",s.LineWidth.ToString(CultureInfo.InvariantCulture)),new XElement("ScaleRatio",s.ScaleRatio.ToString(CultureInfo.InvariantCulture)),new XElement("CloudAutoFit",s.CloudAutoFit),new XElement("FontAutoFit",s.FontAutoFit),new XElement("FixedWidth",s.FixedWidth),new XElement("FixedWidthValue",s.FixedWidthValue.ToString(CultureInfo.InvariantCulture)),
                new XElement("DefaultAuthor", s.DefaultAuthor), new XElement("DefaultDiscipline", s.DefaultDiscipline),
                new XElement("DefaultRole",s.DefaultRole),new XElement("AutoNumber", s.AutoNumber), new XElement("NextNumber", s.NextNumber),
                new XElement("AutoCloseOrtho",s.AutoCloseOrtho),new XElement("AutoCloseSnap",s.AutoCloseSnap),new XElement("ViewTopIsNorth",s.ViewTopIsNorth),new XElement("DoubleClickEdit",s.DoubleClickEdit),new XElement("ContinuousAnnotation",s.ContinuousAnnotation),new XElement("CloudOnly",s.CloudOnly),new XElement("SameColors",s.SameColors),new XElement("LayerAppendDate",s.LayerAppendDate),new XElement("LayerAppendName",s.LayerAppendName),new XElement("DateBeforeName",s.DateBeforeName),new XElement("Connector",s.Connector),new XElement("Plottable",s.Plottable),new XElement("CheckHeight",s.CheckHeight.ToString(CultureInfo.InvariantCulture)),new XElement("AutoTextViewPercent",s.AutoTextViewPercent.ToString(CultureInfo.InvariantCulture)),
                new XElement("ShowDiscipline",s.ShowDiscipline),new XElement("ShowAuthor",s.ShowAuthor),new XElement("ShowRole",s.ShowRole),new XElement("ShowDate",s.ShowDate),new XElement("ShowStatus",s.ShowStatus),new XElement("ShowDrawingNo",s.ShowDrawingNo),
                new XElement("ArchiveOnCreate",s.ArchiveOnCreate),new XElement("ContentSuggest",s.ContentSuggest),
                new XElement("CloudMarkerEnabled",s.CloudMarkerEnabled),new XElement("CloudMarkerText",s.CloudMarkerText??""),new XElement("CloudMarkerAutoIncrement",s.CloudMarkerAutoIncrement),
                new XElement("CloudMarkerTextHeight",s.CloudMarkerTextHeight.ToString(CultureInfo.InvariantCulture)),new XElement("CloudMarkerBoxHeight",s.CloudMarkerBoxHeight.ToString(CultureInfo.InvariantCulture)),new XElement("CloudMarkerBoxWidth",s.CloudMarkerBoxWidth.ToString(CultureInfo.InvariantCulture)),
                new XElement("CloudMarkerShape",CloudMarker.Normalize(s.CloudMarkerShape)),new XElement("CloudMarkerColor",s.CloudMarkerColor),
                new XElement("SeparateColors",s.SeparateColors??""));
        }

        /// <summary>与磁盘内容不同才写（原子替换）。DocumentActivated 等高频路径借此避免无谓写盘。</summary>
        private static void WriteIfChanged(XElement root)
        {
            var text = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" + root.ToString();
            try
            {
                if (File.Exists(PathName) && string.Equals(File.ReadAllText(PathName, System.Text.Encoding.UTF8), text, StringComparison.Ordinal)) return;
            }
            catch (Exception ex) { PluginLog.Warning("Settings.Compare", ex.Message); }
            DataFiles.WriteAllTextAtomic(PathName, text, new System.Text.UTF8Encoding(false));
        }

        /// <summary>不经 AnnotationSettings、由各模块单独读写的节点；<see cref="Save"/> 时原样保留。</summary>
        private static readonly string[] ExtraKeys = { "ToolbarVisible", "PendingSysvarRestore", "PendingDrawAidsRestore" };

        /// <summary>读取单独存放的节点值；没有时返回 null。</summary>
        public static string LoadExtra(string key) => ReadElement(key);

        /// <summary>只更新 settings.xml 中的某个单独节点（value 为 null 时删除该节点），其余设置原样保留。
        /// 文件损坏时不覆盖，避免丢掉其他设置。</summary>
        public static bool SaveExtra(string key, string value)
        {
            try
            {
                using (DataFiles.Lock(false))
                {
                    XElement x = null;
                    if (File.Exists(PathName))
                    {
                        try { x = XElement.Load(PathName); }
                        catch (Exception ex) { PluginLog.Error("Settings.Extra.Load", ex); return false; }
                    }
                    if (x == null) x = new XElement("Settings");
                    x.SetElementValue(key, value);
                    WriteIfChanged(x);
                    return true;
                }
            }
            catch (Exception ex) { PluginLog.Error("Settings.Extra.Save", ex); return false; }
        }

        // ============ 「GM批注」工具栏显隐（单独读写，不经 AnnotationSettings） ============

        private const string ToolbarVisibleKey = "ToolbarVisible";

        /// <summary>上次记录的工具栏显隐；没有记录（首次使用 / 老配置文件）时默认显示。</summary>
        public static bool LoadToolbarVisible()
        {
            return ParseBool(ReadElement(ToolbarVisibleKey) ?? "true", true);
        }

        /// <summary>只更新 settings.xml 中的工具栏显隐节点，其余设置原样保留。</summary>
        public static void SaveToolbarVisible(bool visible)
        {
            SaveExtra(ToolbarVisibleKey, visible ? "true" : "false");
        }

        private static string ReadElement(string name)
        {
            try { return File.Exists(PathName) ? (string)XElement.Load(PathName).Element(name) : null; }
            catch { return null; }
        }

        private static string Get(XElement x, string n, string f) => (string)x.Element(n) ?? f;
        private static double ParseDouble(string v, double f) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : f;
        private static short ParseShort(string v, short f) => short.TryParse(v, out var n) ? n : f;
        private static int ParseInt(string v, int f) => int.TryParse(v, out var n) ? n : f;
        private static bool ParseBool(string v, bool f) => bool.TryParse(v, out var n) ? n : f;
    }
}
