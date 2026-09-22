using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace StutterFix
{
    // 게임 화면 오른쪽 위에 띄워 두는 실시간 모니터 (Shift+Insert, 또는 설정 창 "모니터").
    //
    //   FPS / 프레임 시간 / 1% low, 최근 프레임 그래프
    //   CPU(전체·게임) / GPU 3D / VRAM(넘침 표시) / RAM / 메모리 정리(GC) 상태
    //   끊기면 아래에 알림 카드가 미끄러져 나와 "왜 끊겼는지"를 보여 준다
    //
    // 원인 분류 (그 프레임에 있었던 일로 가린다. 정밀 측정이 아니라 추정이다):
    //   GC 가 돌았다                       -> 메모리 정리
    //   효과 시작에 프레임의 40% 이상을 썼다 -> 효과 몰림
    //   GPU 가 프레임의 70% 이상 바빴다       -> GPU 과부하
    //   게임 메인 스레드가 60% 이상 바빴다    -> 게임 처리
    //   셋 다 아니다                       -> 게임 바깥 (윈도우, 다른 프로그램)
    //
    // 비용: 사용량은 SystemMonitor 가 작업 스레드에서 1초에 한 번 읽는다. 화면 글자는 1초에 4번만 새로 만든다
    // (곡 중에는 GC 가 멈춰 있어서, 매 프레임 문자열을 만들면 그대로 쌓인다).
    // 모양: 불투명도 75% 의 어두운 판, 흰 글자, 얇은 막대. 나타날 때 옆에서 미끄러져 들어온다.
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

        internal static void Destroy()
        {
            if (Instance == null) return;
            SystemMonitor.Stop();
            UnityEngine.Object.Destroy(Instance.gameObject);
            Instance = null;
        }

        private static bool Visible { get { return Main.Config != null && Main.Config.ShowOverlay; } }
        private static string T(string ko, string en) { return SettingsWindow.T(ko, en); }

        // ── 프레임 기록 ────────────────────────────────────────────────
        private const int GraphN = 90, LowN = 300;
        private readonly float[] graph = new float[GraphN];
        private int graphHead;
        private readonly float[] recent = new float[LowN];
        private readonly float[] sortBuf = new float[LowN];
        private int recentCount, recentHead;
        private long lastStamp;
        private float avgMs = 8f;
        private int lastGc;
        private int hitchCount;
        private float lastHitchTime = -999f;

        // ── 알림 ───────────────────────────────────────────────────────
        private class Toast { public string Title, Detail; public Color Tone; public float Born; public float Y = -1; }
        private readonly List<Toast> toasts = new List<Toast>();
        private const float ToastLife = 3.2f;

        // ── 화면에 보이는 값 (부드럽게 따라간다) ─────────────────────────
        private float show;                 // 창이 나타난 정도
        private float cpuBar, gpuBar, vramBar, ramBar, flash;
        private float textTimer;
        private string sFps = "", sMs = "", sLow = "", sCpu = "", sCpuSub = "", sGpu = "", sGpuSub = "", sVram = "", sVramSub = "",
            sRam = "", sRamSub = "", sGc = "", sGcSub = "", sFooter = "";
        private bool vramWarn;

        private void Update()
        {
            if (Main.Config == null) return;
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (shift && Input.GetKeyDown(Main.Config.WindowKey)) { Main.Config.ShowOverlay = !Main.Config.ShowOverlay; SaveConfig(); }

            float dt = Time.unscaledDeltaTime;
            show = Mathf.MoveTowards(show, Visible ? 1f : 0f, dt / (Visible ? 0.3f : 0.18f));
            if (Visible) SystemMonitor.Start();
            else if (show <= 0f) SystemMonitor.Stop();

            MeasureFrame();
            if (show <= 0f) return;

            cpuBar = Approach(cpuBar, Pct(SystemMonitor.CpuTotal), 8f);
            gpuBar = Approach(gpuBar, Pct(SystemMonitor.Gpu3D), 8f);
            float vramTotal = SystemInfo.graphicsMemorySize;
            vramBar = Approach(vramBar, vramTotal > 0 && SystemMonitor.VramUsedMB > 0 ? Mathf.Clamp01(SystemMonitor.VramUsedMB / vramTotal) : 0f, 8f);
            ramBar = Approach(ramBar, Pct(SystemMonitor.RamLoad), 8f);
            flash = Mathf.MoveTowards(flash, 0f, dt / 0.6f);

            textTimer -= dt;
            if (textTimer <= 0f) { textTimer = 0.25f; RefreshText(); }
        }

        private static float Pct(float v) { return v < 0 ? 0f : Mathf.Clamp01(v / 100f); }
        private static float Approach(float cur, float target, float speed) { return cur + (target - cur) * (1f - Mathf.Exp(-speed * Time.unscaledDeltaTime)); }
        private static void SaveConfig() { try { Main.Config.Save(Main.Entry); } catch { } }

        // 프레임 시간을 재고, 끊겼으면 원인을 가린다. 창이 꺼져 있어도 기록은 해 둔다(켰을 때 그래프가 비지 않게).
        private void MeasureFrame()
        {
            long now = Stopwatch.GetTimestamp();
            if (lastStamp == 0) { lastStamp = now; lastGc = GC.CollectionCount(0); return; }
            float ms = (float)((now - lastStamp) * 1000.0 / Stopwatch.Frequency);
            lastStamp = now;
            int gc = GC.CollectionCount(0);
            int gcDelta = gc - lastGc;
            lastGc = gc;

            graph[graphHead] = ms; graphHead = (graphHead + 1) % GraphN;
            recent[recentHead] = ms; recentHead = (recentHead + 1) % LowN;
            if (recentCount < LowN) recentCount++;

            bool hitch = ms > Mathf.Max(25f, avgMs * 2.2f);
            if (!hitch) avgMs = Mathf.Lerp(avgMs, ms, 0.05f);
            if (!hitch || !Visible || Main.Config == null || !Main.Config.HitchAlerts) { if (hitch) hitchCount++; return; }

            hitchCount++;
            lastHitchTime = Time.unscaledTime;
            flash = 1f;
            Classify(ms, gcDelta);
        }

        private void Classify(float ms, int gcDelta)
        {
            // 한참 멈춘 것은 끊김이 아니라 불러오기다
            if (ms > 1500f || ImagePrefetch.Running)
            {
                AddToast(T("불러오는 중", "Loading") + "  " + (ms / 1000f).ToString("F1") + T("초", "s"), T("맵이나 곡을 준비하느라 멈췄습니다", "The game paused to prepare a level"), Hex(0x8A8F9C));
                hitchCount--;
                return;
            }

            float gpu = 0, cpuMain = 0;
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timing) > 0) { gpu = (float)timing[0].gpuFrameTime; cpuMain = (float)timing[0].cpuMainThreadFrameTime; }
            }
            catch { }
            float fx = (float)Math.Max(EffectScan.LastFrameEffectMs, EffectScan.FrameEffectMs);

            string title = T("끊김 ", "Hitch ") + ms.ToString("F0") + "ms";
            Color tone = ms >= 50 ? Hex(0xFF6B6B) : Hex(0xF2B24B);
            string cause, detail;
            if (gcDelta > 0) { cause = T("메모리 정리 (GC)", "Memory cleanup (GC)"); detail = T("게임이 메모리를 정리했습니다", "The game ran a garbage collection"); }
            else if (fx > ms * 0.4f) { cause = T("효과 몰림", "Effect burst"); detail = T("효과 시작에 ", "Starting effects took ") + fx.ToString("F0") + "ms"; }
            else if (gpu > ms * 0.7f) { cause = T("GPU 과부하", "GPU overload"); detail = T("그래픽카드가 ", "The GPU took ") + gpu.ToString("F0") + T("ms 동안 바빴습니다", "ms"); }
            else if (cpuMain > ms * 0.6f) { cause = T("게임 처리 (CPU)", "Game logic (CPU)"); detail = T("게임 계산에 ", "Game code took ") + cpuMain.ToString("F0") + "ms"; }
            else if (gpu <= 0 && cpuMain <= 0) { cause = T("원인 불명", "Unknown"); detail = T("이 환경에서는 프레임 시간을 읽을 수 없습니다", "Frame timing is not available here"); }
            else { cause = T("게임 바깥", "Outside the game"); detail = T("게임은 한가했습니다. 윈도우나 다른 프로그램일 수 있습니다", "The game was idle; likely Windows or another app"); }
            AddToast(title + "  ·  " + cause, detail, tone);
        }

        private readonly FrameTiming[] timing = new FrameTiming[1];

        private void AddToast(string title, string detail, Color tone)
        {
            toasts.Insert(0, new Toast { Title = title, Detail = detail, Tone = tone, Born = Time.unscaledTime });
            if (toasts.Count > 3) toasts.RemoveAt(toasts.Count - 1);
        }

        // 글자는 1초에 4번만 새로 만든다
        private void RefreshText()
        {
            // FPS 는 최근 0.5초 평균, 1% low 는 최근 300프레임 중 느린 쪽 1%
            float sum = 0; int n = 0;
            for (int i = 1; i <= recentCount && sum < 500f; i++) { sum += recent[(recentHead - i + LowN) % LowN]; n++; }
            float avg = n > 0 ? sum / n : 0;
            sFps = avg > 0 ? (1000f / avg).ToString("F0") : "-";
            sMs = avg.ToString("F1") + " ms";
            if (recentCount >= 30)
            {
                Array.Copy(recent, sortBuf, recentCount);
                Array.Sort(sortBuf, 0, recentCount);
                float p99 = sortBuf[Mathf.Clamp((int)(recentCount * 0.99f), 0, recentCount - 1)];
                sLow = "1% " + (1000f / p99).ToString("F0");
            }

            sCpu = Val(SystemMonitor.CpuTotal, "%");
            sCpuSub = SystemMonitor.CpuGame >= 0 ? T("게임 ", "game ") + SystemMonitor.CpuGame.ToString("F0") + "%" : "";
            if (SystemMonitor.GpuAvailable)
            {
                sGpu = Val(SystemMonitor.Gpu3D, "%");
                sGpuSub = SystemMonitor.GpuGame >= 0 ? T("게임 ", "game ") + SystemMonitor.GpuGame.ToString("F0") + "%" : "";
                float total = SystemInfo.graphicsMemorySize;
                sVram = (SystemMonitor.VramUsedMB / 1024f).ToString("F1") + " / " + (total / 1024f).ToString("F1") + " GB";
                vramWarn = SystemMonitor.SharedGameMB > 400f;
                sVramSub = vramWarn ? T("넘침 ", "spill ") + SystemMonitor.SharedGameMB.ToString("F0") + "MB"
                                    : T("게임 ", "game ") + (SystemMonitor.VramGameMB / 1024f).ToString("F1") + "GB";
            }
            else { sGpu = "-"; sGpuSub = T("읽을 수 없음", "unavailable"); sVram = "-"; sVramSub = ""; vramWarn = false; }
            sRam = Val(SystemMonitor.RamLoad, "%");
            sRamSub = SystemMonitor.RamGameMB > 0 ? T("게임 ", "game ") + (SystemMonitor.RamGameMB / 1024f).ToString("F1") + "GB" : "";

            sGc = GcControl.Paused ? T("미루는 중", "Deferred") : T("대기", "Idle");
            sGcSub = T("힙 ", "heap ") + (GC.GetTotalMemory(false) / 1073741824f).ToString("F2") + "GB";

            float ago = Time.unscaledTime - lastHitchTime;
            sFooter = T("끊김 ", "Hitches ") + hitchCount + (hitchCount > 0 && ago < 3600
                ? "  ·  " + (ago < 60 ? ago.ToString("F0") + T("초 전", "s ago") : (ago / 60f).ToString("F0") + T("분 전", "m ago")) : "");
        }

        private static string Val(float v, string unit) { return v < 0 ? "-" : v.ToString("F0") + unit; }

        // ── 그리기 ─────────────────────────────────────────────────────
        private bool built;
        private Font font;
        private GUIStyle sPanel, sToast, sBig, sLabel, sValue, sSub, sSmall, sToastTitle, sToastDetail;
        private Texture2D tWhite;

        private static Color Hex(int rgb, float a = 1f) { return new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a); }
        private static readonly Color Fg = new Color(1, 1, 1, 0.94f), Dim = new Color(1, 1, 1, 0.55f), Faint = new Color(1, 1, 1, 0.32f),
            Track = new Color(1, 1, 1, 0.10f), Bar = new Color(1, 1, 1, 0.85f), Warn = Hex(0xF2B24B), Bad = Hex(0xFF6B6B);

        private const float PW = 250f;

        private void Build()
        {
            built = true;
            font = SettingsWindow.UiFont();
            tWhite = Texture2D.whiteTexture;
            sPanel = Box(SettingsWindow.Card(new Color(0.06f, 0.065f, 0.08f, 0.75f), new Color(1, 1, 1, 0.09f), 14, 1, 0, 0f), 16);
            sToast = Box(SettingsWindow.Card(new Color(0.06f, 0.065f, 0.08f, 0.82f), new Color(1, 1, 1, 0.09f), 12, 1, 0, 0f), 14);
            sBig = Text(30, Fg, FontStyle.Bold);
            sLabel = Text(12, Dim, FontStyle.Normal);
            sValue = Text(13, Fg, FontStyle.Bold); sValue.alignment = TextAnchor.UpperRight;
            sSub = Text(11, Faint, FontStyle.Normal); sSub.alignment = TextAnchor.UpperRight;
            sSmall = Text(11, Faint, FontStyle.Normal);
            sToastTitle = Text(13, Fg, FontStyle.Bold);
            sToastDetail = Text(11, Dim, FontStyle.Normal); sToastDetail.wordWrap = true;
        }

        private GUIStyle Box(Texture2D bg, int border)
        {
            var s = new GUIStyle();
            s.normal.background = bg;
            s.border = new RectOffset(border, border, border, border);
            return s;
        }

        private GUIStyle Text(int size, Color c, FontStyle style)
        {
            var s = new GUIStyle { font = font, fontSize = size, fontStyle = style, richText = false, clipping = TextClipping.Overflow };
            s.normal.textColor = c;
            return s;
        }

        private void OnGUI()
        {
            if (show <= 0f || Event.current.type != EventType.Repaint) return;   // 누를 것이 없어 그리기 이벤트만 쓴다
            if (!built) Build();
            GUI.depth = 10;   // 설정 창보다 뒤

            float scale = Mathf.Clamp(Screen.height / 1080f, 0.8f, 2.2f);
            float e = 1f - Mathf.Pow(1f - show, 3f);
            var oldM = GUI.matrix; var oldC = GUI.color;
            GUI.matrix = Matrix4x4.TRS(new Vector3((1 - e) * 40f * scale, 0, 0), Quaternion.identity, new Vector3(scale, scale, 1));
            GUI.color = new Color(1, 1, 1, e);
            try
            {
                float x = Screen.width / scale - PW - 18f, y = 18f;
                float h = DrawPanel(x, y);
                DrawToasts(x, y + h + 10f);
            }
            finally { GUI.matrix = oldM; GUI.color = oldC; }
        }

        private float DrawPanel(float x, float y)
        {
            const float H = 318f;
            sPanel.Draw(new Rect(x, y, PW, H), false, false, false, false);
            if (flash > 0) Fill(new Rect(x, y, PW, H), new Color(Warn.r, Warn.g, Warn.b, 0.18f * flash), 14);   // 끊기면 잠깐 빛난다

            float ix = x + 16, iw = PW - 32, cy = y + 12;
            GUI.Label(new Rect(ix, cy, 120, 36), sFps, sBig);
            GUI.Label(new Rect(ix + 2, cy + 36, 60, 16), "FPS", sSmall);
            GUI.Label(new Rect(ix + iw - 110, cy + 6, 110, 16), sMs, sValue);
            GUI.Label(new Rect(ix + iw - 110, cy + 24, 110, 16), sLow, sSub);
            cy += 60;

            // 최근 프레임 그래프: 16.7ms 기준선, 25ms 넘으면 주황, 50ms 넘으면 빨강
            var g = new Rect(ix, cy, iw, 44);
            Fill(g, new Color(1, 1, 1, 0.04f), 6);
            float refY = g.yMax - g.height * Mathf.Clamp01(16.7f / 50f);
            Fill(new Rect(g.x + 4, refY, g.width - 8, 1), new Color(1, 1, 1, 0.08f), 0);
            float bw = (g.width - 8) / GraphN;
            for (int i = 0; i < GraphN; i++)
            {
                float v = graph[(graphHead + i) % GraphN];
                if (v <= 0) continue;
                float bh = Mathf.Max(1.5f, (g.height - 6) * Mathf.Clamp01(v / 50f));
                Color c = v >= 50 ? Bad : v >= 25 ? Warn : new Color(1, 1, 1, 0.55f);
                Fill(new Rect(g.x + 4 + i * bw, g.yMax - 3 - bh, Mathf.Max(1f, bw - 0.6f), bh), c, 0);
            }
            cy += 56;

            cy = Row(ix, iw, cy, "CPU", sCpu, sCpuSub, cpuBar, false);
            cy = Row(ix, iw, cy, "GPU", sGpu, sGpuSub, gpuBar, false);
            cy = Row(ix, iw, cy, "VRAM", sVram, sVramSub, vramBar, vramWarn);
            cy = Row(ix, iw, cy, "RAM", sRam, sRamSub, ramBar, false);

            GUI.Label(new Rect(ix, cy + 2, 120, 16), T("메모리 정리", "GC"), sLabel);
            GUI.Label(new Rect(ix + iw - 130, cy + 1, 130, 16), sGc, sValue);
            GUI.Label(new Rect(ix + iw - 130, cy + 18, 130, 14), sGcSub, sSub);
            cy += 40;

            Fill(new Rect(ix, cy, iw, 1), new Color(1, 1, 1, 0.08f), 0);
            GUI.Label(new Rect(ix, cy + 8, iw, 14), sFooter, sSmall);
            return H;
        }

        private float Row(float x, float w, float y, string label, string value, string sub, float bar, bool warn)
        {
            GUI.Label(new Rect(x, y + 2, 60, 16), label, sLabel);
            GUI.Label(new Rect(x + w - 150, y + 1, 150, 16), value, sValue);
            if (!string.IsNullOrEmpty(sub))
            {
                var old = sSub.normal.textColor;
                if (warn) sSub.normal.textColor = Warn;
                GUI.Label(new Rect(x + 44, y + 3, w - 44 - 90, 14), sub, sSub);
                sSub.normal.textColor = old;
            }
            var track = new Rect(x, y + 22, w, 3);
            Fill(track, Track, 1.5f);
            Color c = warn ? Warn : bar > 0.9f ? Bad : bar > 0.75f ? Warn : Bar;
            Fill(new Rect(track.x, track.y, Mathf.Max(3f, track.width * bar), track.height), c, 1.5f);
            return y + 34;
        }

        private void DrawToasts(float x, float y)
        {
            float now = Time.unscaledTime;
            for (int i = toasts.Count - 1; i >= 0; i--)
                if (now - toasts[i].Born > ToastLife) toasts.RemoveAt(i);

            float ty = y;
            for (int i = 0; i < toasts.Count; i++)
            {
                var t = toasts[i];
                if (t.Y < 0) t.Y = ty;
                t.Y = Approach(t.Y, ty, 14f);   // 새 알림이 위에 끼면 아래로 밀려난다
                float age = now - t.Born;
                float inT = Mathf.Clamp01(age / 0.28f), outT = Mathf.Clamp01((ToastLife - age) / 0.45f);
                float ein = 1f - Mathf.Pow(1f - inT, 3f);
                float a = ein * outT;
                var r = new Rect(x + (1 - ein) * 30f, t.Y, PW, 58);

                var oldC = GUI.color;
                GUI.color = new Color(oldC.r, oldC.g, oldC.b, oldC.a * a);
                sToast.Draw(r, false, false, false, false);
                Fill(new Rect(r.x + 10, r.y + 12, 3, r.height - 24), t.Tone, 1.5f);
                GUI.Label(new Rect(r.x + 22, r.y + 10, r.width - 32, 18), t.Title, sToastTitle);
                GUI.Label(new Rect(r.x + 22, r.y + 30, r.width - 32, 24), t.Detail, sToastDetail);
                GUI.color = oldC;
                ty += 66;
            }
        }

        // 색 인자 DrawTexture 는 GUI.color 투명도를 따르지 않으므로 직접 곱한다
        private void Fill(Rect r, Color c, float radius)
        {
            c.a *= GUI.color.a;
            GUI.DrawTexture(r, tWhite, ScaleMode.StretchToFill, true, 0, c, 0, radius);
        }

        private void OnDestroy() { SystemMonitor.Stop(); }
    }
}
