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
        // 예전에는 300초(5분)였다. 여기 힙에서는 한 번 정리에 700ms 쯤 걸려서, 1시간짜리 맵이면 5분마다 크게 끊겼다.
        // 곡이 끝난 걸 놓친 경우는 아래 "10초간 조용함" 이 잡으므로, 이 시간은 마지막 안전장치로만 둔다.
        // 메모리는 HardLimitMB 가 따로 지킨다 (곡 중 쌓이는 양은 1분에 100MB 안팎이었다).
        internal static float MaxPauseSeconds = 7200f;
        internal static float EndDelaySeconds = 3f;       // 곡이 끝나고 이만큼 기다렸다 정리한다

        // 완주 직후는 마무리 연출이 돌아가는 중이라, 그 순간 정리하면 연출이 끊긴다.
        // 연출이 끝날 때까지 기다렸다가 치운다.
        private static float resumeCountdown = -1f;
        private static string resumeReason;
        // 죽은 뒤(FailAction)에는 3초 뒤에 치우지 않고, 다음 화면 전환(재시작·재생·편집 복귀)까지 미룬다.
        // 사용자 로그(2.0.0, 에디터에서 죽고 다시 하기 반복): 죽고 3초 뒤 실패 화면에서 160~340ms 정리가 끊김 알림으로 떠서
        // "메모리 정리 때문에 끊긴다" 로 보였다. 재시작 순간은 어차피 곡 준비로 400~700ms 멈추므로 그때 같이 치운다.
        // 아무것도 안 하고 10초 있으면(10초간 조용함) 그때 치운다.
        private static bool holdAfterFail;
        internal static int IncrementalStartMB = 800;     // 조금씩 치우기 모드에서만 쓴다
        internal static float SliceMs = 2f;
        internal static int SliceEveryFrames = 4;

        internal static string LastScene = "?";
        internal static int PeakHeapMB;
        internal static long IncrementalSlices;
        internal static long ForcedCollects;
        internal static bool Paused;

        private static float pausedFor;
        private static float quietTimer;
        private static long quietHeapMark;
        private static int frameCounter;
        private static int slowFrame, ramMB;
        private static float slowDt;
        private static bool patched;

        internal static void Install()
        {
            if (patched) return;
            try
            {
                var harmony = new Harmony("StutterFix.GcControl");

                // 맵 파일 읽기(LevelData.LoadLevel: 파일 전체를 문자열로 읽고 JSON 해석, 이벤트 수만 개 만들기) 동안 GC 멈추기
                var levelData = AccessTools.TypeByName("ADOFAI.LevelData") ?? AccessTools.TypeByName("LevelData");
                if (levelData != null) foreach (var m in levelData.GetMethods(AccessTools.all))
                    if (m.Name == "LoadLevel" && !m.IsAbstract && m.DeclaringType == levelData)
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(GcControl), nameof(ParsePrefix)), finalizer: new HarmonyMethod(typeof(GcControl), nameof(ParseFinalizer)));

                var scnGame = AccessTools.TypeByName("scnGame");
                if (scnGame != null)
                {
                    var load = AccessTools.Method(scnGame, "LoadLevel");
                    if (load != null)
                        harmony.Patch(load, postfix: new HarmonyMethod(typeof(GcControl), nameof(AfterLoad)));
                }

                // 상태값을 지켜보는 대신 "끝나는 순간에 불리는 함수"를 직접 가로챈다.
                // currentState 는 이 버전에서 재생 중에도 None으로 남아 믿을 수 없었다.
                var ends = new[]
                {
                    new[] { "scnEditor", "SwitchToEditMode" },   // 편집으로 복귀
                    new[] { "scrController", "FailAction" },     // 실패
                    new[] { "scrController", "Fail2Action" },
                    new[] { "scrController", "OnLandOnPortal" }, // 완주(포탈 도착)
                    new[] { "scrController", "QuitToMainMenu" },
                    // Checkpoint_Exit 는 넣으면 안 된다. 중간부터 시작할 때 체크포인트 상태에서 플레이로 넘어가며 불리는
                    // 상태 종료 함수(카메라 끄기, 볼륨 되돌리기)다. 곡 끝으로 오인해 3초 뒤 곡 도중에 정리하고, 그 곡 내내 GC를 안 멈췄다.
                    new[] { "scnEditor", "QuitToMenu" },
                    new[] { "scnEditor", "TryQuitToMenu" },
                    new[] { "scnEditor", "SaveAndQuit" },
                };

                // 재시작과 재생 시작은 종료가 아니라 "여기서 한 번 치우고 계속"이다.
                var restarts = new[]
                {
                    new[] { "scrController", "Restart" },
                    new[] { "scnEditor", "Play" },
                    // 에디터에서 죽은 뒤 아무 키나 눌러 다시 할 때는 Restart 가 아니라 이것이다 (Fail2_Update IL 로 확인).
                    // 빠져 있어서 2.0.1 에서는 실패 뒤 정리를 기다리는 동안 다시 한 판들을 곡으로 못 알아보고 GC 를 계속 꺼 둔 채였다(힙 1140MB).
                    new[] { "scrController", "ResetCustomLevel" },
                };

                foreach (var e in ends)
                {
                    try
                    {
                        var type = AccessTools.TypeByName(e[0]);
                        if (type == null) continue;
                        foreach (var m in type.GetMethods(AccessTools.all))
                        {
                            if (m.Name != e[1] || m.IsAbstract || m.ContainsGenericParameters) continue;
                            harmony.Patch(m, prefix: new HarmonyMethod(typeof(GcControl), nameof(OnSongEnd)));
                            Main.Entry.Logger.Log("end hook: " + e[0] + "." + e[1]);
                        }
                    }
                    catch (Exception ex)
                    {
                        Main.Entry.Logger.Error("end hook failed " + e[0] + "." + e[1] + ": " + ex.Message);
                    }
                }

                foreach (var e in restarts)
                {
                    try
                    {
                        var type = AccessTools.TypeByName(e[0]);
                        if (type == null) continue;
                        foreach (var m in type.GetMethods(AccessTools.all))
                        {
                            if (m.Name != e[1] || m.IsAbstract || m.ContainsGenericParameters) continue;
                            harmony.Patch(m, prefix: new HarmonyMethod(typeof(GcControl), nameof(OnSongRestart)));
                            Main.Entry.Logger.Log("restart hook: " + e[0] + "." + e[1]);
                        }
                    }
                    catch (Exception ex)
                    {
                        Main.Entry.Logger.Error("restart hook failed " + e[0] + "." + e[1] + ": " + ex.Message);
                    }
                }

                // 나가는 길은 종류가 많아 다 잡기 어렵다. 씬이 바뀌는 것은 무조건 끝난 것이므로
                // 마지막 그물로 걸어 둔다.
                UnityEngine.SceneManagement.SceneManager.activeSceneChanged += OnSceneChanged;

                patched = true;
                Main.Entry.Logger.Log("gc control installed");
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("gc control install failed: " + ex.Message);
            }
        }

        private static void OnSceneChanged(UnityEngine.SceneManagement.Scene from, UnityEngine.SceneManagement.Scene to)
        {
            endedByHook = true;
            Hitch.Report();
            PerfOverlay.MarkLoading(SettingsWindow.T("화면 전환", "Scene change"));
            Resume("씬 바뀜");
            LeakGuard.ScheduleCensus("장면 " + to.name, 2f);
        }

        // 모드를 다시 불러오기 전에 부른다. GC를 멈춘 채로 두고 내려가면 새 모드가 그 사실을 모른다.
        internal static void Shutdown()
        {
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= OnSceneChanged;
            resumeCountdown = -1f;
            Resume("모드 꺼짐");
            patched = false;   // 다시 켜면 다시 건다
        }

        // ── 맵 파일 읽는 동안 GC 멈추기 ──
        // Arche(42MB 맵 파일)에서 LevelData.LoadLevel 7.5초 동안 GC 가 16번 돌았다. 해석하며 생기는 임시 데이터로 힙이 자라서
        // 한 번에 0.1~0.3초씩 걸린다. 이 동안만 GC 를 멈추고 끝나면 원래대로 켠다(쌓인 것은 뒤이은 이미지 불러오기 끝의 정리나
        // 다음 GC 가 치운다). 멈춘 동안 힙이 파일 크기의 수십 배까지 늘 수 있어서, RAM 이 12GB 이상이고 파일이 200MB 이하일 때만 한다.
        internal static bool ParsePause = true;
        private static bool parsePaused;
        private static long parseT0, parseHeap0; private static int parseGc0;
        public static void ParsePrefix(object[] __args)
        {
            Resilience.Phase("맵 파일 읽는 중");
            if (!Enabled || !ParsePause || Paused || parsePaused) return;
            try
            {
                if (ramMB == 0) { try { ramMB = SystemInfo.systemMemorySize; } catch { ramMB = -1; } }
                if (ramMB < 12000) return;
                string path = __args != null && __args.Length > 0 ? __args[0] as string : null;
                long size = 0; try { if (path != null && System.IO.File.Exists(path)) size = new System.IO.FileInfo(path).Length; } catch { }
                if (size > 200L * 1048576) return;
                if (GarbageCollector.GCMode != GarbageCollector.Mode.Enabled) return;
                parseHeap0 = GC.GetTotalMemory(false); parseGc0 = GC.CollectionCount(0); parseT0 = System.Diagnostics.Stopwatch.GetTimestamp();
                GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
                parsePaused = true;
            }
            catch { }
        }
        public static Exception ParseFinalizer(Exception __exception)
        {
            if (!parsePaused) return __exception;
            parsePaused = false;
            try
            {
                GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - parseT0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Main.Entry.Logger.Log(string.Format("[맵 파일 읽기] GC 멈춤 {0:F0}ms, 힙 {1}MB -> {2}MB (그 사이 GC {3}번)", ms, parseHeap0 / 1048576, GC.GetTotalMemory(false) / 1048576, GC.CollectionCount(0) - parseGc0));
            }
            catch { }
            return __exception;
        }

        public static void AfterLoad() { endedByHook = false; LeakGuard.ScheduleCensus("맵 불러온 뒤", 2f); PerfOverlay.MarkLoading(SettingsWindow.T("맵 불러오기", "Level load")); PerfOverlay.LevelActivity(); Resume("맵 로딩"); }

        public static void OnSongEnd(MethodBase __originalMethod)
        {
            endedByHook = true;
            Hitch.Report();
            // 편집 화면으로 돌아가거나 메뉴로 나갈 때는 타일/장식을 다시 만드느라 멈춘다 (완주 연출은 끊김으로 본다)
            string n = __originalMethod.Name;
            if (n == "SwitchToEditMode" || n.Contains("Quit"))
            {
                // 화면이 바뀌며 어차피 멈추는 순간이라 바로 치운다
                PerfOverlay.MarkLoading(SettingsWindow.T("편집 화면으로", "Back to editor"));
                Resume(n);
                return;
            }
            if (n == "FailAction" || n == "Fail2Action") { if (Paused) holdAfterFail = true; return; }
            ScheduleResume(__originalMethod.Name);
        }

        internal static void ScheduleResume(string reason)
        {
            if (!Paused) return;
            if (resumeCountdown > 0f) return;
            resumeCountdown = EndDelaySeconds;
            resumeReason = reason;
        }

        // 재시작 시간: 재시작 함수가 불린 순간부터 곡이 다시 도는 첫 프레임까지 (Hitch.SongStarted 가 기록)
        internal static long RestartAt;
        internal static string RestartWhy = "";

        public static void OnSongRestart(MethodBase __originalMethod)
        {
            RestartAt = System.Diagnostics.Stopwatch.GetTimestamp(); RestartWhy = __originalMethod.Name;
            Hitch.Report();
            PerfOverlay.MarkLoading(SettingsWindow.T("곡 준비", "Level start"));
            PerfOverlay.BeginStartPhase();
            // 재시작은 어차피 화면이 바뀌는 순간이라 바로 치운다.
            // (재생 누르는 순간부터 GC 를 꺼 두는 것도 해 봤는데, 곡 시작 시간은 그대로였고 시작 직후
            //  "곡 아님" 으로 보이는 순간에 3초 뒤 정리가 예약되어 곡 초반에 끊겼다. 되돌렸다.)
            Resume(__originalMethod.Name);
            EffectBudget.Reset();
            EffectBudget.Suspend(3f);
            endedByHook = false;
        }


        // 종료 함수가 불린 뒤에는 다시 멈추지 않는다.
        // 완주해도 에디터는 playMode를 켜 둔 채라서, 이것이 없으면 다음 프레임에 도로 멈춘다.
        private static bool endedByHook;

        private static void Pause()
        {
            if (Paused) return;
            if (lastCleanMB < 0) { try { lastCleanMB = GC.GetTotalMemory(false) / 1048576; } catch { } }   // 아직 치운 적이 없으면 곡 시작 때 힙이 기준
            try { GarbageCollector.GCMode = GarbageCollector.Mode.Disabled; Paused = true; }
            catch (Exception ex) { Main.Entry.Logger.Error("GC 멈춤 실패: " + ex.Message); }
        }

        // ── 재시작 때 정리 줄이기 ──
        // 2.1.0 사용자 로그(에디터에서 짧은 맵 재시도 반복): 재시작마다 정리에 214~480ms 를 썼는데 줄어든 건 15~100MB 뿐이었다.
        // Mono 의 정리 시간은 쌓인 쓰레기가 아니라 살아 있는 메모리(여기서 460MB)를 훑는 데 들어서, 조금만 쌓여도 매번 0.2초가 든다.
        // 그래서 곡 사이 짧은 전환(재시작, 재생, 편집 복귀, 완주, 실패 뒤, 곡 종료)에서는 지난 정리 뒤로 쌓인 양이 충분할 때만 치우고,
        // 아니면 GC 만 다시 켠다(다음 곡에서 또 멈추므로 쌓인 것은 몇 판 뒤 한 번에 치운다). 새 맵 불러오기·화면 전환은 어차피 멈추는
        // 순간이라 전처럼 치운다. 다른 모드(Quartz 의 부드러운 GC 등)가 이미 치웠으면 쌓인 양이 적어 자연히 건너뛴다.
        internal static int DebtMinMB = 192;
        private static long lastCleanMB = -1;
        internal static long SkippedCollects;
        private static bool QuickTransition(string reason)
        {
            switch (reason)
            {
                case "ResetCustomLevel": case "Restart": case "Play": case "SwitchToEditMode": case "OnLandOnPortal": case "10초간 조용함": return true;
            }
            return reason.StartsWith("곡 종료", StringComparison.Ordinal) || reason.StartsWith("Fail", StringComparison.Ordinal);
        }

        internal static void Resume(string reason)
        {
            if (!Paused) return;
            try
            {
                holdAfterFail = false;
                if (QuickTransition(reason))
                {
                    long heap = GC.GetTotalMemory(false) / 1048576;
                    if (lastCleanMB < 0) lastCleanMB = heap;
                    long debt = heap - lastCleanMB;
                    long need = Math.Max(DebtMinMB, lastCleanMB * 35 / 100);
                    if (debt < need)
                    {
                        GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
                        Paused = false;
                        resumeCountdown = -1f;
                        SkippedCollects++;
                        Main.Entry.Logger.Log(string.Format("GC 재개 ({0}) 정리 생략: 지난 정리 뒤 쌓인 것 {1}MB < {2}MB (힙 {3}MB)", reason, debt, need, heap));
                        return;
                    }
                }
                // 곡 밖에서 하는 정리는 곡 중 끊김이 아니다(곡 중에 날 정리를 미뤄 둔 것). 모니터에는 회색으로 따로 적는다.
                // (재시작·맵 로딩처럼 이미 불러오기로 적힌 순간이면 그 이름을 그대로 둔다)
                bool outside = false;
                try { outside = !IsPlaying(); } catch { }
                if (outside && !PerfOverlay.IsLoadingNow)
                    PerfOverlay.MarkLoading(SettingsWindow.T("곡 밖 메모리 정리", "Memory cleanup outside play"),
                        SettingsWindow.T("곡 중에 끊기지 않게 미뤄 둔 메모리 정리를 곡 밖에서 했습니다. 끊김으로 세지 않습니다",
                            "Memory cleanup postponed from gameplay ran outside play; not counted as a hitch"));
                GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
                Paused = false;
                resumeCountdown = -1f;
                long before = GC.GetTotalMemory(false) / 1048576;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // 유니티의 점진적 GC는 한 번 불러서는 한 주기를 끝내지 않는다.
                // 실제로 6001MB가 704ms 걸려 5671MB로만 줄었고, 4초 뒤 다시 불렀을 때 비로소 757MB가 됐다.
                // 그러니 더 이상 줄지 않을 때까지 이어서 돌린다. 어차피 멈출 거면 한 번에 끝내는 편이 낫다.
                long prev = before;
                for (int i = 0; i < 5; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    long now = GC.GetTotalMemory(false) / 1048576;
                    if (now > prev - 50) break;   // 50MB도 안 줄면 끝난 것이다
                    prev = now;
                }

                sw.Stop();
                lastCleanMB = GC.GetTotalMemory(false) / 1048576;
                ModCost.Add(SettingsWindow.T("메모리 정리 (모드)", "Memory cleanup (mod)"), sw.Elapsed.TotalMilliseconds);
                ForcedCollects++;
                Main.Entry.Logger.Log(string.Format("GC 재개 및 정리 ({0}) {1}MB -> {2}MB, {3}ms",
                    reason, before, GC.GetTotalMemory(false) / 1048576, sw.ElapsedMilliseconds));
            }
            catch (Exception ex) { Main.Entry.Logger.Error("GC 재개 실패: " + ex.Message); }
        }

        internal static void Tick(float dt)
        {
            // 넘겨받는 dt 는 게임 시간이라 일시정지나 메뉴에서 0이 된다.
            // 그 시간으로 세면 "곡 끝나고 3초 뒤 정리", "10초간 조용하면 정리", 시간 제한이
            // 전부 얼어붙어서, 플레이 중에 나가면 GC가 멈춘 채로 영영 남는다.
            dt = Time.unscaledDeltaTime;

            if (!Enabled)
            {
                Resume("기능 꺼짐");
                return;
            }

            bool playing = IsPlaying();
            long hq = System.Diagnostics.Stopwatch.GetTimestamp();
            Hitch.Tick(dt, playing);
            Main.TickCost[16] += System.Diagnostics.Stopwatch.GetTimestamp() - hq;

            if (playing && !Paused) { Pause(); pausedFor = 0f; PeakHeapMB = 0; quietTimer = 0f; quietHeapMark = GC.GetTotalMemory(false) / 1048576; }
            else if (!playing && Paused) { Hitch.Report(); if (!holdAfterFail) ScheduleResume("곡 종료 [" + LastScene + "]"); }

            // 곡이 끝났으면 연출이 끝나기를 기다렸다 치운다.
            if (resumeCountdown > 0f)
            {
                resumeCountdown -= dt;
                if (resumeCountdown <= 0f)
                {
                    resumeCountdown = -1f;
                    Resume(resumeReason);
                    return;
                }
            }

            if (!Paused) return;

            // 상태 감지가 어떤 이유로든 실패해도 메모리가 무한정 늘지 않도록 시간 제한을 둔다.
            pausedFor += dt;
            if (pausedFor > MaxPauseSeconds)
            {
                pausedFor = 0f;
                Resume("시간 제한 " + (int)MaxPauseSeconds + "초");
                return;
            }

            // 아래 판단(힙 최고치, 10초간 조용함, 실패 뒤 곡 감지, 힙 한계)은 느슨한 것이라 8프레임에 한 번만 한다.
            // 예전에는 매 프레임 힙 크기·음악 재생 여부·시스템 RAM 크기(엔진 호출)를 읽었다.
            slowDt += dt;
            if (++slowFrame < 8) return;
            dt = slowDt; slowDt = 0f; slowFrame = 0;
            long heapNow = GC.GetTotalMemory(false) / 1048576;
            if (heapNow > PeakHeapMB) PeakHeapMB = (int)heapNow;

            // 나가는 길을 다 잡지 못해도, 아무 일도 일어나지 않는 상태로 오래 있으면 끝난 것이다.
            // 예전에는 "곡이 도는 중에는 초당 몇 MB씩 쌓인다" 고 보고 힙이 10초간 3MB 도 안 늘면 끝났다고 했는데,
            // 모드가 할당을 많이 줄인 뒤로는 곡 초반 10초 동안 거의 안 늘어서, 곡이 도는 중에 정리(850ms)를 해 버렸다.
            // 지금은 곡 소리가 재생 중이면 끝난 것으로 보지 않는다.
            bool songRunning = false;
            try { var cd = scrConductor.instance; songRunning = cd != null && cd.song != null && cd.song.isPlaying; } catch { }
            // 안전장치: 실패 뒤 정리를 기다리는데 모르는 길로 곡이 다시 돌기 시작했다(곡으로 못 알아봄).
            // 여기서 한꺼번에 치우면 곡 중에 멈추므로, 정리 없이 GC 만 원래대로 켠다(게임 기본 동작과 같음).
            if (holdAfterFail && songRunning && !playing)
            {
                holdAfterFail = false;
                try { GarbageCollector.GCMode = GarbageCollector.Mode.Enabled; Paused = false; resumeCountdown = -1f; } catch { }
                Main.Entry.Logger.Log("[GC] 실패 뒤 곡이 다시 도는 것을 감지: 정리 없이 GC 를 켬 (힙 " + heapNow + "MB)");
                return;
            }
            if (songRunning) { quietTimer = 0f; quietHeapMark = heapNow; }
            else quietTimer += dt;
            if (quietTimer >= 10f)
            {
                if (heapNow - quietHeapMark < 3)
                {
                    Resume("10초간 조용함");
                    return;
                }
                quietHeapMark = heapNow;
                quietTimer = 0f;
            }

            // RAM 이 적은 컴퓨터에서는 한계를 낮춘다 (RAM 의 40%, 최소 1.5GB)
            int limit = HardLimitMB;
            if (ramMB == 0) { try { ramMB = SystemInfo.systemMemorySize; } catch { ramMB = -1; } }   // 바뀌지 않으므로 한 번만
            if (ramMB > 0) limit = Mathf.Min(limit, Mathf.Max(1500, ramMB * 2 / 5));
            if (heapNow > limit)
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
        // 그런데 이 버전에서는 재생 중에도 None으로 남아 있다(패널에서 확인). 쓰지 않는 필드로 보인다.
        // 그래서 끝난 상태로 바뀔 때만 종료 신호로 쓰고, 판정 자체는 gameworld + 에디터 재생 여부로 한다.
        // 종료는 상태값에 기대지 않고 아래 Install()에서 실제 종료 함수들을 직접 가로채 처리한다.
        private static readonly string[] StopStates = { "Fail", "Fail2", "Won" };

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

        // ── 빠른 판단 ──
        // 예전에는 매 프레임 리플렉션 7번(값 상자 만들기 포함) + 상태 이름 ToString + LastScene 문자열 이어 붙이기를 했다.
        // 논이펙 맵 측정에서 UMM 의 UI.Update(모든 모드의 OnUpdate 를 부름)가 프레임당 0.08ms 였다. 같은 값을 델리게이트로 읽고,
        // 상태 문자열은 값이 바뀔 때만 만든다. 판단 결과와 로그는 예전과 같다.
        private static bool fastTried, fastOk;
        private static Func<scrController> ctrlGet;
        private static Func<scrController, bool> pausedGet;
        private static Func<scnEditor, bool> playModeGet, pausedInPlayGet;
        private static AccessTools.FieldRef<scrController, bool> gameworldRef;
        private static AccessTools.FieldRef<scrController, States> stateRef;
        private static AccessTools.FieldRef<scnEditor> editorInstRef;
        private static int lastKey = int.MinValue;

        private static bool PrepareFast()
        {
            if (fastTried) return fastOk;
            fastTried = true;
            try
            {
                ctrlGet = AccessTools.MethodDelegate<Func<scrController>>(AccessTools.PropertyGetter(typeof(ADOBase), "controller"));
                pausedGet = AccessTools.MethodDelegate<Func<scrController, bool>>(AccessTools.PropertyGetter(typeof(scrController), "paused"));
                playModeGet = AccessTools.MethodDelegate<Func<scnEditor, bool>>(AccessTools.PropertyGetter(typeof(scnEditor), "playMode"));
                var pip = AccessTools.PropertyGetter(typeof(scnEditor), "pausedInPlayMode");
                pausedInPlayGet = pip != null ? AccessTools.MethodDelegate<Func<scnEditor, bool>>(pip) : null;
                gameworldRef = AccessTools.FieldRefAccess<scrController, bool>("gameworld");
                stateRef = AccessTools.FieldRefAccess<scrController, States>("currentState");
                fastOk = ctrlGet != null && pausedGet != null && playModeGet != null && gameworldRef != null && stateRef != null
                         && AccessTools.Field(typeof(scnEditor), "instance") != null;
            }
            catch (Exception ex) { fastOk = false; Main.Entry.Logger.Log("[GC] 빠른 판단 준비 실패, 예전 방식 사용: " + ex.Message); }
            return fastOk;
        }

        private static bool IsPlayingFast()
        {
            bool gameworld = false, paused = false, playMode = true, hasEditor = false;
            int st = -1;
            var ctrl = ctrlGet();
            if (ctrl != null)
            {
                gameworld = gameworldRef(ctrl);
                paused = pausedGet(ctrl);
                st = (int)stateRef(ctrl);
            }
            var editor = scnEditor.instance;
            if (editor != null)
            {
                hasEditor = true;
                playMode = playModeGet(editor);
                if (pausedInPlayGet != null && pausedInPlayGet(editor)) paused = true;
            }
            bool stateOk = st != (int)States.Fail && st != (int)States.Fail2 && st != (int)States.Won;
            bool playing = gameworld && !paused && playMode && stateOk && !endedByHook;

            // 상태 이름 문자열은 값이 바뀔 때만 만든다 (예전과 같은 모양)
            int key = (st + 1) | (endedByHook ? 1 << 8 : 0) | (gameworld ? 1 << 9 : 0) | (hasEditor ? 1 << 10 : 0) | (playMode ? 1 << 11 : 0) | (paused ? 1 << 12 : 0);
            if (key != lastKey)
            {
                lastKey = key;
                string stateName = st < 0 ? "?" : ((States)st).ToString();
                LastScene = stateName
                          + (endedByHook ? " 종료됨" : "")
                          + (gameworld ? "" : " world:X")
                          + (hasEditor ? (playMode ? " 에디터재생" : " 편집중") : "")
                          + (paused ? " 일시정지" : "");
                if (LastScene != loggedScene)
                {
                    loggedScene = LastScene;
                    Main.Entry.Logger.Log("[상태] " + LastScene + (playing ? "  -> 플레이" : "  -> 정지"));
                }
            }
            return playing;
        }

        private static bool IsPlaying()
        {
            try { if (PrepareFast()) return IsPlayingFast(); } catch { }
            return IsPlayingSlow();
        }

        private static bool IsPlayingSlow()
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
                bool playing = gameworld && !paused && playMode && stateOk && !endedByHook;

                LastScene = stateName
                          + (endedByHook ? " 종료됨" : "")
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
