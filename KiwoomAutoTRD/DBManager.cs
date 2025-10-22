using Oracle.ManagedDataAccess.Client;
using System;
using System.Data;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KiwoomAutoTRD.Common;


namespace KiwoomAutoTRD
{
    internal sealed class DBManager : IDisposable
    {

        // === [가정] 기존 필드/큐/스레드 존재 ===
        private static readonly BlockingCollection<string> _queue =
            new BlockingCollection<string>(new ConcurrentQueue<string>());
        //private static OracleConnection _conn = null; // Form1에서 초기화했다고 가정

        //private readonly BlockingCollection<string> _eventQueue;
        private CancellationTokenSource _cts;
        private Task _consumerTask;
        private bool _started;

        private static Oracle.ManagedDataAccess.Client.OracleConnection _sharedConn;
        private static readonly object _connLock = new object();

        // === 파라미터 없는 생성자 추가 초기화 ===
        public DBManager()
        {
            // null일 수 있으나 "미할당" 상태는 아님    //10월20일 오류아닌경고해결(추가 구문) _conn = _sharedConn; 
        }

        // === 오라클및 공유 커넥션 관리 ===
        public static bool TryOpenShared(string connStr, out string error)
        {
            error = null;
            lock (_connLock)
            {
                try
                {
                    // 기존 커넥션 정리
                    if (_sharedConn != null)
                    {
                        if (_sharedConn.State != System.Data.ConnectionState.Closed) _sharedConn.Close();
                        _sharedConn.Dispose();
                        _sharedConn = null;
                    }

                    var c = new Oracle.ManagedDataAccess.Client.OracleConnection(connStr);
                    c.Open();                // 여기서 OracleException 가능
                    _sharedConn = c;         // 공유 커넥션 교체
                    return true;
                }
                catch (Oracle.ManagedDataAccess.Client.OracleException oex)
                {
                    error = $"ORA-{oex.Number} {oex.Message}";
                    return false;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    return false;
                }
            }
        }

        public static void CloseShared()
        {
            lock (_connLock)
            {
                try
                {
                    if (_sharedConn != null)
                    {
                        if (_sharedConn.State != System.Data.ConnectionState.Closed) _sharedConn.Close();
                        _sharedConn.Dispose();
                    }
                }
                catch { /* 종료 경로: 무시 */ }
                finally
                {
                    _sharedConn = null;
                }
            }
            // ※ 기존 업서트/소비 루틴에서 _sharedConn 사용하도록 되어있지 않다면,
            //    null 체크 후 _sharedConn 사용하도록 한 줄 보완해 주세요.
        }


        // 호출부에서 new DBManager("...") 형태로 사용 중이므로 단일 인자 생성자를 제공합니다.
        public DBManager(BlockingCollection<string> eventQueue)
        {
            if (eventQueue == null) throw new ArgumentNullException(nameof(eventQueue));
        }

        // DBManager 시작 (큐 소비 시작)
        public void Start()
        {
            if (_started) return;                    // ★ 중복 시작 방지
            _cts = new CancellationTokenSource();
            _consumerTask = Task.Run(() => ConsumeQueue(_cts.Token));
            _started = true;
        }

        public void Stop()
        {
            if (!_started) return;                   // ★ 중복 종료 방지
            _cts?.Cancel();
            try { _consumerTask?.Wait(); } catch { }
            _cts = null;                             // ★ 정리
            _consumerTask = null;                    // ★ 정리
            _started = false;
        }

        // public void Dispose()
        // {
        //     Stop();
        // }

        public void Dispose() => Stop();

        // === 단일 생산자 진입점(어디서든 호출) ===
        public static void Enqueue(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            _queue.Add(line);
        }


        private void ConsumeQueue(CancellationToken token)
        {
            try
            {
                foreach (var msg in _queue.GetConsumingEnumerable(token))
                {
                    try { HandleMessage(msg); }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DBManager] HandleMessage error: {ex.Message} | Raw={msg}");
                    }
                }
            }
            catch (OperationCanceledException) { /* 정상 종료 */ }
            catch (Exception ex) { Console.WriteLine($"[DBManager] ConsumeQueue fatal: {ex.Message}"); }
        }


        private void HandleMessage(string msg)
        {
            if (string.IsNullOrWhiteSpace(msg)) return;

            try
            {
                // 1) 주문 라인: DB|ORD|kind=...|code=...|qty=...|price=...|strat=...|ver=...|run=...|tag=...
                if (msg.StartsWith("DB|ORD|", StringComparison.OrdinalIgnoreCase))
                {
                    // 접두부("DB|ORD|")를 제거하여 K=V 파싱
                    var kv = ParseKeyValues(msg.Substring("DB|ORD|".Length));
                    UpsertTbOrdLst(kv);  // ← 오라클 INSERT
                    return;
                }

                if (msg.StartsWith("DB|CHEJAN|", StringComparison.OrdinalIgnoreCase))
                {
                    // payload 검사
                    var payload = msg.Substring("DB|CHEJAN|".Length);
                    // 신규 포맷 우선
                    if (payload.IndexOf("side=", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        payload.IndexOf("code=", StringComparison.OrdinalIgnoreCase) >= 0 &&
                        payload.IndexOf("qty=", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var kvNew = ParseKeyValues(payload);
                        var side = Get(kvNew, "side");
                        var code = Get(kvNew, "code");
                        var qty = ToInt(Get(kvNew, "qty"));
                        var price = ToInt(Get(kvNew, "price"));

                        if (!string.IsNullOrEmpty(side) && !string.IsNullOrEmpty(code) && qty > 0)
                        {
                            UpsertTbChegyulLst(kvNew); // 신규 포맷 INSERT
                            Console.WriteLine($"[DB/CHEJAN][NEW] side={side} code={code} qty={qty} price={price}");

                            return;
                        }
                        // 필수 누락 시 레거시로 폴백
                    }
                    var dict = ParseKeyValues(payload);
                    var kvRaw = Get(dict, "kv"); // dict.Get("kv")와 동일 효과
                    var kvMap = ParseKvPairs(kvRaw); // 기존 헬퍼 사용 (프로젝트 내 존재)
                    Console.WriteLine($"[DB/CHEJAN][LEGACY] gubun={Get(dict, "gubun")} code={Get(dict, "code")} cnt={ToInt(Get(dict, "cnt"))} kv_count={kvMap.Count}");
                    
                    return;
                }

                if (msg.StartsWith("DB|TR|", StringComparison.OrdinalIgnoreCase))
                {
                    var dict = ParseKeyValues(msg.Substring("DB|TR|".Length));
                    Console.WriteLine($"[DB/TR] rq={Get(dict, "rq")} tr={Get(dict, "tr")} scr={Get(dict, "scr")}");
                    return;
                }

                if (msg.StartsWith("DB|ACCNT|", StringComparison.OrdinalIgnoreCase))
                {
                    var dict = ParseKeyValues(msg.Substring("DB|ACCNT|".Length));
                    var user = Get(dict, "user"); if (string.IsNullOrEmpty(user)) user = "ats";
                    var acc = Get(dict, "acc");
                    var ordPossible = ToInt(Get(dict, "ord_possible"));
                    var depositStr = Get(dict, "deposit"); if (string.IsNullOrEmpty(depositStr)) depositStr = "0";

                    Console.WriteLine($"[DB/ACCNT] user={user} acc={acc} ord_possible={ordPossible} (deposit={depositStr})");

                    try
                    {
                        // ① 기존: 주문가능/출금가능 반영
                        UpsertTbAccnt(user, acc, ordPossible);

                        // ② 예수금/출금가능 동시 MERGE (살린 배선)
                        var deposit = ToLong(depositStr);
                        UpsertAccntInfo(acc, deposit, ordPossible);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DB/ACCNT] UPSERT ERR: {ex.Message}");
                        Console.WriteLine("https://docs.oracle.com/error-help/db/ora-00904/");
                    }
                    return;
                }

                if (msg.StartsWith("DB|ACCNT_INFO|", StringComparison.OrdinalIgnoreCase))
                {
                    // 필요 시 기존 구현
                    return;
                }
                // 미분류: 기존 로깅 유지
                Console.WriteLine("[DB][UNHANDLED] " + msg);
            }
            catch (Exception ex)
            {
                // 소비 루프 보호
                Console.WriteLine("[DB][HANDLE][ERR] " + ex.Message + " | " + msg);
            }
        }


        private static long ToLong(string s)
        {
            long v; return long.TryParse((s ?? "0").Trim(), out v) ? v : 0L;
        }


        // === [추가] Key=Value 파서 (공용) ===
        private static Dictionary<string, string> ParseKeyValues(string payload)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(payload)) return map;
            var parts = payload.Split('|');
            foreach (var part in parts)
            {
                var idx = part.IndexOf('=');
                if (idx <= 0) continue;
                var k = part.Substring(0, idx).Trim();
                var v = part.Substring(idx + 1).Trim();
                map[k] = v;
            }
            return map;
        }

        private static Dictionary<int, string> ParseKvPairs(string kv)
        {
            var map = new Dictionary<int, string>();
            if (string.IsNullOrWhiteSpace(kv)) return map;
            foreach (var pair in kv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = pair.IndexOf(':');
                if (idx <= 0) continue;
                var kStr = pair.Substring(0, idx).Trim();
                var vStr = pair.Substring(idx + 1);
                if (int.TryParse(kStr, out var fid))
                    map[fid] = Unescape(vStr);
            }
            return map;
        }


        // === [추가] TB_ORD_LST 반영 ===
        // 컬럼 예시: ORD_DTM, KIND, CODE, QTY, PRICE, STRATEGY_ID, STRATEGY_VER, RUN_ID, STRATEGY_TAG
        private void UpsertTbOrdLst(Dictionary<string, string> kv)
        {
            if (_sharedConn == null || _sharedConn.State != System.Data.ConnectionState.Open)
            {
                Console.WriteLine("[DB][ORD] 연결 안 됨");
                return;
            }

            var kind = Get(kv, "kind");
            var code = Get(kv, "code");
            var qty = ToInt(Get(kv, "qty"));
            var price = ToInt(Get(kv, "price"));
            //  var sid = Get(kv, "strat");
            //  var sver = ToInt(Get(kv, "ver"));
            var runid = Get(kv, "run");
            var stag = Get(kv, "tag");

            using (var cmd = _sharedConn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO TB_ORD_LST (
    ORD_DTM, ORD_GB, ORD_NO, ORG_ORD_NO,
    JONGMOK_CD, JONGMOK_NM,
    ORD_PRICE, ORD_STOCK_CNT, ORD_AMT,
    USER_ID, ACCNT_NO, REF_DT,
    INST_ID, INST_DTM
) VALUES (
    :ORD_DTM, :ORD_GB, :ORD_NO, :ORG_ORD_NO,
    :JONGMOK_CD, :JONGMOK_NM,
    :ORD_PRICE, :ORD_STOCK_CNT, :ORD_AMT,
    :USER_ID, :ACCNT_NO, :REF_DT,
    :INST_ID, :INST_DTM
)";
                cmd.BindByName = true;

                cmd.Parameters.Add(new OracleParameter("ORD_DTM", DateTime.Now.ToString("yyyyMMddHHmmss")));
                cmd.Parameters.Add(new OracleParameter("ORD_GB", kind == "SELL" ? "S" : "B"));
                cmd.Parameters.Add(new OracleParameter("ORD_NO", runid ?? "GEN_" + DateTime.Now.Ticks));
                cmd.Parameters.Add(new OracleParameter("ORG_ORD_NO", "0"));
                cmd.Parameters.Add(new OracleParameter("JONGMOK_CD", code));
                cmd.Parameters.Add(new OracleParameter("JONGMOK_NM", stag ?? ""));
                cmd.Parameters.Add(new OracleParameter("ORD_PRICE", price));
                cmd.Parameters.Add(new OracleParameter("ORD_STOCK_CNT", qty));
                cmd.Parameters.Add(new OracleParameter("ORD_AMT", qty * price));
                cmd.Parameters.Add(new OracleParameter("USER_ID", "ats"));
                cmd.Parameters.Add(new OracleParameter("ACCNT_NO", "8109429211"));
                cmd.Parameters.Add(new OracleParameter("REF_DT", DateTime.Now.ToString("yyyyMMdd")));
                cmd.Parameters.Add(new OracleParameter("INST_ID", "ats"));
                cmd.Parameters.Add(new OracleParameter("INST_DTM", DateTime.Now));

                try
                {
                    var n = cmd.ExecuteNonQuery();
                    Console.WriteLine($"[DB][ORD] ins rows={n} code={code} kind={kind} qty={qty}@{price} [{stag}]");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[DB][ORD][ERR] " + ex.Message);
                }
            }
        }


        // === [추가] TB_CHEGYUL_LST 반영 ===
        // 컬럼 예시: FILL_DTM, SIDE, CODE, QTY, PRICE, STRATEGY_ID, STRATEGY_VER, RUN_ID, STRATEGY_TAG
        private void UpsertTbChegyulLst(Dictionary<string, string> kv)
        {
            if (_sharedConn == null || _sharedConn.State != System.Data.ConnectionState.Open)
            {
                Console.WriteLine("[DB][FILL] 연결 안 됨");
                return;
            }

            var side = Get(kv, "side");
            var code = Get(kv, "code");
            var qty = ToInt(Get(kv, "qty"));
            var price = ToInt(Get(kv, "price"));
            //var sid = Get(kv, "strat");
            //var sver = ToInt(Get(kv, "ver"));
            var runid = Get(kv, "run");
            var stag = Get(kv, "tag");

            // 🔹 테이블 필수값(스키마) 보강: 없으면 안전 기본값
            var user = Get(kv, "user") ?? "ats";
            var acc = Get(kv, "acc") ?? "8109429211";
            var refDt = Get(kv, "ref_dt") ?? DateTime.Now.ToString("yyyyMMdd");
            var name = Get(kv, "name") ?? "";                        // JONGMOK_NM
            var chegyulGb = Get(kv, "chegyul_gb") ?? "0";
            var chegyulNo = ToInt(Get(kv, "chegyul_no"));
            var chegyulDtm = Get(kv, "chegyul_dtm") ?? DateTime.Now.ToString("yyyyMMddHHmmss");

            using (var cmd = _sharedConn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO TB_CHEGYUL_LST
(USER_ID, ACCNT_NO, REF_DT, JONGMOK_CD, JONGMOK_NM,
 ORD_GB, ORD_NO, CHEGYUL_GB, CHEGYUL_NO,
 CHEGYUL_PRICE, CHEGYUL_STOCK_CNT, CHEGYUL_AMT,
 CHEGYUL_DTM, INST_ID, INST_DTM)
VALUES
(:USER_ID, :ACCNT_NO, :REF_DT, :JONGMOK_CD, :JONGMOK_NM,
 :ORD_GB, :ORD_NO, :CHEGYUL_GB, :CHEGYUL_NO,
 :CHEGYUL_PRICE, :CHEGYUL_STOCK_CNT, :CHEGYUL_AMT,
 :CHEGYUL_DTM, :INST_ID, :INST_DTM)";
                cmd.BindByName = true;

                cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("USER_ID", user));
                cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("ACCNT_NO", acc));
                cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("REF_DT", refDt));
                cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("JONGMOK_CD", code));
                cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("JONGMOK_NM", name));
                cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("ORD_GB", side == "SELL" ? "S" : "B"));
                cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("ORD_NO", string.IsNullOrEmpty(runid) ? ("GEN_" + DateTime.Now.Ticks) : runid));
                cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("CHEGYUL_GB", chegyulGb));
                cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("CHEGYUL_NO", chegyulNo));
                cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("CHEGYUL_PRICE", price));
                cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("CHEGYUL_STOCK_CNT", qty));
                cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("CHEGYUL_AMT", price * qty));
                cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("CHEGYUL_DTM", chegyulDtm)); // CHAR(14) 포맷
                cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("INST_ID", "ats"));
                cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("INST_DTM", DateTime.Now));

                try
                {
                    var n = cmd.ExecuteNonQuery();
                    Console.WriteLine($"[DB][FILL] ins rows={n} code={code} side={side} {qty}@{price} [{stag}]");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[DB][FILL][ERR] " + ex.Message);
                }
            }
        }

        // === 소형 헬퍼 ===
        private static string Get(Dictionary<string, string> kv, string key)
        {
            string v; return kv != null && kv.TryGetValue(key, out v) ? v : string.Empty;
        }
        private static int ToInt(string s)
        {
            int v; return int.TryParse(s, out v) ? v : 0;
        }


        // ★ TB_ACCNT MERGE: ORD_POSSIBLE_AMT만 사용 (DEPOSIT 컬럼 사용 금지)
        private void UpsertTbAccnt(string user, string acc, int ordPossible)
        {
            // TODO: 실제 환경의 연결 취득 로직 사용
            // 아래는 예시 — Form1와 동일 커넥션 문자열을 공용 설정으로 빼두면 더 좋음
            var connStr = "User Id=ats;Password=35Gidam!@57;Data Source=localhost:1521/FREEPDB1;";

            using (var conn = new Oracle.ManagedDataAccess.Client.OracleConnection(connStr))
            using (var cmd = new Oracle.ManagedDataAccess.Client.OracleCommand())
            {
                conn.Open();
                cmd.Connection = conn;
                cmd.CommandType = System.Data.CommandType.Text;

                // user, acc, 오늘날짜로 키 고정. 존재시 update, 없으면 insert
                cmd.CommandText =
                    @"merge into TB_ACCNT a
              using(
                select :user_id as user_id, :accnt_no as accnt_no, to_char(sysdate,'yyyymmdd') as ref_dt from dual
              ) b
              on (a.user_id=b.user_id and a.accnt_no=b.accnt_no and a.ref_dt=b.ref_dt)
              when matched then update set
                   ORD_POSSIBLE_AMT = :ord_possible,
                   UPDT_DTM = SYSDATE,
                   UPDT_ID  = 'ats'
              when not matched then insert
                   (USER_ID, ACCNT_NO, REF_DT, ORD_POSSIBLE_AMT, INST_DTM, INST_ID)
              values(:user_id, :accnt_no, to_char(sysdate,'yyyymmdd'), :ord_possible, SYSDATE, 'ats')";

                cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("user_id", user));
                cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("accnt_no", acc));
                cmd.Parameters.Add(new Oracle.ManagedDataAccess.Client.OracleParameter("ord_possible", ordPossible));

                cmd.ExecuteNonQuery();
            }
        }


        private long ParseLong(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            long v;
            return long.TryParse(s.Replace(",", "").Replace("+", "").Replace("-", ""), out v) ? v : 0;
        }


        private static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Replace("\\\\", "\u0001");
            s = s.Replace("\\|", "|").Replace("\\,", ",").Replace("\\:", ":");
            s = s.Replace("\u0001", "\\");
            return s;
        }


        // ===  전략 태깅 표준 라인 ===
        public static void EnqueueOrderMeta(
            string kind, string code, int qty, int price,
            string strategyId, int strategyVersion, string runId, string canonicalTag)
        {
            var sb = new StringBuilder(128);
            sb.Append("DB|ORD");
            sb.Append("|kind=").Append(kind);
            sb.Append("|code=").Append(code);
            sb.Append("|qty=").Append(qty);
            sb.Append("|price=").Append(price);
            sb.Append("|strat=").Append(strategyId);
            sb.Append("|ver=").Append(strategyVersion);
            sb.Append("|run=").Append(runId);
            sb.Append("|tag=").Append(canonicalTag);
            Enqueue(sb.ToString());
        }


        // === [공통 유틸: 문자열 → 숫자 파싱] ===
        // WHY: Form1, KiwoomApi 등에서 중복 사용되는 TryParse/Replace 패턴을 통일
        internal static class DbUtil
        {
            public static long ParseLong(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return 0;
                long v;
                return long.TryParse(
                    s.Replace(",", "").Replace("+", "").Replace("-", ""),
                    out v) ? v : 0;
            }

            public static int ParseInt(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return 0;
                int v;
                return int.TryParse(
                    s.Replace(",", "").Replace("+", "").Replace("-", ""),
                    out v) ? v : 0;
            }
        }

        // ★ OPW00001 값 반영: TB_ACCNT (또는 TB_ACCNT_INFO 확장컬럼) 업서트
        private void UpsertAccntInfo(string accNo, long deposit, long withdrawable)
        {
            if (string.IsNullOrWhiteSpace(accNo)) return;

            if (_sharedConn == null || _sharedConn.State != System.Data.ConnectionState.Open)
            {
                Console.WriteLine("[DB/ACCNT] 공유 연결 없음");
                return;
            }

            const string sql = @"
MERGE INTO TB_ACCNT t
USING (
    SELECT :USER_ID AS USER_ID, :ACCNT_NO AS ACCNT_NO, :REF_DT AS REF_DT FROM dual
) s
ON (t.USER_ID = s.USER_ID AND t.ACCNT_NO = s.ACCNT_NO AND t.REF_DT = s.REF_DT)
WHEN MATCHED THEN UPDATE SET
    t.DEPOSIT      = :DEPOSIT,
    t.WITHDRAWABLE = :WITHDRAWABLE,
    t.UPDT_ID      = :UPDT_ID,
    t.UPDT_DTM     = SYSDATE
WHEN NOT MATCHED THEN
INSERT (USER_ID, ACCNT_NO, REF_DT, DEPOSIT, WITHDRAWABLE, INST_ID, INST_DTM)
VALUES (:USER_ID, :ACCNT_NO, :REF_DT, :DEPOSIT, :WITHDRAWABLE, :UPDT_ID, SYSDATE)";

            using (var cmd = _sharedConn.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.BindByName = true;

                // TODO: 추후 StrategyParams로 이관 권장(단일 소스)
                cmd.Parameters.Add("USER_ID", "ats");
                cmd.Parameters.Add("ACCNT_NO", accNo);
                cmd.Parameters.Add("REF_DT", DateTime.Now.ToString("yyyyMMdd"));
                cmd.Parameters.Add("DEPOSIT", deposit);
                cmd.Parameters.Add("WITHDRAWABLE", withdrawable);
                cmd.Parameters.Add("UPDT_ID", "DBMAN");

                var _ = cmd.ExecuteNonQuery(); // discard로 IDE0059 경고 회피
            }
        }

       
    }

}

