using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace StutterFix
{
    // 게임 화면에 띄워 두는 실시간 모니터.
    //
    // 표시 방식 (단축키, 기본 Shift+Insert 로 차례로 바뀐다, 설정 창 "모니터" 에서도 고른다)
    //   아이콘 : 화면 끝의 작은 탭. FPS 와 고른 항목(프레임 시간, CPU, GPU, VRAM ...), 상태 점, 작은 그래프.
    //            누르면 상세 패널이 옆으로 펼쳐진다
    //   미니   : 한 줄짜리 알약. FPS 와 고른 항목을 칸으로 나눠 보여 주고 그래프를 붙인다
    //   상세   : FPS, 그래프, 이번 곡 통계, CPU/GPU/VRAM/RAM, GC, 최근 끊김 목록
    // 본체를 잡고 끌면 위아래로 옮겨지고, 화면 반대쪽으로 끌면 그쪽 끝에 붙는다.
    //
    // 끊김 원인 (그 프레임에 있었던 일로 가린다. 정밀 측정이 아니라 추정이다):
    //   GC 가 돌았다 -> 메모리 정리 / 효과 시작에 프레임의 40% 이상 -> 효과 몰림 /
    //   GPU 가 70% 이상 바빴다 -> GPU 과부하 / 메인 스레드가 60% 이상 -> 게임 처리 / 모두 아니다 -> 게임 바깥
    // GPU·메인 스레드 시간은 유니티가 몇 프레임 늦게 준다. 끊긴 순간에만 요청하면 비어서 "원인 불명"이 떴다.
    // 그래서 모니터가 켜져 있는 동안 매 프레임 수집하고, 끊긴 뒤 5프레임 동안 들어온 값 중 가장 큰 것으로 가린다.
    //
    // 비용: 사용량은 SystemMonitor 가 작업 스레드에서 1초에 한 번 읽는다. 글자는 1초에 4번만 새로 만든다
    // (곡 중에는 GC 가 멈춰 있어서, 매 프레임 문자열을 만들면 그대로 쌓인다).
    public class PerfOverlay : MonoBehaviour
    {
        internal static PerfOverlay Instance;

        internal static void Create()
        {
            if (Instance != null) return;
            var go = new GameObject("StutterFix.PerfOverlay");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<PerfOverlay>();
        }

        // UMM 창(Ctrl+F10)을 열고 닫을 때: 열 때는 UMM 과 각 모드가 설정 화면을 처음 만들고, 닫을 때는 모드들이
        // 설정을 파일로 저장한다(AdofaiTweaks 는 11개). 그 순간의 멈춤은 게임 끊김이 아니라 "모드 창" 으로 적는다.
        internal static void Install(HarmonyLib.Harmony h)
        {
            var prefix = new HarmonyLib.HarmonyMethod(typeof(PerfOverlay), nameof(UmmToggle));
            foreach (var m in HarmonyLib.AccessTools.GetDeclaredMethods(typeof(UnityModManagerNet.UnityModManager.UI)))
                if (m.Name == "ToggleWindow") h.Patch(m, prefix: prefix);

            // 편집 화면에서 맵 열기/저장: 윈도우 파일 선택 창이 떠 있는 동안 게임이 통째로 멈춘다(1.1초가 "게임 처리" 로 찍혔다).
            var file = new HarmonyLib.HarmonyMethod(typeof(PerfOverlay), nameof(FileDialog));
            foreach (var n in new[] { "OpenLevel", "OpenLevelCo", "OpenRecent", "SaveLevel", "SaveLevelAs", "SaveLevelAsCo" })
                foreach (var m in HarmonyLib.AccessTools.GetDeclaredMethods(typeof(scnEditor)))
                    if (m.Name == n && !m.IsGenericMethod) { try { h.Patch(m, prefix: file); } catch { } }
        }

        private static System.Reflection.PropertyInfo ummInstP, ummOpenedP;
        private static System.Reflection.FieldInfo ummInstF;
        private static bool ummLooked;
        private static bool UmmOpen()
        {
            try
            {
                var ui = typeof(UnityModManagerNet.UnityModManager.UI);
                if (!ummLooked)
                {
                    ummLooked = true;
                    ummInstP = HarmonyLib.AccessTools.Property(ui, "Instance");
                    if (ummInstP == null) ummInstF = HarmonyLib.AccessTools.Field(ui, "Instance");
                    ummOpenedP = HarmonyLib.AccessTools.Property(ui, "Opened");
                }
                if (ummOpenedP == null) return false;
                object inst = ummInstP != null ? ummInstP.GetValue(null, null) : ummInstF != null ? ummInstF.GetValue(null) : null;
                return inst != null && (bool)ummOpenedP.GetValue(inst, null);
            }
            catch { return false; }
        }

        private static void FileDialog() { MarkLoading(SettingsWindow.T("맵 열기·저장", "Open / save level")); }

        private static void UmmToggle() { MarkLoading(SettingsWindow.T("모드 창 (UMM)", "Mod window (UMM)")); }

        internal static void Destroy()
        {
            if (Instance == null) return;
            SystemMonitor.Stop();
            UiInputBlock.Remove(Instance);
            UnityEngine.Object.Destroy(Instance.gameObject);
            Instance = null;
        }

        private static Settings C { get { return Main.Config; } }
        private static int Mode { get { return C == null ? 0 : C.OverlayMode; } }
        private static string T(string ko, string en) { return SettingsWindow.T(ko, en); }

        // ── 프레임 기록 ────────────────────────────────────────────────
        private const int GraphN = 90, LowN = 300, HistN = 60;
        private readonly float[] graph = new float[GraphN];
        private int graphHead;
        private readonly float[] recent = new float[LowN];
        private readonly float[] sortBuf = new float[LowN];
        private int recentCount, recentHead;
        private long lastStamp;
        private float avgMs = 8f;
        private bool prevHitch;
        private float prevMs;
        private int lastGc;
        private int hitchCount;
        private float lastHitchTime = -999f, lastHitchMs;

        // 유니티가 늦게 주는 GPU/메인 스레드 시간: 최근 4프레임
        private readonly FrameTiming[] timing = new FrameTiming[1];
        private readonly float[] gpuRing = new float[4], cpuRing = new float[4];
        private int timingHead, timingSamples;

        // 사용량 추이 (1초에 한 칸, 최근 60초)
        private readonly float[] hCpu = new float[HistN], hGpu = new float[HistN], hVram = new float[HistN], hRam = new float[HistN];
        private int histHead, histCount;
        private float histTimer;

        // 이번 곡 통계
        private bool wasPlaying;
        private double songMs;
        private int songFrames, songHitches;
        private float songWorst;

        private class HitchRec { public float Ms, Time; public int Count = 1; public bool IsMod, IsLoading; public string Cause, Detail, Title, Short; public Color Tone; }
        private readonly List<HitchRec> history = new List<HitchRec>();   // 최근 끊김 (상세 패널 목록)
        private readonly List<HitchRec> toasts = new List<HitchRec>();    // 떠 있는 알림
        private readonly Dictionary<HitchRec, float> toastY = new Dictionary<HitchRec, float>();
        private const float ToastLife = 4f;

        // ── 화면 상태 ──────────────────────────────────────────────────
        private float show;          // 전체가 나타난 정도
        private float open;          // 아이콘에서 상세 패널이 펼쳐진 정도
        private bool iconOpen;
        private int lastMode = 1;
        private float cpuBar, gpuBar, vramBar, ramBar, flash;
        private float textTimer;
        private string sFps = "-", sMs = "", sLow = "", sCpu = "", sCpuSub = "", sGpu = "", sGpuSub = "", sVram = "", sVramSub = "",
            sRam = "", sRamSub = "", sGc = "", sGcSub = "", sFooter = "", sSong = "", sSongSub = "";
        private string sMsShort = "", sLowShort = "", sCpuShort = "", sGpuShort = "", sVramShort = "", sRamShort = "";
        private string[] sHist = new string[0], sHistMs = new string[0], sHistAgo = new string[0];
        private string sSongWorst = "", sSongCount = "";
        private bool vramWarn;

        private void Update()
        {
            if (C == null) return;
            if (Hotkey.Down(C.OverlayKey, C.OverlayMods)) { C.OverlayMode = (C.OverlayMode + 1) % 4; iconOpen = false; SaveConfig(); }

            float dt = Time.unscaledDeltaTime;
            bool on = Mode > 0;
            show = Mathf.MoveTowards(show, on ? 1f : 0f, dt / (on ? 0.3f : 0.18f));
            open = Mathf.MoveTowards(open, Mode == 1 && iconOpen ? 1f : 0f, dt / 0.22f);
            if (on) { SystemMonitor.Start(); CaptureTiming(); }
            else if (show <= 0f) { SystemMonitor.Stop(); UiInputBlock.Clear(this); }

            MeasureFrame();
            if (show <= 0f) return;

            cpuBar = Approach(cpuBar, Pct(SystemMonitor.CpuTotal), 8f);
            gpuBar = Approach(gpuBar, Pct(SystemMonitor.Gpu3D), 8f);
            vramBar = Approach(vramBar, VramFrac(), 8f);
            ramBar = Approach(ramBar, Pct(SystemMonitor.RamLoad), 8f);
            flash = Mathf.MoveTowards(flash, 0f, dt / 0.6f);

            histTimer -= dt;
            if (histTimer <= 0f)
            {
                histTimer = 1f;
                hCpu[histHead] = Pct(SystemMonitor.CpuTotal); hGpu[histHead] = Pct(SystemMonitor.Gpu3D);
                hVram[histHead] = VramFrac(); hRam[histHead] = Pct(SystemMonitor.RamLoad);
                histHead = (histHead + 1) % HistN;
                if (histCount < HistN) histCount++;
            }

            textTimer -= dt;
            if (textTimer <= 0f) { textTimer = 0.25f; RefreshText(); }
        }

        private void CaptureTiming()
        {
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timing) > 0)
                {
                    gpuRing[timingHead] = lastGpu = (float)timing[0].gpuFrameTime;
                    // 메인 스레드 시간이 비어 오는 환경이 있다. 그때는 전체 CPU 프레임 시간으로 대신한다
                    double cm = timing[0].cpuMainThreadFrameTime;
                    cpuRing[timingHead] = lastCpu = (float)(cm > 0 ? cm : timing[0].cpuFrameTime);
                    timingSamples++;
                    timingHead = (timingHead + 1) % gpuRing.Length;
                }
            }
            catch { }
        }

        private static float Max(float[] a) { float m = 0; for (int i = 0; i < a.Length; i++) if (a[i] > m) m = a[i]; return m; }
        private static float VramFrac() { float t = SystemInfo.graphicsMemorySize; return t > 0 && SystemMonitor.VramUsedMB > 0 ? Mathf.Clamp01(SystemMonitor.VramUsedMB / t) : 0f; }
        private static float Pct(float v) { return v < 0 ? 0f : Mathf.Clamp01(v / 100f); }
        private static float Approach(float cur, float target, float speed) { return cur + (target - cur) * (1f - Mathf.Exp(-speed * Time.unscaledDeltaTime)); }
        private static float EaseOut(float t) { t = 1f - Mathf.Clamp01(t); return 1f - t * t * t; }
        private static void SaveConfig() { try { Main.Config.Save(Main.Entry); } catch { } }

        // ── 측정 ───────────────────────────────────────────────────────
        // 창이 꺼져 있어도 기록은 해 둔다(켰을 때 그래프가 비지 않게).
        private void MeasureFrame()
        {
            long now = Stopwatch.GetTimestamp();
            if (lastStamp == 0) { lastStamp = now; lastGc = GC.CollectionCount(0); return; }
            float ms = (float)((now - lastStamp) * 1000.0 / Stopwatch.Frequency);
            lastStamp = now;
            int gc = GC.CollectionCount(0);
            int gcDelta = gc - lastGc;
            lastGc = gc;

            // 게임·모드 시작 구간: 켠 뒤 최소 20초, 그리고 5초 연속 안정될 때까지. 모드 로딩 사이의 조용한 틈에
            // 끝났다고 보면, 뒤늦게 로딩하는 모드(Quartz 버전 불러오기 등)의 멈춤이 다시 "원인 불명" 으로 떴다.
            if (startup) { smooth = ms < 50f ? smooth + ms / 1000f : 0f; if (smooth > 5f && Time.realtimeSinceStartup > 20f) startup = false; }

            graph[graphHead] = ms; graphHead = (graphHead + 1) % GraphN;
            recent[recentHead] = ms; recentHead = (recentHead + 1) % LowN;
            if (recentCount < LowN) recentCount++;

            // 이번 곡 통계: 곡이 시작되면 새로 센다 (곡이 끝난 뒤에도 다음 곡까지 남겨 둔다)
            bool playing = Hitch.Playing;
            if (playing && !wasPlaying) { songMs = 0; songFrames = 0; songHitches = 0; songWorst = 0; }
            wasPlaying = playing;
            if (playing && ms < 1500f) { songMs += ms; songFrames++; if (ms > songWorst) songWorst = ms; }

            DecidePending();

            float limit = Mathf.Max(C != null ? C.AlertMs : 33f, avgMs * 2.2f);
            bool hitch = ms > limit;
            // 평소 프레임 기준. 튄 프레임도 조금씩은 반영해야 한다. 예전에는 튄 프레임을 아예 빼서, 맵 자체가
            // 계속 느린 곳(블렌드 장식 1755개, 매 프레임 90ms)에서는 모든 프레임이 끊김으로 잡혀 알림이 매 프레임 쌓였다.
            // 느린 프레임이 비슷한 길이로 연달아 오면(맵이 계속 무거운 것) 끊김은 처음 한 번만 세고,
            // 평균이 빨리 따라가게 해서 곧 "프레임 낮음" 알림 하나로 바뀌게 한다.
            bool sustained = hitch && prevHitch && ms < prevMs * 1.6f && ms > prevMs * 0.6f;
            prevHitch = hitch; prevMs = ms;
            avgMs = Mathf.Lerp(avgMs, Mathf.Min(ms, 1000f), sustained ? 0.25f : hitch ? 0.03f : 0.05f);
            CheckSlow();
            if (!hitch || sustained) return;
            if (Mode == 0) { hitchCount++; return; }
            // 모드 창(UMM, 이 모드의 설정 창)이 열려 있는 동안은 창 안에서 누르는 것(다른 모드 화면 열기, 설정 저장)이 멈춤을 만든다
            if (UmmOpen()) MarkLoading(T("모드 창 (UMM)", "Mod window (UMM)"));
            else if (SettingsWindow.Open) MarkLoading(T("설정 창", "Settings window"));

            // 불러오기 구간이면 바로 적는다
            var load = LoadingOrNull(ms);
            if (load != null) { Commit(load); return; }

            // 진짜 끊김: 상태 점과 횟수는 바로, 원인은 5프레임 뒤에 (GPU/CPU 시간이 늦게 오므로)
            hitchCount++;
            if (playing) songHitches++;
            lastHitchTime = Time.unscaledTime;
            lastHitchMs = ms;
            flash = 1f;
            bool modNow = ModCost.FrameMs >= ModCost.LastFrameMs;
            pending.Add(new Pending
            {
                Ms = ms, Gc = gcDelta, Frame = Time.frameCount, Time = Time.unscaledTime,
                Fx = (float)Math.Max(EffectScan.LastFrameEffectMs, EffectScan.FrameEffectMs),
                Mod = (float)(modNow ? ModCost.FrameMs : ModCost.LastFrameMs),
                ModWhat = modNow ? ModCost.Top : ModCost.LastTop,
            });
        }

        // ── 원인은 몇 프레임 뒤에 정한다 ─────────────────────────────────
        // 유니티는 한 프레임의 GPU/CPU 시간을 2~4 프레임 늦게 준다. 끊긴 그 순간에 판단하면 앞의 멀쩡한
        // 프레임 값을 보고 "게임은 한가했다(게임 바깥)" 로 잘못 말했다(다른 모드가 로딩하며 멈춘 것도 그렇게 떴다).
        // 끊김을 잡아 두고, 그 뒤 5 프레임 동안 들어온 값 중 가장 큰 것으로 정한다.
        private class Pending { public float Ms, Time, Fx, Mod, Gpu, Cpu; public int Gc, Frame; public string ModWhat; }
        private readonly List<Pending> pending = new List<Pending>();
        private float lastGpu, lastCpu;

        // ── 계속 느린 상태 ────────────────────────────────────────────
        // 순간 끊김이 아니라 프레임이 계속 낮은 경우(평균 40fps 아래가 1초 넘게)는 한 번만 알린다.
        private bool slow;
        private float slowSince = -1f;

        private void CheckSlow()
        {
            if (startup || InLoading || ImagePrefetch.Running) { slowSince = -1f; return; }
            bool low = avgMs > 25f;
            if (!low) { if (slow && avgMs < 20f) slow = false; slowSince = -1f; return; }
            if (slow) return;
            if (slowSince < 0) { slowSince = Time.unscaledTime; return; }
            if (Time.unscaledTime - slowSince < 1f) return;

            slow = true;
            float gpu = Max(gpuRing), cpu = Max(cpuRing);
            string why = gpu > avgMs * 0.7f ? T("GPU 과부하", "GPU overload") : cpu > avgMs * 0.6f ? T("게임 처리", "Game logic") : T("원인 불명", "Unknown");
            string detail = gpu > avgMs * 0.7f
                ? T("그래픽카드가 프레임마다 ", "The GPU takes ") + gpu.ToString("F0") + T("ms 걸립니다. 장식이나 필터가 많은 구간입니다", "ms per frame (many decorations or filters)")
                : T("평균 ", "Average ") + (1000f / avgMs).ToString("F0") + " FPS";
            var h = new HitchRec { Ms = avgMs, Time = Time.unscaledTime, Tone = Warn, Cause = T("프레임 낮음", "Low frame rate") + " · " + why, Detail = detail };
            string fps = (1000f / avgMs).ToString("F0") + " FPS";
            h.Title = h.Cause + "  (" + fps + ")";
            h.Short = fps + "   " + h.Cause;
            Commit(h);
        }

        private void DecidePending()
        {
            for (int i = 0; i < pending.Count; )
            {
                var p = pending[i];
                if (lastGpu > p.Gpu) p.Gpu = lastGpu;
                if (lastCpu > p.Cpu) p.Cpu = lastCpu;
                if (Time.frameCount - p.Frame < 5) { i++; continue; }
                pending.RemoveAt(i);
                Commit(Classify(p));
            }
        }

        // 문제 보고(로그 내보내기)용: 이번 실행의 끊김 기록을 줄 글로 남긴다 (최근 400개)
        private static readonly List<string> reportLines = new List<string>();
        private int totalLoading, totalMod;

        internal static string ReportText()
        {
            var sb = new System.Text.StringBuilder();
            var o = Instance;
            if (o == null) return "실시간 모니터 없음\n";
            sb.AppendLine("모니터 모드: " + Mode + " (0 끔, 1 아이콘, 2 미니, 3 상세)" + (Mode == 0 ? "  ※ 모니터가 꺼져 있으면 원인 기록이 남지 않는다" : ""));
            sb.AppendLine("끊김 " + o.hitchCount + "번 (모드 작업 " + o.totalMod + ", 불러오기로 분류 " + o.totalLoading + ")");
            if (o.songFrames > 0)
                sb.AppendLine(string.Format("마지막 곡: 평균 {0:F0} FPS, 최악 {1:F0}ms, 끊김 {2}번", 1000.0 * o.songFrames / System.Math.Max(1.0, o.songMs), o.songWorst, o.songHitches));
            sb.AppendLine();
            lock (reportLines) foreach (var l in reportLines) sb.AppendLine(l);
            return sb.ToString();
        }

        private void Commit(HitchRec h)
        {
            h.Time = Time.unscaledTime;   // 알림은 지금부터 센다
            if (h.IsLoading) totalLoading++; else if (h.IsMod) totalMod++;
            lock (reportLines)
            {
                reportLines.Add(string.Format("{0:HH:mm:ss} {1,5:F0}ms {2}{3}{4} | {5} | {6}", System.DateTime.Now, h.Ms,
                    h.IsLoading ? "[불러오기] " : "", h.IsMod ? "[모드] " : "", Hitch.Playing ? "[곡 중] " : "", h.Cause, h.Detail));
                if (reportLines.Count > 400) reportLines.RemoveAt(0);
            }
            history.Insert(0, h);
            if (history.Count > 6) history.RemoveAt(history.Count - 1);

            if (C.HitchAlerts)
            {
                // 같은 원인이 1.5초 안에 또 나면 새 카드를 쌓지 않고 "×2" 로 묶는다
                var top = toasts.Count > 0 ? toasts[0] : null;
                if (top != null && top.Cause == h.Cause && Time.unscaledTime - top.Time < 1.5f)
                {
                    top.Count++;
                    top.Ms = Mathf.Max(top.Ms, h.Ms);
                    top.Time = Time.unscaledTime;
                    top.Tone = top.IsLoading ? LoadTone : top.IsMod ? ModTone : top.Ms >= 50 ? Bad : Warn;
                    TitleOf(top);
                }
                else
                {
                    toasts.Insert(0, h);
                    if (toasts.Count > 3) { toastY.Remove(toasts[3]); toasts.RemoveAt(3); }
                }
            }
            textTimer = 0f;   // 목록을 바로 갱신
        }

        // ── 불러오기 구간 ──────────────────────────────────────────────
        // 게임·모드가 처음 뜨는 동안, 맵 불러오기, 곡 준비(재생/재시작), 화면 전환 직후에 튀는 프레임은
        // 끊김이 아니라 불러오기다. 예전에는 다른 모드들이 로딩하며 멈춘 것까지 "메모리 정리", "원인 불명",
        // "게임 바깥" 으로 떠서 헷갈렸다. 회색으로 따로 적고 끊김 수에는 넣지 않는다.
        private static int loadFrame = -1000;
        private static float loadTime = -999f;
        private static string loadWhat = "";
        private bool startup = true;   // 게임을 켠 뒤 최소 20초 + 프레임이 5초 동안 안정될 때까지
        private float smooth;

        internal static void MarkLoading(string what)
        {
            loadFrame = Time.frameCount;          // 긴 프레임은 시간으로 재면 창을 넘기므로 프레임 수로도 본다
            loadTime = Time.realtimeSinceStartup;
            loadWhat = what;
        }

        private static bool InLoading { get { return Time.frameCount - loadFrame <= 30 || Time.realtimeSinceStartup - loadTime < 2f; } }

        private HitchRec Loading(float ms, string cause, string detail)
        {
            var h = new HitchRec { Ms = ms, Time = Time.unscaledTime, IsLoading = true, Tone = LoadTone, Cause = cause, Detail = detail };
            TitleOf(h);
            return h;
        }

        private HitchRec LoadingOrNull(float ms)
        {
            if (startup)
                return Loading(ms, T("게임·모드 시작 중", "Game / mods starting"),
                    T("게임과 모드들이 처음 준비되는 중입니다. 끊김으로 세지 않습니다", "The game and mods are still loading; not counted as a hitch"));
            if (ms > 1500f || ImagePrefetch.Running || InLoading)
                return Loading(ms, T("불러오기", "Loading") + (loadWhat.Length > 0 ? " · " + loadWhat : ""),
                    T("맵이나 곡을 준비하느라 멈췄습니다. 끊김으로 세지 않습니다", "Preparing a level or scene; not counted as a hitch"));
            return null;
        }

        private HitchRec Classify(Pending p)
        {
            float ms = p.Ms, gpu = p.Gpu, cpuMain = p.Cpu, fx = p.Fx, mod = p.Mod;
            int gcDelta = p.Gc;
            string modWhat = p.ModWhat;

            var h = new HitchRec { Ms = ms, Time = Time.unscaledTime, Tone = ms >= 50 ? Bad : Warn };
            // 모드 때문인지를 가장 먼저 본다. 모드가 한 일은 원래 게임 일을 옮긴 것이어도 따로 알려야 판단할 수 있다.
            if (mod > ms * 0.35f && mod > 8f)
            {
                h.Cause = T("모드 작업", "Mod work"); h.IsMod = true; h.Tone = ModTone;
                h.Detail = "StutterFix · " + modWhat + " " + mod.ToString("F0") + "ms";
            }
            else if (gcDelta > 0) { h.Cause = T("메모리 정리", "Memory cleanup"); h.Detail = T("게임이 GC 로 메모리를 정리했습니다", "The game ran a garbage collection"); }
            else if (fx > ms * 0.4f) { h.Cause = T("효과 몰림", "Effect burst"); h.Detail = T("한 번에 시작된 효과들이 ", "Effects starting at once took ") + fx.ToString("F0") + T("ms 걸렸습니다", "ms"); }
            else if (gpu > ms * 0.7f) { h.Cause = T("GPU 과부하", "GPU overload"); h.Detail = T("그래픽카드가 ", "The GPU was busy for ") + gpu.ToString("F0") + T("ms 동안 바빴습니다 (필터가 많은 구간)", "ms (heavy filters)"); }
            else if (cpuMain > ms * 0.6f) { h.Cause = T("게임 처리", "Game logic"); h.Detail = T("게임 계산에 ", "Game code took ") + cpuMain.ToString("F0") + T("ms 걸렸습니다", "ms"); }
            else if (gpu <= 0 && cpuMain <= 0) { h.Cause = T("원인 불명", "Unknown"); h.Detail = T("프레임 시간을 아직 읽지 못했습니다", "Frame timing not available yet"); }
            else { h.Cause = T("게임 바깥", "Outside the game"); h.Detail = T("게임은 한가했습니다. 윈도우나 다른 프로그램일 수 있습니다", "The game was idle; likely Windows or another app"); }
            TitleOf(h);   // 알림 제목은 한 번만 만든다
            if (Edition.Dev)   // 원인 분류가 무엇을 보고 정했는지 남긴다 ("원인 불명" 추적용)
                Main.Entry.Logger.Log(string.Format("[모니터] {0:F0}ms -> {1} / gpu {2:F1} cpu {3:F1} (수집 {4}번) 효과 {5:F1} 모드 {6:F1}({7}) gc {8}",
                    ms, h.Cause, gpu, cpuMain, timingSamples, fx, mod, modWhat, gcDelta));
            if (Edition.Dev && gpu > ms * 0.7f)
                Main.Entry.Logger.Log("[모니터]   직전 필터 변화 (6프레임): " + FilterTrace.Recent(p.Frame, 6)
                    + string.Format(" | VRAM 전체 {0:F0}/{1}MB, 게임 전용 {2:F0}MB, 게임 공유(시스템 RAM) {3:F0}MB",
                        SystemMonitor.VramUsedMB, SystemInfo.graphicsMemorySize, SystemMonitor.VramGameMB, SystemMonitor.SharedGameMB));
            return h;
        }

        // 자세히: "끊김 42ms · 효과 몰림  ×3" (정도는 글로도), 간단: "42ms  효과 몰림  ×3"
        private static void TitleOf(HitchRec h)
        {
            string times = h.Count > 1 ? "  ×" + h.Count : "";
            if (h.IsLoading)   // 불러오기는 정도 대신 걸린 시간만
            {
                string len = h.Ms >= 1000 ? (h.Ms / 1000f).ToString("F1") + T("초", "s") : h.Ms.ToString("F0") + "ms";
                h.Title = h.Cause + "  " + len + times;
                h.Short = len + "   " + h.Cause + times;
                return;
            }
            string sev = h.Ms >= 100 ? T("큰 끊김", "Big hitch") : h.Ms >= 50 ? T("끊김", "Hitch") : T("짧은 끊김", "Short hitch");
            h.Title = sev + " " + h.Ms.ToString("F0") + "ms  ·  " + h.Cause + times;
            h.Short = h.Ms.ToString("F0") + "ms   " + h.Cause + times;
        }

        // ── 글자 (1초에 4번) ────────────────────────────────────────────
        private void RefreshText()
        {
            float sum = 0; int n = 0;
            for (int i = 1; i <= recentCount && sum < 500f; i++) { sum += recent[(recentHead - i + LowN) % LowN]; n++; }
            float avg = n > 0 ? sum / n : 0;
            sFps = avg > 0 ? (1000f / avg).ToString("F0") : "-";
            sMs = avg.ToString("F1") + " ms";
            sMsShort = avg.ToString("F1") + "ms";
            if (recentCount >= 30)
            {
                Array.Copy(recent, sortBuf, recentCount);
                Array.Sort(sortBuf, 0, recentCount);
                float p99 = sortBuf[Mathf.Clamp((int)(recentCount * 0.99f), 0, recentCount - 1)];
                sLow = "1% low " + (1000f / p99).ToString("F0");
                sLowShort = "1% " + (1000f / p99).ToString("F0");
            }

            sCpu = Val(SystemMonitor.CpuTotal, "%");
            sCpuShort = "CPU " + sCpu;
            sCpuSub = SystemMonitor.CpuGame >= 0 ? T("게임 ", "game ") + SystemMonitor.CpuGame.ToString("F0") + "%" : "";
            if (SystemMonitor.GpuAvailable)
            {
                sGpu = Val(SystemMonitor.Gpu3D, "%");
                sGpuSub = SystemMonitor.GpuGame >= 0 ? T("게임 ", "game ") + SystemMonitor.GpuGame.ToString("F0") + "%" : "";
                float total = SystemInfo.graphicsMemorySize;
                sVram = (SystemMonitor.VramUsedMB / 1024f).ToString("F1") + " / " + (total / 1024f).ToString("F1") + " GB";
                sVramShort = "VRAM " + (SystemMonitor.VramUsedMB / 1024f).ToString("F1") + "G";
                vramWarn = SystemMonitor.SharedGameMB > 400f;
                sVramSub = vramWarn ? T("넘침 ", "spill ") + SystemMonitor.SharedGameMB.ToString("F0") + "MB"
                                    : T("게임 ", "game ") + (SystemMonitor.VramGameMB / 1024f).ToString("F1") + "GB";
            }
            else { sGpu = "-"; sGpuSub = T("읽을 수 없음", "n/a"); sVram = "-"; sVramSub = ""; sVramShort = "VRAM -"; vramWarn = false; }
            sGpuShort = "GPU " + sGpu;
            sRam = Val(SystemMonitor.RamLoad, "%");
            sRamShort = "RAM " + sRam;
            sRamSub = SystemMonitor.RamGameMB > 0 ? T("게임 ", "game ") + (SystemMonitor.RamGameMB / 1024f).ToString("F1") + "GB" : "";
            sGc = GcControl.Paused ? T("미루는 중", "Deferred") : T("대기", "Idle");
            sGcSub = T("힙 ", "heap ") + (GC.GetTotalMemory(false) / 1073741824f).ToString("F2") + "GB";

            if (songFrames > 30)
            {
                sSong = (1000.0 * songFrames / songMs).ToString("F0") + " FPS";
                sSongSub = T("가장 긴 프레임 ", "worst ") + songWorst.ToString("F0") + "ms  ·  " + T("끊김 ", "hitches ") + songHitches;
            }
            else { sSong = "-"; sSongSub = T("곡을 시작하면 셉니다", "starts counting when a level plays"); }

            // 최근 끊김은 ms / 원인 / 몇 초 전을 칸으로 나눠 맞춘다
            if (sHist.Length != history.Count) { sHist = new string[history.Count]; sHistMs = new string[history.Count]; sHistAgo = new string[history.Count]; }
            for (int i = 0; i < history.Count; i++)
            {
                var hr = history[i];
                sHistMs[i] = hr.Ms >= 1000 ? (hr.Ms / 1000f).ToString("F1") + T("초", "s") : hr.Ms.ToString("F0") + "ms";
                sHist[i] = hr.Cause + (hr.Count > 1 ? "  ×" + hr.Count : "");
                sHistAgo[i] = Ago(Time.unscaledTime - hr.Time);
            }
            if (songFrames > 30)
            {
                sSongWorst = T("가장 긴 프레임 ", "worst ") + songWorst.ToString("F0") + "ms";
                sSongCount = T("끊김 ", "hitches ") + songHitches;
            }
            else { sSongWorst = T("곡을 시작하면", "starts when"); sSongCount = T("셉니다", "a level plays"); }
            sFooter = T("전체 끊김 ", "Hitches ") + hitchCount;
        }

        private static string Ago(float s) { return s < 60 ? s.ToString("F0") + T("초 전", "s ago") : (s / 60f).ToString("F0") + T("분 전", "m ago"); }
        private static string Val(float v, string unit) { return v < 0 ? "-" : v.ToString("F0") + unit; }

        // 아이콘/미니에 보여 줄 항목 (고른 순서대로)
        private readonly List<string> compact = new List<string>(6);
        private readonly List<float> compactLoad = new List<float>(6);   // 막대 색을 정하는 사용률 (-1 = 없음)
        private void CollectCompact()
        {
            compact.Clear(); compactLoad.Clear();
            if (C.CmMs) { compact.Add(sMsShort); compactLoad.Add(-1); }
            if (C.CmLow) { compact.Add(sLowShort); compactLoad.Add(-1); }
            if (C.CmCpu) { compact.Add(sCpuShort); compactLoad.Add(cpuBar); }
            if (C.CmGpu) { compact.Add(sGpuShort); compactLoad.Add(gpuBar); }
            if (C.CmVram) { compact.Add(sVramShort); compactLoad.Add(vramWarn ? 1f : vramBar); }
            if (C.CmRam) { compact.Add(sRamShort); compactLoad.Add(ramBar); }
        }

        // ── 모양 ───────────────────────────────────────────────────────
        private static Color Hex(int rgb, float a = 1f) { return new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a); }
        private static readonly Color Fg = new Color(1, 1, 1, 0.94f), Dim = new Color(1, 1, 1, 0.56f), Faint = new Color(1, 1, 1, 0.34f),
            Track = new Color(1, 1, 1, 0.10f), Bar = new Color(1, 1, 1, 0.85f), Good = Hex(0x5FD39B), Warn = Hex(0xF2B24B), Bad = Hex(0xFF6B6B),
            Base = new Color(0.06f, 0.065f, 0.08f, 1f);
        private static readonly Color ModTone = Hex(0xB39DFF);   // 모드 때문에 끊긴 것
        private static readonly Color LoadTone = Hex(0x8A8F9C);  // 불러오기 (끊김으로 세지 않음)

        private static Color LoadColor(float load, Color normal) { return load > 0.9f ? Bad : load > 0.75f ? Warn : normal; }

        private bool built;
        private Font font;
        private Texture2D tWhite;
        private GUIStyle sBig, sMid, sLabel, sValue, sSub, sSmall, sTitle, sDetail, sCenterBig, sCenterSmall, sCenterLine, sLine, sToastShort,
            stHistMs, stHistCause, sSubFaint;

        private void Build()
        {
            built = true;
            MarkLoading(SettingsWindow.T("모드 창 준비", "Preparing mod window"));   // 글꼴/모양 그림을 처음 만드는 프레임
            font = SettingsWindow.UiFont();
            tWhite = Texture2D.whiteTexture;
            sBig = Text(28, Fg, FontStyle.Bold);
            sMid = Text(18, Fg, FontStyle.Bold);
            sLabel = Text(12, Dim, FontStyle.Normal);
            sValue = Text(13, Fg, FontStyle.Bold); sValue.alignment = TextAnchor.UpperRight;
            sSub = Text(11, Faint, FontStyle.Normal); sSub.alignment = TextAnchor.UpperRight;
            sSmall = Text(11, Faint, FontStyle.Normal);
            sTitle = Text(13, Fg, FontStyle.Bold);
            sDetail = Text(11, Dim, FontStyle.Normal); sDetail.wordWrap = true;
            sCenterBig = Text(19, Fg, FontStyle.Bold); sCenterBig.alignment = TextAnchor.MiddleCenter;
            sCenterSmall = Text(9, Faint, FontStyle.Bold); sCenterSmall.alignment = TextAnchor.MiddleCenter;
            sCenterLine = Text(10, Dim, FontStyle.Bold); sCenterLine.alignment = TextAnchor.MiddleCenter;
            sLine = Text(12, Fg, FontStyle.Bold); sLine.alignment = TextAnchor.MiddleCenter;
            sToastShort = Text(12, Fg, FontStyle.Bold); sToastShort.alignment = TextAnchor.MiddleLeft;
            stHistMs = Text(12, Fg, FontStyle.Bold);
            stHistCause = Text(12, Dim, FontStyle.Normal); stHistCause.clipping = TextClipping.Clip;
            sSubFaint = Text(10, new Color(1, 1, 1, 0.22f), FontStyle.Normal); sSubFaint.alignment = TextAnchor.UpperRight;
        }

        private GUIStyle Text(int size, Color c, FontStyle style)
        {
            var s = new GUIStyle { font = font, fontSize = size, fontStyle = style, clipping = TextClipping.Overflow };
            s.normal.textColor = c;
            return s;
        }

        // 판 배경: 설정한 불투명도의 어두운 둥근 판 + 얇은 테두리
        private void Panel(Rect r, float radius)
        {
            float a = Mathf.Clamp(C.OverlayOpacity, 0.3f, 1f);
            Fill(r, new Color(Base.r, Base.g, Base.b, a), radius);
            Border(r, new Color(1, 1, 1, 0.09f), radius);
        }

        private void Fill(Rect r, Color c, float radius)
        {
            c.a *= GUI.color.a;
            GUI.DrawTexture(r, tWhite, ScaleMode.StretchToFill, true, 0, c, 0, radius);
        }

        private void Border(Rect r, Color c, float radius)
        {
            c.a *= GUI.color.a;
            GUI.DrawTexture(r, tWhite, ScaleMode.StretchToFill, true, 0, c, 1, radius);
        }

        private static void Label(Rect r, string s, GUIStyle st) { GUI.Label(r, s, st); }

        private void WithColor(GUIStyle st, Color c, Rect r, string s)
        {
            var old = st.normal.textColor;
            st.normal.textColor = c;
            GUI.Label(r, s, st);
            st.normal.textColor = old;
        }

        // ── 배치 ───────────────────────────────────────────────────────
        private const float IconW = 64, MiniH = 46, PW = 256;
        private float scale = 1f, sw, sh, warmedScale = -1f;
        private Rect widget;           // 끌어서 옮기는 본체(아이콘/미니/상세 패널)
        private bool right;

        private float IconHeight() { return 62 + compact.Count * 15; }

        private float MiniWidth()
        {
            float w = 16 + 96;                       // 점 + FPS
            for (int i = 0; i < compact.Count; i++) w += 74;
            return w + 70 + 14;                      // 그래프
        }

        private float PanelHeight()
        {
            float h = 66;                          // FPS 머리
            if (C.OvGraph) h += 54;
            if (C.OvSession) h += 52;
            int rows = (C.OvCpu ? 1 : 0) + (C.OvGpu ? 1 : 0) + (C.OvVram ? 1 : 0) + (C.OvRam ? 1 : 0);
            h += rows * RowH;
            if (C.OvGc) h += 26;
            if (C.OvHitchList) h += 34 + Mathf.Max(1, sHist.Length) * 18;
            return h + 34;                         // 아래 줄
        }

        private void OnGUI()
        {
            if (show <= 0f || C == null) return;
            if (!built) Build();
            GUI.depth = 10;   // 설정 창보다 뒤

            scale = Mathf.Clamp(Screen.height / 1080f, 0.8f, 2.2f) * Mathf.Clamp(C.OverlayScale, 0.7f, 1.6f);
            if (Mathf.Abs(scale - warmedScale) > 0.001f && SettingsWindow.WarmStyles(this, scale)) warmedScale = scale;   // 크기를 바꾸면 다시
            sw = Screen.width / scale; sh = Screen.height / scale;
            right = C.OverlayRight;
            if (Mode != 0) lastMode = Mode;
            int mode = lastMode;   // 사라지는 동안에는 마지막 모양을 유지한다
            CollectCompact();

            long drawStart = Stopwatch.GetTimestamp();
            var oldM = GUI.matrix; var oldC = GUI.color;
            float e = EaseOut(show);
            // 붙어 있는 쪽 바깥에서 미끄러져 들어온다
            GUI.matrix = Matrix4x4.TRS(new Vector3((right ? 1 : -1) * (1 - e) * 60f * scale, 0, 0), Quaternion.identity, new Vector3(scale, scale, 1));
            GUI.color = new Color(1, 1, 1, e);
            try
            {
                if (mode == 1) widget = IconRect();
                else if (mode == 2) widget = EdgeRect(MiniWidth(), MiniH, 12);
                else widget = EdgeRect(PW, PanelHeight(), 12);

                if (Mode != 0) HandleMouse(mode);

                if (Event.current.type == EventType.Repaint)
                {
                    Rect side = widget;
                    if (mode == 1)
                    {
                        DrawIcon(widget);
                        if (open > 0f) side = DrawPanelBeside(widget);
                    }
                    else if (mode == 2) DrawMini(widget);
                    else DrawPanel(widget);
                    DrawToasts(side);
                }
            }
            finally
            {
                GUI.matrix = oldM; GUI.color = oldC;
                if (Event.current.type == EventType.Repaint)   // 모니터 자신도 "모드 작업" 으로 센다
                    ModCost.Add(T("모니터 그리기", "Monitor drawing"), (Stopwatch.GetTimestamp() - drawStart) * 1000.0 / Stopwatch.Frequency);
            }
        }

        // 화면 끝에 붙은 탭: 바깥쪽 모서리는 화면 밖으로 넘겨 안쪽만 둥글게 보이게 한다
        private Rect IconRect()
        {
            float h = IconHeight();
            float y = Mathf.Lerp(16, sh - h - 16, Mathf.Clamp01(C.OverlayY));
            return right ? new Rect(sw - IconW, y, IconW + 16, h) : new Rect(-16, y, IconW + 16, h);
        }

        private Rect EdgeRect(float w, float h, float margin)
        {
            float y = Mathf.Lerp(16, sh - h - 16, Mathf.Clamp01(C.OverlayY));
            return right ? new Rect(sw - w - margin, y, w, h) : new Rect(margin, y, w, h);
        }

        // ── 마우스: 누르면 펼치기, 끌면 옮기기 ─────────────────────────────
        private int dragId;
        private bool dragging;
        private Vector2 downPos;
        private float grabDy;

        private void HandleMouse(int mode)
        {
            var ev = Event.current;
            Vector2 m = ev.mousePosition;
            // 곡 중에는 게임이 커서를 숨긴다. 마우스 클릭을 박자 입력으로 쓰는 사람도 있어서, 그때 아이콘이
            // 클릭을 가로채면 안 된다. 커서가 보일 때(편집 화면, 메뉴, 설정 창)만 누르고 끌 수 있다.
            if (!Cursor.visible && !dragging) { UiInputBlock.Clear(this); return; }

            Rect grab = mode == 3 ? new Rect(widget.x, widget.y, widget.width, 60) : widget;   // 상세는 머리만 잡힌다
            Rect hover = widget;
            if (mode == 1 && open > 0.5f) hover = Union(widget, PanelBesideRect(widget));
            UiInputBlock.Place(this, new Rect(hover.x * scale, hover.y * scale, hover.width * scale, hover.height * scale));   // 뒤의 게임이 클릭을 받지 않게

            if (dragId == 0) dragId = GUIUtility.GetControlID(FocusType.Passive);
            switch (ev.type)
            {
                case EventType.MouseDown:
                    if (ev.button == 0 && grab.Contains(m))
                    {
                        GUIUtility.hotControl = dragId;
                        downPos = m; dragging = false; grabDy = m.y - widget.y;
                        ev.Use();
                    }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == dragId)
                    {
                        if (!dragging && (m - downPos).sqrMagnitude > 16f) dragging = true;
                        if (dragging)
                        {
                            C.OverlayRight = m.x > sw / 2f;
                            float span = Mathf.Max(1f, sh - widget.height - 32);
                            C.OverlayY = Mathf.Clamp01((m.y - grabDy - 16) / span);
                        }
                        ev.Use();
                    }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == dragId)
                    {
                        GUIUtility.hotControl = 0;
                        if (dragging) SaveConfig();
                        else if (mode == 1) iconOpen = !iconOpen;   // 그냥 누르면 펼치기/접기
                        dragging = false;
                        ev.Use();
                    }
                    break;
            }
        }

        private static Rect Union(Rect a, Rect b)
        {
            return Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));
        }

        private Color StatusColor(out float pulse)
        {
            float since = Time.unscaledTime - lastHitchTime;
            pulse = since < 5f ? 0.6f + 0.4f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 5f)) : 1f;
            return since < 5f ? (lastHitchMs >= 50 ? Bad : Warn) : Good;
        }

        // ── 그리기: 아이콘 ────────────────────────────────────────────
        private void DrawIcon(Rect r)
        {
            bool hover = r.Contains(Event.current.mousePosition) && Cursor.visible;
            Panel(r, 14);
            if (hover || dragging) Fill(r, new Color(1, 1, 1, 0.05f), 14);
            if (flash > 0) Fill(r, new Color(Warn.r, Warn.g, Warn.b, 0.22f * flash), 14);

            float cx = right ? r.x : r.x + 16;          // 화면 안쪽으로 보이는 부분의 왼쪽 끝
            var inner = new Rect(cx, r.y, IconW, r.height);
            Label(new Rect(inner.x, inner.y + 9, inner.width, 22), sFps, sCenterBig);
            Label(new Rect(inner.x, inner.y + 30, inner.width, 12), "FPS", sCenterSmall);

            float pulse;
            Color dot = StatusColor(out pulse);
            Fill(new Rect(inner.xMax - 13, inner.y + 7, 6, 6), new Color(dot.r, dot.g, dot.b, pulse), 3);

            // 고른 항목: FPS 아래에 한 줄씩. 사용률이 높으면 주황/빨강
            float ly = inner.y + 44;
            for (int i = 0; i < compact.Count; i++)
            {
                if (i == 0) Fill(new Rect(inner.x + 10, ly - 1, inner.width - 20, 1), new Color(1, 1, 1, 0.07f), 0);
                WithColor(sCenterLine, compactLoad[i] < 0 ? Dim : LoadColor(compactLoad[i], Dim), new Rect(inner.x, ly + 1, inner.width, 13), compact[i]);
                ly += 15;
            }

            // 작은 그래프 (최근 24프레임)
            var g = new Rect(inner.x + 9, r.yMax - 14, inner.width - 18, 9);
            float bw = g.width / 24f;
            for (int i = 0; i < 24; i++)
            {
                float v = graph[(graphHead - 24 + i + GraphN) % GraphN];
                if (v <= 0) continue;
                float bh = Mathf.Max(1f, g.height * Mathf.Clamp01(v / 50f));
                Fill(new Rect(g.x + i * bw, g.yMax - bh, Mathf.Max(1f, bw - 0.8f), bh), BarColor(v, 0.5f), 0);
            }

            // 펼칠 수 있다는 표시 (안쪽 가장자리의 짧은 선)
            float lx = right ? inner.x + 3 : inner.xMax - 5;
            Fill(new Rect(lx, r.center.y - 8, 2, 16), new Color(1, 1, 1, hover ? 0.4f : 0.15f), 1);
        }

        private Rect PanelBesideRect(Rect icon)
        {
            float h = PanelHeight();
            float y = Mathf.Clamp(icon.center.y - h / 2f, 12, sh - h - 12);
            return right ? new Rect(icon.x - PW - 10, y, PW, h) : new Rect(icon.xMax + 10, y, PW, h);
        }

        private Rect DrawPanelBeside(Rect icon)
        {
            var target = PanelBesideRect(icon);
            float e = EaseOut(open);
            var old = GUI.color;
            GUI.color = new Color(old.r, old.g, old.b, old.a * e);
            // 아이콘 쪽에서 살짝 미끄러져 나온다
            var r = new Rect(target.x + (right ? 1 : -1) * (1 - e) * 18f, target.y, target.width, target.height);
            DrawPanel(r);
            GUI.color = old;
            return r;
        }

        // ── 그리기: 미니 ─────────────────────────────────────────────
        private void DrawMini(Rect r)
        {
            Panel(r, r.height / 2f);
            if (flash > 0) Fill(r, new Color(Warn.r, Warn.g, Warn.b, 0.2f * flash), r.height / 2f);
            float pulse;
            Color dot = StatusColor(out pulse);
            Fill(new Rect(r.x + 16, r.center.y - 3, 6, 6), new Color(dot.r, dot.g, dot.b, pulse), 3);
            Label(new Rect(r.x + 30, r.y + 10, 60, 24), sFps, sMid);
            Label(new Rect(r.x + 74, r.y + 17, 40, 14), "FPS", sSmall);

            // 고른 항목을 칸으로 나눈다
            float x = r.x + 16 + 96;
            for (int i = 0; i < compact.Count; i++)
            {
                Fill(new Rect(x, r.y + 12, 1, r.height - 24), new Color(1, 1, 1, 0.08f), 0);
                WithColor(sLine, compactLoad[i] < 0 ? Fg : LoadColor(compactLoad[i], Fg), new Rect(x, r.y, 74, r.height), compact[i]);
                x += 74;
            }
            Fill(new Rect(x, r.y + 12, 1, r.height - 24), new Color(1, 1, 1, 0.08f), 0);
            Graph(new Rect(x + 10, r.y + 12, 64, r.height - 24), 32, false);
        }

        // ── 그리기: 상세 패널 ──────────────────────────────────────────
        private void DrawPanel(Rect r)
        {
            Panel(r, 14);
            if (flash > 0) Fill(r, new Color(Warn.r, Warn.g, Warn.b, 0.16f * flash), 14);

            float ix = r.x + 18, iw = r.width - 36, cy = r.y + 14;
            float pulse;
            Color dot = StatusColor(out pulse);

            // 머리: 큰 FPS + 상태 점 / 오른쪽에 프레임 시간과 1% low
            Label(new Rect(ix, cy - 2, 120, 34), sFps, sBig);
            Fill(new Rect(ix, cy + 38, 6, 6), new Color(dot.r, dot.g, dot.b, pulse), 3);
            Label(new Rect(ix + 11, cy + 34, 60, 14), "FPS", sSmall);
            Label(new Rect(ix + iw - 120, cy + 6, 120, 16), sMs, sValue);
            Label(new Rect(ix + iw - 120, cy + 25, 120, 14), sLow, sSub);
            cy += 58;

            if (C.OvGraph) { Graph(new Rect(ix, cy, iw, 42), GraphN, true); cy += 54; }

            // 이번 곡: 왼쪽 두 줄(제목, 평균 FPS) / 오른쪽 두 줄(가장 긴 프레임, 끊김 수)
            if (C.OvSession)
            {
                var box = new Rect(ix, cy, iw, 42);
                Fill(box, new Color(1, 1, 1, 0.045f), 9);
                Label(new Rect(box.x + 12, box.y + 6, 100, 13), T("이번 곡", "This level"), sSmall);
                Label(new Rect(box.x + 12, box.y + 20, 110, 18), sSong, sTitle);
                Label(new Rect(box.xMax - 12 - 150, box.y + 7, 150, 14), sSongWorst, sSub);
                Label(new Rect(box.xMax - 12 - 150, box.y + 22, 150, 14), sSongCount, sSub);
                cy += 52;
            }

            if (C.OvCpu) cy = Row(ix, iw, cy, "CPU", sCpu, sCpuSub, cpuBar, false);
            if (C.OvGpu) cy = Row(ix, iw, cy, "GPU", sGpu, sGpuSub, gpuBar, false);
            if (C.OvVram) cy = Row(ix, iw, cy, "VRAM", sVram, sVramSub, vramBar, vramWarn);
            if (C.OvRam) cy = Row(ix, iw, cy, "RAM", sRam, sRamSub, ramBar, false);
            if (C.OvGc)
            {
                Label(new Rect(ix, cy + 2, 110, 16), T("메모리 정리", "GC"), sLabel);
                RightPair(ix, iw, cy, sGc, sGcSub, false);
                cy += 26;
            }

            // 최근 끊김: 점 | ms | 원인 ............ 몇 초 전
            if (C.OvHitchList)
            {
                Fill(new Rect(ix, cy + 4, iw, 1), new Color(1, 1, 1, 0.08f), 0);
                Label(new Rect(ix, cy + 12, iw, 14), T("최근 끊김", "Recent hitches"), sLabel);
                cy += 34;
                if (sHist.Length == 0) { Label(new Rect(ix, cy, iw, 14), T("아직 없음", "None yet"), sSmall); cy += 18; }
                for (int i = 0; i < sHist.Length && i < history.Count; i++)
                {
                    var hr = history[i];
                    float fade = i == 0 ? 1f : 0.75f;
                    Fill(new Rect(ix, cy + 5, 5, 5), hr.Tone, 2.5f);
                    WithColor(stHistMs, new Color(1, 1, 1, 0.9f * fade), new Rect(ix + 12, cy, 52, 15), sHistMs[i]);
                    WithColor(stHistCause, hr.IsMod ? ModTone : hr.IsLoading ? LoadTone : new Color(1, 1, 1, 0.7f * fade), new Rect(ix + 66, cy, iw - 66 - 58, 15), sHist[i]);
                    Label(new Rect(ix + iw - 58, cy, 58, 15), sHistAgo[i], sSub);
                    cy += 18;
                }
            }

            Fill(new Rect(ix, r.yMax - 30, iw, 1), new Color(1, 1, 1, 0.08f), 0);
            Label(new Rect(ix, r.yMax - 22, iw / 2, 14), sFooter, sSmall);
            Label(new Rect(ix + iw / 2, r.yMax - 22, iw / 2, 14), T("끌어서 옮기기", "drag to move"), sSubFaint);
        }

        // 최근 n 프레임 그래프: 16.7ms 기준선, 25ms 넘으면 주황, 50ms 넘으면 빨강
        private void Graph(Rect g, int n, bool box)
        {
            if (box) Fill(g, new Color(1, 1, 1, 0.04f), 6);
            float pad = box ? 4 : 0;
            float refY = g.yMax - pad - (g.height - pad * 2) * Mathf.Clamp01(16.7f / 50f);
            Fill(new Rect(g.x + pad, refY, g.width - pad * 2, 1), new Color(1, 1, 1, 0.08f), 0);
            float bw = (g.width - pad * 2) / n;
            for (int i = 0; i < n; i++)
            {
                float v = graph[(graphHead - n + i + GraphN * 2) % GraphN];
                if (v <= 0) continue;
                float bh = Mathf.Max(1.5f, (g.height - pad * 2) * Mathf.Clamp01(v / 50f));
                Fill(new Rect(g.x + pad + i * bw, g.yMax - pad - bh, Mathf.Max(1f, bw - 0.6f), bh), BarColor(v, 0.55f), 0);
            }
        }

        private static Color BarColor(float ms, float normalAlpha) { return ms >= 50 ? Bad : ms >= 25 ? Warn : new Color(1, 1, 1, normalAlpha); }

        private const float RowH = 34;
        private readonly GUIContent measure = new GUIContent();

        // 한 줄: 이름(왼쪽) ... 보조 값  값(오른쪽), 그 아래 폭 전체의 사용률 막대
        private float Row(float x, float w, float y, string label, string value, string sub, float bar, bool warn)
        {
            Label(new Rect(x, y + 2, 70, 16), label, sLabel);
            RightPair(x, w, y, value, sub, warn);
            var track = new Rect(x, y + 23, w, 3);
            Fill(track, Track, 1.5f);
            Color c = warn ? Warn : LoadColor(bar, Bar);
            Fill(new Rect(track.x, track.y, Mathf.Max(3f, track.width * bar), track.height), c, 1.5f);
            return y + RowH;
        }

        // 오른쪽 끝에 "보조 값   값" 을 한 줄로 (값은 굵게, 보조 값은 흐리게 바로 왼쪽에)
        private void RightPair(float x, float w, float y, string value, string sub, bool warn)
        {
            measure.text = value;   // 값 길이만큼 비운다 (곡 중엔 GC 가 멈춰 있으니 GUIContent 를 새로 만들지 않는다)
            float vw = sValue.CalcSize(measure).x;
            Label(new Rect(x + w - 160, y + 1, 160, 16), value, sValue);
            if (string.IsNullOrEmpty(sub)) return;
            var old = sSub.normal.textColor;
            if (warn) sSub.normal.textColor = Warn;
            Label(new Rect(x + w - 160 - vw - 8, y + 3, 160, 14), sub, sSub);
            sSub.normal.textColor = old;
        }

        // ── 알림 ───────────────────────────────────────────────────────
        // 모양: 간단(한 줄 알약) / 자세히(설명과 남은 시간 막대가 있는 카드)
        // 위치: 모니터 옆(화면 안쪽으로) / 화면 위 가운데 / 화면 아래 가운데. 들어오는 방향도 위치를 따른다.
        private void DrawToasts(Rect anchor)
        {
            float now = Time.unscaledTime;
            for (int i = toasts.Count - 1; i >= 0; i--)
                if (now - toasts[i].Time > ToastLife) { toastY.Remove(toasts[i]); toasts.RemoveAt(i); }
            if (toasts.Count == 0) return;

            bool detailed = C.AlertDetailed;
            float TW = detailed ? 272 : 236, TH = detailed ? 58 : 32, gap = detailed ? 8 : 6;
            int pos = C.AlertPos;

            float x, ty, dir = 1;   // dir: 쌓이는 방향 (1 아래로, -1 위로)
            Vector2 enter;          // 들어올 때 밀려 오는 방향
            if (pos == 1) { x = sw / 2f - TW / 2f; ty = 18; enter = new Vector2(0, -18); }
            else if (pos == 2) { x = sw / 2f - TW / 2f; ty = sh - 18 - TH; dir = -1; enter = new Vector2(0, 18); }
            else
            {
                x = right ? anchor.x - TW - 10 : anchor.xMax + 10;
                ty = Mathf.Clamp(anchor.y, 12, sh - toasts.Count * (TH + gap) - 12);
                enter = new Vector2(right ? 24 : -24, 0);
            }

            for (int i = 0; i < toasts.Count; i++)
            {
                var t = toasts[i];
                float y;
                if (!toastY.TryGetValue(t, out y)) y = ty;
                y = Approach(y, ty, 14f);   // 새 알림이 끼면 한 칸씩 밀려난다
                toastY[t] = y;

                float age = now - t.Time;
                float ein = EaseOut(age / 0.28f), outT = Mathf.Clamp01((ToastLife - age) / 0.5f);
                var r = new Rect(x + enter.x * (1 - ein), y + enter.y * (1 - ein), TW, TH);

                var old = GUI.color;
                GUI.color = new Color(old.r, old.g, old.b, old.a * ein * outT);
                if (detailed)
                {
                    Panel(r, 12);
                    Fill(new Rect(r.x + 10, r.y + 12, 3, r.height - 24), t.Tone, 1.5f);
                    WithColor(sTitle, t.IsMod ? ModTone : Fg, new Rect(r.x + 22, r.y + 9, r.width - 32, 18), t.Title);
                    Label(new Rect(r.x + 22, r.y + 29, r.width - 32, 24), t.Detail, sDetail);
                    float left = Mathf.Clamp01(1f - age / ToastLife);   // 사라지기까지 남은 시간
                    Fill(new Rect(r.x + 22, r.yMax - 5, (r.width - 44) * left, 2), new Color(t.Tone.r, t.Tone.g, t.Tone.b, 0.5f), 1);
                }
                else
                {
                    Panel(r, TH / 2f);
                    Fill(new Rect(r.x + 13, r.center.y - 3, 6, 6), t.Tone, 3);
                    WithColor(sToastShort, t.IsMod ? ModTone : Fg, new Rect(r.x + 26, r.y, r.width - 34, r.height), t.Short);
                }
                GUI.color = old;
                ty += dir * (TH + gap);
            }
        }

        private void OnDestroy() { SystemMonitor.Stop(); UiInputBlock.Remove(this); }
    }
}
