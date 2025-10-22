// 거래대금 DTO 로 값 분배하기

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KiwoomAutoTRD.Adapters
{
    internal class SignalDtos
    {
    }
    internal sealed class BurstBuySignal
    {
        public string Code { get; set; }           // 종목코드
        public int Price { get; set; }             // 신호 생성 시 기준가격(현재가 또는 호가)
        public int Qty { get; set; }               // 제안 수량(전략 계산 or 기본단위)
        public DateTime TsUtc { get; set; }        // 신호 시각(UTC)
        public string Reason { get; set; }         // "burst_value" 등 사유코드

        // 디버그/로그용 메타
        public int SpreadTicks { get; set; }       // 스프레드(틱)
        public double ChangeRate { get; set; }     // 등락률(%)
        public double Ratio { get; set; }          // bid/ask 잔량비
        public double SumWinM { get; set; }        // 창 합계(백만원 단위)
        public double ReqWinM { get; set; }        // 요구치(백만원 단위)
        public double EmaPer { get; set; }         // 틱당 EMA(원)
    }

}
