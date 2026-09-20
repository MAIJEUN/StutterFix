using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Scripting;

namespace StutterFix
{
    // 순간 끊김의 큰 축은 GC였다.
    //
    // A/B 측정 (0.8초마다 GC를 멈췄다 켜며 125쌍 비교):
    //   GC 멈춤: 평균 124fps, 최악 프레임 평균 18.9ms, 33ms 넘은 구간 12/125
    //   GC 정상: 평균 106fps, 최악 프레임 평균 45.3ms, 33ms 넘은 구간 118/125
    //
    // 그래서 곡을 플레이하는 동안에는 치우지 않고 쌓아 두기만 하고, 곡이 끝나면 한 번에 정리한다.
    // "곡이 끝났는지"는 예전에 곡 위치가 흐르는지로 봤는데 메뉴에서도 곡이 흘러서 오판했다.
    // 지금은 게임이 직접 들고 있는 상태값(scrController.currentState)을 읽는다.
    public static class GcControl
    {
        internal static bool Enabled = true;
        internal static bool NoCollectDuringSong = true;  // 곡 중에는 조금씩 치우기도 하지 않는다
        internal static int HardLimitMB = 6000;           // 여기 넘으면 끊김을 감수하고 완전 정리
        internal static float MaxPauseSeconds = 300f;     // 감지가 실패해도 이 시간이 지나면 반드시 정리
        internal static int IncrementalStartMB = 800;     // 조금씩 치우기 모드에서만 쓴다
        internal static float SliceMs = 2f;
        internal static int SliceEveryFrames = 4;

        internal static string LastScene = "?";
        internal static int PeakHeapMB;
        internal static long IncrementalSlices;
        internal static long ForcedCollects;
        internal static bool Paused;

        private static float pausedFor;
        private static int frameCounter;
        private static bool patched;

        internal static void Install()
        {
            if (patched) return;
            try
            {
                var harmony = new Harmony("StutterFix.GcControl");

                var scnGame = AccessTools.TypeByName("scnGame");
                if (scnGame != null)
                {
                    var load = AccessTools.Method(scnGame, "LoadLevel");
                    if (load != null)
                        harmony.Patch(load, postfix: new HarmonyMethod(typeof(GcControl), nameof(AfterLoad)));
                }

                // 에디터로 돌아가는 순간은 확실한 종료 신호다. 여기서 반드시 정리한다.
                var scnEditor = AccessTools.TypeByName("scnEditor");
                if (scnEditor != null)
                {
                    var back = AccessTools.Method(scnEditor, "SwitchToEditMode");
                    if (back != null)
                        harmony.Patch(back, prefix: new HarmonyMethod(typeof(GcControl), nameof(BeforeEditMode)));
                }

                patched = true;
                Main.Entry.Logger.Log("gc control installed");
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("gc control install failed: " + ex.Message);
            }
        }

        public static void AfterLoad() { Resume("맵 로딩"); }
        public static void BeforeEditMode() { Hitch.Report(); Resume("에디터 복귀"); }

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
                long before = GC.GetTotalMemory(false) / 1048576;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                GC.Collect();
                sw.Stop();
                ForcedCollects++;
                Main.Entry.Logger.Log(string.Format("GC 재개 및 정리 ({0}) {1}MB -> {2}MB, {3}ms",
                    reason, before, GC.GetTotalMemory(false) / 1048576, sw.ElapsedMilliseconds));
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
            Hitch.Tick(dt, playing);

            if (playing && !Paused) { Pause(); pausedFor = 0f; PeakHeapMB = 0; }
            else if (!playing && Paused) { Hitch.Report(); Resume("곡 종료 [" + LastScene + "]"); }

            if (!Paused) return;

            // 상태 감지가 어떤 이유로든 실패해도 메모리가 무한정 늘지 않도록 시간 제한을 둔다.
            pausedFor += dt;
            if (pausedFor > MaxPauseSeconds)
            {
                pausedFor = 0f;
                Resume("시간 제한 " + (int)MaxPauseSeconds + "초");
                return;
            }

            long heapNow = GC.GetTotalMemory(false) / 1048576;
            if (heapNow > PeakHeapMB) PeakHeapMB = (int)heapNow;

            if (heapNow > HardLimitMB)
            {
                // 안전장치. 여기까지 오면 어쩔 수 없이 한 번 멈춘다.
                Resume("힙 한계 " + heapNow + "MB");
                Pause();
                return;
            }

            if (NoCollectDuringSong) return;

            // 유니티의 점진적 정리는 GC가 켜져 있을 때만 동작한다.
            // 꺼둔 채로 부르면 아무 일도 일어나지 않아 힙이 무한정 늘어난다(실제로 21GB까지 갔다).
            // 그래서 몇 프레임마다 잠깐 켜서 짧게 치우고 다시 끈다.
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
        }

        // ── 게임 상태 읽기 ──────────────────────────────────────────────
        // scrController.currentState 는 None / Start / Countdown / Checkpoint / PlayerControl / Fail / Fail2 / Won.
        // 끝난 상태만 골라내고 나머지는 플레이로 본다.
        // 반대로(플레이 상태만 골라내기) 하면 목록에 없는 이름이 하나라도 나올 때
        // 곡 내내 GC가 안 멈춘다. 실제로 그렇게 만들었다가 한 번도 안 멈췄다.
        private static readonly string[] StopStates = { "None", "Fail", "Fail2", "Won" };

        private static PropertyInfo controllerProp, pausedProp, playModeProp, pausedInPlayProp;
        private static FieldInfo gameworldField, editorInstanceField, stateField, floorField;
        private static bool reflectionReady;
        private static string loggedScene = "";

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
                stateField = AccessTools.Field(ctrl, "currentState");
                floorField = AccessTools.Field(ctrl, "currentFloorID");

                var editor = AccessTools.TypeByName("scnEditor");
                if (editor != null)
                {
                    editorInstanceField = AccessTools.Field(editor, "instance");
                    playModeProp = AccessTools.Property(editor, "playMode");
                    pausedInPlayProp = AccessTools.Property(editor, "pausedInPlayMode");
                }
                Main.Entry.Logger.Log("gc reflection: state=" + (stateField != null) +
                    " gameworld=" + (gameworldField != null) + " playMode=" + (playModeProp != null) +
                    " floor=" + (floorField != null));
            }
            catch (Exception ex) { Main.Entry.Logger.Error("gc reflection 실패: " + ex.Message); }
        }

        internal static int CurrentFloor()
        {
            try
            {
                if (controllerProp == null || floorField == null) return -1;
                var ctrl = controllerProp.GetValue(null);
                if (ctrl == null) return -1;
                return Convert.ToInt32(floorField.GetValue(ctrl));
            }
            catch { return -1; }
        }

        private static bool IsPlaying()
        {
            PrepareReflection();
            try
            {
                bool gameworld = false, paused = false, playMode = true, hasEditor = false;
                string stateName = "?";

                var ctrl = controllerProp != null ? controllerProp.GetValue(null) : null;
                if (ctrl != null)
                {
                    if (gameworldField != null) gameworld = Convert.ToBoolean(gameworldField.GetValue(ctrl));
                    if (pausedProp != null) paused = Convert.ToBoolean(pausedProp.GetValue(ctrl));
                    if (stateField != null)
                    {
                        var v = stateField.GetValue(ctrl);
                        if (v != null) stateName = v.ToString();
                    }
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

                bool stateOk = Array.IndexOf(StopStates, stateName) < 0;
                bool playing = gameworld && !paused && playMode && stateOk;

                LastScene = stateName
                          + (gameworld ? "" : " world:X")
                          + (hasEditor ? (playMode ? " 에디터재생" : " 편집중") : "")
                          + (paused ? " 일시정지" : "");

                // 상태 이름이 바뀔 때만 남긴다. 감지가 또 어긋나면 이 줄만 보면 된다.
                if (LastScene != loggedScene)
                {
                    loggedScene = LastScene;
                    Main.Entry.Logger.Log("[상태] " + LastScene + (playing ? "  -> 플레이" : "  -> 정지"));
                }
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
                return (Paused ? "GC 멈춤" : "GC 정상") + " [" + LastScene + "]"
                     + ", 힙 " + heap + "MB (곡 중 최대 " + PeakHeapMB + "MB)"
                     + ", 할당 " + Hitch.AllocMBPerSec.ToString("F0") + "MB/s"
                     + ", 전체정리 " + ForcedCollects + "회"
                     + (NoCollectDuringSong ? "" : ", 조각정리 " + IncrementalSlices + "회");
            }
        }
    }
}
