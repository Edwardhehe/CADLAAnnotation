using System;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace LAAnnotation
{
    /// <summary>设置持久化：将 AnnotationSettings 读写到 %AppData%/LAAnnotation/settings.xml。</summary>
    internal static class SettingsStore
    {
        private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LAAnnotation");
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
                s.TextStyleName = Get(x, "TextStyleName", s.TextStyleName);
                s.ColorIndex = ParseShort(Get(x, "ColorIndex", s.ColorIndex.ToString()), s.ColorIndex);
                s.CloudColor = ParseShort(Get(x,"CloudColor","6"),6); s.LeaderColor=ParseShort(Get(x,"LeaderColor","6"),6); s.TextColor=ParseShort(Get(x,"TextColor","1"),1);s.BoxColor=ParseShort(Get(x,"BoxColor","5"),5); s.ReplyColor=ParseShort(Get(x,"ReplyColor","6"),6);s.ScreenshotBackgroundColor=ParseShort(Get(x,"ScreenshotBackgroundColor","7"),7);s.PassColor=ParseShort(Get(x,"PassColor","3"),3);s.CheckColor=ParseShort(Get(x,"CheckColor","3"),3);s.ScreenshotBackgroundOnceReply=ParseBool(Get(x,"ScreenshotBackgroundOnceReply","false"),false);
                s.TextHeight = ParseDouble(Get(x, "TextHeight", "3"), s.TextHeight);
                s.HeaderHeight=ParseDouble(Get(x,"HeaderHeight","3"),3);s.SecondLineHeight=ParseDouble(Get(x,"SecondLineHeight","3"),3);
                s.CloudRadius = ParseDouble(Get(x, "CloudRadius", "2"), s.CloudRadius);
                s.LineWidth=ParseDouble(Get(x,"LineWidth","0.2"),0.2);s.ScaleRatio=ParseDouble(Get(x,"ScaleRatio","1"),1);s.CloudAutoFit=ParseBool(Get(x,"CloudAutoFit","true"),true);s.FontAutoFit=ParseBool(Get(x,"FontAutoFit","true"),true);s.FixedWidth=ParseBool(Get(x,"FixedWidth","false"),false);s.FixedWidthValue=ParseDouble(Get(x,"FixedWidthValue","55"),55);
                s.DefaultAuthor = Get(x, "DefaultAuthor", s.DefaultAuthor);
                s.DefaultDiscipline = Get(x, "DefaultDiscipline", s.DefaultDiscipline);
                s.DefaultRole=Get(x,"DefaultRole",s.DefaultRole);
                s.AutoNumber = ParseBool(Get(x, "AutoNumber", "true"), true);
                s.NextNumber = ParseInt(Get(x, "NextNumber", "1"), 1);
                s.AutoCloseOrtho=ParseBool(Get(x,"AutoCloseOrtho","true"),true);s.AutoCloseSnap=ParseBool(Get(x,"AutoCloseSnap","true"),true);s.ViewTopIsNorth=ParseBool(Get(x,"ViewTopIsNorth","true"),true);s.DoubleClickEdit=ParseBool(Get(x,"DoubleClickEdit","true"),true);s.ContinuousAnnotation=ParseBool(Get(x,"ContinuousAnnotation","false"),false);s.CloudOnly=ParseBool(Get(x,"CloudOnly","false"),false);s.SameColors=ParseBool(Get(x,"SameColors","true"),true);s.LayerAppendDate=ParseBool(Get(x,"LayerAppendDate","true"),true);s.LayerAppendName=ParseBool(Get(x,"LayerAppendName","false"),false);s.DateBeforeName=ParseBool(Get(x,"DateBeforeName","true"),true);s.Connector=Get(x,"Connector","-");s.Plottable=ParseBool(Get(x,"Plottable","false"),false);s.CheckHeight=ParseDouble(Get(x,"CheckHeight","8"),8);
                s.AutoTextViewPercent=ParseDouble(Get(x,"AutoTextViewPercent","4"),4);
            }
            catch (Exception ex) { PluginLog.Error("Settings.Load",ex); }
            return s;
        }

        public static void Save(AnnotationSettings s)
        {
            Directory.CreateDirectory(Folder);
            new XElement("Settings",
                new XElement("Shape",s.Shape),new XElement("CloudStyle",s.CloudStyle),
                new XElement("LayerName", s.LayerName), new XElement("ColorIndex", s.ColorIndex),
                new XElement("TextStyleName",s.TextStyleName),new XElement("CloudColor",s.CloudColor),new XElement("LeaderColor",s.LeaderColor),new XElement("TextColor",s.TextColor),new XElement("BoxColor",s.BoxColor),new XElement("ReplyColor",s.ReplyColor),new XElement("ScreenshotBackgroundColor",s.ScreenshotBackgroundColor),new XElement("PassColor",s.PassColor),new XElement("CheckColor",s.CheckColor),new XElement("ScreenshotBackgroundOnceReply",s.ScreenshotBackgroundOnceReply),
                new XElement("TextHeight", s.TextHeight.ToString(CultureInfo.InvariantCulture)),
                new XElement("HeaderHeight",s.HeaderHeight.ToString(CultureInfo.InvariantCulture)),new XElement("SecondLineHeight",s.SecondLineHeight.ToString(CultureInfo.InvariantCulture)),
                new XElement("CloudRadius", s.CloudRadius.ToString(CultureInfo.InvariantCulture)),
                new XElement("LineWidth",s.LineWidth.ToString(CultureInfo.InvariantCulture)),new XElement("ScaleRatio",s.ScaleRatio.ToString(CultureInfo.InvariantCulture)),new XElement("CloudAutoFit",s.CloudAutoFit),new XElement("FontAutoFit",s.FontAutoFit),new XElement("FixedWidth",s.FixedWidth),new XElement("FixedWidthValue",s.FixedWidthValue.ToString(CultureInfo.InvariantCulture)),
                new XElement("DefaultAuthor", s.DefaultAuthor), new XElement("DefaultDiscipline", s.DefaultDiscipline),
                new XElement("DefaultRole",s.DefaultRole),new XElement("AutoNumber", s.AutoNumber), new XElement("NextNumber", s.NextNumber),
                new XElement("AutoCloseOrtho",s.AutoCloseOrtho),new XElement("AutoCloseSnap",s.AutoCloseSnap),new XElement("ViewTopIsNorth",s.ViewTopIsNorth),new XElement("DoubleClickEdit",s.DoubleClickEdit),new XElement("ContinuousAnnotation",s.ContinuousAnnotation),new XElement("CloudOnly",s.CloudOnly),new XElement("SameColors",s.SameColors),new XElement("LayerAppendDate",s.LayerAppendDate),new XElement("LayerAppendName",s.LayerAppendName),new XElement("DateBeforeName",s.DateBeforeName),new XElement("Connector",s.Connector),new XElement("Plottable",s.Plottable),new XElement("CheckHeight",s.CheckHeight.ToString(CultureInfo.InvariantCulture)),new XElement("AutoTextViewPercent",s.AutoTextViewPercent.ToString(CultureInfo.InvariantCulture))).Save(PathName);
        }

        private static string Get(XElement x, string n, string f) => (string)x.Element(n) ?? f;
        private static double ParseDouble(string v, double f) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : f;
        private static short ParseShort(string v, short f) => short.TryParse(v, out var n) ? n : f;
        private static int ParseInt(string v, int f) => int.TryParse(v, out var n) ? n : f;
        private static bool ParseBool(string v, bool f) => bool.TryParse(v, out var n) ? n : f;
    }
}
