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
        internal static int MaxHeapMB = 6000;      // 비상용. 곡 중에는 되도록 정리하지 않는다
        internal static string LastScene = "?";
        internal static int PeakHeapMB;
        internal static int IncrementalStartMB = 800;    // 이 이상이면 조금씩 치우기 시작
        internal static float SliceMs = 2f;              // 한 번에 쓸 정리 시간
        internal static long IncrementalSlices;
        internal static int HardLimitMB = 3000;          // 여기 넘으면 끊김을 감수하고 완전 정리
        internal static float MaxPauseSeconds = 240f;    // 감지가 실패해도 이 시간이 지나면 반드시 정리한다
        private static float pausedFor;
        internal static int SliceEveryFrames = 4;        // 몇 프레임마다 짧게 치울지
        private static int frameCounter;
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

            if (playing && !Paused) { Pause(); pausedFor = 0f; }
            else if (!playing && Paused) Resume("곡 종료");

            // 상태 감지가 어떤 이유로든 실패해도 메모리가 무한정 늘지 않도록 시간 제한을 둔다.
            if (Paused)
            {
                pausedFor += dt;
                if (pausedFor > MaxPauseSeconds)
                {
                    Resume("시간 제한 " + (int)pausedFor + "초");
                    pausedFor = 0f;
                }
            }

            // 곡이 길면 메모리가 계속 쌓이므로 한계에 가까워지면 한 번 정리한다.
            if (!Paused) return;

            // 유니티의 점진적 정리는 GC가 켜져 있을 때만 동작한다.
            // 꺼둔 채로 부르면 아무 일도 일어나지 않아 힙이 무한정 늘어난다(실제로 21GB까지 갔다).
            // 그래서 몇 프레임마다 잠깐 켜서 짧게 치우고 다시 끈다.
            long heapNow = GC.GetTotalMemory(false) / 1048576;

            if (heapNow > HardLimitMB)
            {
                // 안전장치: 여기까지 오면 끊김을 감수하고 완전히 정리한다.
                Resume("힙 한계 " + heapNow + "MB");
                Pause();
                return;
            }

            if (heapNow > IncrementalStartMB && ++frameCounter >= SliceEveryFrames)
            {
                frameCounter = 0;
                try
                {
                    GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
                    GarbageCollector.CollectIncremental((ulong)(SliceMs * 1000000f));
                    GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
                    IncrementalSlices++;
                }
                catch { }
            }

            checkTimer += dt;
            if (checkTimer < 2f) return;
            checkTimer = 0f;

            long heapMB = GC.GetTotalMemory(false) / 1048576;
            PeakHeapMB = Math.Max(PeakHeapMB, (int)heapMB);
        }

        // 에디터와 게임이 같은 씬을 쓰므로 씬 이름으로는 구분할 수 없다.
        // 게임 내부 상태를 직접 읽는다.
        //   일반 플레이 : scrController.gameworld == true && !paused
        //   에디터 재생 : scnEditor.playMode == true && !pausedInPlayMode
        private static System.Reflection.PropertyInfo controllerProp, pausedProp, playModeProp, pausedInPlayProp;
        private static System.Reflection.FieldInfo gameworldField, editorInstanceField;
        private static bool reflectionReady;

        private static void PrepareReflection()
        {
            if (reflectionReady) return;
            reflectionReady = true;
            try
            {
                var adoBase = AccessTools.TypeByName("ADOBase");
                controllerProp = AccessTools.Property(adoBase, "controller");

                var ctrl = AccessTools.TypeByName("scrController");
                gameworldField = AccessTools.Field(ctrl, "gameworld");
                pausedProp = AccessTools.Property(ctrl, "paused");

                var editor = AccessTools.TypeByName("scnEditor");
                if (editor != null)
                {
                    editorInstanceField = AccessTools.Field(editor, "instance");
                    playModeProp = AccessTools.Property(editor, "playMode");
                    pausedInPlayProp = AccessTools.Property(editor, "pausedInPlayMode");
                }
                Main.Entry.Logger.Log("gc reflection: controller=" + (controllerProp != null) +
                    " gameworld=" + (gameworldField != null) + " playMode=" + (playModeProp != null));
            }
            catch (Exception ex) { Main.Entry.Logger.Error("gc reflection 실패: " + ex.Message); }
        }

        private static bool IsPlaying()
        {
            PrepareReflection();
            try
            {
                bool gameworld = false, paused = false, playMode = true, hasEditor = false;

                var ctrl = controllerProp?.GetValue(null);
                if (ctrl != null)
                {
                    if (gameworldField != null) gameworld = Convert.ToBoolean(gameworldField.GetValue(ctrl));
                    if (pausedProp != null) paused = Convert.ToBoolean(pausedProp.GetValue(ctrl));
                }

                if (editorInstanceField != null && playModeProp != null)
                {
                    var editor = editorInstanceField.GetValue(null);
                    if (editor != null)
                    {
                        hasEditor = true;
                        playMode = Convert.ToBoolean(playModeProp.GetValue(editor));
                        if (pausedInPlayProp != null && Convert.ToBoolean(pausedInPlayProp.GetValue(editor))) paused = true;
                    }
                }

                bool moving = SongMoving();
                bool playing = gameworld && !paused && playMode && moving;

                LastScene = "world:" + (gameworld ? "O" : "X")
                          + " play:" + (hasEditor ? (playMode ? "O" : "X") : "-")
                          + " pause:" + (paused ? "O" : "X")
                          + " song:" + (moving ? "흐름" : "정지");
                return playing;
            }
            catch (Exception ex)
            {
                LastScene = "판단 실패: " + ex.Message;
                return false;   // 판단이 안 되면 안전하게 GC를 켠 상태로 둔다
            }
        }

        internal static string Status
        {
            get
            {
                long heap = GC.GetTotalMemory(false) / 1048576;
                return (Paused ? "GC 멈춤" : "GC 정상") + " [" + LastScene + "]" +
                       ", 힙 " + heap + "MB (최대 " + PeakHeapMB + "MB), 조금씩정리 " + IncrementalSlices + "회, 전체정리 " + ForcedCollects + "회";
            }
        }

        // 곡 위치가 실제로 흐르고 있는지 확인한다.
        // 곡이 끝났는데도 playMode가 켜진 채로 남는 경우를 걸러내기 위한 것이다.
        private static System.Reflection.PropertyInfo conductorProp, songPosProp;
        private static double lastSongPos = -1;
        private static float stillTime;

        private static bool SongMoving()
        {
            try
            {
                if (conductorProp == null)
                    conductorProp = AccessTools.Property(AccessTools.TypeByName("ADOBase"), "conductor");
                var cond = conductorProp?.GetValue(null);
                if (cond == null) return false;

                if (songPosProp == null || !songPosProp.DeclaringType.IsInstanceOfType(cond))
                    songPosProp = AccessTools.Property(cond.GetType(), "songposition_minusi")
                               ?? AccessTools.Property(cond.GetType(), "songposition");
                if (songPosProp == null) return true;

                double pos = Convert.ToDouble(songPosProp.GetValue(cond));
                bool moved = Math.Abs(pos - lastSongPos) > 0.0001;
                lastSongPos = pos;

                if (moved) { stillTime = 0f; return true; }
                stillTime += Time.unscaledDeltaTime;
                return stillTime < 1f;
            }
            catch { return true; }
        }
    }
}
