using System;
using System.Collections.Generic;
using UnityEngine;

namespace StutterFix
{
    // UMM 목록 안이 아니라 게임 화면 위에 따로 뜨는 설정 창 (기본 단축키 Insert).
    //
    // 플레이어가 기술 용어 없이 "무엇이 좋아지는지"만 보고 고를 수 있게 한다. 한국어/English 전환.
    // 모양은 밝고 깔끔하게: 옅은 회색 바탕 위의 흰 카드, 카드 밑의 아주 옅은 그림자, 강조색은 검정 하나.
    // (어두운 그라데이션은 "AI 티"가 난다, 반투명 유리는 뒤 게임 화면이 비쳐 읽기 어렵다는 의견으로 바꿨다.)
    // IMGUI 로 그리되 기본 회색 상자는 쓰지 않는다. 그림자는 카드 텍스처에 구워 넣고 overflow 로 바깥에 그린다.
    // 글꼴은 윈도우의 Segoe UI + 맑은 고딕.
    public class SettingsWindow : MonoBehaviour
    {
        internal static SettingsWindow Instance;
        internal static bool Open;

        internal static void Create()
        {
            if (Instance != null) return;
            var go = new GameObject("StutterFix.SettingsWindow");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<SettingsWindow>();
        }

        internal static void Destroy()
        {
            if (Instance == null) return;
            if (Open) Instance.FinishClose();   // 모드를 끌 때는 애니메이션 없이 바로
            UnityEngine.Object.Destroy(Instance.gameObject);
            Instance = null;
        }

        internal static void Toggle() { if (Instance != null) Instance.SetOpen(!Open || Instance.closing); }

        // ── 언어 ───────────────────────────────────────────────────────
        internal static bool English
        {
            get
            {
                var lang = Main.Config != null ? Main.Config.Language : "";
                if (lang == "en") return true;
                if (lang == "ko") return false;
                return Application.systemLanguage != SystemLanguage.Korean;   // 처음에는 윈도우 언어를 따른다
            }
        }

        internal static string T(string ko, string en) { return English ? en : ko; }

        // ── 색 ─────────────────────────────────────────────────────────
        private static Color Hex(int rgb, float a = 1f) { return new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, a); }
        private static readonly Color Page = Hex(0xF4F4F6), CardC = Hex(0xFFFFFF), Edge = Hex(0xE9E9EE), EdgeHover = Hex(0xD6D6DD),
            Ink = Hex(0x15161A), Text2 = Hex(0x6E6F78), Text3 = Hex(0xA3A4AD), Rule = Hex(0xE6E6EB),
            TrackOff = Hex(0xDCDCE2), Soft = Hex(0xECECF0);

        private static string HexStr(Color c) { return ColorUtility.ToHtmlStringRGB(c); }

        // ── 상태 ───────────────────────────────────────────────────────
        private Rect rect;
        private bool needCenter = true;
        private int page;
        private Vector2 scroll;
        private bool cursorWas;
        private bool built;
        private float scale = 1f, warmedScale = -1f;
        // ── 애니메이션 ─────────────────────────────────────────────────
        // show: 창이 나타난 정도(0~1). 닫을 때도 0까지 내려간 뒤에야 실제로 닫는다.
        // pageT: 페이지를 바꾼 뒤 본문이 들어온 정도. navY/tabX: 메뉴 선택 표시와 언어 밑줄의 현재 위치.
        private float show;
        private float windowAlpha = 1f;
        private bool closing;
        private float pageT = 1f;
        private float navY = -1f, tabX = -1f;
        private readonly Dictionary<string, float> anim = new Dictionary<string, float>();

        private static float EaseOut(float t) { t = 1f - Mathf.Clamp01(t); return 1f - t * t * t; }   // 빠르게 시작해 부드럽게 멈춤
        private static float Approach(float cur, float target, float speed) { return cur + (target - cur) * (1f - Mathf.Exp(-speed * Time.unscaledDeltaTime)); }

        private Font font;
        private GUIStyle sWindow, sShadow, sTitle, sSub, sH1, sLead, sBody, sDim, sSmall, sTag, sCard, sCardDark, sNav, sNavOn, sNavText,
            sPrimary, sClose, sTab, sTabOn, sStat, sStatDark, sStatLabel, sStatLabelDark, sScroll, sThumb,
            sSegKnob, sSegText, sSegOnText, sSliderValue, sChip, sChipOn;
        private Texture2D tWhite, tMark;

        private const float W = 900f, H = 590f, SideW = 196f, HeaderH = 66f;

        // 여는 것은 바로, 닫는 것은 사라지는 애니메이션이 끝난 뒤에 한다.
        private void SetOpen(bool open)
        {
            if (Edition.Dev) Main.Entry.Logger.Log("[설정 창] " + (open ? "열기" : "닫기") + " 요청, 프레임 " + Time.frameCount + ", 지금 열림 " + Open + ", 닫는 중 " + closing + ", 준비됨 " + built + ", 글자 미리 만듦 " + prewarmed);
            SetOpenCore(open);
        }
        private void SetOpenCore(bool open)
        {
            if (open)
            {
                if (Open && !closing) return;
                if (!Open) { cursorWas = Cursor.visible; show = 0f; pageT = 1f; }
                Open = true;
                closing = false;
            }
            else if (Open) closing = true;
        }

        private void FinishClose()
        {
            Open = false;
            panelOpen = false;
            panelT = 0f;
            capturing = -1;
            Hotkey.Capturing = false;
            closing = false;
            show = 0f;
            Cursor.visible = cursorWas;
            SetUiBlocked(false);
        }

        private void Update()
        {
            if (Main.Config == null) return;
            if (Hotkey.Down(Main.Config.WindowKey, Main.Config.WindowMods)) SetOpen(!Open || closing);
            if (!Open) return;
            // Esc: 패널이 펼쳐져 있으면 패널만 접고, 한 번 더 누르면 아이콘 줄까지 닫는다
            if (!closing && !Hotkey.Capturing && Input.GetKeyDown(KeyCode.Escape)) { if (panelOpen) panelOpen = false; else SetOpen(false); }
            Cursor.visible = true;   // 곡 중에는 게임이 커서를 숨긴다

            float dt = Time.unscaledDeltaTime;
            if (closing)
            {
                show = Mathf.MoveTowards(show, 0f, dt / 0.14f);   // 닫기는 빠르게
                if (show <= 0f) FinishClose();
            }
            else show = Mathf.MoveTowards(show, 1f, dt / 0.26f);
            pageT = Mathf.MoveTowards(pageT, 1f, dt / 0.24f);
            panelT = Mathf.MoveTowards(panelT, panelOpen && !closing ? 1f : 0f, dt / (panelOpen ? 0.22f : 0.14f));
        }

        // 메뉴를 바꿀 때: 본문을 처음부터 다시 들여보내고, 스크롤은 맨 위로
        private void GoTo(int p)
        {
            if (p == page) return;
            page = p;
            scroll = Vector2.zero;
            pageT = 0f;
        }

        // 창 위를 누를 때 뒤의 게임 UI(에디터 버튼 등)가 같이 눌리지 않게 막는다 (실시간 모니터와 같이 쓴다).
        private void SetUiBlocked(bool block) { if (!block) UiInputBlock.Clear(this); }

        private void OnDisable() { UiInputBlock.Clear(this); }
        private void OnDestroy() { UiInputBlock.Remove(this); }

        // ── Insert 로 여는 창: 오른쪽 끝의 아이콘 줄 + 아이콘을 누르면 옆에 펼쳐지는 기능 패널 ──
        // 예전에는 화면 가운데에 큰 창(900x590)이 떠서 게임 화면을 가렸다. 이제 처음에는 오른쪽 끝에 반투명(75%) 아이콘만
        // 나오고, 아이콘을 누르면 그 기능 패널이 아이콘 줄 왼쪽에 펼쳐진다. 같은 아이콘을 다시 누르면 접힌다.
        // 패널 안의 내용(스위치, 설명, 버튼)은 예전 페이지 코드를 그대로 쓴다.
        private const float DockW = 60f, IconS = 44f, IconGap = 6f, PanelW = 720f;
        private bool panelOpen;
        private float panelT;       // 패널이 펼쳐진 정도(0~1)
        private Texture2D[] icons;
        private GUIStyle sTip, sTipLeft;
        private float restartArmedUntil;

        private string[] PageNames()
        {
            return new[] { T("홈", "Home"), T("플레이", "Gameplay"), T("맵 불러오기", "Level loading"), T("그래픽", "Graphics"), T("모니터", "Monitor"), T("정보", "About") };
        }

        private Rect DockRect(float sw, float sh, float e)
        {
            float h = 7 * IconS + 6 * IconGap + 20 + 10;   // 기능 6개 + 구분선 + 재시작
            return new Rect(sw - DockW - 12 + (1 - e) * (DockW + 24), (sh - h) / 2f, DockW, h);   // 오른쪽 밖에서 미끄러져 들어온다
        }

        private Rect PanelRect(float sw, float sh, Rect dock, float pe)
        {
            float h = Mathf.Min(H, sh - 24);
            return new Rect(dock.x - 12 - PanelW + (1 - pe) * 24f, (sh - h) / 2f, PanelW, h);
        }

        private static Rect Union(Rect a, Rect b)
        {
            return Rect.MinMaxRect(Mathf.Min(a.xMin, b.xMin), Mathf.Min(a.yMin, b.yMin), Mathf.Max(a.xMax, b.xMax), Mathf.Max(a.yMax, b.yMax));
        }

        private static float ScaleNow() { return Mathf.Clamp(Screen.height / 1080f * 1.1f, 0.8f, 2.2f); }

        // 처음 Insert 를 누를 때 창 그림 만들기와 글자 만들기(개발자용 기록 1,205ms)로 멈추고 창도 늦게 떴다.
        // 그 일을 게임이 켜진 직후(로고·불러오기 화면이라 멈춰도 티가 안 나는 때) 창을 연 적이 없어도 미리 한다.
        // 글자는 투명하게 그려서 화면에는 아무것도 안 나온다. 나중에 화면 크기가 바뀌면 창을 열 때 그 크기로 다시 만든다.
        private bool prewarmed;
        private void PreWarm()
        {
            if (prewarmed || Main.Config == null || Hitch.Playing) return;
            if (!built) { if (Event.current.type == EventType.Layout) Build(); return; }
            float s = ScaleNow();
            if (WarmStyles(this, s)) { scale = s; warmedScale = s; prewarmed = true; }
        }

        private void OnGUI()
        {
            if (!Open) { PreWarm(); return; }
            if (!built) Build();
            CaptureKey();

            scale = ScaleNow();
            if (Mathf.Abs(scale - warmedScale) > 0.001f && WarmStyles(this, scale)) warmedScale = scale;
            float sw = Screen.width / scale, sh = Screen.height / scale;

            var oldMatrix = GUI.matrix;
            var oldColor = GUI.color;
            try
            {
                float e = closing ? show * show : EaseOut(show);
                float pe = EaseOut(panelT);
                if (Event.current.type == EventType.Repaint && pe > 0f)
                {
                    GUI.color = new Color(0, 0, 0, 0.25f * e * pe);   // 패널이 열려 있을 때만 뒤 게임 화면을 살짝 가린다
                    GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), tWhite);
                }
                GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
                GUI.color = new Color(1, 1, 1, e);
                windowAlpha = e;

                var dock = DockRect(sw, sh, e);
                rect = PanelRect(sw, sh, dock, pe);
                // 아이콘 줄(과 펼친 패널) 자리에만 보이지 않는 UI 판을 깔아 뒤의 게임이 그 자리 클릭을 받지 않게 한다.
                // 그 바깥을 누르면 창을 닫고, 클릭은 그대로 게임에 간다(닫으려고 한 번, 게임을 누르려고 또 한 번 누를 필요가 없게).
                if (closing) UiInputBlock.Clear(this);
                else
                {
                    var blk = panelT > 0f ? Union(dock, rect) : dock;
                    UiInputBlock.Place(this, new Rect(blk.x * scale, blk.y * scale, blk.width * scale, blk.height * scale));
                    var ev = Event.current;
                    if (ev.type == EventType.MouseDown && !dock.Contains(ev.mousePosition) && !(panelT > 0f && rect.Contains(ev.mousePosition)))
                        SetOpen(false);
                }

                DrawDock(dock);

                if (panelT > 0f)
                {
                    windowAlpha = e * pe;
                    GUI.color = new Color(1, 1, 1, windowAlpha);
                    if (Event.current.type == EventType.Repaint)
                        sShadow.Draw(new Rect(rect.x - 34, rect.y - 22, rect.width + 68, rect.height + 70), false, false, false, false);
                    GUI.Window(0x5F1A, rect, DrawWindow, GUIContent.none, sWindow);
                }
            }
            finally
            {
                GUI.color = oldColor;
                GUI.matrix = oldMatrix;
            }
        }

        // 오른쪽 아이콘 줄: 75% 불투명한 어두운 판 위에 기능 아이콘 6개. 누르면 그 기능 패널을 펼치거나 접는다.
        private void DrawDock(Rect d)
        {
            Fill(d, new Color(0.06f, 0.065f, 0.08f, 0.75f), 16);
            var names = PageNames();
            var m = Event.current.mousePosition;
            int hover = -1;
            float y = d.y + 10;
            for (int i = 0; i < names.Length; i++)
            {
                var r = new Rect(d.x + (d.width - IconS) / 2f, y, IconS, IconS);
                bool on = panelOpen && page == i;
                bool hov = r.Contains(m);
                if (hov) hover = i;
                if (on) Fill(r, new Color(1, 1, 1, 0.20f), 12);
                else if (hov) Fill(r, new Color(1, 1, 1, 0.09f), 12);
                if (Event.current.type == EventType.Repaint && icons != null)
                {
                    var c = GUI.color;
                    GUI.color = new Color(1, 1, 1, c.a * (on || hov ? 1f : 0.72f));
                    GUI.DrawTexture(new Rect(r.x + 10, r.y + 10, IconS - 20, IconS - 20), icons[i]);
                    GUI.color = c;
                }
                if (GUI.Button(r, GUIContent.none, GUIStyle.none)) TogglePanel(i);
                y += IconS + IconGap;
            }

            // 맨 아래: 게임 재시작 (실수로 눌리지 않게 3초 안에 한 번 더 눌러야 한다). 재시작하면 좋은 때면 주황 점.
            Fill(new Rect(d.x + 14, y + 3, d.width - 28, 1), new Color(1, 1, 1, 0.14f), 0);
            y += 10;
            var rr = new Rect(d.x + (d.width - IconS) / 2f, y, IconS, IconS);
            bool armed = Time.realtimeSinceStartup < restartArmedUntil;
            bool rhov = rr.Contains(m);
            var why = RestartAdvisor.Reasons();
            if (armed) Fill(rr, new Color(0.92f, 0.32f, 0.30f, 0.55f), 12);
            else if (rhov) Fill(rr, new Color(1, 1, 1, 0.09f), 12);
            if (Event.current.type == EventType.Repaint && icons != null && icons.Length > 6)
            {
                var c = GUI.color;
                GUI.color = new Color(1, 1, 1, c.a * (armed || rhov ? 1f : 0.72f));
                GUI.DrawTexture(new Rect(rr.x + 10, rr.y + 10, IconS - 20, IconS - 20), icons[6]);
                GUI.color = c;
                if (why.Count > 0) Fill(new Rect(rr.xMax - 13, rr.y + 5, 8, 8), new Color(1f, 0.62f, 0.2f, c.a), 4);
            }
            if (GUI.Button(rr, GUIContent.none, GUIStyle.none))
            {
                if (armed) { restartArmedUntil = 0; RestartAdvisor.Restart(); }
                else restartArmedUntil = Time.realtimeSinceStartup + 3f;
            }
            if (rhov && panelT <= 0f)
            {
                var lines = new List<string>();
                lines.Add(armed ? T("한 번 더 누르면 게임을 다시 켭니다", "Click again to restart the game") : T("게임 재시작", "Restart game"));
                if (why.Count > 0) { lines.Add(T("지금 재시작하면 좋은 이유:", "Good time to restart:")); foreach (var s in why) lines.Add("· " + s); }
                float w = 0; foreach (var s in lines) w = Mathf.Max(w, sTip.CalcSize(new GUIContent(s)).x);
                w += 22; float h = lines.Count * 22 + 8;
                var tr = new Rect(d.x - 10 - w, rr.center.y - h / 2f, w, h);
                Fill(tr, new Color(0.06f, 0.065f, 0.08f, 0.9f), 9);
                for (int i = 0; i < lines.Count; i++) GUI.Label(new Rect(tr.x + 11, tr.y + 4 + i * 22, w - 22, 22), lines[i], i == 0 ? sTip : sTipLeft);
            }

            // 이름표: 마우스를 올린 아이콘 왼쪽에 (패널이 펼쳐져 있으면 패널 제목이 대신한다)
            if (hover >= 0 && panelT <= 0f)
            {
                float iy = d.y + 10 + hover * (IconS + IconGap);
                var gc = new GUIContent(names[hover]);
                float w = sTip.CalcSize(gc).x + 22;
                var tr = new Rect(d.x - 10 - w, iy + IconS / 2f - 14, w, 28);
                Fill(tr, new Color(0.06f, 0.065f, 0.08f, 0.9f), 9);
                GUI.Label(tr, gc, sTip);
            }
        }

        private void TogglePanel(int i)
        {
            if (panelOpen && page == i) { panelOpen = false; return; }
            if (page != i) GoTo(i);
            panelOpen = true;
        }

        // 창 내용은 OnGUI 가 끝난 뒤 따로 그려지므로, 스킨 바꿔 끼우기를 여기서 한다.
        private void DrawWindow(int id)
        {
            var oldBar = GUI.skin.verticalScrollbar;
            var oldThumb = GUI.skin.verticalScrollbarThumb;
            var oldColor = GUI.color;
            GUI.skin.verticalScrollbar = sScroll;         // 스크롤바 손잡이 모양은 이름으로 찾으므로 잠깐 바꿔 끼운다
            GUI.skin.verticalScrollbarThumb = sThumb;
            GUI.color = new Color(1, 1, 1, windowAlpha);   // 창 안의 글자와 카드도 같이 나타나고 사라진다
            try { DrawContents(); }
            finally
            {
                GUI.skin.verticalScrollbar = oldBar;
                GUI.skin.verticalScrollbarThumb = oldThumb;
                GUI.color = oldColor;
            }
        }

        private void DrawContents()
        {
            float pw = rect.width, ph = rect.height;
            // ── 제목줄: 로고, 이름, 지금 보고 있는 기능
            GUI.DrawTexture(new Rect(24, 22, 24, 24), tMark);
            GUI.Label(new Rect(58, 14, 300, 24), "Stutter Fix", sTitle);
            GUI.Label(new Rect(58, 37, 400, 18), PageNames()[Mathf.Clamp(page, 0, 5)] + "  ·  v" + Main.Entry.Info.Version, sSub);

            // 언어: 글자 탭 + 선택된 쪽 아래 짧은 검정 선
            float tx = pw - 222;
            var ko = new Rect(tx, 20, 70, 28);
            var en = new Rect(tx + 74, 20, 76, 28);
            if (GUI.Button(ko, "한국어", English ? sTab : sTabOn)) SetLanguage("ko");
            if (GUI.Button(en, "English", English ? sTabOn : sTab)) SetLanguage("en");
            float tabTarget = (English ? en : ko).center.x;
            if (tabX < 0) tabX = tabTarget;
            if (Event.current.type == EventType.Repaint) tabX = Approach(tabX, tabTarget, 16f);
            Fill(new Rect(tabX - 9, ko.yMax + 1, 18, 2), Ink, 1);
            if (GUI.Button(new Rect(pw - 56, 18, 34, 32), "×", sClose)) panelOpen = false;   // 패널만 접는다 (아이콘 줄은 남는다)

            // ── 본문 (페이지를 바꾸면 옆에서 살짝 밀려 들어온다)
            const float Gutter = 12f;
            float pe = EaseOut(pageT);
            var body = new Rect(6 + (1 - pe) * 16f, HeaderH + 4, pw - 12, ph - HeaderH - 16);
            var oldC = GUI.color;
            GUI.color = new Color(oldC.r, oldC.g, oldC.b, oldC.a * pe);
            GUILayout.BeginArea(body);
            scroll = GUILayout.BeginScrollView(scroll, false, false, GUIStyle.none, sScroll, GUIStyle.none);
            GUILayout.BeginHorizontal();
            GUILayout.Space(Gutter);
            GUILayout.BeginVertical(GUILayout.Width(body.width - Gutter * 2 - 18));
            GUILayout.Space(Gutter);
            switch (page)
            {
                case 0: PageHome(); break;
                case 1: PagePlay(); break;
                case 2: PageLoad(); break;
                case 3: PageGraphics(); break;
                case 4: PageMonitor(); break;
                default: PageAbout(); break;
            }
            GUILayout.Space(Gutter);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            GUILayout.EndScrollView();
            GUILayout.EndArea();
            GUI.color = oldC;
        }

        // ── 아이콘 그림: 이미지 파일 없이 모양을 계산해 만든다 (가장자리는 4x4 표본으로 부드럽게) ──
        private static Texture2D MakeIcon(Func<float, float, bool> inside)
        {
            const int N = 64, S = 4;
            var tex = new Texture2D(N, N, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
            var px = new Color32[N * N];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    int hit = 0;
                    for (int sy = 0; sy < S; sy++)
                        for (int sx = 0; sx < S; sx++)
                            if (inside((x + (sx + 0.5f) / S) / N, (y + (sy + 0.5f) / S) / N)) hit++;
                    // 텍스처는 아래가 0 이라 세로를 뒤집어 넣는다(모양은 위가 0 으로 정의)
                    px[(N - 1 - y) * N + x] = new Color32(255, 255, 255, (byte)(255 * hit / (S * S)));
                }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        private static bool InBox(float x, float y, float x0, float y0, float x1, float y1) { return x >= x0 && x <= x1 && y >= y0 && y <= y1; }
        private static bool InCircle(float x, float y, float cx, float cy, float r) { return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r; }
        private static bool InTri(float x, float y, float ax, float ay, float bx, float by, float cx, float cy)
        {
            float d1 = (x - bx) * (ay - by) - (ax - bx) * (y - by);
            float d2 = (x - cx) * (by - cy) - (bx - cx) * (y - cy);
            float d3 = (x - ax) * (cy - ay) - (cx - ax) * (y - ay);
            bool neg = d1 < 0 || d2 < 0 || d3 < 0, pos = d1 > 0 || d2 > 0 || d3 > 0;
            return !(neg && pos);
        }

        private static Texture2D[] MakeIcons()
        {
            return new[]
            {
                // 홈: 지붕 + 몸통 (문 자리는 비움)
                MakeIcon((x, y) => InTri(x, y, 0.5f, 0.10f, 0.06f, 0.50f, 0.94f, 0.50f)
                                || (InBox(x, y, 0.20f, 0.46f, 0.80f, 0.90f) && !InBox(x, y, 0.42f, 0.63f, 0.58f, 0.90f))),
                // 플레이: 재생 삼각형
                MakeIcon((x, y) => InTri(x, y, 0.26f, 0.12f, 0.26f, 0.88f, 0.88f, 0.50f)),
                // 맵 불러오기: 아래 화살표 + 받침
                MakeIcon((x, y) => InBox(x, y, 0.43f, 0.08f, 0.57f, 0.50f) || InTri(x, y, 0.22f, 0.44f, 0.78f, 0.44f, 0.50f, 0.72f)
                                || InBox(x, y, 0.10f, 0.80f, 0.90f, 0.92f) || InBox(x, y, 0.10f, 0.60f, 0.22f, 0.92f) || InBox(x, y, 0.78f, 0.60f, 0.90f, 0.92f)),
                // 그래픽: 그림 틀 + 산 + 해
                MakeIcon((x, y) => (InBox(x, y, 0.06f, 0.16f, 0.94f, 0.84f) && !InBox(x, y, 0.16f, 0.26f, 0.84f, 0.74f))
                                || InTri(x, y, 0.16f, 0.74f, 0.44f, 0.40f, 0.72f, 0.74f) || InCircle(x, y, 0.68f, 0.40f, 0.08f)),
                // 모니터: 막대그래프
                MakeIcon((x, y) => InBox(x, y, 0.12f, 0.56f, 0.30f, 0.90f) || InBox(x, y, 0.41f, 0.34f, 0.59f, 0.90f) || InBox(x, y, 0.70f, 0.12f, 0.88f, 0.90f)),
                // 정보: 동그라미 안에 i
                MakeIcon((x, y) =>
                {
                    float d = (x - 0.5f) * (x - 0.5f) + (y - 0.5f) * (y - 0.5f);
                    return (d <= 0.46f * 0.46f && d >= 0.36f * 0.36f) || InCircle(x, y, 0.5f, 0.31f, 0.065f) || InBox(x, y, 0.445f, 0.43f, 0.555f, 0.73f);
                }),
                // 재시작: 위쪽이 끊긴 동그라미 + 끊긴 자리의 화살촉 (시계 방향)
                MakeIcon((x, y) =>
                {
                    float dx = x - 0.5f, dy = y - 0.54f; float dd = Mathf.Sqrt(dx * dx + dy * dy);
                    float ang = Mathf.Atan2(-dy, dx) * Mathf.Rad2Deg;
                    bool ring = dd >= 0.25f && dd <= 0.37f && !(ang > 15f && ang < 82f);
                    return ring || InTri(x, y, 0.50f, 0.07f, 0.50f, 0.36f, 0.74f, 0.215f);
                }),
            };
        }

        // ── 페이지 ─────────────────────────────────────────────────────
        private void PageHome()
        {
            Heading(T("홈", "Home"), T("고사양 커스텀 맵에서 플레이 중 순간적으로 멈추는 현상과 맵 로딩 시간을 줄입니다. 연출과 판정은 바꾸지 않습니다.",
                "Reduces hitches during play and loading times on heavy custom levels. Visuals and judgement are unchanged."));

            var c = Main.Config;
            int on = (c.GcPause ? 1 : 0) + (c.EffectSplit ? 1 : 0) + (c.RecolorSplit ? 1 : 0) + (c.TweenGuard ? 1 : 0) + (c.SkipSameText ? 1 : 0)
                   + (c.ShaderWarm ? 1 : 0) + (c.FastBlend ? 1 : 0) + (c.SkipInvisible ? 1 : 0) + (c.LazyHidden ? 1 : 0) + (c.ZeroTween ? 1 : 0) + (c.InstantDirect ? 1 : 0) + (c.SkipSame ? 1 : 0) + (c.FastLoop ? 1 : 0) + (c.Precheck ? 1 : 0) + (c.DecoAnim ? 1 : 0) + (c.MoveFinish ? 1 : 0) + (c.DormantSkip ? 1 : 0) + (c.ImagePrefetch ? 1 : 0) + (c.SkipAssetUnload ? 1 : 0) + (c.LegacyGfxJobs ? 1 : 0);
            string d = BootConfig.Describe();
            bool jobs = d.Contains("Jobified") || d.Contains("Split");

            GUILayout.BeginHorizontal();
            Stat(on + " / 20", T("켜진 기능", "Features on"), true);
            GUILayout.Space(14);
            Stat(GcControl.Paused ? T("미루는 중", "Deferred") : T("대기", "Idle"), T("메모리 정리", "Memory cleanup"), false);
            GUILayout.Space(14);
            Stat(jobs ? T("켜짐", "On") : T("꺼짐", "Off"), T("멀티스레드 그리기", "Multithreaded rendering"), false);
            GUILayout.EndHorizontal();
            GUILayout.Space(14);

            // 지금 재시작하면 좋은 때 (메모리가 쌓임, 멀티스레드 그리기 변경, 모드 업데이트 등)
            var why = RestartAdvisor.Reasons();
            if (why.Count > 0)
            {
                InfoCard(new[] { T("재시작 권장", "Restart suggested"), string.Join("\n", why.ToArray()) });
                GUILayout.BeginHorizontal();
                bool armed = Time.realtimeSinceStartup < restartArmedUntil;
                if (GUILayout.Button(armed ? T("한 번 더 누르면 재시작", "Click again to restart") : T("지금 재시작", "Restart now"), sPrimary, GUILayout.Width(190), GUILayout.Height(38)))
                {
                    if (armed) { restartArmedUntil = 0; RestartAdvisor.Restart(); }
                    else restartArmedUntil = Time.realtimeSinceStartup + 3f;
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(14);
            }

            InfoCard(new[]
            {
                T("마지막 맵 불러오기", "Last level load"), LoadSummary(),
                T("그래픽", "Graphics"), d.Replace("지금 ", ""),
            });

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(T("모두 권장값으로", "Reset to recommended"), sPrimary, GUILayout.Width(170), GUILayout.Height(38))) ResetDefaults();
            GUILayout.EndHorizontal();
            GUILayout.Space(14);
            KeysCard();
        }

        // ── 단축키 바꾸기 ───────────────────────────────────────────────
        // 버튼을 누르면 다음에 누르는 키(+ Ctrl/Shift/Alt)를 그 단축키로 쓴다. Esc 는 취소.
        private int capturing = -1;          // 0 설정 창, 1 모니터, -1 아님
        private string keyNote = "";

        private void KeysCard()
        {
            var c = Main.Config;
            GUILayout.BeginVertical(sCard);
            GUILayout.Label(T("단축키", "Shortcuts"), sBody);
            GUILayout.Space(8);
            KeyRow(0, T("설정 창 열기/닫기", "Open / close settings"), c.WindowKey, c.WindowMods);
            GUILayout.Space(8);
            KeyRow(1, T("모니터 표시 방식 바꾸기", "Cycle monitor style"), c.OverlayKey, c.OverlayMods);
            if (keyNote.Length > 0) { GUILayout.Space(8); GUILayout.Label(keyNote, sSub); }
            GUILayout.EndVertical();
        }

        private void KeyRow(int id, string label, KeyCode key, int mods)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, sDim, GUILayout.Height(34));
            GUILayout.FlexibleSpace();
            bool on = capturing == id;
            string text = on ? T("키를 누르세요… (Esc 취소)", "Press a key… (Esc to cancel)") : Hotkey.Name(key, mods);
            if (GUILayout.Button(text, on ? sChipOn : sChip, GUILayout.MinWidth(150), GUILayout.Height(34)))
            {
                capturing = on ? -1 : id;
                Hotkey.Capturing = capturing >= 0;
                keyNote = "";
            }
            GUILayout.EndHorizontal();
        }

        private void CaptureKey()
        {
            if (capturing < 0) return;
            var e = Event.current;
            if (e.type != EventType.KeyDown || e.keyCode == KeyCode.None || Hotkey.IsModifier(e.keyCode)) return;
            e.Use();
            int id = capturing;
            capturing = -1;
            Hotkey.Capturing = false;
            if (e.keyCode == KeyCode.Escape) { keyNote = ""; return; }

            int mods = (e.shift ? Hotkey.Shift : 0) | (e.control ? Hotkey.Ctrl : 0) | (e.alt ? Hotkey.Alt : 0);
            var c = Main.Config;
            KeyCode otherKey = id == 0 ? c.OverlayKey : c.WindowKey;
            int otherMods = id == 0 ? c.OverlayMods : c.WindowMods;
            if (e.keyCode == otherKey && mods == otherMods)
            {
                keyNote = T("다른 단축키와 같은 키입니다. 다른 키를 골라 주세요.", "That is already used by the other shortcut.");
                return;
            }
            if (id == 0) { c.WindowKey = e.keyCode; c.WindowMods = mods; }
            else { c.OverlayKey = e.keyCode; c.OverlayMods = mods; }
            keyNote = Hotkey.MayClashWithGame(e.keyCode, mods)
                ? T("이 키는 플레이 중 박자 입력과 겹칠 수 있습니다. F1~F12, Insert, Home 같은 키나 Ctrl/Shift 조합을 권합니다.",
                    "This key may also count as a tap while playing. F-keys, Insert, Home or a Ctrl/Shift combo are safer.")
                : "";
            Save();
        }

        private void PagePlay()
        {
            var c = Main.Config;
            Heading(T("플레이", "Gameplay"), T("곡을 플레이하는 동안의 끊김을 줄입니다. 모두 켜 두는 것을 권장합니다. 들여 쓴 기능은 위 기능이 켜져 있을 때만 동작합니다.",
                "Reduces hitches while a level is playing. Keeping everything on is recommended. Indented features only work while the feature above them is on."));
            bool ch = false;

            Section(T("기본", "General"));
            ch |= Option("gc", ref c.GcPause, T("메모리 정리 미루기", "Defer memory cleanup"),
                T("플레이 중 게임이 메모리를 정리하느라 잠깐 멈추는 것을 막습니다. 곡이 끝나고 몇 초 뒤 한 번에 정리합니다.",
                  "Stops the game from pausing to clean up memory mid-song. Cleanup runs once, a few seconds after the level ends."),
                T("효과 가장 큼", "Biggest impact"));
            ch |= Option("fx", ref c.EffectSplit, T("효과 몰림 나누기", "Spread effect bursts"),
                T("한 순간에 효과 수십 개가 동시에 시작될 때, 몇 프레임에 나눠 시작해 화면이 멈추지 않게 합니다.",
                  "When dozens of effects start on the same beat, starts them over a few frames instead of freezing one frame."), null);
            ch |= Option("recolor", ref c.RecolorSplit, T("타일 색 바꾸기 나누기", "Spread tile recolors"),
                T("타일 수천 개의 색을 한 번에 바꾸는 이벤트를 조금씩 나눠 칠합니다. 먼 타일이 아주 잠깐 늦게 바뀔 뿐 결과는 같습니다.",
                  "Recolors thousands of tiles in small batches. Far-away tiles update a few frames later; the result is identical."), null);
            ch |= Option("tween", ref c.TweenGuard, T("애니메이션 처리 최적화", "Animation list guard"),
                T("효과가 많을 때 게임이 애니메이션 목록을 반복해서 다시 정리하느라 느려지는 문제를 막습니다.",
                  "Prevents the game from repeatedly re-sorting its animation list when many effects are running."), null);
            ch |= Option("text", ref c.SkipSameText, T("글자 장식 최적화", "Text decoration skip"),
                T("같은 글자를 매 프레임 다시 쓰는 글자 장식은 건너뜁니다. PACL2 같은 모드를 함께 쓸 때 효과가 큽니다.",
                  "Skips text decorations that are re-set to the same text every frame. Helps a lot with mods like PACL2."), null);
            ch |= Option("shader", ref c.ShaderWarm, T("그래픽 미리 준비", "Shader warm-up"),
                T("곡이 시작될 때 그래픽 준비를 미리 해 두어, 효과가 처음 나올 때의 끊김을 줄입니다.",
                  "Prepares shaders when a level starts, reducing the hitch the first time an effect appears."), null);
            ch |= Option("blend", ref c.FastBlend, T("블렌드 장식 빠르게 그리기", "Faster blend decorations"),
                T("더하기(Linear Dodge) 블렌드 장식을 화면 복사 없이 그립니다. 모양은 같고, 블렌드 장식이 많은 맵에서 프레임이 크게 오릅니다.",
                  "Draws additive (Linear Dodge) blend decorations without copying the screen. Looks identical; big FPS gain on maps with many blend decorations."),
                T("무거운 맵", "Heavy maps"));

            Section(T("투명한 장식", "Hidden decorations"));
            ch |= Option("invis", ref c.SkipInvisible, T("투명한 장식 그리지 않기", "Skip invisible decorations"),
                T("투명도가 0 이라 보이지 않는 이미지 장식을 그리기에서 뺍니다. 다시 보이게 되면 바로 그립니다. 화면은 같고, 나중에 나타날 이미지를 깔아 둔 맵에서 프레임이 오릅니다.",
                  "Leaves fully transparent image decorations out of rendering and draws them again as soon as they become visible. Looks identical; raises FPS on maps that pre-place hidden images."), null);
            ch |= Option("lazy", ref c.LazyHidden, T("투명한 장식 위치 미루기", "Defer hidden decoration moves"),
                T("투명해서 안 보이는 장식은 옮겨도 값만 저장했다가, 보이게 되는 순간 한 번 반영합니다. 히트박스·마스크 장식은 제외합니다.",
                  "Hidden decorations only store their new position until they become visible, then apply it once. Hitbox and mask decorations are excluded."),
                null, 1, Need(c.SkipInvisible, T("투명한 장식 그리지 않기", "Skip invisible decorations")));

            Section(T("장식 이동", "Decoration moves"));
            ch |= Option("zerotween", ref c.ZeroTween, T("즉시 이동 최적화", "Instant decoration moves"),
                T("장식을 즉시(길이 0) 옮기는 이벤트를 애니메이션 없이 바로 처리하고, 곧바로 덮어써질 중간 호출은 건너뜁니다. 결과는 게임과 똑같습니다(26만 개를 비트 단위로 비교해 확인).",
                  "Applies instant (zero-length) decoration moves without creating animations and skips intermediate calls that are overwritten right away. Identical results (verified bit-for-bit over 260,000 cases)."),
                T("장식 많은 맵", "Decoration-heavy maps"));
            string zt = Need(c.ZeroTween, T("즉시 이동 최적화", "Instant decoration moves"));
            ch |= Option("instant", ref c.InstantDirect, T("즉시 이동 직접 처리", "Direct instant moves"),
                T("즉시 이동이 한꺼번에 몰리는 순간(효과 몰림) 게임 코드가 속성마다 애니메이션 객체를 만드는 과정 자체를 건너뛰고 최종 값만 넣습니다. Arche 효과 몰림 68 → 36ms.",
                  "When many instant moves land at once, skips the game's per-property animation setup entirely and applies only the final values. Arche effect burst 68 → 36 ms."),
                T("효과 몰림", "Effect bursts"), 1, zt);
            string id = zt ?? Need(c.InstantDirect, T("즉시 이동 직접 처리", "Direct instant moves"));
            ch |= Option("fastloop", ref c.FastLoop, T("장식 이동 루프", "Decoration move loop"),
                T("장식 이동 효과를 게임 코드 대신 모드의 루프로 돕니다. 게임 코드는 장식마다 객체를 여러 개 만들고 대상 목록을 여러 겹으로 훑는데, 같은 순서로 같은 일만 합니다. 이미지·마스크를 바꾸는 효과는 원래대로 둡니다.",
                  "Runs decoration move effects in the mod's own loop instead of the game code, which allocates several objects per decoration and walks the target list through layered queries. Same work in the same order. Effects that change images or masks are left alone."),
                T("효과 몰림", "Effect bursts"), 2, id);
            string fl = id ?? Need(c.FastLoop, T("장식 이동 루프", "Decoration move loop"));
            ch |= Option("decoanim", ref c.DecoAnim, T("장식 애니메이션 직접 처리", "Decoration animations"),
                T("길이가 있는 장식 이동(위치·회전·크기·색·불투명도)의 애니메이션을 DOTween 대신 모드가 돌립니다. 시간 누적, 이징, 콜백 순서, 끊기까지 DOTween 과 똑같이 하고(33만 개를 DOTween 과 나란히 돌려 비트 단위로 확인), 애니메이션 관리 비용만 줄입니다. 피벗·시차가 섞인 효과는 원래대로 둡니다.",
                  "Runs decoration move animations (position, rotation, scale, color, opacity) in the mod instead of DOTween, with the same timing, easing, callback order and kill behavior (verified bit-for-bit against DOTween over 330,000 animations), cutting only the tween bookkeeping. Effects that also animate pivot or parallax stay on DOTween."),
                T("무거운 구간", "Heavy sections"), 3, fl);
            string ss = id ?? Need(c.SkipInvisible, T("투명한 장식 그리지 않기", "Skip invisible decorations"));
            ch |= Option("samevalue", ref c.SkipSame, T("투명 장식 빠른 처리", "Fast path for hidden decorations"),
                T("즉시 이동이 투명한 장식을 옮기면 게임 함수를 거치지 않고 위치를 바로 \"보일 때 반영\" 목록에 넣고, 이미 가진 것과 같은 색은 다시 넣지 않으며, 바뀌어도 투명한 채라면 값만 저장합니다. 게임 상태는 원래와 똑같습니다.",
                  "When an instant move touches a transparent decoration, its position goes straight into the apply-when-visible list, re-writing an unchanged color is skipped, and color changes that stay transparent only store values. Game state stays identical."),
                T("효과 몰림", "Effect bursts"), 2, ss);
            string pc = fl ?? ss ?? Need(c.SkipSame, T("투명 장식 빠른 처리", "Fast path for hidden decorations")) ?? Need(c.LazyHidden, T("투명한 장식 위치 미루기", "Defer hidden decoration moves"));
            ch |= Option("precheck", ref c.Precheck, T("미리 확인", "Look-ahead check"),
                T("곧 발동할 무거운 장식 이동 효과(대상 200개 이상)가 이미 투명하고 값도 그대로인 장식에만 닿는지 몇 초 앞서 여유 있는 프레임에 나눠 확인해 두고, 발동할 때까지 대상이 하나도 안 바뀌었으면 효과를 통째로 건너뜁니다. 대상이 바뀌는 모든 길을 지켜보다가 바뀐 장식만 원래대로 처리합니다.",
                  "Checks upcoming heavy decoration moves (200+ targets) a few seconds ahead, spread over idle frames, and skips the whole effect when every target is already hidden with the same values. Every write path to a watched decoration is tracked; decorations touched in between are processed normally."),
                T("효과 몰림", "Effect bursts"), 3, pc);
            ch |= Option("movefinish", ref c.MoveFinish, T("장식 위치 계산 줄이기", "Fewer position updates"),
                T("장식을 옮길 때 위치 마무리 계산을 한 번으로 묶고, 값이 그대로인 쓰기와 플레이 중 필요 없는 편집기 작업을 건너뜁니다. 보이는 장식의 위치 재계산은 어차피 같은 프레임에 게임이 다시 하므로 그때 한 번만 합니다.",
                  "Batches position finishing per decoration, skips unchanged writes and editor-only work while playing, and leaves visible decorations' position recompute to the game's own once-per-frame pass."), null);
            ch |= Option("dormant", ref c.DormantSkip, T("장식 순회 줄이기", "Skip idle decorations"),
                T("게임은 매 프레임 장식 전부를 훑습니다. 안 보이고 바뀔 일이 없는 장식과, 히트박스가 없는 장식은 그 순회에서 빼 둡니다. 장식이 수만 개인 맵에서 평소 프레임이 크게 오릅니다(Arche 107 → 170 fps).",
                  "The game walks every decoration every frame. Idle invisible decorations and decorations without hitboxes are left out of those walks. Big everyday FPS gain on maps with tens of thousands of decorations (Arche 107 → 170 fps)."),
                T("장식 많은 맵", "Decoration-heavy maps"));
            if (ch) Save();
        }

        // 묶음 제목
        private void Section(string title)
        {
            GUILayout.Space(6);
            GUILayout.Label(title, sTag);
            GUILayout.Space(6);
        }

        // 상위 기능이 꺼져 있으면 그 이름("… 꺼짐"), 켜져 있으면 null
        private static string Need(bool on, string name) { return on ? null : name; }

        // 하위 기능: 들여 쓰고 왼쪽에 이어지는 선. 상위 기능이 꺼져 있으면(off != null) 흐리게, 누를 수 없게, 무엇이 꺼져 있는지 적는다.
        private bool Option(string key, ref bool value, string title, string desc, string tag, int depth, string off)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Space(depth * 26);
            GUILayout.BeginVertical();
            var oldC = GUI.color;
            if (off != null) GUI.color = new Color(oldC.r, oldC.g, oldC.b, oldC.a * 0.45f);
            string t2 = off != null ? T("쉬는 중 · ", "Paused · ") + off + T(" 꺼짐", " is off") : tag;
            bool changed = Option(key, ref value, title, desc, t2, off == null);
            GUI.color = oldC;
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            if (depth > 0 && Event.current.type == EventType.Repaint)
            {
                var r = GUILayoutUtility.GetLastRect();
                float lx = r.x + depth * 26 - 14;
                Fill(new Rect(lx, r.y - 12, 2, 35), Rule, 1);   // 위 기능에서 내려오는 선
                Fill(new Rect(lx, r.y + 21, 11, 2), Rule, 1);
            }
            return changed;
        }

        private void PageLoad()
        {
            var c = Main.Config;
            Heading(T("맵 불러오기", "Level loading"), T("맵을 열거나 편집 화면으로 돌아올 때 기다리는 시간을 줄입니다.",
                "Shortens waits when opening a level or returning to the editor."));
            bool ch = false;
            ch |= Option("img", ref c.ImagePrefetch, T("이미지 빠르게 불러오기", "Parallel image loading"),
                T("장식 이미지가 많은 맵을 열 때 CPU 여러 코어로 이미지를 동시에 불러옵니다.",
                  "Decodes decoration images on several CPU cores at once when a level opens."),
                T("예: 67초 → 38초", "e.g. 67s → 38s"));
            ch |= Option("unload", ref c.SkipAssetUnload, T("불필요한 정리 건너뛰기", "Skip asset unload"),
                T("맵을 열거나 편집으로 돌아올 때 게임이 하는 짧은 정리 작업을 건너뛰어 멈춤을 줄입니다.",
                  "Skips a short cleanup the game runs when opening a level or returning to the editor."), null);

            // 큰 이미지 줄이기 (화질을 조금 내주고 VRAM 을 아낀다)
            GUILayout.BeginVertical(sCard);
            GUILayout.BeginHorizontal();
            GUILayout.Label(T("큰 이미지 줄이기", "Downscale large images"), sBody, GUILayout.ExpandWidth(false));
            GUILayout.Space(8);
            GUILayout.Label("·  " + T("자동 권장", "Auto recommended"), sTag, GUILayout.ExpandWidth(false));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
            GUILayout.Label(T("장식 이미지가 수천 장인 맵은 그래픽 메모리(VRAM)가 넘쳐 GPU 가 크게 느려집니다. 긴 변이 기준보다 큰 이미지를 줄여 불러옵니다. 장식의 화면 크기는 그대로이고 선명도만 낮아집니다. <b>자동</b>은 처음에는 원본 그대로 불러오고, 플레이 중 그래픽 메모리가 가득 차서 끊긴 맵만 기억해 두었다가 다음에 불러올 때 큰 이미지부터 한 단계씩(3072 → 2048 → 1536 → 1024) 줄입니다. 끊기지 않는 맵은 화질을 건드리지 않습니다. 다음에 여는 맵부터 적용됩니다.",
                "Levels with thousands of decoration images can overflow video memory (VRAM) and slow the GPU badly. Images larger than the limit are loaded smaller. Decorations keep their on-screen size; only sharpness drops. <b>Auto</b> loads images at full size first. If a level stutters because VRAM is full, it remembers that level and caps large images one step lower (3072 → 2048 → 1536 → 1024) the next time it loads. Levels that run fine keep full quality. Applies to the next level you open."), sDim);
            GUILayout.Space(10);
            int cap = c.ImageMaxSide == ImagePrefetch.Auto ? 1 : c.ImageMaxSide >= 4096 ? 2 : c.ImageMaxSide > 0 ? 3 : 0;
            if (Segment("imgcap", ref cap, new[] { T("끔", "Off"), T("자동", "Auto"), T("긴 변 4096", "4096 px"), T("긴 변 2048", "2048 px") }))
            {
                c.ImageMaxSide = cap == 1 ? ImagePrefetch.Auto : cap == 2 ? 4096 : cap == 3 ? 2048 : 0;
                ch = true;
            }
            int remembered = VramGuard.Remembered;
            if (c.ImageMaxSide == ImagePrefetch.Auto && remembered > 0)
            {
                GUILayout.Space(10);
                GUILayout.BeginHorizontal();
                GUILayout.Label(T("자동이 줄이기로 기억한 맵 ", "Levels remembered by Auto: ") + remembered + T("개", ""), sDim, GUILayout.Height(34));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(T("기억 지우기", "Forget"), sChip, GUILayout.Height(34), GUILayout.ExpandWidth(false))) VramGuard.Forget();
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
            GUILayout.Space(12);

            if (ch) Save();
            InfoCard(new[] { T("마지막 맵 불러오기", "Last level load"), LoadSummary() });
        }

        private void PageGraphics()
        {
            var c = Main.Config;
            Heading(T("그래픽", "Graphics"), T("바꾸면 게임을 다시 켜야 적용됩니다.", "Changes apply after restarting the game."));
            bool v = c.LegacyGfxJobs;
            if (Option("jobs", ref v, T("멀티스레드 그리기", "Multithreaded rendering"),
                T("화면을 그리는 준비 작업을 여러 CPU 코어에 나눠 프레임을 높이고 끊김을 줄입니다. 게임 폴더의 boot.config 에 한 줄을 넣고, 모드를 끄면 원래대로 돌려놓습니다.",
                  "Splits render preparation across CPU cores for higher, steadier FPS. Adds one line to the game's boot.config and restores it when the mod is turned off."),
                T("권장", "Recommended")))
            {
                c.LegacyGfxJobs = v;
                BootConfig.Apply(v);
                Save();
            }
            var rows = new List<string> { T("지금 상태", "Current"), BootConfig.Describe().Replace("지금 ", "") };
            if (BootConfig.Status.Contains("다음 실행")) { rows.Add(T("적용", "Pending")); rows.Add(T("게임을 다시 켜면 적용됩니다", "Applies after restart")); }
            if (Main.LaunchWarning.Length > 0) { rows.Add(T("주의", "Warning")); rows.Add(Main.LaunchWarning.Trim()); }
            InfoCard(rows.ToArray());
        }

        private void PageMonitor()
        {
            var c = Main.Config;
            Heading(T("모니터", "Monitor"), T("게임 화면 끝에 프레임, CPU, GPU, VRAM, RAM 사용량을 띄웁니다. 끊기면 왜 끊겼는지 알려 줍니다. 모니터를 잡고 끌면 위치를 옮길 수 있습니다.",
                "Shows frame time, CPU, GPU, VRAM and RAM at the screen edge and tells you why a hitch happened. Drag it to move it."));
            bool ch = false;

            // 표시 방식
            GUILayout.BeginVertical(sCard);
            GUILayout.Label(T("표시 방식", "Style"), sBody);
            GUILayout.Space(3);
            GUILayout.Label(T("게임 중 " + Hotkey.Name(c.OverlayKey, c.OverlayMods) + " 로 차례로 바꿀 수 있습니다 (홈에서 키 변경). 아이콘은 누르면 상세 정보가 펼쳐집니다.",
                "Cycle with " + Hotkey.Name(c.OverlayKey, c.OverlayMods) + " in game (change it on Home). Click the icon to expand it."), sDim);
            GUILayout.Space(10);
            ch |= Segment("ovmode", ref c.OverlayMode, new[] { T("끔", "Off"), T("아이콘", "Icon"), T("미니", "Mini"), T("상세", "Detail") });
            GUILayout.Space(12);
            int side = c.OverlayRight ? 1 : 0;
            if (Segment("ovside", ref side, new[] { T("왼쪽 끝", "Left edge"), T("오른쪽 끝", "Right edge") })) { c.OverlayRight = side == 1; ch = true; }
            GUILayout.EndVertical();
            GUILayout.Space(12);

            // 모양
            GUILayout.BeginVertical(sCard);
            GUILayout.Label(T("모양", "Look"), sBody);
            GUILayout.Space(8);
            ch |= Slider("ovop", ref c.OverlayOpacity, 0.3f, 1f, T("불투명도", "Opacity"), (c.OverlayOpacity * 100).ToString("F0") + "%");
            ch |= Slider("ovscale", ref c.OverlayScale, 0.7f, 1.6f, T("크기", "Size"), (c.OverlayScale * 100).ToString("F0") + "%");
            ch |= Slider("ovy", ref c.OverlayY, 0f, 1f, T("세로 위치", "Vertical position"), c.OverlayY < 0.34f ? T("위", "Top") : c.OverlayY > 0.66f ? T("아래", "Bottom") : T("가운데", "Middle"));
            GUILayout.EndVertical();
            GUILayout.Space(12);

            // 아이콘/미니에 보여 줄 항목
            GUILayout.BeginVertical(sCard);
            GUILayout.Label(T("아이콘·미니에 보여 줄 항목", "Items in icon and mini"), sBody);
            GUILayout.Space(3);
            GUILayout.Label(T("FPS 는 항상 보입니다. 사용률이 75%를 넘으면 주황, 90%를 넘으면 빨강으로 바뀝니다.",
                "FPS is always shown. Usage turns orange above 75% and red above 90%."), sDim);
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            ch |= Chip(ref c.CmMs, T("프레임 시간", "Frame time"));
            ch |= Chip(ref c.CmLow, "1% low");
            ch |= Chip(ref c.CmCpu, "CPU");
            ch |= Chip(ref c.CmGpu, "GPU");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            ch |= Chip(ref c.CmVram, "VRAM");
            ch |= Chip(ref c.CmRam, "RAM");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.Space(12);

            // 상세 정보에 보여 줄 항목
            GUILayout.BeginVertical(sCard);
            GUILayout.Label(T("상세 정보에 보여 줄 항목", "Items in the detail view"), sBody);
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            ch |= Chip(ref c.OvGraph, T("그래프", "Graph"));
            ch |= Chip(ref c.OvCpu, "CPU");
            ch |= Chip(ref c.OvGpu, "GPU");
            ch |= Chip(ref c.OvVram, "VRAM");
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            ch |= Chip(ref c.OvRam, "RAM");
            ch |= Chip(ref c.OvGc, T("메모리 정리", "GC"));
            ch |= Chip(ref c.OvHitchList, T("최근 끊김", "Recent hitches"));
            ch |= Chip(ref c.OvSession, T("이번 곡", "This level"));
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
            GUILayout.Space(12);

            // 끊김 알림: 모양, 위치, 기준
            GUILayout.BeginVertical(sCard);
            GUILayout.Label(T("끊김 알림", "Hitch alerts"), sBody);
            GUILayout.Space(3);
            GUILayout.Label(T("프레임이 튀면 원인을 띄웁니다: 모드 작업(보라), 메모리 정리, 효과 몰림, GPU 과부하, 게임 처리, 게임 바깥(윈도우나 다른 프로그램). 같은 원인이 연달아 나면 ×2, ×3 으로 묶습니다.",
                "Shows the likely cause when a frame spikes: mod work (purple), memory cleanup, effect burst, GPU overload, game logic, or something outside the game. Repeats are grouped (×2, ×3)."), sDim);
            GUILayout.Space(10);
            int style = !c.HitchAlerts ? 0 : c.AlertDetailed ? 2 : 1;
            if (Segment("alertstyle", ref style, new[] { T("끔", "Off"), T("간단", "Simple"), T("자세히", "Detailed") }))
            {
                c.HitchAlerts = style > 0; c.AlertDetailed = style == 2; ch = true;
            }
            GUILayout.Space(10);
            ch |= Segment("alertpos", ref c.AlertPos, new[] { T("모니터 옆", "Beside monitor"), T("화면 위", "Top center"), T("화면 아래", "Bottom center") });
            GUILayout.Space(12);
            ch |= Slider("alertms", ref c.AlertMs, 20f, 100f, T("알림 기준", "Alert above"), c.AlertMs.ToString("F0") + "ms");
            GUILayout.Label(T("이보다 긴 프레임만 알립니다. 33ms 는 60fps 기준 두 프레임이 밀린 것입니다.",
                "Only frames longer than this are reported. 33ms is two frames at 60 fps."), sDim);
            GUILayout.EndVertical();
            GUILayout.Space(12);
            if (ch) Save();
            InfoCard(new[]
            {
                T("VRAM 넘침", "VRAM spill"), T("VRAM 줄에 주황색 '넘침'이 뜨면 그래픽 메모리가 모자라 시스템 램으로 밀려난 것입니다. 이때 곡 중에 멈출 수 있습니다.",
                    "An orange 'spill' on the VRAM row means video memory ran out and data moved to system RAM, which can cause mid-song freezes."),
            });
        }

        // 문제 보고: 바탕화면에 로그 묶음(zip)을 만들어 제작자에게 보낼 수 있게 한다
        private void ReportCard()
        {
            GUILayout.BeginVertical(sCard);
            GUILayout.Label(T("문제 보고용 로그 만들기", "Create a log for bug reports"), sBody);
            GUILayout.Space(4);
            GUILayout.Label(T("게임이 끊기거나 오류가 났다면, 그 판을 끝낸 뒤(게임이 튕겼다면 다시 켠 뒤) 눌러 주세요. 바탕화면에 zip 파일이 생기고, 그 파일을 디스코드 <b>narooh</b> 에게 DM 으로 보내 주세요. 사양, 설정, 모드 목록, 게임 로그, 끊김 기록이 들어가며 윈도우 사용자 이름은 가려집니다.",
                "If you hit a stutter or an error, press this after that run (or after restarting if the game crashed). A zip file appears on your desktop; send it to <b>narooh</b> on Discord (DM). It contains specs, settings, the mod list, game logs and the hitch record, with your Windows user name hidden."), sLead);
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(T("로그 파일 만들기", "Create log file"), sPrimary, GUILayout.Width(170), GUILayout.Height(38)))
            {
                if (LogExport.Export() != null) LogExport.Reveal();
            }
            GUILayout.Space(10);
            if (LogExport.LastPath.Length > 0 && GUILayout.Button(T("폴더 열기", "Show file"), sChip, GUILayout.Height(38), GUILayout.ExpandWidth(false))) LogExport.Reveal();
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            if (LogExport.LastError.Length > 0) { GUILayout.Space(6); GUILayout.Label(T("만들지 못했습니다: ", "Failed: ") + LogExport.LastError, sSub); }
            else if (LogExport.LastPath.Length > 0) { GUILayout.Space(6); GUILayout.Label(T("만든 파일: ", "Created: ") + System.IO.Path.GetFileName(LogExport.LastPath) + T("  (바탕화면)", "  (desktop)"), sSub); }
            GUILayout.EndVertical();
        }

        private void PageAbout()
        {
            Heading("Stutter Fix", "v" + Main.Entry.Info.Version + "  ·  " + (English ? (Edition.Dev ? "developer build" : "player build") : Edition.Name));
            GUILayout.BeginVertical(sCardDark);
            GUILayout.Label("naro & Claude", sStatDark);
            GUILayout.Space(6);
            GUILayout.Label(T("실제 맵에서 끊긴 순간을 하나씩 측정해 원인을 찾고, 원인마다 고친 모드입니다.",
                "Built by measuring each real hitch in real levels, finding its cause, and fixing it."), sStatLabelDark);
            GUILayout.EndVertical();
            GUILayout.Space(14);
            InfoCard(new[]
            {
                T("모드를 끄면", "Turning it off"), T("UMM 에서 끄면 모든 변경을 즉시 되돌립니다. 멀티스레드 그리기는 다음 실행부터 원래대로 돌아갑니다.",
                    "Turning the mod off in UMM reverts everything immediately. Multithreaded rendering reverts on the next launch."),
                T("그래도 끊긴다면", "Still stuttering?"), T("필터가 아주 많이 겹치는 구간은 그래픽카드 한계이고, 백그라운드 프로그램이 순간 끊김을 만들 수도 있습니다.",
                    "Scenes stacking many full-screen filters are limited by the GPU, and background apps can cause occasional hitches."),
                T("소스", "Source"), "github.com/pding4569/StutterFix",
            });
            GUILayout.Space(14);
            ReportCard();
        }

        private static string LoadSummary()
        {
            if (ImagePrefetch.Last == "아직 안 함") return T("아직 맵을 불러오지 않았습니다", "No level loaded yet");
            return ImagePrefetch.Last;
        }

        // ── 부품 ───────────────────────────────────────────────────────
        private void Heading(string title, string lead)
        {
            GUILayout.Label(title, sH1);
            GUILayout.Space(4);
            GUILayout.Label(lead, sLead);
            GUILayout.Space(18);
        }

        private bool Option(string key, ref bool value, string title, string desc, string tag, bool clickable = true)
        {
            GUILayout.BeginHorizontal(sCard);
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUILayout.Label(title, sBody, GUILayout.ExpandWidth(false));
            if (tag != null) { GUILayout.Space(8); GUILayout.Label("·  " + tag, sTag, GUILayout.ExpandWidth(false)); }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
            GUILayout.Label(desc, sDim);
            GUILayout.EndVertical();
            GUILayout.Space(24);
            GUILayout.BeginVertical(GUILayout.Width(44));
            GUILayout.FlexibleSpace();
            Rect r = GUILayoutUtility.GetRect(44, 24, GUILayout.Width(44), GUILayout.Height(24));
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            Rect card = GUILayoutUtility.GetLastRect();
            GUILayout.Space(12);

            DrawSwitch(key, r, value);

            // 카드 아무 데나 눌러도 바뀐다
            var e = Event.current;
            if (clickable && e.type == EventType.MouseDown && e.button == 0 && card.Contains(e.mousePosition))
            {
                e.Use();
                value = !value;
                return true;
            }
            return false;
        }

        // 여러 개 중 하나 고르기: 옅은 바탕 위에서 흰 선택 칸이 미끄러진다
        private bool Segment(string key, ref int value, string[] labels)
        {
            Rect r = GUILayoutUtility.GetRect(10, 36, GUILayout.ExpandWidth(true), GUILayout.Height(36));
            float w = r.width / labels.Length;
            var e = Event.current;
            bool changed = false;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition))
            {
                int v = Mathf.Clamp((int)((e.mousePosition.x - r.x) / w), 0, labels.Length - 1);
                if (v != value) { value = v; changed = true; }
                e.Use();
            }
            if (e.type == EventType.Repaint)
            {
                float x;
                if (!anim.TryGetValue(key, out x)) x = value;
                x = Mathf.Lerp(x, value, 1f - Mathf.Exp(-18f * Time.unscaledDeltaTime));
                anim[key] = x;
                Fill(r, Soft, 10);
                var sel = new Rect(r.x + 3 + x * w, r.y + 3, w - 6, r.height - 6);
                sSegKnob.Draw(sel, false, false, false, false);
                for (int i = 0; i < labels.Length; i++)
                    (i == value ? sSegOnText : sSegText).Draw(new Rect(r.x + i * w, r.y, w, r.height), labels[i], false, false, false, false);
            }
            return changed;
        }

        // 가로 막대를 끌어서 값 고르기
        private bool Slider(string key, ref float value, float min, float max, string label, string shown)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, sDim, GUILayout.Width(110), GUILayout.Height(28));
            Rect r = GUILayoutUtility.GetRect(10, 28, GUILayout.ExpandWidth(true), GUILayout.Height(28));
            GUILayout.Label(shown, sSliderValue, GUILayout.Width(64), GUILayout.Height(28));
            GUILayout.EndHorizontal();
            GUILayout.Space(4);

            int id = GUIUtility.GetControlID(key.GetHashCode(), FocusType.Passive, r);
            var e = Event.current;
            float old = value;
            var track = new Rect(r.x + 9, r.center.y - 2, r.width - 18, 4);
            switch (e.type)
            {
                case EventType.MouseDown:
                    if (e.button == 0 && r.Contains(e.mousePosition)) { GUIUtility.hotControl = id; value = Pick(track, e.mousePosition.x, min, max); e.Use(); }
                    break;
                case EventType.MouseDrag:
                    if (GUIUtility.hotControl == id) { value = Pick(track, e.mousePosition.x, min, max); e.Use(); }
                    break;
                case EventType.MouseUp:
                    if (GUIUtility.hotControl == id) { GUIUtility.hotControl = 0; e.Use(); }
                    break;
                case EventType.Repaint:
                    float k = Mathf.InverseLerp(min, max, value);
                    Fill(track, TrackOff, 2);
                    Fill(new Rect(track.x, track.y, track.width * k, track.height), Ink, 2);
                    float kx = track.x + track.width * k;
                    bool active = GUIUtility.hotControl == id;
                    float kr = active ? 9f : 8f;
                    Fill(new Rect(kx - kr, track.center.y - kr, kr * 2, kr * 2), Ink, kr);
                    Fill(new Rect(kx - kr + 3, track.center.y - kr + 3, kr * 2 - 6, kr * 2 - 6), Color.white, kr - 3);
                    break;
            }
            return !Mathf.Approximately(old, value);
        }

        private static float Pick(Rect track, float x, float min, float max)
        {
            return Mathf.Lerp(min, max, Mathf.Clamp01((x - track.x) / track.width));
        }

        // 켜고 끄는 작은 알약 버튼
        private bool Chip(ref bool on, string label)
        {
            var content = new GUIContent((on ? "✓  " : "") + label);
            var st = on ? sChipOn : sChip;
            Rect r = GUILayoutUtility.GetRect(content, st, GUILayout.Height(32));
            GUILayout.Space(8);
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && r.Contains(e.mousePosition)) { on = !on; e.Use(); return true; }
            if (e.type == EventType.Repaint) st.Draw(r, content, r.Contains(e.mousePosition), false, false, false);
            return false;
        }

        private void DrawSwitch(string key, Rect r, bool on)
        {
            if (Event.current.type != EventType.Repaint) return;
            float target = on ? 1f : 0f, t;
            if (!anim.TryGetValue(key, out t)) t = target;
            t = Mathf.MoveTowards(t, target, Time.unscaledDeltaTime * 7f);
            anim[key] = t;
            float k = Mathf.SmoothStep(0, 1, t);

            var track = new Rect(r.x, r.y + 1, 44, 24);
            GUI.DrawTexture(track, tWhite, ScaleMode.StretchToFill, true, 0, Faded(Color.Lerp(TrackOff, Ink, k)), 0, 12);
            float kx = Mathf.Lerp(track.x + 3, track.xMax - 21, k);
            GUI.DrawTexture(new Rect(kx, track.y + 3, 18, 18), tWhite, ScaleMode.StretchToFill, true, 0, Faded(Color.white), 0, 9);
        }

        private void Stat(string value, string label, bool dark)
        {
            GUILayout.BeginVertical(dark ? sCardDark : sCard, GUILayout.ExpandWidth(true), GUILayout.Height(88));
            GUILayout.Label(value, dark ? sStatDark : sStat);
            GUILayout.FlexibleSpace();
            GUILayout.Label(label, dark ? sStatLabelDark : sStatLabel);
            GUILayout.EndVertical();
        }

        // 라벨/값 짝을 줄마다 나눠 한 카드에 담는다
        private void InfoCard(string[] kv)
        {
            GUILayout.BeginVertical(sCard);
            for (int i = 0; i + 1 < kv.Length; i += 2)
            {
                if (i > 0)
                {
                    GUILayout.Space(11);
                    Rect line = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true), GUILayout.Height(1));
                    Fill(line, Rule, 1);
                    GUILayout.Space(11);
                }
                GUILayout.Label(kv[i], sSmall);
                GUILayout.Space(3);
                GUILayout.Label(kv[i + 1], sBody);
            }
            GUILayout.EndVertical();
            GUILayout.Space(14);
        }

        private void Fill(Rect r, Color c, float radius)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(r, tWhite, ScaleMode.StretchToFill, true, 0, Faded(c), 0, radius);
        }

        // 색 인자를 받는 DrawTexture 는 GUI.color 의 투명도를 따르지 않으므로 직접 곱한다
        private static Color Faded(Color c) { return new Color(c.r, c.g, c.b, c.a * GUI.color.a); }

        private void SetLanguage(string lang)
        {
            Main.Config.Language = lang;
            try { Main.Config.Save(Main.Entry); } catch { }
        }

        private void ResetDefaults()
        {
            var c = Main.Config;
            c.GcPause = c.EffectSplit = c.RecolorSplit = c.TweenGuard = c.SkipSameText = c.ShaderWarm = c.FastBlend = c.SkipInvisible = c.LazyHidden = c.ZeroTween = c.InstantDirect = c.SkipSame = c.FastLoop = c.Precheck = c.DecoAnim = c.MoveFinish = c.DormantSkip = c.ImagePrefetch = c.SkipAssetUnload = true;
            if (!c.LegacyGfxJobs) { c.LegacyGfxJobs = true; BootConfig.Apply(true); }
            Save();
        }

        private void Save()
        {
            Main.ApplyConfig();
            try { Main.Config.Save(Main.Entry); } catch { }
        }

        // ── 모양 만들기 ─────────────────────────────────────────────────
        // 설정 창과 실시간 모니터가 같이 쓰는 글꼴 (윈도우의 Segoe UI + 맑은 고딕)
        private static Font uiFont;
        internal static Font UiFont()
        {
            if (uiFont != null) return uiFont;
            try { uiFont = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Malgun Gothic", "Arial" }, 16); } catch { uiFont = null; }
            if (uiFont == null) uiFont = GUI.skin.font;
            return uiFont;
        }

        // 글자를 처음 그릴 때(글꼴/굵기/크기마다) 글자 이미지를 새로 만들고, 그때 30~90ms 멈춘다.
        // 모니터를 처음 띄우거나 새 알림 문구가 나올 때 "모드 작업 (모니터 그리기)" 끊김으로 잡히던 게 이것이었다.
        // 유니티 6 의 IMGUI 는 TextCore 로 글자를 그린다(IMGUITextHandle). 예전에는 옛 방식
        // (Font.RequestCharactersInTexture)으로 미리 만들었는데 TextCore 와는 상관이 없어 효과가 없었고 1.1초만 먹었다.
        // 지금은 창이 실제로 쓰는 스타일마다 쓰는 글자 전부의 크기를 한 번 잰다. 크기를 재려면 TextCore 가 글자를
        // 만들어야 하므로 그때 한꺼번에 만들어진다. 스타일을 만든 직후(OnGUI 안, 불러오기로 표시) 한 번만 한다.
        internal const string WarmAscii = WarmText;
        private const string WarmText = " !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~·×→←↑↓…°%";
        // scale: 그 창이 GUI.matrix 로 키우는 배율. 글자는 화면에 실제로 그려지는 크기마다 따로 만들어지므로
        // 같은 배율을 걸고 재고, 한 번은 실제로(투명하게) 그려 둔다. 그리기 이벤트(Repaint)에서만 한다. 했으면 true.
        internal static bool WarmStyles(object owner, float scale)
        {
            if (Event.current == null || Event.current.type != EventType.Repaint) return false;
            var oldM = GUI.matrix; var oldC = GUI.color;
            try
            {
                GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
                GUI.color = new Color(1, 1, 1, 0f);
                PerfOverlay.MarkLoading(T("글꼴 준비", "Preparing font"));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var gc = new GUIContent(WarmChars.Text + WarmText);
                int n = 0;
                foreach (var f in owner.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
                {
                    if (f.FieldType != typeof(GUIStyle)) continue;
                    var s = f.GetValue(owner) as GUIStyle;
                    if (s == null) continue;
                    var ws = new GUIStyle(s) { wordWrap = false, richText = false };
                    var size = ws.CalcSize(gc);
                    GUI.Label(new Rect(0, 0, size.x, size.y), gc, ws);   // 투명하게 한 번 그린다
                    n++;
                }
                PerfOverlay.MarkLoading(T("글꼴 준비", "Preparing font"));
                if (Edition.Dev) Main.Entry.Logger.Log("[모니터] 글자 미리 만들기 (" + owner.GetType().Name + "): 배율 " + scale.ToString("F2") + ", 스타일 " + n + "개, " + sw.ElapsedMilliseconds + "ms");
            }
            catch (Exception ex) { if (Edition.Dev) Main.Entry.Logger.Log("[모니터] 글자 미리 만들기 실패: " + ex.Message); }
            finally { GUI.matrix = oldM; GUI.color = oldC; }
            return true;
        }

        private void Build()
        {
            built = true;
            PerfOverlay.MarkLoading(T("모드 창 준비", "Preparing mod window"));   // 둥근 카드 그림을 처음 만드는 프레임
            font = UiFont();
            if (font == null) font = GUI.skin.font;

            tWhite = Texture2D.whiteTexture;
            tMark = Mark(48);
            icons = MakeIcons();

            sWindow = Styled(Card(Page, Hex(0xFFFFFF, 0.6f), 20, 1, 0, 0f), 22);
            sWindow.padding = new RectOffset(0, 0, 0, 0);
            sShadow = Styled(Shadow(48, 34), 48);

            // 흰 카드 + 옅은 테두리 + 아래로 살짝 떨어지는 그림자 (그림자는 overflow 로 바깥에)
            const int pad = 8;
            sCard = Styled(Card(CardC, Edge, 14, 1, pad, 0.06f), 14 + pad);
            sCard.overflow = new RectOffset(pad, pad, pad, pad);
            sCard.padding = new RectOffset(20, 20, 16, 17);
            sCard.hover.background = Card(CardC, EdgeHover, 14, 1, pad, 0.09f);
            sCardDark = Styled(Card(Ink, Ink, 14, 0, pad, 0.18f), 14 + pad);
            sCardDark.overflow = new RectOffset(pad, pad, pad, pad);
            sCardDark.padding = new RectOffset(20, 20, 16, 17);

            sTitle = Label(17, Ink, FontStyle.Bold);
            sTip = Label(12, Color.white, FontStyle.Bold); sTip.alignment = TextAnchor.MiddleCenter;   // 아이콘 이름표
            sTipLeft = Label(12, Hex(0xFFFFFF, 0.85f), FontStyle.Normal); sTipLeft.alignment = TextAnchor.MiddleLeft;
            sSub = Label(12, Text3, FontStyle.Normal);
            sH1 = Label(25, Ink, FontStyle.Bold);
            sLead = Label(13, Text2, FontStyle.Normal); sLead.wordWrap = true;
            sBody = Label(15, Ink, FontStyle.Bold); sBody.wordWrap = true;
            sDim = Label(13, Text2, FontStyle.Normal); sDim.wordWrap = true;
            sSmall = Label(11, Text3, FontStyle.Normal);
            sTag = Label(12, Text3, FontStyle.Normal); sTag.padding = new RectOffset(0, 0, 3, 0);
            sStat = Label(24, Ink, FontStyle.Bold);
            sStatLabel = Label(12, Text2, FontStyle.Normal);
            sStatDark = Label(24, Color.white, FontStyle.Bold); sStatDark.wordWrap = true;
            sStatLabelDark = Label(12, Hex(0xFFFFFF, 0.62f), FontStyle.Normal); sStatLabelDark.wordWrap = true;

            // 메뉴: 선택된 것만 흰 카드로 떠 있다
            sNav = Styled(null, 10);
            sNav.normal.textColor = Text2; sNav.fontSize = 14; sNav.alignment = TextAnchor.MiddleLeft; sNav.padding = new RectOffset(16, 8, 0, 0);
            sNav.hover.textColor = Ink;
            sNavOn = Styled(Card(CardC, Edge, 10, 1, 6, 0.06f), 16);
            sNavOn.overflow = new RectOffset(6, 6, 6, 6);
            sNavOn.normal.textColor = Ink; sNavOn.hover.textColor = Ink; sNavOn.fontSize = 14; sNavOn.fontStyle = FontStyle.Bold;
            sNavOn.alignment = TextAnchor.MiddleLeft; sNavOn.padding = new RectOffset(16, 8, 0, 0);
            sNavOn.hover.background = sNavOn.normal.background;
            sNavText = new GUIStyle(sNav) { fontStyle = FontStyle.Bold };   // 선택 카드 위의 글자 (배경은 따로 미끄러지며 그린다)
            sNavText.normal.textColor = Ink; sNavText.hover.textColor = Ink;

            sPrimary = Styled(Card(Ink, Ink, 10, 0, 0, 0f), 12);
            sPrimary.normal.textColor = Color.white; sPrimary.alignment = TextAnchor.MiddleCenter; sPrimary.fontSize = 14; sPrimary.fontStyle = FontStyle.Bold;
            sPrimary.hover.background = Card(Hex(0x2C2D33), Hex(0x2C2D33), 10, 0, 0, 0f); sPrimary.hover.textColor = Color.white;

            sTab = Styled(null, 4);
            sTab.normal.textColor = Text3; sTab.hover.textColor = Ink; sTab.alignment = TextAnchor.MiddleCenter; sTab.fontSize = 13;
            sTabOn = new GUIStyle(sTab) { fontStyle = FontStyle.Bold };
            sTabOn.normal.textColor = Ink;

            sClose = Styled(null, 10);
            sClose.normal.textColor = Text3; sClose.alignment = TextAnchor.MiddleCenter; sClose.fontSize = 22; sClose.padding = new RectOffset(0, 0, 0, 4);
            sClose.hover.background = Card(Soft, Soft, 9, 0, 0, 0f); sClose.hover.textColor = Ink;


            // 모니터 페이지의 조절 도구
            sSegKnob = Styled(Card(CardC, Edge, 8, 1, 4, 0.08f), 12);
            sSegKnob.overflow = new RectOffset(4, 4, 4, 4);
            sSegText = Label(13, Text2, FontStyle.Normal); sSegText.alignment = TextAnchor.MiddleCenter;
            sSegOnText = Label(13, Ink, FontStyle.Bold); sSegOnText.alignment = TextAnchor.MiddleCenter;
            sSliderValue = Label(13, Ink, FontStyle.Bold); sSliderValue.alignment = TextAnchor.MiddleRight;
            sChip = Styled(Card(CardC, Edge, 15, 1, 0, 0f), 16);
            sChip.normal.textColor = Text2; sChip.fontSize = 13; sChip.alignment = TextAnchor.MiddleCenter;
            sChip.padding = new RectOffset(14, 14, 0, 0);
            sChip.hover.background = Card(Soft, EdgeHover, 15, 1, 0, 0f); sChip.hover.textColor = Ink;
            sChipOn = Styled(Card(Ink, Ink, 15, 0, 0, 0f), 16);
            sChipOn.normal.textColor = Color.white; sChipOn.fontSize = 13; sChipOn.fontStyle = FontStyle.Bold; sChipOn.alignment = TextAnchor.MiddleCenter;
            sChipOn.padding = new RectOffset(14, 14, 0, 0);
            sChipOn.hover.background = Card(Hex(0x2C2D33), Hex(0x2C2D33), 15, 0, 0, 0f); sChipOn.hover.textColor = Color.white;

            sScroll = new GUIStyle { fixedWidth = 4, margin = new RectOffset(14, 0, 0, 0), border = new RectOffset(2, 2, 2, 2) };
            sThumb = new GUIStyle { fixedWidth = 4, border = new RectOffset(2, 2, 2, 2) };
            sThumb.normal.background = Card(Hex(0xC9C9D0), Hex(0xC9C9D0), 2, 0, 0, 0f);
        }

        private GUIStyle Label(int size, Color color, FontStyle style)
        {
            var s = new GUIStyle(GUI.skin.label) { font = font, fontSize = size, fontStyle = style, richText = true, wordWrap = false };
            s.normal.textColor = color;
            s.hover.textColor = color;
            s.padding = new RectOffset(0, 0, 1, 1);
            s.margin = new RectOffset(0, 0, 0, 0);
            return s;
        }

        private GUIStyle Styled(Texture2D bg, int border)
        {
            var s = new GUIStyle { font = font, richText = true };
            s.normal.background = bg;
            s.border = new RectOffset(border, border, border, border);
            s.margin = new RectOffset(0, 0, 0, 0);
            return s;
        }

        // ── 텍스처 ─────────────────────────────────────────────────────
        internal static Texture2D NewTex(int w, int h)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        }

        // 한 변이 size 인 정사각형 안의 반지름 r 둥근 사각형까지의 거리 (안쪽이 음수)
        internal static float RoundDist(float x, float y, float size, float r)
        {
            float h = size / 2f;
            float qx = Mathf.Abs(x - h) - (h - r), qy = Mathf.Abs(y - h) - (h - r);
            float ox = Mathf.Max(qx, 0), oy = Mathf.Max(qy, 0);
            return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0) - r;
        }

        // 둥근 카드: 채움 + 테두리(두께 bw) + 바깥 pad 만큼 아래로 살짝 떨어진 옅은 그림자(진하기 shadowA).
        // 9칸 나누기로 늘여 쓰고, 그림자 부분은 GUIStyle.overflow 로 카드 바깥에 그린다.
        internal static Texture2D Card(Color fill, Color border, int r, int bw, int pad, float shadowA)
        {
            int inner = r * 2 + 4, size = inner + pad * 2;
            var t = NewTex(size, size);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float fx = x + 0.5f - pad, fy = y + 0.5f - pad;
                    float d = RoundDist(fx, fy, inner, r);

                    // 그림자: 아래로 2px 내려 부드럽게 퍼진다 (텍스처 y 는 아래가 0)
                    Color bg = Color.clear;
                    if (pad > 0 && shadowA > 0)
                    {
                        float ds = RoundDist(fx, fy + 2f, inner, r);
                        float s = Mathf.Clamp01(1f - Mathf.Max(0, ds) / pad);
                        bg = new Color(0.1f, 0.1f, 0.15f, s * s * shadowA);
                    }

                    float cover = Mathf.Clamp01(0.5f - d);
                    Color c = fill;
                    if (bw > 0) c = Color.Lerp(border, fill, Mathf.Clamp01(-d - bw + 0.5f));
                    float a = c.a * cover;
                    // 카드를 그림자 위에 겹친다
                    float outA = a + bg.a * (1 - a);
                    Color rgb = outA > 0 ? (c * a + bg * bg.a * (1 - a)) / outA : Color.clear;
                    px[y * size + x] = new Color(rgb.r, rgb.g, rgb.b, outA);
                }
            t.SetPixels(px);
            t.Apply(false, false);
            return t;
        }

        // 제목 옆 표시: 검정 둥근 사각형 안의 흰 막대 세 개 (프레임 그래프)
        private static Texture2D Mark(int s)
        {
            var t = NewTex(s, s);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float d = RoundDist(x + 0.5f, y + 0.5f, s, s * 0.26f);
                    float cover = Mathf.Clamp01(0.5f - d);
                    float u = (x + 0.5f) / s, v = (y + 0.5f) / s;
                    float bar = Mathf.Max(Bar(u, v, 0.27f, 0.37f, 0.25f, 0.52f, s),
                                Mathf.Max(Bar(u, v, 0.45f, 0.55f, 0.25f, 0.75f, s), Bar(u, v, 0.63f, 0.73f, 0.25f, 0.63f, s)));
                    Color c = Color.Lerp(Ink, Color.white, bar);
                    c.a = cover;
                    t.SetPixel(x, y, c);
                }
            t.Apply(false, false);
            return t;
        }

        private static float Bar(float u, float v, float x0, float x1, float bottom, float top, int s)
        {
            float r = (x1 - x0) / 2f, cx = (x0 + x1) / 2f;
            float cy = Mathf.Clamp(v, bottom + r, top - r);
            float d = Mathf.Sqrt((u - cx) * (u - cx) + (v - cy) * (v - cy)) - r;
            return Mathf.Clamp01(0.5f - d * s);
        }

        // 창 뒤의 넓고 옅은 그림자
        internal static Texture2D Shadow(int half, int blur)
        {
            int n = half * 2;
            var t = NewTex(n, n);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = RoundDist(x + 0.5f, y + 0.5f, n, blur + 8) + blur;
                    float a = Mathf.Clamp01(1f - d / blur);
                    t.SetPixel(x, y, new Color(0, 0, 0, a * a * 0.38f));
                }
            t.Apply(false, false);
            return t;
        }
    }
}
