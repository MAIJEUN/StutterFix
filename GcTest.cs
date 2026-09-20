using System;
using UnityEngine.Scripting;

namespace StutterFix
{
    // 무거운 구간에서 GC가 초당 200~580회 돌고 있었다.
    // 유니티는 GC를 일시적으로 멈출 수 있으므로, 멈춘 상태와 아닌 상태를 비교하면
    // 순간 끊김이 GC 때문인지 바로 판별된다.
    //
    // 멈춰 두면 메모리가 계속 늘어나므로 곡이 끝난 뒤에는 반드시 다시 켜야 한다.
    public static class GcTest
    {
        internal static bool Paused;

        internal static void SetPaused(bool value)
        {
            try
            {
                GarbageCollector.GCMode = value
                    ? GarbageCollector.Mode.Disabled
                    : GarbageCollector.Mode.Enabled;
                Paused = value;
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("GC 제어 실패: " + ex.Message);
            }
        }

        internal static string Status
        {
            get
            {
                long heap = GC.GetTotalMemory(false) / 1048576;
                return (Paused ? "GC 멈춤" : "GC 정상") + ", 힙 " + heap + "MB, gen0 누적 " + GC.CollectionCount(0);
            }
        }
    }
}
