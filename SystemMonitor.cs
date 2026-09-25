using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace StutterFix
{
    // 실시간 모니터가 보여 줄 CPU / GPU / VRAM / RAM 사용량을 모은다.
    //
    // 측정 자체가 끊김을 만들면 안 되므로 전부 작업 스레드에서 1초에 한 번 읽고, 메인 스레드는 값만 가져간다.
    //   CPU  : GetSystemTimes(전체), GetProcessTimes(게임)
    //   RAM  : GlobalMemoryStatusEx(전체), K32GetProcessMemoryInfo(게임)
    //   GPU  : 작업 관리자와 같은 성능 카운터(PDH) "GPU Engine" 의 3D 엔진 사용률
    //   VRAM : PDH "GPU Adapter Memory" (전체), "GPU Process Memory" (게임, 전용/공유)
    //          게임의 공유 메모리가 크면 VRAM 이 넘쳐 시스템 램으로 밀려난 것이다(곡 중 멈춤의 원인이었다).
    // 창이 꺼져 있으면 스레드도 멈춘다.
    internal static class SystemMonitor
    {
        // 결과 (작업 스레드가 쓰고 메인 스레드가 읽는다. 값 하나씩이라 잠금 없이 둔다)
        internal static volatile float CpuTotal = -1, CpuGame = -1, Gpu3D = -1, GpuGame = -1;
        internal static volatile float VramUsedMB = -1, VramGameMB = -1, SharedGameMB = -1;
        internal static volatile float RamLoad = -1, RamTotalMB = -1, RamGameMB = -1;
        internal static volatile bool GpuAvailable;

        private static Thread thread;
        private static volatile bool running;
        internal static volatile bool Keep;   // 모니터를 숨겨도 계속 읽는다 (큰 이미지 줄이기 자동이 VRAM 을 볼 때)

        internal static void Start()
        {
            if (running) return;
            running = true;
            thread = new Thread(Loop) { IsBackground = true, Name = "StutterFix.Monitor", Priority = ThreadPriority.BelowNormal };
            thread.Start();
        }

        internal static void Stop()
        {
            running = false;
            var t = thread;
            thread = null;
            if (t != null) { try { t.Join(2000); } catch { } }
        }

        private static void Loop()
        {
            var pdh = new Pdh();
            try
            {
                long pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                string tag = "pid_" + pid + "_";
                ulong lastIdle = 0, lastKernel = 0, lastUser = 0, lastProc = 0;
                long lastTicks = 0;
                bool gpuOk = pdh.Open();

                while (running)
                {
                    // CPU
                    ulong idle, kernel, user;
                    if (GetSystemTimes(out idle, out kernel, out user))
                    {
                        ulong di = idle - lastIdle, dk = kernel - lastKernel, du = user - lastUser;
                        if (lastKernel != 0 && dk + du > 0) CpuTotal = Clamp100(100.0 * (dk + du - di) / (dk + du));
                        lastIdle = idle; lastKernel = kernel; lastUser = user;
                    }
                    ulong c, e, pk, pu;
                    long now = DateTime.UtcNow.Ticks;
                    if (GetProcessTimes(GetCurrentProcess(), out c, out e, out pk, out pu))
                    {
                        ulong proc = pk + pu;
                        if (lastTicks != 0 && now > lastTicks)
                            CpuGame = Clamp100(100.0 * (proc - lastProc) / ((now - lastTicks) * (double)Environment.ProcessorCount));
                        lastProc = proc; lastTicks = now;
                    }

                    // RAM
                    var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
                    if (GlobalMemoryStatusEx(ref ms)) { RamLoad = ms.dwMemoryLoad; RamTotalMB = ms.ullTotalPhys / 1048576f; }
                    var pmc = new PROCESS_MEMORY_COUNTERS { cb = (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS)) };
                    if (K32GetProcessMemoryInfo(GetCurrentProcess(), ref pmc, pmc.cb)) RamGameMB = pmc.WorkingSetSize.ToUInt64() / 1048576f;

                    // GPU / VRAM (PDH). 사용률은 두 번 읽은 차이라 첫 값은 버린다.
                    if (gpuOk && pdh.Collect())
                    {
                        double total3d = 0, game3d = 0;
                        pdh.ForEach(pdh.Engine, (name, v) =>
                        {
                            if (name.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0) return;
                            total3d += v;
                            if (name.StartsWith(tag, StringComparison.Ordinal)) game3d += v;
                        });
                        Gpu3D = Clamp100(total3d);
                        GpuGame = Clamp100(game3d);

                        double adapter = 0;
                        pdh.ForEach(pdh.AdapterDedicated, (name, v) => { if (v > adapter) adapter = v; });
                        VramUsedMB = (float)(adapter / 1048576.0);

                        double gded = 0, gsh = 0;
                        pdh.ForEach(pdh.ProcDedicated, (name, v) => { if (name.StartsWith(tag, StringComparison.Ordinal)) gded += v; });
                        pdh.ForEach(pdh.ProcShared, (name, v) => { if (name.StartsWith(tag, StringComparison.Ordinal)) gsh += v; });
                        VramGameMB = (float)(gded / 1048576.0);
                        SharedGameMB = (float)(gsh / 1048576.0);
                        GpuAvailable = true;
                    }

                    for (int i = 0; i < 10 && running; i++) Thread.Sleep(100);
                }
            }
            catch { }
            finally { pdh.Close(); }
        }

        private static float Clamp100(double v) { return (float)Math.Max(0, Math.Min(100, v)); }

        // ── PDH (성능 카운터) ──────────────────────────────────────────
        internal class Pdh
        {
            private IntPtr query;
            internal IntPtr Engine, AdapterDedicated, ProcDedicated, ProcShared;
            private IntPtr buffer = IntPtr.Zero;
            private uint bufferSize;

            internal bool Open()
            {
                if (IntPtr.Size != 8) return false;   // 항목 구조를 64비트 기준으로 읽는다
                try
                {
                    if (PdhOpenQuery(null, IntPtr.Zero, out query) != 0) return false;
                    PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out Engine);
                    PdhAddEnglishCounter(query, @"\GPU Adapter Memory(*)\Dedicated Usage", IntPtr.Zero, out AdapterDedicated);
                    PdhAddEnglishCounter(query, @"\GPU Process Memory(*)\Dedicated Usage", IntPtr.Zero, out ProcDedicated);
                    PdhAddEnglishCounter(query, @"\GPU Process Memory(*)\Shared Usage", IntPtr.Zero, out ProcShared);
                    PdhCollectQueryData(query);
                    return Engine != IntPtr.Zero;
                }
                catch { return false; }   // pdh.dll 이 없거나 카운터가 없는 환경
            }

            internal bool Collect() { return query != IntPtr.Zero && PdhCollectQueryData(query) == 0; }

            // 카운터의 인스턴스마다 (이름, 값)을 넘긴다
            internal void ForEach(IntPtr counter, Action<string, double> each)
            {
                if (counter == IntPtr.Zero) return;
                uint size = 0, count;
                int r = PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, IntPtr.Zero);
                if (r != PDH_MORE_DATA || size == 0) return;
                if (size > bufferSize)
                {
                    if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                    bufferSize = size * 2;
                    buffer = Marshal.AllocHGlobal((int)bufferSize);
                }
                size = bufferSize;
                if (PdhGetFormattedCounterArray(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buffer) != 0) return;
                // PDH_FMT_COUNTERVALUE_ITEM_W (64비트): 이름 포인터 8 + 상태 4 + 채움 4 + double 8 = 24 바이트
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(buffer.ToInt64() + i * 24L);
                    uint status = (uint)Marshal.ReadInt32(item, 8);
                    if (status != 0 && status != 1) continue;   // PDH_CSTATUS_VALID_DATA / NEW_DATA
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 0)) ?? "";
                    double v = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16));
                    each(name, v);
                }
            }

            internal void Close()
            {
                try { if (query != IntPtr.Zero) PdhCloseQuery(query); } catch { }
                query = IntPtr.Zero;
                if (buffer != IntPtr.Zero) { Marshal.FreeHGlobal(buffer); buffer = IntPtr.Zero; bufferSize = 0; }
            }
        }

        private const uint PDH_FMT_DOUBLE = 0x00000200, PDH_FMT_NOCAP100 = 0x00008000;
        private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

        [DllImport("pdh.dll", EntryPoint = "PdhOpenQueryW", CharSet = CharSet.Unicode)]
        private static extern int PdhOpenQuery(string dataSource, IntPtr userData, out IntPtr query);
        [DllImport("pdh.dll", EntryPoint = "PdhAddEnglishCounterW", CharSet = CharSet.Unicode)]
        private static extern int PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);
        [DllImport("pdh.dll")]
        private static extern int PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW", CharSet = CharSet.Unicode)]
        private static extern int PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);
        [DllImport("pdh.dll")]
        private static extern int PdhCloseQuery(IntPtr query);

        [DllImport("kernel32.dll")]
        private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")]
        private static extern bool GetProcessTimes(IntPtr process, out ulong creation, out ulong exit, out ulong kernel, out ulong user);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll")]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

        [StructLayout(LayoutKind.Sequential)]
        private struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb, PageFaultCount;
            public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage,
                QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage;
        }
        [DllImport("kernel32.dll")]
        private static extern bool K32GetProcessMemoryInfo(IntPtr process, ref PROCESS_MEMORY_COUNTERS counters, uint size);
    }
}
