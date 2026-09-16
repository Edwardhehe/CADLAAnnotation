using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using CadApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif

namespace GMAnnotation.Views
{
    internal partial class AnnotationWindow : Window
    {
        public AnnotationData Value{get;}
        /// <summary>用户点了"拾取文字"：窗口已保存已填内容并关闭，调用方应在 CAD 编辑器可交互时拾取，随后重开窗口。</summary>
        public bool PickTextRequested{get;private set;}
        /// <summary>用户点了"增补云线"（仅编辑模式）：窗口已保存已填内容并关闭，调用方在编辑器可交互时
        /// 执行增补云线会话（连续框选，回车结束），随后重开窗口继续编辑。</summary>
        public bool AppendCloudRequested{get;private set;}
        private string _drawingPath = "";
        /// <summary>输入建议候选条数上限。</summary>
        private const int SuggestMax = 12;
        /// <summary>建议功能是否已就绪：构造期与程序性赋值期间为 false，避免把初始化当成用户输入。</summary>
        private bool _suggestReady;
        /// <summary>程序性改写内容框期间为 true（套用候选 / 追加回复 / 从知识库回填），不重算候选。</summary>
        private bool _suggestSuppress;
        /// <summary>光标所在行的行首：套用候选时替换的就是这一段。</summary>
        private int _suggestLineStart;

        public AnnotationWindow(AnnotationData value,bool editing)
        {
            InitializeComponent();Value=value;Title=editing?"编辑批注 · "+value.Number:"新建 GM批注";ConfirmButton.Content=editing?"更新批注":"创建批注";
            if(editing)AppendCloudButton.Visibility=Visibility.Visible; // 新建时还没有批注对象，增补无意义
            DisciplineComboBox.ItemsSource=AnnotationOptions.Disciplines;RoleComboBox.ItemsSource=AnnotationOptions.Roles;StatusComboBox.ItemsSource=AnnotationOptions.Statuses;ContentTextBox.Text=value.Content;DisciplineComboBox.Text=value.Discipline;AuthorTextBox.Text=value.Author;RoleComboBox.Text=value.Role;AnnotationDatePicker.SelectedDate=DateTime.TryParse(value.Date,out var d)?d:DateTime.Today;StatusComboBox.Text=value.Status;NumberTextBox.Text=value.Number;
            // 图号：优先取批注数据；新建时按当前 DWG 自动带出上次使用的图号。
            _drawingPath=GetDrawingPath();
            DrawingNoTextBox.Text=string.IsNullOrWhiteSpace(value.DrawingNo)?AnnotationHistoryStore.GetDrawingNo(_drawingPath):value.DrawingNo;
            // 历史：最近使用过的批注内容，选择后填入内容框。
            HistoryComboBox.SelectionChanged+=History_SelectionChanged;
            ReloadHistory();
            ArchiveCheckBox.IsChecked=SettingsStore.Load().ArchiveOnCreate;
            // 输入建议：随输入实时给候选（批注历史记录 / 常用批注语 / 知识库条目）；控件先设初值再接事件，避免初始化触发。
            SuggestPopup.PlacementTarget=ContentTextBox;
            SuggestCheckBox.IsChecked=SettingsStore.Load().ContentSuggest;
            ContentTextBox.TextChanged+=Content_TextChanged;
            ContentTextBox.SelectionChanged+=Content_SelectionChanged;
            ContentTextBox.PreviewKeyDown+=Content_PreviewKeyDown;
            _suggestReady=true;
            Loaded+=(s,e)=>{WindowSizing.FitToWorkArea(this);ContentTextBox.Focus();ContentTextBox.CaretIndex=ContentTextBox.Text.Length;};
        }

        private void History_SelectionChanged(object sender,SelectionChangedEventArgs e)
        {
            if(HistoryComboBox.SelectedItem is string content&&!string.IsNullOrWhiteSpace(content))
            {
                _suggestSuppress=true;
                try{ContentTextBox.Text=content;}finally{_suggestSuppress=false;}
                CloseSuggest();
            }
        }

        /// <summary>把"最近使用过的批注内容"灌入历史下拉；为空时置 null，避免出现空白候选项。</summary>
        private void ReloadHistory()
        {
            var contents=AnnotationHistoryStore.LoadRecentContents();
            HistoryComboBox.ItemsSource=contents.Count>0?contents:null;
        }

        // ==================== 输入建议（自动补全 / 续写） ====================

        /// <summary>光标所在行的已输入内容（去掉首尾空白）。候选匹配与套用都只看"当前行、光标之前"这一段，不动别的行。</summary>
        private string CurrentLineFragment(out int lineStart)
        {
            var text=ContentTextBox.Text??"";
            var caret=Math.Max(0,Math.Min(ContentTextBox.CaretIndex,text.Length));
            lineStart=caret;
            while(lineStart>0&&text[lineStart-1]!='\n')lineStart--;
            return text.Substring(lineStart,caret-lineStart).Trim();
        }

        private void Content_TextChanged(object sender,TextChangedEventArgs e)
        {
            if(!_suggestReady||_suggestSuppress)return;
            try{RefreshSuggestions(true);}catch(System.Exception ex){PluginLog.Warning("Suggest.Refresh",ex.Message);}
        }

        /// <summary>光标移动（含打字后的自动后移）：弹窗已经打开时跟着重算，没打开就不打扰。</summary>
        private void Content_SelectionChanged(object sender,RoutedEventArgs e)
        {
            if(!_suggestReady||_suggestSuppress)return;
            if(!SuggestPopup.IsOpen)return;
            try{RefreshSuggestions(false);}catch(System.Exception ex){PluginLog.Warning("Suggest.Move",ex.Message);}
        }

        /// <summary>按当前行的已输入内容重算候选并（可选）弹出。
        /// autoOpen=true 是"用户正在打字"：只在片段较短、或存在能直接续写的候选时才弹，写长句时不挡视线；
        /// autoOpen=false 是显式请求（Ctrl+空格、或弹窗已打开时跟随光标），有候选就显示。</summary>
        private void RefreshSuggestions(bool autoOpen)
        {
            if(SuggestCheckBox.IsChecked!=true){CloseSuggest();return;}
            var fragment=CurrentLineFragment(out _suggestLineStart);
            var items=ContentSuggestionEngine.Suggest(fragment,SuggestMax);
            bool wantOpen;
            if(items.Count==0)wantOpen=false;
            else if(!autoOpen)wantOpen=true;
            else if(fragment.Length==0)wantOpen=false;
            else wantOpen=fragment.Length<=14||items.Exists(i=>i.IsContinuation);
            if(!wantOpen){CloseSuggest();return;}
            SuggestPopup.Width=Math.Max(240,ContentTextBox.ActualWidth);
            SuggestList.ItemsSource=items;
            SuggestList.SelectedIndex=0;
            SuggestPopup.IsOpen=true;
        }

        private void CloseSuggest()
        {
            if(SuggestPopup.IsOpen)SuggestPopup.IsOpen=false;
            SuggestList.ItemsSource=null;
        }

        private void Content_PreviewKeyDown(object sender,KeyEventArgs e)
        {
            try
            {
                if(e.Key==Key.Space&&(Keyboard.Modifiers&ModifierKeys.Control)!=0){RefreshSuggestions(false);e.Handled=true;return;}
                if(!SuggestPopup.IsOpen)return;
                if(e.Key==Key.Down){MoveSuggestion(1);e.Handled=true;}
                else if(e.Key==Key.Up){MoveSuggestion(-1);e.Handled=true;}
                // 回车/Tab 套用候选；Shift+回车、Ctrl+回车仍按原样换行。
                else if(e.Key==Key.Enter&&(Keyboard.Modifiers&(ModifierKeys.Shift|ModifierKeys.Control))==0){AcceptSelected();e.Handled=true;}
                else if(e.Key==Key.Tab){AcceptSelected();e.Handled=true;}
                else if(e.Key==Key.Escape){CloseSuggest();e.Handled=true;}
            }
            catch(System.Exception ex){PluginLog.Warning("Suggest.Key",ex.Message);}
        }

        private void MoveSuggestion(int delta)
        {
            var count=SuggestList.Items.Count;if(count==0)return;
            var index=SuggestList.SelectedIndex+delta;
            if(index<0)index=count-1;
            if(index>=count)index=0;
            SuggestList.SelectedIndex=index;
            SuggestList.ScrollIntoView(SuggestList.SelectedItem);
        }

        private void AcceptSelected(){AcceptSuggestion(SuggestList.SelectedItem as ContentSuggestion);}

        private void Suggest_Click(object sender,MouseButtonEventArgs e)
        {
            try
            {
                var suggestion=FindSuggestion(e.OriginalSource as DependencyObject);
                if(suggestion!=null)AcceptSuggestion(suggestion);
            }
            catch(System.Exception ex){PluginLog.Warning("Suggest.Click",ex.Message);}
        }

        /// <summary>从鼠标命中的元素往上找所在的建议行（列表设了 Focusable=False，点它不会抢走内容框的焦点）。</summary>
        private static ContentSuggestion FindSuggestion(DependencyObject source)
        {
            while(source!=null)
            {
                if(source is ListBoxItem item)return item.DataContext as ContentSuggestion;
                try{source=VisualTreeHelper.GetParent(source);}catch{return null;}
            }
            return null;
        }

        /// <summary>套用候选：把"当前行、光标之前"那段已输入内容整段换成候选文本。
        /// 候选以这段开头时，效果就是把后半句续写上去；否则相当于用候选改写这一行。其他行与光标后面的内容都不动。</summary>
        private void AcceptSuggestion(ContentSuggestion suggestion)
        {
            if(suggestion==null||string.IsNullOrEmpty(suggestion.Text))return;
            var text=ContentTextBox.Text??"";
            var caret=Math.Max(0,Math.Min(ContentTextBox.CaretIndex,text.Length));
            var start=Math.Max(0,Math.Min(_suggestLineStart,caret));
            var newText=text.Substring(0,start)+suggestion.Text+text.Substring(caret);
            _suggestSuppress=true;
            try
            {
                ContentTextBox.Text=newText;
                ContentTextBox.CaretIndex=start+suggestion.Text.Length;
                ContentTextBox.ScrollToEnd();
            }
            finally{_suggestSuppress=false;}
            CloseSuggest();
            ContentTextBox.Focus();
        }

        /// <summary>开/关输入建议，并记住选择（settings.xml 的 ContentSuggest）。</summary>
        private void Suggest_Toggled(object sender,RoutedEventArgs e)
        {
            if(!_suggestReady)return;
            try
            {
                var settings=SettingsStore.Load();settings.ContentSuggest=SuggestCheckBox.IsChecked==true;SettingsStore.Save(settings);
                if(SuggestCheckBox.IsChecked!=true)CloseSuggest();
            }
            catch(System.Exception ex){PluginLog.Warning("Suggest.Toggle",ex.Message);}
        }

        /// <summary>清除"历史"下拉里的最近批注内容：只清下拉候选（在数据目录写一个清除时间标记，下次不再带出旧内容），
        /// 不删除批注历史记录（留痕）——要删留痕请用批注历史记录窗口（GM_PZ_HISTORY）的"清理"。清除后本次窗口内下拉立即变空。</summary>
        private void ClearHistory_Click(object sender,RoutedEventArgs e)
        {
            try
            {
                var count=HistoryComboBox.Items.Count;
                if(count==0)
                {
                    MessageBox.Show(this,"历史下拉里没有可清除的内容。","清除历史",MessageBoxButton.OK,MessageBoxImage.Information);
                    return;
                }
                var answer=MessageBox.Show(this,
                    "将清空\"历史\"下拉中的 "+count+" 条最近批注内容。\n\n"+
                    "仅清空下拉候选，不删除批注历史记录（批注历史记录窗口的\"清理\"才删除留痕）。\n\n是否继续？",
                    "清除历史",MessageBoxButton.OKCancel,MessageBoxImage.Warning);
                if(answer!=MessageBoxResult.OK)return;
                var ok=AnnotationHistoryStore.ClearRecentContents();
                HistoryComboBox.SelectedItem=null;
                HistoryComboBox.ItemsSource=null;
                HistoryComboBox.Text="";
                if(!ok)
                {
                    MessageBox.Show(this,"清除失败，详见日志：%AppData%\\GMAnnotation\\Logs\\GMAnnotation.log","清除历史",MessageBoxButton.OK,MessageBoxImage.Warning);
                    return;
                }
                MessageBox.Show(this,"已清空历史下拉，后续新增的批注内容会重新积累。","清除历史",MessageBoxButton.OK,MessageBoxImage.Information);
            }
            catch(System.Exception ex)
            {
                PluginLog.Error("ClearHistory",ex);
                MessageBox.Show(this,"清除历史失败: "+Describe(ex),"GM批注",MessageBoxButton.OK,MessageBoxImage.Warning);
            }
        }

        /// <summary>回复分隔线：与图内/截图模板一致的横线。</summary>
        internal const string ReplySeparator="----------------------------";

        /// <summary>在批注内容末尾追加一条回复：分隔线 + 回复:时间 批注人 + 内容:，光标停在"内容:"后直接输入。
        /// 回复随内容一起保存，图上批注框按换行原样显示。</summary>
        private void Reply_Click(object sender,RoutedEventArgs e)
        {
            try
            {
                var author=AuthorTextBox.Text.Trim();
                // 去掉正文结尾的空行，让分隔线紧贴原有内容。
                var text=ContentTextBox.Text.TrimEnd('\r','\n');
                var sb=new System.Text.StringBuilder(text);
                if(sb.Length>0)sb.Append(Environment.NewLine);
                sb.Append(ReplySeparator).Append(Environment.NewLine);
                sb.Append("回复:"+DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                if(author.Length>0)sb.Append(' ').Append(author);
                sb.Append(Environment.NewLine);
                sb.Append("内容:");
                // 程序性追加，不触发建议弹窗；用户接着打字时建议自然会出来。
                _suggestSuppress=true;
                try{ContentTextBox.Text=sb.ToString();}finally{_suggestSuppress=false;}
                CloseSuggest();
                ContentTextBox.Focus();
                ContentTextBox.CaretIndex=ContentTextBox.Text.Length;
                ContentTextBox.ScrollToEnd();
            }
            catch(System.Exception ex)
            {
                PluginLog.Error("Reply_Click",ex);
                MessageBox.Show(this,"追加回复失败: "+Describe(ex),"GM批注",MessageBoxButton.OK,MessageBoxImage.Warning);
            }
        }

        private void KnowledgeBase_Click(object sender,RoutedEventArgs e)
        {
            try
            {
                // 批注窗口本身已是 CAD 的模态窗口，再调 CAD 的 ShowModalWindow 属嵌套模态（易出异常），
                // 故知识库改为非模态打开：挂在批注窗口下，可边查边把条目/常用语填进内容框。
                // 注意：知识库是独立体系，与"批注历史记录（留痕）"不是同一个窗口。
                KnowledgeWindow.ShowModeless(this);
            }
            catch(System.Exception ex)
            {
                PluginLog.Error("KnowledgeBase",ex);
                MessageBox.Show(this,"打开知识库失败: "+Describe(ex),"GM批注",MessageBoxButton.OK,MessageBoxImage.Warning);
            }
        }

        /// <summary>异常摘要：类型 + 消息 + 第一帧堆栈，便于在没有调试器的环境里直接定位出错位置。</summary>
        private static string Describe(System.Exception ex)
        {
            var text=ex.GetType().Name+": "+ex.Message;
            var stack=ex.StackTrace;
            if(!string.IsNullOrEmpty(stack))
            {
                var lines=stack.Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries);
                if(lines.Length>0)text+="\n"+lines[0].Trim();
            }
            return text;
        }

        private void Confirm_Click(object sender,RoutedEventArgs e)
        {
            if(string.IsNullOrWhiteSpace(ContentTextBox.Text)){MessageBox.Show(this,"请输入批注内容。","GM批注",MessageBoxButton.OK,MessageBoxImage.Information);ContentTextBox.Focus();return;}
            if(string.IsNullOrWhiteSpace(NumberTextBox.Text)){MessageBox.Show(this,"批注编号不能为空。","GM批注",MessageBoxButton.OK,MessageBoxImage.Information);return;}
            CaptureInto(Value);
            // 记住本图的图号与"立即入库"开关，下次自动带出。
            AnnotationHistoryStore.SetDrawingNo(_drawingPath,Value.DrawingNo);
            var settings=SettingsStore.Load();settings.ArchiveOnCreate=ArchiveCheckBox.IsChecked==true;SettingsStore.Save(settings);
            DialogResult=true;
        }

        /// <summary>把知识库窗口选中的"批注条目内容 / 常用批注语"填进内容框（替换原有内容）。
        /// 仅仅替换文本，仍需用户点「创建批注 / 更新批注」才会落图；知识库是从本窗口非模态打开的，故可直接回调。</summary>
        internal void ApplyPhrase(string text)
        {
            try
            {
                if(string.IsNullOrWhiteSpace(text))return;
                _suggestSuppress=true;
                try{ContentTextBox.Text=text;}finally{_suggestSuppress=false;}
                CloseSuggest();
                ContentTextBox.Focus();
                ContentTextBox.CaretIndex=ContentTextBox.Text.Length;
                ContentTextBox.ScrollToEnd();
                Activate();
            }
            catch(System.Exception ex){PluginLog.Error("ApplyPhrase",ex);}
        }

        /// <summary>把窗口上已填写的各项写回数据对象（不做必填校验）。拾取文字前也要调用，重开窗口时才能原样恢复已填内容。</summary>
        private void CaptureInto(AnnotationData data)
        {
            data.Content=ContentTextBox.Text.Trim();
            data.DrawingNo=DrawingNoTextBox.Text.Trim();
            data.Discipline=DisciplineComboBox.Text.Trim();
            data.Author=AuthorTextBox.Text.Trim();
            data.Role=string.IsNullOrWhiteSpace(RoleComboBox.Text)?"批注人":RoleComboBox.Text.Trim();
            data.Date=(AnnotationDatePicker.SelectedDate??DateTime.Today).ToString("yyyy-MM-dd");
            data.Status=string.IsNullOrWhiteSpace(StatusComboBox.Text)?"待处理":StatusComboBox.Text.Trim();
            data.Number=NumberTextBox.Text.Trim();
        }

        /// <summary>拾取图面文字作为图号：批注窗口是 CAD 模态窗口，显示期间无法与图形交互，
        /// 因此先保存已填内容并关闭窗口，由 CadDialog.ShowAnnotation 在编辑器可交互时拾取，再重开窗口带回结果。</summary>
        private void PickText_Click(object sender,RoutedEventArgs e)
        {
            try
            {
                CaptureInto(Value);
                PickTextRequested=true;
                DialogResult=false;
            }
            catch(System.Exception ex)
            {
                PluginLog.Error("PickDrawingNo",ex);
                MessageBox.Show(this,"启动拾取失败: "+Describe(ex),"GM批注",MessageBoxButton.OK,MessageBoxImage.Warning);
            }
        }

        private void Cancel_Click(object sender,RoutedEventArgs e){DialogResult=false;}

        /// <summary>增补云线（仅编辑模式）：批注窗口是 CAD 模态窗口，显示期间无法与图形交互，
        /// 因此先保存已填内容并关闭窗口，由 CadDialog.ShowAnnotation 在编辑器可交互时执行增补会话，再重开窗口。</summary>
        private void AppendCloud_Click(object sender,RoutedEventArgs e)
        {
            try
            {
                CaptureInto(Value);
                AppendCloudRequested=true;
                DialogResult=false;
            }
            catch(System.Exception ex)
            {
                PluginLog.Error("AppendCloud.Click",ex);
                MessageBox.Show(this,"启动增补云线失败: "+Describe(ex),"GM批注",MessageBoxButton.OK,MessageBoxImage.Warning);
            }
        }

        private static string GetDrawingPath()
        {
            try{return CadApplication.DocumentManager.MdiActiveDocument?.Name??"";}catch{return "";}
        }
    }
}
