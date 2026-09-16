using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
#if ZWCAD
using ZwSoft.ZwCAD.ApplicationServices;
using CadApplication = ZwSoft.ZwCAD.ApplicationServices.Core.Application;
#else
using Autodesk.AutoCAD.ApplicationServices;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#endif

namespace GMAnnotation
{
    /// <summary>单条批注留痕记录。</summary>
    internal sealed class AnnotationHistoryRecord
    {
        public DateTime Time { get; set; }
        public string User { get; set; }
        public string Action { get; set; }        // 创建/修改/删除/移动/增补云线/复制修复
        public string Drawing { get; set; }       // 图名（DWG 文件名，不含路径）
        public string DrawingPath { get; set; }   // 完整路径
        public string AnnotationId { get; set; }  // 批注 Id
        public string Number { get; set; }
        public string Date { get; set; }
        public string Discipline { get; set; }
        public string DrawingNo { get; set; }     // 图号
        public string Author { get; set; }
        public string Role { get; set; }
        public string Status { get; set; }
        public string Content { get; set; }
        public string Changes { get; set; }       // 变更明细（修改时填）
    }

    /// <summary>批注历史记录（留痕）：按 JSON Lines 追加写入 %AppData%/GMAnnotation/history.json，
    /// 支持按图名/编号/内容等关键字过滤查询、导出 CSV（Excel 可直接打开）。任何失败只记日志，不影响 CAD 操作。
    /// 注意：这里只记"谁在什么时候创建/修改/删除了哪条批注"，与「知识库」
    /// （<see cref="KnowledgeStore"/> / knowledge.json，规范条文条目 + 常用批注语）是两套互不关联的体系。</summary>
    internal static class AnnotationHistoryStore
    {
        private static readonly string Folder = AppPaths.DataFolder;
        private static readonly string PathName = Path.Combine(Folder, "history.json");
        private const int MaxContentLength = 500; // 单字段截断上限，防止超长内容撑爆记录

        /// <summary>记录一条留痕（供各业务动作在事务提交成功后调用）。</summary>
        public static void Record(Document doc, string action, AnnotationData data, string changes)
        {
            try
            {
                if (data == null) return;
                var drawingPath = "";
                try { drawingPath = doc?.Name ?? ""; } catch { }
                var record = new AnnotationHistoryRecord
                {
                    Time = DateTime.Now,
                    User = CurrentUser(),
                    Action = action,
                    Drawing = string.IsNullOrEmpty(drawingPath) ? "(未命名)" : Path.GetFileName(drawingPath),
                    DrawingPath = drawingPath,
                    AnnotationId = data.Id,
                    Number = Truncate(data.Number),
                    Date = data.Date,
                    Discipline = data.Discipline,
                    DrawingNo = data.DrawingNo,
                    Author = data.Author,
                    Role = data.Role,
                    Status = data.Status,
                    Content = Truncate(data.Content),
                    Changes = Truncate(changes)
                };
                Directory.CreateDirectory(Folder);
                File.AppendAllText(PathName, ToJson(record) + "\r\n", Encoding.UTF8);
            }
            catch (Exception ex) { PluginLog.Warning("History.Record", ex.Message); }
        }

        /// <summary>读取全部留痕（按时间正序返回）。</summary>
        public static List<AnnotationHistoryRecord> LoadAll()
        {
            var list = new List<AnnotationHistoryRecord>();
            try
            {
                if (!File.Exists(PathName)) return list;
                foreach (var line in File.ReadLines(PathName, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var record = Parse(line);
                    if (record != null) list.Add(record);
                }
            }
            catch (Exception ex) { PluginLog.Warning("History.LoadAll", ex.Message); }
            return list;
        }

        /// <summary>清理前自动备份到的文件路径（每次清理覆盖写入，仅保留最近一次清理前的完整历史）。</summary>
        public static string BackupPath { get { return PathName + ".bak"; } }

        /// <summary>
        /// 清理留痕：把 <paramref name="remove"/> 中的记录从 history.json 删除，其余原样保留，
        /// 返回实际删除条数；失败返回 -1。清理前自动备份原文件；重写采用"先写 .tmp 再替换"，
        /// 避免中途失败把历史文件截断。无法解析的脏行一律保留。
        /// </summary>
        /// <remarks>
        /// 记录除时间外没有唯一标识，因此内存记录与磁盘行按"时间+操作人+操作+图名+编号+内容+变更明细"
        /// 组合键对应——同一秒内写入且这些字段完全相同的多条记录会被一并清理，属可接受误差。
        /// </remarks>
        public static int Cleanup(IEnumerable<AnnotationHistoryRecord> remove)
        {
            try
            {
                var targets = remove == null ? new List<AnnotationHistoryRecord>() : remove.ToList();
                if (targets.Count == 0) return 0;
                if (!File.Exists(PathName)) return 0;
                var keys = new HashSet<string>(targets.Select(KeyOf), StringComparer.Ordinal);
                var keep = new List<string>(); var removed = 0;
                foreach (var line in File.ReadAllLines(PathName, Encoding.UTF8))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var record = Parse(line);
                    if (record != null && keys.Contains(KeyOf(record))) { removed++; continue; }
                    keep.Add(line);
                }
                if (removed == 0) return 0;

                try { File.Copy(PathName, BackupPath, true); }
                catch (Exception ex) { PluginLog.Warning("History.Backup", ex.Message); }

                var temp = PathName + ".tmp";
                File.WriteAllText(temp, keep.Count > 0 ? string.Join("\r\n", keep) + "\r\n" : "", Encoding.UTF8);
                if (File.Exists(PathName)) File.Delete(PathName);
                File.Move(temp, PathName);
                return removed;
            }
            catch (Exception ex) { PluginLog.Warning("History.Cleanup", ex.Message); return -1; }
        }

        /// <summary>记录身份键：内存记录与磁盘行靠它对应。</summary>
        private static string KeyOf(AnnotationHistoryRecord r)
        {
            return string.Join("|", new[]
            {
                r.Time.ToString("yyyy-MM-dd HH:mm:ss"), r.User ?? "", r.Action ?? "",
                r.Drawing ?? "", r.Number ?? "", r.Content ?? "", r.Changes ?? ""
            });
        }

        /// <summary>导出为 CSV（UTF-8 BOM，Excel 可直接打开）。</summary>
        public static void ExportCsv(string filePath, IEnumerable<AnnotationHistoryRecord> records)
        {
            var sb = new StringBuilder();
            sb.AppendLine("时间,操作,图名,图号,编号,批注日期,专业,批注人,角色,状态,批注内容,变更明细,操作人");
            foreach (var r in records)
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(r.Time.ToString("yyyy-MM-dd HH:mm:ss")), Csv(r.Action), Csv(r.Drawing), Csv(r.DrawingNo), Csv(r.Number),
                    Csv(r.Date), Csv(r.Discipline), Csv(r.Author), Csv(r.Role), Csv(r.Status),
                    Csv(r.Content), Csv(r.Changes), Csv(r.User)
                }));
            }
            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
        }

        // ---------- 图号按图记忆（drawingnos.json：{ DWG路径: 图号 }） ----------

        private static readonly string DrawingNoPath = Path.Combine(Folder, "drawingnos.json");

        /// <summary>取某张 DWG 上次使用的图号；没有则返回空串。</summary>
        public static string GetDrawingNo(string drawingPath)
        {
            try
            {
                if (string.IsNullOrEmpty(drawingPath) || !File.Exists(DrawingNoPath)) return "";
                var match = Regex.Match(File.ReadAllText(DrawingNoPath, Encoding.UTF8),
                    Regex.Escape(Escape(drawingPath)) + "\":\"((?:[^\"\\\\]|\\\\.)*)\"");
                return match.Success ? Unescape(match.Groups[1].Value) : "";
            }
            catch (Exception ex) { PluginLog.Warning("History.GetDrawingNo", ex.Message); return ""; }
        }

        /// <summary>记住某张 DWG 的图号，下次打开批注窗口自动带出。</summary>
        public static void SetDrawingNo(string drawingPath, string drawingNo)
        {
            try
            {
                if (string.IsNullOrEmpty(drawingPath)) return;
                Directory.CreateDirectory(Folder);
                var text = File.Exists(DrawingNoPath) ? File.ReadAllText(DrawingNoPath, Encoding.UTF8) : "";
                text = text.Trim();
                if (text.StartsWith("{") && text.EndsWith("}")) text = text.Substring(1, text.Length - 2); else text = "";
                // 去掉本图的旧记录，追加新记录。
                var pattern = "\\s*\"" + Regex.Escape(Escape(drawingPath)) + "\":\"(?:[^\"\\\\]|\\\\.)*\",?";
                text = Regex.Replace(text, pattern, "");
                text = text.TrimEnd().TrimEnd(',');
                var entry = Q(drawingPath, drawingNo ?? "");
                File.WriteAllText(DrawingNoPath, "{" + (text.Length > 0 ? text + "," : "") + entry + "}", Encoding.UTF8);
            }
            catch (Exception ex) { PluginLog.Warning("History.SetDrawingNo", ex.Message); }
        }

        /// <summary>
        /// "历史"下拉（最近批注内容）被清除的时间标记文件：写入清除那一刻的时间，早于该时间的留痕内容
        /// 不再出现在下拉里。用时间标记而不是删留痕，是为了"清空下拉"与"删除批注历史记录"两件事互不牵连。
        /// </summary>
        private static readonly string RecentClearedPath = Path.Combine(Folder, "recentcontents.cleared");

        /// <summary>"历史"下拉最近一次被清除的时间；没有标记时返回 DateTime.MinValue（表示不过滤）。</summary>
        private static DateTime RecentClearedBefore()
        {
            try
            {
                if (!File.Exists(RecentClearedPath)) return DateTime.MinValue;
                return DateTime.TryParse(File.ReadAllText(RecentClearedPath, Encoding.UTF8).Trim(), out var t)
                    ? t : DateTime.MinValue;
            }
            catch (Exception ex) { PluginLog.Warning("History.RecentClearedBefore", ex.Message); return DateTime.MinValue; }
        }

        /// <summary>
        /// 清空批注窗口"历史"下拉里的最近批注内容：只写一个清除时间标记，
        /// 之后下拉重新从新产生的批注内容积累；history.json 留痕记录原样保留（批注历史记录窗口的"清理"才删留痕）。
        /// </summary>
        public static bool ClearRecentContents()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(RecentClearedPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), Encoding.UTF8);
                return true;
            }
            catch (Exception ex) { PluginLog.Warning("History.ClearRecentContents", ex.Message); return false; }
        }

        /// <summary>从历史留痕提取"最近使用过的批注内容"（去重、最新在前），供批注窗口历史下拉选用。
        /// 已被"清除"过的旧内容（时间早于清除标记）不再返回。</summary>
        public static List<string> LoadRecentContents(int max = 50)
        {
            var result = new List<string>();
            try
            {
                var cleared = RecentClearedBefore();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                // LoadAll 按写入顺序（旧 → 新）返回；这里从末尾倒序遍历，
                // 保证"最新使用过的批注内容"排在下拉最前，且取到的是最近 max 条而非最早 max 条。
                var all = LoadAll();
                for (var i = all.Count - 1; i >= 0; i--)
                {
                    // 时间只精确到秒：与"清除"同一秒内写入的批注会被一并滤掉，属可接受误差。
                    if (all[i].Time <= cleared) continue;
                    var content = (all[i].Content ?? "").Trim();
                    if (content.Length == 0 || !seen.Add(content)) continue;
                    result.Add(content);
                    if (result.Count >= max) break;
                }
            }
            catch (Exception ex) { PluginLog.Warning("History.LoadRecentContents", ex.Message); }
            return result;
        }

        /// <summary>生成修改留痕的变更明细：逐字段比较新旧值，格式"字段: 旧值 → 新值"。</summary>
        public static string Diff(AnnotationData oldData, AnnotationData newData)
        {
            if (oldData == null) return "";
            var parts = new List<string>();
            Compare(parts, "编号", oldData.Number, newData.Number);
            Compare(parts, "图号", oldData.DrawingNo, newData.DrawingNo);
            Compare(parts, "日期", oldData.Date, newData.Date);
            Compare(parts, "专业", oldData.Discipline, newData.Discipline);
            Compare(parts, "批注人", oldData.Author, newData.Author);
            Compare(parts, "角色", oldData.Role, newData.Role);
            Compare(parts, "状态", oldData.Status, newData.Status);
            Compare(parts, "内容", oldData.Content, newData.Content);
            return string.Join("; ", parts);
        }

        // ---------- 内部实现 ----------

        private static void Compare(List<string> parts, string field, string oldV, string newV)
        {
            if (string.Equals(oldV ?? "", newV ?? "", StringComparison.Ordinal)) return;
            parts.Add(field + ": " + (oldV ?? "(空)") + " → " + (newV ?? "(空)"));
        }

        private static string Truncate(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            value = value.Replace("\r", " ").Replace("\n", " ");
            return value.Length <= MaxContentLength ? value : value.Substring(0, MaxContentLength) + "...";
        }

        // ---------- 以下 JSON 转义 / 解析 / CSV 辅助方法与 KnowledgeStore 共用（故为 internal），改动务必兼顾两处 ----------

        internal static string CurrentUser()
        {
            try
            {
                var login = CadApplication.GetSystemVariable("LOGINNAME") as string;
                if (!string.IsNullOrWhiteSpace(login)) return login;
            }
            catch { }
            return Environment.UserName;
        }

        internal static string Escape(string value)
        {
            if (value == null) return "";
            var sb = new StringBuilder(value.Length + 8);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        internal static string Unescape(string value)
        {
            if (string.IsNullOrEmpty(value) || value.IndexOf('\\') < 0) return value;
            var sb = new StringBuilder(value.Length);
            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] != '\\' || i + 1 >= value.Length) { sb.Append(value[i]); continue; }
                var next = value[++i];
                switch (next)
                {
                    case '\\': sb.Append('\\'); break;
                    case '"': sb.Append('"'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 < value.Length && int.TryParse(value.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var code))
                        { sb.Append((char)code); i += 4; }
                        else sb.Append(next);
                        break;
                    default: sb.Append(next); break;
                }
            }
            return sb.ToString();
        }

        private static string ToJson(AnnotationHistoryRecord r)
        {
            return "{" + string.Join(",", new[]
            {
                Q("time", r.Time.ToString("yyyy-MM-dd HH:mm:ss")),
                Q("user", r.User), Q("action", r.Action), Q("drawing", r.Drawing),
                Q("drawingPath", r.DrawingPath), Q("annotationId", r.AnnotationId),
                Q("number", r.Number), Q("date", r.Date), Q("discipline", r.Discipline), Q("drawingNo", r.DrawingNo),
                Q("author", r.Author), Q("role", r.Role), Q("status", r.Status),
                Q("content", r.Content), Q("changes", r.Changes)
            }) + "}";
        }

        internal static string Q(string key, string value) => "\"" + key + "\":\"" + Escape(value) + "\"";

        private static AnnotationHistoryRecord Parse(string line)
        {
            try
            {
                var r = new AnnotationHistoryRecord();
                r.Time = DateTime.TryParse(GetString(line, "time"), out var t) ? t : DateTime.Now;
                r.User = GetString(line, "user");
                r.Action = GetString(line, "action");
                r.Drawing = GetString(line, "drawing");
                r.DrawingPath = GetString(line, "drawingPath");
                r.AnnotationId = GetString(line, "annotationId");
                r.Number = GetString(line, "number");
                r.Date = GetString(line, "date");
                r.Discipline = GetString(line, "discipline");
                r.DrawingNo = GetString(line, "drawingNo");
                r.Author = GetString(line, "author");
                r.Role = GetString(line, "role");
                r.Status = GetString(line, "status");
                r.Content = GetString(line, "content");
                r.Changes = GetString(line, "changes");
                return r;
            }
            catch { return null; }
        }

        internal static string GetString(string line, string key)
        {
            var match = Regex.Match(line, "\"" + key + "\":\"((?:[^\"\\\\]|\\\\.)*)\"");
            return match.Success ? Unescape(match.Groups[1].Value) : "";
        }

        internal static string Csv(string value)
        {
            value = value ?? "";
            if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                return "\"" + value.Replace("\"", "\"\"") + "\"";
            return value;
        }
    }
}
