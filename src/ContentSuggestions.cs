using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;

namespace GMAnnotation
{
    /// <summary>批注内容输入框的一条"补全建议"。</summary>
    internal sealed class ContentSuggestion
    {
        /// <summary>建议填入的完整内容（多行时整段代入）。</summary>
        public string Text { get; set; }

        /// <summary>来源标签：历史 / 常用语 / 条目。</summary>
        public string Source { get; set; }

        /// <summary>补充说明：知识库条目显示"规范名称 规范编号 条款"，便于确认引用的是哪一条。</summary>
        public string Note { get; set; }

        /// <summary>匹配得分，越大越靠前。</summary>
        public int Score { get; set; }

        /// <summary>是否"续写"：候选正好以当前已输入的内容开头，套用后等于把后半句接上。</summary>
        public bool IsContinuation { get; set; }

        /// <summary>单行预览：换行折成" ⏎ "、过长截断，避免建议列表被整段条文撑开。</summary>
        public string Preview
        {
            get
            {
                var text = (Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", " ⏎ ");
                return text.Length <= 60 ? text : text.Substring(0, 60) + "…";
            }
        }

        /// <summary>补充说明为空时隐藏第二行（省掉一个 BoolToVisibility 转换器）。</summary>
        public Visibility NoteVisibility
        {
            get { return string.IsNullOrWhiteSpace(Note) ? Visibility.Collapsed : Visibility.Visible; }
        }
    }

    /// <summary>
    /// 批注内容输入建议引擎：把「批注历史记录里以前写过的批注内容」「知识库 · 常用批注语」「知识库 · 规范条文条目」
    /// 三处语料汇到一起，随用户输入实时给出候选 —— 以已输入内容开头的直接<b>续写</b>，字词相近的作为<b>套用</b>候选，
    /// 什么都没输入时给出最近用过的几条供直接挑选。
    ///
    /// <para><b>完全本地、不联网、不调用任何模型</b>：只用"分词（中文按字 + 二元组，英文/数字按原样）＋ 前缀 / 包含 /
    /// 字词重合度"打分，所以它能做到的"理解"只到关键词级别 —— 不会凭空生成新句子，也不会改写候选原文，
    /// 用户随时可以无视它继续手写。候选按输入框里的<b>当前行、光标之前</b>那段文字匹配。</para>
    ///
    /// <para>数据每次读取后在 <see cref="CacheWindow"/> 内复用，避免逐键读盘；知识库/历史在别的窗口被改动后，
    /// 最多晚 2 秒生效。</para>
    /// </summary>
    internal static class ContentSuggestionEngine
    {
        /// <summary>单条内容长度上限：防止异常数据把建议列表和输入框撑爆。</summary>
        private const int MaxTextLength = 4000;
        /// <summary>参与匹配的输入长度上限（只看尾部，越靠后越相关）。</summary>
        private const int MaxQueryLength = 40;
        /// <summary>语料缓存时长。</summary>
        private static readonly TimeSpan CacheWindow = TimeSpan.FromSeconds(2);

        private static readonly object Gate = new object();
        private static List<CorpusItem> _corpus = new List<CorpusItem>();
        private static DateTime _loadedAt = DateTime.MinValue;
        private static bool _loaded;

        /// <summary>按"当前行已输入的内容"取候选；fragment 为空时返回最近用过的一批（供直接挑选）。</summary>
        public static List<ContentSuggestion> Suggest(string fragment, int max = 12)
        {
            var result = new List<ContentSuggestion>();
            try
            {
                var corpus = LoadCorpus();
                if (corpus.Count == 0) return result;
                var query = (fragment ?? "").Trim();
                if (query.Length == 0)
                {
                    // 未输入任何字：给最近用过的（历史在前，其次常用语、条目），用户可直接挑一条。
                    foreach (var item in corpus.OrderBy(i => i.Priority).ThenBy(i => i.Order).Take(max))
                        result.Add(ToSuggestion(item, false, 0));
                    return result;
                }
                if (query.Length > MaxQueryLength) query = query.Substring(query.Length - MaxQueryLength);
                var grams = Bigrams(query);
                var hits = new List<ContentSuggestion>();
                foreach (var item in corpus)
                {
                    var score = Score(item, query, grams, out var continuation);
                    if (score <= 0) continue;
                    hits.Add(ToSuggestion(item, continuation, score));
                }
                // 同分时短的优先（更接近"一句话"）；再同分时按来源优先级（历史 > 常用语 > 条目）。
                return hits.OrderByDescending(h => h.Score).ThenBy(h => h.Text.Length).Take(max).ToList();
            }
            catch (Exception ex) { PluginLog.Warning("Suggest.Query", ex.Message); return result; }
        }

        /// <summary>把三处语料汇成一份带来源标记的候选池；同一句话出现在多处时保留优先级高的来源。</summary>
        private static List<CorpusItem> LoadCorpus()
        {
            lock (Gate)
            {
                if (_loaded && DateTime.UtcNow - _loadedAt < CacheWindow) return _corpus;
                var items = new List<CorpusItem>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    // 优先级：常用批注语 > 知识库条目 > 历史内容（常用语本就是被挑选/沉淀过的说法）。
                    foreach (var phrase in KnowledgeStore.LoadPhrases())
                        Add(items, seen, phrase == null ? null : phrase.Text, "常用语", null, 1.2, null, 1);

                    foreach (var entry in KnowledgeStore.LoadEntries())
                    {
                        if (entry == null) continue;
                        var title = Join(" ", entry.SpecName, entry.SpecNumber, entry.Clause);
                        var meta = Join(" ", entry.SpecName, entry.SpecNumber, entry.Clause, entry.Attribute, entry.Discipline);
                        // 正文为空但填了规范/条款的条目（只登记了条文出处）时，用"规范名称 编号 条款"当内容，免得候选是空的。
                        var text = string.IsNullOrWhiteSpace(entry.Content) ? title : entry.Content;
                        Add(items, seen, text, "条目", title, 1.1, meta, 2);
                    }

                    var order = 0;
                    foreach (var text in AnnotationHistoryStore.LoadRecentContents(200))
                        Add(items, seen, text, "历史", null, 1.0, null, 0, order++);
                }
                catch (Exception ex) { PluginLog.Warning("Suggest.Corpus", ex.Message); }
                _corpus = items; _loadedAt = DateTime.UtcNow; _loaded = true; return _corpus;
            }
        }

        private static void Add(List<CorpusItem> items, HashSet<string> seen, string text, string source, string note, double weight, string meta, int priority, int order = 0)
        {
            var value = (text ?? "").Trim();
            if (value.Length == 0 || value.Length > MaxTextLength) return;
            if (!seen.Add(value)) return;
            items.Add(new CorpusItem { Text = value, Source = source, Note = note, Meta = meta ?? "", Weight = weight, Priority = priority, Order = order });
        }

        /// <summary>候选打分：前缀一致 → 强"续写"；包含 → 次强；都不是 → 按字词重合度；最后再试着用条目的规范字段匹配。</summary>
        private static int Score(CorpusItem item, string query, List<string> grams, out bool continuation)
        {
            continuation = false;
            var text = item.Text ?? "";
            if (text.Length == 0) return 0;
            if (string.Equals(text, query, StringComparison.Ordinal)) return 0; // 已经就是这句话，没什么可补
            var weighted = 1 + (int)(item.Weight * 30);
            if (text.StartsWith(query, StringComparison.Ordinal))
            {
                continuation = true;
                return 1000 + query.Length * 8 + weighted;
            }
            var index = text.IndexOf(query, StringComparison.Ordinal);
            if (index >= 0) return 640 - Math.Min(index, 40) * 3 + query.Length * 4 + weighted;
            var overlap = OverlapScore(text, query, grams);
            if (overlap > 0) return overlap + weighted;
            // 正文里没有这些字，但规范名称/编号/条款里有 —— 让"按规范找批注内容"也能命中，权重减半。
            if (item.Meta.Length == 0) return 0;
            var metaIndex = item.Meta.IndexOf(query, StringComparison.Ordinal);
            if (metaIndex >= 0) return 420 - Math.Min(metaIndex, 40) * 3;
            var metaOverlap = OverlapScore(item.Meta, query, grams);
            return metaOverlap > 0 ? metaOverlap / 2 : 0;
        }

        /// <summary>字词重合度：中文按"单字覆盖率"与"二元组命中率"混合，两者都过低就不算候选。</summary>
        private static int OverlapScore(string candidate, string query, List<string> grams)
        {
            if (candidate.Length == 0 || query.Length == 0) return 0;
            var chars = new List<char>();
            foreach (var ch in query) if (!char.IsWhiteSpace(ch) && !chars.Contains(ch)) chars.Add(ch);
            if (chars.Count == 0) return 0;
            var charHits = 0;
            foreach (var ch in chars) if (candidate.IndexOf(ch) >= 0) charHits++;
            var charRatio = (double)charHits / chars.Count;
            var gramHits = 0;
            if (grams.Count > 0) foreach (var gram in grams) if (candidate.IndexOf(gram, StringComparison.Ordinal) >= 0) gramHits++;
            var gramRatio = grams.Count == 0 ? charRatio : (double)gramHits / grams.Count;
            var ratio = 0.4 * charRatio + 0.6 * gramRatio;
            if (ratio < 0.45) return 0;
            return (int)(200 + ratio * 320);
        }

        private static List<string> Bigrams(string query)
        {
            var buffer = new StringBuilder();
            foreach (var ch in query) if (!char.IsWhiteSpace(ch)) buffer.Append(ch);
            var text = buffer.ToString();
            var grams = new List<string>();
            if (text.Length == 1) { grams.Add(text); return grams; }
            for (var i = 0; i + 1 < text.Length && grams.Count < 40; i++)
            {
                var gram = text.Substring(i, 2);
                if (!grams.Contains(gram)) grams.Add(gram);
            }
            return grams;
        }

        private static ContentSuggestion ToSuggestion(CorpusItem item, bool continuation, int score)
        {
            return new ContentSuggestion { Text = item.Text, Source = item.Source, Note = item.Note, Score = score, IsContinuation = continuation };
        }

        private static string Join(string separator, params string[] values)
        {
            return string.Join(separator, values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()));
        }

        /// <summary>候选池里的一条：Text 是要填入的内容，Meta 只参与检索（条目的规范名称/编号/条款等）。</summary>
        private sealed class CorpusItem
        {
            public string Text;
            public string Source;
            public string Note;
            public string Meta;
            public double Weight;
            /// <summary>未输入时的展示优先级：0 历史、1 常用语、2 条目。</summary>
            public int Priority;
            /// <summary>同级内的顺序（历史按"最近使用"倒序）。</summary>
            public int Order;
        }
    }
}
