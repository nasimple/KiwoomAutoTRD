using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using System.Threading;
using System.Threading.Tasks;

namespace KiwoomAutoTRD.Services
{
    /// <summary>
    /// 0.3초 윈도우 내 체결 단건 카운트/등락폭 기반으로 상위 3개 종목 선별 및
    /// 최저가 관측시 매수, 최고가 관측시 매도 신호를 TradingManager로 통지.
    /// - 외부 I/O(주문)는 포트(TradingManager)를 통해 실행(의존성 역전).
    /// - UI 접근 없음(순수 로직). 
    /// </summary>
    internal sealed class MomentumEngine : IDisposable
    {
        private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(300);
        private const int MIN_TICK_RANGE = 3;

        private readonly object _gate = new object();
        private readonly Dictionary<string, Ring> _rings = new Dictionary<string, Ring>(256);
        private readonly Func<string, int, int> _getTickSize; // (code, price)->tickSize
        private readonly Func<string, bool> _isDeep;          // DEEP 여부 확인
        private readonly Action<string, int> _onBuySignal;    // (code, price)
        private readonly Action<string, int> _onSellSignal;   // (code, price)

        private sealed class TickItem
        {
            public DateTime TsUtc;
            public int Price;
            public int Qty;   // 단건 체결량
        }

        private sealed class Ring
        {
            public readonly Queue<TickItem> Q = new Queue<TickItem>(128);
            public int LastMin = int.MaxValue;
            public int LastMax = int.MinValue;
            public int LastCount;
            public DateTime LastEvalUtc;
        }

        public MomentumEngine(
            Func<string, int, int> getTickSize,
            Func<string, bool> isDeep,
            Action<string, int> onBuySignal,
            Action<string, int> onSellSignal)
        {
            _getTickSize = getTickSize ?? throw new ArgumentNullException(nameof(getTickSize));
            _isDeep = isDeep ?? throw new ArgumentNullException(nameof(isDeep));
            _onBuySignal = onBuySignal ?? throw new ArgumentNullException(nameof(onBuySignal));
            _onSellSignal = onSellSignal ?? throw new ArgumentNullException(nameof(onSellSignal));
        }

        public void OnTradeTick(string code, int price, int qty, DateTime tsUtc)
        {
            if (string.IsNullOrEmpty(code)) return;
            if (!_isDeep(code)) return; // DEEP 승격 종목만

            lock (_gate)
            {
                if (!_rings.TryGetValue(code, out var ring))
                {
                    ring = new Ring();
                    _rings[code] = ring;
                }

                ring.Q.Enqueue(new TickItem { TsUtc = tsUtc, Price = price, Qty = qty });

                // 윈도우 밖 제거
                var cutoff = tsUtc - Window;
                while (ring.Q.Count > 0 && ring.Q.Peek().TsUtc < cutoff)
                    ring.Q.Dequeue();

                // 상태 갱신
                if (ring.Q.Count > 0)
                {
                    ring.LastMin = int.MaxValue;
                    ring.LastMax = int.MinValue;
                    int cnt = 0;

                    foreach (var t in ring.Q)
                    {
                        if (t.Price < ring.LastMin) ring.LastMin = t.Price;
                        if (t.Price > ring.LastMax) ring.LastMax = t.Price;
                        cnt += 1; // 단건 카운팅(체결 건수)
                    }
                    ring.LastCount = cnt;
                    ring.LastEvalUtc = tsUtc;

                    // 틱 범위 확인
                    int tickSize = _getTickSize(code, price);
                    if (tickSize <= 0) tickSize = 1;
                    int diffTicks = (ring.LastMax - ring.LastMin) / tickSize;

                    // 등락폭 3틱 이상이면 신호: 최저=매수, 최고=매도
                    if (diffTicks >= MIN_TICK_RANGE)
                    {
                        // 신호는 최근 윈도우 관측값 기준
                        _onBuySignal(code, ring.LastMin);
                        _onSellSignal(code, ring.LastMax);
                    }
                }
            }
        }

        /// <summary>
        /// 상위 3개(0.3초 윈도우 내 카운트 상위)를 리턴. 디버그/모니터링용.
        /// </summary>
        public List<(string code, int count)> Top3ByCount(DateTime nowUtc)
        {
            lock (_gate)
            {
                var triples = _rings
                    .Where(kv => (nowUtc - kv.Value.LastEvalUtc) <= TimeSpan.FromSeconds(2)) // 최근 업데이트만
                    .Select(kv => (kv.Key, kv.Value.LastCount))
                    .OrderByDescending(x => x.LastCount)
                    .Take(3)
                    .ToList();
                return triples.Select(t => (t.Key, t.LastCount)).ToList();
            }
        }

        public void Dispose()
        {
            lock (_gate) { _rings.Clear(); }
        }
    }
}