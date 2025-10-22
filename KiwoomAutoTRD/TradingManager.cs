using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ConstrainedExecution;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Globalization;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using KiwoomAutoTRD.Common;
using KiwoomAutoTRD.Adapters;
using KiwoomAutoTRD.Services;



namespace KiwoomAutoTRD
{
    // AFTER (추가/확장만, 기존 로직 불변)
    public class TradingManager : IDisposable
    {
        // 필드

        // ---------- 전략 파라미터/상태 ----------
        private KiwoomApi _kiwoomApi;    //  API 연결 핸들

        #region 거래대금 랭킹 엔진 연결   START    ============================================================


        // 마지막 틱 수신 시각(아이들 강등용)
        private readonly Dictionary<string, DateTime> _lastTickUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // 활성(DEEP) 집합 (실제 구독 중인 코드)
        private readonly HashSet<string> _deepCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);


        // 아이들 스윕 타이머
        private System.Threading.Timer _sweepTimer = null;

        // 거래대금 상위 계산식 엔진
        private TurnoverBurstEngine _tov;

        // 매매 엔진
        private TurnoverBurstEngine _burst = null;

        #endregion  거래대금 랭킹 엔진 연결   END ============================================================



        // DEEP 관리
        private readonly Dictionary<string, DeepState> _deep = new Dictionary<string, DeepState>();


        // 손절 블랙리스트(당일 재매수 금지)
        private readonly HashSet<string> _lossBlacklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase);


        // VI 중앙화 상태 캐시
        private readonly HashSet<string> _viTriggered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);  // 현재 VI 발동 중 코드
        private readonly Dictionary<string, int> _lastViKstSec = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 최근 VI 이벤트(KST epoch sec)


        // ---------- 익절 로직 상태 ----------
        private readonly Dictionary<string, DateTime> _aboveSince = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // Net 익절 Step-Down 하한(floor) 저장소
        private readonly Dictionary<string, int> _stepDownFloorByCode =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // === 1틱으로 주문 넣고 시장가로 매도 하기전 최종 대기 타이머
        private readonly Dictionary<string, DateTime> _tpFinalStartUtcByCode
            = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // 추격매수 종목 표시용 (한 번만 적용 후 제거)
        private readonly HashSet<string> _chaseBuyCodes = 
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 종목별 최종 대기 시간 기록용
        private readonly Dictionary<string, int> _tpFinalWaitMsByCode =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // 종목별 최종 매수가 기록용 (재매수 판단용)
        private readonly Dictionary<string, int> _lastPriceByCode =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);


        // 금액기반 수량 계산용 최근가 캐시
        private readonly System.Collections.Generic.Dictionary<string, int> _lastPrice
            = new System.Collections.Generic.Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);


        // (문맥 포함) 필드 섹션에 추가 — 기존 필드/시그니처 변경 금지
        // ---------- TurnoverBurst@v1: 틱 기반 거래대금 버스트 탐지 상태 ----------
        private readonly Dictionary<string, Queue<long>> _winVal = new Dictionary<string, Queue<long>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> _winSum = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> _emaPerTick = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastBuyUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // DEEP 캐시(호가/잔량/등락률) — OnDeepTick에서 갱신, OnTradeTick 트리거에서 사용
        private readonly Dictionary<string, int> _lastBestBid = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _lastBestAsk = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _lastBidQty = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _lastAskQty = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> _lastChgRt = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);


        // 매수가보다 연속 상승 유지 시간(초)
        private static readonly TimeSpan TAKE_PROFIT_ABOVE_DURATION = TimeSpan.FromSeconds(2);

        // 윈도우 길이(모멘텀 엔진의 평가주기와 맞춰 300ms 권장)
        //StategyParams에서 공통 정의로 이동
        private static readonly TimeSpan TREND_WINDOW
            = KiwoomAutoTRD.Services.StrategyParams.MomentumWindow;
        private static readonly int TREND_CONSEC_REQUIRED
    = KiwoomAutoTRD.Services.StrategyParams.TrendConsecRequired;
        private static readonly int TREND_MIN_TICK_ADVANCE
            = KiwoomAutoTRD.Services.StrategyParams.TrendMinTickAdvance;


        // 코드별 추세 상태
        private sealed class TrendState
        {
            public DateTime WindowStartUtc;   // 현재 창의 시작 시각
            public int WindowMaxPrice;        // 현재 창에서 관측한 최고가
            public int PrevWindowMaxPrice;    // 직전 창의 마감 최고가
            public DateTime PrevEvalUtc;      // 직전 창 평가 시각
            public int ConsecUp;              // 연속 상승 창 카운트
            public bool Initialized;          // 첫 창 초기화 여부
        }

        private readonly Dictionary<string, TrendState> _trend
        = new Dictionary<string, TrendState>(StringComparer.OrdinalIgnoreCase);

        // ----------- 손절 시장가 매도(STRAT_STOP_LOSS_MKT)로 보낸 종목을 일시 태깅 → OnOrderAccepted에서 PendingOrder에 표시
        private readonly HashSet<string> _pendingStopLoss = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ---------- 미체결 주문 관리 (기존 주문 포트 사용) ----------
        private readonly Dictionary<string, PendingOrder> _pendingByOrdNo = new Dictionary<string, PendingOrder>(StringComparer.Ordinal);
        private readonly object _lockPending = new object();
        
        private readonly Timer _cancelTimer;

        // 손절(SELL) 재시도 타임아웃(초)
        private const int CANCEL_SELL_RETRY_TIMEOUT_SEC = 1;

        // 매수 미체결 자동취소 임계(초)  3초
        private const int CANCEL_BUY_TIMEOUT_SEC = 3;


        // 매도 재시도 최대 횟수(손절 무제한 재시도)
        private const int SELL_RETRY_MAX = int.MaxValue;    //   2; //앞에 "int.MaxValue;" 지우고 수"2;" 만 넣으면 재 매도 2번으로 제한 (현재는 무제한) 


        // ---------- 리소스관리 ----------
        private bool _disposed; // 중복 해제 방지 플래그



        // 보유 포지션(간이) — 손절 트리거용
        private sealed class Position
        {
            public int Qty;
            public int AvgPrice;
            public int LastBuyPrice; // [추가] 마지막 매수가 저장
            public int LastBuyYmd;   // YYYYMMDD (KST 기준)  // ★ [추가] 당일 판단용
        }


        private readonly Dictionary<string, Position> _positions = new Dictionary<string, Position>(StringComparer.OrdinalIgnoreCase);

        // 손절 임계값
        private const double STOP_LOSS_PCT = 0.012; // 1.2%


        // 모멘텀 엔진
        private MomentumEngine _mom;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();



        #region 실시간 틱 수신 정보 전략에 DTO로 보내주기 START ===================================================================
        // 실시간 틱 수신(전략 워커 진입 지점) - 계산은 엔진에서 수행
        // === 실시간 DTO 수신 ===
        public void OnRealTick(TickDto dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.Code)) return;

            // 마지막 틱 갱신(아이들 감시)
            _lastTickUtc[dto.Code] = dto.TsUtc;

            // 2단계: 랭킹
            var t = _tov; if (t != null) t.Enqueue(dto);
            // 3단계: 버스트
            var b = _burst; if (b != null) b.Enqueue(dto);
        }


        // VI 이벤트 수신(발동/해제/근접 등은 허브에서 확장)
        public void OnViEvent(ViEventDto dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.Code)) return;
            var code = Normalize(dto.Code);

            // KST epoch sec 기록
            _lastViKstSec[code] = (int)(DateTime.UtcNow.AddHours(9) - new DateTime(1970, 1, 1)).TotalSeconds;

            // 발동/해제 상태 캐시
            if (dto.IsFired) _viTriggered.Add(code);
            else _viTriggered.Remove(code);

            // 필요시: 발동 직후 DEEP 강등이 정책이라면 아래 라인 유지
            // if (dto.IsFired) Demote(code, reason: "vi_fired");
        }



        // KiwoomApi 초기 바인딩 시 엔진 스타트(기존 BindApi 호출부 유지)
        // ---------- API 바인딩 ----------
        public void BindApi(KiwoomApi api)
        {
            try
            {
                if (_kiwoomApi != null)
                    _kiwoomApi.TradeTick -= OnTradeTick; // 중복 구독 방지
            }
            catch
            {
                // 무시 (종료 경로/중복해제 방어)
            }

            _kiwoomApi = api;

            // 모멘텀 엔진 생성(의존: KiwoomApi 공개 헬퍼)
            _mom = new MomentumEngine(
                _kiwoomApi.GetTickSize,
                _kiwoomApi.IsDeep,
                (string code, int price) => { TryPlaceBuy(code, price); },
                (string code, int price) => { TryPlaceSell(code, price); });

            // 실시간 틱 구독
            try
            {
                _kiwoomApi.TradeTick -= OnTradeTick; // 안전가드
                _kiwoomApi.TradeTick += OnTradeTick;
            }
            catch
            {
                // 무시
            }
        }


        // === 승격/강등: 전략에서 신호만 내면 여기서 일원화 처리 ===
        public bool Promote(string code, string reason = "strategy_promote")
        {
            if (string.IsNullOrWhiteSpace(code)) return false;

            // 이미 활성 → 참조수만 증가
            if (_deepCodes.Contains(code))
            {
                return true;
            }

            // 상한 체크
            if (_deepCodes.Count >= StrategyParams.MaxDeepSlots)
            {
                Log("[DEEP] promote.skipped capacity full " + code + " size=" + _deepCodes.Count);
                return false;
            }

            // ✅ ScreenPool에서 슬롯만 확보
            var slot = ScreenManager.Get("real_stock").ReserveSlot();
            var screen = slot.Screen; // ex: "5000" ~ "5599" 내 가용 스크린

            // UI 스레드에서 구독
            var ok = _kiwoomApi != null && _kiwoomApi.SubscribeDeep(code, screen); // ★ _kiwoomApi 로 통일
            if (!ok)
            {
                Log("[DEEP] promote.fail subscribe_error " + code + " screen=" + screen);
                return false;
            }

            _deepCodes.Add(code);
            Log("[DEEP] promote.ok " + code + " screen=" + screen + " reason=" + reason);
            return true;
        }

        public bool Demote(string code, string reason = "strategy_demote")
        {
            if (string.IsNullOrWhiteSpace(code)) return false;

            // ✅ 실제 구독 해제만 수행 (코드 단위)
            var ok = _kiwoomApi != null && _kiwoomApi.UnsubscribeDeep(code);

            // 로컬 상태 제거
            _deepCodes.Remove(code);

            if (ok) Log("[DEEP] demote.ok " + code + " reason=" + reason);
            else Log("[DEEP] demote.warn unsub_fail " + code + " reason=" + reason);
            return ok;
        }


        // === 아이들 스윕(마지막 틱 이후 IdleDemoteSec 지나면 강등) ===
        private void SweepIdle()
        {
            try
            {
                if (_deepCodes.Count == 0) return;

                var now = DateTime.UtcNow;
                var idleList = new List<string>(8);

                foreach (var code in _deepCodes)
                {
                    DateTime ts;
                    if (!_lastTickUtc.TryGetValue(code, out ts)) continue;
                    if ((now - ts).TotalSeconds >= StrategyParams.IdleDemoteSec)
                        idleList.Add(code);
                }

                foreach (var c in idleList)
                    Demote(c, reason: "idle_timeout");
            }
            catch
            {
                // 안전 무시
            }
        }

        // === 신호 라우팅(실제 Risk/RateLimit/주문 호출 연결 필요) ===
        private void OnBurstBuySignal(BurstBuySignal sig)
        {
            if (sig == null || string.IsNullOrWhiteSpace(sig.Code)) return;
            try
            {
                if (!OrderRateLimiter.TryAcquire()) return;
                Promote(sig.Code, "burst_signal");
                // TODO: 주문 라우터 연동
                Log("[ORDER] burst_signal " + sig.Code + " qty=" + sig.Qty + " reason=" + sig.Reason + " spread=" + sig.SpreadTicks + " r=" + sig.ChangeRate.ToString("0.00") + "%");
            }
            catch { /* ignore */ }
        }





        // === 간단 로그 헬퍼 ===
        private void Log(string msg)
        {
            try { System.Diagnostics.Debug.WriteLine(msg); } catch { }
        }

        public void ShutdownEngines()
        {
            try { _tov?.Stop(); } catch { }
            try { _burst?.Stop(); } catch { }
            try { _sweepTimer?.Dispose(); } catch { }

            try
            {
                var snapshot = new List<string>(_deepCodes);
                foreach (var c in snapshot) Demote(c, "shutdown");
            }
            catch { }
        }



        #endregion  실시간 틱 수신 정보 전략에 DTO로 보내주기 END ===================================================================



        private sealed class DeepState
        {
            public DateTime PromotedAtUtc;
            public int PromoteBestBid;      // 승격 당시 최우선 매수호가
            public int ViBasePrice;         // 승격 직후 기준가(현재가)
            public bool Bought;             // 중복 매수 방지
            public bool Initialized;        // 첫 틱 초기화 완료 여부
        }


        // 미체결 주문 추적용
        private sealed class PendingOrder
        {
            public string OrdNo;
            public string Code;
            public int Qty;                // 접수 시점 주문수량(잔량과 다를 수 있음)
            public DateTime AcceptedUtc;

            // 매수/매도 구분 + 중복 취소 방지
            public string Side;          // "BUY" / "SELL" 구분
            public bool CancelRequested;    // 취소 요청 1회만 보내기 위한 플래그
            public int RetryCount;  // 재시도 횟수

            //  손절 매도 여부(이 값이 true인 SELL만 1초 후 취소→시장가 재발주)
            public bool IsStopLoss;

            // 최초 주문가/재주문가 추적용
            public int LastPrice;   // ← 여기에 추가

        }


        // ---------- 기존 콜백 ----------
        public void OnLoginSuccess(string accNo) => Console.WriteLine($"[LOGIN] Account={accNo}");
        public void OnRealData(string sRealKey, string sRealType)
        {
            // ★ 실시간 콘솔 로그는 과다하므로 차단
            // Console.WriteLine($"[REAL] {sRealType}:{sRealKey}");

        }
        public void OnTrData(string rqName, string trCode, string scrNo) => Console.WriteLine($"[TR] {rqName}/{trCode}/{scrNo}");
        public void OnChejanData(string gubun, int itemCnt, string fidList) => Console.WriteLine($"[CHEJAN] gubun={gubun}, cnt={itemCnt}");

        

        // ---------- 금액기반 사이징 헬퍼 (price는 현재가 또는 최근 체결가)---------- 
        private int ResolveOrderQty(string code, int price)
        {
            int qty = StrategyParams.Entry.DefaultOrderQty;

            if (StrategyParams.Entry.EnableCashSizing && price > 0)
            {
                long budget = StrategyParams.Entry.TargetOrderAmountKRW;
                long calc = budget / (long)price;

                if (calc < StrategyParams.Entry.MinOrderQty) calc = StrategyParams.Entry.MinOrderQty;
                if (calc > StrategyParams.Entry.MaxOrderQtyPerSignal) calc = StrategyParams.Entry.MaxOrderQtyPerSignal;

                int lot = StrategyParams.Entry.LotSize > 0 ? StrategyParams.Entry.LotSize : 1;
                int rounded = (int)(calc / lot) * lot;
                if (rounded < StrategyParams.Entry.MinOrderQty) rounded = StrategyParams.Entry.MinOrderQty;

                qty = rounded;
            }
            return qty;
        }

        // ---------- VI 발동/해제 이벤트 처리 ----------

        // VI 발동/해제(통합). isTriggered=true면 발동→매수 금지, false면 해제→금지 해제.
        public void OnViTriggered(string code, bool isTriggered)
        {
            code = Normalize(code);
            if (string.IsNullOrWhiteSpace(code)) return;

            _lastViKstSec[code] = (int)(DateTime.UtcNow.AddHours(9) - new DateTime(1970, 1, 1)).TotalSeconds;
            if (isTriggered) _viTriggered.Add(code);
            else _viTriggered.Remove(code);
        }

        // ------- [호환용] 기존 단일 메서드 호출을 대비한 오버로드(원본 호출과의 호환성 유지) ------
        public void OnViTriggered(string code) => OnViTriggered(code, true);
        public void OnViReleased(string code) => OnViTriggered(code, false);
        // ----------------------------------------------------------------------------------------


        // === 주문 직전 공통 VI 차단 게이트 ========================================
        // estBuy : 이번에 체결될 것으로 보는 매수가(호가 단위 반영 전/후 허용)
        // estTp  : 순이익 기준 목표 매도(없으면 0)
        private bool IsViBuyBlocked(string code, int estBuy, int estTp, out string reason)
        {
            reason = null;
            if(!KiwoomAutoTRD.Services.StrategyParams.ViGuard.Enable) return false;
            if (string.IsNullOrWhiteSpace(code)) return false;

            // 1) 현재 발동 중 차단
            if (_viTriggered.Contains(code))
            {
                reason = "VI-TRIGGERED";
                return true;
            }

            // 2) 해제 직후 쿨다운
            int last;
            if (_lastViKstSec.TryGetValue(code, out last))
            {
                int nowKst = (int)(DateTime.UtcNow.AddHours(9) - new DateTime(1970, 1, 1)).TotalSeconds;
                if (nowKst - last <= KiwoomAutoTRD.Services.StrategyParams.ViGuard.CooldownSec)
                {
                    reason = "VI-COOLDOWN";
                    return true;
                }
            }

            // 3) 상단 근접(틱/%) — DeepState에 보존한 기준가 필요
            DeepState st;
            if (!_deep.TryGetValue(code, out st) || st == null || st.ViBasePrice <= 0)
                return false;

            // 상단 트리거 계산
            double upRaw = st.ViBasePrice * (1.0 + KiwoomAutoTRD.Services.StrategyParams.ViGuard.UpperTriggerPct);
            int tick = get_hoga_unit_price((int)Math.Round(upRaw));
            if (tick <= 0) tick = 1;
            int viUp = ((int)Math.Ceiling(upRaw / tick)) * tick;

            // 근접 여유(틱/%) 중 큰 값
            int marginByTicks = Math.Max(KiwoomAutoTRD.Services.StrategyParams.ViGuard.ProximityTicks, 1) * tick;
            int marginByPct = (int)Math.Round(viUp * KiwoomAutoTRD.Services.StrategyParams.ViGuard.ProximityPct);
            int nearMargin = Math.Max(marginByTicks, marginByPct);

            bool nearBuy = (estBuy > 0) && (estBuy >= viUp - nearMargin);
            bool nearTp = (estTp > 0) && (estTp >= viUp - nearMargin);

            if (nearBuy || nearTp)
            {
                reason = nearBuy ? "VI-NEAR-BUY" : "VI-NEAR-TAKEPROFIT";
                return true;
            }

            return false;
        }




        // ---------- VI 블랙리스트 조회용 스냅샷 메서드 ----------
        // UI(textBox1)에서 현재 VI 활성 종목을 즉시 나열하기 위해 읽기 전용 스냅샷 제공
        public string[] GetViBlacklistSnapshot()
        {
            // 현재 발동 중인 VI 종목 목록을 UI에 표시
            return _viTriggered.ToArray();
        }

        public string[] GetLossBlacklistSnapshot() { return _lossBlacklist.ToArray(); }


        // ---------- 승격 알림 ----------
        public void OnDeepPromoted(string code, int promoteBestBid, int viBasePrice)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            code = Normalize(code);

            _deep[code] = new DeepState
            {
                PromotedAtUtc = DateTime.UtcNow,
                PromoteBestBid = promoteBestBid,
                ViBasePrice = viBasePrice,
                Bought = false,
                Initialized = true
            };

            // Console.WriteLine($"[STRAT] DEEP promoted init: {code} base={viBasePrice} bid={promoteBestBid}"); // 전략 로그
        }

        // ---------- 틱 처리(심층 판정) ----------
        public void OnDeepTick(string code, int lastPrice, int bestBid, int bestAsk, int bidQty, int askQty, double chgRt)
        {
            // 0) 방어
            if (string.IsNullOrWhiteSpace(code) || lastPrice <= 0 || bestBid <= 0 || bestAsk <= 0) return;
            if (_kiwoomApi == null) return;

            // 1) ★ 먼저 정규화 → 딕셔너리 키 일관성 유지
            code = Normalize(code);

            // 2) 캐시 갱신 (정규화된 code로 저장)
            _lastBestBid[code] = bestBid;
            _lastBestAsk[code] = bestAsk;
            _lastBidQty[code] = bidQty;
            _lastAskQty[code] = askQty;
            _lastChgRt[code] = chgRt;
            _lastPrice[code] = lastPrice;
            _lastPriceByCode[code] = lastPrice;

            // tickSize는 반드시 lastPrice로 계산 (미사용이면 제거해도 됨)
            // int tickSize = _kiwoomApi.GetTickSize(code, lastPrice);

            UpdateTrend(code, lastPrice);

            // 손절 블랙리스트(당일 재매수 금지)
            if (_lossBlacklist.Contains(code)) return;

            // ----- 보유 포지션 손절/익절 처리 -----
            Position pos;
            if (_positions.TryGetValue(code, out pos) && pos != null && pos.Qty > 0 && pos.AvgPrice > 0)
            {
                // === 정확익절(Net) 계산 ===
                int netTarget = CalcNetTakeProfitTarget(code, pos.AvgPrice, pos.Qty);
                Console.WriteLine("[DBG][NET] code=" + code + " avg=" + pos.AvgPrice + " qty=" + pos.Qty + " netTarget=" + netTarget);

                if (netTarget > 0)
                {
                    int basis = bestBid > 0 ? bestBid : lastPrice;
                    DateTime now = DateTime.UtcNow;

                    if (basis >= netTarget)
                    {
                        DateTime since;
                        if (!_aboveSince.TryGetValue(code, out since))
                        {
                            _aboveSince[code] = now;
                        }
                        else
                        {
                            if ((now - since) >= TAKE_PROFIT_ABOVE_DURATION)
                            {
                                int qtyToSell = pos.Qty;
                                if (qtyToSell > 0)
                                {
                                    int askPx = (bestAsk > 0) ? bestAsk : basis;
                                    bool ok = false;
                                    try
                                    {
                                        ok = _kiwoomApi.SendOrderSell("STRAT_TP_HOLD2S", code, qtyToSell, askPx);
                                    }
                                    catch (Exception ex)
                                    {
                                        try { TradingEvents.RaiseTradeInfo("[TP][ERR] HOLD2S SELL " + code + " ex=" + ex.Message); } catch { }
                                    }
                                    finally
                                    {
                                        _aboveSince.Remove(code);
                                        _stepDownFloorByCode[code] = netTarget;
                                    }

                                    if (ok)
                                    {
                                        try { TradingEvents.RaiseTradeInfo("[TP] 2s≥net → ASK SELL: " + code + " " + qtyToSell + "@" + askPx); } catch { }
                                        // 필요 시 return; 고려
                                    }
                                }
                            }
                        }
                    }
                    else
                    {
                        if (_aboveSince.ContainsKey(code)) _aboveSince.Remove(code);
                    }
                }

                // [시간 기반 익절]
                DateTime now2 = DateTime.UtcNow;
                if (lastPrice > pos.AvgPrice)
                {
                    DateTime started;
                    if (!_aboveSince.TryGetValue(code, out started))
                    {
                        _aboveSince[code] = now2;
                    }
                    else
                    {
                        if ((now2 - started) >= TAKE_PROFIT_ABOVE_DURATION)
                        {
                            int qtyToSellTime = pos.Qty;
                            int stepTick2 = get_hoga_unit_price(lastPrice); if (stepTick2 <= 0) stepTick2 = 1;
                            int minPlus1 = pos.AvgPrice + stepTick2;
                            int netBreakEven = CalcNetBreakEvenPrice(code, pos.AvgPrice);
                            int floorTime = Math.Max(minPlus1, netBreakEven);

                            if (bestAsk > 0 && bestAsk >= floorTime && qtyToSellTime > 0)
                            {
                                bool soldTime = _kiwoomApi.SendOrderSell("STRAT_TP_TIME_BESTASK", code, qtyToSellTime, bestAsk);
                                if (soldTime)
                                {
                                    try { TradingEvents.RaisePending("TP(2초↑-BESTASK) SELL " + code + " " + qtyToSellTime + "@" + bestAsk + " (floor=" + floorTime + ")"); } catch { }
                                    Console.WriteLine("[TP][TIME-BESTASK] " + code + " avg=" + pos.AvgPrice + " bid=" + bestBid + " ask=" + bestAsk + " floor=" + floorTime + " qty=" + qtyToSellTime);
                                    if (_aboveSince.ContainsKey(code)) _aboveSince.Remove(code);
                                    DeepState st2; if (_deep.TryGetValue(code, out st2) && st2 != null) st2.Bought = false;
                                    return;
                                }
                            }
                        }
                    }
                }
                else
                {
                    if (_aboveSince.ContainsKey(code)) _aboveSince.Remove(code);
                }

                // [손절]
                int stopPx = (int)Math.Floor(pos.AvgPrice * (1.0 - STOP_LOSS_PCT));
                if (lastPrice <= stopPx)
                {
                    int qtyToSell = pos.Qty;
                    int px = (bestBid > 0 ? bestBid : lastPrice);

                    _pendingStopLoss.Add(code);
                    bool sold = _kiwoomApi.SendOrderSellMarket("STRAT_STOP_LOSS_MKT", code, qtyToSell);

                    if (sold)
                    {
                        TradingEvents.RaisePending("STOP-LOSS SELL " + code + " " + qtyToSell + "@" + px + " (대기)");
                        Console.WriteLine("[STOP] Trigger " + code + " avg=" + pos.AvgPrice + " now=" + lastPrice + " px=" + px + " qty=" + qtyToSell);
                        _lossBlacklist.Add(code);
                        DeepState st2; if (_deep.TryGetValue(code, out st2) && st2 != null) st2.Bought = false;
                    }
                }
            }

            // DeepState 초기화
            DeepState st;
            if (!_deep.TryGetValue(code, out st))
            {
                st = new DeepState
                {
                    PromotedAtUtc = DateTime.UtcNow,
                    PromoteBestBid = bestBid,
                    ViBasePrice = lastPrice,
                    Bought = false,
                    Initialized = true
                };
                _deep[code] = st;
            }

            if (st.Bought) return; // 중복 매수 방지

            // ---- 매수 조건
            bool cond1 = bestBid > 0;
            bool cond2 = bidQty > askQty;
            int tick = get_hoga_unit_price(bestBid);
            bool cond3 = bestBid >= st.PromoteBestBid + (tick * 3);

            if (_lossBlacklist.Contains(code)) return;

            // (기존)
            if (cond1 && cond2 && cond3)
            {
                int qty = 1; // 정책 단순화
                             // === VI 공통 게이트(주문 직전) ===
                int estTp = CalcNetTakeProfitTarget(code, bestBid, qty);
                string viWhy;
                if (IsViBuyBlocked(code, bestBid, estTp, out viWhy))
                {
                    try { TradingEvents.RaisePending($"[BLOCK][VI] {code} {viWhy} estBuy={bestBid} estTP={estTp}"); } catch { }
                    return;
                }

                bool ok = _kiwoomApi.SendOrderBuy("STRAT_DEEP_BUY", code, qty, bestBid);
                if (ok)
                {
                    st.Bought = true;
                    Console.WriteLine($"[ORDER] BUY {code} qty={qty} price={bestBid} (tick={tick})");
                }
                else
                {
                    Console.WriteLine($"[ORDER-FAIL] BUY {code} {bestBid}");
                }
            }

        }



        // WHY: 시간 기반 익절도 손실 방지(순손익 0 이상) 가드가 필요
        private int CalcNetBreakEvenPrice(string code, int avgPrice)
        {
            // 중앙 파라미터 사용(수수료/세금)
            double buyFee = StrategyParams.BuyFeeRate;   // 매수 수수료
            double sellFee = StrategyParams.SellFeeRate;  // 매도 수수료
            double tax = StrategyParams.SellTaxRate;  // 거래세

            // (매도가)*(1 - 매도수수료 - 세금) >= (평단)*(1 + 매수수수료)
            double denom = (1.0 - sellFee - tax);
            if (denom <= 0.0) denom = 0.999; // 가드

            double raw = avgPrice * (1.0 + buyFee) / denom;

            // 호가단위로 올림
            int tick = get_hoga_unit_price((int)Math.Round(raw));
            if (tick <= 0) tick = 1;
            int px = (int)Math.Ceiling(raw / tick) * tick;
            return px;
        }


        // 거래시간 체크
        private static int GetKstYmd()
        {
            // KST = UTC+9 고정(서머타임 없음). 운영환경 시간대가 UTC여도 안전하게 계산.
            var kst = DateTime.UtcNow.AddHours(9);
            return kst.Year * 10000 + kst.Month * 100 + kst.Day;
        }

        private static bool IsSameKstDay(int ymd1, int ymd2)
        {
            return ymd1 == ymd2 && ymd1 > 0;
        }

        // 당일 바뀌면 종목별 재매수 제한 기준 리셋
        private static void MaybeResetDaily(Position p)
        {
            if (p == null) return;
            int today = GetKstYmd();
            if (!IsSameKstDay(p.LastBuyYmd, today))
            {
                // WHY: 이전 거래일의 매수가 기준이 오늘까지 이어지지 않도록 리셋
                p.LastBuyYmd = 0;
                p.LastBuyPrice = 0;
            }
        }

        // --- 검사하기 ---
        public TradingManager()
        {
            _cancelTimer = new Timer(CheckPendingOrders, null,
            StrategyParams.ReorderDelayMs,
            StrategyParams.ReorderDelayMs);// 3초
        }


        public TradingManager(KiwoomApi api /*, ... 기존 인자들 */)
        {
            if (api == null) throw new ArgumentNullException(nameof(api));

            // BindApi 내부에서 _kiwoomApi = api; 및 이벤트 구독/엔진 바인딩을 수행
            BindApi(api);

            // TurnoverBurstEngine 생성자 시그니처에 맞춰 재구성
            // (BindApi에서 이미 _tov를 만들었다면 중복 방지를 위해 널가드 유지)
            if (_tov == null)
            {
                _tov = new KiwoomAutoTRD.Services.TurnoverBurstEngine(
                    StrategyParams.Parallelism,
                    StrategyParams.RankingTopN,
                    StrategyParams.UiRefreshMs,
                    code =>
                    {
                        // 랭킹 단계 사전 필터: 최근가(px)와 중앙 TP 계산(estTp)로 VI 차단 판단
                        int px = 0;
                        _lastPrice.TryGetValue(code, out px);                // 근사 체결가
                        int estTp = CalcNetTakeProfitTarget(code, px, 1);    // 1주 기준 TP 근사
                        string _;
                        return IsViBuyBlocked(code, px, estTp, out _);
                    },
                    text => { try { TradingEvents.RaiseTradeInfo(text); } catch { } }
                );
                try { _tov.Start(); } catch { /* 안전가드 */ }

            }
        }


        // [OnTradeTick: 널가드]
        internal void OnTradeTick(string code, int price, int qty, DateTime tsUtc)
        {
            if (string.IsNullOrWhiteSpace(code) || price <= 0 || qty <= 0) return;

            string reason = string.Empty;
            int spreadTicks = 0;
            double ratio = 0.0, dChgRt = 0.0, sumWinM = 0.0, reqM = 0.0, emaPer = 0.0;

            if (_tov != null && _tov.TryEvaluateBuy(
                    code, price, qty, tsUtc,
                    out reason, out spreadTicks, out ratio, out dChgRt, out sumWinM, out reqM, out emaPer))
            {
                int px = price;
                int cached;
                if (_lastPrice.TryGetValue(code, out cached) && cached > 0) px = cached;

                // === VI 공통 게이트(시장가 추정가격 기준) ===
                int estTpTov = CalcNetTakeProfitTarget(code, px, 1);
                string viWhy;
                if (IsViBuyBlocked(code, px, estTpTov, out viWhy))
                {
                    TradingEvents.RaisePending($"[BLOCK][VI][TOV] {code} {viWhy} estBuy={px} estTP={estTpTov}");
                    return;
                }

                int qtyToBuy = ResolveOrderQty(code, px);
                bool ok = _kiwoomApi != null && _kiwoomApi.SendOrderBuyMarket("TOV_BURST_V1", code, qtyToBuy);
                if (ok)
                {
                    TradingEvents.RaiseTradeInfo(
                        "[BUY] code=" + code +
                        " qty=" + qtyToBuy +
                        " why=" + reason +
                        " sumWin=" + sumWinM.ToString("N1", CultureInfo.InvariantCulture) + "백만" +
                        " req=" + reqM.ToString("N1", CultureInfo.InvariantCulture) + "백만" +
                        " emaPer=" + emaPer.ToString("N0") +
                        " spread=" + spreadTicks +
                        " chgRt=" + dChgRt.ToString("+0.00;-0.00;0.00") + "%" +
                        " ratio=" + ratio.ToString("0.00") +
                        " tag=" + StrategyParams.CanonicalTag +
                        " run=" + StrategyParams.RunId
                    );
                }
                else
                {
                    TradingEvents.RaisePending("[BUY-FAIL] code=" + code + " reason=sendOrder ret=false tag=" + StrategyParams.CanonicalTag + " run=" + StrategyParams.RunId);
                }
            }
        }


        #region ===== 연속 윈도우 기반 상승 추세 감지 상태 START =====


        private bool IsTrendUp(string code)
        {
            TrendState st;
            if (!_trend.TryGetValue(code, out st) || st == null) return false;
            return st.ConsecUp >= TREND_CONSEC_REQUIRED;
        }


        // OnDeepTick에서 호출: 0.3초 창 기반 최고가/연속상승 상태 갱신
        private void UpdateTrend(string code, int lastPrice)
        {
            if (string.IsNullOrWhiteSpace(code) || lastPrice <= 0 || _kiwoomApi == null) return;
            TrendState st;
            if (!_trend.TryGetValue(code, out st))
            {
                st = new TrendState();
                st.WindowStartUtc = DateTime.UtcNow;
                st.WindowMaxPrice = lastPrice;
                st.PrevWindowMaxPrice = 0;
                st.PrevEvalUtc = DateTime.MinValue;
                st.ConsecUp = 0;
                st.Initialized = true;
                _trend[code] = st;
                return;
            }
            var now = DateTime.UtcNow;
            // 현재 창 진행 중: 최고가 갱신
            if ((now - st.WindowStartUtc) < TREND_WINDOW)
            {
                if (lastPrice > st.WindowMaxPrice)
                    st.WindowMaxPrice = lastPrice;

                // 👇 최소 주기마다 강제 평가할 수 있도록 체크
                if ((now - st.PrevEvalUtc) >= TREND_WINDOW)
                {
                    // 창 종료와 동일한 평가 실행
                    EvaluateTrend(st, lastPrice, now, code);
                }
                return;
            }
            // 창 종료 → 직전 창 대비 상승 여부 판단
            int tickSize = _kiwoomApi.GetTickSize(code, st.WindowMaxPrice > 0 ? st.WindowMaxPrice : lastPrice);
            if (tickSize <= 0) tickSize = 1;

            bool up =
                (st.PrevWindowMaxPrice > 0) &&
                (st.WindowMaxPrice >= st.PrevWindowMaxPrice + (tickSize * TREND_MIN_TICK_ADVANCE));

            if (st.PrevWindowMaxPrice == 0)
                st.ConsecUp = 0;          // 첫 비교는 기준만 세팅
            else if (up)
                st.ConsecUp++;
            else
                st.ConsecUp = 0;
            // 창 롤오버
            st.PrevWindowMaxPrice = (st.WindowMaxPrice > 0 ? st.WindowMaxPrice : lastPrice);
            st.PrevEvalUtc = now;
            st.WindowStartUtc = now;
            st.WindowMaxPrice = lastPrice;
        }


        private void EvaluateTrend(TrendState st, int lastPrice, DateTime now, string code)
        {
            if (st == null) return;
            int currMax = st.WindowMaxPrice > 0 ? st.WindowMaxPrice : lastPrice;
            int prevMax = st.PrevWindowMaxPrice;
            int tickSize = 1;
            try
            {
                if (_kiwoomApi != null)
                {
                    int basis = currMax > 0 ? currMax : lastPrice;
                    tickSize = _kiwoomApi.GetTickSize(code, basis);
                    if (tickSize <= 0) tickSize = 1;
                }
            }
            catch { tickSize = 1; }
            //    (즉, 1틱 이상 높을 필요 없음. '동가 유지'도 연속상승으로 카운트)
            bool isUp = (prevMax > 0) && (currMax >= prevMax); //금액 동일할 때에도 매수
            // bool isUp = (prevMax > 0) && (currMax >= prevMax + tickSize);   // 매수조건 상승세 체크 구문 1틱 상승시 연속상승으로 카운트 
            // bool isUp = (prevMax > 0) && (currMax >= prevMax + (2 * tickSize));   // 매수 블록킹을 2틱으로 조절

            if (isUp) st.ConsecUp++;
            else st.ConsecUp = 0;
            st.PrevWindowMaxPrice = currMax;
            st.WindowStartUtc = now;
            st.WindowMaxPrice = lastPrice;
            st.PrevEvalUtc = now;
            // 필요하면 여기에서 연속상승 N회 시 신호 콜백 호출(주문은 별도 레이어에서)
        }


        #endregion ===== 연속 윈도우 기반 상승 추세 감지 상태 END =====




        
        #region ===== 보유/미체결 체크(계산 구문) START =====

        // (도우미) 종목 보유/미체결 간단 체크 — 기존 구조 최대한 활용
        private bool HasOpenOrPending(string code)
        {
            // 보유 수량 체크
            Position p;
            if (_positions != null && _positions.TryGetValue(code, out p) && p != null && p.Qty > 0) return true;

            // 미체결(대략) 체크: 내부 펜딩 딕셔너리 존재 시 활용
            try
            {
                // _pendingByOrdNo : { ordNo → PendingOrder } 구조 가정
                // PendingOrder에 Code 필드가 있다고 가정하고 순회(성능 영향 미미한 소량)
                foreach (var kv in _pendingByOrdNo)
                {
                    if (kv.Value != null && string.Equals(kv.Value.Code, code, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch { /* 구조가 없으면 무시 */
    }


            return false;
        }

        // (도우미) EMA per-tick 업데이트
        private double UpdateEmaPerTick(string code, long tickValue, int baselineTicks)
        {
            double prev = 0;
            _emaPerTick.TryGetValue(code, out prev);
            var alpha = 2.0 / (baselineTicks + 1.0);
            var ema = (alpha * tickValue) + (1.0 - alpha) * (prev <= 0 ? (double)tickValue : prev);
            _emaPerTick[code] = ema;
            return ema;
        }

        // (도우미) 윈도우 합 업데이트(O(1))
        private long UpdateWindow(string code, long tickValue, int windowTicks)
        {
            Queue<long> q;
            if (!_winVal.TryGetValue(code, out q) || q == null)
            {
                q = new Queue<long>(windowTicks + 4);
                _winVal[code] = q;
                _winSum[code] = 0;
            }
            long sum = _winSum[code];
            q.Enqueue(tickValue);
            sum += tickValue;
            while (q.Count > windowTicks)
            {
                sum -= q.Dequeue();
            }
            _winSum[code] = sum;
            return sum;
        }

        #endregion ===== 보유/미체결 체크(계산 구문) END =====




        // === 주문 라우팅 ===
        private void TryPlaceBuy(string code, int price)
        {
            // 코드 정규화(딕셔너리 키 일관성)
            code = Normalize(code);

            // DEEP 상태 조회 후 ‘이미 샀음’ 가드
            DeepState ds;
            if (_deep.TryGetValue(code, out ds) && ds != null && ds.Bought)
            {
                try { TradingEvents.RaisePending("[BLOCK] 이미 매수 진행됨(중복 차단): " + code + " @ " + price); } catch { }
                return;
            }

            // --- 연속 상승 추세 조건 미충족 시 매수 금지 ---
            if (!IsTrendUp(code))
            {
                try { TradingEvents.RaisePending($"[BLOCK] TrendUp 미충족 → BUY 차단: {code} @ {price}"); } catch { }

                return;
            }

            if (_lossBlacklist.Contains(code)) return;

            // === VI 공통 게이트(주문 직전 단일화) ===
            // estTp는 수량이 아직 미정이므로 1주 기준으로 근사(호가·수수료·세금 반영 함수 사용)
            int estTp = CalcNetTakeProfitTarget(code, price, 1);
            string viWhy;
            if (IsViBuyBlocked(code, price, estTp, out viWhy))
            {
                try { TradingEvents.RaisePending($"[BLOCK][VI] {code} {viWhy} estBuy={price} estTP={estTp}"); } catch { }
                return;
            }

            int qty = CalcBuyQty(code, price);
            if (qty <= 0) return;

            var ok = SendLimitBuy(code, qty, price);
            if (ok)
            {
                if (ds == null)
                {
                    ds = new DeepState { PromotedAtUtc = DateTime.UtcNow, PromoteBestBid = price, ViBasePrice = price, Bought = true, Initialized = true };
                    _deep[code] = ds;
                }
                else ds.Bought = true;

                try { TradingEvents.RaisePending($"[ORDER] BUY(모멘텀) {code} {qty}@{price} (중복매수 잠금)"); } catch { }
            }


            // ★ V손절 블랙리스트 차단
            if (_lossBlacklist.Contains(code))  // 당일 손절 종목 재매수 금지
                return;

            // 3) 당일 재매수 가드: '직전 매수가 - N틱' 이하 가격이면 재매수 차단
            Position p;
            if (_positions.TryGetValue(code, out p) && p != null)
            {
                try { MaybeResetDaily(p); } catch { /* 날짜 리셋 헬퍼 없으면 무시 */ }

                if (p.LastBuyPrice > 0 && IsSameKstDay(p.LastBuyYmd, GetKstYmd()))
                {
                    int tick = get_hoga_unit_price(p.LastBuyPrice);
                    if (tick <= 0) tick = 1;

                    int guardTicks = StrategyParams.RebuyGuardDownTicks; // 기본 8 (7로 바꾸려면 StrategyParams에서 변경)
                    int guardFloor = p.LastBuyPrice - (guardTicks * tick); // 직전 매수가 - N틱

                    if (price <= guardFloor)
                    {
                        try
                        {
                            TradingEvents.RaisePending(
                                "[BLOCK][REBUY-GUARD] " + code +
                                " 재매수가=" + price +
                                " ≤ 가드바닥=" + guardFloor +
                                " (직전매수가=" + p.LastBuyPrice + ", 틱=" + tick + ", 가드=" + guardTicks + "틱)"
                            );
                        }
                        catch { }
                        return; // 당일 재매수 차단
                    }
                }
            }


            // [정책] 수량 산정/예산/중복매수 방지 등은 기존 상태/포트폴리오 관리 사용
            qty = CalcBuyQty(code, price);
            if (qty <= 0) return;

            // 실제 주문 (기존 주문 포트 사용)
            ok = SendLimitBuy(code, qty, price);

            // [추가] 발주 성공 시, 즉시 ‘이미 샀음’ 플래그 ON (모멘텀 중복 신호 방지)
            if (ok)
            {
                if (ds == null)
                {
                    ds = new DeepState
                    {
                        PromotedAtUtc = DateTime.UtcNow,
                        PromoteBestBid = price,
                        ViBasePrice = price,
                        Bought = true,
                        Initialized = true
                    };
                    _deep[code] = ds;
                }
                else
                {
                    ds.Bought = true;
                }

                try { TradingEvents.RaisePending($"[ORDER] BUY(모멘텀) {code} {qty}@{price} (중복매수 잠금)"); } catch { }
            }
        }



        private void TryPlaceSell(string code, int price)
        {
            int qty = GetSellableQty(code);
            if (qty <= 0) return;

            var ok = SendLimitSell(code, qty, price);
            /*
            if (ok)
            {
                TradingEvents.RaisePending($"sell {code}  수량:{qty}  요청가:{price} (매도 요청)");
            }
            */
        }

        // === 실제 주문 전송부(프로젝트의 기존 포트를 반드시 활용) ===
        private bool SendLimitBuy(string code, int qty, int price)
        {
            try
            {
                if (_kiwoomApi == null) return false;
                return _kiwoomApi.SendOrderBuy("STRAT_LIMIT_BUY", code, qty, price);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ORD][ERR] BUY {code} {qty}@{price} ex={ex.Message}");
                return false;
            }
        }

        private bool SendLimitSell(string code, int qty, int price)
        {
            try
            {
                if (_kiwoomApi == null) return false;
                return _kiwoomApi.SendOrderSell("STRAT_LIMIT_SELL", code, qty, price);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ORD][ERR] SELL {code} {qty}@{price} ex={ex.Message}");
                return false;
            }
        }

        private int CalcBuyQty(string code, int price)
        {
            // 예산/리스크 기반 수량 산정(기존 정책 연결). 자리표시자:
            return 1;
        }

        // 보유 수량 조회(자리표시자: 포지션 테이블 참조 시 우선 사용)
        private int GetSellableQty(string code)
        {
            Position p;
            if (_positions.TryGetValue(code, out p) && p != null) return p.Qty > 0 ? p.Qty : 1;
            return 1;
        }

        // === 체결/미체결 콜백: 기존 OnReceiveChejanData 등에서 아래 헬퍼를 호출만 추가 ===
        public void OnOrderAccepted(string code, string ordNo, string side, int qty, int price)
        {
            if (string.IsNullOrWhiteSpace(ordNo) || string.IsNullOrWhiteSpace(code)) return;

            var strat = StrategyParams.StrategyId;
            var runId = StrategyParams.RunId;


            TradingEvents.RaisePending(
                $"{side} 대기: {code} {qty}@{price} (ord:{ordNo}) — Strategy={strat}, RUN_ID={runId}"
            );

            if (!string.IsNullOrWhiteSpace(ordNo))
            {
                lock (_lockPending)
                {
                    var norm = Normalize(code);

                    // ▶ 한 번에 처리(존재 확인 + 제거) — 기능 동일, 조회 1회로 줄임
                    bool isStop = _pendingStopLoss.Remove(norm);

                    _pendingByOrdNo[ordNo] = new PendingOrder
                    {
                        OrdNo = ordNo,
                        Code = norm,
                        Qty = qty,
                        AcceptedUtc = DateTime.UtcNow,
                        Side = (side ?? "").ToUpperInvariant(),
                        CancelRequested = false,
                        RetryCount = 0,
                        IsStopLoss = isStop        // ← 손절만 true
                    };

                    // 로그(접수)
                    TradingEvents.RaisePending(
                    "[ORD-ACPT] side=" + side + " code=" + norm + " ord=" + ordNo + " qty=" + qty + " t=" + DateTime.UtcNow.ToString("HH:mm:ss.fff")
                    );
                }
            }

        }

        // 주문 요청 실패 갱신요청구문
        public void OnOrderFilled(string code, string side, int qty, int price)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            if (qty <= 0 || price <= 0) return;

            // Canonical 태그/런아이디
            var tag = StrategyParams.CanonicalTag;     // 예: OpeningBurst@v2
            var run = StrategyParams.RunId;            // 예: 20250903-01
            var sideNorm = (side ?? "").ToUpperInvariant();

            // 체결 요약 UI 로그 (기존 스타일 유지)
            Adapters.TradingEvents.RaiseTradeInfo(
                $"[FILL] side={sideNorm} code={code} qty={qty} px={price} — Strategy={tag}, RUN_ID={run}"
            );

            // 포지션 갱신(손절 트리거용)
            code = CodeUtil.NormalizeCode(code);

            if (!string.IsNullOrWhiteSpace(side) && qty > 0)
            {
                Position p;
                if (!_positions.TryGetValue(code, out p) || p == null)
                {
                    p = new Position { Qty = 0, AvgPrice = 0 };
                    _positions[code] = p;
                }

                if (string.Equals(side, "BUY", StringComparison.OrdinalIgnoreCase))
                {
                    // 평균단가 갱신 외에 마지막 매수가도 갱신
                    p.LastBuyPrice = price > 0 ? price : p.AvgPrice;    // 마지막 매수가 갱신
                    p.LastBuyYmd = GetKstYmd();  // 오늘 날짜로 마킹


                    int newQty = p.Qty + qty;
                    if (newQty > 0)
                    {
                        int newAvg = (int)Math.Round(((long)p.AvgPrice * p.Qty + (long)price * qty) / (double)newQty);
                        p.Qty = newQty;
                        p.AvgPrice = newAvg;
                    }
                }
                else if (string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase))
                {
                    p.Qty -= qty;
                    if (p.Qty <= 0)
                    {
                        p.Qty = 0;
                        p.AvgPrice = 0;
                    }
                }
            }

            TradingEvents.RaiseTradeInfo($"체결 완료: {side} {code} {qty}@{price}");

            // ===== 여기부터 추가: 체결 로그 + 미체결 펜딩 제거 로그 =====
            TradingEvents.RaisePending("[FILL] side=" + side + " code=" + code + " qty=" + qty + " px=" + price + " t=" + DateTime.UtcNow.ToString("HH:mm:ss.fff"));


            // 이 종목(code)으로 걸려 있던 "미체결 주문"(pending) 제거
            var removed = new List<string>();

            lock (_lockPending)
            {
                foreach (var kv in _pendingByOrdNo.ToList())
                {
                    var po = kv.Value;
                    if (po != null && string.Equals(po.Code, code, StringComparison.OrdinalIgnoreCase))
                    {
                        removed.Add(kv.Key);            // 주문번호 수집
                        _pendingByOrdNo.Remove(kv.Key); // 펜딩에서 제거
                    }
                }
            }
            if (removed.Count > 0)
            {
                var msg = "[PENDING-CLEAR] by FILL code=" + code + " removed=" + string.Join(",", removed);
                TradingEvents.RaisePending(msg);
                Console.WriteLine(msg);
            }
            try
            {
                // ★ 추가: 세금/수수료 보정 포함 PnL 집계 (중앙화)
                PnLMeter.RecordFill(
                    code,
                    sideNorm,                              // "BUY" / "SELL"
                    qty,
                    price,
                    StrategyParams.StrategyId,
                    StrategyParams.StrategyVersion,
                    StrategyParams.RunId,
                    StrategyParams.CanonicalTag
                );
            }
            catch
            {
                // 집계 실패해도 트레이딩 흐름은 계속
            }
        }


        //
        public void OnOrderPartiallyFilled(string code, string side, int filled, int remain, int price)
        {
            // 왜: 부분체결 시에도 보유 수량/평단을 즉시 반영하고 진행 상황을 UI(textBox4)에 알려주기 위함
            code = Normalize(code);

            if (!string.IsNullOrWhiteSpace(side) && filled > 0 && price > 0)
            {
                Position p;
                if (!_positions.TryGetValue(code, out p) || p == null)
                {
                    p = new Position { Qty = 0, AvgPrice = 0 };
                    _positions[code] = p;
                }

                if (string.Equals(side, "BUY", StringComparison.OrdinalIgnoreCase))
                {
                    // 가중 평균단가 갱신(부분 체결분만 반영)
                    int newQty = p.Qty + filled;
                    if (newQty > 0)
                    {
                        int newAvg = (int)Math.Round(((long)p.AvgPrice * p.Qty + (long)price * filled) / (double)newQty);
                        p.Qty = newQty;
                        p.AvgPrice = newAvg;
                    }
                }
                else if (string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase))
                {
                    p.Qty -= filled;
                    if (p.Qty <= 0)
                    {
                        p.Qty = 0;
                        p.AvgPrice = 0;
                    }
                }
            }

            // 진행 상황 로그: textBox4에 표시됨(TradingEvents.UiPending 구독)
            int total = filled + Math.Max(remain, 0);
            TradingEvents.RaisePending($"부분체결: {side} {code} {filled}/{(total > 0 ? total : filled)}@{price} (미체결:{Math.Max(remain, 0)})");
        }



        // === 미체결 주문 자동 취소 타이머 콜백 ===
        private void CheckPendingOrders(object state)
        {
            try
            {
                var now = DateTime.UtcNow;
                List<string> toCancel = null;

                lock (_lockPending)
                {
                    foreach (var kv in _pendingByOrdNo)
                    {
                        var p = kv.Value;
                        if (p == null) continue;
                        if (p.CancelRequested) continue;

                        var age = (now - p.AcceptedUtc).TotalSeconds;

                        // [추가] BUY 미체결 4초 경과 → 취소 대상으로 등록
                        if (string.Equals(p.Side, "BUY", StringComparison.OrdinalIgnoreCase)
                            && age >= CANCEL_BUY_TIMEOUT_SEC)
                        {
                            if (toCancel == null) toCancel = new List<string>(8);
                            toCancel.Add(kv.Key);
                        }

                        // 손절 취소 트리거는 p.IsStopLoss를 반드시 포함해야 합니다.
                        if (string.Equals(p.Side, "SELL", StringComparison.OrdinalIgnoreCase)
                            && p.IsStopLoss                         // [수정 필요] 손절만!
                            && age >= 1.0)                          // CANCEL_SELL_RETRY_TIMEOUT_SEC(=1s)
                        {
                            if (toCancel == null) toCancel = new List<string>(8);
                            toCancel.Add(kv.Key);                    // 원주문번호를 취소→재주문 큐에 적재
                        }

                        /// === SELL(익절) StepDown 재시도 ===
                        if (string.Equals(p.Side, "SELL", StringComparison.OrdinalIgnoreCase) && !p.IsStopLoss)
                        {
                            // 3초 주기 체크(타이머 주기와 일치) - AcceptedUtc 기준 경과
                            double ageMs = (DateTime.UtcNow - p.AcceptedUtc).TotalMilliseconds;

                            // 1) 최종(+1틱) 단계 진입 후 '대기 중'이면 재주문 금지, 대기 만료 시 시장가 청산
                           
                            DateTime t0;
                            if (_tpFinalStartUtcByCode.TryGetValue(p.Code, out t0))
                            {
                                int needMs;
                                if (!_tpFinalWaitMsByCode.TryGetValue(p.Code, out needMs) || needMs <= 0)
                                    needMs = StrategyParams.FinalWaitMs; // 안전 기본값 67초

                                _tpFinalStartUtcByCode.Remove(p.Code);
                                _tpFinalWaitMsByCode.Remove(p.Code);
                                TradingEvents.RaisePending("[FINAL-REMOVED] " + p.Code + " final-wait path purged");
                            }

                            // 2) +3 → +2 → +1틱까지만 단계적 하향 (p.RetryCount: 0 → 1 → 2)
                            //    - 기존 10회에서 2회로 제한: 0(시작=+3틱), 1(재시도=+2틱), 2(재시도=+1틱=마지막)
                            if (ageMs >= StrategyParams.ReorderDelayMs && p.RetryCount < 2)
                            {
                                int tickSize = get_hoga_unit_price(p.LastPrice);
                                if (tickSize <= 0) tickSize = 1;

                                int newPx = p.LastPrice - tickSize;   // 직전 주문가에서 1틱 내림
                                if (newPx > 0)
                                {
                                    bool okSell = (_kiwoomApi != null) && _kiwoomApi.SendOrderSellStepDown("STRAT_TP_STEP_RETRY", p.Code, p.Qty, newPx, tickSize);
                                    if (okSell)
                                    {
                                        p.AcceptedUtc = DateTime.UtcNow; // 재주문 타임스탬프 갱신
                                        p.LastPrice = newPx;
                                        p.RetryCount++; // 0→1(+2틱), 1→2(+1틱)

                                        TradingEvents.RaisePending("[RETRY-STEP] SELL " + p.Code + " qty=" + p.Qty + " @" + newPx);

                                        // 2회째 재주문(= +1틱)에 진입한 순간부터 최종 대기 카운트 시작
                                        if (p.RetryCount == 2) // ★ +1틱 진입
                                        {

                                        }
                                    }
                                }
                            }
                        }


                        // 2) BUY: 원주문 취소 후 추격매수로 전환
                        if (string.Equals(p.Side, "BUY", StringComparison.OrdinalIgnoreCase))
                        {
                            var okCancel = _kiwoomApi.SendOrderCancel("STRAT_TIMEOUT_CANCEL", p.OrdNo, p.Code, 0);
                            if (!okCancel)
                            {
                                lock (_lockPending) { p.CancelRequested = false; }
                                return;
                            }

                            TradingEvents.RaisePending("[CANCEL-BUY] code=" + p.Code + " ord=" + p.OrdNo + " (timeout " + CANCEL_BUY_TIMEOUT_SEC + "s)");

                            lock (_lockPending)
                            {
                                _pendingByOrdNo.Remove(p.OrdNo);  // 펜딩 정리
                                p.CancelRequested = false;
                            }

                            return; // 반드시 종료 (이후 SELL 루틴에서 처리됨)
                        }

                    }
                }


                // 손절 재시도와 BUY 취소를 담당하는 핵심 경계 로직
                if (toCancel == null || toCancel.Count == 0) return;

                foreach (var ordNo in toCancel)
                {
                    PendingOrder p;
                    lock (_lockPending) { _pendingByOrdNo.TryGetValue(ordNo, out p); }
                    if (p == null || _kiwoomApi == null) continue;

                    // 레이트 리밋: 초과 시 다음 라운드에서 재시도
                    if (!KiwoomAutoTRD.Services.OrderRateLimiter.TryAcquire())
                    {
                        lock (_lockPending) { p.CancelRequested = false; }
                        continue;
                    }

                    // ★ UI 스레드로 마샬링해서 취소 호출
                    _kiwoomApi.RunOnUi(() =>
                    {
                        try
                        {
                            // 1) 원주문 취소
                            var okCancel = _kiwoomApi.SendOrderCancel("STRAT_TIMEOUT_CANCEL", p.OrdNo, p.Code, 0);
                            if (!okCancel)
                            {
                                lock (_lockPending) { p.CancelRequested = false; } // 다음 라운드 재시도
                                return;
                            }

                            // 2) BUY: 취소 후 시장가 추격매수 → 순이익 기준 +3틱 익절 시퀀스 시작
                            if (string.Equals(p.Side, "BUY", StringComparison.OrdinalIgnoreCase))
                            {
                                int qtyToBuy = p.Qty;
                                if (qtyToBuy <= 0) { lock (_lockPending) { p.CancelRequested = false; } return; }

                                // (1) 시장가 매수
                                bool okBuy = _kiwoomApi.SendOrderBuyMarket("STRAT_BUY_CHASE_MKT", p.Code, qtyToBuy);
                                if (!okBuy)
                                {
                                    // 실패 시 다음 라운드 재시도 여지 남김
                                    lock (_lockPending) { p.CancelRequested = false; }
                                    return;
                                }

                                TradingEvents.RaisePending("[CHASE-MKT] BUY " + p.Code + " qty=" + qtyToBuy + " @MKT");

                                // (2) 기대 매수단가(근사) → 정확익절 목표가 계산
                                int approxBuy = (p.LastPrice > 0) ? p.LastPrice : 0;
                                int netTarget = CalcNetTakeProfitTarget(p.Code, approxBuy, qtyToBuy);
                                if (netTarget <= 0) netTarget = approxBuy;

                                // (3) 호가단위/틱 계산
                                int tick = get_hoga_unit_price((approxBuy > 0) ? approxBuy : netTarget);
                                if (tick <= 0) tick = 1;

                                int startPx = netTarget + (3 * tick); // 순이익 기준 +3틱부터 시작

                                // (4) 지정가 익절 1차 발주 (+3틱)
                                bool okSell = _kiwoomApi.SendOrderSellStepDown("STRAT_TP_FROM_BUY", p.Code, qtyToBuy, startPx, tick);
                                if (okSell)
                                {
                                    TradingEvents.RaisePending("[TP-START] SELL " + p.Code + " qty=" + qtyToBuy + " @" + startPx + " (from BUY chase)");
                                    lock (_lockPending) { _chaseBuyCodes.Add(p.Code); } // 최종대기 단축용 플래그
                                }

                                // 상태 초기화(다음 라운드 간섭 방지)
                                lock (_lockPending)
                                {
                                    p.RetryCount = 0;              // 익절 시퀀스는 새 주문에서 다시 시작
                                    p.CancelRequested = false;     // 취소 처리 완료
                                    p.AcceptedUtc = DateTime.UtcNow;
                                    p.LastPrice = startPx;         // StepDown 기준가
                                    p.Side = "SELL";               // 이후부터는 익절 StepDown 경로
                                    p.IsStopLoss = false;          // ← 익절/일반 청산
                                }
                                return; // BUY 처리는 종료, 이후 SELL(익절) 로직은 상단 분기에서 관리
                            }

                            // 3) SELL(손절만): 시장가 재발주(체결될 때까지 반복)
                            if (string.Equals(p.Side, "SELL", StringComparison.OrdinalIgnoreCase) && p.IsStopLoss)
                            {
                                int qtyToSell = 0;
                                Position pos;
                                if (_positions.TryGetValue(p.Code, out pos) && pos != null && pos.Qty > 0)
                                    qtyToSell = pos.Qty;

                                if (qtyToSell > 0)
                                {
                                    // 접수 시 IsStopLoss 승계용 태깅 (OnOrderAccepted에서 IsStopLoss=true 반영)
                                    _pendingStopLoss.Add(p.Code);
                                    bool okSellMkt = _kiwoomApi.SendOrderSellMarket("STRAT_STOP_RETRY_MKT", p.Code, qtyToSell);
                                    
                                    TradingEvents.RaisePending($"[RESELL-MKT][STOP] code={p.Code} qty={qtyToSell} ok={okSellMkt} t={DateTime.UtcNow:HH:mm:ss.fff}");

                                }
                                else
                                {
                                    // 포지션 0 → 루프 종료
                                    TradingEvents.RaisePending(
                                        $@"[STOP-DONE] StrategyTag={StrategyParams.StrategyId}@v{StrategyParams.StrategyVersion} RUN_ID={StrategyParams.RunId} Code={p.Code} Pos=0 Reason=StopLossCompleted t={DateTime.UtcNow:HH:mm:ss.fff} 손절 완료"
                                    );

                                }

                                // 재시도 상태 갱신(손절은 회수 제한 없음)
                                lock (_lockPending)
                                {
                                    p.RetryCount++;                // 통계용
                                    p.CancelRequested = false;
                                    p.AcceptedUtc = DateTime.UtcNow; // 다음 라운드 기준시각
                                }
                            }
                        }
                        catch
                        {
                            lock (_lockPending) { p.CancelRequested = false; }
                        }
                    });
                }
            }
            catch
            {
                // 타이머 예외 격리
            }
        }


        // 리소스 관리 구문
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this); // 파이널라이저(있다면) 생략 요청
        }

        // 필요 시 파이널라이저를 둘 수도 있지만 현재는 비필수
        // ~TradingManager() { Dispose(false); }

        // WHY: 표준 Dispose 패턴 - 관리형/비관리형 해제를 분기 처리
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                // 관리형 리소스만 해제 (disposing == true 일 때)
                if (disposing)
                {
                    try { if (_cts != null) { _cts.Cancel(); _cts.Dispose(); } } catch { }
                    try { if (_kiwoomApi != null) _kiwoomApi.TradeTick -= OnTradeTick; } catch { }
                    try { if (_mom != null) _mom.Dispose(); } catch { }
                    try { if (_cancelTimer != null) _cancelTimer.Dispose(); } catch { }

                    // 딕셔너리/컬렉션은 GC가 정리하므로 명시 해제 불필요. 단, 상태 초기화는 안전상 할 수 있음.
                    try
                    {
                        lock (_lockPending) { _pendingByOrdNo.Clear(); }
                        _aboveSince.Clear();
                        _deep.Clear();
                        _viTriggered.Clear();
                        _lossBlacklist.Clear();
                        _positions.Clear();
                    }
                    catch { }
                }

                // 비관리 리소스 해제 자리(현재 없음)
            }
            catch
            {
                // 예외 격리
            }
        }


        // ---------- [추가] 호가 단위 (KRX 표준) ----------
        private int get_hoga_unit_price(int price)
        {
            if (price < 1000) return 1;
            if (price < 5000) return 5;
            if (price < 10000) return 10;
            if (price < 50000) return 50;
            if (price < 100000) return 100;
            return 500;
        }

        // === [추가] 정확익절(Net) 계산 헬퍼 ===
        private static int CeilToTick(int price, int tickSize)
        {
            if (tickSize <= 0) tickSize = 1;
            int q = price / tickSize;
            if (price % tickSize != 0) q++;
            return q * tickSize; // tick 단위 올림
        }

        // 평균단가/목표 순익(주당) 및 세금/수수료를 반영한 '최소 필요 매도가' 계산
        private int CalcNetTakeProfitTarget(string code, int avgPrice, int qty)
        {
            if (avgPrice <= 0 || qty <= 0) return 0;

            // 파라미터 로드(오차 축소를 위해 decimal 사용)
            decimal rBuy = (decimal)StrategyParams.BuyFeeRate;  // 매수 수수료 ) 0.015%
            decimal rSell = (decimal)StrategyParams.SellFeeRate;    // 매도 수수료 ) 0.015%
            decimal rTax = (decimal)StrategyParams.SellTaxRate; //세금    
            decimal tgtWon = (decimal)StrategyParams.TakeProfitNetPerShareWon;  // 1주당 순이익 목표(원). 0이면 미사용
            decimal tgtPct = (decimal)StrategyParams.TakeProfitNetPct; // 평균단가 대비 순이익 % (예: 0.003=0.3%). 0이면 미사용

        // 순이익 목표(주당): 금액/퍼센트 중 큰 값을 사용
        decimal perShareTargetWon = tgtWon;
            if (tgtPct > 0m)
            {
                var pctWon = Math.Ceiling((decimal)avgPrice * tgtPct);
                if (pctWon > perShareTargetWon) perShareTargetWon = pctWon;
            }
            if (perShareTargetWon <= 0m) return 0; // 목표 미설정이면 사용 안 함

            // 공식:
            // 순익/주 = Sell*(1 - (rSell+rTax)) - Avg*(1 + rBuy) >= perShareTargetWon
            // → Sell >= (Avg*(1+rBuy) + perShareTargetWon) / (1 - (rSell+rTax))
            decimal denom = 1m - (rSell + rTax);
            if (denom <= 0m) denom = 0.999m; // 방어

            decimal rawTarget = ((decimal)avgPrice * (1m + rBuy) + perShareTargetWon) / denom;
            int need = (int)Math.Ceiling(rawTarget); // 최소 정수 가격

            // tick 올림 반영
            int tick = get_hoga_unit_price(need);
            int rounded = CeilToTick(need, tick);

            return rounded;
        }


        private static string Normalize(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return code;
            return (code[0] == 'A' || code[0] == 'a') ? code.Substring(1) : code;
        }



        /// 종목 보유 수량 (없으면 0)
        public int PositionQty(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return 0;

            Position p;
            if (_positions != null && _positions.TryGetValue(code, out p) && p != null)
                return p.Qty;

            return 0;
        }

        /// 미체결 주문 존재 여부
        public bool HasOpenOrders(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            lock (_lockPending)
            {
                foreach (var kv in _pendingByOrdNo)
                {
                    var po = kv.Value;
                    if (po != null && string.Equals(po.Code, code, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            return false;
        }


    }
}
