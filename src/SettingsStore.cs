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

        public static AnnotationSettings Load()
        {
            var s = new AnnotationSettings();
            try
            {
                if (!File.Exists(PathName)) return s;
                var x = XElement.Load(PathName);
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
            }
            catch (Exception ex) { PluginLog.Error("Settings.Load",ex); }
            return s;
        }

        public static void Save(AnnotationSettings s)
        {
            Directory.CreateDirectory(Folder);
            // 工具栏显隐不属于 AnnotationSettings（由 ToolbarInstaller 单独读写）：整文件重写时原样保留该节点，
            // 防止面板/设置窗口长时间持有的旧设置对象把用户刚关掉的工具栏状态覆盖回去。
            var toolbarVisible = ReadElement(ToolbarVisibleKey);
            var root = new XElement("Settings",
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
                new XElement("ArchiveOnCreate",s.ArchiveOnCreate),new XElement("ContentSuggest",s.ContentSuggest));
            if (toolbarVisible != null) root.Add(new XElement(ToolbarVisibleKey, toolbarVisible));
            root.Save(PathName);
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
            try
            {
                Directory.CreateDirectory(Folder);
                XElement x = null;
                if (File.Exists(PathName))
                {
                    try { x = XElement.Load(PathName); }
                    catch (Exception ex) { PluginLog.Error("Settings.Toolbar.Load", ex); return; } // 文件损坏时不覆盖，避免丢掉其他设置
                }
                if (x == null) x = new XElement("Settings");
                x.SetElementValue(ToolbarVisibleKey, visible);
                x.Save(PathName);
            }
            catch (Exception ex) { PluginLog.Error("Settings.Toolbar.Save", ex); }
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
