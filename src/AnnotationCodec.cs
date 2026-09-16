using System;
using System.Collections.Generic;
using System.Text;

namespace GMAnnotation
{
    /// <summary>批注数据编解码：AnnotationData ↔ Base64 管道分隔字符串，支持分片写入 XRecord。</summary>
    internal static class AnnotationCodec
    {
        public const string AppName = "LA_PZ_ANNOTATION";

        /// <summary>实体 XData 内嵌数据分片前缀。完整数据随每个子实体冗余保存，
        /// 这样批注实体被 COPY/复制粘贴（编组不跟随复制）后仍可独立还原批注数据。</summary>
        public const string ChunkPrefix = "LA_D";
        /// <summary>XData 单个 ASCII 字符串上限 255 字符，留裕量。</summary>
        private const int ChunkSize = 200;

        /// <summary>把批注数据编码并包装为带序号前缀的 XData 分片，如 "LA_D0:xxx"。</summary>
        public static string[] BuildChunks(AnnotationData d)
        {
            var parts = Split(Encode(d), ChunkSize);
            for (var i = 0; i < parts.Length; i++) parts[i] = ChunkPrefix + i + ":" + parts[i];
            return parts;
        }

        /// <summary>从实体 XData 字符串集合中还原批注数据（仅识别带前缀的分片，校验分片连续）。</summary>
        public static bool TryDecodeChunks(IEnumerable<string> values, out AnnotationData data)
        {
            data = null; if (values == null) return false;
            var indexed = new SortedDictionary<int, string>();
            foreach (var v in values)
            {
                if (string.IsNullOrEmpty(v)) continue;
                if (!v.StartsWith(ChunkPrefix, StringComparison.Ordinal)) continue;
                var sep = v.IndexOf(':');
                if (sep <= ChunkPrefix.Length) continue;
                if (!int.TryParse(v.Substring(ChunkPrefix.Length, sep - ChunkPrefix.Length), out var idx)) continue;
                indexed[idx] = v.Substring(sep + 1);
            }
            if (indexed.Count == 0) return false;
            for (var i = 0; i < indexed.Count; i++) if (!indexed.ContainsKey(i)) return false;
            return TryDecode(string.Concat(indexed.Values), out data);
        }

        public static string Encode(AnnotationData d) => string.Join("|", E(d.Id), E(d.Number), E(d.Date), E(d.Discipline), E(d.Author), E(d.Status), E(d.Content), N(d.RenderTextHeight), N(d.RenderHeaderHeight), N(d.RenderSecondLineHeight), N(d.RenderCloudRadius), N(d.RenderLineWidth), E(d.Role), E(d.DrawingNo));

        public static string[] Split(string value, int size = 240)
        {
            var count = (value.Length + size - 1) / size;
            var parts = new string[count];
            for (var i = 0; i < count; i++) parts[i] = value.Substring(i * size, Math.Min(size, value.Length - i * size));
            return parts;
        }

        public static bool TryDecode(string value, out AnnotationData data)
        {
            data = null;
            if (string.IsNullOrWhiteSpace(value)) return false;
            var p = value.Split('|');
            if (p.Length != 7 && p.Length != 12 && p.Length != 13 && p.Length != 14) return false;
            try
            {
                data = new AnnotationData { Id = D(p[0]), Number = D(p[1]), Date = D(p[2]), Discipline = D(p[3]), Author = D(p[4]), Status = D(p[5]), Content = D(p[6]) };
                if(p.Length>=12){data.RenderTextHeight=P(p[7]);data.RenderHeaderHeight=P(p[8]);data.RenderSecondLineHeight=P(p[9]);data.RenderCloudRadius=P(p[10]);data.RenderLineWidth=P(p[11]);}
                // 13 字段 = 角色收尾；14 字段 = 角色 + 图号，两者都必须还原角色，
                // 否则读取时角色会回落到默认值，随后一次编辑写回就把图内原有的角色永久覆盖掉。
                if(p.Length>=13)data.Role=D(p[12]);
                if(p.Length==14)data.DrawingNo=D(p[13]);
                return true;
            }
            catch { return false; }
        }

        private static string E(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        private static string D(string value) => Encoding.UTF8.GetString(Convert.FromBase64String(value));
        private static string N(double value)=>value.ToString("R",System.Globalization.CultureInfo.InvariantCulture);
        private static double P(string value)=>double.TryParse(value,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var n)?n:0;
    }
}
