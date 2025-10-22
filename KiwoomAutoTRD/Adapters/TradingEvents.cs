//version 250831
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KiwoomAutoTRD.Adapters
{
    /// <summary>
    /// TradingManager↔Form1 UI 이벤트 브릿지(이름/시그니처 추가만).
    /// </summary>
    internal static class TradingEvents
    {
        // UI 표시는 문자열 한 줄 단위로 전달(핫패스에선 Invoke 측에서 rate limit 권장)
        public static event Action<string> UiTradeInfo;   // textBox3용(체결/완료)
        public static event Action<string> UiPendingInfo; // textBox4용(미체결)

        
        // 2단계: 거래대금 랭킹 텍스트 갱신 브로드캐스트
        public static readonly int Parallelism = Math.Max(2, Environment.ProcessorCount / 2);


        public static void RaiseTradeInfo(string line)
             => SafeInvoke(UiTradeInfo, line);

        public static void RaisePending(string line)
            => SafeInvoke(UiPendingInfo, line);

        // === [추가] 호환용 별칭: Form1에서 UiPending을 구독해도 동작하도록 매핑 ===
        public static event Action<string> UiPending
        {
            add { UiPendingInfo += value; }
            remove { UiPendingInfo -= value; }
        }

        public static void RaiseUiPending(string line) => RaisePending(line);
        // === [추가 끝] ===

        private static void SafeInvoke(Action<string> evt, string s)
        {
            try { evt?.Invoke(s); } catch { /* UI 오류 격리 */ }
        }
    }
}