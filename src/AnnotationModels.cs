using System;

namespace GMAnnotation
{
    /// <summary>单条批注的业务数据，序列化后存储在 DWG 编组 XRecord 中。</summary>
    internal sealed class AnnotationData
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Number { get; set; } = "GM-001";
        public string Date { get; set; } = DateTime.Today.ToString("yyyy-MM-dd");
        public string Discipline { get; set; } = "建筑";
        public string Author { get; set; } = Environment.UserName;
        public string Role { get; set; } = "批注人";
        public string Status { get; set; } = "待处理";
        public string Content { get; set; } = "";
        public string DrawingNo { get; set; } = ""; // 图号：入库留痕与知识库检索用
        public double RenderTextHeight { get; set; }
        public double RenderHeaderHeight { get; set; }
        public double RenderSecondLineHeight { get; set; }
        public double RenderCloudRadius { get; set; }
        public double RenderLineWidth { get; set; }

        // ===== 仅 CSV 导入用（不进 XRecord、不参与图内数据）：导出时记下的<b>原图坐标</b>（WCS）。
        // 导入到其他 DWG 时按这些坐标还原批注位置，而不是排布到用户指定的基点网格上。
        // 旧 CSV 没有这些列 → Placed 为 false，退回"指定基点 + 网格排布"的老行为。
        public bool Placed { get; set; }                 // 本行是否带完整的云线范围坐标
        public string PlacedLayout { get; set; }         // 原批注所在布局（"模型" 或 布局名）
        public double PlacedX1 { get; set; }             // 云线范围左下角 X
        public double PlacedY1 { get; set; }
        public double PlacedX2 { get; set; }             // 云线范围右上角 X
        public double PlacedY2 { get; set; }
        public bool PlacedText { get; set; }             // 是否带文字框锚点坐标
        public double PlacedTextX { get; set; }
        public double PlacedTextY { get; set; }
        public double PlacedZ { get; set; }              // 标高（云线/文字所在平面）
    }

    /// <summary>批注全局设置，持久化到本机 XML 文件，包含外形、文字、图层、颜色等全部可配置项。</summary>
    internal sealed class AnnotationSettings
    {
        public string Shape { get; set; } = "矩形";
        public string CloudStyle { get; set; } = "等宽";
        public string LayerName { get; set; } = "GM-批注";
        public string TextStyleName { get; set; } = "Standard";
        public short ColorIndex { get; set; } = 6;
        public short CloudColor { get; set; } = 6;
        public short LeaderColor { get; set; } = 6;
        public short TextColor { get; set; } = 6;
        public short BoxColor { get; set; } = 6;
        public short ReplyColor { get; set; } = 6;
        public short ScreenshotBackgroundColor { get; set; } = 7;
        public short PassColor { get; set; } = 6;
        public short CheckColor { get; set; } = 6;
        public bool ScreenshotBackgroundOnceReply { get; set; } = false;
        // 文字与云线尺寸：未启用自适应时按"打印尺寸(mm)"理解，图上实测值 = 打印值 × 比例分母（见 AnnotationService.ResolveEffectiveSettings）
        public double TextHeight { get; set; } = 3.0;
        public double HeaderHeight { get; set; } = 3.0;
        public double SecondLineHeight { get; set; } = 3.0;
        public double CloudRadius { get; set; } = 2.0;
        public double LineWidth { get; set; } = 0.2;
        /// <summary>比例选型：出图比例 1:N 的分母 N（设置窗口里从下拉选，也可手填 "1:150"）。
        /// 未启用自适应时，字高/云线半径按"打印尺寸(mm) × N"换算为图面单位；启用自适应时该值不参与字高换算。
        /// 字段名沿用旧版 XML 节点 &lt;ScaleRatio&gt;，老配置文件可直接升级。</summary>
        public double ScaleRatio { get; set; } = 1.0;
        public bool CloudAutoFit { get; set; } = true;
        public bool FontAutoFit { get; set; } = true;
        public bool FixedWidth { get; set; } = false;
        public double FixedWidthValue { get; set; } = 55.0;
        public string DefaultAuthor { get; set; } = Environment.UserName;
        public string DefaultDiscipline { get; set; } = "岩土";
        public string DefaultRole { get; set; } = "批注人";
        public bool AutoNumber { get; set; } = true;
        public int NextNumber { get; set; } = 1;
        public bool AutoCloseOrtho { get; set; } = true;
        public bool AutoCloseSnap { get; set; } = true;
        public bool ViewTopIsNorth { get; set; } = true;
        public bool DoubleClickEdit { get; set; } = true;
        public bool ContinuousAnnotation { get; set; } = true;
        public bool CloudOnly { get; set; } = false;
        public bool SameColors { get; set; } = true;
        public bool LayerAppendDate { get; set; } = true;
        public bool LayerAppendName { get; set; } = false;
        public bool DateBeforeName { get; set; } = true;
        public string Connector { get; set; } = "-";
        public bool Plottable { get; set; } = false;
        public double CheckHeight { get; set; } = 8.0;
        public double AutoTextViewPercent { get; set; } = 5.0; // 字高 ≈ 云线对角线 × 此百分比
        // 批注框内显示内容（批注内容必选，始终显示；其余可选，默认全勾选）
        // 编号不在此列：编号仅用于批注列表与导出/导入匹配，不显示在图内。
        public bool ShowDiscipline { get; set; } = true;
        public bool ShowAuthor { get; set; } = true;
        public bool ShowRole { get; set; } = true;
        public bool ShowDate { get; set; } = true;
        public bool ShowStatus { get; set; } = true;
        public bool ShowDrawingNo { get; set; } = true;
        public bool ArchiveOnCreate { get; set; } = true; // 立即入库：创建/修改批注时写入知识库（KnowledgeStore / knowledge.json）
        /// <summary>输入建议：批注内容输入框是否实时给出补全/续写候选（来源：批注历史记录 / 常用批注语 / 知识库条目）。</summary>
        public bool ContentSuggest { get; set; } = true;
        // ---- 单绘云线角标（云线右下角内侧的"框 + 文字"）。尺寸为打印毫米，落图时按批注字高同一倍数换算 ----
        public bool CloudMarkerEnabled { get; set; } = false;
        public string CloudMarkerText { get; set; } = "A";
        /// <summary>每画一个角标后文字自动递增（A→B→…→Z→AA，1→2，A9→A10）。</summary>
        public bool CloudMarkerAutoIncrement { get; set; } = false;
        public double CloudMarkerTextHeight { get; set; } = 2.5;
        /// <summary>框高（建议字高 2 倍）。</summary>
        public double CloudMarkerBoxHeight { get; set; } = 5.0;
        /// <summary>框宽，仅长形（椭圆/长矩形/长六边形/长八边形/平行四边形）有效，建议字高 5~10 倍。</summary>
        public double CloudMarkerBoxWidth { get; set; } = 10.0;
        /// <summary>形状：圆/矩形/六边形/八边形/菱形，或长形 椭圆/长矩形/长六边形/长八边形/平行四边形。</summary>
        public string CloudMarkerShape { get; set; } = "六边形";
        /// <summary>角标颜色：1~255 为 ACI 颜色；其他值（默认 -1）表示与云线同色。图层始终跟随云线。</summary>
        public short CloudMarkerColor { get; set; } = -1;

        public AnnotationSettings Clone() => (AnnotationSettings)MemberwiseClone();
    }
}
