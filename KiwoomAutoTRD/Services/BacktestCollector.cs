using KiwoomAutoTRD.Adapters;
using KiwoomAutoTRD.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KiwoomAutoTRD.Services
{
    /// <summary>
    /// 초단타 스캘핑 백테스트용 "인프로세스" 실데이터 수집기 (Non-intrusive)
    /// - UI 스레드(KH Ingress)의 이벤트를 동일 시퀀스로 캡처
    /// - 파일 I/O는 독립 Writer 스레드가 배치 처리(무블로킹), per-code 파일로 분할
    /// - RUN_ID 단위 회전 + 세션 메타 기록
    /// - 체결틱(TICKS), L1호가(QUOTE) 동시 수집 (Phase2: DECISION/ORDER/EXEC 확장 지점 포함)
    /// </summary>
    internal sealed class BacktestCollector : IDisposable
    {
        // --------- 설정(필요 시 StrategyParams에서 핫리로드 가능) ----------
        private const int WRITE_BUFFER_BYTES = 1 << 16;   // 64KB
        private const int FLUSH_INTERVAL_MS = 250;        // 주기적 Flush
        private const int MAX_QUEUE_LEN = 200_000;        // 백프레셔 보호(드롭 카운트 기록)
        private const int MAX_QUOTE_HZ_PER_CODE = 50;     // L1 호가 샘플링 상한(최신우선)

        // --------- 상태 ----------
        private readonly object _sync = new object();
        // private readonly Func<DateTime> _nowKst = () => DateTime.UtcNow.AddHours(9);

        private volatile bool _started;
        private string _baseDir;
        private string _runDir;
        public string CurrentRunDir { get { return _runDir; } }



        private DateTime _nextMidnightKst;  // 날짜 넘어가면 RunId 값 자동 갱신호출

        private readonly object _lock = new object();
        private long _seqIngress; // Ingress 시퀀스
        private bool _resumeMode;   // 이어쓰기
        private CancellationTokenSource _cts; // Writer 스레드 제어

        // per-code 라이터
        private readonly ConcurrentDictionary<string, StreamWriter> _tickWriters =
            new ConcurrentDictionary<string, StreamWriter>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, StreamWriter> _quoteWriters =
            new ConcurrentDictionary<string, StreamWriter>(StringComparer.OrdinalIgnoreCase);

        // 무블로킹 큐(고속): 이벤트를 문자열 라인으로 변환하지 않고 "작업"으로 넘김 -> Writer 스레드가 문자열화+쓰기
        private readonly ConcurrentQueue<ILogJob> _jobs = new ConcurrentQueue<ILogJob>();
        private int _queueLen;
        private long _dropped; // 백프레셔로 드롭된 이벤트 수

        // 코드별 L1 샘플링 타임스탬프(KST millis)
        private readonly ConcurrentDictionary<string, int> _quoteLastMs = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // ---- 외부 이벤트 소스 연결용 델리게이트 (기존 시스템과 느슨한 결합) ----
        public Action<Action> UiMarshal { get; set; } // 필요 시 UI 마샬링(기본 null)

        // ---- 공개 속성 (대시보드/로그용) ----
        public string RunId { get; private set; }

        // 현지 시각 함수가 없으면 간단 구현
        private static DateTime _nowKst()
        {
            try { return TimeZoneInfo.ConvertTime(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Korea Standard Time")); }
            catch { return DateTime.UtcNow.AddHours(9); }
        }

        // 다음 자정(KST) 계산 유틸
        private static DateTime CalcNextMidnightKst()
        {
            var now = _nowKst();
            return now.Date.AddDays(1); // 내일 00:00:00 (KST)
        }

        public long DroppedCount => Interlocked.Read(ref _dropped);

        public BacktestCollector(string baseDir = null)
        {
            _baseDir = string.IsNullOrWhiteSpace(baseDir)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data")
                : baseDir;
        }

        // ---- 시작/정지 ----
        public void Start(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) throw new ArgumentException("runId is empty");
            lock (_sync)
            {
                if (_started) return;

                Directory.CreateDirectory(_baseDir);
                _runDir = Path.Combine(_baseDir, runId);
                Directory.CreateDirectory(_runDir);

                RunId = runId;
                _cts = new CancellationTokenSource();

                // ✅ 경로 확인용 강력 로그
                try { TradingEvents.RaiseTradeInfo("[COLLECTOR] start run=" + RunId + " dir=" + _runDir); } catch { }

                _resumeMode = StrategyParams.Collector.EnableResume && HasAnyDataFiles(_runDir);

                if (_resumeMode)
                {
                    long lastSeq = TryRecoverLastSeq(_runDir, StrategyParams.Collector.TailScanBytes);
                    if (lastSeq >= 0) Interlocked.Exchange(ref _seqIngress, lastSeq + 1);
                    else Interlocked.Exchange(ref _seqIngress, 0);
                    SafeLog("[COLLECTOR] RESUME run=" + RunId + " startSeq=" + _seqIngress);
                }
                else
                {
                    Interlocked.Exchange(ref _seqIngress, 0);
                }

                // ✅ 메타 파일은 설정 여부와 무관하게 한 번은 남기도록 보강(문제 추적용)
                try { WriteSessionMeta(_resumeMode); }
                catch (Exception ex)
                {
                    try { TradingEvents.RaiseTradeInfo("[COLLECTOR] write_meta.fail " + ex.Message + " dir=" + _runDir); } catch { }
                }

                _nextMidnightKst = CalcNextMidnightKst();
                _started = true;

                Task.Factory.StartNew(WriterLoop, _cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                if (!_started) return;
                _started = false;
                try { _cts.Cancel(); } catch { }
                try { FlushCloseAll(); } catch { }
            }
        }

        public void Dispose() => Stop();

        // -----------------    시스템 끊겼을때 재시작 부분 수정하고 이어받기   START   ---------------------------------------------------
        public static IEnumerable<string> EnumerateCsvLinesSafe(string csvPath, bool includeHeader, Action<int, string> onSkip)
        {
            if (string.IsNullOrEmpty(csvPath) || !File.Exists(csvPath))
                yield break;

            using (var fs = new FileStream(csvPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs, Encoding.UTF8, true))
            {
                string header = sr.ReadLine();
                if (header == null) yield break;

                int expectedDelims = CountDelimiter(header, StrategyParams.CollectorSafety.CsvDelimiter);
                if (includeHeader) yield return header;

                int lineNo = 1; // header = 1
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    lineNo++;
                    if (!StrategyParams.CollectorSafety.SkipMalformedCsvLine || IsCsvDataLineValid(line, expectedDelims))
                    {
                        yield return line;
                    }
                    else
                    {
                        if (onSkip != null) onSkip(lineNo, line);
                    }
                }
            }
        }

        private static bool IsCsvDataLineValid(string line, int expectedDelims)
        {
            if (string.IsNullOrWhiteSpace(line)) return false;
            if (expectedDelims < 0) return true; // 헤더 없으면 판단 불가 → 스킵하지 않음
            var delims = CountDelimiter(line, StrategyParams.CollectorSafety.CsvDelimiter);
            if (delims != expectedDelims) return false;       // 컬럼 개수 불일치 → 깨진 줄로 간주
            if (line.EndsWith(StrategyParams.CollectorSafety.CsvDelimiter.ToString()))
                return false;                                  // 끝 컬럼 비어있는 반쪽 라인 방지(스키마에 따라 완화 가능)
            return true;
        }

        private static int CountDelimiter(string s, char delim)
        {
            int cnt = 0;
            for (int i = 0; i < s.Length; i++)
                if (s[i] == delim) cnt++;
            return cnt;
        }


        // -----------------    시스템 끊겼을때 재시작 부분 수정하고 이어받기   END ---------------------------------------------------





        // ---- 인입(=UI 스레드에서 즉시 호출, 변환 가벼움 유지) ----
        // 체결틱 인입(시그니처는 상위 이벤트에 맞춰 호출하세요)
        public void IngressTradeTick(string code, int price, int qty, DateTime? tsKhUtc)
        {
            if (!_started || string.IsNullOrWhiteSpace(code) || price <= 0 || qty <= 0) return;

            var seq = Interlocked.Increment(ref _seqIngress);
            var kst = _nowKst();
            var job = TradeJob.PoolRent(code, price, qty, kst, tsKhUtc, seq);

            Enqueue(job);
        }

        // L1 호가 인입(가격/호가요약) — 최신우선, 코드별 50Hz 상한
        public void IngressQuoteL1(string code, int last, int bid, int ask, int bidQty, int askQty, double chgRt)
        {
            if (!_started || string.IsNullOrWhiteSpace(code) || last <= 0) return;

            // 간단한 50Hz 샘플링(최신우선)
            var now = _nowKst();
            var ms = now.Millisecond + now.Second * 1000; // 0~59999
            var lastMs = _quoteLastMs.GetOrAdd(code, -1000);
            if ((ms - lastMs) < (1000 / MAX_QUOTE_HZ_PER_CODE) && (ms - lastMs) >= 0)
            {
                return; // 드랍(샘플링): 최신만 남기기 위해 약간의 손실 허용 (스캘핑 재현에 충분)
            }
            _quoteLastMs[code] = ms;

            var seq = Interlocked.Increment(ref _seqIngress);
            var job = QuoteJob.PoolRent(code, last, bid, ask, bidQty, askQty, chgRt, now, seq);

            Enqueue(job);
        }

        // Phase2 확장: 의사결정/주문/체결 로그 Hook
        public void IngressDecision(string strategyTag, string code, string signal, string reason, string inputsHash)
        {
            if (!_started) return;
            var seq = Interlocked.Increment(ref _seqIngress);
            var kst = _nowKst();
            var job = DecisionJob.PoolRent(strategyTag, code, signal, reason, inputsHash, kst, seq);
            Enqueue(job);
        }

        public void IngressOrder(string orderId, string strategyTag, string code, string side, int qty, string priceType, int limitPrice)
        {
            if (!_started) return;
            var seq = Interlocked.Increment(ref _seqIngress);
            var kst = _nowKst();
            var job = OrderJob.PoolRent(orderId, strategyTag, code, side, qty, priceType, limitPrice, kst, seq);
            Enqueue(job);
        }

        public void IngressExecution(string orderId, string execId, string code, int price, int qty, double fee, double tax)
        {
            if (!_started) return;
            var seq = Interlocked.Increment(ref _seqIngress);
            var kst = _nowKst();
            var job = ExecJob.PoolRent(orderId, execId, code, price, qty, fee, tax, kst, seq);
            Enqueue(job);
        }

        // ---- 내부: 큐 삽입(백프레셔) ----
        private void Enqueue(ILogJob job)
        {
            var len = Interlocked.Increment(ref _queueLen);
            if (len > MAX_QUEUE_LEN)
            {
                // 과도한 유량 → 가장 오래된 것부터 버린다고 가정(간단 구현: 새 job을 폐기)
                Interlocked.Decrement(ref _queueLen);
                Interlocked.Increment(ref _dropped);
                job.ReturnToPool();
                return;
            }
            _jobs.Enqueue(job);
        }

        // ---- Writer 스레드 ----
        private void WriterLoop()
        {
            var nextFlush = Environment.TickCount + FLUSH_INTERVAL_MS;
            while (!_cts.IsCancellationRequested)
            {
                // 자정 도달 시 롤오버 시도
                TryMidnightRollover();

                ILogJob job;
                if (_jobs.TryDequeue(out job))
                {
                    Interlocked.Decrement(ref _queueLen);
                    try { job.WriteTo(this); }
                    catch { /* I/O 오류 무중단 */ }
                    finally { job.ReturnToPool(); }
                }
                else
                {
                    // 큐가 비었으면 잠깐 쉼
                    Thread.Sleep(1);
                }

                var now = Environment.TickCount;
                if (now >= nextFlush)
                {
                    SafeFlushAll();
                    nextFlush = now + FLUSH_INTERVAL_MS;
                }
            }
            // 종료 시 최종 플러시
            SafeFlushAll();
        }

        //  ------- 하루 지나는거 체크하는 구문
        private void TryMidnightRollover()
        {
            if (!StrategyParams.Collector.AutoRolloverAtMidnight) return;

            var nowKst = _nowKst();
            if (nowKst < _nextMidnightKst) return; // 아직 자정 전이면 아무 것도 안 함

            // 자정 도달(또는 지남) → 새 날짜로 롤오버
            var newDateTag = nowKst.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            DoRolloverToNewDate(newDateTag);

            // 다음 자정 재예약
            _nextMidnightKst = CalcNextMidnightKst();
        }

        private void DoRolloverToNewDate(string newDateTag)
        {
            lock (_sync)
            {
                if (!_started) return;

                // 1) 기존 파일 핸들 정리
                FlushCloseAll();

                // 2) 새 RunId 구성: YYYYMMDD-01 (단순 정책)
                var newRunId = newDateTag + "-01";

                // 3) 새 폴더 준비
                Directory.CreateDirectory(_baseDir);
                var newDir = Path.Combine(_baseDir, newRunId);
                Directory.CreateDirectory(newDir);

                // 4) 내부 상태 전환
                _runDir = newDir;
                RunId = newRunId;

                // 외부 참조 일관성(있다면) 유지: StrategyParams.RunId 업데이트 시도
                try { StrategyParams.RunId = newRunId; } catch { }

                // 5) 이어쓰기 여부/시퀀스 복구
                _resumeMode = StrategyParams.Collector.EnableResume && HasAnyDataFiles(_runDir);
                if (_resumeMode)
                {
                    long lastSeq = TryRecoverLastSeq(_runDir, StrategyParams.Collector.TailScanBytes);
                    if (lastSeq >= 0) Interlocked.Exchange(ref _seqIngress, lastSeq + 1);
                    else Interlocked.Exchange(ref _seqIngress, 0);
                }
                else
                {
                    Interlocked.Exchange(ref _seqIngress, 0);
                }

                // 6) 세션 메타 기록(StartedAtKST 새로)
                if (StrategyParams.Collector.WriteSessionMeta)
                    WriteSessionMeta(false);

                SafeLog("[COLLECTOR] ROLLOVER newRun=" + RunId + " startSeq=" + _seqIngress);
            }
        }


        // ---- 파일 Writer/Flush/Close ----

        internal void WriteTick(long seq, DateTime tsKst, DateTime? tsKhUtc, string code, int price, int qty)
        {
            var sw = GetTickWriter(code);
            // KH UTC 시각이 없으면 공란
            var tsKh = tsKhUtc.HasValue ? tsKhUtc.Value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) : "";
            sw.WriteLine($"{seq},{tsKst:yyyy-MM-dd HH:mm:ss.fff},{tsKh},{code},{price},{qty}");
        }

        internal void WriteQuote(long seq, DateTime tsKst, string code, int last, int bid, int ask, int bidQty, int askQty, double chgRt)
        {
            var sw = GetQuoteWriter(code);
            sw.WriteLine($"{seq},{tsKst:yyyy-MM-dd HH:mm:ss.fff},{code},{last},{bid},{ask},{bidQty},{askQty},{chgRt.ToString("0.####", CultureInfo.InvariantCulture)}");
        }

        private void SafeFlushAll()
        {
            foreach (var kv in _tickWriters) { try { kv.Value.Flush(); } catch { } }
            foreach (var kv in _quoteWriters) { try { kv.Value.Flush(); } catch { } }
        }

        private void FlushCloseAll()
        {
            SafeFlushAll();
            foreach (var kv in _tickWriters) { try { kv.Value.Dispose(); } catch { } }
            foreach (var kv in _quoteWriters) { try { kv.Value.Dispose(); } catch { } }
            _tickWriters.Clear();
            _quoteWriters.Clear();
        }

        private void WriteSessionMeta(bool resumed)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_runDir))
                    throw new InvalidOperationException("runDir not initialized");

                var metaPath = Path.Combine(_runDir, $"SESSION_{RunId}.txt");
                var sb = new StringBuilder();
                if (!File.Exists(metaPath))
                {
                    sb.AppendLine("RUN_ID=" + RunId);
                    sb.AppendLine("StartedAtKST=" + _nowKst().ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    try
                    {
                        sb.AppendLine("StrategyId=" + StrategyParams.StrategyId);
                        sb.AppendLine("StrategyVersion=" + StrategyParams.StrategyVersion);
                        sb.AppendLine("StrategyTag=" + StrategyParams.CanonicalTag);
                    }
                    catch { }
                    File.WriteAllText(metaPath, sb.ToString(), new UTF8Encoding(false));

                    // ✅ 생성 확인 로그
                    try { TradingEvents.RaiseTradeInfo("[COLLECTOR] meta.created " + metaPath); } catch { }
                }
                else if (resumed)
                {
                    // 재시작 이력만 추가
                    File.AppendAllText(metaPath,
                        "ResumedAtKST=" + _nowKst().ToString("yyyy-MM-dd HH:mm:ss.fff") + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                // ✅ 예외 내용을 남겨서 경로/권한 문제 즉시 파악
                try { TradingEvents.RaiseTradeInfo("[COLLECTOR] write_meta.fail " + ex.Message + " dir=" + _runDir); } catch { }
            }
        }


        // ---- CSV Writer: Append & 헤더 중복 방지 ----

        private StreamWriter GetTickWriter(string code)
        {
            return _tickWriters.GetOrAdd(code, c =>
            {
                var path = Path.Combine(_runDir, "TICKS_" + c + ".csv");
                bool append = _resumeMode;
                var fs = new FileStream(
                    path,
                    append ? FileMode.OpenOrCreate : FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    WRITE_BUFFER_BYTES,
                    FileOptions.WriteThrough);

                if (append) fs.Seek(0, SeekOrigin.End);
                var sw = new StreamWriter(fs, new UTF8Encoding(false), WRITE_BUFFER_BYTES);

                // 빈 파일일 때만 헤더 1회 기록
                if (!append && !StrategyParams.Collector.WriteHeaderOnlyWhenEmpty)
                {
                    sw.WriteLine("# seq,ts_kst,ts_kh_utc,code,price,qty");
                }
                else if (StrategyParams.Collector.WriteHeaderOnlyWhenEmpty && fs.Length == 0)
                {
                    sw.WriteLine("# seq,ts_kst,ts_kh_utc,code,price,qty");
                }
                return sw;
            });
        }

        private StreamWriter GetQuoteWriter(string code)
        {
            return _quoteWriters.GetOrAdd(code, c =>
            {
                var path = Path.Combine(_runDir, "QUOTE_" + c + ".csv");
                bool append = _resumeMode;
                var fs = new FileStream(
                    path,
                    append ? FileMode.OpenOrCreate : FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    WRITE_BUFFER_BYTES,
                    FileOptions.WriteThrough);

                if (append) fs.Seek(0, SeekOrigin.End);
                var sw = new StreamWriter(fs, new UTF8Encoding(false), WRITE_BUFFER_BYTES);

                if (!append && !StrategyParams.Collector.WriteHeaderOnlyWhenEmpty)
                {
                    sw.WriteLine("# seq,ts_kst,code,last,bid,ask,bidQty,askQty,chgRt");
                }
                else if (StrategyParams.Collector.WriteHeaderOnlyWhenEmpty && fs.Length == 0)
                {
                    sw.WriteLine("# seq,ts_kst,code,last,bid,ask,bidQty,askQty,chgRt");
                }
                return sw;
            });
        }

        // ---- 이어쓰기 판단/복구 유틸 ----
        private static bool HasAnyDataFiles(string runDir)
        {
            try
            {
                return Directory.EnumerateFiles(runDir, "TICKS_*.csv").Any()
                    || Directory.EnumerateFiles(runDir, "QUOTE_*.csv").Any();
            }
            catch { return false; }
        }

        private static long TryRecoverLastSeq(string runDir, int tailBytes)
        {
            long maxSeq = -1;
            try
            {
                foreach (var file in Directory.EnumerateFiles(runDir, "*.csv"))
                {
                    long s = ReadLastSeqFromTail(file, tailBytes);
                    if (s > maxSeq) maxSeq = s;
                }
            }
            catch { /* ignore */ }
            return maxSeq;
        }

        private static long ReadLastSeqFromTail(string path, int tailBytes)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length == 0) return -1;
                    int readBytes = (int)Math.Min(fs.Length, (long)tailBytes);
                    fs.Seek(-readBytes, SeekOrigin.End);
                    var buf = new byte[readBytes];
                    fs.Read(buf, 0, readBytes);


                    var text = Encoding.UTF8.GetString(buf);
                    var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

                    // === [추가] 헤더 기반 검증 준비 ===
                    int expectedDelims = -1;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var ln = lines[i];
                        if (!string.IsNullOrEmpty(ln) && ln[0] == '#')
                        {
                            expectedDelims = CountDelimiter(ln, StrategyParams.CollectorSafety.CsvDelimiter);
                            break;
                        }
                    }

                    // === [수정] 뒤에서 앞으로 스캔: 깨진 꼬리 라인/형식 불일치 라인 스킵 ===
                    for (int i = lines.Length - 1; i >= 0; i--)
                    {
                        var line = lines[i];
                        if (string.IsNullOrEmpty(line) || line[0] == '#') continue;

                        // 헤더를 tail에서 찾았으면 컬럼 검증 적용
                        if (expectedDelims >= 0 && !IsCsvDataLineValid(line, expectedDelims)) continue;

                        int comma = line.IndexOf(',');
                        if (comma <= 0) continue;

                        long seq;
                        if (long.TryParse(line.Substring(0, comma), NumberStyles.Any, CultureInfo.InvariantCulture, out seq))
                            return seq;
                    }
                }
            }
            catch { /* ignore */ }
            return -1;
        }

        private void SafeLog(string msg)
        {
            try { Console.WriteLine(msg); } catch { }
        }


        // ---- Job 인터페이스 & 풀 (할당 최소화) ----
        private interface ILogJob
        {
            void WriteTo(BacktestCollector c);
            void Reset();
            void ReturnToPool();
        }

        private sealed class TradeJob : ILogJob
        {
            private static readonly ConcurrentBag<TradeJob> _pool = new ConcurrentBag<TradeJob>();

            public string Code; public int Price; public int Qty;
            public DateTime TsKst; public DateTime? TsKhUtc; public long Seq;

            public static TradeJob PoolRent(string code, int price, int qty, DateTime tsKst, DateTime? tsKhUtc, long seq)
            {
                TradeJob j; if (!_pool.TryTake(out j)) j = new TradeJob();
                j.Code = code; j.Price = price; j.Qty = qty; j.TsKst = tsKst; j.TsKhUtc = tsKhUtc; j.Seq = seq;
                return j;
            }
            public void WriteTo(BacktestCollector c) => c.WriteTick(Seq, TsKst, TsKhUtc, Code, Price, Qty);
            public void Reset() { Code = null; Price = 0; Qty = 0; TsKst = default(DateTime); TsKhUtc = null; Seq = 0; }
            public void ReturnToPool() { Reset(); _pool.Add(this); }
        }

        private sealed class QuoteJob : ILogJob
        {
            private static readonly ConcurrentBag<QuoteJob> _pool = new ConcurrentBag<QuoteJob>();

            public string Code; public int Last; public int Bid; public int Ask;
            public int BidQty; public int AskQty; public double ChgRt; public DateTime TsKst; public long Seq;

            public static QuoteJob PoolRent(string code, int last, int bid, int ask, int bidQty, int askQty, double chgRt, DateTime tsKst, long seq)
            {
                QuoteJob j; if (!_pool.TryTake(out j)) j = new QuoteJob();
                j.Code = code; j.Last = last; j.Bid = bid; j.Ask = ask; j.BidQty = bidQty; j.AskQty = askQty; j.ChgRt = chgRt; j.TsKst = tsKst; j.Seq = seq;
                return j;
            }
            public void WriteTo(BacktestCollector c) => c.WriteQuote(Seq, TsKst, Code, Last, Bid, Ask, BidQty, AskQty, ChgRt);
            public void Reset() { Code = null; Last = Bid = Ask = BidQty = AskQty = 0; ChgRt = 0; TsKst = default(DateTime); Seq = 0; }
            public void ReturnToPool() { Reset(); _pool.Add(this); }
        }

        private sealed class DecisionJob : ILogJob
        {
            private static readonly ConcurrentBag<DecisionJob> _pool = new ConcurrentBag<DecisionJob>();
            public string StrategyTag, Code, Signal, Reason, InputsHash;
            public DateTime TsKst; public long Seq;

            public static DecisionJob PoolRent(string tag, string code, string signal, string reason, string inputsHash, DateTime tsKst, long seq)
            {
                DecisionJob j; if (!_pool.TryTake(out j)) j = new DecisionJob();
                j.StrategyTag = tag; j.Code = code; j.Signal = signal; j.Reason = reason; j.InputsHash = inputsHash; j.TsKst = tsKst; j.Seq = seq;
                return j;
            }

            public void WriteTo(BacktestCollector c)
            {
                // Phase2: 결정 로그 파일(JSONL)로 기록 — 현재는 세션 메타에 집중, 필요 시 파일 열기
                var path = Path.Combine(c._runDir, "DECISION.log");
                using (var sw = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)))
                {
                    // JSONL (따옴표 이스케이프 최소화 — 간단 직렬화)
                    sw.Write("{\"seq\":"); sw.Write(Seq);
                    sw.Write(",\"ts_kst\":\""); sw.Write(TsKst.ToString("yyyy-MM-dd HH:mm:ss.fff")); sw.Write("\"");
                    sw.Write(",\"tag\":\""); sw.Write(StrategyTag); sw.Write("\"");
                    sw.Write(",\"code\":\""); sw.Write(Code); sw.Write("\"");
                    sw.Write(",\"signal\":\""); sw.Write(Signal); sw.Write("\"");
                    sw.Write(",\"reason\":\""); sw.Write(Reason); sw.Write("\"");
                    sw.Write(",\"inputs\":\""); sw.Write(InputsHash); sw.Write("\"");
                    sw.WriteLine("}");
                }
            }
            public void Reset() { StrategyTag = Code = Signal = Reason = InputsHash = null; TsKst = default(DateTime); Seq = 0; }
            public void ReturnToPool() { Reset(); _pool.Add(this); }
        }

        private sealed class OrderJob : ILogJob
        {
            private static readonly ConcurrentBag<OrderJob> _pool = new ConcurrentBag<OrderJob>();
            public string OrderId, StrategyTag, Code, Side, PriceType;
            public int Qty, LimitPrice; public DateTime TsKst; public long Seq;

            public static OrderJob PoolRent(string orderId, string tag, string code, string side, int qty, string priceType, int limitPrice, DateTime tsKst, long seq)
            {
                OrderJob j; if (!_pool.TryTake(out j)) j = new OrderJob();
                j.OrderId = orderId; j.StrategyTag = tag; j.Code = code; j.Side = side; j.Qty = qty; j.PriceType = priceType; j.LimitPrice = limitPrice; j.TsKst = tsKst; j.Seq = seq;
                return j;
            }

            public void WriteTo(BacktestCollector c)
            {
                var path = Path.Combine(c._runDir, "ORDERS.log");
                using (var sw = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)))
                {
                    sw.Write("{\"seq\":"); sw.Write(Seq);
                    sw.Write(",\"ts_kst\":\""); sw.Write(TsKst.ToString("yyyy-MM-dd HH:mm:ss.fff")); sw.Write("\"");
                    sw.Write(",\"orderId\":\""); sw.Write(OrderId); sw.Write("\"");
                    sw.Write(",\"tag\":\""); sw.Write(StrategyTag); sw.Write("\"");
                    sw.Write(",\"code\":\""); sw.Write(Code); sw.Write("\"");
                    sw.Write(",\"side\":\""); sw.Write(Side); sw.Write("\"");
                    sw.Write(",\"qty\":"); sw.Write(Qty);
                    sw.Write(",\"priceType\":\""); sw.Write(PriceType); sw.Write("\"");
                    sw.Write(",\"limit\":"); sw.Write(LimitPrice);
                    sw.WriteLine("}");
                }
            }
            public void Reset() { OrderId = StrategyTag = Code = Side = PriceType = null; Qty = LimitPrice = 0; TsKst = default(DateTime); Seq = 0; }
            public void ReturnToPool() { Reset(); _pool.Add(this); }
        }

        private sealed class ExecJob : ILogJob
        {
            private static readonly ConcurrentBag<ExecJob> _pool = new ConcurrentBag<ExecJob>();
            public string OrderId, ExecId, Code; public int Price, Qty; public double Fee, Tax; public DateTime TsKst; public long Seq;

            public static ExecJob PoolRent(string orderId, string execId, string code, int price, int qty, double fee, double tax, DateTime tsKst, long seq)
            {
                ExecJob j; if (!_pool.TryTake(out j)) j = new ExecJob();
                j.OrderId = orderId; j.ExecId = execId; j.Code = code; j.Price = price; j.Qty = qty; j.Fee = fee; j.Tax = tax; j.TsKst = tsKst; j.Seq = seq;
                return j;
            }

            public void WriteTo(BacktestCollector c)
            {
                var path = Path.Combine(c._runDir, "EXEC.log");
                using (var sw = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)))
                {
                    sw.Write("{\"seq\":"); sw.Write(Seq);
                    sw.Write(",\"ts_kst\":\""); sw.Write(TsKst.ToString("yyyy-MM-dd HH:mm:ss.fff")); sw.Write("\"");
                    sw.Write(",\"orderId\":\""); sw.Write(OrderId); sw.Write("\"");
                    sw.Write(",\"execId\":\""); sw.Write(ExecId); sw.Write("\"");
                    sw.Write(",\"code\":\""); sw.Write(Code); sw.Write("\"");
                    sw.Write(",\"price\":"); sw.Write(Price);
                    sw.Write(",\"qty\":"); sw.Write(Qty);
                    sw.Write(",\"fee\":"); sw.Write(Fee.ToString("0.####", CultureInfo.InvariantCulture));
                    sw.Write(",\"tax\":"); sw.Write(Tax.ToString("0.####", CultureInfo.InvariantCulture));
                    sw.WriteLine("}");
                }
            }
            public void Reset() { OrderId = ExecId = Code = null; Price = Qty = 0; Fee = Tax = 0; TsKst = default(DateTime); Seq = 0; }
            public void ReturnToPool() { Reset(); _pool.Add(this); }
        }
    }
}
