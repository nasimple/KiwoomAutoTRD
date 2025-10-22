//version 250831
using AxKHOpenAPILib;
using KiwoomAutoTRD.Adapters;
using KiwoomAutoTRD.Common;
using KiwoomAutoTRD.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static KiwoomAutoTRD.DBManager;


namespace KiwoomAutoTRD
{
    public class KiwoomApi
    {
        private AxKHOpenAPI axKHOpenAPI1;
        private TradingManager _tradingManager;

        public string AccountNumber { get; private set; }
        public Dictionary<string, StockInfo> portfolio = new Dictionary<string, StockInfo>();


        // ---- 상태 캐시: 딥/라이트 구분 + 마지막 틱 수신 시각 ----
        private readonly HashSet<string> _deepCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastTickUtc = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        // 핀(미체결/보유 시 DEEP 고정)
        private readonly HashSet<string> _pinDeep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 스윕/폴백 타이머
        private System.Timers.Timer _fallbackTimer; // 폴백 TR
        private System.Timers.Timer _sweepTimer; // ★ Deep 강등 스윕 타이머

        private readonly TimeSpan _deepIdle = TimeSpan.FromSeconds(5); // ★ 5초 무활동 시 강등  // 확인하기


        // ---- 조건검색 상수 ----
        public const string CONDITION_NAME = "RiskFilterStock";   // 조건식 이름
        // 조건식 재확인(재실행)용 상태
        private int _condIndex = -1;
        private string _condScreen = null;
        private System.Timers.Timer _condTimer;



        public KiwoomApi(AxKHOpenAPI api, TradingManager manager)
        {
            axKHOpenAPI1 = api;
            _tradingManager = manager;

            try { if ( _tradingManager != null) _tradingManager.BindApi(this); } catch { /* 안전 무시 */ }
        }

        // ---- Smart Universe 상수 ----

        // StrategyParams에서 중앙 관리되는 FID 문자열 사용
        private static readonly string LIGHT_FIDS = KiwoomAutoTRD.Services.StrategyParams.Realtime.LightFids;
        private static readonly string DEEP_FIDS = KiwoomAutoTRD.Services.StrategyParams.Realtime.DeepFids;


        // [필드: 최근 체결강도 저장]
        private readonly Dictionary<string, double> _lastTradePower = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        
        // ---- 코드→화면번호/심층여부/최근활동 ----
        private readonly HashSet<string> deepCodes = new HashSet<string>();
        private readonly Dictionary<string, DateTime> lastActive = new Dictionary<string, DateTime>();


        private bool _eventsAttached; // AttachEvents()가 여러 번 호출되더라도 이벤트 핸들러가 중복 등록되지 않도록 제어한다. true=구독 완료, false=미구독 상태


        // ---- DEEP 상태/틱 알림 이벤트 (Form1에서 textBox2 갱신용)
        public event Action<string> DeepPromoted;  // code
        public event Action<string> DeepDemoted;   // code
        public event Action<string, double, int, int> DeepTick; // code, chgRt, vol, bestBid

        // === 실시간 틱 이벤트 (TradingManager로 전달) ===
        public event Action<string, int, int, DateTime> TradeTick; // (code, price, qty, tsUtc)

        // (code, last, bestBid, bestAsk, bidQty, askQty, chgRt)
        public event Action<string, int, int, int, int, int, double> L1Quote;


        // ---- VI 신호 이벤트/카운터 (UI/로그 점검용)
        public event Action<string, bool, string, string, string> ViSignal;
        // args: code, isTriggered(발동= true/해제=false), viPrice, viRate, viCount

        private int _viTriggeredCount; // 발동 누계
        private int _viReleasedCount;  // 해제 누계

        public (int triggered, int released) GetViStats() => (_viTriggeredCount, _viReleasedCount);


        // 코드 정규화: 'A000140' → '000140'
        private static string NormalizeCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return code;
            return (code[0] == 'A' || code[0] == 'a') ? code.Substring(1) : code;
        }


        #region --- 로그인 및 이벤트 구독 ---
        // 로그인 메서드
        public void Login()
        {
            axKHOpenAPI1.CommConnect();
        }



        // 로그인 이벤트 핸들러
        private void OnEventConnect(object sender, _DKHOpenAPIEvents_OnEventConnectEvent e)
        {
            if (e.nErrCode == 0)
            {
                AccountNumber = axKHOpenAPI1.GetLoginInfo("ACCLIST").Split(';')[0];

                // ✅ 널 가드 추가
                var mgr = _tradingManager;
               if (mgr != null)
        {
            try { mgr.OnLoginSuccess(AccountNumber); } catch { /* 안전 무시 */ }
        }

        // ★ 여기서 반드시 조건식 로드/전개 트리거
        if (KiwoomAutoTRD.Services.StrategyParams.Realtime.EnableKosdaqLightAll)
        {
            RegisterKosdaqLightAll(); // 폴백/부하테스트용
        }
        else
        {
            LoadAndRunCondition();    // 정상 운영 경로
        }
            }
        }
        #endregion  --- 로그인 및 이벤트 구독 ---


        #region --- 이벤트 구독 및 해제 ---
        //이벤트  구독 메서드
        public void AttachEvents()
        {
            if (_eventsAttached) return; // ★ 중복 구독 방지

            axKHOpenAPI1.OnReceiveRealData += OnReceiveRealData;     // 실시간 데이터 구독
            axKHOpenAPI1.OnReceiveTrData += OnReceiveTrData;         // TR
            axKHOpenAPI1.OnReceiveChejanData += OnReceiveChejanData; // 체잔
            axKHOpenAPI1.OnEventConnect += OnEventConnect;

            // --- ★여기에 조건식 3종 이벤트 구독을 추가한다(같은 가드 안) ---
            axKHOpenAPI1.OnReceiveConditionVer += axKHOpenAPI1_OnReceiveConditionVer;   // 조건식 목록 로드 완료
            axKHOpenAPI1.OnReceiveTrCondition += axKHOpenAPI1_OnReceiveTrCondition;     // 조건식 조회 결과(일괄)
            axKHOpenAPI1.OnReceiveRealCondition += axKHOpenAPI1_OnReceiveRealCondition; // 조건식 실시간 편입/이탈

            _eventsAttached = true;

            // ★ Deep 강등 스윕 타이머 설정
            if (_sweepTimer == null)
            {
                _sweepTimer = new System.Timers.Timer(5000); // 5초 간격 강등 조건 확인하기
                _sweepTimer.AutoReset = true;
                _sweepTimer.Elapsed += (s, a) => SweepDeepIdle();
                _sweepTimer.Start();
            }
        }

        // 주식 계좌번호 불러오기
        public void SetAccountNumber(string accNo)
        {
            if (string.IsNullOrWhiteSpace(accNo)) throw new ArgumentNullException(nameof(accNo));
            AccountNumber = accNo;
        }


        // ★ 예수금상세현황(OPW00001) 요청
        public void RequestDepositDetail()
        {
            if (string.IsNullOrWhiteSpace(AccountNumber)) return;

            try
            {
                // 키움 가이드: OPW00001
                // 입력: 계좌번호 / 비밀번호 / 비밀번호입력매체구분(00:키보드) / 조회구분(2:추정조회)
                axKHOpenAPI1.SetInputValue("계좌번호", AccountNumber);
                axKHOpenAPI1.SetInputValue("비밀번호", ""); // 계좌비번 필요 시 설정
                axKHOpenAPI1.SetInputValue("비밀번호입력매체구분", "00");
                axKHOpenAPI1.SetInputValue("조회구분", "2");

                // 화면번호는 TR 전용 범위 사용 권장
                axKHOpenAPI1.CommRqData("예수금상세현황요청", "OPW00001", 0, "2000");
            }
            catch
            {
                // 무시 가능
            }
        }


        // Deep 강등 스윕 처리기
        private void SweepDeepIdle()
        {
            try
            {
                var now = DateTime.UtcNow;
                // 사본으로 순회(변경 중 컬렉션 예외 방지)
                var snapshot = deepCodes.ToList();

                foreach (var code in snapshot)
                {
                    if (lastActive.TryGetValue(code, out var ts))
                    {
                        if (now - ts > _deepIdle)
                        {
                            // 오래 조용하면 강등
                            DemoteToLight(code);
                            Console.WriteLine($"[REAL] Auto-demote {code} (idle>{_deepIdle.TotalSeconds:N0}s)");
                        }
                    }
                    else
                    {
                        // 활동 기록이 없는 DEEP 항목도 강등
                        DemoteToLight(code);
                        Console.WriteLine($"[REAL] Auto-demote {code} (no activity ts)");
                    }
                }
            }
            catch { /* 안전 무시 */ }
        }




        // --- 조건식 버전 수신 → 조건 목록 파싱 → 대상 조건 실행
        private void axKHOpenAPI1_OnReceiveConditionVer(object sender, _DKHOpenAPIEvents_OnReceiveConditionVerEvent e)
        {
            try
            {
                // "인덱스^이름;" 포맷 목록
                string raw = axKHOpenAPI1.GetConditionNameList() ?? "";
                if (string.IsNullOrWhiteSpace(raw)) { Console.WriteLine("[COND] 목록 없음"); return; }

                // 목표 조건 찾기
                int idx = -1;
                foreach (var part in raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var arr = part.Split('^');
                    if (arr.Length >= 2)
                    {
                        int i; if (int.TryParse(arr[0], out i))
                        {
                            var name = (arr[1] ?? "").Trim();
                            if (string.Equals(name, CONDITION_NAME, StringComparison.OrdinalIgnoreCase))
                            {
                                idx = i; break;
                            }
                        }
                    }
                }
                if (idx < 0) { Console.WriteLine($"[COND] '{CONDITION_NAME}' 없음"); return; }

                // 조건식 실행(조회+실시간등록: 1)
                // SendCondition(화면번호, 조건식이름, 인덱스, 실시간여부)
                var pool = ScreenManager.Get("condition");
                var slot = pool.ReserveSlot();
                _condIndex = idx;
                _condScreen = slot.Screen;

                axKHOpenAPI1.SendCondition(slot.Screen, CONDITION_NAME, idx, 1);
                Console.WriteLine($"[COND] '{CONDITION_NAME}' 실행 시작(idx={_condIndex}, screen={_condScreen})");

                EnsureConditionRefreshTimer(); // ← 재확인 타이머 가동
            
            }
            catch { /* 무시 */ }
        }

        private void EnsureConditionRefreshTimer()
        {
            try
            {
                if (_condTimer != null) return;
                var sec = Math.Max(60, KiwoomAutoTRD.Services.StrategyParams.Realtime.ConditionRefreshSec);
                _condTimer = new System.Timers.Timer(sec * 1000);
                _condTimer.AutoReset = true;
                _condTimer.Elapsed += (s, a) =>
                {
                    try
                    {
                        if (_condIndex < 0 || string.IsNullOrEmpty(_condScreen)) return;
                        // 주기적 재실행(재확인) → TR 결과 핸들러에서 라이트 풀 ‘교체’
                        axKHOpenAPI1.SendCondition(_condScreen, CONDITION_NAME, _condIndex, 1);
                        UiAppend($"[COND/REFRESH] '{CONDITION_NAME}' 재실행");
                    }
                    catch { /* 무시 */ }
                };
                _condTimer.Start();
            }
            catch { /* 무시 */ }
        }




        // --- 조건식 조회 결과(일괄) 수신 → 라이트 등록
        private void axKHOpenAPI1_OnReceiveTrCondition(object sender, _DKHOpenAPIEvents_OnReceiveTrConditionEvent e)
        {
            try
            {
                var codes = (e.strCodeList ?? "")
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToArray();
                if (codes.Length == 0) return;

                // === [변경 ①] 기존 라이트 풀 전체 초기화 ===
                var poolL = ScreenManager.Get("real_stock");
                poolL.Clear(axKHOpenAPI1);  // ← 추가된 부분

                // === [변경 ②] 조건식 결과만 새로 등록 ===
                poolL.RegisterCodes(axKHOpenAPI1, codes, KiwoomAutoTRD.Services.StrategyParams.Realtime.LightFids);  // ← 수정된 부분

                // === [변경 ③] 로그 개선 (UI 표시 포함) ===
                var cnt = poolL.GetRegisteredCount();
                UiAppend($"[COND/TR][REPLACE] 라이트=조건식 결과로 교체: {codes.Length}개 (현재={cnt})");  // ← Console.WriteLine → UiAppend + 상세 출력
            }
            catch { /* 무시 */ }
        }


        // ... (기존 로그인 후 처리 유지)
        private void AfterLogin()
        {
            if (StrategyParams.Realtime.EnableKosdaqLightAll)
            {
                // KOSDAQ 유니버스 전종목 라이트 등록
                var codes = GetCodesByMarket("10"); // "10"=KOSDAQ
                var poolL = ScreenManager.Get("real_stock");
                foreach (var c in codes) poolL.RegisterCode(axKHOpenAPI1, c, LIGHT_FIDS);
                UiAppend($"[REAL] KOSDAQ 전종목 라이트 등록: {codes.Length}");
            }

            // 스윕 타이머 시작(딥 강등/워치독)
            EnsureSweepTimer();

            // (옵션) 폴백 TR 타이머 시작
            EnsureFallbackTimer();
        }



        // === 조건식 결과(일괄) 등록: LIGHT→DEEP 이동(중복 금지, opt=1 금지) ===
        private void OnConditionResultBatch(string[] codes)
        {
            if (codes == null || codes.Length == 0) return;

            var poolL = ScreenManager.Get("real_stock");
            var poolD = ScreenManager.Get("real_stock_deep");

            foreach (var code in codes)
            {
                // 1) 라이트에 있으면 해제
                string scr;
                if (poolL.TryGetScreen(code, out scr))
                    poolL.UnregisterCode(axKHOpenAPI1, code);

                // 2) 딥에 등록
                if (!poolD.TryGetScreen(code, out scr))
                    poolD.RegisterCode(axKHOpenAPI1, code, DEEP_FIDS);

                _deepCodes.Add(code);
                _lastTickUtc[code] = DateTime.UtcNow;
            }

            UiAppend($"[COND/TR] → DEEP 등록 {codes.Length}개 (라이트 중복 제거)");
        }

        // === 조건식 실시간 편입/이탈 ===
        private void axKHOpenAPI1_OnReceiveRealCondition(object sender, _DKHOpenAPIEvents_OnReceiveRealConditionEvent e)
        {
            try
            {
                var raw = (e.sTrCode ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(raw)) return;

                // [변경 ①] 코드 정규화: 풀/캐시 키 일관성 확보
                var code = NormalizeCode(raw);

                var type = Convert.ToString(e.strType ?? string.Empty); // "I" / "D"
                var poolL = ScreenManager.Get("real_stock");
                var poolD = ScreenManager.Get("real_stock_deep");

                if (string.Equals(type, "I", StringComparison.OrdinalIgnoreCase))
                {
                    // 편입 → 라이트에 추가(중복 안전)
                    poolL.RegisterCode(axKHOpenAPI1, code, KiwoomAutoTRD.Services.StrategyParams.Realtime.LightFids);
                    UiAppend($"[COND/REAL] 편입 → LIGHT {code} (현재={poolL.GetRegisteredCount()})");

                    // 편입 → 딥 승격 (중복 구독 방지: 라이트 해제)
                    if (poolL.TryGetScreen(code, out _))
                        poolL.UnregisterCode(axKHOpenAPI1, code);

                    if (!poolD.TryGetScreen(code, out _))
                        poolD.RegisterCode(axKHOpenAPI1, code, KiwoomAutoTRD.Services.StrategyParams.Realtime.DeepFids); // [변경 ②] 단일 소스

                    _deepCodes.Add(code);
                    _lastTickUtc[code] = DateTime.UtcNow;
                    UiAppend($"[COND/REAL] 편입 → DEEP {code}");
                }
                else if (string.Equals(type, "D", StringComparison.OrdinalIgnoreCase))
                {
                    // 이탈 → 라이트에서 제거
                    poolL.UnregisterCode(axKHOpenAPI1, code);
                    UiAppend($"[COND/REAL] 이탈 → UNREG {code} (현재={poolL.GetRegisteredCount()})");

                    // 이탈 → 핀 규칙 검사 (미체결/보유 시 DEEP 유지)
                    if (IsPinDeep(code))
                    {
                        UiAppend($"[COND/REAL] 이탈 무시(핀 유지) {code}");
                        return;
                    }

                    // 딥 해제
                    if (poolD.TryGetScreen(code, out _))
                        poolD.UnregisterCode(axKHOpenAPI1, code);
                    _deepCodes.Remove(code);

                    // 정책: 전종목 라이트 ON이면 라이트 복귀, 아니면 완전 해제
                    if (StrategyParams.Realtime.EnableKosdaqLightAll)
                    {
                        if (!poolL.TryGetScreen(code, out _))
                            poolL.RegisterCode(axKHOpenAPI1, code, KiwoomAutoTRD.Services.StrategyParams.Realtime.LightFids); // [변경 ②] 단일 소스
                        UiAppend($"[COND/REAL] 이탈 → LIGHT {code}");
                    }
                    else
                    {
                        UiAppend($"[COND/REAL] 이탈 → UNREG {code}");
                    }
                }
            }
            catch { /* 무시 */ }
        }



        // === 틱 수신 시각 갱신(체결/시세 콜백들에서 호출) ===
        private void TouchLastTick(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            _lastTickUtc[code] = DateTime.UtcNow;
        }

        // === 핀 규칙: 미체결/보유 존재 시 DEEP 유지 ===
        private bool IsPinDeep(string code)
        {
            try
            {
                if (_tradingManager == null) return false;
                if (_tradingManager.HasOpenOrders(code)) return true;
                if (_tradingManager.PositionQty(code) != 0) return true;
            }
            catch { /* 무시 */ }
            return false;
        }

        // === 스윕: DEEP 강등/워치독 ===
        private void EnsureSweepTimer()
        {
            if (_sweepTimer != null) return;
            _sweepTimer = new System.Timers.Timer(1_000);
            _sweepTimer.AutoReset = true;
            _sweepTimer.Elapsed += (s, e) =>
            {
                try
                {
                    var now = DateTime.UtcNow;
                    var poolL = ScreenManager.Get("real_stock");
                    var poolD = ScreenManager.Get("real_stock_deep");

                    foreach (var code in _deepCodes.ToList())
                    {
                        // 1) 핀 규칙: 미체결/보유/유예 구간이면 유지
                        if (IsPinDeep(code))
                        {
                            _pinDeep.Add(code);
                            continue;
                        }

                        // 유예 시간(Grace) 고려
                        if (_pinDeep.Contains(code))
                        {
                            // 핀 해제 최초 시각 기록 후 충분히 지났는지 검사
                            DateTime last;
                            if (!_lastTickUtc.TryGetValue(code, out last)) last = now;
                            var graceOk = (now - last).TotalMilliseconds >= StrategyParams.Realtime.GraceMsBeforeDowngrade;
                            if (!graceOk) continue;
                            _pinDeep.Remove(code);
                        }

                        // 2) 워치독: 딥인데 틱이 너무 오래 없으면 재보장(선택)
                        DateTime t;
                        if (_lastTickUtc.TryGetValue(code, out t))
                        {
                            var noTickSec = (now - t).TotalSeconds;
                            if (noTickSec >= StrategyParams.Realtime.DeepWatchdogNoTickSec)
                            {
                                // 딥 재보장(해제→재등록)
                                if (poolD.TryGetScreen(code, out _))
                                    poolD.UnregisterCode(axKHOpenAPI1, code);
                                poolD.RegisterCode(axKHOpenAPI1, code, DEEP_FIDS);
                                _lastTickUtc[code] = now;
                                UiAppend($"[REAL/WATCH] 딥 재보장 {code}");
                                continue;
                            }
                        }

                        // 3) 강등: 조건 OK면 DEEP 해제 → (옵션) LIGHT 복귀
                        if (poolD.TryGetScreen(code, out _))
                            poolD.UnregisterCode(axKHOpenAPI1, code);
                        _deepCodes.Remove(code);

                        if (StrategyParams.Realtime.EnableKosdaqLightAll)
                        {
                            if (!poolL.TryGetScreen(code, out _))
                                poolL.RegisterCode(axKHOpenAPI1, code, LIGHT_FIDS);
                            UiAppend($"[REAL/SWEEP] 강등 → LIGHT {code}");
                        }
                        else
                        {
                            UiAppend($"[REAL/SWEEP] 강등 → UNREG {code}");
                        }
                    }
                }
                catch { /* 무시 */ }
            };
            _sweepTimer.Start();
        }

        // === (옵션) 폴백 TR: 포지션/미체결 존재 시 저빈도 계좌/미체결 조회 ===
        private void EnsureFallbackTimer()
        {
            if (StrategyParams.Realtime.FallbackTrPollSec <= 0) return;
            if (_fallbackTimer != null) return;

            _fallbackTimer = new System.Timers.Timer(Math.Max(5, StrategyParams.Realtime.FallbackTrPollSec) * 1000);
            _fallbackTimer.AutoReset = true;
            _fallbackTimer.Elapsed += (s, e) =>
            {
                try
                {
                    if (_tradingManager == null) return;

                    // 전체 포지션/미체결 스냅샷 필요 시에만 폴백 TR 수행
                    bool need = false;
                    try
                    {
                        // 간단 기준: DEEP 코드가 있거나, 포지션/미체결 총합 > 0
                        if (_deepCodes.Count > 0) need = true;
                    }
                    catch { /* 무시 */ }

                    if (!need) return;

                    // ※ 화면번호는 기존 my_info(2000대) 사용. TR 자체 구현은 프로젝트 기존 로직을 재사용.
                    TryRequestOutstandingAndPositions();
                }
                catch { /* 무시 */ }
            };
            _fallbackTimer.Start();
        }

        // 프로젝트에 이미 계좌/미체결 TR 요청 경로가 있다면 그 함수를 호출.
        // 없다면 아래 구현을 차후 실제 TR 세팅으로 대체하면 됨.
        private void TryRequestOutstandingAndPositions()
        {
            try
            {
                // 예: 기존에 사용하던 TR 요청 래퍼 재사용 (존재 시)
                // RequestDeposit(); RequestAccountEval(); RequestOpenOrders();

                // 최소한의 안전 로그
                UiAppend("[FALLBACK/TR] 계좌/미체결 조회 트리거 (저빈도)");
            }
            catch { /* 무시 */ }
        }

        // === 틱 이벤트 핸들러 내부에서 TouchLastTick 호출만 보강 ===
        private void axKHOpenAPI1_OnReceiveRealData(object sender, _DKHOpenAPIEvents_OnReceiveRealDataEvent e)
        {
            try
            {
                var code = (e.sRealKey ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(code)) TouchLastTick(code);

                // ... (기존 체결/호가 파싱·분배 로직 그대로 유지) ...
            }
            catch { /* 무시 */ }
        }


        // ✅ UI 로그 브리지(중앙 통일) — textBox/리치텍스트는 TradingEvents 구독으로 갱신됨
        private static void UiAppend(string text)
        {
            try { KiwoomAutoTRD.Adapters.TradingEvents.RaiseTradeInfo(text); } catch { }
        }


        #endregion --- 이벤트 구독 및 해제 ---  



        #region UI Thread Helper (public) // ActiveX 호출은 UI 스레드 강제. 외부(TradingManager)에서 안전하게 실행하기 위한 공개 래퍼 START
        public void RunOnUi(Action action)
        {
            if (action == null) return;
            try
            {
                var c = this.axKHOpenAPI1 as System.Windows.Forms.Control;
                if (c == null || c.IsDisposed || !c.IsHandleCreated)
                    return;

                if (c.InvokeRequired)
                {
                    c.BeginInvoke((System.Windows.Forms.MethodInvoker)(() =>
                    {
                        try { action(); }
                        catch { /* no-throw: UI 콜백 실패 격리 */ }
                    }));
                }
                else
                {
                    action();
                }
            }
            catch { /* UI 마샬링 실패 격리 */ }
        }

        // === (선택) ActiveX 편의 래퍼: SetRealReg / SetRealRemove ===
        public void SetRealRegUi(string screen, string code, string fids, string opt)
            => RunOnUi(() => { try { axKHOpenAPI1.SetRealReg(screen, code, fids, opt); } catch { } });

        public void SetRealRemoveUi(string screen, string codeOrAll)
            => RunOnUi(() => { try { axKHOpenAPI1.SetRealRemove(screen, codeOrAll); } catch { } });



        // === DEEP 구독/해제 래퍼 (반드시 UI 스레드에서만 호출) ===
        public bool SubscribeDeep(string code, string screenNo)
        {
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(screenNo)) return false;
            try
            {
                // fidSet: "10;12;13;14;15;27;28;121;122" 등 필요 FID 구성은 내부 정책으로
                // 이 예제는 최소 "체결" 기반(10/12/13/14/15) + 최우선호가(27/28) + 잔량(121/122)
                string fidSet = "10;12;13;14;15;27;28;121;122";
                // SetRealReg(화면번호, 종목코드, FID목록, 갱신방식 0:추가 1:대체)
                axKHOpenAPI1.SetRealReg(screenNo, code, fidSet, "0");
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool UnsubscribeDeep(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            try
            {
                // DisconnectRealData: 화면번호 기준 해제도 가능하지만
                // 개별 종목 해제는 SetRealRemove 사용
                axKHOpenAPI1.SetRealRemove("", code);
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion UI Thread Helper (public) // ActiveX 호출은 UI 스레드 강제. 외부(TradingManager)에서 안전하게 실행하기 위한 공개 래퍼 END



        // 모든 풀에서 코드가 등록된 스크린을 찾아오는 ‘공식’ 해상기
        private bool TryResolveScreen(string code, out string screen)
        {
            screen = null;
            if (string.IsNullOrWhiteSpace(code)) return false;

            var c = NormalizeCode(code);

            foreach (var pool in ScreenManager.AllPools)
            {
                // 정규화된 코드로 먼저 시도
                if (pool.TryGetScreen(c, out screen) && !string.IsNullOrEmpty(screen))
                    return true;

                // 혹시 풀에 'A000000'로 저장돼 있다면 보정 시도
                var withA = "A" + c;
                if (pool.TryGetScreen(withA, out screen) && !string.IsNullOrEmpty(screen))
                    return true;
            }
            return false;
        }

        // --- 조건식 로드 및 실행 ---
        public void LoadAndRunCondition()
        {
            try
            {
                // 조건식 서버목록 로드만 트리거 (목록과 실행은 OnReceiveConditionVer에서 처리)
                int ret = axKHOpenAPI1.GetConditionLoad();
                if (ret != 1)
                { 
                    Console.WriteLine("조건식 로드 실패(호출반환)");
                }
                else
                {
                    Console.WriteLine("조건식 로드 요청 성공(이벤트 대기)");
                 }

                }
            catch
            {
                // 무시 가능: 실제 처리/로그는 Form1 이벤트에서 수행
            }
        }


        // ★★★ [옵션] 0.3초 모멘텀 필터 — 언제든 삭제 가능 ★★★
        private readonly Dictionary<string, Queue<(DateTime ts, int qty)>> _momWin
            = new Dictionary<string, Queue<(DateTime ts, int qty)>>(StringComparer.OrdinalIgnoreCase);


        // 0.3초 윈도우 StategyParams에서 공통 정의로 이동
        private static readonly TimeSpan MOMENTUM_WINDOW
            = KiwoomAutoTRD.Services.StrategyParams.MomentumWindow;

        private const int MOMENTUM_QTY_THRESHOLD = 400; // 1.0초당 합계 ≥ 400

        // ★ "연속 유지" 요구치: momentumOk가 N초 연속 충족해야 승격
        private const int MOMENTUM_SUSTAIN_REQUIRED = 2; // 2초 연속 유지

        // 코드별 연속 유지 카운터(충족 시 +1, 실패 시 0으로 리셋)
        private readonly Dictionary<string, int> _momSustain
            = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        

        private int UpdateMomentumWindow(string code, int tickQty, DateTime nowUtc)
{
    if (string.IsNullOrWhiteSpace(code)) return 0;

    Queue<(DateTime ts, int qty)> q;
    if (!_momWin.TryGetValue(code, out q))
    {
        q = new Queue<(DateTime ts, int qty)>(8);
        _momWin[code] = q;
    }

    if (tickQty > 0)
        q.Enqueue((nowUtc, tickQty));

    while (q.Count > 0 && (nowUtc - q.Peek().ts) > MOMENTUM_WINDOW)
        q.Dequeue();

    int sum = 0;
    foreach (var it in q) sum += it.qty;
    return sum;
}
        // ★★★ [옵션 끝] ★★★



        // --- 실시간 데이터 처리 START ---
        private void OnReceiveRealData(object sender, AxKHOpenAPILib._DKHOpenAPIEvents_OnReceiveRealDataEvent e)
        {
            // 지역 변수로 일원화(가독성/오타방지)
            string sCode = e.sRealKey;  // 종목코드
            string sRealType = e.sRealType; // 실시간 타입


            // ✅ 널 가드 추가 (핫패스 NRE 방지)
            var mgr = _tradingManager;
            if (mgr != null)
            {
                try { mgr.OnRealData(sCode, sRealType); }
                catch { /* 안전 무시 */ }


                // === [추가] VI 발동/해제 처리 ===
                if (sRealType == "VI발동/해제")
                {
                    // 핵심 FID
                    string viFlag = axKHOpenAPI1.GetCommRealData(sCode, 9068)?.Trim(); // 9068=VI발동구분 (1=발동, 2=해제)

                    // 참고용 FID(로그/디버깅에 유용)
                    string viPrice = axKHOpenAPI1.GetCommRealData(sCode, 1221)?.Trim(); // 1221=VI발동가격
                    string viRate = axKHOpenAPI1.GetCommRealData(sCode, 1489)?.Trim(); // 1489=VI발동가 등락률
                    string viCount = axKHOpenAPI1.GetCommRealData(sCode, 1490)?.Trim(); // 1490=VI발동횟수

                    Console.WriteLine($"[VI] {sCode} type={sRealType}, flag={viFlag}, price={viPrice}, rate={viRate}, count={viCount}"); // VI 출력

                    bool isTrig = (viFlag == "1");
                    bool isRel = (viFlag == "2");

                    if (isTrig)
                    {
                        _viTriggeredCount++;
                        _tradingManager?.OnViTriggered(sCode, true);   // 발동 → 매수 금지
                    }
                    else if (isRel)
                    {
                        _viReleasedCount++;
                        _tradingManager?.OnViTriggered(sCode, false);  // 해제 → 금지 해제
                    }

                    try { ViSignal?.Invoke(sCode, isTrig, viPrice, viRate, viCount); } catch { /* 무시 */ }
                    return; // VI는 여기서 종료(다른 실시간 처리와 분리)
                }


                // ‘주식체결’에서 가벼운 판단으로 승격
                if (sRealType == "주식체결")
                {
                    // 최소 FID만 사용 : 현재가(10), 등락률(12), 거래량(13), 체결량(15, 단건)
                    var sPrice = axKHOpenAPI1.GetCommRealData(sCode, 10)?.Trim();    // 10=현재가
                    var sChgRt = axKHOpenAPI1.GetCommRealData(sCode, 12)?.Trim();    // 12=등락률
                    var sVol = axKHOpenAPI1.GetCommRealData(sCode, 13)?.Trim();    // 13=거래량
                    var sAmt = axKHOpenAPI1.GetCommRealData(sCode, 14)?.Trim();
                    var sTickQ = axKHOpenAPI1.GetCommRealData(sCode, 15)?.Trim(); // ★ 체결량(단건)
                                                                                  // “매수 주도/매도 주도”는 보통 현재가 vs 최우선호가(27, 28) 로 분류합니다. 체결가 >= (최우선)매도호가(27) → 매수 주도 체결로 간주, 체결가 <= (최우선)매수호가(28) → 매도 주도 체결로 간주,   그 외 → 중립(또는 패스)

                    int price = 0, vol = 0, tickQty = 0;
                    double chgRt = 0;
                    int.TryParse((sPrice ?? "").Replace("+", "").Replace("-", ""), out price);
                    int.TryParse(sVol ?? "0", out vol);
                    int.TryParse((sTickQ ?? "0").Replace(",", ""), out tickQty);
                    double.TryParse(sChgRt ?? "0", out chgRt);


                    //Console.WriteLine($"[TICK] {sCode} P={sPrice} R={sChgRt} V={sVol}");    // [TICK] 000660 P=62000 R=2.63 V=123456 예상 출력내용


                    // ★ 마지막 활동 갱신(여기 한 줄 추가)
                    if (!string.IsNullOrWhiteSpace(sCode))
                        lastActive[NormalizeCode(sCode)] = DateTime.UtcNow;

                    // === 0.3초 모멘텀 계산 ===
                    var nowUtc = DateTime.UtcNow;
                    var codeN = NormalizeCode(sCode);
                    int recentQty = UpdateMomentumWindow(codeN, tickQty, nowUtc);   // 최근 0.3초 체결 '건수' 누적
                    bool momentumOk = (recentQty >= MOMENTUM_QTY_THRESHOLD);        // 0.3초당 합계 ≥ 100
                    bool rateOk = (chgRt >= 2.5);

                    // === "연속 유지" 카운터 ===
                    int sustain = 0;
                    if (!_momSustain.TryGetValue(codeN, out sustain)) sustain = 0;
                    if (momentumOk) sustain += 1;
                    else sustain = 0;
                    _momSustain[codeN] = sustain;

                    // === 모멘텀 엔진용 틱 이벤트 발생(단건 체결량 포함) ===
                    try
                    {
                        var h = TradeTick;
                        if (h != null && price > 0 && tickQty > 0)
                            h(sCode, price, tickQty, DateTime.UtcNow);
                    }
                    catch { /* 안전 무시 */ }



                    // ---- 강화된 승격 조건 ----
                    if (momentumOk && rateOk)
                        PromoteToDeep(sCode);

                    //   - 등락률 필터(기존) 충족
                    if (momentumOk && sustain >= MOMENTUM_SUSTAIN_REQUIRED && rateOk)
                        PromoteToDeep(sCode);

                    // ---- 모멘텀 미달 시 DEEP 강등 ----
                    // “거래량이 높아도 0.3초당 100건을 못 채우면 DEEP에서 제외”
                    if (deepCodes.Contains(codeN) && !momentumOk)
                        DemoteToLight(sCode);

                    // ---- 강등 조건(예: 급락) ----
                    if (chgRt <= -2.0)
                        DemoteToLight(sCode);


                    // ---- DEEP 종목이면: 전략/UI 양방향으로 틱 알림 ----
                    // ---- DEEP 종목이면 전략/UI에 심층 틱 통지 ----
                    try
                    {
                        var c = NormalizeCode(sCode);

                        if (deepCodes.Contains(c))
                        {
                            var sLast = axKHOpenAPI1.GetCommRealData(sCode, 10)?.Trim();  // 현재가
                            var sAsk = axKHOpenAPI1.GetCommRealData(sCode, 27)?.Trim();  // 최우선 매도
                            var sBid = axKHOpenAPI1.GetCommRealData(sCode, 28)?.Trim();  // 최우선 매수
                            var sAskQ = axKHOpenAPI1.GetCommRealData(sCode, 121)?.Trim(); // 매도 잔량
                            var sBidQ = axKHOpenAPI1.GetCommRealData(sCode, 122)?.Trim(); // 매수 잔량

                            int bestBid = SafeParse.ParseInt(sBid);
                            int bestAsk = SafeParse.ParseInt(sAsk);
                            int last = SafeParse.ParseInt(sLast);
                            int askQty = SafeParse.ParseInt(sAskQ);
                            int bidQty = SafeParse.ParseInt(sBidQ);

                            // UI(state) 갱신
                            DeepTick?.Invoke(c, chgRt, vol, bestBid);

                            // 전략 엔진으로 전달
                            _tradingManager?.OnDeepTick(c, last, bestBid, bestAsk, bidQty, askQty, chgRt);
                        }



                        // ★★★ 전면 수집: 딥 여부와 무관하게 L1Quote 발생 (지역변수 재선언으로 충돌/스코프 문제 해결)
                        try
                        {
                            // 기존 변수와 이름 충돌 피하기 위해 새로운 이름 사용
                            var codeNorm = NormalizeCode(e.sRealKey);

                            // 기존 포맷 그대로 재취득
                            string sLast2 = axKHOpenAPI1.GetCommRealData(e.sRealKey, 10)?.Trim();   // 현재가
                            string sAsk2 = axKHOpenAPI1.GetCommRealData(e.sRealKey, 27)?.Trim();   // 최우선 매도
                            string sBid2 = axKHOpenAPI1.GetCommRealData(e.sRealKey, 28)?.Trim();   // 최우선 매수
                            string sAskQ2 = axKHOpenAPI1.GetCommRealData(e.sRealKey, 121)?.Trim();  // 매도 잔량
                            string sBidQ2 = axKHOpenAPI1.GetCommRealData(e.sRealKey, 122)?.Trim();  // 매수 잔량

                            // 안전 파싱(기존과 동일 규칙: +, -, , 제거)
                            int last2 = 0, bestAsk2 = 0, bestBid2 = 0, askQty2 = 0, bidQty2 = 0;
                            int.TryParse((sLast2 ?? "").Replace("+", "").Replace("-", "").Replace(",", ""), out last2);
                            int.TryParse((sAsk2 ?? "").Replace("+", "").Replace("-", "").Replace(",", ""), out bestAsk2);
                            int.TryParse((sBid2 ?? "").Replace("+", "").Replace("-", "").Replace(",", ""), out bestBid2);
                            int.TryParse((sAskQ2 ?? "").Replace("+", "").Replace("-", "").Replace(",", ""), out askQty2);
                            int.TryParse((sBidQ2 ?? "").Replace("+", "").Replace("-", "").Replace(",", ""), out bidQty2);

                            // 위쪽에서 이미 계산된 chgRt(double)를 같은 스코프에서 사용 (없으면 0으로 두어도 됨)
                            double dChgRt2 = 0;
                            try { dChgRt2 = chgRt; } catch { /* 동일 스코프에 chgRt가 없으면 0 사용 */ }

                            var h = L1Quote;
                            if (h != null)
                                h(codeNorm, last2, bestBid2, bestAsk2, bidQty2, askQty2, dChgRt2);
                        }
                        catch
                        {
                            // 전면 수집용 L1 실패는 무시 (트레이딩 경로 영향 없음)
                        }

                    }
                    catch { /* 안전 무시 */ }

                }

            }
        }
        // --- 실시간 데이터 처리 END ---



        // --- 실시간 데이터 불러와지는 종목 확인코드    START

        private static void LogSafe(string msg)
        {
            var line = msg ?? string.Empty;
            try { Debug.WriteLine(line); } catch { }
            try { Trace.WriteLine(line); } catch { }
        }

        public string[] GetRealtimeSubscriptionSnapshot()
        {
            try
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var pool in ScreenManager.AllPools)
                {
                    string[] codes;
                    try { codes = pool.GetRegisteredCodesSnapshot(); }
                    catch { continue; }

                    foreach (var c in codes)
                    {
                        if (string.IsNullOrWhiteSpace(c)) continue;
                        var norm = (c[0] == 'A' || c[0] == 'a') ? c.Substring(1) : c;
                        set.Add(norm);
                    }
                }
                return set.ToArray();
            }
            catch
            {
                return new string[0];
            }
        }

        // --- 실시간 데이터 불러와지는 종목 확인코드    END










        // TR 수신 → 전략 경로로 전달
        private void OnReceiveTrData(object sender, _DKHOpenAPIEvents_OnReceiveTrDataEvent e)
        {
            _tradingManager?.OnTrData(e.sRQName, e.sTrCode, e.sScrNo);
        }

        // 체잔 수신 → 전략 경로로 전달
        private void OnReceiveChejanData(object sender, _DKHOpenAPIEvents_OnReceiveChejanDataEvent e)
        {
            _tradingManager?.OnChejanData(e.sGubun, e.nItemCnt, e.sFIdList);
        }


        public string[] GetCodesByMarket(string market)
        {
            var raw = axKHOpenAPI1.GetCodeListByMarket(market) ?? string.Empty; // "10"=KOSDAQ, "0"=KOSPI
            return raw.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
        }

        private IEnumerable<string[]> Chunk(string[] arr, int size)
        {
            for (int i = 0; i < arr.Length; i += size)
            {
                var len = Math.Min(size, arr.Length - i);
                var piece = new string[len];
                Array.Copy(arr, i, piece, 0, len);
                yield return piece;
            }
        }


        // 코스닥 전 종목을 '가벼운 FID(LIGHT_FIDS)'로만 등록
        public void RegisterKosdaqLightAll()
        {
            // [안전가드] 혹시라도 다른 경로에서 호출되더라도 정책이 false면 노옵(no-op)
            if (!KiwoomAutoTRD.Services.StrategyParams.Realtime.EnableKosdaqLightAll)
            {
                try { Console.WriteLine("[REAL] KOSDAQ Light 등록 건너뜀 (EnableKosdaqLightAll=false)"); } catch { }
                return;
            }

            var codes = GetCodesByMarket("10"); // "10"=KOSDAQ
            if (codes.Length == 0) return;

            var pool = ScreenManager.Get("real_stock");

            foreach (var code in codes)
            {
                // 풀 정책으로 등록 (스크린 매핑은 필요 시 TryResolveScreen으로 조회)
                pool.RegisterCode(axKHOpenAPI1, code, LIGHT_FIDS);
            }
            try { Console.WriteLine($"[REAL] KOSDAQ Light 등록: {codes.Length} codes / screens={pool.Screens.Count}"); } catch { }
        }


        // ---------- 매수 주문단 START ----------
        public bool SendOrderBuy(string rqName, string code, int qty, int price)
        {
            // rqName: 사용자 구분용 요청명
            if (string.IsNullOrWhiteSpace(AccountNumber)) return false;
            string strat = KiwoomAutoTRD.Services.StrategyParams.CanonicalTag;
            string runId = KiwoomAutoTRD.Services.StrategyParams.RunId;
            Console.WriteLine($"[ORD] BUY {code} {qty}@{price} — Strategy={strat}, RUN_ID={runId}");    // 주문 로그 

            if (string.IsNullOrWhiteSpace(code) || qty <= 0 || price <= 0) return false;

            // 매수 레이트 리밋(초당 4 / 분당 90)
            if (!KiwoomAutoTRD.Services.OrderRateLimiter.TryAcquire())
            {
                try { Console.WriteLine($"[RATE] DROP BUY {code} {qty}@{price}"); } catch { }
                return false; // UI 프리즈 방지: 대기 대신 거절
            }

            bool ok = false;
            Ui(() =>
            {
                try
                {
                    // 지정가(00) 신규매수(1), 주문화면번호는 주문 전용 대역(5600) 사용
                    int ret = axKHOpenAPI1.SendOrder(
                        rqName, "5600", AccountNumber,
                        1, code, qty, price, "00", ""
                    );
                    ok = (ret == 0);
                }
                catch { ok = false; }
            });
            return ok;
        }
        // ---------- 매수 주문단 END ----------



        // ---------- 매도 주문단 START ----------
        public bool SendOrderSell(string rqName, string code, int qty, int price)
        {
            if (string.IsNullOrWhiteSpace(AccountNumber)) return false;
            if (string.IsNullOrWhiteSpace(code) || qty <= 0 || price <= 0) return false;

            // ★ 레이트 리밋(초당 4 / 분당 90)
            if (!KiwoomAutoTRD.Services.OrderRateLimiter.TryAcquire())
            {
                try { Console.WriteLine($"[RATE] DROP SELL {code} {qty}@{price}"); } catch { }
                return false; // UI 프리즈 방지: 대기 대신 거절
            }

            bool ok = false;
            Ui(() =>
            {
                try
                {
                    // 지정가(00) 신규매도(2), 주문화면번호는 주문 전용 대역(5600) 사용
                    int ret = axKHOpenAPI1.SendOrder(
                        rqName, "5600", AccountNumber,
                        2, code, qty, price, "00", ""
                    );
                    ok = (ret == 0);
                }
                catch { ok = false; }
            });
            return ok;
        }
        // ---------- 매도 주문단 END ----------

        // ---------- 스텝다운 매도 주문단 START ----------
        public bool SendOrderSellStepDown(string rqName, string code, int qty, int startPrice, int tickSize)
        {
            if (string.IsNullOrWhiteSpace(AccountNumber)) return false;
            if (string.IsNullOrWhiteSpace(code) || qty <= 0 || startPrice <= 0 || tickSize <= 0) return false;

            bool ok = false;
            Ui(() =>
            {
                try
                {
                    // 지정가(00) 신규매도(2), 5600번 화면 사용
                    int ret = axKHOpenAPI1.SendOrder(
                        rqName, "5600", AccountNumber,
                        2, code, qty, startPrice, "00", ""
                    );
                    ok = (ret == 0);
                }
                catch { ok = false; }
            });
            return ok;
        }
        // ---------- 스텝다운 매도 주문단 END ----------


        // ---------- 시장가 매도/매수 전송 헬퍼 (모의/실전 공통) START ----------
        public bool SendOrderSellMarket(string rqName, string code, int qty)
        {
            if (string.IsNullOrWhiteSpace(AccountNumber)) return false;
            if (string.IsNullOrWhiteSpace(code) || qty <= 0) return false;

            if (!KiwoomAutoTRD.Services.OrderRateLimiter.TryAcquire())
            {
                try { Console.WriteLine($"[RATE] DROP SELL-MKT {code} {qty}"); } catch { }
                return false;
            }

            bool ok = false;
            Ui(() =>
            {
                try
                {
                    // 거래구분(hogaGb) "03" = 시장가, 가격 0
                    int ret = axKHOpenAPI1.SendOrder(
                        rqName, "5600", AccountNumber,
                        2, code, qty, 0, "03", ""
                    );
                    ok = (ret == 0);
                }
                catch { ok = false; }
            });
            return ok;
        }
        // ---------- 시장가 매도/매수 전송 헬퍼 (모의/실전 공통) END ----------


        public bool SendOrderBuyMarket(string rqName, string code, int qty)
        {
            if (string.IsNullOrWhiteSpace(AccountNumber)) return false;
            if (string.IsNullOrWhiteSpace(code) || qty <= 0) return false;

            if (!KiwoomAutoTRD.Services.OrderRateLimiter.TryAcquire())
            {
                try { Console.WriteLine($"[RATE] DROP BUY-MKT {code} {qty}"); } catch { }
                return false;
            }

            bool ok = false;
            Ui(() =>
            {
                try
                {
                    // 거래구분(hogaGb) "03" = 시장가, 가격 0
                    int ret = axKHOpenAPI1.SendOrder(
                        rqName, "5600", AccountNumber,
                        1, code, qty, 0, "03", ""
                    );
                    ok = (ret == 0);
                }
                catch { ok = false; }
            });
            return ok;
        }









        // ---------- 미체결 취소 START ----------
        public bool SendOrderCancel(string rqName, string ordNo, string code, int qty)
        {
            if (string.IsNullOrWhiteSpace(AccountNumber))
            {
                try { TradingEvents.RaisePending($"[ORD-FAIL] CANCEL ord:{ordNo} (no account)"); } catch { }
                return false;
            }
            if (string.IsNullOrWhiteSpace(ordNo))
            {
                try { TradingEvents.RaisePending($"[ORD-FAIL] CANCEL (ord missing)"); } catch { }
                return false;
            }

            // ★ 취소도 레이트 리밋 대상(초당 4 / 분당 90)
            if (!KiwoomAutoTRD.Services.OrderRateLimiter.TryAcquire())
            {
                try { TradingEvents.RaisePending($"[RATE] DROP CANCEL ord:{ordNo}"); } catch { }
                return false;
            }

            bool ok = false;
            Ui(() =>
            {
                try
                {
                    // 3 = 취소, code/qty는 환경별 요구 다름 → 인자 그대로 전달
                    int ret = axKHOpenAPI1.SendOrder(
                        rqName, "5600", AccountNumber,
                        3,                    // 취소
                        code ?? "",           // 일부 환경에서 필요
                        qty,                  // 0=잔량전량 취소 허용 환경 존재
                        0, "00", ordNo        // 원주문번호
                    );
                    ok = (ret == 0);
                    if (!ok) { try { TradingEvents.RaisePending($"[ORD-FAIL] CANCEL ord:{ordNo} ret={ret}"); } catch { } }
                }
                catch (Exception ex)
                {
                    ok = false;
                    try { TradingEvents.RaisePending($"[ORD-FAIL] CANCEL ord:{ordNo} ex={ex.Message}"); } catch { }
                }
            });
            return ok;
        }
        // ---------- 미체결 취소 END ----------










        // === [추가] DEEP 여부 조회 헬퍼 ===
        public bool IsDeep(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return false;
            try { return deepCodes.Contains(NormalizeCode(code)); } catch { return false; }
        }


        // === [추가] 틱사이즈 계산 헬퍼 ===
        // (프로젝트에 기존 호가단위 함수가 있다면 그 함수를 호출하도록 교체해도 OK)
        public int GetTickSize(string code, int price)
        {
            if (price < 2000) return 1;
            if (price < 5000) return 5;
            if (price < 10000) return 10;
            if (price < 50000) return 50;
            if (price < 100000) return 100;
            if (price < 500000) return 500;
            return 1000;
        }


        // 모든 풀에서 코드가 등록된 스크린을 찾아오는 ‘공식’ 해상기
        public void PromoteToDeep(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;

            var c = NormalizeCode(code);
            if (deepCodes.Contains(c)) return;

            // 1차: 등록된 스크린 찾아보기
            if (!TryResolveScreen(c, out var screen))
            {
                Console.WriteLine($"[WARN] Screen resolve failed for {c} → fallback register to real_stock");

                // 2차: 실시간 풀에 경량(LIGHT_FIDS)으로 먼저 등록해 스크린을 확보
                var pool = ScreenManager.Get("real_stock");
                pool.RegisterCode(axKHOpenAPI1, c, LIGHT_FIDS);

                // 3차: 다시 스크린 해석
                if (!TryResolveScreen(c, out screen))
                {
                    Console.WriteLine($"[ERROR] Screen resolve still failed for {c} after fallback");
                    return;
                }
            }

            // 최종: 심층(DEEP_FIDS) 추가 등록
            axKHOpenAPI1.SetRealReg(screen, c, DEEP_FIDS, "1");
            deepCodes.Add(c);

            Console.WriteLine($"[REAL] Promote {c} → DEEP (screen={screen})"); //  DEEP 스크린번호 출력 로그
            try { DeepPromoted?.Invoke(c); } catch { /* 무시 */ }

            // --- [추가] 전략 초기화: 승격 시점의 기준가/최우선매수호가를 전달 ---
            try
            {
                // 등록 직후의 값(없을 수 있으나 대체로 최신 틱 유지)
                var sLast = axKHOpenAPI1.GetCommRealData(c, 10)?.Trim();
                var sBid = axKHOpenAPI1.GetCommRealData(c, 28)?.Trim();

                int last = 0, bid = 0;
                int.TryParse((sLast ?? "").Replace(",", "").Replace("+", "").Replace("-", ""), out last);
                int.TryParse((sBid ?? "").Replace(",", "").Replace("+", "").Replace("-", ""), out bid);

                _tradingManager?.OnDeepPromoted(c, bid, last);
            }
            catch { /* 무시 */ }

        }

        /// <summary>해당 종목을 가벼운(LIGHT_FIDS)으로 강등</summary>
        public void DemoteToLight(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;

            // ★ 종목코드 정규화 (A000140 → 000140)
            var c = NormalizeCode(code);
            if (!deepCodes.Contains(c)) return;

            if (!TryResolveScreen(c, out var screen))
            {
                Console.WriteLine($"[WARN] Screen resolve failed for {c}");
                return;
            }

            axKHOpenAPI1.SetRealReg(screen, c, LIGHT_FIDS, "0");
            deepCodes.Remove(c);
            lastActive[c] = DateTime.UtcNow;

            Console.WriteLine($"[REAL] Demote {c} → LIGHT");
            try { DeepDemoted?.Invoke(c); } catch { /* 무시 */ }
        }

        // 해당 종목 실시간 해제
        public void UnregisterCode(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;

            // ★ 종목코드 정규화
            var c = NormalizeCode(code);

            if (!TryResolveScreen(c, out var screen)) return;

            axKHOpenAPI1.SetRealRemove(screen, c);
            deepCodes.Remove(c);
            lastActive.Remove(c);
        }

        /// <summary>전체 실시간 해제</summary>

        public void UnregisterAllRealtime()
        {
            // 0) ActiveX 관련 호출은 반드시 UI 스레드에서
            Ui(() =>
            {
                // 1) 화면별 실시간 수신 차단
                DisconnectRange(1000, 1099); // start_stop
                DisconnectRange(2000, 2099); // my_info
                DisconnectRange(5000, 5599); // real_stock (DEEP/LIGHT)
                DisconnectRange(5600, 5799); // order_stock
                DisconnectRange(6000, 6999); // condition

                // 2) 화면별 등록 제거
                RemoveRange(1000, 1099);
                RemoveRange(2000, 2099);
                RemoveRange(5000, 5599);
                RemoveRange(5600, 5799);
                RemoveRange(6000, 6999);

                // 3) 최후 안전장치: 핸들이 살아있을 때만 전체 해제(예외 무시)
                SafeRealRemoveAll();
            });

            // 4) 내부 상태 정리 (UI 스레드 불필요)
            deepCodes.Clear();
            lastActive.Clear();
            _momSustain.Clear(); // ★ 연속 유지 카운터도 초기화


            // 5) 스윕 타이머 정지/해제
            if (_sweepTimer != null)
            {
                try { _sweepTimer.Stop(); } catch { }
                try { _sweepTimer.Dispose(); } catch { }
                _sweepTimer = null;
            }

            // ---- 지역 함수들 ----
            void DisconnectRange(int from, int to)
            {
                for (int s = from; s <= to; s++)
                {
                    try { axKHOpenAPI1.DisconnectRealData(s.ToString()); } catch { }
                }
            }

            void RemoveRange(int from, int to)
            {
                for (int s = from; s <= to; s++)
                {
                    try { axKHOpenAPI1.SetRealRemove(s.ToString(), "ALL"); } catch { }
                }
            }
        }

        // UI 스레드 보장: ActiveX는 반드시 UI 스레드에서 호출

        private void Ui(Action a)
        {
            var c = axKHOpenAPI1 as System.Windows.Forms.Control;
            if (c != null && c.InvokeRequired)
            {
                try { c.Invoke(a); } catch { /* 종료 중 예외 무시 */ }
            }
            else
            {
                try { a(); } catch { }
            }
        }

        // KiwoomApi 전용: ActiveX 전체 해제 안전 헬퍼
        private void SafeRealRemoveAll()
        {
            try
            {
                if (axKHOpenAPI1 == null) return;
                if (axKHOpenAPI1.IsDisposed) return;
                if (!axKHOpenAPI1.IsHandleCreated) return;

                if (axKHOpenAPI1.InvokeRequired)
                {
                    axKHOpenAPI1.BeginInvoke(new Action(() =>
                    {
                        try { axKHOpenAPI1.SetRealRemove("ALL", "ALL"); } catch { }
                    }));
                }
                else
                {
                    try { axKHOpenAPI1.SetRealRemove("ALL", "ALL"); } catch { }
                }
            }
            catch { /* 종료 경로 예외 무시 */ }
        }


    }
}
