using System;
using System.Text;

namespace LAAnnotation
{
    /// <summary>批注数据编解码：AnnotationData ↔ Base64 管道分隔字符串，支持分片写入 XRecord。</summary>
    internal static class AnnotationCodec
    {
        public const string AppName = "LA_PZ_ANNOTATION";

        public static string Encode(AnnotationData d) => string.Join("|", E(d.Id), E(d.Number), E(d.Date), E(d.Discipline), E(d.Author), E(d.Status), E(d.Content), N(d.RenderTextHeight), N(d.RenderHeaderHeight), N(d.RenderSecondLineHeight), N(d.RenderCloudRadius), N(d.RenderLineWidth), E(d.Role));

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
            if (p.Length != 7 && p.Length != 12 && p.Length != 13) return false;
            try
            {
                data = new AnnotationData { Id = D(p[0]), Number = D(p[1]), Date = D(p[2]), Discipline = D(p[3]), Author = D(p[4]), Status = D(p[5]), Content = D(p[6]) };
                if(p.Length>=12){data.RenderTextHeight=P(p[7]);data.RenderHeaderHeight=P(p[8]);data.RenderSecondLineHeight=P(p[9]);data.RenderCloudRadius=P(p[10]);data.RenderLineWidth=P(p[11]);}
                if(p.Length==13)data.Role=D(p[12]);
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
