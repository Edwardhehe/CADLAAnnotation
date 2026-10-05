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
        private AnnotationSettings _s;
        /// <summary>打开窗口时活动文档是否处于布局图纸空间（只用于提示"本次生效比例"，不影响保存值）。</summary>
        private readonly bool _paperSpaceNow;
        /// <summary>程序性赋值比例（初始化）期间为 true，避免触发"选比例自动关自适应"。</summary>
        private bool _scaleChanging;
        /// <summary>本窗口内对"比例 + 自适应"询问的回答（只问一次；校验失败重试时不重复弹）。</summary>
        private bool? _scaleAutoAnswer;
        /// <summary>当前 DWG 中已有的文字样式（没有传入时为空，不做存在性提醒）。</summary>
        private readonly HashSet<string> _drawingStyles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>保存成功后的设置（调用方在 DialogResult=true 时读取）。</summary>
        public AnnotationSettings Value => _s;

        /// <summary>构造设置窗口。textStyles 为当前 DWG 中可用的文字样式名列表。</summary>
        public SettingsWindow(AnnotationSettings value, IEnumerable<string> textStyles = null)
        {
            InitializeComponent();
            _s = value ?? new AnnotationSettings();
            ShapeCombo.ItemsSource = new[] { "矩形", "菱形", "椭圆" };
            CloudStyleCombo.ItemsSource = new[] { "等宽", "渐变" };
            DefaultRoleCombo.ItemsSource = AnnotationOptions.Roles;
            DefaultDisciplineCombo.ItemsSource = AnnotationOptions.Disciplines;
            ConnectorCombo.ItemsSource = new[] { "-", "_", "·", "无" };
            ScaleRatioCombo.ItemsSource = AnnotationOptions.PlotScales;
            // 所有颜色下拉菜单共享 ACI 1~255 色列表
            foreach (var combo in AllColorCombos())
                combo.ItemsSource = AciColors.All;
            // 文字样式下拉菜单：合并预设 + DWG 中已有样式
            var styles = new List<string> { "Standard" };
            if (textStyles != null)
            {
                foreach (var name in textStyles) if (!string.IsNullOrWhiteSpace(name)) _drawingStyles.Add(name);
                styles.AddRange(_drawingStyles.Where(x => !string.Equals(x, "Standard", StringComparison.OrdinalIgnoreCase)));
            }
            TextStyleCombo.ItemsSource = styles;
            LoadValues();
            try { _paperSpaceNow = AnnotationService.IsPaperSpaceActiveNow(); } catch { _paperSpaceNow = false; }
            if (_s.LoadedFromFallback && !SettingsStore.IsCorrupt)
                Title += "（settings.xml 暂时读不到，当前显示默认值；点「保存设置」才会覆盖原文件）";
            if (SettingsStore.IsCorrupt)
                Title += "（settings.xml 损坏，当前显示默认值；点「保存设置」才会覆盖原文件" +
                         (string.IsNullOrEmpty(SettingsStore.CorruptBackupPath) ? "" : "，原文件已备份") + "）";
            // 比例提示随输入实时刷新（可编辑下拉手填、字高/半径改动都会更新）。
            ScaleRatioCombo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((sender, e) => RefreshScaleHint()));
            TextHeightText.TextChanged += (sender, e) => RefreshScaleHint();
            CloudRadiusText.TextChanged += (sender, e) => RefreshScaleHint();
            Loaded += (sender, e) => { WindowSizing.FitToWorkArea(this, 0.9, 0.86); RefreshDependencies(); };
        }

        private IEnumerable<ComboBox> AllColorCombos() => new[] { ColorIndexCombo, CloudColorCombo, LeaderColorCombo, TextColorCombo, BoxColorCombo, ReplyColorCombo, ScreenshotBackgroundColorCombo, PassColorCombo, CheckColorCombo };

        /// <summary>统一颜色模式下记住的 7 个分项颜色（顺序：云线,引线,文字,框,已回复,已完成,对勾）。</summary>
        private ComboBox[] SeparateColorCombos() => new[] { CloudColorCombo, LeaderColorCombo, TextColorCombo, BoxColorCombo, ReplyColorCombo, PassColorCombo, CheckColorCombo };

        private void LoadValues()
        {
            // 下拉不可编辑：设置文件里的未知值回退到第一项，避免显示为空、保存成空串。
            ShapeCombo.Text = ShapeCombo.Items.Contains(_s.Shape) ? _s.Shape : "矩形";
            CloudStyleCombo.Text = CloudStyleCombo.Items.Contains(_s.CloudStyle) ? _s.CloudStyle : "等宽";
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
            DefaultAuthorText.Text = _s.DefaultAuthor; DefaultRoleCombo.Text = _s.DefaultRole; DefaultDisciplineCombo.Text = _s.DefaultDiscipline;
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
            // 统一颜色模式：分项下拉显示之前记住的分项颜色（取消统一颜色时即恢复为原来的分项配色）。
            if (_s.SameColors && TryParseSeparateColors(_s.SeparateColors, out var separate))
            {
                var combos = SeparateColorCombos();
                for (var i = 0; i < combos.Length; i++) SelectColor(combos[i], separate[i]);
            }
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
            RefreshDependencies();
        }

        private static bool TryParseSeparateColors(string text, out short[] colors)
        {
            colors = null;
            var parts = (text ?? "").Split(',');
            if (parts.Length != 7) return false;
            var result = new short[7];
            for (var i = 0; i < 7; i++) if (!short.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out result[i])) return false;
            colors = result;
            return true;
        }

        private void UpdateCloudMarkerSummary()
        {
            if (CloudMarkerSummaryText == null) return;
            var preview = _s.Clone();
            preview.CloudMarkerEnabled = true;
            var detail = CloudMarker.Describe(preview).Substring(2); // 去掉"开，"
            CloudMarkerSummaryText.Text = CloudMarkerEnabledCheck.IsChecked == true
                ? "当前：开，" + detail
                : "当前：关（开启后为：" + detail + "）";
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
                var collected = Collect(true);
                if (collected == null) return; // 用户在提醒里选择返回修改
                SettingsStore.Save(collected, true);
                _s = collected;
                DialogResult = true;
            }
            catch (Exception ex) { MessageBox.Show(this, "设置保存失败：" + ex.Message, "GM批注", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        /// <summary>把界面值收集到一份新的设置副本（校验失败抛 InvalidOperationException；原副本不被部分改写）。
        /// interactive=true 时做"比例+自适应""文字样式不存在"等询问，用户选择返回修改时返回 null。</summary>
        private AnnotationSettings Collect(bool interactive)
        {
            var t = _s.Clone();
            t.Shape = ShapeCombo.Text.Trim(); t.CloudStyle = CloudStyleCombo.Text.Trim();
            var cloudAuto = On(CloudAutoFitCheck); var fontAuto = On(FontAutoFitCheck);
            // 被禁用（不参与计算）的输入框不阻止保存：值非法时保留原值。
            t.CloudRadius = cloudAuto ? Lenient(CloudRadiusText, _s.CloudRadius, 0.001) : N(CloudRadiusText, "云线半径", 0.001);
            t.LineWidth = cloudAuto ? Lenient(LineWidthText, _s.LineWidth, 0) : N(LineWidthText, "云线/引线线宽", 0);
            t.CloudAutoFit = cloudAuto; t.FontAutoFit = fontAuto;
            t.AutoTextViewPercent = fontAuto || cloudAuto ? N(AutoTextViewPercentText, "字高占云线 %", 0.1, 10) : Lenient(AutoTextViewPercentText, _s.AutoTextViewPercent, 0.1, 10);
            t.ScaleRatio = ParseScale(ScaleRatioCombo.Text);
            // 比例与自适应是两套算法：选了比例却还勾着自适应时，字高仍按云线尺寸算，比例看不出效果。
            // 这里问一句再决定（本窗口只问一次），避免用户以为"比例没生效"。
            if (interactive && t.ScaleRatio > 1.0 + 1e-9 && (t.FontAutoFit || t.CloudAutoFit))
            {
                if (!_scaleAutoAnswer.HasValue)
                {
                    var autoNames = t.FontAutoFit && t.CloudAutoFit ? "字体/云线自适应" : t.FontAutoFit ? "字体自适应" : "云线自适应";
                    var answer = MessageBox.Show(this,
                        "已选比例 1:" + FormatDenominator(t.ScaleRatio) + "，但仍勾选了" + autoNames + "。\n\n" +
                        "自适应按云线尺寸计算，比例不参与，图上尺寸不会按比例放大。\n" +
                        "是否改为按比例换算（取消自适应勾选）？",
                        "GM批注", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    _scaleAutoAnswer = answer == MessageBoxResult.Yes;
                }
                if (_scaleAutoAnswer == true)
                {
                    t.FontAutoFit = false; t.CloudAutoFit = false;
                    FontAutoFitCheck.IsChecked = false; CloudAutoFitCheck.IsChecked = false;
                }
            }
            t.CloudMarkerEnabled = On(CloudMarkerEnabledCheck);
            t.AutoCloseOrtho = On(AutoCloseOrthoCheck); t.AutoCloseSnap = On(AutoCloseSnapCheck);
            t.DoubleClickEdit = On(DoubleClickEditCheck); t.ViewTopIsNorth = On(ViewTopIsNorthCheck);
            t.HeaderHeight = N(HeaderHeightText, "首行字高", 0.1); t.SecondLineHeight = N(SecondLineHeightText, "次行字高", 0.1);
            t.TextHeight = N(TextHeightText, "正文字高", 0.1);
            t.DefaultAuthor = DefaultAuthorText.Text.Trim(); t.DefaultRole = DefaultRoleCombo.Text.Trim();
            t.DefaultDiscipline = DefaultDisciplineCombo.Text.Trim();
            t.FixedWidth = On(FixedWidthCheck);
            t.FixedWidthValue = t.FixedWidth ? N(FixedWidthValueText, "固定宽度", 1) : Lenient(FixedWidthValueText, _s.FixedWidthValue, 1);
            t.AutoNumber = On(AutoNumberCheck);
            t.LayerName = LayerNameText.Text.Trim();
            if (t.LayerName.Length == 0) { LayerNameText.Focus(); throw new InvalidOperationException("图层名称不能为空。"); }
            CheckLayerText(t.LayerName, "图层基础名称", LayerNameText);
            t.TextStyleName = TextStyleCombo.Text.Trim();
            if (interactive && t.TextStyleName.Length > 0 && _drawingStyles.Count > 0 && !_drawingStyles.Contains(t.TextStyleName) &&
                !string.Equals(t.TextStyleName, "Standard", StringComparison.OrdinalIgnoreCase))
            {
                var answer = MessageBox.Show(this,
                    "当前图纸中没有文字样式「" + t.TextStyleName + "」，在这张图里创建批注时会改用默认样式（Standard）。\n\n仍然保存这个样式名吗？",
                    "GM批注", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) { TextStyleCombo.Focus(); return null; }
            }
            t.SameColors = On(SameColorsCheck);
            t.ColorIndex = SelectedColor(ColorIndexCombo); t.CloudColor = SelectedColor(CloudColorCombo);
            t.LeaderColor = SelectedColor(LeaderColorCombo); t.TextColor = SelectedColor(TextColorCombo);
            t.BoxColor = SelectedColor(BoxColorCombo); t.ReplyColor = SelectedColor(ReplyColorCombo);
            t.ScreenshotBackgroundOnceReply = On(ScreenshotBackgroundOnceReplyCheck);
            t.ScreenshotBackgroundColor = SelectedColor(ScreenshotBackgroundColorCombo);
            t.PassColor = SelectedColor(PassColorCombo); t.CheckColor = SelectedColor(CheckColorCombo);
            t.CheckHeight = Lenient(CheckHeightText, _s.CheckHeight, 0.01); // 预留项，输入框禁用：不阻止保存
            t.LayerAppendDate = On(LayerAppendDateCheck); t.LayerAppendName = On(LayerAppendNameCheck);
            t.DateBeforeName = On(DateBeforeNameCheck); t.Connector = ConnectorCombo.Text.Trim();
            if (t.Connector != "无") CheckLayerText(t.Connector, "连接符", ConnectorCombo);
            if (t.LayerAppendName) CheckLayerText(t.DefaultAuthor, "默认批注人（已勾选「图层名添加批注人姓名」）", DefaultAuthorText);
            t.Plottable = On(PlottableCheck);
            t.ShowDiscipline = On(ShowDisciplineCheck);
            t.ShowAuthor = On(ShowAuthorCheck); t.ShowRole = On(ShowRoleCheck);
            t.ShowDate = On(ShowDateCheck); t.ShowStatus = On(ShowStatusCheck);
            t.ShowDrawingNo = On(ShowDrawingNoCheck);
            // 统一颜色模式：运行时各分项颜色同步为统一颜色（与旧版一致），但把用户原来的分项颜色另存一份，取消统一颜色时可还原。
            if (t.SameColors)
            {
                t.SeparateColors = string.Join(",", SeparateColorCombos().Select(c => SelectedColor(c).ToString(CultureInfo.InvariantCulture)));
                t.CloudColor = t.LeaderColor = t.TextColor = t.BoxColor = t.ReplyColor = t.PassColor = t.CheckColor = t.ColorIndex;
            }
            else t.SeparateColors = "";
            return t;
        }

        /// <summary>恢复默认：界面填入默认值（保留下一个编号）；点「保存设置」后才生效。</summary>
        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(this, "把界面上的所有设置恢复为默认值？\n（下一个编号保持不变；点「保存设置」后才生效，点「取消」可放弃）",
                    "GM批注", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            var defaults = new AnnotationSettings { NextNumber = _s.NextNumber };
            _s = defaults; _scaleAutoAnswer = null;
            LoadValues();
        }

        /// <summary>导入：读入导出的设置文件填到界面（保留本机的下一个编号）；点「保存设置」后才生效。</summary>
        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "导入 GM批注 设置", Filter = "GM批注设置 (*.xml)|*.xml|所有文件 (*.*)|*.*" };
            if (dialog.ShowDialog(this) != true) return;
            if (!SettingsStore.TryLoadFrom(dialog.FileName, out var imported, out var error))
            {
                MessageBox.Show(this, "导入失败：" + error, "GM批注", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            imported.NextNumber = _s.NextNumber;
            _s = imported; _scaleAutoAnswer = null;
            LoadValues();
            MessageBox.Show(this, "已读入到界面，请核对后点「保存设置」生效。", "GM批注", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>导出：把界面上的当前值（需通过校验）写成设置文件。</summary>
        private void Export_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var current = Collect(false);
                var dialog = new Microsoft.Win32.SaveFileDialog { Title = "导出 GM批注 设置", Filter = "GM批注设置 (*.xml)|*.xml", FileName = "GM批注设置-" + DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".xml" };
                if (dialog.ShowDialog(this) != true) return;
                SettingsStore.ExportTo(current, dialog.FileName);
                MessageBox.Show(this, "已导出到：\n" + dialog.FileName, "GM批注", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex) { MessageBox.Show(this, "导出失败：" + ex.Message, "GM批注", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + AppPaths.DataFolder + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(this, "无法打开数据目录：" + ex.Message + "\n" + AppPaths.DataFolder, "GM批注", MessageBoxButton.OK, MessageBoxImage.Warning); }
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
                if (On(CloudAutoFitCheck))
                    hint += " 当前勾选了云线自适应：云线半径/线宽按云线尺寸计算。";
                hint += " 布局图纸空间（未进入视口）自动按 1:1，模型空间（含在布局里进入视口）用此比例；切换空间不会改动这里保存的比例。";
                if (!On(FontAutoFitCheck) || !On(CloudAutoFitCheck))
                    hint += _paperSpaceNow ? " 当前处于布局图纸空间：本次生效 1:1。" : " 当前处于模型空间：本次生效 1:" + denominator + "。";
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
                    : "未启用字体自适应：字高按「打印字高(mm) × 比例分母」换算为图面尺寸（如 3mm、1:100 → 图上 300）；布局图纸空间自动 1:1，模型空间用此比例。";
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
        /// <summary>不参与计算（输入框禁用）的数值：能解析且在范围内就用新值，否则保留原值，不阻止保存。</summary>
        private static double Lenient(TextBox x, double fallback, double min, double max = double.MaxValue)
            => (double.TryParse(x.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || double.TryParse(x.Text, out n)) && n >= min && n <= max ? n : fallback;
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
