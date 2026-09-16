using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace GMAnnotation.Views
{
    /// <summary>
    /// 知识库窗口：两个页签——「知识库条目」（规范条文表：序号/专业/属性/规范名称/规范编号/条款/内容，可直接编辑、
    /// 可导入导出 CSV）与「常用批注语」（可复用的批注内容，可导入）。
    ///
    /// <para><b>与「批注历史记录」（<see cref="HistoryWindow"/>）是两套独立体系</b>：本窗口只读 <c>knowledge.json</c>，
    /// 历史记录窗口只读 <c>history.json</c>（留痕）。两者的检索、导出、删除、清空互不影响。</para>
    ///
    /// 从批注窗口打开时是非模态的（可边查边把条目/常用语填进批注内容框）；也可由 <c>GM_PZ_KB</c> 命令模态打开。
    /// 表格改动即时落盘（<see cref="KnowledgeStore.SaveAll"/>），不弹确认；破坏性操作（删除/清空/导入）才备份 + 二次确认。
    /// </summary>
    internal partial class KnowledgeWindow : Window
    {
        private List<KnowledgeEntry> _entries = new List<KnowledgeEntry>();
        private List<KnowledgePhrase> _phrases = new List<KnowledgePhrase>();
        private bool _dirty;   // 表格里有改动还没落盘
        private bool _saving;  // 存盘防重入

        public KnowledgeWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => WindowSizing.FitToWorkArea(this);
            try { Reload(); }
            catch (Exception ex) { PluginLog.Error("KnowledgeWindow.Reload", ex); }
        }

        private static KnowledgeWindow _open;
        private static readonly object _openLock = new object();

        /// <summary>
        /// 以"非模态"方式打开知识库：批注窗口本身已是 CAD 的模态窗口，在其中再调 CAD 的 ShowModalWindow
        /// 属嵌套模态（最容易出异常、也最难复现的路径），所以与批注历史记录窗口一样走非模态——
        /// 挂在批注窗口下，既能随时查阅，又能把条目/常用语直接填进正在填写的批注内容框。
        /// </summary>
        public static void ShowModeless(Window owner)
        {
            lock (_openLock)
            {
                if (_open != null)
                {
                    try { _open.Reload(); _open.Activate(); return; }
                    catch (Exception ex) { PluginLog.Warning("KnowledgeWindow.Reuse", ex.Message); _open = null; }
                }
                var window = new KnowledgeWindow();
                _open = window;
                window.Closed += (s, e) => { lock (_openLock) { if (ReferenceEquals(_open, window)) _open = null; } };
                if (owner != null) window.Owner = owner;
                window.Show();
                window.Activate();
            }
        }

        // ==================== 加载 / 过滤 / 存盘 ====================

        /// <summary>从磁盘重新加载。若表格里还有未落盘的改动，先提交并保存，避免被重新加载冲掉。</summary>
        private void Reload()
        {
            CommitGridEdits();
            if (_dirty) SaveEntries();
            _entries = KnowledgeStore.LoadEntries();
            _phrases = KnowledgeStore.LoadPhrases();
            ApplyFilters();
        }

        /// <summary>序号按文件顺序从 1 编号（过滤后仍保留原序号，便于对照导出/导入文件）。</summary>
        private void NumberEntries()
        {
            for (var i = 0; i < _entries.Count; i++) _entries[i].Seq = i + 1;
        }

        /// <summary>按当前关键字刷新两个页签的列表与计数。控件引用一律做 null 兜底，XAML 不匹配也不抛空引用。</summary>
        private void ApplyFilters()
        {
            NumberEntries();

            var keyword = (EntryFilterBox?.Text ?? "").Trim();
            var entries = keyword.Length > 0 ? _entries.Where(x => x.Matches(keyword)).ToList() : new List<KnowledgeEntry>(_entries);
            if (EntryGrid != null) EntryGrid.ItemsSource = entries;

            var phraseKeyword = (PhraseFilterBox?.Text ?? "").Trim();
            var phrases = _phrases.OrderByDescending(p => p.Time).ToList();
            if (phraseKeyword.Length > 0) phrases = phrases.Where(p => KnowledgeStore.Contains(p.Text, phraseKeyword)).ToList();
            if (PhraseGrid != null) PhraseGrid.ItemsSource = phrases;

            if (CountText != null)
                CountText.Text = "知识库条目 " + _entries.Count + " 条（当前显示 " + entries.Count + " 条） · 常用批注语 "
                    + _phrases.Count + " 条（当前显示 " + phrases.Count + " 条）";
            if (FileText != null)
                FileText.Text = "数据文件：" + KnowledgeStore.FilePath + "（知识库与「批注历史记录」相互独立）";
        }

        /// <summary>把正在编辑的单元格提交（离开窗口/点其它按钮时容易丢掉编辑中的值）。</summary>
        private void CommitGridEdits()
        {
            try
            {
                if (EntryGrid != null && EntryGrid.IsKeyboardFocusWithin)
                    EntryGrid.CommitEdit(DataGridEditingUnit.Row, true);
            }
            catch (Exception ex) { PluginLog.Warning("KnowledgeWindow.Commit", ex.Message); }
        }

        /// <summary>条目 + 常用批注语整体存盘（表格改动的唯一出口）。</summary>
        private void SaveEntries()
        {
            if (_saving) return;
            _saving = true;
            try
            {
                if (KnowledgeStore.SaveAll(_entries, _phrases)) _dirty = false;
                else MessageBox.Show(this, "保存失败，详见日志：%AppData%\\GMAnnotation\\Logs\\GMAnnotation.log", "知识库");
            }
            catch (Exception ex) { PluginLog.Warning("KnowledgeWindow.Save", ex.Message); }
            finally { _saving = false; }
        }

        /// <summary>表格改完（单元格提交）后落盘：CellEditEnding 里 Binding 还没写回对象，排到后台优先级再存。</summary>
        private void EntryGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e == null || e.EditAction != DataGridEditAction.Commit) return;
            _dirty = true;
            Dispatcher.BeginInvoke(new Action(SaveEntries), DispatcherPriority.Background);
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            try
            {
                CommitGridEdits();
                if (_dirty) SaveEntries();
            }
            catch (Exception ex) { PluginLog.Warning("KnowledgeWindow.OnClosing", ex.Message); }
            base.OnClosing(e);
        }

        private void Filter_Changed(object sender, RoutedEventArgs e) { ApplyFilters(); }
        private void Refresh_Click(object sender, RoutedEventArgs e) { Reload(); }
        private void Close_Click(object sender, RoutedEventArgs e) { Close(); }

        // ==================== 新增 / 填入批注内容 ====================

        private void EntryAdd_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Reload(); // 先把外部改动/表格改动对齐，再追加
                var entry = new KnowledgeEntry { Time = DateTime.Now, User = AnnotationHistoryStore.CurrentUser() };
                _entries.Add(entry);
                SaveEntries();   // 这一步会给新条目补上稳定 Id
                ApplyFilters();
                if (EntryGrid != null)
                {
                    EntryGrid.SelectedItem = entry;
                    EntryGrid.ScrollIntoView(entry);
                    EntryGrid.Focus();
                }
            }
            catch (Exception ex) { Report("新增条目失败", ex); }
        }

        /// <summary>把一段文本填进批注窗口的内容框；知识库是从批注窗口非模态打开的，Owner 就是那个批注窗口。</summary>
        private void ApplyToOwner(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show(this, "没有可填入的内容。", "知识库");
                return;
            }
            var owner = Owner as AnnotationWindow;
            if (owner == null)
            {
                MessageBox.Show(this, "请从批注窗口的「知识库」按钮打开本窗口，才能把内容填进批注内容框。", "知识库");
                return;
            }
            owner.ApplyPhrase(text);
        }

        private void EntryApply_Click(object sender, RoutedEventArgs e) { ApplySelectedEntry(); }

        /// <summary>
        /// 双击一行 = 填入内容。因为表格本身可编辑，必须用 PreviewMouseLeftButtonDown 抢在单元格之前处理，
        /// 并把事件标记为已处理，否则双击会同时在单元格里进入编辑状态（两个动作会打架）。
        /// </summary>
        private void EntryGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (e.ClickCount != 2) return;
                if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) == null) return; // 表头/空白处双击走默认
                e.Handled = true;
                ApplySelectedEntry();
            }
            catch (Exception ex) { PluginLog.Warning("KnowledgeWindow.EntryDoubleClick", ex.Message); }
        }

        private void ApplySelectedEntry()
        {
            try
            {
                var entry = EntryGrid?.SelectedItem as KnowledgeEntry;
                if (entry == null) { MessageBox.Show(this, "请先在上表中选择一条知识库条目。", "知识库"); return; }
                ApplyToOwner(entry.Content);
            }
            catch (Exception ex) { Report("填入内容失败", ex); }
        }

        private void PhraseApply_Click(object sender, RoutedEventArgs e) { ApplySelectedPhrase(); }

        // 双击表格空白/表头时 SelectedItem 为空，这里静默返回，不弹提示。
        private void PhraseGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (PhraseGrid != null && PhraseGrid.SelectedItem is KnowledgePhrase) ApplySelectedPhrase();
        }

        private void ApplySelectedPhrase()
        {
            try
            {
                var phrase = PhraseGrid?.SelectedItem as KnowledgePhrase;
                if (phrase == null) { MessageBox.Show(this, "请先在列表中选择一条常用批注语。", "知识库"); return; }
                ApplyToOwner(phrase.Text);
            }
            catch (Exception ex) { Report("填入内容失败", ex); }
        }

        private static T FindAncestor<T>(DependencyObject start) where T : DependencyObject
        {
            try
            {
                var current = start;
                while (current != null)
                {
                    var typed = current as T;
                    if (typed != null) return typed;
                    current = VisualTreeHelper.GetParent(current);
                }
            }
            catch (Exception) { } // OriginalSource 可能不是 Visual，取父级会抛异常
            return null;
        }

        // ==================== 条目：导出 / 导入 / 删除 / 清空 ====================

        private List<KnowledgeEntry> SelectedEntries()
        {
            var list = new List<KnowledgeEntry>();
            if (EntryGrid == null) return list;
            foreach (var item in EntryGrid.SelectedItems)
            {
                var entry = item as KnowledgeEntry;
                if (entry != null) list.Add(entry);
            }
            return list;
        }

        private void EntryExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CommitGridEdits();
                var records = EntryGrid == null ? new List<KnowledgeEntry>() : EntryGrid.Items.Cast<KnowledgeEntry>().ToList();
                if (records.Count == 0) { MessageBox.Show(this, "当前没有可导出的知识库条目。", "知识库"); return; }
                var dialog = new SaveFileDialog
                {
                    Title = "导出知识库条目",
                    Filter = "CSV 文件 (*.csv)|*.csv",
                    FileName = "知识库条目_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv"
                };
                if (dialog.ShowDialog(this) != true) return;
                KnowledgeStore.ExportEntriesCsv(dialog.FileName, records);
                MessageBox.Show(this, "已导出 " + records.Count + " 条到:\n" + dialog.FileName
                    + "\n\n列序：序号,专业,属性,规范名称,规范编号,条款,内容\n该文件可直接用作导入模板。", "知识库");
            }
            catch (Exception ex) { Report("导出失败", ex); }
        }

        private void EntryImport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CommitGridEdits();
                if (_dirty) SaveEntries();
                var dialog = new OpenFileDialog
                {
                    Title = "导入知识库条目",
                    Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*"
                };
                if (dialog.ShowDialog(this) != true) return;
                var answer = MessageBox.Show(this,
                    "将从以下文件导入知识库条目：\n" + dialog.FileName + "\n\n" +
                    "· 表头需含 专业 / 属性 / 规范名称 / 规范编号 / 条款 / 内容（无表头时按此顺序逐列解析）\n" +
                    "· 同一「规范编号 + 条款」视为同一条，按更新处理；两者都空则一律新增\n" +
                    "· 清单里已有的其它条目不会被删掉，导入前自动备份到 knowledge.json.bak\n" +
                    "· 编码按 UTF-8(BOM) → UTF-8 → GBK 依次尝试（Excel 请尽量选「CSV UTF-8」）\n\n是否继续？",
                    "知识库 · 导入条目", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (answer != MessageBoxResult.OK) return;
                var result = KnowledgeStore.ImportEntriesCsv(dialog.FileName);
                Reload();
                MessageBox.Show(this, "导入完成。\n\n" + result.Describe(), "知识库 · 导入条目");
            }
            catch (Exception ex) { Report("导入条目失败", ex); }
        }

        private void EntryDelete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CommitGridEdits();
                if (_dirty) SaveEntries();
                var selected = SelectedEntries();
                if (selected.Count == 0) { MessageBox.Show(this, "请先在上表中选择要删除的条目。", "知识库"); return; }
                var answer = MessageBox.Show(this,
                    "将从知识库删除 " + selected.Count + " 条条目。\n\n" +
                    "删除前会自动备份一次到：\n" + KnowledgeStore.BackupPath + "\n\n" +
                    "（「批注历史记录」不受影响）该操作不可撤销，是否继续？",
                    "知识库 · 删除条目", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.OK) return;
                if (!KnowledgeStore.RemoveEntries(selected))
                {
                    MessageBox.Show(this, "删除失败，详见日志：%AppData%\\GMAnnotation\\Logs\\GMAnnotation.log", "知识库");
                    return;
                }
                Reload();
            }
            catch (Exception ex) { Report("删除条目失败", ex); }
        }

        private void EntryClear_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CommitGridEdits();
                if (_dirty) SaveEntries();
                if (_entries.Count == 0) { MessageBox.Show(this, "知识库中还没有条目。", "知识库"); return; }
                var answer = MessageBox.Show(this,
                    "将清空知识库中的全部 " + _entries.Count + " 条条目（常用批注语保留）。\n\n" +
                    "清空前会自动备份一次到：\n" + KnowledgeStore.BackupPath + "\n\n" +
                    "（「批注历史记录」不受影响）该操作不可撤销，是否继续？",
                    "知识库 · 清空条目", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.OK) return;
                if (!KnowledgeStore.ClearEntries())
                {
                    MessageBox.Show(this, "清空失败，详见日志：%AppData%\\GMAnnotation\\Logs\\GMAnnotation.log", "知识库");
                    return;
                }
                Reload();
            }
            catch (Exception ex) { Report("清空条目失败", ex); }
        }

        // ==================== 常用批注语：新增 / 替换 / 删除 / 清空 / 导出 / 导入 ====================

        private List<KnowledgePhrase> SelectedPhrases()
        {
            var list = new List<KnowledgePhrase>();
            if (PhraseGrid == null) return list;
            foreach (var item in PhraseGrid.SelectedItems)
            {
                var phrase = item as KnowledgePhrase;
                if (phrase != null) list.Add(phrase);
            }
            return list;
        }

        private void PhraseAdd_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var text = (NewPhraseBox?.Text ?? "").Trim();
                if (text.Length == 0)
                {
                    MessageBox.Show(this, "请先在「新常用语」中输入内容。", "知识库");
                    NewPhraseBox?.Focus();
                    return;
                }
                var result = KnowledgeStore.AddPhrase(text);
                if (result == 0) { MessageBox.Show(this, "该常用批注语已存在，未重复添加。", "知识库"); return; }
                if (result < 0)
                {
                    MessageBox.Show(this, "保存失败，详见日志：%AppData%\\GMAnnotation\\Logs\\GMAnnotation.log", "知识库");
                    return;
                }
                NewPhraseBox.Text = "";
                Reload();
            }
            catch (Exception ex) { Report("添加常用语失败", ex); }
        }

        private void PhraseReplace_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var target = PhraseGrid?.SelectedItem as KnowledgePhrase;
                if (target == null) { MessageBox.Show(this, "请先在列表中选择要替换的常用批注语。", "知识库"); return; }
                var text = (NewPhraseBox?.Text ?? "").Trim();
                if (text.Length == 0) { MessageBox.Show(this, "请先在「新常用语」中输入内容。", "知识库"); return; }
                if (!KnowledgeStore.ReplacePhrase(target, text))
                {
                    MessageBox.Show(this, "替换失败（内容为空、改成的内容已存在，或写入失败）。", "知识库");
                    return;
                }
                NewPhraseBox.Text = "";
                Reload();
            }
            catch (Exception ex) { Report("替换常用语失败", ex); }
        }

        private void PhraseDelete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selected = SelectedPhrases();
                if (selected.Count == 0) { MessageBox.Show(this, "请先在列表中选择要删除的常用批注语。", "知识库"); return; }
                var answer = MessageBox.Show(this,
                    "将从知识库删除 " + selected.Count + " 条常用批注语。\n\n删除前会自动备份一次到：\n" +
                    KnowledgeStore.BackupPath + "\n\n该操作不可撤销，是否继续？",
                    "知识库 · 删除常用语", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.OK) return;
                if (!KnowledgeStore.RemovePhrases(selected))
                {
                    MessageBox.Show(this, "删除失败，详见日志：%AppData%\\GMAnnotation\\Logs\\GMAnnotation.log", "知识库");
                    return;
                }
                Reload();
            }
            catch (Exception ex) { Report("删除常用语失败", ex); }
        }

        private void PhraseClear_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_phrases.Count == 0) { MessageBox.Show(this, "知识库中还没有常用批注语。", "知识库"); return; }
                var answer = MessageBox.Show(this,
                    "将清空全部 " + _phrases.Count + " 条常用批注语（知识库条目保留）。\n\n" +
                    "清空前会自动备份一次到：\n" + KnowledgeStore.BackupPath + "\n\n该操作不可撤销，是否继续？",
                    "知识库 · 清空常用语", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.OK) return;
                if (!KnowledgeStore.ClearPhrases())
                {
                    MessageBox.Show(this, "清空失败，详见日志：%AppData%\\GMAnnotation\\Logs\\GMAnnotation.log", "知识库");
                    return;
                }
                Reload();
            }
            catch (Exception ex) { Report("清空常用语失败", ex); }
        }

        private void PhraseExport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var records = PhraseGrid == null ? new List<KnowledgePhrase>() : PhraseGrid.Items.Cast<KnowledgePhrase>().ToList();
                if (records.Count == 0) { MessageBox.Show(this, "当前没有可导出的常用批注语。", "知识库"); return; }
                var dialog = new SaveFileDialog
                {
                    Title = "导出常用批注语",
                    Filter = "CSV 文件 (*.csv)|*.csv",
                    FileName = "常用批注语_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv"
                };
                if (dialog.ShowDialog(this) != true) return;
                KnowledgeStore.ExportPhrasesCsv(dialog.FileName, records);
                MessageBox.Show(this, "已导出 " + records.Count + " 条常用批注语到:\n" + dialog.FileName, "知识库");
            }
            catch (Exception ex) { Report("导出失败", ex); }
        }

        private void PhraseImport_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Title = "导入常用批注语",
                    Filter = "CSV 文件 (*.csv)|*.csv|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*"
                };
                if (dialog.ShowDialog(this) != true) return;
                var answer = MessageBox.Show(this,
                    "将从以下文件导入常用批注语：\n" + dialog.FileName + "\n\n" +
                    "· 第一行含「常用批注语 / 批注内容 / 内容」表头时取该列，否则取第一列逐行读入\n" +
                    "· 已存在的同内容不重复添加；导入前自动备份到 knowledge.json.bak\n\n是否继续？",
                    "知识库 · 导入常用批注语", MessageBoxButton.OKCancel, MessageBoxImage.Question);
                if (answer != MessageBoxResult.OK) return;
                var result = KnowledgeStore.ImportPhrasesCsv(dialog.FileName);
                Reload();
                MessageBox.Show(this, "导入完成。\n\n" + result.Describe(), "知识库 · 导入常用批注语");
            }
            catch (Exception ex) { Report("导入常用批注语失败", ex); }
        }

        /// <summary>异常摘要（类型 + 消息 + 第一帧堆栈），便于在没有调试器的环境里定位。</summary>
        private void Report(string title, Exception ex)
        {
            PluginLog.Error("KnowledgeWindow." + title, ex);
            var text = ex.GetType().Name + ": " + ex.Message;
            var stack = ex.StackTrace;
            if (!string.IsNullOrEmpty(stack))
            {
                var lines = stack.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0) text += "\n" + lines[0].Trim();
            }
            MessageBox.Show(this, title + ": " + text, "知识库", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
