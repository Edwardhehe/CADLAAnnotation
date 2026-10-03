using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace GMAnnotation.Views
{
    internal partial class SettingsWindow : Window
    {
        private readonly AnnotationSettings _s;
        /// <summary>程序性赋值比例（初始化）期间为 true，避免触发"选比例自动关自适应"。</summary>
        private bool _scaleChanging;
        public AnnotationSettings Value => _s;

        /// <summary>构造设置窗口。textStyles 为当前 DWG 中可用的文字样式名列表。</summary>
        public SettingsWindow(AnnotationSettings value, IEnumerable<string> textStyles = null)
        {
            InitializeComponent();
            _s = value;
            ShapeCombo.ItemsSource = new[] { "矩形", "菱形", "椭圆" };
            CloudStyleCombo.ItemsSource = new[] { "等宽", "渐变" };
            DefaultRoleCombo.ItemsSource = AnnotationOptions.Roles;
            ConnectorCombo.ItemsSource = new[] { "-", "_", "·", "无" };
            ScaleRatioCombo.ItemsSource = AnnotationOptions.PlotScales;
            // 所有颜色下拉菜单共享 ACI 255 色列表
            foreach (var combo in new[] { ColorIndexCombo, CloudColorCombo, LeaderColorCombo, TextColorCombo, BoxColorCombo, ReplyColorCombo, ScreenshotBackgroundColorCombo, PassColorCombo, CheckColorCombo })
                combo.ItemsSource = AciColors.All;
            // 文字样式下拉菜单：合并预设 + DWG 中已有样式
            var styles = new List<string> { "Standard" };
            if (textStyles != null) styles.AddRange(textStyles.Where(s => !string.Equals(s, "Standard", StringComparison.OrdinalIgnoreCase)));
            TextStyleCombo.ItemsSource = styles;
            LoadValues();
            if (SettingsStore.IsCorrupt)
                Title += "（settings.xml 损坏，当前显示默认值；点「保存设置」才会覆盖原文件" +
                         (string.IsNullOrEmpty(SettingsStore.CorruptBackupPath) ? "" : "，原文件已备份") + "）";
            Loaded += (s, e) => { WindowSizing.FitToWorkArea(this, 0.9, 0.86); RefreshDependencies(); };
        }

        private void LoadValues()
        {
            ShapeCombo.Text = _s.Shape; CloudStyleCombo.Text = _s.CloudStyle;
            Set(CloudRadiusText, _s.CloudRadius); Set(LineWidthText, _s.LineWidth);
            CloudAutoFitCheck.IsChecked = _s.CloudAutoFit; FontAutoFitCheck.IsChecked = _s.FontAutoFit;
            Set(AutoTextViewPercentText, _s.AutoTextViewPercent);
            // 旧配置里存的是"全局比例"数字；这里统一按"1:N 出图比例"显示。
            _scaleChanging = true;
            ScaleRatioCombo.Text = FormatScale(_s.ScaleRatio);
            _scaleChanging = false;
            AutoCloseOrthoCheck.IsChecked = _s.AutoCloseOrtho; AutoCloseSnapCheck.IsChecked = _s.AutoCloseSnap;
            DoubleClickEditCheck.IsChecked = _s.DoubleClickEdit; ViewTopIsNorthCheck.IsChecked = _s.ViewTopIsNorth;
            Set(HeaderHeightText, _s.HeaderHeight); Set(SecondLineHeightText, _s.SecondLineHeight); Set(TextHeightText, _s.TextHeight);
            DefaultAuthorText.Text = _s.DefaultAuthor; DefaultRoleCombo.Text = _s.DefaultRole; DefaultDisciplineText.Text = _s.DefaultDiscipline;
            FixedWidthCheck.IsChecked = _s.FixedWidth; Set(FixedWidthValueText, _s.FixedWidthValue);
            AutoNumberCheck.IsChecked = _s.AutoNumber;
            LayerNameText.Text = _s.LayerName; TextStyleCombo.Text = _s.TextStyleName;
            SameColorsCheck.IsChecked = _s.SameColors;
            SelectColor(ColorIndexCombo, _s.ColorIndex); SelectColor(CloudColorCombo, _s.CloudColor);
            SelectColor(LeaderColorCombo, _s.LeaderColor); SelectColor(TextColorCombo, _s.TextColor);
            SelectColor(BoxColorCombo, _s.BoxColor); SelectColor(ReplyColorCombo, _s.ReplyColor);
            ScreenshotBackgroundOnceReplyCheck.IsChecked = _s.ScreenshotBackgroundOnceReply;
            SelectColor(ScreenshotBackgroundColorCombo, _s.ScreenshotBackgroundColor);
            SelectColor(PassColorCombo, _s.PassColor); SelectColor(CheckColorCombo, _s.CheckColor);
            Set(CheckHeightText, _s.CheckHeight);
            LayerAppendDateCheck.IsChecked = _s.LayerAppendDate; LayerAppendNameCheck.IsChecked = _s.LayerAppendName;
            DateBeforeNameCheck.IsChecked = _s.DateBeforeName; ConnectorCombo.Text = _s.Connector;
            PlottableCheck.IsChecked = _s.Plottable;
            ShowDisciplineCheck.IsChecked = _s.ShowDiscipline;
            ShowAuthorCheck.IsChecked = _s.ShowAuthor; ShowRoleCheck.IsChecked = _s.ShowRole;
            ShowDateCheck.IsChecked = _s.ShowDate; ShowStatusCheck.IsChecked = _s.ShowStatus;
            ShowDrawingNoCheck.IsChecked = _s.ShowDrawingNo;
            CloudMarkerEnabledCheck.IsChecked = _s.CloudMarkerEnabled;
            UpdateCloudMarkerSummary();
        }

        private void UpdateCloudMarkerSummary()
        {
            if (CloudMarkerSummaryText == null) return;
            var preview = _s.Clone();
            preview.CloudMarkerEnabled = CloudMarkerEnabledCheck.IsChecked == true;
            CloudMarkerSummaryText.Text = "当前：" + CloudMarker.Describe(preview);
        }

        private void CloudMarkerEnabled_Changed(object sender, RoutedEventArgs e) => UpdateCloudMarkerSummary();

        /// <summary>打开云线标记设置；改动先写入本窗口的设置副本，点「保存设置」时随其他设置一起持久化。</summary>
        private void CloudMarkerSettings_Click(object sender, RoutedEventArgs e)
        {
            _s.CloudMarkerEnabled = CloudMarkerEnabledCheck.IsChecked == true;
            var dialog = new CloudMarkerWindow(_s) { Owner = this };
            if (dialog.ShowDialog() == true) CloudMarkerEnabledCheck.IsChecked = _s.CloudMarkerEnabled;
            UpdateCloudMarkerSummary();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _s.Shape = ShapeCombo.Text.Trim(); _s.CloudStyle = CloudStyleCombo.Text.Trim();
                _s.CloudRadius = N(CloudRadiusText, "云线半径", 0.001); _s.LineWidth = N(LineWidthText, "云线线宽", 0);
                _s.CloudAutoFit = On(CloudAutoFitCheck); _s.FontAutoFit = On(FontAutoFitCheck);
                _s.AutoTextViewPercent = N(AutoTextViewPercentText, "自适应百分比", 0.1, 10);
                _s.ScaleRatio = ParseScale(ScaleRatioCombo.Text);
                // 比例与自适应是两套算法：选了比例却还勾着自适应时，字高仍按云线尺寸算，比例看不出效果。
                // 这里问一句再决定，避免用户以为"比例没生效"。
                if (_s.ScaleRatio > 1.0 + 1e-9 && (_s.FontAutoFit || _s.CloudAutoFit))
                {
                    var autoNames = _s.FontAutoFit && _s.CloudAutoFit ? "字体/云线自适应" : _s.FontAutoFit ? "字体自适应" : "云线自适应";
                    var answer = MessageBox.Show(this,
                        "已选比例 1:" + FormatDenominator(_s.ScaleRatio) + "，但仍勾选了" + autoNames + "。\n\n" +
                        "自适应按云线尺寸计算，比例不参与，图上尺寸不会按比例放大。\n" +
                        "是否改为按比例换算（取消自适应勾选）？",
                        "GM批注", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (answer == MessageBoxResult.Yes) { _s.FontAutoFit = false; _s.CloudAutoFit = false; }
                }
                _s.CloudMarkerEnabled = On(CloudMarkerEnabledCheck);
                _s.AutoCloseOrtho = On(AutoCloseOrthoCheck); _s.AutoCloseSnap = On(AutoCloseSnapCheck);
                _s.DoubleClickEdit = On(DoubleClickEditCheck); _s.ViewTopIsNorth = On(ViewTopIsNorthCheck);
                _s.HeaderHeight = N(HeaderHeightText, "首行字高", 0.01); _s.SecondLineHeight = N(SecondLineHeightText, "次行字高", 0.01);
                _s.TextHeight = N(TextHeightText, "正文字高", 0.01);
                _s.DefaultAuthor = DefaultAuthorText.Text.Trim(); _s.DefaultRole = DefaultRoleCombo.Text.Trim();
                _s.DefaultDiscipline = DefaultDisciplineText.Text.Trim();
                _s.FixedWidth = On(FixedWidthCheck); _s.FixedWidthValue = N(FixedWidthValueText, "固定宽度", 1);
                _s.AutoNumber = On(AutoNumberCheck);
                _s.LayerName = LayerNameText.Text.Trim();
                if (_s.LayerName.Length == 0) throw new InvalidOperationException("图层名称不能为空。");
                CheckLayerText(_s.LayerName, "图层基础名称", LayerNameText);
                _s.TextStyleName = TextStyleCombo.Text.Trim();
                _s.SameColors = On(SameColorsCheck);
                _s.ColorIndex = SelectedColor(ColorIndexCombo); _s.CloudColor = SelectedColor(CloudColorCombo);
                _s.LeaderColor = SelectedColor(LeaderColorCombo); _s.TextColor = SelectedColor(TextColorCombo);
                _s.BoxColor = SelectedColor(BoxColorCombo); _s.ReplyColor = SelectedColor(ReplyColorCombo);
                _s.ScreenshotBackgroundOnceReply = On(ScreenshotBackgroundOnceReplyCheck);
                _s.ScreenshotBackgroundColor = SelectedColor(ScreenshotBackgroundColorCombo);
                _s.PassColor = SelectedColor(PassColorCombo); _s.CheckColor = SelectedColor(CheckColorCombo);
                _s.CheckHeight = N(CheckHeightText, "对勾高度", 0.01);
                _s.LayerAppendDate = On(LayerAppendDateCheck); _s.LayerAppendName = On(LayerAppendNameCheck);
                _s.DateBeforeName = On(DateBeforeNameCheck); _s.Connector = ConnectorCombo.Text.Trim();
                if (_s.Connector != "无") CheckLayerText(_s.Connector, "连接符", ConnectorCombo);
                if (_s.LayerAppendName) CheckLayerText(_s.DefaultAuthor, "默认批注人（已勾选「图层名添加批注人姓名」）", DefaultAuthorText);
                _s.Plottable = On(PlottableCheck);
                _s.ShowDiscipline = On(ShowDisciplineCheck);
                _s.ShowAuthor = On(ShowAuthorCheck); _s.ShowRole = On(ShowRoleCheck);
                _s.ShowDate = On(ShowDateCheck); _s.ShowStatus = On(ShowStatusCheck);
                _s.ShowDrawingNo = On(ShowDrawingNoCheck);
                // 统一颜色模式：所有独立颜色同步为统一颜色
                if (_s.SameColors) _s.CloudColor = _s.LeaderColor = _s.TextColor = _s.BoxColor = _s.ReplyColor = _s.PassColor = _s.CheckColor = _s.ColorIndex;
                SettingsStore.Save(_s, true); DialogResult = true;
            }
            catch (Exception ex) { MessageBox.Show(this, "设置保存失败：" + ex.Message, "GM批注", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }
        private void Dependency_Changed(object sender, RoutedEventArgs e) { RefreshDependencies(); }

        /// <summary>用户从下拉里选比例：按需求"选比例就自动关自适应"——
        /// 自适应按云线尺寸算、比例不参与，两者同时开着会让"选了比例却没变化"。
        /// 1:1 不干预（等于原样，留着自适应更合理）。</summary>
        private void ScaleRatio_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_scaleChanging || !IsInitialized) return;
            var n = TryParseScale(ScaleRatioCombo.Text);
            if (n.HasValue && n.Value > 1.0 + 1e-9 && (On(FontAutoFitCheck) || On(CloudAutoFitCheck)))
            {
                FontAutoFitCheck.IsChecked = false;
                CloudAutoFitCheck.IsChecked = false;
            }
            RefreshDependencies();
        }

        /// <summary>把当前比例与打印尺寸当场换算成图面尺寸显示，避免"选了比例不知道算成多大"。</summary>
        private void RefreshScaleHint()
        {
            if (!IsInitialized) return;
            try
            {
                var n = TryParseScale(ScaleRatioCombo.Text) ?? 1.0;
                var textMm = TryNumber(TextHeightText, 3.0);
                var radiusMm = TryNumber(CloudRadiusText, 2.0);
                var denominator = FormatDenominator(n);
                var hint = "比例 1:" + denominator +
                           " → 图面字高 = " + Num(textMm) + "mm × " + denominator + " = " + Num(textMm * n) +
                           "；图面云线半径 = " + Num(radiusMm) + "mm × " + denominator + " = " + Num(radiusMm * n) + "。";
                if (On(FontAutoFitCheck))
                    hint += " 当前勾选了字体自适应：字高按云线尺寸计算、比例不参与字高换算。";
                ScaleHintText.Text = hint;
            }
            catch (Exception ex)
            {
                ScaleHintText.Text = "";
                PluginLog.Warning("Settings.ScaleHint", ex.Message);
            }
        }

        private void RefreshDependencies()
        {
            if (!IsInitialized) return;
            FixedWidthValueText.IsEnabled = On(FixedWidthCheck);
            var cloudAuto = On(CloudAutoFitCheck);
            CloudRadiusText.IsEnabled = !cloudAuto;
            LineWidthText.IsEnabled = !cloudAuto;
            var fontAuto = On(FontAutoFitCheck);
            // 界面状态与实际计算一致：百分比同时决定"自适应字高"和"云线自适应的半径/线宽"，任一自适应开启都参与计算；
            // 三个字高在字体自适应时仍是相对比例（首行/次行相对正文、云线标记尺寸的换算基准），因此始终可编辑。
            AutoTextViewPercentText.IsEnabled = fontAuto || cloudAuto;
            HeaderHeightText.IsEnabled = true;
            SecondLineHeightText.IsEnabled = true;
            TextHeightText.IsEnabled = true;
            if (TextSizeHintText != null)
                TextSizeHintText.Text = fontAuto
                    ? "已启用字体自适应：正文字高 = 云线对角线 × 百分比（比例不参与）；这里的三个字高只作为相对比例——首行/次行相对正文放大缩小，云线标记尺寸也按「标记字高 ÷ 正文字高」跟随。"
                    : "未启用字体自适应：字高按「打印字高(mm) × 比例分母」换算为图面尺寸（如 3mm、1:100 → 图上 300；布局图纸空间中按 1:1）。";
            var same = On(SameColorsCheck);
            ColorIndexCombo.IsEnabled = same;
            foreach (var x in new[] { CloudColorCombo, LeaderColorCombo, TextColorCombo, BoxColorCombo, ReplyColorCombo, PassColorCombo, CheckColorCombo })
                x.IsEnabled = !same;
            RefreshScaleHint();
        }

        // ---- 比例（1:N 分母）解析与格式化 ----
        /// <summary>把比例分母写成 "1:N"；旧配置里存的就是 1，显示为 1:1。</summary>
        private static string FormatScale(double denominator) => "1:" + FormatDenominator(denominator);

        private static string FormatDenominator(double value)
        {
            var n = value > 0 ? value : 1.0;
            return Math.Abs(n - Math.Round(n)) < 1e-9
                ? ((long)Math.Round(n)).ToString(CultureInfo.InvariantCulture)
                : n.ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>兼容 "1:100" 与 "100" 两种写法，解析出比例分母；解析不了返回 null。</summary>
        private static double? TryParseScale(string text)
        {
            var raw = (text ?? "").Trim();
            var colon = raw.IndexOf(':');
            if (colon >= 0) raw = raw.Substring(colon + 1).Trim();
            if (raw.Length == 0) return null;
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && !double.TryParse(raw, out n)) return null;
            return n > 0 ? n : (double?)null;
        }

        private static double ParseScale(string text)
        {
            var n = TryParseScale(text);
            if (!n.HasValue || n.Value < 0.01) throw new InvalidOperationException("比例必须是形如 1:100 的出图比例。");
            return n.Value;
        }

        private static double TryNumber(TextBox box, double fallback)
            => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || double.TryParse(box.Text, out n) ? n : fallback;

        private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>图层名组成部分（基础名/连接符/批注人）不得含 CAD 图层名非法字符，保存时提示并定位到该输入框。</summary>
        private static void CheckLayerText(string text, string name, Control focus)
        {
            var i = AnnotationService.FindInvalidLayerChar(text);
            if (i < 0) return;
            focus?.Focus();
            throw new InvalidOperationException(name + "含有 CAD 图层名不允许的字符「" + text[i] + "」。\n不允许的字符：< > / \\ \" : ; ? * | , = `");
        }

        // ---- 辅助方法 ----
        private static bool On(CheckBox x) => x.IsChecked == true;
        private static void Set(TextBox x, double v) => x.Text = v.ToString("0.###", CultureInfo.InvariantCulture);
        private static double N(TextBox x, string name, double min, double max = double.MaxValue)
        {
            if (!double.TryParse(x.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && !double.TryParse(x.Text, out n))
                throw new InvalidOperationException(name + "必须是数字。");
            if (n < min || n > max) throw new InvalidOperationException(name + "超出允许范围。");
            return n;
        }
        /// <summary>在颜色 ComboBox 中按 ACI <b>颜色号</b>选中对应项；原值记在 Tag 里，
        /// 不在 1~255 的旧值（如 0/256）不选中任何项，保存时原样写回，不会被改成 7。</summary>
        private static void SelectColor(ComboBox combo, short index) { combo.Tag = index; combo.SelectedItem = AciColors.Find(index); }
        /// <summary>获取颜色 ComboBox 当前选中项的 ACI 颜色号；未选中时返回加载时的原值（没有原值才用 7）。</summary>
        private static short SelectedColor(ComboBox combo) => (combo.SelectedItem as AciColorItem)?.Index ?? (combo.Tag is short original ? original : (short)7);
    }
}
