using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KiwoomAutoTRD
{
    internal static class Program
    {
        /// <summary>
        /// 해당 애플리케이션의 주 진입점입니다.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            var main = new Form1();

            // 전역 예외/종료 훅: 비정상 종료 시에도 정리 보장
            Application.ThreadException += (s, e) => { try { main?.Close(); } catch { } };
            AppDomain.CurrentDomain.UnhandledException += (s, e) => { try { main?.Close(); } catch { } };
            AppDomain.CurrentDomain.ProcessExit += (s, e) => { try { /* FormClosing 타도록 Close() */ main?.Close(); } catch { } };

            Application.Run(main);



        }
    }
}
