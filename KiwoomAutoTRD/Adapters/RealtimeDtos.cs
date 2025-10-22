using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KiwoomAutoTRD.Adapters
{
    internal class RealtimeDtos
    {
    }
    public sealed class TickDto
    {
        public string Code { get; set; }           // 종목코드
        public int LastPrice { get; set; }         // FID 10
        public double ChangeRate { get; set; }     // FID 12 (%)
        public int VolumeSum { get; set; }         // FID 13
        public long AmountSum { get; set; }        // FID 14
        public int TradeQty { get; set; }          // FID 15 (단건 체결량)
        public DateTime TsUtc { get; set; }        // 수신시각(UTC)
    }

    public sealed class ViEventDto
    {
        public string Code { get; set; }
        public bool IsFired { get; set; }          // 발동 여부(또는 근접/해제는 전략 허브에서 확장)
        public DateTime TsUtc { get; set; }
    }
}
