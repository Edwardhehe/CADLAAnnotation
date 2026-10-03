using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace GMAnnotation.Views
{
    /// <summary>云线角标设置（GM_PZ_CLOUD 的「标记设置(S)」与设置窗口共用）。确定时把结果写回传入的设置对象，由调用方决定何时保存。</summary>
    internal partial class CloudMarkerWindow : Window
    {
        private readonly AnnotationSettings _s;
        private string _shape;

        public CloudMarkerWindow(AnnotationSettings settings)
        {
            InitializeComponent();
            _s = settings ?? throw new ArgumentNullException(nameof(settings));
            ColorCombo.ItemsSource = AciColors.All;
            EnabledCheck.IsChecked = _s.CloudMarkerEnabled;
            MarkerTextBox.Text = _s.CloudMarkerText ?? "";
            AutoIncrementCheck.IsChecked = _s.CloudMarkerAutoIncrement;
            TextHeightBox.Text = Format(_s.CloudMarkerTextHeight);
            BoxHeightBox.Text = Format(_s.CloudMarkerBoxHeight);
            BoxWidthBox.Text = Format(_s.CloudMarkerBoxWidth);
            var follow = _s.CloudMarkerColor < 1 || _s.CloudMarkerColor > 255;
            FollowCloudColorCheck.IsChecked = follow;
            ColorCombo.SelectedItem = AciColors.All.FirstOrDefault(c => c.Index == (follow ? (short)8 : _s.CloudMarkerColor));
            ColorCombo.IsEnabled = !follow;
            SelectShape(CloudMarker.Normalize(_s.CloudMarkerShape));
            Loaded += (sender, e) => { MarkerTextBox.Focus(); MarkerTextBox.SelectAll(); };
        }

        private void SelectShape(string shape)
        {
            _shape = shape;
            foreach (var button in FindRadioButtons(this))
                button.IsChecked = string.Equals(button.Tag as string, shape, StringComparison.Ordinal);
            UpdateShapeHint();
        }

        private static System.Collections.Generic.IEnumerable<RadioButton> FindRadioButtons(DependencyObject root)
        {
            // 构造函数里视觉树尚未生成，走逻辑树。
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                if (child is RadioButton radio) { yield return radio; continue; }
                foreach (var nested in FindRadioButtons(child)) yield return nested;
            }
        }

        private void Shape_Checked(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton radio && radio.Tag is string shape) { _shape = shape; UpdateShapeHint(); }
        }

        private void UpdateShapeHint()
        {
            if (ShapeNameText == null) return;
            ShapeNameText.Text = "当前形状：" + _shape + (CloudMarker.IsWide(_shape) ? "（长形，框宽生效）" : "（单字形，框宽不生效）");
            if (BoxWidthBox != null) BoxWidthBox.IsEnabled = CloudMarker.IsWide(_shape);
        }

        private void FollowColor_Changed(object sender, RoutedEventArgs e)
        {
            if (ColorCombo != null) ColorCombo.IsEnabled = FollowCloudColorCheck.IsChecked != true;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var textHeight = Number(TextHeightBox, "字高");
                var boxHeight = Number(BoxHeightBox, "框高");
                var boxWidth = Number(BoxWidthBox, "框宽");
                _s.CloudMarkerEnabled = EnabledCheck.IsChecked == true;
                _s.CloudMarkerText = (MarkerTextBox.Text ?? "").Trim();
                _s.CloudMarkerAutoIncrement = AutoIncrementCheck.IsChecked == true;
                _s.CloudMarkerTextHeight = textHeight;
                _s.CloudMarkerBoxHeight = boxHeight;
                _s.CloudMarkerBoxWidth = boxWidth;
                _s.CloudMarkerShape = CloudMarker.Normalize(_shape);
                _s.CloudMarkerColor = FollowCloudColorCheck.IsChecked == true || !(ColorCombo.SelectedItem is AciColorItem item)
                    ? (short)-1 : item.Index;
                DialogResult = true;
            }
            catch (InvalidOperationException ex)
            {
                MessageBox.Show(this, ex.Message, "云线标记设置", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static double Number(TextBox box, string name)
        {
            var text = (box.Text ?? "").Trim();
            if ((double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
                 double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) && value > 0 && value < 1e6)
                return value;
            box.Focus();
            throw new InvalidOperationException(name + "必须是大于 0 的数字。");
        }

        private static string Format(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
