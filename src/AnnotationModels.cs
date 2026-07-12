using System;

namespace LAAnnotation
{
    /// <summary>单条批注的业务数据，序列化后存储在 DWG 编组 XRecord 中。</summary>
    internal sealed class AnnotationData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Number { get; set; } = "LA-001";
        public string Date { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");
        public string Discipline { get; set; } = "建筑";
        public string Author { get; set; } = Environment.UserName;
        public string Role { get; set; } = "批注人";
        public string Status { get; set; } = "待处理";
        public string Content { get; set; } = "";
        public double RenderTextHeight { get; set; }
        public double RenderHeaderHeight { get; set; }
        public double RenderSecondLineHeight { get; set; }
        public double RenderCloudRadius { get; set; }
        public double RenderLineWidth { get; set; }
    }

    /// <summary>批注全局设置，持久化到本机 XML 文件，包含外形、文字、图层、颜色等全部可配置项。</summary>
    internal sealed class AnnotationSettings
    {
        public string Shape { get; set; } = "矩形";
        public string CloudStyle { get; set; } = "渐变";
        public string LayerName { get; set; } = "LA-批注";
        public string TextStyleName { get; set; } = "Standard";
        public short ColorIndex { get; set; } = 6;
        public short CloudColor { get; set; } = 6;
        public short LeaderColor { get; set; } = 6;
        public short TextColor { get; set; } = 1;
        public short BoxColor { get; set; } = 5;
        public short ReplyColor { get; set; } = 6;
        public short ScreenshotBackgroundColor { get; set; } = 7;
        public short PassColor { get; set; } = 3;
        public short CheckColor { get; set; } = 3;
        public bool ScreenshotBackgroundOnceReply { get; set; } = false;
        public double TextHeight { get; set; } = 3.0;
        public double HeaderHeight { get; set; } = 3.0;
        public double SecondLineHeight { get; set; } = 3.0;
        public double CloudRadius { get; set; } = 2.0;
        public double LineWidth { get; set; } = 0.2;
        public double ScaleRatio { get; set; } = 1.0;
        public bool CloudAutoFit { get; set; } = true;
        public bool FontAutoFit { get; set; } = true;
        public bool FixedWidth { get; set; } = false;
        public double FixedWidthValue { get; set; } = 55.0;
        public string DefaultAuthor { get; set; } = Environment.UserName;
        public string DefaultDiscipline { get; set; } = "建筑";
        public string DefaultRole { get; set; } = "批注人";
        public bool AutoNumber { get; set; } = true;
        public int NextNumber { get; set; } = 1;
        public bool AutoCloseOrtho { get; set; } = true;
        public bool AutoCloseSnap { get; set; } = true;
        public bool ViewTopIsNorth { get; set; } = true;
        public bool DoubleClickEdit { get; set; } = true;
        public bool ContinuousAnnotation { get; set; } = false;
        public bool CloudOnly { get; set; } = false;
        public bool SameColors { get; set; } = true;
        public bool LayerAppendDate { get; set; } = true;
        public bool LayerAppendName { get; set; } = false;
        public bool DateBeforeName { get; set; } = true;
        public string Connector { get; set; } = "-";
        public bool Plottable { get; set; } = false;
        public double CheckHeight { get; set; } = 8.0;
        public double AutoTextViewPercent { get; set; } = 4.0; // 字高 ≈ 云线对角线 × 此百分比

        public AnnotationSettings Clone() => (AnnotationSettings)MemberwiseClone();
    }
}
