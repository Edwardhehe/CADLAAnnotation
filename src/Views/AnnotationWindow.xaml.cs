using System;
using System.Windows;

namespace LAAnnotation.Views
{
    internal partial class AnnotationWindow : Window
    {
        public AnnotationData Value{get;}
        public AnnotationWindow(AnnotationData value,bool editing)
        {
            InitializeComponent();Value=value;Title=editing?"编辑批注 · "+value.Number:"新建 LA批注";ConfirmButton.Content=editing?"更新批注":"创建批注";
            DisciplineComboBox.ItemsSource=new[]{"建筑","结构","给排水","暖通","电气","道路","桥梁","隧道","交通","管线","绿化","景观","岩土","其他"};RoleComboBox.ItemsSource=new[]{"批注人","校审人","回复人"};StatusComboBox.ItemsSource=new[]{"待处理","已回复","已完成"};ContentTextBox.Text=value.Content;DisciplineComboBox.Text=value.Discipline;AuthorTextBox.Text=value.Author;RoleComboBox.Text=value.Role;AnnotationDatePicker.SelectedDate=DateTime.TryParse(value.Date,out var d)?d:DateTime.Today;StatusComboBox.Text=value.Status;NumberTextBox.Text=value.Number;Loaded+=(s,e)=>{WindowSizing.FitToWorkArea(this);ContentTextBox.Focus();ContentTextBox.CaretIndex=ContentTextBox.Text.Length;};
        }
        private void Confirm_Click(object sender,RoutedEventArgs e){if(string.IsNullOrWhiteSpace(ContentTextBox.Text)){MessageBox.Show(this,"请输入批注内容。","LA批注",MessageBoxButton.OK,MessageBoxImage.Information);ContentTextBox.Focus();return;}if(string.IsNullOrWhiteSpace(NumberTextBox.Text)){MessageBox.Show(this,"批注编号不能为空。","LA批注",MessageBoxButton.OK,MessageBoxImage.Information);return;}Value.Content=ContentTextBox.Text.Trim();Value.Discipline=DisciplineComboBox.Text.Trim();Value.Author=AuthorTextBox.Text.Trim();Value.Role=string.IsNullOrWhiteSpace(RoleComboBox.Text)?"批注人":RoleComboBox.Text.Trim();Value.Date=(AnnotationDatePicker.SelectedDate??DateTime.Today).ToString("yyyy-MM-dd");Value.Status=string.IsNullOrWhiteSpace(StatusComboBox.Text)?"待处理":StatusComboBox.Text.Trim();Value.Number=NumberTextBox.Text.Trim();DialogResult=true;}
        private void Cancel_Click(object sender,RoutedEventArgs e){DialogResult=false;}
    }
}
