//version 250831
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KiwoomAutoTRD.Services
{
    internal class StrategyParams
    {
        #region 전략 식별자 관리 START (region=지역(발음:리즌))  =================================================================

        // === 전략 식별자 START=====================================================
        public static readonly string StrategyId = "TurnoverBurst";   // 거래대금상위
        public static readonly int StrategyVersion = 1;               // 기존: 2

        // Canonical Tag: "OpeningBurst@v2", "TurnoverBurst@v1" 전략 태그로 변환구문
        public static string CanonicalTag => $"{StrategyId}@v{StrategyVersion}";

        // === 실행 세션 RUN_ID ===
        // Form1 시작 시 20250902-01 같은 형식으로 할당
        public static string RunId { get; set; }
        // === 전략 식별자 END========================================================
        #endregion  전략 식별자 관리 END   ===============================================================================


        #region UI 관련 관리 값 START===============================================================================
        // 랭킹 UI 스로틀(ms) - textBox5 등 주기 갱신
        public static readonly int UiRefreshMs = 750;

        // 거래대금 상위 노출 개수
        public static readonly int RankingTopN = 10;

        // 엔진 내부 병렬 파티션 개수(코어수 기반 기본값)
        public static readonly int Parallelism = Math.Max(2, Environment.ProcessorCount / 2);
        #endregion UI 관련 관리 값 END===============================================================================

        #region 스크린/딥 관리 파라미터 START===============================================================================
        public static readonly int MaxDeepSlots = 80;      // DEEP 동시 구독 상한
        public static readonly int IdleDemoteSec = 5;      // 마지막 틱 이후 N초 무활동이면 강등
        public static readonly int SweepIntervalMs = 1000; // 아이들 스윕 주기(ms)
        #endregion  스크린/딥 관리 파라미터 END   ===============================================================================


        // === 화면(Screen) 정책: 목적별 대역/수용량 (단일 소스) ================================================
        public static class Screens
        {
            // 목적별 베이스 대역 (필요 시 값만 수정)
            public const int TrBase = 2000;   // TR 요청
            public const int RealLight = 4000;   // 라이트 실시간
            public const int RealDeep = 4100;   // 딥 실시간
            public const int RealStock = 5000;   // (기존 real_stock 풀 사용 시)
            public const int OrderBase = 5600;   // 주문
            public const int Condition = 6000;   // 조건식

            // 화면당 최대 등록 종목 수 (권장: 50~80 범위 내에서 조정)
            public const int PerPoolCap = 80;
        }

        // === 실시간 정보 받아오는 정보별 코드값 관리 =======================================================

        public static class Realtime
        {
            // 전종목 라이트 피드 활성화 여부 (KOSDAQ 등 유니버스 라이트 등록) // 기본 false: 전종목 라이트 금지 (조건식 결과만 실시간)
            public static readonly bool EnableKosdaqLightAll = false;
            //public static readonly bool EnableKosdaqLightAll = true;

            // 조건식 재확인 주기(초) — 너무 잦으면 API 부하 ↑, 180~600 권장
            public static readonly int ConditionRefreshSec = 600;

            // 실시간 FID 구성 중앙 관리 (현재가/등락률/누적거래량/누적거래대금/체결강도)
            public static readonly string LightFids = "10;12;13;14;20;228";
            public static readonly string DeepFids = "10;12;13;20;27;28;41;61;71;81;121;122"; // 심층호가 등


            // DEEP→LIGHT 강등 유예(ms). 포지션/미체결 종료 후 이 시간 지나면 강등 허용.
            public static readonly int GraceMsBeforeDowngrade = 30_000;

            // (옵션) 폴백 TR 주기(초). 포지션/미체결 존재 시에만 낮은 빈도로 계좌/미체결 조회.
            public static readonly int FallbackTrPollSec = 30;

            // 워치독: 마지막 틱 수신 없을 때 DEEP 재보장(초) — 필요 시 사용
            public static readonly int DeepWatchdogNoTickSec = 15;
        }


        // ====================== TurnoverBurst@v1 전략 파라미터 ======================
        public static class BurstTick
        {
            // 틱 윈도우(최근 N틱 합으로 순간 거래대금 측정)
            public static readonly int WindowTicks = 50;

            // 기준선(EMA per-tick) 평활 계수 계산용: BaselineTicks → α = 2/(N+1)
            public static readonly int BaselineTicks = 200;

            // 상대 임계: (최근 WindowTicks 합) ≥ (EMA_per_tick × WindowTicks × Multiple)
            public static readonly double Multiple = 2.5;

            // 절대 하한(원): 평균이 작을 때의 가짜 버스트 컷 (코스닥 기준 제안 7억)
            public static readonly long MinDeltaWon = 700_000_000;

            // 재진입 쿨다운(초): 동일 종목 과열 진입 방지
            public static readonly int CooldownSec = 20;

            // VI 해제 직후 N초 무시(가짜 버스트 방지)
            
        }

        // === VI 공통 가드(단일 소스) =========================================
        public static class ViGuard
        {
            // 전체 토글
            public static readonly bool Enable = true;

            // VI 상단 트리거율(기준가 대비 +6% 등) — 시장 규정 바뀌면 여기만 수정
            public static readonly double UpperTriggerPct = 0.06;

            // 근접 마진: %와 틱 중 큰 값 적용
            public static readonly double ProximityPct = 0.015; // 1.5%
            public static readonly int ProximityTicks = 3;    // 3틱

            // 해제 직후 쿨다운(초)
            public static readonly int CooldownSec = 10;

            // 승격 기준가 대비 허용 상승폭 — 예: 3% 이상이면 근접차단
            public static readonly double MaxRisePct = 0.03;

        }



        public static class Entry
        {
            // 보조 가드(호가/등락 조건)
            public static readonly double MinChgPct = 2.0;     // 등락률 + 2% 이상
            public static readonly int MaxSpreadTicks = 2;     // 스프레드 틱 상한
            public static readonly double MinBidAskRatio = 1.8;// 매수잔량/매도잔량

            // ▼ 수량기반 매수 사이징
            public static readonly int DefaultOrderQty = 10;   // 기본 매수 수량

            // ▼ 금액기반 매수 사이징
            public static readonly bool EnableCashSizing = true;       // true면 금액기반으로 수량 자동 산정
            public static readonly int TargetOrderAmountKRW = 400_000; // 주문 1회당 목표 금액(원)
            public static readonly int MinOrderQty = 1;                // 최소 1주
            public static readonly int MaxOrderQtyPerSignal = 30;    // 안전 상한(필요시 조정)
            public static readonly int LotSize = 1;                    // 한국 주식은 1주 단위
        }

        // 스케일-인은(재매수 막는구문) v1에선 비활성 (후속 버전에서 활성화)
        public static class ScaleIn
        {
            public static readonly bool Enable = false;
            public static readonly int MaxAdds = 0;
        }


        // === 체결강도(Trade Power) 임계 ===========================================================
        // 키움 FID 228(체결강도, %) 기반. 예: 120 == 120%
        public const double MinTradePower = 120.0;   // 매수 허용 최소 체결강도


        // 모멘텀/추세 판단 공통 윈도우
        public static readonly TimeSpan MomentumWindow = TimeSpan.FromMilliseconds(300); // 0.3s

        // “연속 윈도우 상승” 요건
        public const int TrendConsecRequired = 2;     // 연속 2창 이상
        public const int TrendMinTickAdvance = 1;     // 창간 최고가가 최소 1틱 이상 진전


        // 필요 시 다른 공통 파라미터도 여기에 추가(거래대금/체결강도 임계 등)
        // public const int TradePowerThreshold = 120;
        // public const long MinTurnoverPerSec = 10_000_000;

        // === 거래 비용(세금/수수료) 및 순익 목표 ====================================================
        // 왜: 브로커 수수료·거래세는 시기/계좌마다 다름 → 파라미터로 중앙 관리
        public static readonly double BuyFeeRate  = 0.00015; // 매수 수수료 ) 0.015%
        public static readonly double SellFeeRate = 0.00015; // 매도 수수료 ) 0.015%
        public static readonly double SellTaxRate = 0.00150; // 매도 증권거래 세금 ) 0.15% (시장/기간별 상이)

        // ‘순이익’ 목표: 두 방식 중 원하는 것을 사용 (둘 다 설정 시 더 큰 쪽 적용)
        public static readonly int TakeProfitNetPerShareWon = 100;   // 1주당 순이익 목표(원). 0이면 미사용
        public static readonly double TakeProfitNetPct = 0.003; // 평균단가 대비 순이익 % (예: 0.003=0.3%). 0이면 미사용

        // 재주문/최종 대기 시간 파라미터
        public const int ReorderDelayMs = 3000;    // 재주문 간격: 3초
        public const int FinalWaitMs = 61000;    // 순이익 +1틱 단계에서 최종 대기: 1분 1초(앞에 6초 딜레이까지 계산)
        public const int FinalWaitAfterBuyMs = 58000;   // 추격매수: 1분 1초

        // 재매수 가드: 직전 매수가 대비 몇 틱 하락 시 당일 재매수 금지 (요청: 8틱)
        public const int RebuyGuardDownTicks = 8;


        // === 공통 파라미터(실거래/백테 공용 백테스팅과 혼용) =============================================
        public static int MaxSpreadTicks { get; } = 2;
        public static double SlippageK { get; } = 0.8;
        public static int MaxSlipTicks { get; } = 2;

        public static int TurnoverWindowMs { get; } = 1000;
        public static int IntensityWindowMs { get; } = 1000;

        // === 전종목 받아올지 의사결정 로그 토글 ===
        public static bool EnableDecisionLog { get; } = true;


        // === BacktestCollector(실시간 수집기) 재시작-이어쓰기 SSOT 파라미터 ===
        public static class Collector
        {
            // 같은 RUN_ID 폴더로 재기동 시, 기존 CSV 끝에서 이어쓰기 허용
            public static readonly bool EnableResume = true;

            // 마지막 레코드(seq) 복구를 위해 파일 끝에서 읽을 바이트 수
            public static readonly int TailScanBytes = 64 * 1024; // 64KB

            // CSV 헤더는 파일이 비어 있을 때만 1회 기록
            public static readonly bool WriteHeaderOnlyWhenEmpty = true;

            // 세션 메타(SESSION_*.txt) 기록 토글
            public static readonly bool WriteSessionMeta = true;

            // 자정 자동 롤오버 토글
            public static readonly bool AutoRolloverAtMidnight = true;
        }

        // === Backtest Collector 안전 설정 ===
        // 기존 필드/시그니처는 변경하지 않음. 필요한 상수만 추가.
        public static class CollectorSafety
        {
            // 꼬리 복구 시 뒤에서부터 스캔할 최대 바이트(기존 구현치 유지/노출)
            public static readonly int TailScanBytes = 64 * 1024;

            // CSV 검증 기준: 헤더의 구분자(,) 개수를 기준으로 라인 유효성 판단
            // (스키마 수가 변해도 헤더만 일치하면 안전)
            public static readonly char CsvDelimiter = ',';

            // CSV 읽기 시 비정상 줄을 스킵할지 여부 (백테스터·유틸 공용)
            public static readonly bool SkipMalformedCsvLine = true;
        }


    }



    // ============================================================
    // 실거래/백테 공용 계산 로직 — 변경 시 전 구간(실전/백테/테스트)에 즉시 영향
    // ============================================================

    // ── 스프레드/슬리피지: 순수 함수 ──
    internal static class SpreadGuard
    {
        public static int SpreadTicks(int bid, int ask, int tickSize)
        {
            if (bid <= 0 || ask <= 0 || ask <= bid || tickSize <= 0) return int.MaxValue;
            return (ask - bid) / tickSize;
        }

        public static bool Evaluate(int bid, int ask, int tickSize, int maxSpreadTicks, out int spreadTicks)
        {
            spreadTicks = SpreadTicks(bid, ask, tickSize);
            if (spreadTicks == int.MaxValue) return false;
            return spreadTicks <= (maxSpreadTicks < 0 ? 0 : maxSpreadTicks);
        }
    }

    internal static class SlippageModel
    {
        // 매수 체결가 추정
        public static int EstimateBuyFill(int ask, int askQty, int orderQty, int tickSize, double k, int maxSlipTicks, out int impactTicks)
        {
            impactTicks = 0;
            if (ask <= 0 || tickSize <= 0 || orderQty <= 0) return 0;

            double denom = Math.Max(1.0, (double)askQty * Math.Max(0.1, k));
            var raw = (int)Math.Ceiling(orderQty / denom);
            if (raw < 0) raw = 0;
            impactTicks = Math.Min(raw, Math.Max(0, maxSlipTicks));

            return ask + impactTicks * tickSize;
        }

        // 매도 체결가 추정
        public static int EstimateSellFill(int bid, int bidQty, int orderQty, int tickSize, double k, int maxSlipTicks, out int impactTicks)
        {
            impactTicks = 0;
            if (bid <= 0 || tickSize <= 0 || orderQty <= 0) return 0;

            double denom = Math.Max(1.0, (double)bidQty * Math.Max(0.1, k));
            var raw = (int)Math.Ceiling(orderQty / denom);
            if (raw < 0) raw = 0;
            impactTicks = Math.Min(raw, Math.Max(0, maxSlipTicks));

            return bid - impactTicks * tickSize;
        }
    }

    // ── 거래대금/체결강도: 롤링 윈도우 ──
    internal interface IRollingTurnover
    {
        void OnTick(DateTime tsUtc, int price, int qty); // 틱 공급
        long GetTurnover();                              // 윈도우 합계(원)
    }

    internal interface IAggressorIntensity
    {
        void OnTick(DateTime tsUtc, int price, int bid, int ask, int qty); // 틱 공급
        int BuySum();     // 윈도우 내 매수 체결수량
        int SellSum();    // 윈도우 내 매도 체결수량
        double Intensity();  // Buy / max(Sell,1)
        double BuyRatio();   // Buy / max(Buy+Sell,1)
    }

    internal sealed class RollingTurnover1s : IRollingTurnover
    {
        private readonly int _ms = StrategyParams.TurnoverWindowMs;
        private readonly System.Collections.Generic.Queue<(long t, long v)> _q =
            new System.Collections.Generic.Queue<(long t, long v)>(64);
        private long _sum;

        public void OnTick(DateTime tsUtc, int price, int qty)
        {
            if (price <= 0 || qty <= 0) return;
            var now = tsUtc.Ticks / System.TimeSpan.TicksPerMillisecond;
            var val = (long)price * (long)qty;
            _q.Enqueue((now, val));
            _sum += val;

            var cut = now - _ms;
            while (_q.Count > 0 && _q.Peek().t < cut)
            {
                _sum -= _q.Dequeue().v;
                if (_sum < 0) _sum = 0;
            }
        }

        public long GetTurnover() => _sum;
    }

    internal sealed class AggressorIntensity1s : IAggressorIntensity
    {
        private readonly int _ms = StrategyParams.IntensityWindowMs;
        private readonly System.Collections.Generic.Queue<(long t, int buy, int sell)> _q =
            new System.Collections.Generic.Queue<(long t, int buy, int sell)>(64);
        private int _buy, _sell;

        public void OnTick(DateTime tsUtc, int price, int bid, int ask, int qty)
        {
            if (qty <= 0 || price <= 0 || bid <= 0 || ask <= 0) return;

            int b = 0, s = 0;
            if (price >= ask) b = qty;      // aggressor buy
            else if (price <= bid) s = qty; // aggressor sell

            var now = tsUtc.Ticks / System.TimeSpan.TicksPerMillisecond;
            _q.Enqueue((now, b, s));
            _buy += b; _sell += s;

            var cut = now - _ms;
            while (_q.Count > 0 && _q.Peek().t < cut)
            {
                var it = _q.Dequeue();
                _buy -= it.buy; _sell -= it.sell;
                if (_buy < 0) _buy = 0; if (_sell < 0) _sell = 0;
            }
        }

        public int BuySum() => _buy;
        public int SellSum() => _sell;
        public double Intensity() => _sell > 0 ? (double)_buy / _sell : (_buy > 0 ? double.PositiveInfinity : 0.0);
        public double BuyRatio() { var d = _buy + _sell; return d > 0 ? (double)_buy / d : 0.0; }
    }

    // ── 코드별 메트릭 레지스트리(실거래/백테 공용) ──
    internal sealed class MetricsRegistry
    {
        private static readonly MetricsRegistry _inst = new MetricsRegistry();
        public static MetricsRegistry Instance => _inst;

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IRollingTurnover> _turn =
            new System.Collections.Concurrent.ConcurrentDictionary<string, IRollingTurnover>(System.StringComparer.OrdinalIgnoreCase);

        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IAggressorIntensity> _intn =
            new System.Collections.Concurrent.ConcurrentDictionary<string, IAggressorIntensity>(System.StringComparer.OrdinalIgnoreCase);

        private MetricsRegistry() { }

        private static string Key(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return code;
            return (code[0] == 'A' ? code.Substring(1) : code);
        }

        public void OnTick(string code, DateTime tsUtc, int price, int bid, int ask, int qty)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            var k = Key(code);

            var t = _turn.GetOrAdd(k, _ => new RollingTurnover1s());
            t.OnTick(tsUtc, price, qty);

            var a = _intn.GetOrAdd(k, _ => new AggressorIntensity1s());
            a.OnTick(tsUtc, price, bid, ask, qty);
        }

        public (long turnover, int buy, int sell, double intensity, double buyRatio) Snapshot(string code)
        {
            var k = Key(code);
            _turn.TryGetValue(k, out var t);
            _intn.TryGetValue(k, out var a);

            var to = t != null ? t.GetTurnover() : 0L;
            var b = a != null ? a.BuySum() : 0;
            var s = a != null ? a.SellSum() : 0;
            var it = a != null ? a.Intensity() : 0.0;
            var br = a != null ? a.BuyRatio() : 0.0;

            return (to, b, s, it, br);
        }
    }

    // ── (옵션) 의사결정 스냅샷 로거(JSONL) ──
    internal static class DecisionLogger
    {
        private static readonly object _sync = new object();
        private static System.IO.StreamWriter _sw;

        private static System.IO.StreamWriter GetWriter()
        {
            if (!StrategyParams.EnableDecisionLog) return null;
            if (_sw != null) return _sw;
            try
            {
                var dir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", StrategyParams.RunId);
                System.IO.Directory.CreateDirectory(dir);
                var path = System.IO.Path.Combine(dir, "DECISION.log");
                _sw = new System.IO.StreamWriter(
                    new System.IO.FileStream(path, System.IO.FileMode.Append, System.IO.FileAccess.Write, System.IO.FileShare.Read),
                    new System.Text.UTF8Encoding(false), 1 << 16
                );
            }
            catch { _sw = null; }
            return _sw;
        }

        public static void LogBuy(string code, int reqQty, int bid, int ask, int bidQty, int askQty, int tickSize, int spreadTicks, int estFill, int impactTicks)
        {
            if (!StrategyParams.EnableDecisionLog) return;
            try
            {
                var snap = MetricsRegistry.Instance.Snapshot(code);
                var o = new
                {
                    ts_utc = DateTime.UtcNow.ToString("o"),
                    run_id = StrategyParams.RunId,
                    strat = StrategyParams.CanonicalTag,
                    side = "BUY",
                    code = (code != null && code.StartsWith("A") ? code.Substring(1) : code),
                    req_qty = reqQty,
                    bid,
                    ask,
                    bidQty,
                    askQty,
                    tickSize,
                    spreadTicks,
                    estFill,
                    impactTicks,
                    turnover_1s = snap.turnover,
                    buy_1s = snap.buy,
                    sell_1s = snap.sell,
                    intensity_1s = snap.intensity,
                    buyratio_1s = snap.buyRatio
                };
                var line = System.Text.Json.JsonSerializer.Serialize(o);
                lock (_sync)
                {
                    var w = GetWriter();
                    if (w != null) { w.WriteLine(line); w.Flush(); }
                }
            }
            catch { /* 무시 */ }
        }
    }


}
