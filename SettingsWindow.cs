using System;
using UnityEngine;

namespace StutterFix
{
    // UMM 목록 안이 아니라 게임 화면 위에 따로 뜨는 설정 창 (기본 단축키 Insert).
    //
    // 플레이어가 기술 용어 없이 "무엇이 좋아지는지"만 보고 고를 수 있게 한다.
    // IMGUI 로 그리되, 둥근 모서리/스위치/색은 직접 만든 텍스처로 입힌다(기본 회색 상자를 쓰지 않는다).
    // 화면 높이에 맞춰 크기를 키우고, 제목줄을 잡고 끌 수 있다. Esc 또는 X 로 닫는다.
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
            Instance.SetOpen(false);
            UnityEngine.Object.Destroy(Instance.gameObject);
            Instance = null;
        }

        internal static void Toggle() { if (Instance != null) Instance.SetOpen(!Open); }

        // ── 색 ─────────────────────────────────────────────────────────
        private static readonly Color Bg = Hex(0x17181D), Side = Hex(0x1F2027), Card = Hex(0x262833), CardHover = Hex(0x2D3040),
            Line = Hex(0x33364A), Text = Hex(0xECEDF3), Dim = Hex(0x9A9DB0), Accent = Hex(0x7AA2FF), AccentDim = Hex(0x3A4A7A),
            Off = Hex(0x4A4D5E), Good = Hex(0x6BD49A), Warn = Hex(0xF2C26B);

        private static Color Hex(int rgb) { return new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f); }

        // ── 상태 ───────────────────────────────────────────────────────
        private Rect rect;
        private int page;
        private Vector2 scroll;
        private bool cursorWas;
        private bool built;
        private float scale = 1f;

        private GUIStyle sWindow, sTitle, sH1, sBody, sDim, sSmall, sCard, sNav, sNavOn, sButton, sAccentBtn, sClose, sStat, sStatLabel;
        private Texture2D tRound, tRoundSmall, tPill, tCircle, tWhite;

        private static readonly string[] Pages = { "홈", "플레이", "맵 불러오기", "그래픽", "정보" };

        private const float W = 860f, H = 560f, SideW = 190f, HeaderH = 52f;

        private void SetOpen(bool open)
        {
            if (open == Open) return;
            Open = open;
            if (open)
            {
                cursorWas = Cursor.visible;
                if (rect.width == 0) rect = new Rect((Screen.width / scale - W) / 2f, (Screen.height / scale - H) / 2f, W, H);
            }
            else
            {
                Cursor.visible = cursorWas;
                SetUiBlocked(false);
            }
        }

        private void Update()
        {
            if (Main.Config == null) return;
            if (Input.GetKeyDown(Main.Config.WindowKey)) SetOpen(!Open);
            if (!Open) return;
            if (Input.GetKeyDown(KeyCode.Escape)) { SetOpen(false); return; }
            Cursor.visible = true;   // 곡 중에는 게임이 커서를 숨긴다
        }

        // 창 위를 누를 때 뒤의 게임 UI(에디터 버튼 등)가 같이 눌리지 않게 막는다.
        private bool uiBlocked;
        private void SetUiBlocked(bool block)
        {
            if (block == uiBlocked) return;
            uiBlocked = block;
            try
            {
                var es = UnityEngine.EventSystems.EventSystem.current;
                if (es != null) es.enabled = !block;
            }
            catch { }
        }

        private void OnGUI()
        {
            if (!Open) return;
            if (!built) Build();

            scale = Mathf.Clamp(Screen.height / 1080f * 1.15f, 0.8f, 2.2f);
            var old = GUI.matrix;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(scale, scale, 1f));

            // 화면 밖으로 끌려 나가지 않게
            rect.x = Mathf.Clamp(rect.x, 0, Mathf.Max(0, Screen.width / scale - rect.width));
            rect.y = Mathf.Clamp(rect.y, 0, Mathf.Max(0, Screen.height / scale - rect.height));

            Vector2 mouse = Event.current.mousePosition;
            SetUiBlocked(rect.Contains(mouse));

            rect = GUI.Window(0x5F1A, rect, DrawWindow, GUIContent.none, sWindow);
            GUI.matrix = old;
        }

        private void DrawWindow(int id)
        {
            // 제목줄 (위쪽 두 모서리만 둥글게: 둥근 상자 위에 아래 절반을 네모로 덮는다)
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(0, 0, W, HeaderH), tWhite, ScaleMode.StretchToFill, true, 0, Side, 0, 14);
            GUI.color = Side;
            GUI.DrawTexture(new Rect(0, HeaderH / 2, W, HeaderH / 2), tWhite);
            GUI.color = Accent;
            GUI.DrawTexture(new Rect(0, HeaderH - 2, W, 2), tWhite);
            GUI.color = Color.white;
            GUI.Label(new Rect(22, 0, 400, HeaderH), "<b>STUTTER FIX</b>  <color=#9A9DB0><size=13>끊김 줄이기</size></color>", sTitle);
            if (GUI.Button(new Rect(W - 48, 10, 32, 32), "×", sClose)) SetOpen(false);

            // 왼쪽 메뉴
            // 왼쪽 아래 모서리만 둥글게
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(0, HeaderH, SideW, H - HeaderH), tWhite, ScaleMode.StretchToFill, true, 0, Side, 0, 14);
            GUI.color = Side;
            GUI.DrawTexture(new Rect(0, HeaderH, SideW, H - HeaderH - 20), tWhite);
            GUI.DrawTexture(new Rect(20, HeaderH, SideW - 20, H - HeaderH), tWhite);
            GUI.color = Color.white;
            for (int i = 0; i < Pages.Length; i++)
                if (GUI.Button(new Rect(12, HeaderH + 14 + i * 44, SideW - 24, 38), Pages[i], i == page ? sNavOn : sNav)) { page = i; scroll = Vector2.zero; }

            GUI.Label(new Rect(16, H - 58, SideW - 20, 20), "made by <b>naro</b> & <b>Claude</b>", sSmall);
            GUI.Label(new Rect(16, H - 36, SideW - 20, 20), "v" + Main.Entry.Info.Version + " · " + Edition.Name, sSmall);

            // 본문
            var body = new Rect(SideW + 24, HeaderH + 16, W - SideW - 48, H - HeaderH - 32);
            GUILayout.BeginArea(body);
            scroll = GUILayout.BeginScrollView(scroll, false, false, GUIStyle.none, GUI.skin.verticalScrollbar);
            switch (page)
            {
                case 0: PageHome(); break;
                case 1: PagePlay(); break;
                case 2: PageLoad(); break;
                case 3: PageGraphics(); break;
                default: PageAbout(); break;
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            GUI.DragWindow(new Rect(0, 0, W - 60, HeaderH));
        }

        // ── 페이지 ─────────────────────────────────────────────────────
        private void PageHome()
        {
            GUILayout.Label("홈", sH1);
            GUILayout.Label("고사양 커스텀 맵에서 플레이 중 순간적으로 멈추는 현상을 줄입니다.\n화면에 보이는 연출은 바꾸지 않고, 게임이 일을 처리하는 순서와 방법만 바꿉니다.", sBody);
            GUILayout.Space(12);

            var c = Main.Config;
            int on = (c.GcPause ? 1 : 0) + (c.EffectSplit ? 1 : 0) + (c.RecolorSplit ? 1 : 0) + (c.TweenGuard ? 1 : 0) + (c.SkipSameText ? 1 : 0)
                   + (c.ShaderWarm ? 1 : 0) + (c.ImagePrefetch ? 1 : 0) + (c.SkipAssetUnload ? 1 : 0) + (c.LegacyGfxJobs ? 1 : 0);

            GUILayout.BeginHorizontal();
            Stat(on + " / 9", "켜진 기능");
            GUILayout.Space(10);
            Stat(GcControl.Paused ? "멈춤" : "정상", "메모리 정리 (GC)");
            GUILayout.Space(10);
            Stat(BootConfig.Describe().Contains("Jobified") || BootConfig.Describe().Contains("Split") ? "켜짐" : "꺼짐", "멀티스레드 그리기");
            GUILayout.EndHorizontal();

            GUILayout.Space(14);
            Info("마지막 맵 불러오기", ImagePrefetch.Last);
            Info("그래픽 상태", BootConfig.Status.Length > 0 ? BootConfig.Status : BootConfig.Describe());
            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("모두 권장값으로", sAccentBtn, GUILayout.Width(170), GUILayout.Height(36))) ResetDefaults();
            GUILayout.Space(10);
            GUILayout.Label("창 열기/닫기: <b>" + Main.Config.WindowKey + "</b>", sDim, GUILayout.Height(36));
            GUILayout.EndHorizontal();
        }

        private void PagePlay()
        {
            var c = Main.Config;
            GUILayout.Label("플레이", sH1);
            GUILayout.Label("곡을 플레이하는 동안 생기는 끊김을 줄입니다. 모두 켜 두는 것을 권장합니다.", sDim);
            GUILayout.Space(8);
            bool changed = false;
            changed |= Option(ref c.GcPause, "메모리 정리 미루기",
                "플레이 중 게임이 메모리를 정리하느라 잠깐 멈추는 것을 막습니다. 곡이 끝나고 몇 초 뒤 한 번에 정리합니다.", "가장 효과가 큰 기능");
            changed |= Option(ref c.EffectSplit, "효과 몰림 나누기",
                "한 순간에 효과 수십 개가 동시에 시작될 때, 몇 프레임에 나눠 시작해서 화면이 멈추지 않게 합니다.", null);
            changed |= Option(ref c.RecolorSplit, "타일 색 바꾸기 나누기",
                "타일 수천 개의 색을 한 번에 바꾸는 이벤트를 조금씩 나눠 칠합니다. 멀리 있는 타일이 아주 잠깐 늦게 바뀔 뿐 결과는 같습니다.", null);
            changed |= Option(ref c.TweenGuard, "애니메이션 처리 최적화",
                "효과가 많을 때 게임이 애니메이션 목록을 반복해서 다시 정리하느라 느려지는 문제를 막습니다.", null);
            changed |= Option(ref c.SkipSameText, "글자 장식 최적화",
                "같은 글자를 매 프레임 다시 쓰는 글자 장식은 건너뜁니다. PACL2 같은 모드를 함께 쓸 때 효과가 큽니다.", null);
            changed |= Option(ref c.ShaderWarm, "그래픽 미리 준비",
                "곡이 시작될 때 필요한 그래픽 준비를 미리 해 두어, 효과가 처음 나올 때의 끊김을 줄입니다.", null);
            if (changed) Save();
        }

        private void PageLoad()
        {
            var c = Main.Config;
            GUILayout.Label("맵 불러오기", sH1);
            GUILayout.Label("맵을 열거나 편집 화면으로 돌아올 때 기다리는 시간을 줄입니다.", sDim);
            GUILayout.Space(8);
            bool changed = false;
            changed |= Option(ref c.ImagePrefetch, "이미지 빠르게 불러오기",
                "장식 이미지가 많은 맵을 열 때 CPU 여러 코어로 이미지를 동시에 불러옵니다. 이미지가 많은 맵일수록 빨라집니다.", "예) 67초 → 38초");
            changed |= Option(ref c.SkipAssetUnload, "불필요한 정리 건너뛰기",
                "맵을 열거나 편집으로 돌아올 때 게임이 하는 짧은 정리 작업을 건너뛰어 멈춤을 줄입니다.", null);
            if (changed) Save();
            GUILayout.Space(6);
            Info("마지막 맵 불러오기", ImagePrefetch.Last);
        }

        private void PageGraphics()
        {
            var c = Main.Config;
            GUILayout.Label("그래픽", sH1);
            GUILayout.Label("바꾸면 <b>게임을 다시 켜야</b> 적용됩니다.", sDim);
            GUILayout.Space(8);
            bool v = c.LegacyGfxJobs;
            if (Option(ref v, "멀티스레드 그리기",
                "화면을 그리는 준비 작업을 여러 CPU 코어에 나눠 프레임을 높이고 끊김을 줄입니다. 게임 폴더의 boot.config 에 한 줄을 넣으며, 이 모드를 끄면 원래대로 돌려놓습니다.", "권장"))
            {
                c.LegacyGfxJobs = v;
                BootConfig.Apply(v);
                Save();
            }
            Info("지금 상태", BootConfig.Status.Length > 0 ? BootConfig.Status : BootConfig.Describe());
            if (Main.LaunchWarning.Length > 0) Info("주의", Main.LaunchWarning.Trim());
        }

        private void PageAbout()
        {
            GUILayout.Label("정보", sH1);
            GUILayout.Label("<b>Stutter Fix</b>  v" + Main.Entry.Info.Version + " (" + Edition.Name + ")", sBody);
            GUILayout.Space(4);
            GUILayout.Label("made by <b>naro</b> & <b>Claude</b> (Anthropic)", sBody);
            GUILayout.Space(12);
            Info("무엇을 하나요?", "모든 기능은 실제 맵에서 끊긴 순간을 하나씩 측정해 원인을 찾은 뒤 만들었습니다. 연출과 판정은 바꾸지 않습니다.");
            Info("모드를 끄면?", "UMM 에서 끄면 모든 변경을 즉시 되돌립니다. 멀티스레드 그리기 설정은 다음 실행부터 원래대로 돌아갑니다.");
            Info("그래도 끊긴다면", "필터가 아주 많이 겹치는 구간은 그래픽카드 성능 한계이고, 백그라운드 프로그램이 순간적으로 끊김을 만들 수도 있습니다.");
            Info("소스", "github.com/pding4569/StutterFix");
        }

        // ── 부품 ───────────────────────────────────────────────────────
        private bool Option(ref bool value, string title, string desc, string badge)
        {
            GUILayout.BeginVertical(sCard);
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical();
            GUILayout.Label("<b>" + title + "</b>" + (badge != null ? "   <color=#7AA2FF><size=12>" + badge + "</size></color>" : ""), sBody);
            GUILayout.Label(desc, sDim);
            GUILayout.EndVertical();
            GUILayout.Space(12);
            Rect r = GUILayoutUtility.GetRect(56, 30, GUILayout.Width(56), GUILayout.Height(30));
            bool clicked = Switch(r, value);
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();

            // 카드 아무 데나 눌러도 바뀌게
            Rect card = GUILayoutUtility.GetLastRect();
            if (!clicked && Event.current.type == EventType.MouseUp && card.Contains(Event.current.mousePosition)) { clicked = true; Event.current.Use(); }
            GUILayout.Space(8);
            if (clicked) { value = !value; return true; }
            return false;
        }

        private bool Switch(Rect r, bool on)
        {
            float y = r.y + (r.height - 26) / 2f;
            var track = new Rect(r.x, y, 50, 26);
            GUI.color = on ? Accent : Off;
            GUI.DrawTexture(track, tPill);
            GUI.color = Color.white;
            var knob = new Rect(on ? track.xMax - 23 : track.x + 3, y + 3, 20, 20);
            GUI.DrawTexture(knob, tCircle);
            return GUI.Button(track, GUIContent.none, GUIStyle.none);
        }

        private void Stat(string value, string label)
        {
            GUILayout.BeginVertical(sCard, GUILayout.Width(180), GUILayout.Height(74));
            GUILayout.Label(value, sStat);
            GUILayout.Label(label, sStatLabel);
            GUILayout.EndVertical();
        }

        private void Info(string label, string text)
        {
            GUILayout.BeginVertical(sCard);
            GUILayout.Label("<b>" + label + "</b>", sBody);
            GUILayout.Label(text, sDim);
            GUILayout.EndVertical();
            GUILayout.Space(8);
        }

        private void ResetDefaults()
        {
            var c = Main.Config;
            c.GcPause = c.EffectSplit = c.RecolorSplit = c.TweenGuard = c.SkipSameText = c.ShaderWarm = c.ImagePrefetch = c.SkipAssetUnload = true;
            if (!c.LegacyGfxJobs) { c.LegacyGfxJobs = true; BootConfig.Apply(true); }
            Save();
        }

        private void Save()
        {
            Main.ApplyConfig();
            try { Main.Config.Save(Main.Entry); } catch { }
        }

        // ── 모양 만들기 ─────────────────────────────────────────────────
        private void Build()
        {
            built = true;
            tWhite = Texture2D.whiteTexture;
            tRound = RoundTex(14, 14);
            tRoundSmall = RoundTex(10, 10);
            tPill = RoundTex(13, 13);
            tCircle = RoundTex(10, 10);

            sWindow = Box(Bg, tRound, 14);
            sWindow.padding = new RectOffset(0, 0, 0, 0);
            sCard = Box(Card, tRoundSmall, 10);
            sCard.padding = new RectOffset(16, 16, 12, 12);

            sTitle = Label(18, Text); sTitle.alignment = TextAnchor.MiddleLeft;
            sH1 = Label(24, Text); sH1.fontStyle = FontStyle.Bold; sH1.margin = new RectOffset(0, 0, 0, 6);
            sBody = Label(15, Text); sBody.wordWrap = true;
            sDim = Label(13, Dim); sDim.wordWrap = true;
            sSmall = Label(11, Dim);
            sStat = Label(26, Accent); sStat.fontStyle = FontStyle.Bold;
            sStatLabel = Label(12, Dim);

            sNav = Box(Side, tRoundSmall, 10);
            sNav.normal.textColor = Dim; sNav.fontSize = 15; sNav.alignment = TextAnchor.MiddleLeft; sNav.padding = new RectOffset(16, 8, 0, 0);
            sNav.hover.background = Tint(tRoundSmall, CardHover); sNav.hover.textColor = Text;
            sNavOn = new GUIStyle(sNav);
            sNavOn.normal.background = Tint(tRoundSmall, AccentDim); sNavOn.normal.textColor = Text; sNavOn.fontStyle = FontStyle.Bold;
            sNavOn.hover.background = sNavOn.normal.background;

            sButton = Box(Card, tRoundSmall, 10);
            sButton.normal.textColor = Text; sButton.alignment = TextAnchor.MiddleCenter; sButton.fontSize = 14;
            sButton.hover.background = Tint(tRoundSmall, CardHover); sButton.hover.textColor = Text;
            sAccentBtn = new GUIStyle(sButton);
            sAccentBtn.normal.background = Tint(tRoundSmall, Accent); sAccentBtn.normal.textColor = Bg; sAccentBtn.fontStyle = FontStyle.Bold;
            sAccentBtn.hover.background = Tint(tRoundSmall, Color.Lerp(Accent, Color.white, 0.15f)); sAccentBtn.hover.textColor = Bg;

            sClose = new GUIStyle(sButton);
            sClose.normal.background = null; sClose.normal.textColor = Dim; sClose.fontSize = 18;
            sClose.hover.background = Tint(tRoundSmall, CardHover); sClose.hover.textColor = Text;
        }

        private static GUIStyle Label(int size, Color color)
        {
            var s = new GUIStyle(GUI.skin.label) { fontSize = size, richText = true, wordWrap = false };
            s.normal.textColor = color;
            s.hover.textColor = color;
            return s;
        }

        private static GUIStyle Box(Color color, Texture2D shape, int border)
        {
            var s = new GUIStyle(GUI.skin.box) { richText = true };
            s.normal.background = Tint(shape, color);
            s.border = new RectOffset(border, border, border, border);
            s.margin = new RectOffset(0, 0, 0, 0);
            return s;
        }

        // 흰색 둥근 모양을 원하는 색으로 칠한 사본
        private static Texture2D Tint(Texture2D src, Color c)
        {
            var t = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = src.GetPixels();
            for (int i = 0; i < px.Length; i++) px[i] = new Color(c.r, c.g, c.b, c.a * px[i].a);
            t.SetPixels(px);
            t.Apply(false, false);
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }

        // 모서리 반지름 r 인 흰 둥근 사각형 (가장자리는 부드럽게). 9칸 나누기로 늘여 쓴다.
        private static Texture2D RoundTex(int r, int pad)
        {
            int size = r * 2 + 2;
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            var px = new Color[size * size];
            float c0 = r - 0.5f, c1 = size - r - 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float cx = Mathf.Clamp(x, c0, c1), cy = Mathf.Clamp(y, c0, c1);
                    float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                    float a = Mathf.Clamp01(r - d + 0.5f);
                    px[y * size + x] = new Color(1, 1, 1, a);
                }
            t.SetPixels(px);
            t.Apply(false, false);
            t.hideFlags = HideFlags.HideAndDontSave;
            return t;
        }
    }
}
