using System.Linq;
using System.Windows;
using System.Windows.Input;
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

        private void LoadAnnotations()
        {
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) { CountText.Text = "无打开的 CAD 文档"; AnnotationList.ItemsSource = null; return; }
            var list = AnnotationService.GetAllAnnotations(doc);
            AnnotationList.ItemsSource = list;
            CountText.Text = $"共 {list.Count} 条批注";
        }

        private void List_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(AnnotationList.SelectedItem is AnnotationService.AnnotationInfo info)) return;
            var doc = CadApplication.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            if (info.FirstEntityId.IsValid) AnnotationService.ZoomToAnnotation(doc, info.FirstEntityId);
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => LoadAnnotations();
    }
}
