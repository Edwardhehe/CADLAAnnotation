using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
using CadApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif

namespace LAAnnotation.Views
{
    /// <summary>浮动批注面板：选择批注类型、形式和设置，点击开始时统一生效，支持连续批注。</summary>
    internal partial class AnnotationPanel : Window
    {
        private static AnnotationPanel _instance;
        private AnnotationSettings _s;
        private bool _isRunning;
        private bool _startPending;
        private Document _queuedDocument;
        private DateTime _pendingSinceUtc;
        private readonly List<Point3d> _multiCloudFirsts = new List<Point3d>();
        private readonly List<Point3d> _multiCloudSeconds = new List<Point3d>();
        private readonly List<ObjectId> _multiCloudIds = new List<ObjectId>();

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
            // 面板修改只保留为待应用值；点击“开始批注”时才统一写入设置。
            ShapeRectRadio.Checked += (_, __) => ShapeCombo.Text = "矩形";
            ShapeCircleRadio.Checked += (_, __) => ShapeCombo.Text = "椭圆";
            SingleModeRadio.Checked += (_, __) => UpdateModeAvailability();
            MultiModeRadio.Checked += (_, __) => UpdateModeAvailability();
            // 打开面板后先允许用户选择模式和参数，再由“开始批注”按钮进入 CAD 交互。
            Loaded += (_, __) => { Left = SystemParameters.WorkArea.Left + 20; Top = SystemParameters.WorkArea.Top + 20; };
            Closing += (_, __) => _instance = null;
            UpdateModeAvailability();
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
            ContinuousCheck.IsChecked = _s.ContinuousAnnotation;
            CloudOnlyCheck.IsChecked = _s.CloudOnly;
        }

        /// <summary>点击开始时，将面板当前值一次性写回设置并持久化。</summary>
        private void ApplyPanelSettings()
        {
            _s.DefaultDiscipline = DisciplineCombo.Text.Trim();
            _s.DefaultAuthor = AuthorText.Text.Trim();
            _s.Shape = ShapeCombo.Text.Trim();
            _s.CloudStyle = CloudStyleCombo.Text.Trim();
            _s.DefaultRole = RoleCombo.Text.Trim();
            _s.AutoNumber = AutoNumberCheck.IsChecked == true;
            _s.DoubleClickEdit = DoubleClickEditCheck.IsChecked == true;
            _s.ContinuousAnnotation = ContinuousCheck.IsChecked == true;
            _s.CloudOnly = CloudOnlyCheck.IsChecked == true;
            SettingsStore.Save(_s);
        }

        /// <summary>显示或激活批注面板（单例）。</summary>
        public static void ShowOrActivate()
        {
            if (_instance != null && _instance.IsLoaded) { _instance.Activate(); return; }
            _instance = new AnnotationPanel();
            _instance.Show();
        }

        private void Start_Click(object sender, RoutedEventArgs e) => QueueAnnotationCommand(true, false);

        /// <summary>由 CAD 内部命令调用，确保所有 Editor 选点都运行在正式命令上下文。</summary>
        internal static void RunQueuedAnnotation()
        {
            var panel=_instance;
            if(panel==null||!panel.IsLoaded||!panel._startPending)return;
            var queuedDocument=panel._queuedDocument;
            if(queuedDocument==null||queuedDocument.IsDisposed){panel.ResetPendingStart();return;}
            // 旧图纸队列中的延迟命令不能消费另一张图纸的新请求。
            if(CadApplication.DocumentManager.MdiActiveDocument!=queuedDocument)return;
            panel.ResetPendingStart();
            panel.StartAnnotation(queuedDocument);
        }

        /// <summary>切换图纸时取消旧图纸尚未执行的面板请求，避免跨图绘制。</summary>
        internal static void HandleDocumentActivated(Document activeDocument)
        {
            var panel=_instance;
            if(panel==null||!panel.IsLoaded||!panel._startPending)return;
            if(panel._queuedDocument!=activeDocument)panel.ResetPendingStart();
        }

        private void ResetPendingStart()
        {
            _startPending=false;_queuedDocument=null;_pendingSinceUtc=default(DateTime);
            if(IsLoaded){StartButton.IsEnabled=true;StartButton.Content="开始批注";}
        }

        private void QueueAnnotationCommand(bool applyPanelSettings,bool fromContinuous)
        {
            if(_isRunning&&!fromContinuous)return;
            try
            {
                if(applyPanelSettings)ApplyPanelSettings();
                var doc=CadApplication.DocumentManager.MdiActiveDocument;
                if(doc==null){Close();return;}
                if(_startPending&&_queuedDocument==doc&&(DateTime.UtcNow-_pendingSinceUtc).TotalSeconds<2)return;
                _queuedDocument=doc;_startPending=true;_pendingSinceUtc=DateTime.UtcNow;
                StartButton.IsEnabled=!_isRunning;StartButton.Content="等待 CAD…（可重试）";
                doc.SendStringToExecute("LA_PZ_RUN ",true,false,false);
            }
            catch(Exception ex)
            {
                ResetPendingStart();
                var doc=CadApplication.DocumentManager.MdiActiveDocument;
                doc?.Editor.WriteMessage("\n无法启动批注命令: "+ex.Message);
            }
        }

        /// <summary>启动批注流程：读取面板当前模式，进入 CAD 交互。</summary>
        private void StartAnnotation(Document doc)
        {
            if (_isRunning) return;
            _isRunning = true;StartButton.IsEnabled=false;
            try
            {
                if(doc==null||doc.IsDisposed||CadApplication.DocumentManager.MdiActiveDocument!=doc)return;

                var isSingle = SingleModeRadio.IsChecked == true;
                var shape = ShapeRectRadio.IsChecked == true ? "矩形"
                          : ShapePlineRadio.IsChecked == true ? "pline"
                          : ShapeCircleRadio.IsChecked == true ? "圆形"
                          : "十字点";

                if (isSingle) DoSingleAnnotation(doc, shape);
                else DoMultiAnnotation(doc);
            }
            catch (Exception ex) { var d = CadApplication.DocumentManager.MdiActiveDocument; d?.Editor.WriteMessage("\n批注失败: " + ex.Message); }
            finally { _isRunning = false;if(IsLoaded)StartButton.IsEnabled=true; }
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
            var data = new AnnotationData { Author = _s.DefaultAuthor, Discipline = _s.DefaultDiscipline, Role = _s.DefaultRole };
            if (_s.AutoNumber) { _s.NextNumber = AnnotationService.GetNextNumber(doc); data.Number = "LA-" + _s.NextNumber.ToString("D3"); }

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
            var pointWcs=pt.Value.TransformBy(AnnotationService.GetUcsMatrix(doc));
            AnnotationService.CreateCrossMark(doc, _s, pointWcs);
            doc.Editor.WriteMessage("\n十字点已放置。");
            CheckContinuous(doc);
        }

        private void DoPlineCloud(Document doc, AnnotationData data)
        {
            var points = CollectPlinePoints(doc, out var cancelled);
            if (cancelled || points == null) return;
            var ucsMatrix=AnnotationService.GetUcsMatrix(doc);var wcsToUcs=ucsMatrix.Inverse();var localPoints=points.Select(p=>p.TransformBy(wcsToUcs)).ToList();
            var polygonPoints=localPoints.Select(p=>new Point2d(p.X,p.Y)).ToList();
            if(!AnnotationService.ValidateCloudPolygon(polygonPoints,out var validationError)){doc.Editor.WriteMessage("\n"+validationError);return;}
            var xs=localPoints.Select(p=>p.X);var ys=localPoints.Select(p=>p.Y);
            var w = xs.Max() - xs.Min(); var h = ys.Max() - ys.Min();
            var diagonal = Math.Sqrt(w * w + h * h);
            var effective = AnnotationService.ResolveEffectiveSettings(doc, _s, data, diagonal);

            // 仅绘云线模式：沿折线创建云线后即结束
            if (_s.CloudOnly)
            {
                AnnotationService.CreatePlineCloudOnly(doc,effective,points);
                doc.Editor.WriteMessage("\nPL 云线已创建。");
                CheckContinuous(doc);
                return;
            }
            // 完整 PL 批注
            var textPt = doc.Editor.GetPoint("\n指定批注文字位置: ");
            if (textPt.Status != PromptStatus.OK) return;
            var textPointWcs=textPt.Value.TransformBy(AnnotationService.GetUcsMatrix(doc));
            if (effective.FontAutoFit) doc.Editor.WriteMessage($"\n云线范围: {w:0.#}×{h:0.#}  字高: {effective.TextHeight:0.###}  云线半径: {effective.CloudRadius:0.###}");
            var form = new AnnotationWindow(data, false);
            if (CadDialog.ShowModal(form) != true) return;
            AnnotationService.CreatePlineCloud(doc, data, effective, points, textPointWcs);
            FinalizeAnnotation(doc, data);
            CheckContinuous(doc);
        }

        private void DoRegionAnnotation(Document doc, AnnotationData data)
        {
            var preview = AnnotationService.ResolveEffectiveSettings(doc, _s, data);

            // 仅绘云线：跳过文字位置选点，直接画云线
            if (_s.CloudOnly)
            {
                if (!AnnotationService.PromptCloudOnly(doc, preview, out var first, out var second)) return;
                var (_,w,h)=AnnotationService.UcsAlignedExtents(doc,first,second);
                var diagonal = Math.Sqrt(w * w + h * h);
                var effective = AnnotationService.ResolveEffectiveSettings(doc, _s, data, diagonal);
                if (effective.FontAutoFit) doc.Editor.WriteMessage($"\n云线对角线: {diagonal:0.#}  云线半径: {effective.CloudRadius:0.###}");
                AnnotationService.CreateCloudOnly(doc, effective, first, second);
                doc.Editor.WriteMessage("\n云线已创建。");
                CheckContinuous(doc);
                return;
            }
            // 完整批注：选云线范围 → 选文字位置 → 填表 → 创建实体组
            if (!AnnotationService.PromptGeometry(doc, preview, out var f, out var s, out var textPt)) return;
            var (_,bw,bh)=AnnotationService.UcsAlignedExtents(doc,f,s);
            var bdiagonal = Math.Sqrt(bw * bw + bh * bh);
            var beffective = AnnotationService.ResolveEffectiveSettings(doc, _s, data, bdiagonal);
            if (beffective.FontAutoFit) doc.Editor.WriteMessage($"\n云线对角线: {bdiagonal:0.#}  字高: {beffective.TextHeight:0.###}  云线半径: {beffective.CloudRadius:0.###}");
            var form = new AnnotationWindow(data, false);
            if (CadDialog.ShowModal(form) != true) return;
            AnnotationService.Create(doc, data, beffective, f, s, textPt);
            FinalizeAnnotation(doc, data);
            CheckContinuous(doc);
        }

        // ═══════════════ 多对一批注 ═══════════════

        private void DoMultiAnnotation(Document doc)
        {
            _multiCloudFirsts.Clear(); _multiCloudSeconds.Clear(); _multiCloudIds.Clear();
            var data = new AnnotationData { Author = _s.DefaultAuthor, Discipline = _s.DefaultDiscipline, Role = _s.DefaultRole };
            if (_s.AutoNumber) { _s.NextNumber = AnnotationService.GetNextNumber(doc); data.Number = "LA-" + _s.NextNumber.ToString("D3"); }

            var preview = AnnotationService.ResolveEffectiveSettings(doc, _s, data);
            doc.Editor.WriteMessage("\n多对一批注：连续绘制云线；在下一个云线的第一个角点提示时按回车或空格进入文字。");
            var completed = false;
            try
            {
                while (true)
                {
                    var promptResult=AnnotationService.PromptCloudOrFinish(doc,preview,out var first,out var second);
                    if(promptResult==CloudPromptResult.Finished)break;
                    if(promptResult==CloudPromptResult.Cancelled)return;
                    _multiCloudFirsts.Add(first); _multiCloudSeconds.Add(second);
                    var id = AnnotationService.CreateCloudOnly(doc, preview, first, second);
                    _multiCloudIds.Add(id);
                    doc.Editor.WriteMessage($"\n已添加第 {_multiCloudFirsts.Count} 个云线；继续指定下一条，或按回车/空格填写文字。");
                }
                if(_multiCloudFirsts.Count==0)return;

                var diagonals = _multiCloudFirsts.Select((f, i) =>
                {
                    var (_,cw,ch)=AnnotationService.UcsAlignedExtents(doc,f,_multiCloudSeconds[i]);
                    return Math.Sqrt(cw*cw+ch*ch);
                }).OrderBy(x=>x).ToList();
                var middle=diagonals.Count/2;
                var representativeDiagonal=diagonals.Count%2==1?diagonals[middle]:(diagonals[middle-1]+diagonals[middle])/2.0;

                var textPt = doc.Editor.GetPoint("\n指定批注文字位置: ");
                if (textPt.Status != PromptStatus.OK) return;
                var textPointWcs=textPt.Value.TransformBy(AnnotationService.GetUcsMatrix(doc));
                var effective = AnnotationService.ResolveEffectiveSettings(doc, _s, data, representativeDiagonal);
                if (effective.FontAutoFit||effective.CloudAutoFit) doc.Editor.WriteMessage($"\n代表云线尺寸: {representativeDiagonal:0.#}  字高: {effective.TextHeight:0.###}  云线半径和线宽按各云线自身尺寸计算。");

                var form = new AnnotationWindow(data, false);
                if (CadDialog.ShowModal(form) != true) return;

                AnnotationService.CreateMultiCloud(doc, data, effective, _s, _multiCloudIds, _multiCloudFirsts, _multiCloudSeconds, textPointWcs);
                completed = true;
                FinalizeAnnotation(doc, data);
                CheckContinuous(doc);
            }
            finally
            {
                if (!completed) AnnotationService.EraseTemporaryEntities(doc, _multiCloudIds);
                _multiCloudIds.Clear(); _multiCloudFirsts.Clear(); _multiCloudSeconds.Clear();
            }
        }

        // ═══════════════ 工具方法 ═══════════════

        private static List<Point3d> CollectPlinePoints(Document doc,out bool cancelled)
        {
            cancelled=false;
            var points = new List<Point3d>();
            var ed = doc.Editor;
            while (true)
            {
                var prompt = points.Count == 0 ? "\n指定 PL 云线起点: " : $"\n指定下一点（已输入 {points.Count} 点，至少 3 点后回车结束）: ";
                var opts = new PromptPointOptions(prompt) { AllowNone = true };
                if(points.Count>0){opts.UseBasePoint=true;opts.BasePoint=points[points.Count-1];}
                var result = ed.GetPoint(opts);
                if(result.Status==PromptStatus.OK)
                {
                    var pointWcs=result.Value.TransformBy(AnnotationService.GetUcsMatrix(doc));
                    if(points.Count>0&&points[points.Count-1].DistanceTo(pointWcs)<=1e-8){ed.WriteMessage("\n该点与上一点重复，请重新指定。");continue;}
                    points.Add(pointWcs);continue;
                }
                if(result.Status==PromptStatus.None)
                {
                    if(points.Count>=3)break;
                    ed.WriteMessage("\nPL 云线至少需要三个不同的点。");continue;
                }
                cancelled=true;return null;
            }
            return points;
        }

        private void FinalizeAnnotation(Document doc, AnnotationData data)
        {
            if (_s.AutoNumber) { _s.NextNumber++; SettingsStore.Save(_s); }
            doc.Editor.WriteMessage("\nLA批注已创建: " + data.Number);
            AnnotationListPanel.RefreshIfOpen();
        }

        private void UpdateModeAvailability()
        {
            var regionOnly = MultiModeRadio.IsChecked == true;
            ShapePlineRadio.IsEnabled = !regionOnly;
            ShapeCrossRadio.IsEnabled = !regionOnly;
            if (regionOnly && (ShapePlineRadio.IsChecked == true || ShapeCrossRadio.IsChecked == true))
                ShapeRectRadio.IsChecked = true;
        }

        private void CheckContinuous(Document doc)
        {
            if (_s.ContinuousAnnotation) { QueueAnnotationCommand(false,true); return; }
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
