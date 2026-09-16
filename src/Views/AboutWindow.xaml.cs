using System;
using System.Windows;

namespace GMAnnotation.Views
{
    /// <summary>
    /// 「关于 GM批注」对话框：展示产品名、程序集版本与固定联系信息。
    /// </summary>
    internal partial class AboutWindow : Window
    {
        /// <summary>
        /// 初始化关于框，填充产品名、版本与联系信息。
        /// </summary>
        public AboutWindow()
        {
            InitializeComponent();
            Title = "关于 " + AppInfo.ProductName;
            ProductNameText.Text = AppInfo.ProductName;
            VersionText.Text = AppInfo.GetDisplayVersion();
            DesignerText.Text = AppInfo.Designer;
            EmailText.Text = AppInfo.Email;
            QqGroupText.Text = AppInfo.QqGroup;
        }

        /// <summary>
        /// 关闭对话框。
        /// </summary>
        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        /// <summary>
        /// 将 QQ 群号复制到剪贴板；失败时弹窗直接显示群号。
        /// </summary>
        private void CopyQq_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(AppInfo.QqGroup);
                MessageBox.Show(
                    this,
                    "QQ群号 " + AppInfo.QqGroup + " 已复制到剪贴板。",
                    AppInfo.ProductName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception)
            {
                MessageBox.Show(
                    this,
                    "复制失败，请手动记录 QQ 群号：\n" + AppInfo.QqGroup,
                    AppInfo.ProductName,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }
}
