using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace StutterFix
{
    // 끊긴 프레임을 하나하나 기록한다.
    //
    // 평균 프레임은 GC를 멈춰서 올렸지만 "중간중간 확 끊기는" 현상은 남아 있었다.
    // 끊긴 순간에 무슨 일이 있었는지를 같이 적어 두고, 곡이 끝나면 한 번에 정리해서 보여준다.
    //
    //   힙 증가     -> C# 객체를 그 순간 왕창 잡았다
    //   힙 감소     -> GC가 돌았다 (한계에 닿아 우리가 강제로 돌린 것 포함)
    //   네이티브 증가 -> 텍스처, 오디오 같은 리소스를 그 순간 새로 불러왔다
    //   둘 다 아님   -> 엔진/드라이버 쪽 (셰이더 컴파일, 화면 전환 등)
    //
    // 프레임 시간은 Time.deltaTime 을 쓰지 않는다. 그 값은 0.333초에서 잘려서
    // 1초를 멈춰도 333ms로 보인다. 실제로 첫 기록에 333ms가 세 번 찍혔는데 전부 잘린 값이었다.
    public static class Hitch
    {
        internal static bool Enabled = true;
        // 164Hz 화면에서는 한 프레임이 6ms다. 20ms만 돼도 눈에 띄므로 기준을 낮게 잡는다.
        // 30ms로 두었을 때 사용자가 느낀 28~30초 구간이 기록에 아예 안 남았다.
        internal static float ThresholdMs = 16f;
        internal static string Summary = "(아직 기록 없음)";

        private struct Rec
        {
            public int Floor;
            public float Ms;
            public long HeapDelta;      // MB
            public long NativeDelta;    // MB
            public int Collects;
        }

        private static readonly List<Rec> recs = new List<Rec>(512);
        private static long lastHeap, lastNative;
        private static int lastCollects;
        private static bool wasPlaying, reported;
        private static long lastStamp;

        private static float songTime;
        private static float rateTimer;
        private static long rateHeapMark;
        internal static float AllocMBPerSec;
        internal static float PeakAllocMBPerSec;

        // DOTween 의 정리 함수는 살아 있는 애니메이션 목록 전체를 훑는다.
        // 목록이 길면 정리 한 번이 통째로 비싸지므로 그 길이를 같이 본다.
        private static string ActiveTweens()
        {
            try { return DG.Tweening.DOTween.TotalActiveTweens() + "개(재생중 " + DG.Tweening.DOTween.TotalPlayingTweens() + ")"; }
            catch { return "?" ; }
        }

        private static long NativeMB()
        {
            try { return UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576; }
            catch { return 0; }
        }

        internal static void Tick(float dt, bool playing)
        {
            if (!Enabled) return;

            long stamp = Stopwatch.GetTimestamp();
            float realMs = lastStamp == 0 ? dt * 1000f : (stamp - lastStamp) * 1000f / Stopwatch.Frequency;
            lastStamp = stamp;

            long heap = GC.GetTotalMemory(false) / 1048576;
            long native = NativeMB();
            int collects = GC.CollectionCount(0);

            if (playing && !wasPlaying) Begin(heap);
            if (!playing && wasPlaying) Report();
            wasPlaying = playing;

            if (playing)
            {
                songTime += realMs / 1000f;

                // 초당 몇 MB를 새로 잡는지. GC가 멈춰 있으니 힙 증가량이 곧 할당량이다.
                rateTimer += realMs / 1000f;
                if (rateTimer >= 1f)
                {
                    long grown = heap - rateHeapMark;
                    if (grown > 0)
                    {
                        AllocMBPerSec = grown / rateTimer;
                        if (AllocMBPerSec > PeakAllocMBPerSec) PeakAllocMBPerSec = AllocMBPerSec;
                    }
                    rateHeapMark = heap;
                    rateTimer = 0f;
                }

                if (realMs >= ThresholdMs && recs.Count < 500)
                {
                    var r = new Rec
                    {
                        Floor = GcControl.CurrentFloor(),
                        Ms = realMs,
                        HeapDelta = heap - lastHeap,
                        NativeDelta = native - lastNative,
                        Collects = collects - lastCollects,
                    };
                    recs.Add(r);
                    Main.Entry.Logger.Log(string.Format(
                        "[끊김] 타일 #{0}, {1:F1}초 | 프레임 {2:F0}ms | 힙 {3:+#;-#;0}MB | 네이티브 {4:+#;-#;0}MB | 정리 {5}회 | 할당 {6:F0}MB/s | 모드 {7}",
                        r.Floor, songTime, r.Ms, r.HeapDelta, r.NativeDelta, r.Collects, AllocMBPerSec, ModWatch.Top));
                    Main.Entry.Logger.Log("[끊김]    직전 프레임 단계: " + PhaseWatch.TopOfLastFrame(3));
                    Main.Entry.Logger.Log("[끊김]    그리기: " + RenderWatch.Info());
                    Main.Entry.Logger.Log("[끊김]    느린 함수: " + SlowScan.Top(5) + " | " + EffectScan.FrameSummary() + " | 살아있는 애니메이션 " + ActiveTweens());
                }
            }

            lastHeap = heap;
            lastNative = native;
            lastCollects = collects;
            RenderWatch.EndFrame();
            SlowScan.Reset();   // 다음 프레임 몫만 모으도록 매번 비운다
            EffectScan.ResetFrame();
        }

        private static void Begin(long heap)
        {
            recs.Clear();
            songTime = 0f;
            AllocMBPerSec = 0f;
            PeakAllocMBPerSec = 0f;
            rateHeapMark = heap;
            rateTimer = 0f;
            reported = false;
            EffectBudget.Reset();
            SlowScan.InstallOnce();
            Main.Entry.Logger.Log("[끊김] 기록 시작");
        }

        internal static void Report()
        {
            if (songTime < 1f || reported) return;   // 곡이 끝나면 여러 경로에서 불릴 수 있다
            reported = true;

            EffectBudget.Reset();
            ModWatch.Report();

            if (recs.Count == 0)
            {
                Summary = string.Format("{0:F0}초 동안 {1}ms 넘는 끊김 없음 (할당 최고 {2:F0}MB/s)",
                    songTime, ThresholdMs, PeakAllocMBPerSec);
                Main.Entry.Logger.Log("[끊김] " + Summary);
                return;
            }

            float total = 0f, worst = 0f;
            int gcSide = 0, nativeSide = 0, unknown = 0;
            foreach (var r in recs)
            {
                total += r.Ms;
                if (r.Ms > worst) worst = r.Ms;
                if (r.Collects > 0 || r.HeapDelta > 20 || r.HeapDelta < -20) gcSide++;
                else if (r.NativeDelta > 2) nativeSide++;
                else unknown++;
            }

            // 같은 지점에서 반복되는지 보려고 심한 순서대로 남긴다.
            recs.Sort((a, b) => b.Ms.CompareTo(a.Ms));
            var worstList = new System.Text.StringBuilder();
            for (int i = 0; i < recs.Count && i < 10; i++)
            {
                if (i > 0) worstList.Append(", ");
                worstList.Append(recs[i].Ms.ToString("F0")).Append("ms");
            }

            Summary = string.Format(
                "{0:F0}초 중 {1}회 끊김, 합계 {2:F0}ms, 최악 {3:F0}ms | 원인: GC {4}회, 리소스 {5}회, 불명 {6}회 | 할당 최고 {7:F0}MB/s",
                songTime, recs.Count, total, worst, gcSide, nativeSide, unknown, PeakAllocMBPerSec);

            Main.Entry.Logger.Log("[끊김] " + Summary);
            Main.Entry.Logger.Log("[끊김] 심한 순서: " + worstList);
        }
    }
}
