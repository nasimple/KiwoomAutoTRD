//version 250831
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KiwoomAutoTRD.Services
{

    //  키움 SendOrder 호출 레이트 리밋(초당 4회, 분당 90회).

    internal class OrderRateLimiter
    {
        private static readonly object _lock = new object();
        private static readonly Queue<DateTime> _perSec = new Queue<DateTime>(8);
        private static readonly Queue<DateTime> _perMin = new Queue<DateTime>(128);

        private const int PER_SEC_LIMIT = 4;   // 요청: 초당 4회
        private const int PER_MIN_LIMIT = 90;  // 요청: 분당 90회

        /// <summary>호출 전 승인 시도. 승인되면 true, 한도 초과면 false.</summary>
        public static bool TryAcquire()
        {
            var now = DateTime.UtcNow;
            var secAgo = now.AddSeconds(-1);
            var minAgo = now.AddMinutes(-1);

            lock (_lock)
            {
                // 만료 제거(1초 창)
                while (_perSec.Count > 0 && _perSec.Peek() < secAgo) _perSec.Dequeue();
                // 만료 제거(60초 창)
                while (_perMin.Count > 0 && _perMin.Peek() < minAgo) _perMin.Dequeue();

                if (_perSec.Count >= PER_SEC_LIMIT) return false;
                if (_perMin.Count >= PER_MIN_LIMIT) return false;

                _perSec.Enqueue(now);
                _perMin.Enqueue(now);
                return true;
            }
        }
    }
}
