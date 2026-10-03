using System;
using System.Collections.Generic;
using System.Linq;
#if ZWCAD
using ZwSoft.ZwCAD.Colors;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.Geometry;
#else
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
#endif

namespace GMAnnotation
{
    /// <summary>
    /// 单绘云线的角标（云线右下角内侧的"框 + 文字"，如六边形里写 A）。
    /// <para>尺寸（字高/框高/框宽）按"打印毫米"保存，落图前由 <see cref="AnnotationService.ResolveEffectiveSettings"/>
    /// 用与批注字高完全相同的比例/自适应倍数换算成图面尺寸；这里只负责几何与落图。</para>
    /// <para>角标与云线放进同一个匿名编组：选中云线即选中角标，移动/复制/删除一起走。</para>
    /// </summary>
    internal static class CloudMarker
    {
        /// <summary>单字形（框宽由框高决定）。</summary>
        public static readonly string[] SingleShapes = { "圆", "矩形", "六边形", "八边形", "菱形" };
        /// <summary>长形（框宽取"框宽"设置，适合多个字）。</summary>
        public static readonly string[] WideShapes = { "椭圆", "长矩形", "长六边形", "长八边形", "平行四边形" };
        public const string DefaultShape = "六边形";

        public static bool IsWide(string shape) => Array.IndexOf(WideShapes, shape) >= 0;

        public static string Normalize(string shape)
            => Array.IndexOf(SingleShapes, shape) >= 0 || Array.IndexOf(WideShapes, shape) >= 0 ? shape : DefaultShape;

        /// <summary>框的实际宽高（图面单位，输入为已换算的 effective 设置）。框高至少容得下字高。</summary>
        public static void BoxSize(AnnotationSettings s, out double width, out double height)
        {
            var shape = Normalize(s.CloudMarkerShape);
            height = Math.Max(s.CloudMarkerBoxHeight, s.CloudMarkerTextHeight * 1.1);
            if (IsWide(shape)) width = Math.Max(s.CloudMarkerBoxWidth, height);
            else if (shape == "六边形") width = height * 2 / Math.Sqrt(3); // 正六边形（上下为平边）
            else width = height;
        }

        /// <summary>
        /// 在云线右下角内侧生成角标并与云线编为匿名组。<paramref name="min"/>/<paramref name="max"/> 为云线顶点包络（UCS 局部坐标），
        /// 几何先在局部坐标构建再 TransformBy(<paramref name="ucsToWcs"/>)，与云线同一套坐标处理。
        /// 返回提示文字（标记大于云线等），没有问题时返回 null。
        /// </summary>
        public static string AddToCloud(Database db, Transaction tr, BlockTableRecord space, Entity cloud,
            AnnotationSettings s, Point2d min, Point2d max, double z, Matrix3d ucsToWcs, bool polygonCloud, string text)
        {
            BoxSize(s, out var w, out var h);
            var shape = Normalize(s.CloudMarkerShape);
            // 内缩距离：至少 0.3 倍框高；渐变云线轮廓会向内起伏约一个云线半径，再多让一些。
            var radius = Math.Max(0, s.CloudRadius);
            var margin = Math.Max(h * 0.3, s.CloudStyle == "渐变" ? radius : radius * 0.3);
            var anchor = Anchor(polygonCloud ? "矩形" : s.Shape, min, max);
            var center = new Point2d(anchor.X - margin - w / 2, anchor.Y + margin + h / 2);

            string warning = null;
            if (w + 2 * margin > max.X - min.X || h + 2 * margin > max.Y - min.Y)
                warning = "云线范围比标记还小，标记可能超出云线（可在标记设置里调小框高/字高）。";

            var color = s.CloudMarkerColor >= 1 && s.CloudMarkerColor <= 255
                ? Color.FromColorIndex(ColorMethod.ByAci, s.CloudMarkerColor)
                : cloud.Color;
            var ids = new ObjectIdCollection { cloud.ObjectId };

            var outline = BuildOutline(shape, center, w, h, z);
            outline.TransformBy(ucsToWcs);
            outline.Layer = cloud.Layer; outline.Color = color;
            ids.Add(space.AppendEntity(outline)); tr.AddNewlyCreatedDBObject(outline, true);

            if (!string.IsNullOrEmpty(text))
            {
                var mtext = new MText
                {
                    Location = new Point3d(center.X, center.Y, z),
                    TextHeight = Math.Max(0.001, s.CloudMarkerTextHeight),
                    Contents = EscapeMText(text),
                    Attachment = AttachmentPoint.MiddleCenter
                };
                AnnotationService.ApplyTextStyle(db, tr, mtext, s.TextStyleName);
                mtext.TransformBy(ucsToWcs);
                mtext.Layer = cloud.Layer; mtext.Color = color;
                ids.Add(space.AppendEntity(mtext)); tr.AddNewlyCreatedDBObject(mtext, true);
            }

            GroupTogether(db, tr, ids);
            return warning;
        }

        /// <summary>云线外形的"右下角"：矩形/PL 取包络右下角；椭圆取 -45° 方向的边界点；菱形取右下边中点。</summary>
        private static Point2d Anchor(string cloudShape, Point2d min, Point2d max)
        {
            var cx = (min.X + max.X) / 2; var cy = (min.Y + max.Y) / 2;
            var a = (max.X - min.X) / 2; var b = (max.Y - min.Y) / 2;
            if (cloudShape == "椭圆") return new Point2d(cx + a * Math.Sqrt(0.5), cy - b * Math.Sqrt(0.5));
            if (cloudShape == "菱形") return new Point2d(cx + a / 2, cy - b / 2);
            return new Point2d(max.X, min.Y);
        }

        private static Entity BuildOutline(string shape, Point2d c, double w, double h, double z)
        {
            double a = w / 2, b = h / 2;
            switch (shape)
            {
                case "圆":
                {
                    var circle = new Polyline();
                    circle.AddVertexAt(0, new Point2d(c.X - a, c.Y), 1, 0, 0);
                    circle.AddVertexAt(1, new Point2d(c.X + a, c.Y), 1, 0, 0);
                    circle.Closed = true; circle.Elevation = z;
                    return circle;
                }
                case "椭圆":
                {
                    if (Math.Abs(a - b) < 1e-9) goto case "圆";
                    var major = a >= b ? new Vector3d(a, 0, 0) : new Vector3d(0, b, 0);
                    var ratio = a >= b ? b / a : a / b;
                    return new Ellipse(new Point3d(c.X, c.Y, z), Vector3d.ZAxis, major, ratio, 0, 2 * Math.PI);
                }
                case "菱形":
                    return Poly(c, z, 0, -b, a, 0, 0, b, -a, 0);
                case "六边形":
                case "长六边形":
                {
                    var t = shape == "六边形" ? a / 2 : Math.Min(b * 0.6, a / 2);
                    return Poly(c, z, -a, 0, -a + t, -b, a - t, -b, a, 0, a - t, b, -a + t, b);
                }
                case "八边形":
                case "长八边形":
                {
                    var k = Math.Min(h / (2 + Math.Sqrt(2)), a);
                    return Poly(c, z, -a + k, -b, a - k, -b, a, -b + k, a, b - k, a - k, b, -a + k, b, -a, b - k, -a, -b + k);
                }
                case "平行四边形":
                {
                    var d = Math.Min(h * 0.35, a * 0.5);
                    return Poly(c, z, -a, -b, a - d, -b, a, b, -a + d, b);
                }
                default: // 矩形 / 长矩形
                    return Poly(c, z, -a, -b, a, -b, a, b, -a, b);
            }
        }

        private static Polyline Poly(Point2d c, double z, params double[] xy)
        {
            var p = new Polyline();
            for (var i = 0; i + 1 < xy.Length; i += 2) p.AddVertexAt(i / 2, new Point2d(c.X + xy[i], c.Y + xy[i + 1]), 0, 0, 0);
            p.Closed = true; p.Elevation = z;
            return p;
        }

        /// <summary>云线 + 角标编成匿名编组（"*"），宿主不支持匿名名时退回唯一命名。</summary>
        private static void GroupTogether(Database db, Transaction tr, ObjectIdCollection ids)
        {
            var groups = (DBDictionary)tr.GetObject(db.GroupDictionaryId, OpenMode.ForWrite);
            var group = new Group("GM云线标记", true);
            try { groups.SetAt("*", group); }
            catch (Exception ex)
            {
                PluginLog.Warning("CloudMarker.Group", "匿名编组失败，改用命名编组：" + ex.Message);
                groups.SetAt("GM_CLOUDMARK_" + Guid.NewGuid().ToString("N"), group);
            }
            tr.AddNewlyCreatedDBObject(group, true);
            group.Append(ids);
        }

        private static string EscapeMText(string value) => (value ?? "").Replace("\\", "\\\\").Replace("{", "\\{").Replace("}", "\\}");

        /// <summary>自动递增：末尾是数字就 +1（保留位数，A9→A10，07→08）；末尾是英文字母就按 A→B…Z→AA 进位（保留大小写）；否则不变。</summary>
        public static string Next(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var i = text.Length;
            while (i > 0 && text[i - 1] >= '0' && text[i - 1] <= '9') i--;
            if (i < text.Length)
            {
                var digits = text.Substring(i);
                if (long.TryParse(digits, out var n)) return text.Substring(0, i) + (n + 1).ToString().PadLeft(digits.Length, '0');
                return text;
            }
            i = text.Length;
            while (i > 0 && IsAsciiLetter(text[i - 1])) i--;
            if (i == text.Length) return text;
            var letters = text.Substring(i).ToCharArray();
            for (var k = letters.Length - 1; k >= 0; k--)
            {
                var upper = char.IsUpper(letters[k]);
                if (letters[k] != (upper ? 'Z' : 'z')) { letters[k]++; return text.Substring(0, i) + new string(letters); }
                letters[k] = upper ? 'A' : 'a';
            }
            return text.Substring(0, i) + (char.IsUpper(text[i]) ? 'A' : 'a') + new string(letters);
        }

        private static bool IsAsciiLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

        /// <summary>本次要写的角标文字：取本机设置里的最新值（自动递增在多次绘制间推进，不受面板设置快照影响）。</summary>
        public static string CurrentText(AnnotationSettings fallback)
        {
            try { return SettingsStore.Load().CloudMarkerText ?? ""; }
            catch (Exception ex) { PluginLog.Warning("CloudMarker.Text", ex.Message); return fallback?.CloudMarkerText ?? ""; }
        }

        /// <summary>落图成功后推进自动递增（只改角标文字一项）。</summary>
        public static void AdvanceText(string used)
        {
            try
            {
                var fresh = SettingsStore.Load();
                if (!fresh.CloudMarkerAutoIncrement || !string.Equals(fresh.CloudMarkerText ?? "", used ?? "", StringComparison.Ordinal)) return;
                fresh.CloudMarkerText = Next(used);
                SettingsStore.Save(fresh);
            }
            catch (Exception ex) { PluginLog.Warning("CloudMarker.Advance", ex.Message); }
        }

        /// <summary>命令行用的简短状态描述。</summary>
        public static string Describe(AnnotationSettings s)
        {
            if (!s.CloudMarkerEnabled) return "关";
            return "开，" + Normalize(s.CloudMarkerShape) + "「" + (s.CloudMarkerText ?? "") + "」" + (s.CloudMarkerAutoIncrement ? "，自动递增" : "");
        }
    }
}
