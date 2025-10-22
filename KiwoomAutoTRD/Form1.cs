//version 250831
using AxKHOpenAPILib;
using KHOpenAPILib;
using KiwoomAutoTRD;
using KiwoomAutoTRD.Adapters;
using KiwoomAutoTRD.Common;
using KiwoomAutoTRD.Services;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static KiwoomAutoTRD.DBManager;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.TrackBar;


namespace KiwoomAutoTRD
{
    public partial class Form1 : Form
    {
        #region --- 클래스 필드 선언 START ---
        // 클래스 필드선언
        private AxKHOpenAPI axKHOpenAPI1;
        private TradingManager _tradingManager;
        private DBManager dbManager;
        private KiwoomApi _kiwoomApi;
        private BlockingCollection<string> eventQueue = new BlockingCollection<string>(new ConcurrentQueue<string>());
        private ScreenPool _condPool; // ★ 조건식 실시간 전용 ScreenPool(필드로 유지)

        //private TradingManager _tm;
        private KiwoomAutoTRD.Services.BacktestCollector _collector;


        // ---- textBox2용 DEEP 리스트 상태 ----
        private readonly Dictionary<string, string> _deepLineByCode = new Dictionary<string, string>(); // code -> formatted line

        // === 거래대금 상위 7종 관리 ===
        private readonly object _topValueLock = new object();
        private readonly Dictionary<string, long> _tradeValueByCode = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        private List<string> _top7Codes = new List<string>(7);

        // 거래대금 순위 새로고침용 타이머 & 핸들러(구독/해제 대칭을 위해 필드 보관)
        private System.Windows.Forms.Timer _tradeValueTimer;
        private bool _tradeValueHooked;


        // === 등락률 캐시 ===
        // 종목코드별 최신 등락률 저장 (DEEP 틱에서 갱신)
        private readonly Dictionary<string, double> _chgRtByCode = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        #endregion --- 클래스 필드 선언 END ---




        public Form1() //초기화
        {
            InitializeComponent();

            // ✅ TradingManager 인스턴스 미리 생성 (null 방지)
            if (_tradingManager == null)
                _tradingManager = new TradingManager();


            InitOpenApiControl();

            // ▼ textBox1을 로그 콘솔처럼 사용 (멀티라인/스크롤)
            try
            {
                textBox1.Multiline = true;
                // 가로 스크롤은 끄고, 세로만 사용
                textBox1.ScrollBars = ScrollBars.Vertical;
                textBox1.WordWrap = false;
            }
            catch { /* 디자이너 변경 상황에 따라 무시 가능 */ }

            // textBox3 UI 로그 구독
            TradingEvents.UiTradeInfo += line => Ui(() =>
            {
                try
                {
                    // textBox3: 체결/완료
                    textBox3.AppendText(line + Environment.NewLine);
                }
                catch { /* 무시 */ }
            });

            // textBox4 UI 로그 구독
            TradingEvents.UiPending += line => Ui(() =>
            {
                try
                {
                    // textBox4: 미체결
                    textBox4.AppendText(line + Environment.NewLine);
                }
                catch { /* 무시 */ }
            });

            dbManager = new DBManager(eventQueue);
            dbManager.Start();  // DBManager만 시작

            // 폼 종료 이벤트 연결(디자이너에서 이미 연결돼 있으면 생략 가능)
            this.FormClosed += Form1_FormClosed;

        }


        #region // --- 오라클 START

        //오라클 연결
        private async Task ConnectOracleAsync()
        {
            // ※ 운영에선 App.config로 분리 권장
            string connStr = "User Id=ats;Password=35Gidam!@57;Data Source=localhost:1521/FREEPDB1;";

            try
            {
                // 백그라운드에서 연결 테스트 + 간단 정보 조회(예외 → ORA-코드 포함 정규화)
                var result = await Task.Run(() =>
                {
                    try
                    {
                        using (var conn = new Oracle.ManagedDataAccess.Client.OracleConnection(connStr))
                        {
                            conn.Open(); // 여기서 OracleException 가능

                            using (var cmd = new Oracle.ManagedDataAccess.Client.OracleCommand(
                                "SELECT user, sys_context('USERENV','CON_NAME') FROM dual", conn))
                            using (var rdr = cmd.ExecuteReader())
                            {
                                if (rdr.Read())
                                {
                                    var user = rdr.GetString(0);
                                    var con = rdr.GetString(1);
                                    return Tuple.Create(true, $"DB OK: {user}@{con}", (Exception)null);
                                }
                                else
                                {
                                    return Tuple.Create(true, "DB OK: 연결 성공 (세부 정보 조회 결과 없음)", (Exception)null);
                                }
                            }
                        }
                    }
                    catch (Oracle.ManagedDataAccess.Client.OracleException oex)
                    {
                        var msg = $"DB ERR: ORA-{oex.Number} {oex.Message}";
                        return Tuple.Create(false, msg, (Exception)oex);
                    }
                    catch (Exception ex)
                    {
                        var msg = $"DB ERR: {ex.Message}";
                        return Tuple.Create(false, msg, ex);
                    }
                }).ConfigureAwait(true); // WinForms: true 유지(UI 컨텍스트 복원)

                // UI 로그
                UiAppend(result.Item2);

                if (!result.Item1)
                {
                    // 연결 실패면 여기서 종료
                    return;
                }

                // (중요) DBManager가 사용할 공유 커넥션 오픈
                string openErr;
                if (DBManager.TryOpenShared(connStr, out openErr))
                {
                    UiAppend("DB SHARED OK: ORD/FILL 업서트 가능");
                }
                else
                {
                    UiAppend("DB SHARED ERR: " + openErr);
                }

                // (선택) 스키마 존재 확인은 별도 커넥션으로 백그라운드 수행
                await Task.Run(() =>
                {
                    try
                    {
                        using (var c2 = new Oracle.ManagedDataAccess.Client.OracleConnection(connStr))
                        {
                            c2.Open();
                            VerifySchema(c2); // 기존 메서드 재사용
                        }
                    }
                    catch (Exception ex2)
                    {
                        UiAppend("DB WARN: 스키마 확인 실패 - " + ex2.Message);
                    }
                }).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // Task.Run 외부의 예외(마샬링 등) 대비
                UiAppend($"DB ERR(unexpected): {ex.Message}");
            }
        }

        //오라클 존재확인 전용 유틸메서드
        // 존재 확인 대상(프로젝트 표준 5개)
        private static readonly string[] RequiredTables = new[]
        {
            "TB_ORD_LST",
            "TB_CHEGYUL_LST",
            "TB_ACCNT",
            "TB_ACCNT_INFO",
            "TB_TRD_JONGMOK"
        };

        // DDL 없이, USER_TABLES로 존재만 확인
        private void VerifySchema(Oracle.ManagedDataAccess.Client.OracleConnection conn)
        {
            // IN 절 구성
            string inList = string.Join(",", RequiredTables.Select(t => $"'{t}'"));
            string sql = $"SELECT TABLE_NAME FROM USER_TABLES WHERE TABLE_NAME IN ({inList})";

            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using (var cmd = new Oracle.ManagedDataAccess.Client.OracleCommand(sql, conn))
            using (var rdr = cmd.ExecuteReader())
            {
                while (rdr.Read())
                {
                    string name = rdr.GetString(0) ?? "";
                    found.Add(name.ToUpperInvariant());
                }
            }

            var missing = RequiredTables.Where(t => !found.Contains(t)).ToList();

            if (missing.Count == 0)
                UiAppend("DB TABLES OK (5/5): " + string.Join(", ", RequiredTables));
            else
                UiAppend("DB WARN: 누락 테이블 → " + string.Join(", ", missing));
        }
        #endregion // --- 오라클 END


        // ActiveX 컨트롤 초기화 및 TradingManager/KiwoomApi 인스턴스 생성 메서드 START /////////////////////////////////////////////////////////////////////////////////////////
        private void InitOpenApiControl()
        {
            // 컨트롤 생성
            axKHOpenAPI1 = new AxKHOpenAPI();

            ((System.ComponentModel.ISupportInitialize)(this.axKHOpenAPI1)).BeginInit();
            this.SuspendLayout();

            // 위치/크기 설정
            this.axKHOpenAPI1.Enabled = true;
            this.axKHOpenAPI1.Location = new System.Drawing.Point(12, 12);
            this.axKHOpenAPI1.Name = "axKHOpenAPI1";

            this.axKHOpenAPI1.Size = new System.Drawing.Size(150, 30);
            this.axKHOpenAPI1.TabIndex = 0;

            // **axKHOpenAPI1.OnEventConnect  에 이벤트 핸들러 연결 추가**
            this.axKHOpenAPI1.OnEventConnect += AxKHOpenAPI1_OnEventConnect;
            this.axKHOpenAPI1.OnReceiveTrData += AxKHOpenAPI1_OnReceiveTrData;
            this.axKHOpenAPI1.OnReceiveChejanData += AxKHOpenAPI1_OnReceiveChejanData;
            // ▼ 모니터링용 실시간 핸들러(전략 로직은 KiwoomApi에서 계속 처리) 
            //this.axKHOpenAPI1.OnReceiveRealData += AxKHOpenAPI1_OnReceiveRealData; //textBox1에 출력 하게 하려면 앞에 주석만 해제 하면됨

            // axKHOpenAPI1 컨트롤을 현재 Form에 추가
            this.Controls.Add(this.axKHOpenAPI1);
            // ★ 화면에 안 보이게 숨김
            this.axKHOpenAPI1.Visible = false;
            ((System.ComponentModel.ISupportInitialize)(this.axKHOpenAPI1)).EndInit();
            this.ResumeLayout(false);


            _kiwoomApi = new KiwoomApi(axKHOpenAPI1, _tradingManager);    // _kiwoomApi 에 axKHOpenAPI1(키움 컨트롤) tradingManager(트레이딩 전략)의 경로를 인식시킨다는 인스턴스 생성

            // 전략 엔진에 API 바인딩 (주문 실행 위해)
            _tradingManager.BindApi(_kiwoomApi);

            // 조건식 이벤트 연결 ...
            _kiwoomApi.AttachEvents();

            // 조건식 이벤트 연결
            this.axKHOpenAPI1.OnReceiveConditionVer += AxKHOpenAPI1_OnReceiveConditionVer;
            this.axKHOpenAPI1.OnReceiveTrCondition += AxKHOpenAPI1_OnReceiveTrCondition;
            this.axKHOpenAPI1.OnReceiveRealCondition += AxKHOpenAPI1_OnReceiveRealCondition;

            // ---- DEEP 이벤트 구독: textBox2 갱신용
            _kiwoomApi.DeepPromoted += OnDeepPromoted;
            _kiwoomApi.DeepDemoted += OnDeepDemoted;
            _kiwoomApi.DeepTick += OnDeepTick;

            // ---- VI 신호 구독 → textBox1에 즉시 로그
            _kiwoomApi.ViSignal += (code, isTrig, price, rate, cnt) =>
            {
                var ty = isTrig ? "발동" : "해제";
                UiAppend($"[VI] {code} {ty} | price={price} rate={rate} count={cnt}");

                // 누계 출력
                var stat = _kiwoomApi.GetViStats();
                UiAppend($"[VI] 누계: 발동={stat.triggered}, 해제={stat.released}");

                // ★ 현재 활성 VI 종목 리스트 출력(이벤트가 올 때마다 최신 스냅샷을 덤프)
                try
                {
                    var list = _tradingManager.GetViBlacklistSnapshot(); // 코드 배열
                    if (list != null && list.Length > 0)
                    {
                        // 종목명 + 코드 포맷으로 보기 좋게
                        var items = list
                            .Select(c => $"{GetNameCached(c)}({c})")
                            .ToArray();

                        UiAppend($"[VI] 활성 리스트({items.Length}): " + string.Join(", ", items));
                    }
                    else
                    {
                        UiAppend("[VI] 활성 리스트(0): (없음)");
                    }
                }
                catch
                {
                    // 방어적 무시
                }
            };

        }


        ///<summary>  --- 로그인 컬렉션 START

        // 폼 로드 시 로그인 시도
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            HookTradeValueEvents(); // 거래대금 상위 7 실시간 관리 시작
            
            // --- [추가] RUN_ID 생성 ---
            string today = DateTime.Now.ToString("yyyyMMdd");   // 날짜 기준 + 시퀀스 (단일 실행 환경이므로 seq=01 고정. 필요 시 증가 로직 확장)
            StrategyParams.RunId = $"{today}-01";
            Console.WriteLine($"[RUN] Session Started → RUN_ID={StrategyParams.RunId}, Strategy={StrategyParams.CanonicalTag}");

            // 로그인 시도
            int result = axKHOpenAPI1.CommConnect();
            if (result == 0)
                Console.WriteLine("로그인 창 실행됨");
            else
                Console.WriteLine("로그인 호출 실패");
        }

        // 로그인 결과 이벤트 핸들러
        private void AxKHOpenAPI1_OnEventConnect(object sender, AxKHOpenAPILib._DKHOpenAPIEvents_OnEventConnectEvent e)
        {
            if (e.nErrCode == 0)
            {
                // 계좌번호 가져오기
                string accounts = axKHOpenAPI1.GetLoginInfo("ACCLIST");

                Console.WriteLine($"로그인 성공 계좌번호 목록: {accounts}");  //로그인 확인 출력구문
                accounts = accounts.TrimEnd(';');  // 마지막 ; 제거
                textBox1.Text = accounts;   // 디자이너에서 만든 textBox1에 계좌번호 출력
                string accNo = accounts.Split(';')[0];
                _tradingManager.OnLoginSuccess(accNo);
                _kiwoomApi.SetAccountNumber(accNo);

                // 예수금상세현황(OPW00001) 최초 1회 조회
                _kiwoomApi.RequestDepositDetail();

                // 오라클 연결 시도 (비동기)
                _ = ConnectOracleAsync();

                //  Collector를 가장 먼저 켭니다 (실시간 등록/조건식 실행 '이전')
                try
                {
                    // 1) Collector는 한 번만 생성/시작 (재로그인 대비)
                    if (_collector == null)
                    {
                        var baseDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                        _collector = new KiwoomAutoTRD.Services.BacktestCollector(baseDir);
                        _collector.Start(KiwoomAutoTRD.Services.StrategyParams.RunId);
                        UiAppend("BacktestCollector START: " + KiwoomAutoTRD.Services.StrategyParams.RunId);
                    }

                    // 2) 이벤트 핸들러는 중복 연결 방지 위해 먼저 해제 후 재연결 (명명 메서드 사용)
                    _kiwoomApi.TradeTick -= KiwoomApi_OnTradeTickToCollector;
                    _kiwoomApi.L1Quote -= KiwoomApi_OnL1QuoteToCollector;

                    _kiwoomApi.TradeTick += KiwoomApi_OnTradeTickToCollector;
                    _kiwoomApi.L1Quote += KiwoomApi_OnL1QuoteToCollector;
                }
                catch (Exception ex)
                {
                    UiAppend("BacktestCollector ERR: " + ex.Message);
                }


                // ★ 조건식 스크린 풀 준비(필드)
                _condPool = ScreenManager.Get("condition");
                _condPool.Clear(axKHOpenAPI1); // 혹시 남은 등록이 있으면 정리


                // [안전가드] 정책 플래그가 true 일 때만 전종목 라이트를 명시적으로 허용
                if (KiwoomAutoTRD.Services.StrategyParams.Realtime.EnableKosdaqLightAll)
                {
                    UiAppend("[REAL] KOSDAQ 전종목 라이트 등록 실행");
                    _kiwoomApi.RegisterKosdaqLightAll();
                    LogRealtimeCount("로그인 직후 초기 등록");
                }
                else
                {
                    UiAppend("[REAL] 조건식 결과 종목만 실시간 등록");
                }

                // 키움에서 설정해둔 종목 거르기 조건식 로드 시작
                _kiwoomApi.LoadAndRunCondition(); 
                //Console.WriteLine($"로그인 성공! 계좌번호= { accounts}");   // 출력 요청 구문

            }
            else
            {
                Console.WriteLine($"로그인 실패 (에러코드: {e.nErrCode})");
                UiAppend($"로그인 실패 (에러코드: {e.nErrCode})");
                // ★ 로그인 성공 즉시 오라클 연결(비동기) 시도
            }
        }
        ///</summary> --- 로그인 컬렉션 END 



        // === textBox5 안전 갱신(항상 UI 스레드) ===
        private void OnTurnoverRankingUpdated(string text)
        {
            try
            {
                if (this.IsHandleCreated)
                {
                    this.BeginInvoke((Action)(() =>
                    {
                        try
                        {
                            // 스로틀은 엔진에서 수행됨. 여기서는 그대로 반영만.
                            textBox5.Text = text ?? string.Empty;
                        }
                        catch { /* 안전 무시 */ }
                    }));
                }
            }
            catch { /* 안전 무시 */ }
        }


        // 조건식 로드 컬랙션 START ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////// 
        // 조건식 로드 결과(성공=1). "RiskFilterStock"를 찾아 SendCondition 실행
        private void AxKHOpenAPI1_OnReceiveConditionVer(object sender, AxKHOpenAPILib._DKHOpenAPIEvents_OnReceiveConditionVerEvent e)
        {
            if (e.lRet != 1)
            {
                UiAppend("조건식 로드 실패: HTS(0150)에서 조건식을 만들고 '서버저장' 해주세요.");
                return;
            }

            UiAppend("조건식 로드 완료");

            string condList = axKHOpenAPI1.GetConditionNameList(); // "인덱스^이름;인덱스^이름;..."
            if (string.IsNullOrWhiteSpace(condList))
            {
                UiAppend("조건식 없음: HTS(0150)에서 '서버저장' 확인 필요");
                return;
            }

            string targetName = "RiskFilterStock";
            int targetIdx = -1;

            var items = condList.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var it in items)
            {
                var p = it.Split('^');
                int idxTmp;
                if (p.Length >= 2 && int.TryParse(p[0], out idxTmp))
                {
                    var name = (p[1] ?? "").Trim();
                    if (string.Equals(name, targetName, StringComparison.OrdinalIgnoreCase))
                    {
                        targetIdx = idxTmp;
                        targetName = name; // 서버 저장 표기 유지
                        break;
                    }
                }
            }

            if (targetIdx < 0)
            {
                UiAppend("조건식 찾기 실패: RiskFilterStock 미존재. 목록 확인 필요");
                UiAppend($"조건식 목록 원문: {condList}");
                return;
            }
            // nSearch=1 : 최초 리스트(TR) 수신 → 이후 편입/이탈은 실시간 이벤트
            axKHOpenAPI1.SendCondition("6000", targetName, targetIdx, 1);
            UiAppend($"조건검색 실행: [{targetName}] (idx={targetIdx})");
        }

        // 조건검색 TR 결과 (처음 실행 시 종목 리스트)
        private void AxKHOpenAPI1_OnReceiveTrCondition(object sender, AxKHOpenAPILib._DKHOpenAPIEvents_OnReceiveTrConditionEvent e)
        {
            // ① 전 종목 리스트 (정규화: A000140 → 000140)
            string[] codes = (e.strCodeList ?? "")
                .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => CodeUtil.NormalizeCode((s ?? "").Trim()))
                .ToArray();

            UiAppend($"조건검색 [{e.strConditionName}] 결과: {codes.Length} 종목"); // Ui창에 출력

            if (codes.Length == 0)
            {
                UiAppend("조건검색 결과 없음(실시간 등록 스킵)");
                return;
            }

            // ② 조건식 전용 스크린 풀 준비 및 이전 등록 정리
            if (_condPool == null) _condPool = ScreenManager.Get("condition");
            _condPool.Clear(axKHOpenAPI1); // 중복 등록/잔여 등록 방지

            // ③  종목 실시간 등록(스크린당 N개 자동 분할: ScreenManager 정책값 사용) (다시 등록됨)

            _condPool.RegisterCodes(axKHOpenAPI1, codes, KiwoomAutoTRD.Services.StrategyParams.Realtime.LightFids);

            UiAppend($"조건식 실시간 등록 완료: {codes.Length}개 (FID={KiwoomAutoTRD.Services.StrategyParams.Realtime.LightFids}) (화면={_condPool.Screens.Count})");
            // ★ 추가: 현재 실시간 구독 수 로깅
            LogRealtimeCount("조건식(TR) 등록 완료");

        }

        // 조건검색 실시간 편입/이탈
        private void AxKHOpenAPI1_OnReceiveRealCondition(object sender, AxKHOpenAPILib._DKHOpenAPIEvents_OnReceiveRealConditionEvent e)
        {
            if (_condPool == null) _condPool = ScreenManager.Get("condition");
            string ty = (e.strType ?? "").Trim();
            string codeNorm = CodeUtil.NormalizeCode(e.sTrCode); // e.sTrCode는 조건식 검색 TR결과에서 넘어오는 (종목코드값)

            if (ty == "I" || ty == "편입")
            {

                _condPool.RegisterCode(axKHOpenAPI1, codeNorm, KiwoomAutoTRD.Services.StrategyParams.Realtime.LightFids);
                UiAppend($"[조건식] 편입 → 등록: {codeNorm}");
                LogRealtimeCount("편입 처리");
            }
            else if (ty == "D" || ty == "이탈")
            {
                _condPool.UnregisterCode(axKHOpenAPI1, codeNorm);
                UiAppend($"[조건식] 이탈 → 해제: {codeNorm}");
                LogRealtimeCount("이탈 처리");
            }
            else
            {
                UiAppend($"조건검색 실시간: {codeNorm} - {e.strType} ({e.strConditionName})");
            }

        }


        // 실시간 정보 불러오는 종목확인 START

        private void LogRealtimeCount(string reason)
        {
            try
            {
                if (_kiwoomApi == null) return;

                var codes = _kiwoomApi.GetRealtimeSubscriptionSnapshot();
                var preview = string.Join(", ", codes.Take(20));
                if (codes.Length > 20) preview += " …";

                UiAppend($"[REAL] {reason} → 현재 실시간 구독 종목 수 = {codes.Length}  (샘플: {preview})");
            }
            catch
            {
                // 안전 무시
            }
        }
        // 실시간 정보 불러오는 종목확인 END 


        // 조건식 로드 컬랙션 END ///////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////// 


        // DBManager가 파싱하기 쉬운 “표준 포맷”으로 큐에 적재 (단일 라인, 키=값)
        private void AxKHOpenAPI1_OnReceiveTrData(object sender, _DKHOpenAPIEvents_OnReceiveTrDataEvent e)
        {
            // DB 표준 라인: DB|TR|rq=<rq>|tr=<tr>|scr=<scr>
            string line = $"DB|TR|rq={e.sRQName}|tr={e.sTrCode}|scr={e.sScrNo}";
            DBManager.Enqueue(line);

            // ★ OPW00001 (예수금상세현황) 수신 시: 예수금/출금가능 파싱해서 DBManager로 전달
            if (e.sTrCode == "OPW00001")
            {
                try
                {
                    string sDeposit = axKHOpenAPI1.GetCommData(e.sTrCode, e.sRQName, 0, "예수금")?.Trim() ?? "0";
                    string sWithdraw = axKHOpenAPI1.GetCommData(e.sTrCode, e.sRQName, 0, "출금가능금액")?.Trim() ?? "0";

                    long deposit = SafeParse.ParseLong(sDeposit);
                    long withdrawable = SafeParse.ParseLong(sWithdraw);

                    UiAppend($"OPW00001 수신: 예수금={deposit:N0}, 출금가능={withdrawable:N0}");

                    string userId = (axKHOpenAPI1.GetLoginInfo("USER_ID") ?? "").Trim();
                    string acc = _kiwoomApi?.AccountNumber ?? "";
                    // 표준: ord_possible=출금가능금액
                    string lineAcc = $"DB|ACCNT|user={userId}|acc={acc}|ord_possible={withdrawable}|deposit={deposit}";
                    DBManager.Enqueue(lineAcc);
                }
                catch { /* 무시 */ }
            }

        }


        // 체잔 수신 이벤트 핸들러 (주문/체결)    
        private void AxKHOpenAPI1_OnReceiveChejanData(object sender, _DKHOpenAPIEvents_OnReceiveChejanDataEvent e)
        {
            // 종목코드 FID = 9001
            string codeRaw = axKHOpenAPI1.GetChejanData(9001) ?? string.Empty;
            string code = CodeUtil.NormalizeCode(codeRaw); // CodeUtil에서 "A000660" → "000660 이런식으로 종목코드값 앞에 A를 스트립 해 온값으로 code로 정의해서 씀)

            // DB 표준 라인 적재
            string kv = BuildChejanKv(e.sFIdList);  // FID별 값 조회(ActiveX는 UI 스레드에서만)
            string line = $"DB|CHEJAN|gubun={e.sGubun}|code={code}|cnt={e.nItemCnt}|kv={kv}";
            DBManager.Enqueue(line);

            // === [추가] 체결/주문 접수 → TradingManager에 연결하여 textBox3/4로 흘려보내기 ===
            try
            {
                // 기본 필드들 안전 파싱
                string sSide = axKHOpenAPI1.GetChejanData(905) ?? "";     // 주문구분: "매수", "매도" 포함
                string side = sSide.IndexOf("매수", StringComparison.Ordinal) >= 0 ? "BUY"
                            : sSide.IndexOf("매도", StringComparison.Ordinal) >= 0 ? "SELL" : "UNK";

                int orderQty = CodeUtil.SafeInt(axKHOpenAPI1.GetChejanData(902)); // 주문수량
                int filledQty = CodeUtil.SafeInt(axKHOpenAPI1.GetChejanData(911)); // 체결량(이번 통지)
                int fillPrice = CodeUtil.SafeInt(axKHOpenAPI1.GetChejanData(910)); // 체결가(이번 통지)
                int unfilled = CodeUtil.SafeInt(axKHOpenAPI1.GetChejanData(906)); // 미체결수량(전체)


                // 주문번호 (키움 환경별로 9203 또는 913로 제공되는 경우가 있어 둘 다 시도)
                string ordNo = axKHOpenAPI1.GetChejanData(9203);
                if (string.IsNullOrWhiteSpace(ordNo)) ordNo = axKHOpenAPI1.GetChejanData(913);

                // 1) 체결 발생
                if (filledQty > 0)
                {
                    if (unfilled > 0)
                    {
                        // 부분체결
                        _tradingManager.OnOrderPartiallyFilled(code, side, filledQty, unfilled, (fillPrice > 0 ? fillPrice : 0));
                    }
                    else
                    {
                        // 전량 체결 또는 남은 수량 0
                        _tradingManager.OnOrderFilled(code, side, filledQty, (fillPrice > 0 ? fillPrice : 0));
                    }
                }
                // 2) 체결은 없고, 주문이 접수/등록만 된 경우(대기)
                else if (!string.IsNullOrWhiteSpace(ordNo) && orderQty > 0)
                {
                    // 접수 시에는 가격 정보를 못 받을 수 있음 → 0 허용(로그 용도)
                    _tradingManager.OnOrderAccepted(code, ordNo, side, orderQty, 0);
                }
            }
            catch
            {
                // UI/주문 로직은 격리: 예외 전파 금지
            }
            // 요약 UI 로그 (좌측 textBox1)
            UiAppend($"체결:{e.sGubun}:{code}");
        }


        #region --- 실시간 수신 모니터링용 디바운스 사전 START ---

        // 실시간 수신 모니터링용 디바운스 사전 /////////////////////////////////////////////////////////////////////////////////////////

        // ★ 종목명 캐시 (GetMasterCodeName 호출 최소화)
        private readonly Dictionary<string, string> _nameCache = new Dictionary<string, string>();

        private string GetNameCached(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "";
            string n;
            if (_nameCache.TryGetValue(code, out n)) return n;
            n = axKHOpenAPI1.GetMasterCodeName(code) ?? "";
            _nameCache[code] = n;
            return n;
        }

        // 실시간 수신 모니터링용
        private void AxKHOpenAPI1_OnReceiveRealData(object sender, _DKHOpenAPIEvents_OnReceiveRealDataEvent e)
        {
            // e.sRealType == "주식체결" 등
            // e.sRealKey == 종목코드 

            // 간단히 현재가/등락률/거래량만 뽑아 로그
            try
            {
                string sPrice = axKHOpenAPI1.GetCommRealData(e.sRealKey, 10)?.Trim();    // 현재가 (sRealType를 sRealKey(종목코드)로 바꿈)
                string sChgRt = axKHOpenAPI1.GetCommRealData(e.sRealKey, 12)?.Trim();    // 등락률
                string sAccVol = axKHOpenAPI1.GetCommRealData(e.sRealKey, 13)?.Trim();  // 누적 거래량
                string sTickVol = axKHOpenAPI1.GetCommRealData(e.sRealKey, 14)?.Trim(); // 누적 거래대금
                // string tickQ = axKHOpenAPI1.GetCommRealData(e.sRealKey, 15)?.Trim();    // 체결량(단건)

                // ── Collector 이벤트에 넘기기 위해 정수 변환(기존 포맷 유지: "+", "-", "," 제거) ──
                int iPrice = 0, iTickQty = 0;
                int.TryParse((sPrice ?? "").Replace("+", "").Replace("-", "").Replace(",", ""), out iPrice);
                int.TryParse((sTickVol ?? "0").Replace("+", "").Replace("-", "").Replace(",", ""), out iTickQty);


                // ── L1 호가 요약(백테스트 재현/품질 가드용) ──
                string sAsk = axKHOpenAPI1.GetCommRealData(e.sRealKey, 27)?.Trim();    // 최우선 매도호가
                string sBid = axKHOpenAPI1.GetCommRealData(e.sRealKey, 28)?.Trim();    // 최우선 매수호가
                string sAskQ = axKHOpenAPI1.GetCommRealData(e.sRealKey, 121)?.Trim();   // 매도 잔량
                string sBidQ = axKHOpenAPI1.GetCommRealData(e.sRealKey, 122)?.Trim();   // 매수 잔량

                int iAsk = 0, iBid = 0, iAskQ = 0, iBidQ = 0;
                double dChgRt = 0;
                int.TryParse((sAsk ?? "").Replace("+", "").Replace("-", "").Replace(",", ""), out iAsk);
                int.TryParse((sBid ?? "").Replace("+", "").Replace("-", "").Replace(",", ""), out iBid);
                int.TryParse((sAskQ ?? "").Replace("+", "").Replace("-", "").Replace(",", ""), out iAskQ);
                int.TryParse((sBidQ ?? "").Replace("+", "").Replace("-", "").Replace(",", ""), out iBidQ);
                double.TryParse((sChgRt ?? "").Replace("%", "").Replace("+", "").Replace("-", ""), out dChgRt);


                string code = e.sRealKey;   // 종목코드
                string name = GetNameCached(code);  // 종목명 조회 : name에다 종목 코드(code)로 가져온 종목명을 넣음

                // ★ 종목명까지 포함하여 textBox1에 출력
                UiAppend($"REAL:{e.sRealType}:{code}({name}) P={sPrice} R={sChgRt} V={sAccVol}");

                // ── TurnoverBurst@v1 엔진 입력: 틱 거래대금 = 가격×체결량(틱) ──
                // 14번 체결량은 부호가 붙을 수 있어 수량은 절대값 사용 권장
                int tickQtyAbs = Math.Abs(iTickQty);
                if (e.sRealType == "주식체결" && iPrice > 0 && tickQtyAbs > 0)
                {
                    // TradingManager로 틱 이벤트 전달 (전략 엔진: 틱-버스트 판단)
                    _tradingManager.OnTradeTick(code, iPrice, tickQtyAbs, DateTime.UtcNow);
                }

            }
            catch
            {
                // 무시 가능
            }
        }

        // 체잔 FID 리스트로부터 "fid:val,fid:val,..." 문자열 생성
        private string BuildChejanKv(string fidList)
        {
            if (string.IsNullOrWhiteSpace(fidList)) return string.Empty;

            var fids = fidList.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new StringBuilder();

            foreach (var f in fids)
            {
                if (!int.TryParse(f.Trim(), out var fid)) continue;
                var val = axKHOpenAPI1.GetChejanData(fid) ?? string.Empty; // UI스레드 안전

                // 구분자 이스케이프(간단 버전)
                val = val.Replace("\\", "\\\\").Replace("|", "\\|").Replace(",", "\\,").Replace(":", "\\:");

                if (sb.Length > 0) sb.Append(",");
                sb.Append(f.Trim()).Append(":").Append(val);
            }
            return sb.ToString();
        }

        // textBox1 UI에 메시지 추가 (스레드 안전하게)
        private void UiAppend(string msg)
        {
            try
            {
                if (textBox1 == null) return;

                if (textBox1.InvokeRequired)
                {
                    textBox1.BeginInvoke(new Action<string>(UiAppend), msg);
                    return;
                }

                const int MAX_LEN = 4000;

                if (textBox1.TextLength > 0)
                    textBox1.AppendText(Environment.NewLine);

                textBox1.AppendText($"[{DateTime.Now:HH:mm:ss}] {msg}");

                if (textBox1.TextLength > MAX_LEN)
                {
                    // 앞부분 제거 (최근 로그 위주로 유지)
                    textBox1.Text = textBox1.Text.Substring(textBox1.TextLength - MAX_LEN);

                    textBox1.SelectionStart = textBox1.TextLength;
                    textBox1.ScrollToCaret();
                }
            }
            catch
            {
                // 무시 가능
            }
        }

        // DEEP 승격 → 목록에 빈 줄 생성
        private void OnDeepPromoted(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            UiTextBox2(() =>
            {
                var name = GetNameCached(code);
                var line = $"{name} ({code}) | 등락률=-- | 거래량=-- | 최우선매수=--";
                _deepLineByCode[code] = line;
                RebuildTextBox2();
            });
        }

        // DEEP 강등 → 목록에서 제거
        private void OnDeepDemoted(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            UiTextBox2(() =>
            {
                if (_deepLineByCode.Remove(code))
                    RebuildTextBox2();
            });
        }

        // DEEP 틱 → 한 줄 갱신
        private void OnDeepTick(string code, double chgRt, int vol, int bestBid)
        {
            // ★ 등락률 캐시 갱신 (추가)
            try
            {
                if (!string.IsNullOrWhiteSpace(code))
                    _chgRtByCode[code] = chgRt;
            }
            catch { /* 무시 */ }

            UiTextBox2(() =>
            {
                var name = GetNameCached(code);
                var line = $"{name} ({code}) | 등락률={chgRt:+0.00;-0.00;0.00}% | 거래량={vol:N0} | 최우선매수={bestBid:N0}";
                _deepLineByCode[code] = line;
                RebuildTextBox2();
            });
        }

        // textBox2 전체를 상태 딕셔너리로부터 재구성
        private void RebuildTextBox2()
        {
            if (textBox2 == null) return;
            // 안정적으로 보기 좋게: 종목명 알파벳/한글 순 정렬 (원하면 등락률 순으로 바꿔도 됨)
            var lines = _deepLineByCode.Values.ToList();
            textBox2.Text = string.Join(Environment.NewLine, lines);
        }

        // UI 스레드 보장 도우미
        private void UiTextBox2(Action a)
        {
            try
            {
                if (textBox2 == null) return;
                if (textBox2.InvokeRequired)
                    textBox2.BeginInvoke(a);
                else
                    a();
            }
            catch { /* 무시 */ }
        }

        // Ui(Action) 헬퍼 사용
        private void Ui(Action a)
        {
            if (InvokeRequired) { BeginInvoke(a); }
            else a();
        }


        #endregion --- 실시간 수신 모니터링용 디바운스 사전 END ---

        // 실시간 틱에서 거래대금 누적 업데이트 (호출은 OnDeepTick, OnRealData 등에서)
        private void UpdateTradeValue(string code, int price, int volume)
        {
            if (string.IsNullOrWhiteSpace(code) || price <= 0 || volume <= 0) return;

            lock (_topValueLock)
            {
                long addVal = (long)price * (long)volume;
                long curVal;
                _tradeValueByCode.TryGetValue(code, out curVal);
                _tradeValueByCode[code] = curVal + addVal;
            }
        }

        // 거래대금 순위 갱신 및 UI 반영
        private void RefreshTopValueList()
        {
            List<KeyValuePair<string, long>> topByValue;
            lock (_topValueLock)
            {
                // 1) 거래대금 상위 후보 추림 (상위 50개 정도로 충분; 필요시 StrategyParams로 옮겨도 됨)
                topByValue = _tradeValueByCode
                    .OrderByDescending(kv => kv.Value)
                    .Take(50)
                    .ToList();

                _top7Codes = topByValue.Select(kv => kv.Key).Take(7).ToList();
            }

            // 2) 후보들은 DEEP 승격 요청(등락률/호가 갱신 촉진)
            foreach (var kv in topByValue)
            {
                if (!_kiwoomApi.IsDeep(kv.Key))
                    _kiwoomApi.PromoteToDeep(kv.Key);
            }

            // 3) 후보를 등락률 순으로 재정렬 → 최종 7개
            var final7 = topByValue
                .Select(kv =>
                {
                    double r;
                    if (!_chgRtByCode.TryGetValue(kv.Key, out r)) r = double.NegativeInfinity; // 등락률 미수집은 후순위
                    return new { Code = kv.Key, TradeVal = kv.Value, ChgRt = r };
                })
                .OrderByDescending(x => x.ChgRt)
                .ThenByDescending(x => x.TradeVal) // 동률이면 거래대금 큰 순
                .Take(7)
                .ToList();

            // 4) UI 출력 (거래대금 + 등락률)
            UiTextBox5(() =>
            {
                var sb = new StringBuilder();
                sb.AppendLine("거래대금 TOP→등락률 정렬 7 (실시간)");
                int rank = 1;
                foreach (var x in final7)
                {
                    var name = GetNameCached(x.Code);
                    sb.AppendLine($"{rank}. {name} ({x.Code}) - {x.TradeVal / 1_000_000d:N1}백만 | 등락률 {x.ChgRt:+0.00;-0.00;0.00}%");
                    rank++;
                }
                textBox5.Text = sb.ToString();
            });
        }

        // textBox5 UI 스레드 보장 헬퍼
        private void UiTextBox5(Action a)
        {
            try
            {
                if (textBox5 == null) return;
                if (textBox5.InvokeRequired)
                    textBox5.BeginInvoke(a);
                else
                    a();
            }
            catch { /* 무시 */ }
        }



        // === (2) 체결 틱 핸들러 (이벤트에 붙일 명명 메서드) ===
        private void KiwoomApi_TradeTick(string code, int price, int tickQty, DateTime tsUtc)
        {
            // 거래대금 = price * tickQty
            UpdateTradeValue(code, price, tickQty);
        }


        // --- [연동] KiwoomApi 이벤트로 연결 ---
        private void HookTradeValueEvents()
        {
            // 두 인스턴스 중 살아있는 쪽 모두 구독 (중복 방지)
            if (_kiwoomApi == null) return;

            // 중복 구독 방지
            if (!_tradeValueHooked)
            {
                // 안전 차원에서 한 번 떼고 다시 붙여도 됨(첫 실행 시에는 효과 없음)
                _kiwoomApi.TradeTick -= KiwoomApi_TradeTick;
                _kiwoomApi.TradeTick += KiwoomApi_TradeTick;
                _tradeValueHooked = true;
            }

            // 1초마다 순위 새로고침 (필드 타이머로 유지)
            if (_tradeValueTimer == null)
            {
                _tradeValueTimer = new System.Windows.Forms.Timer();
                _tradeValueTimer.Interval = 1000;
                _tradeValueTimer.Tick += (s, e) => RefreshTopValueList();
                _tradeValueTimer.Start();
            }

        }



        #region --- 수집기 재로그인 시 활성화 되는 코드 START ----
        private void KiwoomApi_OnTradeTickToCollector(string code, int price, int qty, DateTime tsUtc)
        {
            _collector?.IngressTradeTick(code, price, qty, tsUtc);
        }

        private void KiwoomApi_OnL1QuoteToCollector(string code, int last, int bid, int ask, int bidQty, int askQty, double chgRt)
        {
            _collector?.IngressQuoteL1(code, last, bid, ask, bidQty, askQty, chgRt);
        }
        #endregion --- 수집기 재로그인 시 활성화 되는 코드 END ----




        #region // --- 스레드 관리 START ---
        // ★ 재진입 방지
        private bool _isClosing = false;

        // 폼 닫기 시 리소스 정리
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_isClosing) { base.OnFormClosing(e); return; }
            _isClosing = true;


            try
            {
                if (_kiwoomApi != null)
                {
                    _kiwoomApi.TradeTick -= KiwoomApi_OnTradeTickToCollector;
                    _kiwoomApi.L1Quote -= KiwoomApi_OnL1QuoteToCollector;
                }
            }
            catch { /* no-op */ }

            // (A) 거래대금 이벤트/타이머 먼저 정리 — 위쪽 간단 OnFormClosing 내용을 병합
            try
            {
                if (_kiwoomApi != null && _tradeValueHooked)
                {
                    _kiwoomApi.TradeTick -= KiwoomApi_TradeTick;
                    _tradeValueHooked = false;
                }
            }
            catch { /* 무시 */ }

            try
            {
                if (_tradeValueTimer != null)
                {
                    _tradeValueTimer.Stop();
                    _tradeValueTimer = null;
                }
            }
            catch { /* 무시 */ }

            // (B) 기존 포괄 정리 로직 유지
            try
            {
                axKHOpenAPI1.OnReceiveRealData -= AxKHOpenAPI1_OnReceiveRealData;
                axKHOpenAPI1.OnReceiveChejanData -= AxKHOpenAPI1_OnReceiveChejanData;
                axKHOpenAPI1.OnReceiveTrData -= AxKHOpenAPI1_OnReceiveTrData;
                axKHOpenAPI1.OnReceiveConditionVer -= AxKHOpenAPI1_OnReceiveConditionVer;
                axKHOpenAPI1.OnReceiveTrCondition -= AxKHOpenAPI1_OnReceiveTrCondition;
                axKHOpenAPI1.OnReceiveRealCondition -= AxKHOpenAPI1_OnReceiveRealCondition;
                axKHOpenAPI1.OnEventConnect -= AxKHOpenAPI1_OnEventConnect;
            }
            catch { /* 무시 가능 */ }

            try { _kiwoomApi?.UnregisterAllRealtime(); } catch { }
            try { _condPool?.Clear(axKHOpenAPI1); } catch { }
            SafeRealRemoveAll();

            try
            {
                if (eventQueue != null && !eventQueue.IsAddingCompleted)
                    eventQueue.CompleteAdding();
            }
            catch { }

            try { dbManager?.Dispose(); } catch { }
            try { DBManager.CloseShared(); } catch { }
            try { _collector?.Dispose(); } catch { }

            base.OnFormClosing(e);
        }


        // ActiveX 핸들이 살아있을 때만 ALL 해제 시도
        private void SafeRealRemoveAll()
        {
            try
            {
                if (axKHOpenAPI1 == null) return;
                if (axKHOpenAPI1.IsDisposed) return;
                if (!axKHOpenAPI1.IsHandleCreated) return;

                // UI 스레드 보장: OnFormClosing은 UI 스레드이므로 직접 호출 OK
                axKHOpenAPI1.SetRealRemove("ALL", "ALL");
            }
            catch
            {
                // 종료 경로 예외 무시 (핸들 하강 중일 수 있음)
            }
        }

        // 폼 종료 시 리소스 해제
        private void Form1_FormClosed(object sender, FormClosedEventArgs e)
        {
            try { if (_tradingManager != null) _tradingManager.Dispose(); } catch { }
        }
        #endregion --- 스레드 관리 END ---


        
        #region --- 핸들러 컬랙션   START ---
        // 텍스트박스의 TextChanged 이벤트 핸들러 예시
        private void textBox1_TextChanged(object sender, EventArgs e)
        {
        }
        // 버튼 클릭 이벤트 핸들러 예시
        private void button1_Click(object sender, EventArgs e)
        {
        }
        private void label1_Click(object sender, EventArgs e)
        {
        }

        private void Form1_Load(object sender, EventArgs e)
        {
        }

        private void textBox1_TextChanged_1(object sender, EventArgs e)
        {
        }

        private void textBox2_TextChanged(object sender, EventArgs e)
        {
        }
        private void label2_Click(object sender, EventArgs e)
        {
        }

        private void label3_Click(object sender, EventArgs e)
        {
        }

        private void d_Click(object sender, EventArgs e)
        {
        }

        #endregion --- 핸들러 컬랙션   END ---

        private void label4_Click(object sender, EventArgs e)
        {

        }

        private void textBox5_TextChanged(object sender, EventArgs e)
        {

        }
    }
}



#region
#endregion
