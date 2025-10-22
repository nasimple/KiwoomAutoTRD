// 거래대금 상위 종목 걸러주는 계산식

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KiwoomAutoTRD.Adapters;
using KiwoomAutoTRD.Services;

namespace KiwoomAutoTRD.Services
{
    internal class TurnoverBurstEngine
    {
        // 클래스 필드에 DI용 콜백 2개만 추가
        private readonly Func<string, int, int, int> _getTickSize;   // (code, price, defaultTick) -> tickSize
        private readonly Func<string, bool> _hasOpenOrPending;     // 보유/미체결 여부

        private readonly int _parallelism;
        private readonly int _topN;
        private readonly int _uiRefreshMs;
        private readonly Func<string, bool> _isViExcluded;
        private readonly Action<string> _onRankingText;

        // 파티션 큐 & 워커
        private BlockingCollection<TickDto>[] _queues;
        private Thread[] _workers;
        private volatile bool _running;



        // 실시간 스냅샷(라이트/딥 혼합 가능) — 거래대금/등락/가격을 통합 캐시
        private sealed class Snap
        {
            public string Code;
            public int Last;               // 현재가
            public double ChgRt;           // 등락률(%)
            public long AmountSum;         // 누적 거래대금
            public long VolumeSum;         // 누적 거래량
            public int BestBid;
            public int BestAsk;
            public int BidQty;
            public int AskQty;
            public int TickCount;          // 내부 진단용
            public long LastUpdateTicks;   // Environment.TickCount

            // 객체 지향적 갱신: 호가 이벤트를 Snap이 스스로 반영
            public void ApplyQuote(int last, int bestBid, int bestAsk, int bidQty, int askQty, double chgRt, int nowTicks)
            {
                if (last > 0) Last = last;
                BestBid = bestBid;
                BestAsk = bestAsk;
                BidQty = bidQty;
                AskQty = askQty;
                ChgRt = chgRt;
                LastUpdateTicks = nowTicks;
            }

            // 객체 지향적 갱신: 체결틱 이벤트를 Snap이 스스로 반영
            public void ApplyTrade(int last, int tradeQty, double chgRt, long amountSum, long volumeSum, int nowTicks)
            {
                if (last > 0) Last = last;
                ChgRt = chgRt;
                if (amountSum > AmountSum) AmountSum = amountSum;   // 역행 방지
                if (volumeSum > VolumeSum) VolumeSum = volumeSum;
                if (tradeQty > 0) TickCount++;
                LastUpdateTicks = nowTicks;
            }
        }

        private readonly ConcurrentDictionary<string, Snap> _snaps = new ConcurrentDictionary<string, Snap>(StringComparer.OrdinalIgnoreCase);

        private int _lastUiTicks;
        private readonly object _renderGate = new object();


        //  전략 엔진값 불러오는 구문  =================================================================
        public TurnoverBurstEngine(int parallelism, int topN, int uiRefreshMs,
                                   Func<string, bool> isViExcluded,
                                   Action<string> onRankingText)
        {
            _parallelism = Math.Max(1, parallelism);
            _topN = Math.Max(1, topN);
            _uiRefreshMs = Math.Max(250, uiRefreshMs);
            _isViExcluded = isViExcluded ?? (code => false);
            _onRankingText = onRankingText ?? (_ => { });

            // 필드 기본값
            _getTickSize = (c, p, d) => d;
            _hasOpenOrPending = _ => false;
        }


        // (라이트) L1 호가 스냅샷 업데이트 — 등락/현재가/호가 잔량만으로도 뷰 최신화
        public void UpdateSnapshot(string code, int lastPrice, int bestBid, int bestAsk, int bidQty, int askQty, double chgRt)
        {
            if (string.IsNullOrWhiteSpace(code) || lastPrice <= 0) return;
            var now = Environment.TickCount;
            var snap = _snaps.GetOrAdd(code, c => new Snap { Code = c });
            snap.ApplyQuote(lastPrice, bestBid, bestAsk, bidQty, askQty, chgRt, now);
        }

        public void OnTradeTick(string code, int lastPrice, int tradeQty, double chgRt, long amountSum, long volumeSum)
        {
            if (string.IsNullOrWhiteSpace(code) || lastPrice <= 0) return;
            var now = Environment.TickCount;
            var snap = _snaps.GetOrAdd(code, c => new Snap { Code = c });
            snap.ApplyTrade(lastPrice, tradeQty, chgRt, amountSum, volumeSum, now);
        }

        // 외부 워커 파티션에서 주기적 호출 (idx==0 스로틀 발행자)
        public void MaybePublishRanking(int idx)
        {
            if (idx != 0) return; // 단일 발행자
            var now = Environment.TickCount;
            if (unchecked(now - _lastUiTicks) < _uiRefreshMs) return;
            _lastUiTicks = now;

            RenderRanking();
        }

        // ★핵심: "그 순간"의 거래대금 상위 → 그 안에서 등락률 상위 Top-N(=7)으로 즉시 갱신
        private void RenderRanking()
        {
            lock (_renderGate)
            {
                if (_snaps.Count == 0) return;

                // 1) 현재 캐시에 있는 전 종목을 거래대금 내림차순으로 정렬
                // 2) 동순위 시 등락률 내림차순
                // 3) 상위 _topN만 선택 (StrategyParams.RankingTopN에서 7로 설정 권장)
                var top = _snaps.Values
                                .Where(s => s != null && s.Last > 0)
                                .OrderByDescending(s => s.AmountSum)
                                .ThenByDescending(s => s.ChgRt)
                                .Take(_topN)
                                .ToArray();

                if (top.Length == 0) return;

                // VI는 랭킹 계산에서 배제하지 않음(요청사항) — 매수 단계 공통 가드에서 차단
                var sb = new StringBuilder();
                sb.Append("Top").Append(_topN).Append(" (By Turnover → Chg%): ");

                for (int i = 0; i < top.Length; i++)
                {
                    var s = top[i];
                    // 포맷: 순위. 코드 (등락%) [거래대금 억]
                    var amtEok = s.AmountSum > 0 ? (s.AmountSum / 100_000_000.0) : 0.0;
                    sb.Append(i + 1).Append('.')
                      .Append(s.Code)
                      .Append(' ')
                      .AppendFormat("({0:+0.00;-0.00;0.00}%)", s.ChgRt)
                      .Append(' ')
                      .AppendFormat("[{0:0.0}억]", amtEok);

                    if (i != top.Length - 1) sb.Append("  |  ");
                }

                _onRankingText(sb.ToString());
            }
        }



        // TradingManager.OnTradeTick 에서 요구하는 시그니처
        public bool TryEvaluateBuy(
            string code, int lastPrice, int tradeQty, DateTime tsUtc,
            out string reason, out int spreadTicks, out double ratio, out double dChgRt,
            out double sumWinM, out double reqM, out double emaPer)
        {
            // 기본값(컴파일러 definite assignment 방지)
            reason = "noop";
            spreadTicks = 0;
            ratio = 0.0;
            dChgRt = 0.0;
            sumWinM = 0.0;
            reqM = 0.0;
            emaPer = 0.0;

            // 최소 가드
            if (string.IsNullOrWhiteSpace(code) || lastPrice <= 0 || tradeQty <= 0) return false;
            if (_hasOpenOrPending != null && _hasOpenOrPending(code)) return false;

            // [간단 예시 로직] — 필요 시 실제 거래대금 윈도/EMA 로직으로 대체
            long tickValue = (long)lastPrice * (long)tradeQty;
            emaPer = tickValue;                 // 자리표시
            sumWinM = tickValue / 1_000_000d;   // 자리표시
            reqM = sumWinM * 1.5;               // 자리표시
            dChgRt = 0.0;                       // 외부에서 보강 가능
            ratio = 1.0;

            // 호가단위 기반 스프레드 틱 계산(자리표시)
            int tick = (_getTickSize != null) ? _getTickSize(code, lastPrice, 1) : 1;
            spreadTicks = 1; // 자리표시

            // 매우 단순한 임계: 순간 거래대금이 1백만 이상이면 pass
            bool pass = sumWinM >= 1.0;
            if (pass) reason = "burst_value";
            return pass;
        }


        public void Start()
        {
            if (_running) return;
            _running = true;

            _queues = new BlockingCollection<TickDto>[_parallelism];
            _workers = new Thread[_parallelism];
            for (int i = 0; i < _parallelism; i++)
            {
                _queues[i] = new BlockingCollection<TickDto>(new ConcurrentQueue<TickDto>());
                var idx = i;
                _workers[i] = new Thread(() => WorkerLoop(idx))
                {
                    IsBackground = true,
                    Name = "TurnoverWorker-" + idx
                };
                _workers[i].Start();
            }
            _lastUiTicks = Environment.TickCount;
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

        // 외부(TradingManager)에서 호출
        public void Enqueue(TickDto dto)
        {
            if (dto == null || !_running) return;
            // VI 제외 기본 규칙
            if (_isViExcluded(dto.Code)) return;

            var key = dto.Code ?? "";
            // 동일 종목 → 동일 파티션
            int bucket = (key.GetHashCode() & 0x7fffffff) % _parallelism;
            var q = _queues[bucket];
            if (!q.IsAddingCompleted)
            {
                try { q.Add(dto); } catch { /* 안전 무시 */ }
            }
        }

        private void WorkerLoop(int idx)
        {
            var q = _queues[idx];
            foreach (var dto in q.GetConsumingEnumerable())
            {
                try
                {
                    // 🔹 새 구조: 각 틱을 실시간 스냅으로 누적 반영
                    OnTradeTick(
                        dto.Code,
                        dto.LastPrice,    // ← TickDto의 실제 필드명 (예: dto.Price 또는 dto.Last)
                        dto.TradeQty,
                        dto.ChangeRate,
                        dto.AmountSum,
                        dto.VolumeSum
                    );
                    // 🔹 UI 스로틀 갱신 (한 스레드만)
                    MaybePublishRanking(idx);
                }
                catch
                {
                    // 안전 무시(핫패스)
                }
            }
        }



    }
}
