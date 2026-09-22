using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;

namespace StutterFix
{
    // 게임 화면에 띄워 두는 실시간 모니터.
    //
    // 표시 방식 (Shift+Insert 로 차례로 바뀐다, 설정 창 "모니터" 에서도 고른다)
    //   아이콘 : 화면 끝 가운데의 작은 탭. FPS, 상태 점(초록/주황/빨강), 작은 그래프. 누르면 상세 패널이 옆으로 펼쳐진다
    //   미니   : 한 줄짜리 알약. FPS, 프레임 시간, 1% low, 그래프
    //   상세   : FPS, 그래프, CPU/GPU/VRAM/RAM/GC, 최근 끊김 목록
    // 아이콘/미니/패널 머리를 잡고 끌면 위아래로 옮겨지고, 화면 반대쪽으로 끌면 그쪽 끝에 붙는다.
    // 불투명도, 크기, 보여 줄 항목은 설정 창에서 고친다.
    //
    // 끊김 원인 (그 프레임에 있었던 일로 가린다. 정밀 측정이 아니라 추정이다):
    //   GC 가 돌았다 -> 메모리 정리 / 효과 시작에 프레임의 40% 이상 -> 효과 몰림 /
    //   GPU 가 70% 이상 바빴다 -> GPU 과부하 / 메인 스레드가 60% 이상 -> 게임 처리 / 모두 아니다 -> 게임 바깥
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

        internal static void Destroy()
        {
            if (Instance == null) return;
            SystemMonitor.Stop();
            UiInputBlock.Set(Instance, false);
            UnityEngine.Object.Destroy(Instance.gameObject);
            Instance = null;
        }

        private static Settings C { get { return Main.Config; } }
        private static int Mode { get { return C == null ? 0 : C.OverlayMode; } }
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
        private float lastHitchTime = -999f, lastHitchMs;

        private class HitchRec { public float Ms, Time; public string Cause, Detail, Title; public Color Tone; }
        private readonly List<HitchRec> history = new List<HitchRec>();   // 최근 끊김 (상세 패널 목록)
        private readonly List<HitchRec> toasts = new List<HitchRec>();    // 떠 있는 알림
        private readonly Dictionary<HitchRec, float> toastY = new Dictionary<HitchRec, float>();
        private const float ToastLife = 3.2f;

        // ── 화면 상태 ──────────────────────────────────────────────────
        private float show;          // 전체가 나타난 정도
        private float open;          // 아이콘에서 상세 패널이 펼쳐진 정도
        private bool iconOpen;
        private int lastMode = 1;
        private float cpuBar, gpuBar, vramBar, ramBar, flash;
        private float textTimer;
        private string sFps = "-", sMs = "", sLow = "", sCpu = "", sCpuSub = "", sGpu = "", sGpuSub = "", sVram = "", sVramSub = "",
            sRam = "", sRamSub = "", sGc = "", sGcSub = "", sFooter = "";
        private string[] sHist = new string[0];
        private bool vramWarn;

        private void Update()
        {
            if (C == null) return;
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (shift && Input.GetKeyDown(C.WindowKey)) { C.OverlayMode = (C.OverlayMode + 1) % 4; iconOpen = false; SaveConfig(); }

            float dt = Time.unscaledDeltaTime;
            bool on = Mode > 0;
            show = Mathf.MoveTowards(show, on ? 1f : 0f, dt / (on ? 0.3f : 0.18f));
            open = Mathf.MoveTowards(open, Mode == 1 && iconOpen ? 1f : 0f, dt / 0.22f);
            if (on) SystemMonitor.Start();
            else if (show <= 0f) { SystemMonitor.Stop(); UiInputBlock.Set(this, false); }

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

            graph[graphHead] = ms; graphHead = (graphHead + 1) % GraphN;
            recent[recentHead] = ms; recentHead = (recentHead + 1) % LowN;
            if (recentCount < LowN) recentCount++;

            bool hitch = ms > Mathf.Max(25f, avgMs * 2.2f);
            if (!hitch) { avgMs = Mathf.Lerp(avgMs, ms, 0.05f); return; }
            if (Mode == 0) { hitchCount++; return; }

            var h = Classify(ms, gcDelta);
            if (h == null) return;   // 불러오기
            hitchCount++;
            lastHitchTime = Time.unscaledTime;
            lastHitchMs = ms;
            flash = 1f;
            history.Insert(0, h);
            if (history.Count > 6) history.RemoveAt(history.Count - 1);
            if (C.HitchAlerts)
            {
                toasts.Insert(0, h);
                if (toasts.Count > 3) { toastY.Remove(toasts[3]); toasts.RemoveAt(3); }
            }
            textTimer = 0f;   // 목록을 바로 갱신
        }

        private readonly FrameTiming[] timing = new FrameTiming[1];

        private HitchRec Classify(float ms, int gcDelta)
        {
            // 한참 멈춘 것은 끊김이 아니라 불러오기다
            if (ms > 1500f || ImagePrefetch.Running) return null;

            float gpu = 0, cpuMain = 0;
            try
            {
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timing) > 0) { gpu = (float)timing[0].gpuFrameTime; cpuMain = (float)timing[0].cpuMainThreadFrameTime; }
            }
            catch { }
            float fx = (float)Math.Max(EffectScan.LastFrameEffectMs, EffectScan.FrameEffectMs);

            var h = new HitchRec { Ms = ms, Time = Time.unscaledTime, Tone = ms >= 50 ? Bad : Warn };
            if (gcDelta > 0) { h.Cause = T("메모리 정리", "Memory cleanup"); h.Detail = T("게임이 GC 로 메모리를 정리했습니다", "The game ran a garbage collection"); }
            else if (fx > ms * 0.4f) { h.Cause = T("효과 몰림", "Effect burst"); h.Detail = T("효과 시작에 ", "Starting effects took ") + fx.ToString("F0") + "ms"; }
            else if (gpu > ms * 0.7f) { h.Cause = T("GPU 과부하", "GPU overload"); h.Detail = T("그래픽카드가 ", "The GPU was busy for ") + gpu.ToString("F0") + T("ms 동안 바빴습니다", "ms"); }
            else if (cpuMain > ms * 0.6f) { h.Cause = T("게임 처리", "Game logic"); h.Detail = T("게임 계산에 ", "Game code took ") + cpuMain.ToString("F0") + "ms"; }
            else if (gpu <= 0 && cpuMain <= 0) { h.Cause = T("원인 불명", "Unknown"); h.Detail = T("이 환경에서는 프레임 시간을 읽을 수 없습니다", "Frame timing is not available here"); }
            else { h.Cause = T("게임 바깥", "Outside the game"); h.Detail = T("게임은 한가했습니다. 윈도우나 다른 프로그램일 수 있습니다", "The game was idle; likely Windows or another app"); }
            h.Title = ms.ToString("F0") + "ms  ·  " + h.Cause;   // 알림 제목은 한 번만 만든다
            return h;
        }

        // ── 글자 (1초에 4번) ────────────────────────────────────────────
        private void RefreshText()
        {
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
            else { sGpu = "-"; sGpuSub = T("읽을 수 없음", "n/a"); sVram = "-"; sVramSub = ""; vramWarn = false; }
            sRam = Val(SystemMonitor.RamLoad, "%");
            sRamSub = SystemMonitor.RamGameMB > 0 ? T("게임 ", "game ") + (SystemMonitor.RamGameMB / 1024f).ToString("F1") + "GB" : "";
            sGc = GcControl.Paused ? T("미루는 중", "Deferred") : T("대기", "Idle");
            sGcSub = T("힙 ", "heap ") + (GC.GetTotalMemory(false) / 1073741824f).ToString("F2") + "GB";

            if (sHist.Length != history.Count) sHist = new string[history.Count];
            for (int i = 0; i < history.Count; i++)
                sHist[i] = history[i].Ms.ToString("F0") + "ms  " + history[i].Cause + "  ·  " + Ago(Time.unscaledTime - history[i].Time);
            sFooter = T("끊김 ", "Hitches ") + hitchCount + "   ·   " + T("끌어서 옮기기", "drag to move");
        }

        private static string Ago(float s) { return s < 60 ? s.ToString("F0") + T("초 전", "s ago") : (s / 60f).ToString("F0") + T("분 전", "m ago"); }
        private static string Val(float v, string unit) { return v < 0 ? "-" : v.ToString("F0") + unit; }

        // ── 모양 ───────────────────────────────────────────────────────
        private static Color Hex(int rgb, float a = 1f) { return new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a); }
        private static readonly Color Fg = new Color(1, 1, 1, 0.94f), Dim = new Color(1, 1, 1, 0.56f), Faint = new Color(1, 1, 1, 0.34f),
            Track = new Color(1, 1, 1, 0.10f), Bar = new Color(1, 1, 1, 0.85f), Good = Hex(0x5FD39B), Warn = Hex(0xF2B24B), Bad = Hex(0xFF6B6B),
            Base = new Color(0.06f, 0.065f, 0.08f, 1f);

        private bool built;
        private Font font;
        private Texture2D tWhite;
        private GUIStyle sBig, sMid, sLabel, sValue, sSub, sSmall, sTitle, sDetail, sCenterBig, sCenterSmall;

        private void Build()
        {
            built = true;
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

        // ── 배치 ───────────────────────────────────────────────────────
        private const float IconW = 58, IconH = 66, MiniW = 262, MiniH = 46, PW = 252;
        private float scale = 1f, sw, sh;
        private Rect widget;           // 끌어서 옮기는 본체(아이콘/미니/상세 패널)
        private bool right;

        private float PanelHeight()
        {
            float h = 64;                          // FPS 머리
            if (C.OvGraph) h += 56;
            int rows = (C.OvCpu ? 1 : 0) + (C.OvGpu ? 1 : 0) + (C.OvVram ? 1 : 0) + (C.OvRam ? 1 : 0);
            h += rows * 34;
            if (C.OvGc) h += 38;
            if (C.OvHitchList) h += 26 + Mathf.Max(1, sHist.Length) * 17;
            return h + 30;                         // 아래 줄
        }

        private void OnGUI()
        {
            if (show <= 0f || C == null) return;
            if (!built) Build();
            GUI.depth = 10;   // 설정 창보다 뒤

            scale = Mathf.Clamp(Screen.height / 1080f, 0.8f, 2.2f) * Mathf.Clamp(C.OverlayScale, 0.7f, 1.6f);
            sw = Screen.width / scale; sh = Screen.height / scale;
            right = C.OverlayRight;
            if (Mode != 0) lastMode = Mode;
            int mode = lastMode;   // 사라지는 동안에는 마지막 모양을 유지한다

            var oldM = GUI.matrix; var oldC = GUI.color;
            float e = EaseOut(show);
            // 붙어 있는 쪽 바깥에서 미끄러져 들어온다
            GUI.matrix = Matrix4x4.TRS(new Vector3((right ? 1 : -1) * (1 - e) * 60f * scale, 0, 0), Quaternion.identity, new Vector3(scale, scale, 1));
            GUI.color = new Color(1, 1, 1, e);
            try
            {
                if (mode == 1) widget = IconRect();
                else if (mode == 2) widget = EdgeRect(MiniW, MiniH, 12);
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
            finally { GUI.matrix = oldM; GUI.color = oldC; }
        }

        // 화면 끝에 붙은 탭: 바깥쪽 모서리는 화면 밖으로 넘겨 안쪽만 둥글게 보이게 한다
        private Rect IconRect()
        {
            float y = Mathf.Lerp(16, sh - IconH - 16, Mathf.Clamp01(C.OverlayY));
            return right ? new Rect(sw - IconW, y, IconW + 16, IconH) : new Rect(-16, y, IconW + 16, IconH);
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
            if (!Cursor.visible && !dragging) { UiInputBlock.Set(this, false); return; }
            Rect grab = mode == 3 ? new Rect(widget.x, widget.y, widget.width, 60) : widget;   // 상세는 머리만 잡힌다
            Rect hover = widget;
            if (mode == 1 && open > 0.5f) hover = Union(widget, PanelBesideRect(widget));
            UiInputBlock.Set(this, hover.Contains(m) || dragging);

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

        // ── 그리기: 아이콘 ────────────────────────────────────────────
        private void DrawIcon(Rect r)
        {
            bool hover = r.Contains(Event.current.mousePosition);
            Panel(r, 14);
            if (hover || dragging) Fill(r, new Color(1, 1, 1, 0.05f), 14);
            if (flash > 0) Fill(r, new Color(Warn.r, Warn.g, Warn.b, 0.22f * flash), 14);

            float cx = right ? r.x : r.x + 16;          // 화면 안쪽으로 보이는 부분의 왼쪽 끝
            var inner = new Rect(cx, r.y, IconW, r.height);
            Label(new Rect(inner.x, inner.y + 10, inner.width, 22), sFps, sCenterBig);
            Label(new Rect(inner.x, inner.y + 31, inner.width, 12), "FPS", sCenterSmall);

            // 상태 점: 최근 5초 안에 끊겼으면 주황/빨강으로 깜빡인다
            float since = Time.unscaledTime - lastHitchTime;
            Color dot = since < 5f ? (lastHitchMs >= 50 ? Bad : Warn) : Good;
            float pulse = since < 5f ? 0.6f + 0.4f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 5f)) : 1f;
            Fill(new Rect(inner.xMax - 13, inner.y + 7, 6, 6), new Color(dot.r, dot.g, dot.b, pulse), 3);

            // 작은 그래프 (최근 24프레임)
            var g = new Rect(inner.x + 8, inner.y + 47, inner.width - 16, 11);
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
            Fill(new Rect(lx, r.center.y - 8, 2, 16), new Color(1, 1, 1, hover ? 0.35f : 0.15f), 1);
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
            float since = Time.unscaledTime - lastHitchTime;
            Color dot = since < 5f ? (lastHitchMs >= 50 ? Bad : Warn) : Good;
            Fill(new Rect(r.x + 16, r.center.y - 3, 6, 6), dot, 3);
            Label(new Rect(r.x + 30, r.y + 10, 60, 24), sFps, sMid);
            Label(new Rect(r.x + 74, r.y + 17, 40, 14), "FPS", sSmall);
            Label(new Rect(r.x + 112, r.y + 9, 70, 14), sMs, sLabel);
            Label(new Rect(r.x + 112, r.y + 24, 70, 14), sLow, sSmall);
            Graph(new Rect(r.x + 180, r.y + 10, r.width - 196, r.height - 20), 40, false);
        }

        // ── 그리기: 상세 패널 ──────────────────────────────────────────
        private void DrawPanel(Rect r)
        {
            Panel(r, 14);
            if (flash > 0) Fill(r, new Color(Warn.r, Warn.g, Warn.b, 0.16f * flash), 14);

            float ix = r.x + 16, iw = r.width - 32, cy = r.y + 12;
            Label(new Rect(ix, cy, 120, 34), sFps, sBig);
            Label(new Rect(ix + 2, cy + 35, 60, 14), "FPS", sSmall);
            Label(new Rect(ix + iw - 110, cy + 6, 110, 16), sMs, sValue);
            Label(new Rect(ix + iw - 110, cy + 24, 110, 16), sLow, sSub);
            cy += 64;

            if (C.OvGraph) { Graph(new Rect(ix, cy, iw, 44), GraphN, true); cy += 56; }
            if (C.OvCpu) cy = Row(ix, iw, cy, "CPU", sCpu, sCpuSub, cpuBar, false);
            if (C.OvGpu) cy = Row(ix, iw, cy, "GPU", sGpu, sGpuSub, gpuBar, false);
            if (C.OvVram) cy = Row(ix, iw, cy, "VRAM", sVram, sVramSub, vramBar, vramWarn);
            if (C.OvRam) cy = Row(ix, iw, cy, "RAM", sRam, sRamSub, ramBar, false);
            if (C.OvGc)
            {
                Label(new Rect(ix, cy + 2, 120, 16), T("메모리 정리", "GC"), sLabel);
                Label(new Rect(ix + iw - 130, cy + 1, 130, 16), sGc, sValue);
                Label(new Rect(ix + iw - 130, cy + 18, 130, 14), sGcSub, sSub);
                cy += 38;
            }
            if (C.OvHitchList)
            {
                Fill(new Rect(ix, cy, iw, 1), new Color(1, 1, 1, 0.08f), 0);
                Label(new Rect(ix, cy + 8, iw, 14), T("최근 끊김", "Recent hitches"), sLabel);
                cy += 26;
                if (sHist.Length == 0) { Label(new Rect(ix, cy, iw, 14), T("아직 없음", "None yet"), sSmall); cy += 17; }
                for (int i = 0; i < sHist.Length && i < history.Count; i++)
                {
                    Fill(new Rect(ix, cy + 5, 4, 4), history[i].Tone, 2);
                    Label(new Rect(ix + 10, cy, iw - 10, 14), sHist[i], i == 0 ? sLabel : sSmall);
                    cy += 17;
                }
            }
            Fill(new Rect(ix, r.yMax - 28, iw, 1), new Color(1, 1, 1, 0.08f), 0);
            Label(new Rect(ix, r.yMax - 20, iw, 14), sFooter, sSmall);
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

        private float Row(float x, float w, float y, string label, string value, string sub, float bar, bool warn)
        {
            Label(new Rect(x, y + 2, 60, 16), label, sLabel);
            Label(new Rect(x + w - 150, y + 1, 150, 16), value, sValue);
            if (!string.IsNullOrEmpty(sub))
            {
                var old = sSub.normal.textColor;
                if (warn) sSub.normal.textColor = Warn;
                Label(new Rect(x + 44, y + 3, w - 44 - 90, 14), sub, sSub);
                sSub.normal.textColor = old;
            }
            var track = new Rect(x, y + 22, w, 3);
            Fill(track, Track, 1.5f);
            Color c = warn ? Warn : bar > 0.9f ? Bad : bar > 0.75f ? Warn : Bar;
            Fill(new Rect(track.x, track.y, Mathf.Max(3f, track.width * bar), track.height), c, 1.5f);
            return y + 34;
        }

        // ── 알림: 본체 옆(화면 안쪽)에 쌓인다 ─────────────────────────────
        private void DrawToasts(Rect anchor)
        {
            float now = Time.unscaledTime;
            for (int i = toasts.Count - 1; i >= 0; i--)
                if (now - toasts[i].Time > ToastLife) { toastY.Remove(toasts[i]); toasts.RemoveAt(i); }
            if (toasts.Count == 0) return;

            const float TW = 250, TH = 56;
            float x = right ? anchor.x - TW - 10 : anchor.xMax + 10;
            float ty = Mathf.Clamp(anchor.y, 12, sh - toasts.Count * (TH + 8) - 12);
            for (int i = 0; i < toasts.Count; i++)
            {
                var t = toasts[i];
                float y;
                if (!toastY.TryGetValue(t, out y)) y = ty;
                y = Approach(y, ty, 14f);   // 새 알림이 위에 끼면 아래로 밀려난다
                toastY[t] = y;

                float age = now - t.Time;
                float ein = EaseOut(age / 0.28f), outT = Mathf.Clamp01((ToastLife - age) / 0.45f);
                var r = new Rect(x + (right ? 1 : -1) * (1 - ein) * 24f, y, TW, TH);

                var old = GUI.color;
                GUI.color = new Color(old.r, old.g, old.b, old.a * ein * outT);
                Panel(r, 12);
                Fill(new Rect(r.x + 10, r.y + 12, 3, r.height - 24), t.Tone, 1.5f);
                Label(new Rect(r.x + 22, r.y + 9, r.width - 32, 18), t.Title, sTitle);
                Label(new Rect(r.x + 22, r.y + 29, r.width - 32, 24), t.Detail, sDetail);
                GUI.color = old;
                ty += TH + 8;
            }
        }

        private void OnDestroy() { SystemMonitor.Stop(); UiInputBlock.Set(this, false); }
    }
}
