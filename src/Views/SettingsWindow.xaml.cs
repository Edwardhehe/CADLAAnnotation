using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace LAAnnotation.Views
{
    internal partial class SettingsWindow : Window
    {
        private readonly AnnotationSettings _s;
        public AnnotationSettings Value => _s;

        /// <summary>构造设置窗口。textStyles 为当前 DWG 中可用的文字样式名列表。</summary>
        public SettingsWindow(AnnotationSettings value, IEnumerable<string> textStyles = null)
        {
            InitializeComponent();
            _s = value;
            ShapeCombo.ItemsSource = new[] { "矩形", "菱形", "椭圆" };
            CloudStyleCombo.ItemsSource = new[] { "等宽", "渐变" };
            DefaultRoleCombo.ItemsSource = new[] { "批注人", "校审人", "回复人" };
            ConnectorCombo.ItemsSource = new[] { "-", "_", "·", "无" };
            // 所有颜色下拉菜单共享 ACI 255 色列表
            foreach (var combo in new[] { ColorIndexCombo, CloudColorCombo, LeaderColorCombo, TextColorCombo, BoxColorCombo, ReplyColorCombo, ScreenshotBackgroundColorCombo, PassColorCombo, CheckColorCombo })
                combo.ItemsSource = AciColors.All;
            // 文字样式下拉菜单：合并预设 + DWG 中已有样式
            var styles = new List<string> { "Standard" };
            if (textStyles != null) styles.AddRange(textStyles.Where(s => !string.Equals(s, "Standard", StringComparison.OrdinalIgnoreCase)));
            TextStyleCombo.ItemsSource = styles;
            LoadValues();
            Loaded += (s, e) => { WindowSizing.FitToWorkArea(this, 0.9, 0.86); RefreshDependencies(); };
        }

        private void LoadValues()
        {
            ShapeCombo.Text = _s.Shape; CloudStyleCombo.Text = _s.CloudStyle;
            Set(CloudRadiusText, _s.CloudRadius); Set(LineWidthText, _s.LineWidth);
            CloudAutoFitCheck.IsChecked = _s.CloudAutoFit; FontAutoFitCheck.IsChecked = _s.FontAutoFit;
            Set(AutoTextViewPercentText, _s.AutoTextViewPercent); Set(ScaleRatioText, _s.ScaleRatio);
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
            ShowNumberCheck.IsChecked = _s.ShowNumber; ShowDisciplineCheck.IsChecked = _s.ShowDiscipline;
            ShowAuthorCheck.IsChecked = _s.ShowAuthor; ShowRoleCheck.IsChecked = _s.ShowRole;
            ShowDateCheck.IsChecked = _s.ShowDate; ShowStatusCheck.IsChecked = _s.ShowStatus;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _s.Shape = ShapeCombo.Text.Trim(); _s.CloudStyle = CloudStyleCombo.Text.Trim();
                _s.CloudRadius = N(CloudRadiusText, "云线半径", 0.001); _s.LineWidth = N(LineWidthText, "云线线宽", 0);
                _s.CloudAutoFit = On(CloudAutoFitCheck); _s.FontAutoFit = On(FontAutoFitCheck);
                _s.AutoTextViewPercent = N(AutoTextViewPercentText, "自适应百分比", 0.1, 10);
                _s.ScaleRatio = N(ScaleRatioText, "比例", 0.01);
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
                _s.Plottable = On(PlottableCheck);
                _s.ShowNumber = On(ShowNumberCheck); _s.ShowDiscipline = On(ShowDisciplineCheck);
                _s.ShowAuthor = On(ShowAuthorCheck); _s.ShowRole = On(ShowRoleCheck);
                _s.ShowDate = On(ShowDateCheck); _s.ShowStatus = On(ShowStatusCheck);
                // 统一颜色模式：所有独立颜色同步为统一颜色
                if (_s.SameColors) _s.CloudColor = _s.LeaderColor = _s.TextColor = _s.BoxColor = _s.ReplyColor = _s.PassColor = _s.CheckColor = _s.ColorIndex;
                SettingsStore.Save(_s); DialogResult = true;
            }
            catch (Exception ex) { MessageBox.Show(this, "设置保存失败：" + ex.Message, "LA批注", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) { DialogResult = false; }
        private void Dependency_Changed(object sender, RoutedEventArgs e) { RefreshDependencies(); }

        private void RefreshDependencies()
        {
            if (!IsInitialized) return;
            FixedWidthValueText.IsEnabled = On(FixedWidthCheck);
            var cloudAuto = On(CloudAutoFitCheck);
            CloudRadiusText.IsEnabled = !cloudAuto;
            LineWidthText.IsEnabled = !cloudAuto;
            var fontAuto = On(FontAutoFitCheck);
            AutoTextViewPercentText.IsEnabled = fontAuto;
            HeaderHeightText.IsEnabled = !fontAuto;
            SecondLineHeightText.IsEnabled = !fontAuto;
            TextHeightText.IsEnabled = !fontAuto;
            var same = On(SameColorsCheck);
            ColorIndexCombo.IsEnabled = same;
            foreach (var x in new[] { CloudColorCombo, LeaderColorCombo, TextColorCombo, BoxColorCombo, ReplyColorCombo, PassColorCombo, CheckColorCombo })
                x.IsEnabled = !same;
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
        /// <summary>在颜色 ComboBox 中按 ACI 索引选中对应项。</summary>
        private static void SelectColor(ComboBox combo, short index) => combo.SelectedItem = AciColors.Find(index);
        /// <summary>获取颜色 ComboBox 当前选中项的 ACI 索引，未选中时返回 7（白色）。</summary>
        private static short SelectedColor(ComboBox combo) => (combo.SelectedItem as AciColorItem)?.Index ?? 7;
    }
}
