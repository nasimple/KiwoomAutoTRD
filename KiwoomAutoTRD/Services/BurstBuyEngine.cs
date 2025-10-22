using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Threading;
using KiwoomAutoTRD.Adapters;

namespace KiwoomAutoTRD.Services
{
    internal class BurstBuyEngine
    {
        // 튜닝 파라미터(StrategyParams 단일 출처)
        private readonly int _parallelism;
        private readonly int _baselineTicks;
        private readonly int _windowTicks;
        private readonly double _multiple;
        private readonly long _minDeltaWon;
        private readonly int _cooldownSec;
        private readonly int _maxSpreadTicks;
        private readonly double _minChgPct;
        private readonly double _minBidAskRatio;

        // 의존성(함수형 DI) - null 허용(없으면 해당 가드 스킵)
        private readonly Func<string, bool> _isViExcluded;                      // VI 제외
        private readonly Func<string, int, int, int> _getTickSize;             // (code, price, defaultTick) -> tickSize
        private readonly Func<string, (int bb, int ba, int bq, int aq, double r)> _getL1Snapshot; // 호가/잔량/등락률
        private readonly Func<string, bool> _hasOpenOrPending;                 // 보유/미체결 여부
        private readonly Action<BurstBuySignal> _onSignal;                     // 신호 콜백(TradingManager)

        // 파티션 큐 & 워커
        private BlockingCollection<TickDto>[] _queues;
        private Thread[] _workers;
        private volatile bool _running;

        // 파티션별 상태(락 최소화 위해 파티션 로컬 딕셔너리 사용)
        private sealed class PartState
        {
            public readonly Dictionary<string, double> EmaPerTick = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, Queue<long>> WinVal = new Dictionary<string, Queue<long>>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, long> WinSum = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, DateTime> LastBuyUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        }
        private PartState[] _parts;

        public BurstBuyEngine(
            int parallelism,
            // BurstTick
            int baselineTicks, int windowTicks, double multiple, long minDeltaWon, int cooldownSec,
            // Entry
            int maxSpreadTicks, double minChgPct, double minBidAskRatio,
            // DI
            Func<string, bool> isViExcluded,
            Func<string, int, int, int> getTickSize,
            Func<string, (int bb, int ba, int bq, int aq, double r)> getL1Snapshot,
            Func<string, bool> hasOpenOrPending,
            Action<BurstBuySignal> onSignal)
        {
            _parallelism = Math.Max(1, parallelism);
            _baselineTicks = Math.Max(1, baselineTicks);
            _windowTicks = Math.Max(1, windowTicks);
            _multiple = Math.Max(1.0, multiple);
            _minDeltaWon = Math.Max(0, minDeltaWon);
            _cooldownSec = Math.Max(0, cooldownSec);
            _maxSpreadTicks = Math.Max(0, maxSpreadTicks);
            _minChgPct = minChgPct;
            _minBidAskRatio = Math.Max(0.0, minBidAskRatio);

            _isViExcluded = isViExcluded;
            _getTickSize = getTickSize;
            _getL1Snapshot = getL1Snapshot;
            _hasOpenOrPending = hasOpenOrPending;
            _onSignal = onSignal ?? (_ => { });
        }

        public void Start()
        {
            if (_running) return;
            _running = true;

            _queues = new BlockingCollection<TickDto>[_parallelism];
            _workers = new Thread[_parallelism];
            _parts = new PartState[_parallelism];

            for (int i = 0; i < _parallelism; i++)
            {
                _queues[i] = new BlockingCollection<TickDto>(new ConcurrentQueue<TickDto>());
                _parts[i] = new PartState();
                var idx = i;
                _workers[i] = new Thread(() => WorkerLoop(idx))
                {
                    IsBackground = true,
                    Name = "BurstBuyWorker-" + idx
                };
                _workers[i].Start();
            }
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            if (_queues != null)
            {
                foreach (var q in _queues) q.CompleteAdding();
            }
            if (_workers != null)
            {
                foreach (var t in _workers)
                {
                    try { t.Join(200); } catch { /* 안전 무시 */ }
                }
            }
        }

        public void Enqueue(TickDto dto)
        {
            if (!_running || dto == null || string.IsNullOrWhiteSpace(dto.Code)) return;
            if (_isViExcluded != null && _isViExcluded(dto.Code)) return;

            int bucket = (dto.Code.GetHashCode() & 0x7fffffff) % _parallelism;
            var q = _queues[bucket];
            if (!q.IsAddingCompleted)
            {
                try { q.Add(dto); } catch { /* 안전 무시 */ }
            }
        }

        private void WorkerLoop(int idx)
        {
            var q = _queues[idx];
            var st = _parts[idx];

            foreach (var dto in q.GetConsumingEnumerable())
            {
                try
                {
                    Evaluate(st, dto);
                }
                catch
                {
                    // 안전 무시(핫패스)
                }
            }
        }

        private void Evaluate(PartState st, TickDto dto)
        {
            // 입력 검증
            if (dto.TradeQty <= 0 || dto.LastPrice <= 0) return;

            // 1) 틱 거래대금
            long tickValue = (long)dto.LastPrice * (long)dto.TradeQty;

            // 2) EMA per-tick
            double emaPrev = 0;
            st.EmaPerTick.TryGetValue(dto.Code, out emaPrev);
            double alpha = 2.0 / (_baselineTicks + 1.0);
            double seed = (emaPrev <= 0) ? (double)tickValue : emaPrev;
            double ema = alpha * tickValue + (1.0 - alpha) * seed;
            st.EmaPerTick[dto.Code] = ema;

            // 3) 순간창 합계
            Queue<long> q;
            if (!st.WinVal.TryGetValue(dto.Code, out q) || q == null)
            {
                q = new Queue<long>(_windowTicks + 4);
                st.WinVal[dto.Code] = q;
                st.WinSum[dto.Code] = 0;
            }
            long sum = st.WinSum[dto.Code];
            q.Enqueue(tickValue);
            sum += tickValue;
            while (q.Count > _windowTicks) sum -= q.Dequeue();
            st.WinSum[dto.Code] = sum;

            // 4) 임계 비교(상대+절대)
            double required = ema * _windowTicks * _multiple;
            bool passRelative = sum >= required;
            bool passAbsolute = sum >= _minDeltaWon;
            if (!(passRelative && passAbsolute)) return;

            // 5) 품질 가드(호가/등락/스프레드/잔량비/쿨다운/보유·미체결)
            int bb = 0, ba = 0, bq = 0, aq = 0; double r = 0.0;
            if (_getL1Snapshot != null)
            {
                var l1 = _getL1Snapshot(dto.Code);
                bb = l1.bb; ba = l1.ba; bq = l1.bq; aq = l1.aq; r = l1.r;
            }
            // 스프레드
            int spreadTicks = int.MaxValue;
            if (_getTickSize != null && bb > 0 && ba > 0)
            {
                int tickSize = _getTickSize(dto.Code, dto.LastPrice, 1);
                if (tickSize > 0) spreadTicks = (ba - bb) / tickSize;
            }
            if (spreadTicks > _maxSpreadTicks) return;

            // 등락률
            double chgRt = (dto.ChangeRate != 0) ? dto.ChangeRate : r;
            if (chgRt < _minChgPct) return;

            // 잔량비
            double ratio = (aq > 0) ? ((double)bq / (double)aq) : double.PositiveInfinity;
            if (ratio < _minBidAskRatio) return;

            // 쿨다운
            DateTime last;
            st.LastBuyUtc.TryGetValue(dto.Code, out last);
            if ((dto.TsUtc - last).TotalSeconds < _cooldownSec) return;

            // 보유/미체결 회피
            if (_hasOpenOrPending != null && _hasOpenOrPending(dto.Code)) return;

            // === 매수 신호 생성 ===
            st.LastBuyUtc[dto.Code] = dto.TsUtc;

            var sig = new BurstBuySignal
            {
                Code = dto.Code,
                Price = dto.LastPrice,
                Qty = Math.Max(1, StrategyParams.Entry.DefaultOrderQty), // StrategyParams에서 단일 출처
                TsUtc = dto.TsUtc,
                Reason = "burst_value",
                SpreadTicks = spreadTicks,
                ChangeRate = chgRt,
                Ratio = ratio,
                SumWinM = sum / 1_000_000d,
                ReqWinM = required / 1_000_000d,
                EmaPer = ema
            };


            _onSignal(sig);
        }
    }
}
