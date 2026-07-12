using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.Colors;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using CadApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif

namespace LAAnnotation.Views
{
    /// <summary>浮动批注面板：选择批注类型/形式/设置，实时生效，支持连续批注。</summary>
    internal partial class AnnotationPanel : Window
    {
        private static AnnotationPanel _instance;
        private AnnotationSettings _s;
        private readonly List<Point3d> _multiCloudFirsts = new List<Point3d>();
        private readonly List<Point3d> _multiCloudSeconds = new List<Point3d>();

        private AnnotationPanel()
        {
            InitializeComponent();
            _s = SettingsStore.Load();
            // 下拉数据源
            DisciplineCombo.ItemsSource = new[] { "建筑", "结构", "给排水", "暖通", "电气", "道路", "桥梁", "隧道", "交通", "管线", "绿化", "景观", "岩土", "其他" };
            ShapeCombo.ItemsSource = new[] { "矩形", "菱形", "椭圆" };
            CloudStyleCombo.ItemsSource = new[] { "等宽", "渐变" };
            RoleCombo.ItemsSource = new[] { "批注人", "校审人", "回复人" };
            // 加载当前设置值
            RefreshControls();
            // 所有控件变更 → 即时写入 SettingsStore
            DisciplineCombo.LostFocus += (_, __) => SaveQuickSetting();
            DisciplineCombo.SelectionChanged += (_, __) => SaveQuickSetting();
            AuthorText.TextChanged += (_, __) => SaveQuickSetting();
            ShapeCombo.LostFocus += (_, __) => SaveQuickSetting();
            ShapeCombo.SelectionChanged += (_, __) => SaveQuickSetting();
            CloudStyleCombo.LostFocus += (_, __) => SaveQuickSetting();
            CloudStyleCombo.SelectionChanged += (_, __) => SaveQuickSetting();
            RoleCombo.LostFocus += (_, __) => SaveQuickSetting();
            RoleCombo.SelectionChanged += (_, __) => SaveQuickSetting();
            AutoNumberCheck.Checked += (_, __) => SaveQuickSetting();
            AutoNumberCheck.Unchecked += (_, __) => SaveQuickSetting();
            DoubleClickEditCheck.Checked += (_, __) => SaveQuickSetting();
            DoubleClickEditCheck.Unchecked += (_, __) => SaveQuickSetting();
            // 窗口定位到屏幕左上角，加载完毕后自动开始批注
            Loaded += (_, __) => { Left = SystemParameters.WorkArea.Left + 20; Top = SystemParameters.WorkArea.Top + 20; Dispatcher.BeginInvoke(new Action(() => StartAnnotation())); };
            Closing += (_, __) => _instance = null;
        }

        private void RefreshControls()
        {
            DisciplineCombo.Text = _s.DefaultDiscipline;
            AuthorText.Text = _s.DefaultAuthor;
            ShapeCombo.Text = _s.Shape;
            CloudStyleCombo.Text = _s.CloudStyle;
            RoleCombo.Text = _s.DefaultRole;
            AutoNumberCheck.IsChecked = _s.AutoNumber;
            DoubleClickEditCheck.IsChecked = _s.DoubleClickEdit;
        }

        /// <summary>将面板当前值写回设置并持久化。</summary>
        private void SaveQuickSetting()
        {
            _s.DefaultDiscipline = DisciplineCombo.Text.Trim();
            _s.DefaultAuthor = AuthorText.Text.Trim();
            _s.Shape = ShapeCombo.Text.Trim();
            _s.CloudStyle = CloudStyleCombo.Text.Trim();
            _s.DefaultRole = RoleCombo.Text.Trim();
            _s.AutoNumber = AutoNumberCheck.IsChecked == true;
            _s.DoubleClickEdit = DoubleClickEditCheck.IsChecked == true;
            SettingsStore.Save(_s);
        }

        /// <summary>显示或激活批注面板（单例）。</summary>
        public static void ShowOrActivate()
        {
            if (_instance != null && _instance.IsLoaded) { _instance.Activate(); return; }
            _instance = new AnnotationPanel();
            _instance.Show();
        }

        private void Start_Click(object sender, RoutedEventArgs e) => StartAnnotation();

        /// <summary>启动批注流程：读取面板当前模式，进入 CAD 交互。</summary>
        private void StartAnnotation()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) { Close(); return; }

            var isSingle = SingleModeRadio.IsChecked == true;
            var shape = ShapeRectRadio.IsChecked == true ? "矩形"
                      : ShapePlineRadio.IsChecked == true ? "pline"
                      : ShapeCircleRadio.IsChecked == true ? "圆形"
                      : "十字点";

            try
            {
                if (isSingle) DoSingleAnnotation(doc, shape);
                else DoMultiAnnotation(doc, shape);
            }
            catch (Exception ex) { doc.Editor.WriteMessage("\n批注失败: " + ex.Message); }
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            var styles = GetTextStyles(doc);
            var w = new SettingsWindow(_s, styles);
            if (CadDialog.ShowModal(w) == true) { _s = w.Value; RefreshControls(); }
        }

        // ═══════════════ 单独批注 ═══════════════

        private void DoSingleAnnotation(Document doc, string shape)
        {
            var data = new AnnotationData { Author = _s.DefaultAuthor, Discipline = _s.DefaultDiscipline };
            if (_s.AutoNumber) data.Number = "LA-" + _s.NextNumber.ToString("D3");

            switch (shape)
            {
                case "十字点":
                    DoCrossMark(doc); return;
                case "pline":
                    DoPlineCloud(doc, data); return;
                default:
                    DoRegionAnnotation(doc, data); return;
            }
        }

        private void DoCrossMark(Document doc)
        {
            var pt = doc.Editor.GetPoint("\n指定十字点位置: ");
            if (pt.Status != PromptStatus.OK) return;
            AnnotationService.CreateCrossMark(doc, _s, pt.Value);
            doc.Editor.WriteMessage("\n十字点已放置。");
            CheckContinuous(doc);
        }

        private void DoPlineCloud(Document doc, AnnotationData data)
        {
            var points = CollectPlinePoints(doc);
            if (points == null || points.Count < 2) return;
            var xs = points.Select(p => p.X); var ys = points.Select(p => p.Y);
            var w = xs.Max() - xs.Min(); var h = ys.Max() - ys.Min();
            var diagonal = Math.Sqrt(w * w + h * h);
            var effective = AnnotationService.ResolveEffectiveSettings(doc, _s, data, diagonal);

            // 仅绘云线模式：沿折线创建云线后即结束
            if (CloudOnlyCheck.IsChecked == true)
            {
                CreatePlineCloudOnly(doc, effective, points);
                doc.Editor.WriteMessage("\nPL 云线已创建。");
                CheckContinuous(doc);
                return;
            }
            // 完整 PL 批注
            var textPt = doc.Editor.GetPoint("\n指定批注文字位置: ");
            if (textPt.Status != PromptStatus.OK) return;
            if (effective.FontAutoFit) doc.Editor.WriteMessage($"\n云线范围: {w:0.#}×{h:0.#}  字高: {effective.TextHeight:0.###}  云线半径: {effective.CloudRadius:0.###}");
            var form = new AnnotationWindow(data, false);
            if (CadDialog.ShowModal(form) != true) return;
            AnnotationService.CreatePlineCloud(doc, data, _s, effective, points, textPt.Value);
            FinalizeAnnotation(doc, data);
            CheckContinuous(doc);
        }

        private void DoRegionAnnotation(Document doc, AnnotationData data)
        {
            var preview = AnnotationService.ResolveEffectiveSettings(doc, _s, data);
            if (!AnnotationService.PromptGeometry(doc, preview, out var first, out var second, out var textPt)) return;
            var w = Math.Abs(second.X - first.X); var h = Math.Abs(second.Y - first.Y);
            var diagonal = Math.Sqrt(w * w + h * h);
            var effective = AnnotationService.ResolveEffectiveSettings(doc, _s, data, diagonal);
            if (effective.FontAutoFit) doc.Editor.WriteMessage($"\n云线对角线: {diagonal:0.#}  字高: {effective.TextHeight:0.###}  云线半径: {effective.CloudRadius:0.###}");

            // 仅绘云线模式
            if (CloudOnlyCheck.IsChecked == true)
            {
                AnnotationService.CreateCloudOnly(doc, effective, first, second);
                doc.Editor.WriteMessage("\n云线已创建。");
                CheckContinuous(doc);
                return;
            }
            // 完整批注
            var form = new AnnotationWindow(data, false);
            if (CadDialog.ShowModal(form) != true) return;
            AnnotationService.Create(doc, data, effective, first, second, textPt);
            FinalizeAnnotation(doc, data);
            CheckContinuous(doc);
        }

        // ═══════════════ 多对一批注 ═══════════════

        private void DoMultiAnnotation(Document doc, string shape)
        {
            _multiCloudFirsts.Clear(); _multiCloudSeconds.Clear();
            var data = new AnnotationData { Author = _s.DefaultAuthor, Discipline = _s.DefaultDiscipline };
            if (_s.AutoNumber) data.Number = "LA-" + _s.NextNumber.ToString("D3");

            // 预览阶段用视图高度近似
            var preview = AnnotationService.ResolveEffectiveSettings(doc, _s, data);
            while (true)
            {
                if (!AnnotationService.PromptGeometry(doc, preview, out var first, out var second, out _)) break;
                _multiCloudFirsts.Add(first); _multiCloudSeconds.Add(second);
                doc.Editor.WriteMessage($"\n已添加第 {_multiCloudFirsts.Count} 个云线区域（回车继续，输入 N 完成）");
                var opts = new PromptKeywordOptions("\n[继续添加/完成选点] <继续>: ") { AllowNone = true };
                opts.Keywords.Add("Y", "继续", "继续(Y)");
                opts.Keywords.Add("N", "完成", "完成(N)");
                opts.Keywords.Default = "Y";
                var kw = doc.Editor.GetKeywords(opts);
                if (kw.Status == PromptStatus.OK && string.Equals(kw.StringResult, "N", StringComparison.OrdinalIgnoreCase)) break;
                if (kw.Status != PromptStatus.OK) break;
            }
            if (_multiCloudFirsts.Count == 0) return;

            // 用合并包围盒对角线重新计算
            var allX = _multiCloudFirsts.SelectMany((f, i) => new[] { f.X, _multiCloudSeconds[i].X });
            var allY = _multiCloudFirsts.SelectMany((f, i) => new[] { f.Y, _multiCloudSeconds[i].Y });
            var w = allX.Max() - allX.Min(); var h = allY.Max() - allY.Min();
            var diagonal = Math.Sqrt(w * w + h * h);

            var textPt = doc.Editor.GetPoint("\n指定批注文字位置: ");
            if (textPt.Status != PromptStatus.OK) return;
            var effective = AnnotationService.ResolveEffectiveSettings(doc, _s, data, diagonal);
            if (effective.FontAutoFit) doc.Editor.WriteMessage($"\n多区域范围: {w:0.#}×{h:0.#}  字高: {effective.TextHeight:0.###}");

            var form = new AnnotationWindow(data, false);
            if (CadDialog.ShowModal(form) != true) return;

            AnnotationService.CreateMultiCloud(doc, data, effective, _multiCloudFirsts, _multiCloudSeconds, textPt.Value);
            FinalizeAnnotation(doc, data);
            CheckContinuous(doc);
        }

        // ═══════════════ 工具方法 ═══════════════

        private static List<Point3d> CollectPlinePoints(Document doc)
        {
            var points = new List<Point3d>();
            var ed = doc.Editor;
            while (true)
            {
                var prompt = points.Count == 0 ? "\n指定 PL 线起点: " : $"\n指定下一点（已输入 {points.Count} 点，回车结束）: ";
                var opts = new PromptPointOptions(prompt) { AllowNone = true };
                var result = ed.GetPoint(opts);
                if (result.Status == PromptStatus.OK) { points.Add(result.Value); continue; }
                if (points.Count >= 2) break;
                return null;
            }
            return points;
        }

        private void FinalizeAnnotation(Document doc, AnnotationData data)
        {
            if (_s.AutoNumber) { _s.NextNumber++; SettingsStore.Save(_s); }
            doc.Editor.WriteMessage("\nLA批注已创建: " + data.Number);
        }

        /// <summary>仅沿折线创建云线（无文字/引线/边框）。</summary>
        private static void CreatePlineCloudOnly(Document doc, AnnotationSettings settings, List<Point3d> points)
        {
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var cloudPts = points.Select(p => new Point2d(p.X, p.Y)).ToList();
                var cloud = AnnotationService.BuildScallopedVertices(cloudPts, settings.CloudStyle);
                cloud.Elevation = points[0].Z;
                cloud.Layer = AnnotationService.EffectiveLayer(settings);
                cloud.Color = Color.FromColorIndex(ColorMethod.ByAci, settings.CloudColor);
                if (settings.LineWidth > 0) cloud.ConstantWidth = settings.LineWidth;
                var space = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
                space.AppendEntity(cloud); tr.AddNewlyCreatedDBObject(cloud, true);
                tr.Commit();
            }
        }

        private void CheckContinuous(Document doc)
        {
            if (ContinuousCheck.IsChecked == true) { Activate(); Dispatcher.BeginInvoke(new Action(() => StartAnnotation())); return; }
            Close();
        }

        private static IEnumerable<string> GetTextStyles(Document doc)
        {
            var styles = new List<string>();
            try
            {
                if (doc == null) return styles;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var table = (TextStyleTable)tr.GetObject(doc.Database.TextStyleTableId, OpenMode.ForRead);
                    foreach (ObjectId id in table)
                    {
                        if (id.IsValid && !id.IsErased && tr.GetObject(id, OpenMode.ForRead) is TextStyleTableRecord r && !string.IsNullOrWhiteSpace(r.Name))
                            styles.Add(r.Name);
                    }
                }
            }
            catch { }
            return styles;
        }
    }
}
