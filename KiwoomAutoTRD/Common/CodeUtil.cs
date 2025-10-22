using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KiwoomAutoTRD.Common
{
    // 종목 코드/문자열 정규화 유틸
    internal class CodeUtil
    {

        // 키움 종목코드 정규화: "A000660" → "000660"
        public static string NormalizeCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return code;
            code = code.Trim();
            return (code[0] == 'A' || code[0] == 'a') ? code.Substring(1) : code;
        }

        // 숫자 안전 파싱: "1,234", "+1,234", "-1,234" → 1234
        // 체잔 수량/가격 필드 특성상 음수 기호는 제거합니다.
        public static int SafeInt(string s, int defaultValue = 0)
        {
            if (string.IsNullOrWhiteSpace(s)) return defaultValue;
            int v;
            var cleaned = s.Replace(",", "").Replace("+", "").Replace("-", "").Trim();
            return int.TryParse(cleaned, out v) ? v : defaultValue;
        }


    }
}
