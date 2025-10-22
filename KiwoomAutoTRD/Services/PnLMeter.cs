using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Collections.Concurrent;
using KiwoomAutoTRD.Adapters;
using KiwoomAutoTRD.Services; // StrategyParams 접근

namespace KiwoomAutoTRD.Services
{
    internal class PnLMeter
    {
        private sealed class PnLState
        {
            public long Realized;     // 실현손익(보정 후, 원)
            public long Unrealized;   // 미실현손익(보정 후, 원)
            public int PositionQty;   // 보유 수량
            public int AvgPrice;      // 평단(원)
        }

        private static readonly ConcurrentDictionary<string, PnLState> _map =
            new ConcurrentDictionary<string, PnLState>(StringComparer.OrdinalIgnoreCase);

        private static decimal BuyFee => (decimal)StrategyParams.BuyFeeRate;   // 예: 0.00015
        private static decimal SellFee => (decimal)StrategyParams.SellFeeRate; // 예: 0.00015
        private static decimal SellTax => (decimal)StrategyParams.SellTaxRate; // 예: 0.0023

        /// <summary>
        /// 체결 발생 시 호출. side=BUY/SELL, qty>=1, price>=1
        /// 순익 보정식:
        ///   순매도가 = price * (1 - SellFee - SellTax)
        ///   순매수가 = AvgPrice * (1 + BuyFee)
        ///   실현손익증분 = (순매도가 - 순매수가) * qty  (SELL 시)
        /// </summary>
        public static void RecordFill(string code, string side, int qty, int price,
                                      string stratId, int stratVer, string runId, string tag)
        {
            if (qty <= 0 || price <= 0 || string.IsNullOrEmpty(tag)) return;

            var st = _map.GetOrAdd(tag, _ => new PnLState());

            lock (st)
            {
                if (side.Equals("BUY", StringComparison.OrdinalIgnoreCase))
                {
                    // 평단 갱신 (수수료/세금은 실현시 보정으로 처리. 평단은 체결가 평균 유지)
                    int newQty = st.PositionQty + qty;
                    if (newQty > 0)
                    {
                        long totalCost = (long)st.AvgPrice * st.PositionQty + (long)price * qty;
                        st.AvgPrice = (int)Math.Round(totalCost / (double)newQty);
                        st.PositionQty = newQty;
                    }
                }
                else if (side.Equals("SELL", StringComparison.OrdinalIgnoreCase))
                {
                    if (st.PositionQty <= 0)
                    {
                        // 음수 포지션 방지: 방어적 처리 (필요 시 로그만)
                        // TradingEvents.RaiseTradeInfo($"[PnL] WARN no position: {tag} {code}");
                    }
                    int closeQty = Math.Min(qty, Math.Max(0, st.PositionQty));
                    if (closeQty > 0)
                    {
                        decimal netSell = price * (1m - SellFee - SellTax);
                        decimal netBuy = st.AvgPrice * (1m + BuyFee);
                        decimal pnlPer = netSell - netBuy;                       // 1주당 보정 순익
                        long deltaReal = (long)Math.Round(pnlPer * closeQty);    // 원단위 반올림

                        st.Realized += deltaReal;
                        st.PositionQty -= closeQty;
                        if (st.PositionQty <= 0)
                        {
                            st.PositionQty = 0;
                            st.AvgPrice = 0;
                            st.Unrealized = 0; // 포지션 없으면 미실현 0
                        }
                    }
                }

                // 미실현은 보유가 있을 때만 유지 (최근 갱신값 유지)
                if (st.PositionQty <= 0) st.Unrealized = 0;
            }

            // 구조화 로그 한 줄 (대시보드/필터 용이)
            string line = $"[PnL] tag={tag} run={runId} code={code} side={side} qty={qty}@{price} " +
                          $"posQty={st.PositionQty} avg={st.AvgPrice} realized={st.Realized} unrealized={st.Unrealized}";
            Console.WriteLine(line);
            TradingEvents.RaiseTradeInfo(line);
        }

        /// <summary>
        /// 시세 갱신 시 호출(선택). 현재가로 미실현손익(보정) 갱신.
        /// </summary>
        public static void UpdateMark(string tag, int lastPrice)
        {
            if (string.IsNullOrEmpty(tag) || lastPrice <= 0) return;
            PnLState st; if (!_map.TryGetValue(tag, out st)) return;

            lock (st)
            {
                if (st.PositionQty <= 0) { st.Unrealized = 0; return; }
                decimal netSell = lastPrice * (1m - SellFee - SellTax);
                decimal netBuy = st.AvgPrice * (1m + BuyFee);
                decimal pnlPer = netSell - netBuy;
                st.Unrealized = (long)Math.Round(pnlPer * st.PositionQty);
            }
        }

        /// <summary>현재 스냅샷 조회(원시 값 그대로).</summary>
        public static (int PosQty, int AvgPrice, long Realized, long Unrealized) GetSnapshot(string tag)
        {
            PnLState st; if (!_map.TryGetValue(tag, out st)) return (0, 0, 0, 0);
            lock (st) { return (st.PositionQty, st.AvgPrice, st.Realized, st.Unrealized); }
        }

        /// <summary>전략별 초기화(선택).</summary>
        public static void Reset(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return;
            _map.TryRemove(tag, out _);
        }
    }
}
