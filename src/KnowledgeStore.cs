using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using CadApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif

namespace GMAnnotation
{
    /// <summary>
    /// 知识库条目：一条「规范条文」记录（专业 / 属性 / 规范名称 / 规范编号 / 条款 / 内容）。
    ///
    /// <para>身份键 <see cref="Id"/> 用于窗口内编辑、删除、导入更新时的稳定定位；导入按
    /// 「规范编号 + 条款」(<see cref="SpecKey"/>) 判重——同一规范同一条款再次导入是"更新"而不是新增。</para>
    ///
    /// <para>另外保留 <see cref="Drawing"/> ~ <see cref="Status"/> 一组**兼容字段**：批注窗口勾「立即入库」时
    /// 会把图名/图号/编号/批注人等信息一并写进来。这些字段不再在列表中显示，但读写都保留，避免旧数据丢失。</para>
    /// </summary>
    internal sealed class KnowledgeEntry
    {
        /// <summary>稳定身份（GUID）。窗口内改行、删行、导入更新都按它定位。</summary>
        public string Id { get; set; }

        /// <summary>写入/最后修改时间。</summary>
        public DateTime Time { get; set; }

        /// <summary>写入人。</summary>
        public string User { get; set; }

        // ===== 列表显示的六个业务字段 =====

        /// <summary>专业。</summary>
        public string Discipline { get; set; }

        /// <summary>属性（条文性质，如 强制性/非强制性）。</summary>
        public string Attribute { get; set; }

        /// <summary>规范名称。</summary>
        public string SpecName { get; set; }

        /// <summary>规范编号。</summary>
        public string SpecNumber { get; set; }

        /// <summary>条款。</summary>
        public string Clause { get; set; }

        /// <summary>内容（规范条文正文 / 批注内容）。</summary>
        public string Content { get; set; }

        // ===== 兼容字段：批注归档写入的图面信息，不显示、只保留 =====

        public string Drawing { get; set; }
        public string DrawingPath { get; set; }
        public string DrawingNo { get; set; }
        public string Number { get; set; }
        public string Date { get; set; }
        public string Author { get; set; }
        public string Role { get; set; }
        public string Status { get; set; }

        /// <summary>列表展示用序号（1 起，按文件顺序，由窗口在刷新时赋值，不属于存储内容）。</summary>
        public int Seq { get; set; }

        /// <summary>DataGrid 展示用时间。</summary>
        public string TimeString { get { return Time.ToString("yyyy-MM-dd HH:mm:ss"); } }

        /// <summary>删除/去重用的稳定身份：优先 Id，旧数据没有 Id 时退回"图 + 编号"。</summary>
        public string Identity()
        {
            return string.IsNullOrEmpty(Id) ? "L:" + LegacyKey() : Id;
        }

        /// <summary>批注归档的兼容键：图路径（空则图名）+ 编号。</summary>
        public string LegacyKey()
        {
            return (string.IsNullOrEmpty(DrawingPath) ? (Drawing ?? "") : DrawingPath) + "|" + (Number ?? "");
        }

        /// <summary>导入判重键：规范编号 + 条款。两者都空时返回空串（表示"无法判重，只新增"）。</summary>
        public string SpecKey()
        {
            var number = (SpecNumber ?? "").Trim();
            var clause = (Clause ?? "").Trim();
            if (number.Length == 0 && clause.Length == 0) return "";
            return number + "|" + clause;
        }

        /// <summary>是否六个业务字段全空（导入时用于跳过空行）。</summary>
        public bool IsBlank()
        {
            return IsBlankText(Discipline) && IsBlankText(Attribute) && IsBlankText(SpecName)
                && IsBlankText(SpecNumber) && IsBlankText(Clause) && IsBlankText(Content);
        }

        /// <summary>关键词过滤：命中任一显示字段即算匹配。</summary>
        public bool Matches(string keyword)
        {
            return KnowledgeStore.Contains(Discipline, keyword) || KnowledgeStore.Contains(Attribute, keyword)
                || KnowledgeStore.Contains(SpecName, keyword) || KnowledgeStore.Contains(SpecNumber, keyword)
                || KnowledgeStore.Contains(Clause, keyword) || KnowledgeStore.Contains(Content, keyword);
        }

        private static bool IsBlankText(string value) { return string.IsNullOrWhiteSpace(value); }
    }

    /// <summary>常用批注语：可复用的"批注内容"文本，新建批注时可从知识库挑一条填入内容框。</summary>
    internal sealed class KnowledgePhrase
    {
        public DateTime Time { get; set; }
        public string Text { get; set; }

        /// <summary>DataGrid 展示用。</summary>
        public string TimeString { get { return Time.ToString("yyyy-MM-dd HH:mm:ss"); } }
    }

    /// <summary>导入结果统计，用于导入完成后给用户一句明确的回执。</summary>
    internal sealed class KnowledgeImportResult
    {
        public int Lines;    // 读到的数据行数（不含表头）
        public int Added;    // 新增
        public int Updated;  // 按判重键更新
        public int Skipped;  // 空白行 / 无任何有效字段

        public string Describe()
        {
            return "读到 " + Lines + " 行：新增 " + Added + " 条，更新 " + Updated + " 条，跳过（空白或无有效字段）"
                + Skipped + " 条。";
        }
    }

    /// <summary>
    /// 知识库：独立的本地库，存放「规范条文条目」与「常用批注语」，落盘在
    /// <c>%AppData%\GMAnnotation\knowledge.json</c>（JSON Lines，每行一个对象，用 kind 区分两类）。
    ///
    /// <para><b>重要：知识库与「批注历史记录（留痕）」是两套互不关联的体系。</b>
    /// 留痕在 <c>history.json</c>（谁在什么时候创建/修改/删除了哪条批注、变更明细），由
    /// <see cref="AnnotationHistoryStore"/> 维护、在批注历史记录窗口里查询与清理；
    /// 知识库只关心"沉淀下来的条目和常用说法"，两者的增删清空互不影响。</para>
    ///
    /// <para>写入时机：① 批注窗口勾选「立即入库」后，创建/修改批注时调用 <see cref="Archive"/>；
    /// ② 在知识库窗口里手工新增/直接改表格/导入 CSV（<see cref="SaveAll"/>、<see cref="ImportEntriesCsv"/>）。
    /// 任何失败只记日志，绝不影响 CAD 操作。</para></summary>
    internal static class KnowledgeStore
    {
        private static readonly string Folder = AppPaths.DataFolder;
        private static readonly string PathName = Path.Combine(Folder, "knowledge.json");
        private const int MaxContentLength = 2000; // 单字段截断上限，防止超长内容撑爆文件

        private const string KindEntry = "entry";
        private const string KindPhrase = "phrase";

        /// <summary>知识库数据文件路径（供窗口提示用）。</summary>
        public static string FilePath { get { return PathName; } }

        /// <summary>破坏性操作（删除/清空/导入）前自动备份到的文件路径，仅保留最近一次。</summary>
        public static string BackupPath { get { return PathName + ".bak"; } }

        // ==================== 入库（批注归档） ====================

        /// <summary>
        /// 把一条批注沉淀进知识库：按"图 + 编号"更新或新增条目（只填专业与内容，规范名称/编号/条款留空待手填），
        /// 并把其"批注内容"并入常用批注语（去重）。由 <c>AnnotationService.ArchiveRecord</c> 在勾选「立即入库」时调用。
        /// </summary>
        public static bool Archive(Document doc, AnnotationData data)
        {
            try
            {
                if (data == null) return false;
                var drawingPath = "";
                try { drawingPath = doc == null ? "" : (doc.Name ?? ""); } catch { }

                var now = DateTime.Now;
                var entry = new KnowledgeEntry
                {
                    Id = NewId(),
                    Time = now,
                    User = AnnotationHistoryStore.CurrentUser(),
                    Drawing = string.IsNullOrEmpty(drawingPath) ? "(未命名)" : Path.GetFileName(drawingPath),
                    DrawingPath = drawingPath,
                    DrawingNo = data.DrawingNo ?? "",
                    Number = data.Number ?? "",
                    Date = data.Date ?? "",
                    Discipline = data.Discipline ?? "",
                    Author = data.Author ?? "",
                    Role = data.Role ?? "",
                    Status = data.Status ?? "",
                    Content = Truncate(data.Content)
                };

                var snapshot = Load();
                // 兼容键里必须至少含"图"或"编号"才算有效身份：导入的条文条目这两个字段都是空的，
                // 全都退化成 "|"，若不拦一刀，图名为空且没编号的批注就会误更新第一条导入数据。
                var legacyKey = entry.LegacyKey();
                var index = legacyKey.Trim('|').Length == 0
                    ? -1
                    : snapshot.Entries.FindIndex(e => string.Equals(e.LegacyKey(), legacyKey, StringComparison.Ordinal));
                if (index >= 0)
                {
                    // 更新而不是新增：保留原有 Id（窗口里选中的还是同一条）与已手工填好的规范字段。
                    var old = snapshot.Entries[index];
                    entry.Id = string.IsNullOrEmpty(old.Id) ? entry.Id : old.Id;
                    entry.Attribute = old.Attribute;
                    entry.SpecName = old.SpecName;
                    entry.SpecNumber = old.SpecNumber;
                    entry.Clause = old.Clause;
                    snapshot.Entries[index] = entry;
                }
                else snapshot.Entries.Add(entry);

                // 内容同时沉淀为常用批注语（同样内容只留一条）。
                var text = (entry.Content ?? "").Trim();
                if (text.Length > 0 && !HasPhrase(snapshot.Phrases, text))
                    snapshot.Phrases.Add(new KnowledgePhrase { Time = now, Text = text });

                return Save(snapshot);
            }
            catch (Exception ex) { PluginLog.Warning("Knowledge.Archive", ex.Message); return false; }
        }

        // ==================== 读取 ====================

        /// <summary>读取全部条目（文件顺序；列表按此顺序显示并从 1 编号）。</summary>
        public static List<KnowledgeEntry> LoadEntries()
        {
            return Load().Entries;
        }

        /// <summary>读取全部常用批注语。</summary>
        public static List<KnowledgePhrase> LoadPhrases()
        {
            return Load().Phrases;
        }

        /// <summary>关键字比较（不区分大小写），窗口过滤与条目匹配共用。</summary>
        internal static bool Contains(string value, string keyword)
        {
            return (value ?? "").IndexOf(keyword ?? "", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // ==================== 保存（表格编辑 / 新增行） ====================

        /// <summary>
        /// 用给定清单整体覆盖知识库的两类记录（窗口里直接改表格、点「新增」后调用）。
        /// 这是**常规保存**，不备份（每次编辑都备份会把 .bak 冲成最新态，反而失去保护意义）；
        /// 删除/清空/导入这些破坏性操作由各自方法单独备份。
        /// </summary>
        public static bool SaveAll(IEnumerable<KnowledgeEntry> entries, IEnumerable<KnowledgePhrase> phrases)
        {
            try
            {
                var snapshot = Load();
                var list = entries == null ? new List<KnowledgeEntry>() : entries.ToList();
                var text = phrases == null ? new List<KnowledgePhrase>() : phrases.ToList();
                foreach (var e in list) if (string.IsNullOrEmpty(e.Id)) e.Id = NewId();
                snapshot.Entries = list;
                snapshot.Phrases = text;
                return Save(snapshot);
            }
            catch (Exception ex) { PluginLog.Warning("Knowledge.SaveAll", ex.Message); return false; }
        }

        // ==================== 常用批注语维护 ====================

        /// <summary>新增一条常用批注语。返回 1=已添加，0=内容为空或已存在，-1=写入失败。</summary>
        public static int AddPhrase(string text)
        {
            try
            {
                var value = (text ?? "").Trim();
                if (value.Length == 0) return 0;
                var snapshot = Load();
                if (HasPhrase(snapshot.Phrases, value)) return 0;
                snapshot.Phrases.Add(new KnowledgePhrase { Time = DateTime.Now, Text = Truncate(value) });
                return Save(snapshot) ? 1 : -1;
            }
            catch (Exception ex) { PluginLog.Warning("Knowledge.AddPhrase", ex.Message); return -1; }
        }

        /// <summary>把选中的常用批注语替换为新内容。</summary>
        public static bool ReplacePhrase(KnowledgePhrase target, string newText)
        {
            try
            {
                if (target == null) return false;
                var value = (newText ?? "").Trim();
                if (value.Length == 0) return false;
                var snapshot = Load();
                var index = snapshot.Phrases.FindIndex(p => string.Equals(p.Text ?? "", target.Text ?? "", StringComparison.Ordinal));
                if (index < 0) return false;
                if (!string.Equals(snapshot.Phrases[index].Text ?? "", value, StringComparison.Ordinal) && HasPhrase(snapshot.Phrases, value))
                    return false; // 改成的内容已存在
                snapshot.Phrases[index] = new KnowledgePhrase { Time = snapshot.Phrases[index].Time, Text = Truncate(value) };
                return Save(snapshot);
            }
            catch (Exception ex) { PluginLog.Warning("Knowledge.ReplacePhrase", ex.Message); return false; }
        }

        // ==================== 删除 / 清空 ====================

        /// <summary>从知识库删除若干条目（按 Id 匹配，先备份）。</summary>
        public static bool RemoveEntries(IEnumerable<KnowledgeEntry> remove)
        {
            try
            {
                var targets = remove == null ? new List<KnowledgeEntry>() : remove.ToList();
                if (targets.Count == 0) return false;
                var keys = new HashSet<string>(targets.Select(e => e.Identity()), StringComparer.Ordinal);
                var snapshot = Load();
                var before = snapshot.Entries.Count;
                snapshot.Entries.RemoveAll(e => keys.Contains(e.Identity()));
                if (snapshot.Entries.Count == before) return false;
                Backup();
                return Save(snapshot);
            }
            catch (Exception ex) { PluginLog.Warning("Knowledge.RemoveEntries", ex.Message); return false; }
        }

        /// <summary>清空全部条目（常用批注语保留，先备份）。</summary>
        public static bool ClearEntries()
        {
            try
            {
                var snapshot = Load();
                if (snapshot.Entries.Count == 0) return false;
                snapshot.Entries.Clear();
                Backup();
                return Save(snapshot);
            }
            catch (Exception ex) { PluginLog.Warning("Knowledge.ClearEntries", ex.Message); return false; }
        }

        /// <summary>从知识库删除若干常用批注语（先备份）。</summary>
        public static bool RemovePhrases(IEnumerable<KnowledgePhrase> remove)
        {
            try
            {
                var targets = remove == null ? new List<KnowledgePhrase>() : remove.ToList();
                if (targets.Count == 0) return false;
                var texts = new HashSet<string>(targets.Select(p => p.Text ?? ""), StringComparer.Ordinal);
                var snapshot = Load();
                var before = snapshot.Phrases.Count;
                snapshot.Phrases.RemoveAll(p => texts.Contains(p.Text ?? ""));
                if (snapshot.Phrases.Count == before) return false;
                Backup();
                return Save(snapshot);
            }
            catch (Exception ex) { PluginLog.Warning("Knowledge.RemovePhrases", ex.Message); return false; }
        }

        /// <summary>清空全部常用批注语（条目保留，先备份）。</summary>
        public static bool ClearPhrases()
        {
            try
            {
                var snapshot = Load();
                if (snapshot.Phrases.Count == 0) return false;
                snapshot.Phrases.Clear();
                Backup();
                return Save(snapshot);
            }
            catch (Exception ex) { PluginLog.Warning("Knowledge.ClearPhrases", ex.Message); return false; }
        }

        // ==================== 导出 ====================

        /// <summary>
        /// 导出条目 CSV（UTF-8 BOM，Excel 双击可开）。列序与列表一致：
        /// <c>序号,专业,属性,规范名称,规范编号,条款,内容</c>——**导出的文件本身就是导入模板**，改完直接导回即可。
        /// 序号按传入顺序从 1 重排，导入时忽略（不参与判重）。
        /// </summary>
        public static void ExportEntriesCsv(string filePath, IEnumerable<KnowledgeEntry> records)
        {
            var sb = new StringBuilder();
            sb.AppendLine("序号,专业,属性,规范名称,规范编号,条款,内容");
            var index = 0;
            foreach (var e in records ?? new List<KnowledgeEntry>())
            {
                index++;
                sb.AppendLine(string.Join(",", new[]
                {
                    index.ToString(),
                    AnnotationHistoryStore.Csv(e.Discipline), AnnotationHistoryStore.Csv(e.Attribute),
                    AnnotationHistoryStore.Csv(e.SpecName), AnnotationHistoryStore.Csv(e.SpecNumber),
                    AnnotationHistoryStore.Csv(e.Clause), AnnotationHistoryStore.Csv(e.Content)
                }));
            }
            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
        }

        /// <summary>导出常用批注语 CSV（表头含「常用批注语」列，可原样导回）。</summary>
        public static void ExportPhrasesCsv(string filePath, IEnumerable<KnowledgePhrase> records)
        {
            var sb = new StringBuilder();
            sb.AppendLine("加入时间,常用批注语");
            foreach (var p in records ?? new List<KnowledgePhrase>())
                sb.AppendLine(string.Join(",", new[]
                {
                    AnnotationHistoryStore.Csv(p.Time.ToString("yyyy-MM-dd HH:mm:ss")), AnnotationHistoryStore.Csv(p.Text)
                }));
            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
        }

        // ==================== 导入 ====================

        /// <summary>
        /// 从 CSV 导入条目。表头按名称识别（序号/专业/属性/规范名称/规范编号/条款/内容，兼容常见别名与 Excel 导出的
        /// 多余空格）；识别不到表头时按固定顺序 专业,属性,规范名称,规范编号,条款,内容 解析。
        ///
        /// 判重：<b>规范编号 + 条款</b>相同即视为同一条（更新），两者都空则一律新增。
        /// 导入前自动备份一次，导入的是"合并结果"，不覆盖清单里没有的既有条目。
        /// </summary>
        public static KnowledgeImportResult ImportEntriesCsv(string filePath)
        {
            var result = new KnowledgeImportResult();
            try
            {
                var rows = ReadCsv(filePath);
                if (rows.Count == 0) return result;

                var header = rows[0];
                var hasHeader = LooksLikeEntryHeader(header);
                var start = hasHeader ? 1 : 0;

                int cDiscipline, cAttribute, cSpecName, cSpecNumber, cClause, cContent;
                if (hasHeader)
                {
                    var claimed = new HashSet<int>();
                    // 先认「规范编号」再认「规范名称」，避免"规范"这种短别名把"规范编号"列抢走。
                    cSpecNumber = ColumnIndexOf(header, claimed, new[] { "规范编号", "标准编号", "规范号", "文件编号", "编号" });
                    cSpecName = ColumnIndexOf(header, claimed, new[] { "规范名称", "规范名", "标准名称", "规范", "来源", "出处" });
                    cClause = ColumnIndexOf(header, claimed, new[] { "条款", "条款号", "条文号", "条号", "章条" });
                    cAttribute = ColumnIndexOf(header, claimed, new[] { "属性", "性质", "条文属性" });
                    cDiscipline = ColumnIndexOf(header, claimed, new[] { "专业" });
                    cContent = ColumnIndexOf(header, claimed, new[] { "内容", "条文内容", "批注内容", "正文", "条文" });
                    if (cSpecNumber < 0 && cSpecName < 0 && cClause < 0 && cContent < 0)
                        throw new InvalidDataException("表头里没有找到 规范名称/规范编号/条款/内容 中的任何一列，无法确定对应关系。");
                }
                else
                {
                    cDiscipline = 0; cAttribute = 1; cSpecName = 2; cSpecNumber = 3; cClause = 4; cContent = 5;
                }

                var snapshot = Load();
                var now = DateTime.Now;
                var user = AnnotationHistoryStore.CurrentUser();
                for (var i = start; i < rows.Count; i++)
                {
                    var row = rows[i];
                    var entry = new KnowledgeEntry
                    {
                        Id = NewId(),
                        Time = now,
                        User = user,
                        Discipline = Cell(row, cDiscipline),
                        Attribute = Cell(row, cAttribute),
                        SpecName = Cell(row, cSpecName),
                        SpecNumber = Cell(row, cSpecNumber),
                        Clause = Cell(row, cClause),
                        Content = Truncate(Cell(row, cContent))
                    };
                    if (!RowHasAnyText(row)) continue; // 整行空白：连行数都不计
                    result.Lines++;
                    if (entry.IsBlank()) { result.Skipped++; continue; } // 有内容但都不在映射到的列里

                    var key = entry.SpecKey();
                    if (key.Length == 0) { snapshot.Entries.Add(entry); result.Added++; continue; }

                    var index = snapshot.Entries.FindIndex(e => string.Equals(e.SpecKey(), key, StringComparison.Ordinal));
                    if (index >= 0) { entry.Id = snapshot.Entries[index].Id; snapshot.Entries[index] = entry; result.Updated++; }
                    else { snapshot.Entries.Add(entry); result.Added++; }
                }

                if (result.Added == 0 && result.Updated == 0) return result;
                Backup();
                Save(snapshot);
                return result;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Knowledge.ImportEntriesCsv", ex);
                throw;
            }
        }

        /// <summary>
        /// 从 CSV 导入常用批注语。有表头（含「常用批注语 / 批注内容 / 内容 / 文本」列）就取那一列，
        /// 没有表头就取第一列逐行读；同内容只保留一条。
        /// </summary>
        public static KnowledgeImportResult ImportPhrasesCsv(string filePath)
        {
            var result = new KnowledgeImportResult();
            try
            {
                var rows = ReadCsv(filePath);
                if (rows.Count == 0) return result;

                var column = 0;
                var start = 0;
                var headerIndex = ColumnIndexOf(rows[0], new HashSet<int>(), new[] { "常用批注语", "批注内容", "内容", "文本", "text" });
                if (headerIndex >= 0) { column = headerIndex; start = 1; }

                var snapshot = Load();
                var now = DateTime.Now;
                for (var i = start; i < rows.Count; i++)
                {
                    var text = Cell(rows[i], column).Trim();
                    if (text.Length == 0) { if (RowHasAnyText(rows[i])) result.Skipped++; continue; }
                    result.Lines++;
                    if (HasPhrase(snapshot.Phrases, text)) { result.Skipped++; continue; }
                    snapshot.Phrases.Add(new KnowledgePhrase { Time = now, Text = Truncate(text) });
                    result.Added++;
                }

                if (result.Added == 0) return result;
                Backup();
                Save(snapshot);
                return result;
            }
            catch (Exception ex)
            {
                PluginLog.Error("Knowledge.ImportPhrasesCsv", ex);
                throw;
            }
        }

        // ==================== 内部实现 ====================

        /// <summary>文件快照：两类记录 + 无法解析的脏行（原样保留，不因为一次保存就丢数据）。</summary>
        private sealed class Snapshot
        {
            public List<KnowledgeEntry> Entries = new List<KnowledgeEntry>();
            public List<KnowledgePhrase> Phrases = new List<KnowledgePhrase>();
            public List<string> Others = new List<string>();
        }

        private static Snapshot Load()
        {
            var snapshot = new Snapshot();
            try
            {
                if (!File.Exists(PathName)) return snapshot;
                foreach (var line in File.ReadLines(PathName, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var kind = AnnotationHistoryStore.GetString(line, "kind");
                    if (string.Equals(kind, KindEntry, StringComparison.Ordinal))
                    {
                        var entry = new KnowledgeEntry
                        {
                            Id = AnnotationHistoryStore.GetString(line, "id"),
                            Time = ParseTime(line),
                            User = AnnotationHistoryStore.GetString(line, "user"),
                            Discipline = AnnotationHistoryStore.GetString(line, "discipline"),
                            Attribute = AnnotationHistoryStore.GetString(line, "attribute"),
                            SpecName = AnnotationHistoryStore.GetString(line, "specName"),
                            SpecNumber = AnnotationHistoryStore.GetString(line, "specNumber"),
                            Clause = AnnotationHistoryStore.GetString(line, "clause"),
                            Content = AnnotationHistoryStore.GetString(line, "content"),
                            Drawing = AnnotationHistoryStore.GetString(line, "drawing"),
                            DrawingPath = AnnotationHistoryStore.GetString(line, "drawingPath"),
                            DrawingNo = AnnotationHistoryStore.GetString(line, "drawingNo"),
                            Number = AnnotationHistoryStore.GetString(line, "number"),
                            Date = AnnotationHistoryStore.GetString(line, "date"),
                            Author = AnnotationHistoryStore.GetString(line, "author"),
                            Role = AnnotationHistoryStore.GetString(line, "role"),
                            Status = AnnotationHistoryStore.GetString(line, "status")
                        };
                        // 老文件没有 id：补一个稳定的（由图+编号派生），保证同一份数据每次读到的身份一致，
                        // 首次保存时会被写回文件。
                        if (string.IsNullOrEmpty(entry.Id)) entry.Id = DerivedId(entry.LegacyKey());
                        if (!entry.IsBlank() || !string.IsNullOrEmpty(entry.Number)) snapshot.Entries.Add(entry);
                        else snapshot.Others.Add(line);
                    }
                    else if (string.Equals(kind, KindPhrase, StringComparison.Ordinal))
                    {
                        var text = AnnotationHistoryStore.GetString(line, "text");
                        if (text.Trim().Length > 0) snapshot.Phrases.Add(new KnowledgePhrase { Time = ParseTime(line), Text = text });
                        else snapshot.Others.Add(line);
                    }
                    else snapshot.Others.Add(line);
                }
            }
            catch (Exception ex) { PluginLog.Warning("Knowledge.Load", ex.Message); }
            return snapshot;
        }

        /// <summary>整体重写知识库文件：先写 .tmp 再替换，避免中途失败把文件截断。</summary>
        private static bool Save(Snapshot snapshot)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var lines = new List<string>(snapshot.Others);
                foreach (var e in snapshot.Entries) lines.Add(EntryJson(e));
                foreach (var p in snapshot.Phrases) lines.Add(PhraseJson(p));
                var temp = PathName + ".tmp";
                File.WriteAllText(temp, lines.Count > 0 ? string.Join("\r\n", lines) + "\r\n" : "", Encoding.UTF8);
                if (File.Exists(PathName)) File.Delete(PathName);
                File.Move(temp, PathName);
                return true;
            }
            catch (Exception ex) { PluginLog.Warning("Knowledge.Save", ex.Message); return false; }
        }

        /// <summary>破坏性操作前把整个知识库文件原样备份一份。</summary>
        private static void Backup()
        {
            try { if (File.Exists(PathName)) File.Copy(PathName, BackupPath, true); }
            catch (Exception ex) { PluginLog.Warning("Knowledge.Backup", ex.Message); }
        }

        private static bool HasPhrase(List<KnowledgePhrase> phrases, string text)
        {
            if (phrases == null) return false;
            foreach (var p in phrases)
                if (string.Equals((p.Text ?? "").Trim(), text, StringComparison.Ordinal)) return true;
            return false;
        }

        private static DateTime ParseTime(string line)
        {
            return DateTime.TryParse(AnnotationHistoryStore.GetString(line, "time"), out var t) ? t : DateTime.Now;
        }

        private static string Truncate(string value)
        {
            // 不像留痕那样把换行压成空格：知识库内容要保留条文/回复/多行的原貌，换行由 JSON 转义承载。
            value = value ?? "";
            return value.Length <= MaxContentLength ? value : value.Substring(0, MaxContentLength) + "...";
        }

        private static string NewId()
        {
            return Guid.NewGuid().ToString("N");
        }

        /// <summary>由兼容键派生一个稳定 Id（老数据没有 id 时用，长度固定 8+16 位十六进制字符串）。</summary>
        private static string DerivedId(string legacyKey)
        {
            try
            {
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(legacyKey ?? ""));
                    var sb = new StringBuilder("k", 33);
                    for (var i = 0; i < 16; i++) sb.Append(bytes[i].ToString("x2"));
                    return sb.ToString();
                }
            }
            catch { return NewId(); }
        }

        private static string EntryJson(KnowledgeEntry e)
        {
            return "{" + string.Join(",", new[]
            {
                AnnotationHistoryStore.Q("kind", KindEntry),
                AnnotationHistoryStore.Q("id", e.Id),
                AnnotationHistoryStore.Q("time", e.Time.ToString("yyyy-MM-dd HH:mm:ss")),
                AnnotationHistoryStore.Q("user", e.User),
                AnnotationHistoryStore.Q("discipline", e.Discipline), AnnotationHistoryStore.Q("attribute", e.Attribute),
                AnnotationHistoryStore.Q("specName", e.SpecName), AnnotationHistoryStore.Q("specNumber", e.SpecNumber),
                AnnotationHistoryStore.Q("clause", e.Clause), AnnotationHistoryStore.Q("content", e.Content),
                AnnotationHistoryStore.Q("drawing", e.Drawing), AnnotationHistoryStore.Q("drawingPath", e.DrawingPath),
                AnnotationHistoryStore.Q("drawingNo", e.DrawingNo), AnnotationHistoryStore.Q("number", e.Number),
                AnnotationHistoryStore.Q("date", e.Date), AnnotationHistoryStore.Q("author", e.Author),
                AnnotationHistoryStore.Q("role", e.Role), AnnotationHistoryStore.Q("status", e.Status)
            }) + "}";
        }

        private static string PhraseJson(KnowledgePhrase p)
        {
            return "{" + string.Join(",", new[]
            {
                AnnotationHistoryStore.Q("kind", KindPhrase),
                AnnotationHistoryStore.Q("time", p.Time.ToString("yyyy-MM-dd HH:mm:ss")),
                AnnotationHistoryStore.Q("text", p.Text)
            }) + "}";
        }

        // ---------- CSV 读写 ----------

        /// <summary>读整个 CSV 文件并按行/单元格切开（支持引号包裹、引号内换行、"" 转义）。</summary>
        private static List<List<string>> ReadCsv(string filePath)
        {
            var rows = new List<List<string>>();
            var text = ReadAllText(filePath);
            var row = new List<string>();
            var cell = new StringBuilder();
            var inQuotes = false;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else cell.Append(c);
                    continue;
                }
                if (c == '"') inQuotes = true;
                else if (c == ',') { row.Add(cell.ToString()); cell.Length = 0; }
                else if (c == '\r')
                {
                    if (i + 1 < text.Length && text[i + 1] == '\n') continue; // 交给 \n 收尾
                    row.Add(cell.ToString()); cell.Length = 0; rows.Add(row); row = new List<string>();
                }
                else if (c == '\n')
                {
                    row.Add(cell.ToString()); cell.Length = 0; rows.Add(row); row = new List<string>();
                }
                else cell.Append(c);
            }
            if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
            // 丢掉结尾的空行（文件尾的换行会产生一行空串）
            while (rows.Count > 0 && rows[rows.Count - 1].Count == 1 && rows[rows.Count - 1][0].Trim().Length == 0)
                rows.RemoveAt(rows.Count - 1);
            return rows;
        }

        /// <summary>
        /// 读文本并猜编码：UTF-8 BOM → 严格 UTF-8（解不出来就说明不是 UTF-8）→ GBK(936) → 系统默认。
        /// Excel「另存为 CSV」在中文 Windows 上默认是 GBK，所以必须留这条路。
        /// </summary>
        private static string ReadAllText(string filePath)
        {
            var bytes = File.ReadAllBytes(filePath);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (Exception) { /* 不是合法 UTF-8，继续往下试 */ }
            try { return Encoding.GetEncoding(936).GetString(bytes); }
            catch (Exception) { return Encoding.Default.GetString(bytes); }
        }

        /// <summary>表头识别：整行里只要有单元格是已知的列名，就当成表头。</summary>
        private static bool LooksLikeEntryHeader(List<string> row)
        {
            if (row == null) return false;
            foreach (var cell in row)
            {
                var name = NormalizeHeader(cell);
                if (name.Length == 0) continue;
                if (EqualsAny(name, EntryHeaderNames)) return true;
            }
            return false;
        }

        private static readonly string[] EntryHeaderNames =
        {
            "序号", "专业", "属性", "性质", "条文属性", "规范名称", "规范名", "标准名称", "规范编号", "标准编号",
            "规范号", "文件编号", "编号", "条款", "条款号", "条文号", "条号", "章条", "内容", "条文内容",
            "批注内容", "正文", "条文", "规范", "来源", "出处"
        };

        /// <summary>按别名找列号：先精确匹配，再退一步用"包含"匹配；已被占用的列不再参与。</summary>
        private static int ColumnIndexOf(List<string> header, HashSet<int> claimed, string[] aliases)
        {
            if (header == null) return -1;
            foreach (var alias in aliases)
                for (var i = 0; i < header.Count; i++)
                {
                    if (claimed.Contains(i)) continue;
                    if (NormalizeHeader(header[i]) == alias) { claimed.Add(i); return i; }
                }
            foreach (var alias in aliases)
                for (var i = 0; i < header.Count; i++)
                {
                    if (claimed.Contains(i)) continue;
                    if (NormalizeHeader(header[i]).IndexOf(alias, StringComparison.Ordinal) >= 0) { claimed.Add(i); return i; }
                }
            return -1;
        }

        private static bool EqualsAny(string value, string[] names)
        {
            foreach (var name in names) if (string.Equals(value, name, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>列名归一：去 BOM、去空白（含全角空格）、去 Excel 常见的 * 与冒号。</summary>
        private static string NormalizeHeader(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (char.IsWhiteSpace(c) || c == '\u3000' || c == '*' || c == '：' || c == ':' || c == '\uFEFF') continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static string Cell(List<string> row, int index)
        {
            if (row == null || index < 0 || index >= row.Count) return "";
            return (row[index] ?? "").Trim();
        }

        private static bool RowHasAnyText(List<string> row)
        {
            if (row == null) return false;
            foreach (var cell in row) if (!string.IsNullOrWhiteSpace(cell)) return true;
            return false;
        }
    }
}
