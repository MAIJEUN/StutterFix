using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Scripting;

namespace StutterFix
{
    // 순간 끊김의 진짜 원인은 GC였다.
    //
    // A/B 측정 (0.8초마다 GC를 멈췄다 켜며 125쌍 비교):
    //   GC 멈춤: 평균 124fps, 최악 프레임 평균 18.9ms, 33ms 넘은 구간 12/125
    //   GC 정상: 평균 106fps, 최악 프레임 평균 45.3ms, 33ms 넘은 구간 118/125
    //
    // 맵이 초당 수만 개의 임시 객체를 만들어내고, 그것을 치우는 작업이 프레임을 멈춘다.
    // 그래서 곡을 플레이하는 동안에는 GC를 멈추고, 곡이 끝나거나 메모리가 한계에 가까워지면 정리한다.
    public static class GcControl
    {
        internal static bool Enabled = true;
        internal static int MaxHeapMB = 4096;      // 이 이상 쌓이면 곡 중이라도 한 번 정리한다
        internal static bool Paused;
        internal static long ForcedCollects;

        private static bool patched;
        private static float checkTimer;

        internal static void Install()
        {
            if (patched) return;
            try
            {
                var harmony = new Harmony("StutterFix.GcControl");
                var scnGame = AccessTools.TypeByName("scnGame");
                if (scnGame != null)
                {
                    // 곡을 불러올 때마다 상태를 정리한다.
                    var load = AccessTools.Method(scnGame, "LoadLevel");
                    if (load != null)
                        harmony.Patch(load, postfix: new HarmonyMethod(typeof(GcControl), nameof(AfterLoad)));
                }
                patched = true;
                Main.Entry.Logger.Log("gc control installed");
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("gc control install failed: " + ex.Message);
            }
        }

        public static void AfterLoad()
        {
            Resume("맵 로딩");
        }

        private static void Pause()
        {
            if (Paused) return;
            try { GarbageCollector.GCMode = GarbageCollector.Mode.Disabled; Paused = true; }
            catch (Exception ex) { Main.Entry.Logger.Error("GC 멈춤 실패: " + ex.Message); }
        }

        internal static void Resume(string reason)
        {
            if (!Paused) return;
            try
            {
                GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
                Paused = false;
                GC.Collect();
                ForcedCollects++;
                Main.Entry.Logger.Log("GC 재개 및 정리 (" + reason + ")");
            }
            catch (Exception ex) { Main.Entry.Logger.Error("GC 재개 실패: " + ex.Message); }
        }

        internal static void Tick(float dt)
        {
            if (!Enabled)
            {
                Resume("기능 꺼짐");
                return;
            }

            bool playing = IsPlaying();

            if (playing && !Paused) Pause();
            else if (!playing && Paused) Resume("곡 종료");

            // 곡이 길면 메모리가 계속 쌓이므로 한계에 가까워지면 한 번 정리한다.
            if (!Paused) return;
            checkTimer += dt;
            if (checkTimer < 2f) return;
            checkTimer = 0f;

            long heapMB = GC.GetTotalMemory(false) / 1048576;
            if (heapMB > MaxHeapMB)
            {
                Resume("메모리 한계 " + heapMB + "MB");
                Pause();
            }
        }

        // 곡이 실제로 진행 중일 때만 멈춘다. 메뉴나 에디터에서는 정상 동작시킨다.
        private static bool IsPlaying()
        {
            try
            {
                var ctrlType = AccessTools.TypeByName("scrController");
                if (ctrlType == null) return false;
                var inst = AccessTools.Field(ctrlType, "instance")?.GetValue(null);
                if (inst == null) return false;

                var gameworld = AccessTools.Field(ctrlType, "gameworld")?.GetValue(inst);
                var paused = AccessTools.Property(ctrlType, "paused")?.GetValue(inst);
                if (gameworld is bool && !(bool)gameworld) return false;
                if (paused is bool && (bool)paused) return false;
                return true;
            }
            catch { return false; }
        }

        internal static string Status
        {
            get
            {
                long heap = GC.GetTotalMemory(false) / 1048576;
                return (Paused ? "곡 진행 중 — GC 멈춤" : "대기 — GC 정상") +
                       ", 힙 " + heap + "MB, 정리 " + ForcedCollects + "회";
            }
        }
    }
}
