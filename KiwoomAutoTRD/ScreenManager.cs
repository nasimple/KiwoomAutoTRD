//version 250831
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AxKHOpenAPILib;

namespace KiwoomAutoTRD
{
    // 예약 결과 (스크린 번호/해당 스크린의 첫 등록 여부)
    public struct ScreenSlot
    {
        public string Screen;
        public bool IsFirstOnScreen;
    }

    // 스크린 풀(범위/용량/사용중 스크린 + 코드→스크린 매핑 관리)
    public sealed class ScreenPool
    {
        private readonly object _lock = new object();
        public string Name { get; private set; }
        public int Base { get; private set; }
        public int End { get; private set; }
        public int MaxPerScreen { get; private set; }

        private int _current = -1;
        private readonly HashSet<int> _inUse = new HashSet<int>();
        private readonly Dictionary<int, int> _counts = new Dictionary<int, int>();
        private readonly Dictionary<string, int> _codeToScreen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public ScreenPool(string name, int @base, int end, int maxPerScreen)
        {
            Name = name; Base = @base; End = end; MaxPerScreen = maxPerScreen;
        }

        private int AcquireScreenUnsafe()
        {
            if (_current < 0 || !_counts.ContainsKey(_current) || _counts[_current] >= MaxPerScreen)
            {
                int next = (_current < 0) ? Base : _current + 1;
                while (next <= End && _inUse.Contains(next)) next++;

                if (next > End)
                    throw new InvalidOperationException($"Screen exhausted: {Name} base={Base}, end={End}");

                _current = next;
                if (!_counts.ContainsKey(_current)) _counts[_current] = 0;
                _inUse.Add(_current);  // ← 기존 동작과 동일하게 '사용 중' 표기
            }
            return _current;
        }


        // 슬롯만 예약 (SetRealReg는 호출측에서)
        public ScreenSlot ReserveSlot()
        {
            lock (_lock)
            {
                int scr = AcquireScreenUnsafe();
                bool first = (_counts[scr] == 0);
                _counts[scr]++;
                return new ScreenSlot { Screen = scr.ToString(), IsFirstOnScreen = first };
            }
        }

        // 권장: 등록까지 한 번에 (매핑/카운트 반영 포함)
        public void RegisterCode(AxKHOpenAPI api, string code, string fids)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            code = code.Trim();

            if (string.IsNullOrWhiteSpace(fids))
                fids = KiwoomAutoTRD.Services.StrategyParams.Realtime.LightFids;

            lock (_lock)
            {
                if (_codeToScreen.ContainsKey(code)) return;

                int scr = AcquireScreenUnsafe();
                bool first = (_counts[scr] == 0);
                string opt = first ? "1" : "0";

                api.SetRealReg(scr.ToString(), code, fids, opt);

                _counts[scr] = _counts.ContainsKey(scr) ? _counts[scr] + 1 : 1;
                _inUse.Add(scr);
                _codeToScreen[code] = scr;
            }
        }

        public void RegisterCodes(AxKHOpenAPI api, IEnumerable<string> codes, string fids)
        {
            if (codes == null) return;
            foreach (var c in codes) RegisterCode(api, c, fids);
        }

        // 개별 해제(부분 해제)
        public void UnregisterCode(AxKHOpenAPI api, string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            code = code.Trim();

            lock (_lock)
            {
                int scr;
                if (!_codeToScreen.TryGetValue(code, out scr)) return;

                api.SetRealRemove(scr.ToString(), code);
                _codeToScreen.Remove(code);

                if (_counts.ContainsKey(scr))
                {
                    _counts[scr] = Math.Max(0, _counts[scr] - 1);
                    if (_counts[scr] == 0)
                    {
                        api.SetRealRemove(scr.ToString(), "ALL");
                        _inUse.Remove(scr);
                        _counts.Remove(scr);
                        _current = -1;
                    }
                }
            }
        }

        // 이 풀 전체 해제
        public void Clear(AxKHOpenAPI api)
        {
            lock (_lock)
            {
                foreach (var kv in _codeToScreen.ToList())
                    api.SetRealRemove(kv.Value.ToString(), kv.Key);
                foreach (var n in _inUse.ToList())
                    api.SetRealRemove(n.ToString(), "ALL");

                _codeToScreen.Clear();
                _inUse.Clear();
                _counts.Clear();
                _current = -1;
            }
        }

        // 코드가 등록된 스크린 조회(안전한 공식 접근자)
        public bool TryGetScreen(string code, out string screen)
        {
            screen = null;
            if (string.IsNullOrWhiteSpace(code)) return false;

            lock (_lock)
            {
                int scr;
                if (_codeToScreen.TryGetValue(code.Trim(), out scr))
                {
                    screen = scr.ToString();
                    return true;
                }
            }
            return false;
        }

        // 필요 시 편의용(없으면 null)
        public string GetScreenForCode(string code)
        {
            string scr;
            return TryGetScreen(code, out scr) ? scr : null;
        }

        public IReadOnlyList<string> Screens
        {
            get { lock (_lock) { return new List<string>(_inUse.Select(x => x.ToString())); } }
        }



        // --- 실시간 정보 불러오는거 확인 하기   START
        public string[] GetRegisteredCodesSnapshot()
        {
            lock (_lock)
            {
                return _codeToScreen.Keys.ToArray();
            }
        }

        public int GetRegisteredCount()
        {
            lock (_lock)
            {
                return _codeToScreen.Count;
            }
        }
        // --- 실시간 정보 불러오는거 확인 하기   END


    }





    // 중앙 정책/풀 등록소 모든 스크린을 큰 범위로 나누고 등록하는 역할
    public static class ScreenManager
    {
        private static readonly Dictionary<string, ScreenPool> _pools =
            new Dictionary<string, ScreenPool>(StringComparer.OrdinalIgnoreCase)
            {
                 { "start_stop",      new ScreenPool("start_stop",      1000, 1099, 50)  }, // 장시작/종료
                { "my_info",         new ScreenPool("my_info",         2000, 2099, 10)  },  // 계좌/내정보
                { "real_stock",      new ScreenPool("real_stock",      5000, 5599, 80)  }, // LIGHT 전용
                { "order_stock",     new ScreenPool("order_stock",     5600, 5799, 80)  },  // 주문/체결
                { "condition",       new ScreenPool("condition",       6000, 6999, 80)  },  // 조건식 응답전용
                { "real_stock_deep", new ScreenPool("real_stock_deep", 5800, 5899, 80)  }, // ★ DEEP 전용
            };

        public static ScreenPool Get(string name)
        {
            ScreenPool sp;
            if (!_pools.TryGetValue(name, out sp))
                throw new ArgumentException($"Screen pool '{name}' 정의 없음");
            return sp;
        }

        public static IEnumerable<ScreenPool> AllPools { get { return _pools.Values; } }
    }

}

