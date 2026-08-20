using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
#if ZWCAD
using CadApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
#else
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif

namespace LAAnnotation.Views
{
    /// <summary>批注列表面板：左侧停靠，展示当前 DWG 中所有批注，双击定位。</summary>
    internal partial class AnnotationListPanel : Window
    {
        private static AnnotationListPanel _instance;

        private AnnotationListPanel()
        {
            InitializeComponent();
            Loaded += (_, __) =>
            {
                Left = SystemParameters.WorkArea.Left + 20;
                Top = SystemParameters.WorkArea.Top + 60;
            };
            Closing += (_, __) => _instance = null;
        }

        /// <summary>显示或激活批注列表面板（单例），并自动刷新数据。</summary>
        public static void ShowOrActivate()
        {
            if (_instance != null && _instance.IsLoaded) { _instance.Activate(); _instance.LoadAnnotations(); return; }
            _instance = new AnnotationListPanel();
            _instance.Show();
            _instance.LoadAnnotations();
        }

        /// <summary>若面板已打开则刷新数据。</summary>
        public static void RefreshIfOpen()
        {
            if (_instance != null && _instance.IsLoaded) _instance.LoadAnnotations();
        }

        private void LoadAnnotations()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) { CountText.Text = "无打开的 CAD 文档"; AnnotationList.ItemsSource = null; return; }
            var list = AnnotationService.GetAllAnnotations(doc);
            AnnotationList.ItemsSource = list;
            CountText.Text = $"共 {list.Count} 条批注";
        }

        /// <summary>双击列表行：缩放到对应批注并高亮选中。</summary>
        private void List_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            var info = ResolveClickedInfo(e.OriginalSource as DependencyObject)
                ?? AnnotationList.SelectedItem as AnnotationService.AnnotationInfo;
            if (info == null)
            {
                return;
            }

            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            if (info.FirstEntityId.IsNull || !info.FirstEntityId.IsValid || info.FirstEntityId.IsErased)
            {
                doc.Editor.WriteMessage("\n定位失败：批注实体已不存在，请刷新列表。");
                return;
            }

            if (!AnnotationService.ZoomToAnnotation(doc, info.FirstEntityId))
            {
                doc.Editor.WriteMessage($"\n定位批注 {info.Number} 失败。");
            }
        }

        /// <summary>从双击命中的可视化树向上找到对应的列表项数据。</summary>
        private static AnnotationService.AnnotationInfo ResolveClickedInfo(DependencyObject source)
        {
            while (source != null)
            {
                if (source is ListViewItem item)
                {
                    return item.DataContext as AnnotationService.AnnotationInfo;
                }

                source = VisualTreeHelper.GetParent(source);
            }

            return null;
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var info = AnnotationList.SelectedItem
                as AnnotationService.AnnotationInfo;
            if (info == null)
            {
                return;
            }

            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            var result = MessageBox.Show(
                $"确定要删除批注 {info.Number} 吗？",
                "LA批注",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                if (!AnnotationService.Delete(doc, info.FirstEntityId))
                {
                    doc.Editor.WriteMessage(
                        "\n删除失败：批注实体已不存在。");
                }
                else
                {
                    doc.Editor.WriteMessage(
                        $"\nLA批注 {info.Number} 已删除。");
                }
            }
            catch (System.Exception ex)
            {
                doc.Editor.WriteMessage(
                    "\n删除批注失败: " + ex.Message);
            }

            LoadAnnotations();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => LoadAnnotations();
    }
}
