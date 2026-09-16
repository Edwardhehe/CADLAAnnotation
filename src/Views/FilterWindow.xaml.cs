using System;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
#if ZWCAD
using CadApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
#else
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif

namespace GMAnnotation.Views
{
    /// <summary>批注过滤窗口：按「批注内容完全一致」把全图批注分组列出，
    /// 选中一行即只显示这一组、把其它内容的批注隐藏起来，方便逐条核对同一类问题。
    ///
    /// <para>只切实体可见性（<see cref="AnnotationService.FilterByContent"/> → <c>Entity.Visible</c>），
    /// 不删数据、不改图层，随时可以「显示全部」恢复。</para>
    /// <para>非模态窗口（与批注列表面板一致）：开着窗口就能继续在图上缩放查看；窗口内的操作自己 LockDocument。</para>
    /// </summary>
    internal partial class FilterWindow : Window
    {
        private static FilterWindow _instance;

        public FilterWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => { WindowSizing.FitToWorkArea(this); LoadGroups(null); };
            Closing += (s, e) => _instance = null;
        }

        /// <summary>显示或激活过滤窗口。<paramref name="contentKey"/> 非空时自动选中该内容所在行
        /// （命令层预选了某条批注时会走这条路径）。</summary>
        public static void ShowOrActivate(string contentKey = null)
        {
            if (_instance != null && _instance.IsLoaded) { _instance.Activate(); _instance.LoadGroups(contentKey); return; }
            _instance = new FilterWindow();
            _instance.Show();
            _instance.LoadGroups(contentKey);
        }

        /// <summary>窗口开着时刷新数据（合并/编辑/删除批注之后调用）。</summary>
        public static void RefreshIfOpen()
        {
            if (_instance != null && _instance.IsLoaded) _instance.LoadGroups(null, true);
        }

        /// <summary>重新统计分组。<paramref name="keepSelection"/> 为真时尽量保留当前选中行。</summary>
        private void LoadGroups(string selectKey, bool keepSelection = false)
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) { CountText.Text = "无打开的 CAD 文档"; GroupList.ItemsSource = null; return; }
            try
            {
                var groups = AnnotationService.GetContentGroups(doc);
                GroupList.ItemsSource = groups;
                CountText.Text = "共 " + groups.Count + " 种内容 / " + groups.Sum(g => g.Count) + " 条批注";
                var key = selectKey;
                if (key == null && keepSelection && GroupList.SelectedItem is AnnotationService.ContentGroup current) key = current.Key;
                if (key != null)
                {
                    var row = groups.FirstOrDefault(g => g.Key == key);
                    if (row != null) { GroupList.SelectedItem = row; GroupList.ScrollIntoView(row); }
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error("FilterWindow.LoadGroups", ex);
                CountText.Text = "读取批注失败：" + ex.Message;
            }
        }

        private void Apply_Click(object sender, RoutedEventArgs e) => ApplySelected();

        /// <summary>双击某行 = 只显示这一组（与「仅显示所选」等价）。</summary>
        private void List_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            var row = ResolveRow(e.OriginalSource as DependencyObject);
            if (row != null) GroupList.SelectedItem = row; else return; // 双击空白处不误用上一次的选中行
            ApplySelected();
        }

        private void ApplySelected()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) { MessageBox.Show(this, "当前没有打开的 CAD 图纸。", "批注过滤", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            var row = GroupList.SelectedItem as AnnotationService.ContentGroup;
            if (row == null) { MessageBox.Show(this, "请先在列表里选择一种批注内容。", "批注过滤", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            try
            {
                doc.Editor.WriteMessage("\n" + AnnotationService.FilterByContent(doc, row.Key));
            }
            catch (Exception ex)
            {
                PluginLog.Error("FilterWindow.Apply", ex);
                MessageBox.Show(this, "过滤失败: " + ex.Message, "批注过滤", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ShowAll_Click(object sender, RoutedEventArgs e)
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) { MessageBox.Show(this, "当前没有打开的 CAD 图纸。", "批注过滤", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            try
            {
                doc.Editor.WriteMessage("\n" + AnnotationService.FilterByContent(doc, null));
            }
            catch (Exception ex)
            {
                PluginLog.Error("FilterWindow.ShowAll", ex);
                MessageBox.Show(this, "显示全部失败: " + ex.Message, "批注过滤", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>窗口里也能直接合并（走与 GM_PZ_MERGE 相同的确认 + 服务方法）。</summary>
        private void Merge_Click(object sender, RoutedEventArgs e)
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) { MessageBox.Show(this, "当前没有打开的 CAD 图纸。", "合并批注", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            if (!MergePrompt.Confirm(this)) return;
            try
            {
                doc.Editor.WriteMessage("\n" + AnnotationService.MergeDuplicates(doc));
                LoadGroups(null);
            }
            catch (Exception ex)
            {
                PluginLog.Error("FilterWindow.Merge", ex);
                MessageBox.Show(this, "合并失败: " + ex.Message, "合并批注", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            AnnotationListPanel.RefreshIfOpen();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => LoadGroups(null, true);

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>从双击命中的可视化树向上找到对应的列表项数据（空白处返回 null）。</summary>
        private static AnnotationService.ContentGroup ResolveRow(DependencyObject source)
        {
            while (source != null)
            {
                if (source is System.Windows.Controls.ListViewItem item) return item.DataContext as AnnotationService.ContentGroup;
                try { source = VisualTreeHelper.GetParent(source); }
                catch { return null; }
            }
            return null;
        }
    }
}
