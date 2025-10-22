using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KiwoomAutoTRD.Common
{
    internal class SafeParse
    {

        // 통합 유틸관리  
        public static int ParseInt(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            int v;
            return int.TryParse(s.Replace(",", "").Replace("+", "").Replace("-", ""), out v) ? v : 0;
        }

        public static long ParseLong(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            long v;
            return long.TryParse(s.Replace(",", "").Replace("+", "").Replace("-", ""), out v) ? v : 0;
        }

        // 필요 시 확장
        public static double ParseDouble(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0.0;
            double v;
            return double.TryParse(s.Replace(",", "").Replace("+", "").Replace("-", ""), out v) ? v : 0.0;
        }
    }
}
