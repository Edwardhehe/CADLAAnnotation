namespace GMAnnotation
{
    /// <summary>
    /// 下拉选项的集中定义：批注面板、批注窗口、设置窗口共用同一份列表，
    /// 避免"新增一个专业要改多个窗口、漏改导致两处不一致"。
    /// 状态配色（已完成=绿、已回复=黄）属界面表现，定义在 Views/AnnotationListPanel.xaml。
    /// </summary>
    internal static class AnnotationOptions
    {
        /// <summary>专业。</summary>
        public static readonly string[] Disciplines =
        {
            "建筑", "室内", "结构", "给排水", "暖通", "电气",
            "道路", "桥梁", "隧道", "交通", "管线", "绿化", "景观", "岩土", "其他"
        };

        /// <summary>角色。</summary>
        public static readonly string[] Roles = { "批注人", "校审人", "回复人" };

        /// <summary>状态。完成/回复两种状态在批注列表中的文字配色见 Views/AnnotationListPanel.xaml。</summary>
        public static readonly string[] Statuses = { "待处理", "已回复", "已完成" };

        /// <summary>比例选型（出图比例 1:N）常用值。设置窗口的"比例选型"下拉用它；下拉可编辑，允许手填其它比例。
        /// 选中后字高/云线半径按"打印尺寸(mm) × N"换算为图面单位，见 AnnotationService.ResolveEffectiveSettings。</summary>
        public static readonly string[] PlotScales =
        {
            "1:1", "1:2", "1:5", "1:10", "1:20", "1:25", "1:30",
            "1:50", "1:75", "1:100", "1:150", "1:200", "1:300", "1:500", "1:1000"
        };
    }
}
