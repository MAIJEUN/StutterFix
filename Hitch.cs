using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 끊긴 프레임을 하나하나 기록한다.
    //
    // 평균 프레임은 GC를 멈춰서 올렸지만 "중간중간 확 끊기는" 현상은 남아 있었다.
    // 그동안은 무엇이 원인인지 짐작만 했으므로, 끊긴 순간에 무슨 일이 있었는지를
    // 같이 적어 두고 곡이 끝나면 한 번에 정리해서 보여준다.
    //
    //   힙 증가   -> C# 객체 할당/정리 (GC 계열)
    //   네이티브 증가 -> 텍스처, 오디오 같은 리소스를 그 순간 새로 불러온 것
    //   둘 다 아님 -> 엔진/드라이버 쪽 (셰이더 컴파일, 화면 전환 등)
    public static class Hitch
    {
        internal static bool Enabled = true;
        internal static float ThresholdMs = 30f;
        internal static string Summary = "(아직 기록 없음)";

        private struct Rec
        {
            public int Floor;
            public float Ms;
            public long HeapDelta;      // MB
            public long NativeDelta;    // MB
            public int Collects;
            public long Slices;
        }

        private static readonly List<Rec> recs = new List<Rec>(256);
        private static long lastHeap, lastNative, lastSlices;
        private static int lastCollects;
        private static bool wasPlaying;

        // 곡 한 번에 얼마나 할당하는지 (초당 MB). 무정리 모드에서 힙이 어디까지 갈지 예측하는 근거.
        private static float rateTimer;
        private static long rateHeapAtStart;
        private static float songTime;
        internal static float AllocMBPerSec;

        private static long NativeMB()
        {
            try { return UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576; }
            catch { return 0; }
        }

        internal static void Tick(float dt, bool playing)
        {
            if (!Enabled) return;

            long heap = GC.GetTotalMemory(false) / 1048576;
            long native = NativeMB();
            int collects = GC.CollectionCount(0);
            long slices = GcControl.IncrementalSlices;

            if (playing && !wasPlaying) Begin(heap);
            if (!playing && wasPlaying) Report();
            wasPlaying = playing;

            if (playing)
            {
                songTime += dt;
                rateTimer += dt;
                if (rateTimer >= 1f)
                {
                    // 정리가 일어나면 힙이 줄어드니, 늘어난 구간만 더해 대략의 할당 속도를 본다.
                    long grown = heap - lastHeap;
                    if (grown > 0) AllocMBPerSec = AllocMBPerSec * 0.7f + grown * 0.3f / rateTimer;
                    rateTimer = 0f;
                }

                float ms = dt * 1000f;
                if (ms >= ThresholdMs && recs.Count < 400)
                {
                    var r = new Rec
                    {
                        Floor = GcControl.CurrentFloor(),
                        Ms = ms,
                        HeapDelta = heap - lastHeap,
                        NativeDelta = native - lastNative,
                        Collects = collects - lastCollects,
                        Slices = slices - lastSlices,
                    };
                    recs.Add(r);
                    Main.Entry.Logger.Log(string.Format(
                        "[끊김] 타일 #{0}, {1:F1}초 | 프레임 {2:F0}ms | 힙 {3:+#;-#;0}MB | 네이티브 {4:+#;-#;0}MB | 전체정리 {5}회 | 조각정리 {6}회",
                        r.Floor, songTime, r.Ms, r.HeapDelta, r.NativeDelta, r.Collects, r.Slices));
                }
            }

            lastHeap = heap;
            lastNative = native;
            lastCollects = collects;
            lastSlices = slices;
        }

        private static void Begin(long heap)
        {
            recs.Clear();
            songTime = 0f;
            AllocMBPerSec = 0f;
            rateHeapAtStart = heap;
            rateTimer = 0f;
            Main.Entry.Logger.Log("[끊김] 기록 시작");
        }

        internal static void Report()
        {
            if (recs.Count == 0)
            {
                Summary = string.Format("{0:F0}초 동안 {1}ms 넘는 끊김 없음 (할당 {2:F0}MB/s)", songTime, ThresholdMs, AllocMBPerSec);
                Main.Entry.Logger.Log("[끊김] " + Summary);
                return;
            }

            float total = 0f, worst = 0f;
            int gcSide = 0, nativeSide = 0, unknown = 0;
            foreach (var r in recs)
            {
                total += r.Ms;
                if (r.Ms > worst) worst = r.Ms;
                if (r.Collects > 0 || r.Slices > 0 || r.HeapDelta > 20) gcSide++;
                else if (r.NativeDelta > 2) nativeSide++;
                else unknown++;
            }

            // 같은 지점에서 반복되는지 보려고 심한 순서대로 타일 번호를 남긴다.
            recs.Sort((a, b) => b.Ms.CompareTo(a.Ms));
            var tiles = new System.Text.StringBuilder();
            for (int i = 0; i < recs.Count && i < 12; i++)
            {
                if (i > 0) tiles.Append(", ");
                tiles.Append("#").Append(recs[i].Floor).Append("(").Append(recs[i].Ms.ToString("F0")).Append("ms)");
            }

            Summary = string.Format(
                "{0:F0}초 중 {1}회 끊김, 합계 {2:F0}ms, 최악 {3:F0}ms | 원인: GC {4}회, 리소스 {5}회, 불명 {6}회 | 할당 {7:F0}MB/s",
                songTime, recs.Count, total, worst, gcSide, nativeSide, unknown, AllocMBPerSec);

            Main.Entry.Logger.Log("[끊김] " + Summary);
            Main.Entry.Logger.Log("[끊김] 심한 순서: " + tiles);
        }
    }
}
