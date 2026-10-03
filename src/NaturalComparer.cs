using System;
using System.Collections.Generic;

namespace GMAnnotation
{
    /// <summary>自然排序：连续数字按数值比较，其余字符不区分大小写逐字比较。
    /// 用于批注编号（GM-9 排在 GM-10 前面，GM-1000 排在 GM-999 后面），列表、汇总、清单、CSV、Word 统一使用。</summary>
    internal sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new NaturalComparer();

        private static bool IsDigit(char c) => c >= '0' && c <= '9';

        public int Compare(string a, string b)
        {
            a = (a ?? "").Trim(); b = (b ?? "").Trim();
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                if (IsDigit(a[i]) && IsDigit(b[j]))
                {
                    int si = i, sj = j;
                    while (i < a.Length && IsDigit(a[i])) i++;
                    while (j < b.Length && IsDigit(b[j])) j++;
                    var da = a.Substring(si, i - si).TrimStart('0');
                    var db = b.Substring(sj, j - sj).TrimStart('0');
                    if (da.Length != db.Length) return da.Length.CompareTo(db.Length);
                    var c = string.CompareOrdinal(da, db);
                    if (c != 0) return c;
                    var zeros = (i - si).CompareTo(j - sj); // 数值相同：前导零少的在前（"1" < "01"）
                    if (zeros != 0) return zeros;
                }
                else
                {
                    var c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                    if (c != 0) return c;
                    i++; j++;
                }
            }
            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}
