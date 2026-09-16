using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GMAnnotation.Views
{
    /// <summary>格式刷对话框：预填源批注实测样式（字高/文字样式/云线样式/图层/颜色），
    /// 用户调整后确定，命令侧再连续点选目标批注套用（见 <see cref="AnnotationService.ApplyFormatBrush"/>）。
    /// 布局对齐批注设置：批注文字（首行/次行/批注字高、文字样式）、云线样式（等宽/渐变）、图层（图层名与云线/引线/文字颜色）。</summary>
    internal partial class FormatBrushWindow : Window
    {
        private readonly AnnotationService.FormatBrushSpec _spec;

        /// <summary>对话框里可选的 ACI 颜色（名称 + 色板）。</summary>
        private sealed class AcColor
        {
            public short Aci { get; }
            public string Name { get; }
            public Brush Brush { get; }
            public AcColor(short aci, string name, Brush brush) { Aci = aci; Name = name; Brush = brush; }
        }

        private static readonly List<AcColor> Palette = new List<AcColor>
        {
            new AcColor(1, "红色", new SolidColorBrush(Color.FromRgb(0xE0,0x30,0x30))),
            new AcColor(2, "黄色", new SolidColorBrush(Color.FromRgb(0xE8,0xC0,0x00))),
            new AcColor(3, "绿色", new SolidColorBrush(Color.FromRgb(0x30,0xA8,0x50))),
            new AcColor(4, "青色", new SolidColorBrush(Color.FromRgb(0x20,0xA0,0xC0))),
            new AcColor(5, "蓝色", new SolidColorBrush(Color.FromRgb(0x30,0x60,0xD0))),
            new AcColor(6, "紫色", new SolidColorBrush(Color.FromRgb(0xD0,0x30,0xD0))),
            new AcColor(7, "白/黑", new SolidColorBrush(Color.FromRgb(0x50,0x50,0x50))),
            new AcColor(8, "灰色", new SolidColorBrush(Color.FromRgb(0x80,0x80,0x80))),
            new AcColor(9, "浅灰", new SolidColorBrush(Color.FromRgb(0xB0,0xB0,0xB0))),
            new AcColor(30, "橙色", new SolidColorBrush(Color.FromRgb(0xE8,0x80,0x10))),
        };

        public FormatBrushWindow(AnnotationService.FormatBrushSpec spec)
        {
            InitializeComponent();
            _spec = spec ?? new AnnotationService.FormatBrushSpec();
            LoadFields();
        }

        private void LoadFields()
        {
            foreach (var box in new[] { HeaderHeightBox, SecondLineHeightBox, TextHeightBox })
            {
                foreach (var preset in new[] { "2", "2.5", "3", "3.5", "4", "5", "6" }) box.Items.Add(preset);
            }
            HeaderHeightBox.Text = Format(_spec.HeaderHeight);
            SecondLineHeightBox.Text = Format(_spec.SecondLineHeight);
            TextHeightBox.Text = Format(_spec.TextHeight);

            foreach (var name in _spec.TextStyleNames) TextStyleBox.Items.Add(name);
            TextStyleBox.SelectedItem = _spec.TextStyleName != null && _spec.TextStyleNames.Contains(_spec.TextStyleName)
                ? _spec.TextStyleName
                : (object)(_spec.TextStyleName ?? "");

            foreach (var name in _spec.LayerNames) LayerBox.Items.Add(name);
            LayerBox.Text = _spec.LayerName ?? "";

            var gradient = _spec.CloudStyle == "渐变";
            StyleEven.IsChecked = !gradient;
            StyleGradient.IsChecked = gradient;

            foreach (var pair in new[]
                     {
                         (Combo: CloudColorBox, Aci: _spec.CloudColor),
                         (Combo: LeaderColorBox, Aci: _spec.LeaderColor),
                         (Combo: TextColorBox, Aci: _spec.TextColor)
                     })
            {
                var items = new List<AcColor>(Palette);
                // 源批注颜色不在调色板里时，补一项"源色"，保证原值原样保留（不会被近似成调色板色）。
                if (pair.Aci > 0 && items.FindAll(c => c.Aci == pair.Aci).Count == 0)
                    items.Add(new AcColor(pair.Aci, "源色 " + pair.Aci, new SolidColorBrush(Colors.DarkSlateGray)));
                pair.Combo.DisplayMemberPath = "Name";
                pair.Combo.SelectedValuePath = "Aci";
                pair.Combo.ItemsSource = items;
                pair.Combo.SelectedValue = pair.Aci;
            }
            UpdateSwatches();
        }

        private static string Format(double value)
            => value.ToString("0.###", CultureInfo.InvariantCulture);

        private void Color_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSwatches();

        private void UpdateSwatches()
        {
            CloudSwatch.Fill = SwatchOf(CloudColorBox);
            LeaderSwatch.Fill = SwatchOf(LeaderColorBox);
            TextSwatch.Fill = SwatchOf(TextColorBox);
        }

        private static Brush SwatchOf(ComboBox combo)
            => (combo.SelectedItem as AcColor)?.Brush ?? new SolidColorBrush(Colors.Gray);

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            var header = ParseHeight(HeaderHeightBox.Text);
            var second = ParseHeight(SecondLineHeightBox.Text);
            var text = ParseHeight(TextHeightBox.Text);
            if (header <= 0 || second <= 0 || text <= 0)
            {
                MessageBox.Show(this, "字高必须是大于 0 的数字。", "格式刷", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _spec.HeaderHeight = header;
            _spec.SecondLineHeight = second;
            _spec.TextHeight = text;
            _spec.TextStyleName = (TextStyleBox.SelectedItem as string ?? TextStyleBox.Text ?? "").Trim();
            _spec.CloudStyle = StyleGradient.IsChecked == true ? "渐变" : "等宽";
            var layer = (LayerBox.SelectedItem as string ?? LayerBox.Text ?? "").Trim();
            if (layer.Length == 0)
            {
                MessageBox.Show(this, "图层名不能为空。", "格式刷", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _spec.LayerName = layer;
            _spec.CloudColor = AciOf(CloudColorBox);
            _spec.LeaderColor = AciOf(LeaderColorBox);
            _spec.TextColor = AciOf(TextColorBox);
            DialogResult = true;
        }

        private static double ParseHeight(string raw)
            => double.TryParse((raw ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0
                ? v
                : (double.TryParse((raw ?? "").Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var v2) && v2 > 0 ? v2 : 0);

        private static short AciOf(ComboBox combo) => (short)((combo.SelectedItem as AcColor)?.Aci ?? 7);
    }
}
