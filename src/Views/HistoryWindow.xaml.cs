using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace GMAnnotation.Views
{
    /// <summary>批注历史留痕查询窗口：关键字过滤 + 仅当前图 + 导出 CSV。</summary>
    internal partial class HistoryWindow : Window
    {
        /// <summary>DataGrid 行模型：留痕记录 + 绑定用展示字段。</summary>
        private sealed class HistoryRow
        {
            public string TimeString { get; set; }
            public string Action { get; set; }
            public string Drawing { get; set; }
            public string DrawingNo { get; set; }
            public string Number { get; set; }
            public string Date { get; set; }
            public string Discipline { get; set; }
            public string Author { get; set; }
            public string Status { get; set; }
            public string Content { get; set; }
            public string Changes { get; set; }
            public string User { get; set; }

            public static HistoryRow From(AnnotationHistoryRecord r) => new HistoryRow
            {
                TimeString = r.Time.ToString("yyyy-MM-dd HH:mm:ss"),
                Action = r.Action,
                Drawing = r.Drawing,
                DrawingNo = r.DrawingNo,
                Number = r.Number,
                Date = r.Date,
                Discipline = r.Discipline,
                Author = r.Author,
                Status = r.Status,
                Content = r.Content,
                Changes = r.Changes,
                User = r.User
            };
        }

        private List<AnnotationHistoryRecord> _all = new List<AnnotationHistoryRecord>();
        private string _currentDrawing = "";

        /// <summary>视觉树是否已加载完成。XAML 里 <c>CurrentDrawingOnly</c> 带 IsChecked="True" + Checked/Unchecked
        /// 处理器，InitializeComponent 期间就会触发一次过滤；那时 HistoryGrid/CountText 等控件还没建出来。
        /// 光靠下面 ApplyFilter 里的 null 兜底能挡得住空引用，但"构造函数没跑完就执行界面逻辑"本身是隐患，
        /// 所以这里加一道闸：构造完成前一律不刷新列表（日志里曾有 HistoryWindow..ctor → ApplyFilter 的空引用崩溃）。</summary>
        private bool _initialized;

        /// <summary><paramref name="currentDrawing"/> 为当前 DWG 文件名（不含路径），由命令层传入；空则不按图名过滤。</summary>
        public HistoryWindow(string currentDrawing = null)
        {
            InitializeComponent();
            Loaded += (s, e) => WindowSizing.FitToWorkArea(this);
            _currentDrawing = currentDrawing ?? "";
            // ?. 兜底：万一 XAML/BAML 与代码不匹配导致某个 x:Name 未连上，只是退化为"不按图过滤"，不抛空引用。
            if (CurrentDrawingOnly != null && string.IsNullOrEmpty(_currentDrawing)) CurrentDrawingOnly.IsChecked = false;
            _initialized = true;
            try { Reload(); }
            catch (Exception ex) { PluginLog.Error("HistoryWindow.Reload", ex); }
        }

        private static HistoryWindow _open;
        private static readonly object _openLock = new object();

        /// <summary>
        /// 以"非模态"方式打开批注历史记录窗口。
        /// 说明：知识库与批注历史记录拆分后，批注窗口内的按钮指向的是知识库（KnowledgeWindow）；
        /// 本入口目前由外部命令（GM_PZ_HISTORY，模态）承担，保留此处以便将来在批注窗口内直接查留痕。
        /// 批注窗口本身已经是 CAD 的模态窗口（AutoCAD ShowModalWindow），在它里面再调用 CAD 的
        /// ShowModalWindow 属于嵌套模态，是最容易出异常、也最难复现的路径；这里改为打开一个挂在
        /// 批注窗口下的非模态窗口——既能随时查历史，又不干扰继续填写批注。
        /// </summary>
        public static void ShowModeless(Window owner, string currentDrawing)
        {
            lock (_openLock)
            {
                if (_open != null)
                {
                    try { _open.Reload(); _open.Activate(); return; }
                    catch (Exception ex) { PluginLog.Warning("HistoryWindow.Reuse", ex.Message); _open = null; }
                }
                var window = new HistoryWindow(currentDrawing);
                _open = window;
                window.Closed += (s, e) => { lock (_openLock) { if (ReferenceEquals(_open, window)) _open = null; } };
                if (owner != null) window.Owner = owner;
                window.Show();
                window.Activate();
            }
        }

        private void Reload()
        {
            _all = AnnotationHistoryStore.LoadAll();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (!_initialized) return;
            var list = FilteredRecords().OrderByDescending(r => r.Time).ToList();
            if (HistoryGrid != null) HistoryGrid.ItemsSource = list.Select(HistoryRow.From).ToList();
            if (CountText != null) CountText.Text = "共 " + list.Count + " 条留痕（全部 " + (_all?.Count ?? 0) + " 条）";
        }

        /// <summary>
        /// 按当前过滤条件（关键字 + 仅当前图）筛出留痕记录。
        /// 列表显示、导出 CSV、清理三处共用这一个入口，避免过滤条件写成三份、改一处漏两处。
        /// 控件引用全部做 null 兜底：即便 XAML/BAML 与代码不匹配，也只退化为"不过滤"，绝不抛空引用。
        /// </summary>
        private List<AnnotationHistoryRecord> FilteredRecords()
        {
            if (_all == null) _all = new List<AnnotationHistoryRecord>();
            var keyword = (FilterBox?.Text ?? "").Trim();
            IEnumerable<AnnotationHistoryRecord> rows = _all;
            if (CurrentDrawingOnly?.IsChecked == true && !string.IsNullOrEmpty(_currentDrawing))
                rows = rows.Where(r => string.Equals(r.Drawing, _currentDrawing, StringComparison.OrdinalIgnoreCase));
            if (keyword.Length > 0)
                rows = rows.Where(r =>
                    (r.Drawing ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (r.DrawingNo ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (r.Number ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (r.Status ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (r.Author ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (r.User ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (r.Content ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    (r.Changes ?? "").IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
            return rows.ToList();
        }

        private void Filter_TextChanged(object sender, RoutedEventArgs e) => ApplyFilter();
        private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();
        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Title = "导出批注历史记录",
                    Filter = "CSV 文件 (*.csv)|*.csv",
                    FileName = "批注历史_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv"
                };
                if (dialog.ShowDialog(this) != true) return;
                var records = FilteredRecords();
                if (records.Count == 0) { MessageBox.Show(this, "当前没有可导出的留痕记录。", "批注历史"); return; }
                AnnotationHistoryStore.ExportCsv(dialog.FileName, records.OrderByDescending(r => r.Time));
                MessageBox.Show(this, "已导出 " + records.Count + " 条留痕到:\n" + dialog.FileName, "批注历史");
            }
            catch (Exception ex) { MessageBox.Show(this, "导出失败: " + ex.Message, "批注历史"); }
        }

        /// <summary>
        /// 清理：删除当前列表中的留痕记录（按当前过滤条件，与列表所见一致），清理前自动备份。
        /// 不过滤时即"清空全部"；配合关键字或"仅当前图"可只清理指定范围。
        /// </summary>
        private void Cleanup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var records = FilteredRecords();
                if (records.Count == 0) { MessageBox.Show(this, "当前列表没有可清理的留痕记录。", "批注历史"); return; }
                var scope = "";
                if (CurrentDrawingOnly?.IsChecked == true && !string.IsNullOrEmpty(_currentDrawing)) scope += "，仅当前图 " + _currentDrawing;
                var keyword = (FilterBox?.Text ?? "").Trim();
                if (keyword.Length > 0) scope += "，关键字「" + keyword + "」";
                var answer = MessageBox.Show(this,
                    "将清理当前列表中的 " + records.Count + " 条留痕" + scope + "。\n\n" +
                    "清理前会自动备份一次原始记录到：\n" + AnnotationHistoryStore.BackupPath + "\n\n" +
                    "该操作不可撤销（清理后剩余 " + ((_all?.Count ?? 0) - records.Count) + " 条），是否继续？",
                    "清理批注历史", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.OK) return;
                var removed = AnnotationHistoryStore.Cleanup(records);
                if (removed < 0)
                {
                    MessageBox.Show(this, "清理失败，详见日志：%AppData%\\GMAnnotation\\Logs\\GMAnnotation.log", "批注历史");
                    return;
                }
                Reload();
                MessageBox.Show(this, "已清理 " + removed + " 条留痕，剩余 " + (_all?.Count ?? 0) + " 条。", "批注历史");
            }
            catch (Exception ex) { MessageBox.Show(this, "清理失败: " + ex.Message, "批注历史"); }
        }
    }
}
