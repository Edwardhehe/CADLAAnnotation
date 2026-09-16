using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Media;

namespace GMAnnotation
{
    /// <summary>ACI（AutoCAD Color Index）颜色项，用于下拉菜单展示。</summary>
    public sealed class AciColorItem
    {
        public short Index { get; }
        public string Name { get; }
        public string Display => $"{Index}  {Name}";
        public SolidColorBrush Brush { get; }

        internal AciColorItem(short index, string name, byte r, byte g, byte b)
        {
            Index = index;
            Name = name;
            Brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        }
    }

    /// <summary>ACI 255 色索引 → RGB 映射，供设置界面颜色下拉菜单使用。</summary>
    public static class AciColors
    {
        /// <summary>全部 255 种 ACI 颜色。</summary>
        public static ReadOnlyCollection<AciColorItem> All { get; }

        static AciColors()
        {
            var list = new List<AciColorItem>(255);

            // 1–9: 标准色
            list.Add(new AciColorItem(1, "红", 255, 0, 0));
            list.Add(new AciColorItem(2, "黄", 255, 255, 0));
            list.Add(new AciColorItem(3, "绿", 0, 255, 0));
            list.Add(new AciColorItem(4, "青", 0, 255, 255));
            list.Add(new AciColorItem(5, "蓝", 0, 0, 255));
            list.Add(new AciColorItem(6, "洋红", 255, 0, 255));
            list.Add(new AciColorItem(7, "白", 255, 255, 255));
            list.Add(new AciColorItem(8, "深灰", 128, 128, 128));
            list.Add(new AciColorItem(9, "浅灰", 192, 192, 192));

            // 10–249: HSL 色轮分布
            var hueTable = new[] { 0, 20, 40, 60, 80, 100, 120, 140, 160, 180, 200, 220, 240, 260, 280, 300, 320, 340, 10, 30, 50, 70, 90, 110 };
            var satTable = new[] { 1.0, 0.85, 0.7 };
            var lumTable = new[] { 0.55, 0.70, 0.85, 0.40, 0.25, 0.6, 0.5, 0.35 };

            int idx = 10;
            foreach (var h in hueTable)
            {
                foreach (var l in lumTable)
                {
                    if (idx > 249) break;
                    var rgb = HslToRgb(h / 360.0, 0.8, l);
                    list.Add(new AciColorItem((short)idx, $"{idx}", rgb.r, rgb.g, rgb.b));
                    idx++;
                }
                if (idx > 249) break;
            }

            // 250–255: 灰度
            list.Add(new AciColorItem(250, "250", 51, 51, 51));
            list.Add(new AciColorItem(251, "251", 91, 91, 91));
            list.Add(new AciColorItem(252, "252", 132, 132, 132));
            list.Add(new AciColorItem(253, "253", 173, 173, 173));
            list.Add(new AciColorItem(254, "254", 214, 214, 214));
            list.Add(new AciColorItem(255, "255", 255, 255, 255));

            All = new ReadOnlyCollection<AciColorItem>(list);
        }

        /// <summary>根据 ACI 索引查找颜色项，找不到则返回索引 7（白色）。</summary>
        public static AciColorItem Find(short index)
        {
            // 列表下标 0 对应索引 1
            var i = index - 1;
            if (i >= 0 && i < All.Count) return All[i];
            return All[6]; // 默认白色
        }

        private static (byte r, byte g, byte b) HslToRgb(double h, double s, double l)
        {
            double r, g, b;
            if (s == 0) { r = g = b = l; }
            else
            {
                double HueToRgb(double p, double q, double t)
                {
                    if (t < 0) t += 1;
                    if (t > 1) t -= 1;
                    if (t < 1.0 / 6) return p + (q - p) * 6 * t;
                    if (t < 1.0 / 2) return q;
                    if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
                    return p;
                }
                var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
                var p = 2 * l - q;
                r = HueToRgb(p, q, h + 1.0 / 3);
                g = HueToRgb(p, q, h);
                b = HueToRgb(p, q, h - 1.0 / 3);
            }
            return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }
    }
}
