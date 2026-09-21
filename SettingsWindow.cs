using System;
using System.Collections.Generic;
using UnityEngine;

namespace StutterFix
{
    // UMM 목록 안이 아니라 게임 화면 위에 따로 뜨는 설정 창 (기본 단축키 Insert).
    //
    // 플레이어가 기술 용어 없이 "무엇이 좋아지는지"만 보고 고를 수 있게 한다. 한국어/English 전환.
    // 모양은 반투명 유리: 뒤가 비치는 어두운 판, 위쪽의 옅은 빛, 흰색 반투명 테두리, 흰 계열만 쓰는 절제된 색.
    // (뒤를 실제로 흐리게 하려면 매 프레임 화면을 떠야 하는데, 그러면 창 자신까지 찍혀 깜빡이고 프레임도 잡아먹는다.
    //  대신 창을 열면 배경을 살짝 어둡게 해서 읽기 쉽게 한다.)
    // IMGUI 로 그리되 기본 회색 상자는 쓰지 않는다. 글꼴은 윈도우의 Segoe UI + 맑은 고딕.
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

        // ── 색 (흰색의 투명도만으로 층을 나눈다) ────────────────────────
        private static Color White(float a) { return new Color(1f, 1f, 1f, a); }
        private static readonly Color Glass = new Color(0.075f, 0.08f, 0.1f, 0.70f);
        private static readonly Color Text = White(0.93f), Dim = White(0.56f), Faint = White(0.34f);
        private static readonly Color Line = White(0.10f), Rim = White(0.16f);
        private static readonly Color CardFill = White(0.045f), CardHoverFill = White(0.075f), CardRim = White(0.08f);
        private static readonly Color OnTrack = new Color(0.86f, 0.92f, 1f, 0.92f), OffTrack = White(0.14f);
        private static readonly Color Ink = new Color(0.09f, 0.1f, 0.12f, 1f);

        private static string HexStr(Color c) { return ColorUtility.ToHtmlStringRGBA(c); }

        // ── 상태 ───────────────────────────────────────────────────────
        private Rect rect;
        private bool needCenter = true;
        private int page;
        private Vector2 scroll;
        private bool cursorWas;
        private bool built;
        private float scale = 1f;
        private float fade;   // 열릴 때 서서히 나타나게
        private readonly Dictionary<string, float> anim = new Dictionary<string, float>();

        private Font font;
        private GUIStyle sWindow, sShadow, sTitle, sSub, sH1, sLead, sBody, sDim, sSmall, sTag, sCard, sNav, sNavOn,
            sPrimary, sClose, sSeg, sSegOn, sSegBox, sStat, sStatLabel, sScroll, sThumb;
        private Texture2D tWhite, tMark, tSheen;

        private const float W = 900f, H = 590f, SideW = 200f, HeaderH = 62f;

        private void SetOpen(bool open)
        {
            if (open == Open) return;
            Open = open;
            if (open) { cursorWas = Cursor.visible; fade = 0f; }
            else { Cursor.visible = cursorWas; SetUiBlocked(false); }
        }

        private void Update()
        {
            if (Main.Config == null) return;
            if (Input.GetKeyDown(Main.Config.WindowKey)) SetOpen(!Open);
            if (!Open) return;
            if (Input.GetKeyDown(KeyCode.Escape)) { SetOpen(false); return; }
            Cursor.visible = true;   // 곡 중에는 게임이 커서를 숨긴다
            fade = Mathf.MoveTowards(fade, 1f, Time.unscaledDeltaTime * 6f);
        }

        // 창 위를 누를 때 뒤의 게임 UI(에디터 버튼 등)가 같이 눌리지 않게 막는다.
        // 끈 이벤트 시스템을 직접 들고 있어야 한다. 끄는 순간 EventSystem.current 가 비어서,
        // 예전에는 다시 켤 대상을 못 찾아 창을 닫은 뒤 게임 클릭이 영영 먹통이 됐다.
        private UnityEngine.EventSystems.EventSystem blocked;
        private void SetUiBlocked(bool block)
        {
            try
            {
                if (block)
                {
                    if (blocked != null) return;
                    var es = UnityEngine.EventSystems.EventSystem.current;
                    if (es == null || !es.enabled) return;
                    es.enabled = false;
                    blocked = es;
                }
                else if (blocked != null)
                {
                    var es = blocked;
                    blocked = null;
                    if (es != null) es.enabled = true;   // 씬이 바뀌어 사라졌으면 할 일 없음
                }
            }
            catch { blocked = null; }
        }

        private void OnDisable() { SetUiBlocked(false); }
        private void OnDestroy() { SetUiBlocked(false); }

        private void OnGUI()
        {
            if (!Open) return;
            if (!built) Build();

            // 배율을 먼저 정하고 나서 가운데를 잡는다(예전에는 배율 1로 계산해 구석에 떴다)
            scale = Mathf.Clamp(Screen.height / 1080f * 1.1f, 0.8f, 2.2f);
            float sw = Screen.width / scale, sh = Screen.height / scale;
            if (needCenter) { rect = new Rect((sw - W) / 2f, (sh - H) / 2f, W, H); needCenter = false; }
            rect.x = Mathf.Clamp(rect.x, 0, Mathf.Max(0, sw - rect.width));
            rect.y = Mathf.Clamp(rect.y, 0, Mathf.Max(0, sh - rect.height));

            var oldMatrix = GUI.matrix;
            var oldColor = GUI.color;
            var oldFont = GUI.skin.font;
            var oldBar = GUI.skin.verticalScrollbar;
            var oldThumb = GUI.skin.verticalScrollbarThumb;
            GUI.skin.font = font;
            GUI.skin.verticalScrollbar = sScroll;         // 스크롤바 손잡이 모양은 이름으로 찾으므로 잠깐 바꿔 끼운다
            GUI.skin.verticalScrollbarThumb = sThumb;
            try
            {
                float ease = Mathf.SmoothStep(0, 1, fade);

                // 배경을 살짝 어둡게 (화면 전체, 배율 적용 전 좌표)
                if (Event.current.type == EventType.Repaint)
                {
                    GUI.color = new Color(0, 0, 0, 0.32f * ease);
                    GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), tWhite);
                }

                GUI.matrix = Matrix4x4.TRS(new Vector3(0, (1 - ease) * 12f * scale, 0), Quaternion.identity, new Vector3(scale, scale, 1f));
                GUI.color = new Color(1, 1, 1, ease);
                SetUiBlocked(rect.Contains(Event.current.mousePosition));
                if (Event.current.type == EventType.Repaint)
                    sShadow.Draw(new Rect(rect.x - 30, rect.y - 20, rect.width + 60, rect.height + 60), false, false, false, false);
                rect = GUI.Window(0x5F1A, rect, DrawWindow, GUIContent.none, sWindow);
            }
            finally
            {
                GUI.skin.verticalScrollbar = oldBar;
                GUI.skin.verticalScrollbarThumb = oldThumb;
                GUI.skin.font = oldFont;
                GUI.color = oldColor;
                GUI.matrix = oldMatrix;
            }
        }

        // 창 내용은 OnGUI 가 끝난 뒤 따로 그려지므로, 스킨 바꿔 끼우기를 여기서 다시 한다.
        private void DrawWindow(int id)
        {
            var oldBar = GUI.skin.verticalScrollbar;
            var oldThumb = GUI.skin.verticalScrollbarThumb;
            GUI.skin.verticalScrollbar = sScroll;
            GUI.skin.verticalScrollbarThumb = sThumb;
            try { DrawContents(); }
            finally
            {
                GUI.skin.verticalScrollbar = oldBar;
                GUI.skin.verticalScrollbarThumb = oldThumb;
            }
        }

        private void DrawContents()
        {
            // 위쪽의 옅은 빛 반사
            if (Event.current.type == EventType.Repaint) GUI.DrawTexture(new Rect(1, 1, W - 2, 140), tSheen);

            // ── 제목줄
            GUI.DrawTexture(new Rect(24, 20, 22, 22), tMark);
            GUI.Label(new Rect(56, 12, 300, 22), "Stutter Fix", sTitle);
            GUI.Label(new Rect(56, 33, 400, 18), T("끊김 줄이기", "Stutter reduction") + "   v" + Main.Entry.Info.Version, sSub);

            float segX = W - 236;
            GUI.Box(new Rect(segX, 15, 172, 32), GUIContent.none, sSegBox);
            if (GUI.Button(new Rect(segX + 3, 18, 82, 26), "한국어", English ? sSeg : sSegOn)) SetLanguage("ko");
            if (GUI.Button(new Rect(segX + 87, 18, 82, 26), "English", English ? sSegOn : sSeg)) SetLanguage("en");
            if (GUI.Button(new Rect(W - 50, 15, 32, 32), "×", sClose)) SetOpen(false);

            Fill(new Rect(20, HeaderH - 1, W - 40, 1), Line);

            // ── 왼쪽 메뉴
            Fill(new Rect(SideW - 1, HeaderH + 18, 1, H - HeaderH - 36), Line);
            string[] pages = { T("홈", "Home"), T("플레이", "Gameplay"), T("맵 불러오기", "Level loading"), T("그래픽", "Graphics"), T("정보", "About") };
            for (int i = 0; i < pages.Length; i++)
            {
                var r = new Rect(14, HeaderH + 20 + i * 42, SideW - 30, 36);
                if (GUI.Button(r, pages[i], i == page ? sNavOn : sNav)) { page = i; scroll = Vector2.zero; }
            }

            GUI.Label(new Rect(26, H - 56, SideW - 30, 18), "naro & Claude", sSmall);
            GUI.Label(new Rect(26, H - 38, SideW - 30, 18), English ? (Edition.Dev ? "developer build" : "player build") : Edition.Name, sSmall);

            // ── 본문
            var body = new Rect(SideW + 28, HeaderH + 22, W - SideW - 44, H - HeaderH - 36);
            GUILayout.BeginArea(body);
            scroll = GUILayout.BeginScrollView(scroll, false, false, GUIStyle.none, sScroll, GUIStyle.none);
            GUILayout.BeginVertical(GUILayout.Width(body.width - 20));
            switch (page)
            {
                case 0: PageHome(); break;
                case 1: PagePlay(); break;
                case 2: PageLoad(); break;
                case 3: PageGraphics(); break;
                default: PageAbout(); break;
            }
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            GUI.DragWindow(new Rect(0, 0, segX - 10, HeaderH));
        }

        // ── 페이지 ─────────────────────────────────────────────────────
        private void PageHome()
        {
            Heading(T("홈", "Home"), T("고사양 커스텀 맵에서 플레이 중 순간적으로 멈추는 현상과 맵 로딩 시간을 줄입니다. 연출과 판정은 바꾸지 않습니다.",
                "Reduces hitches during play and loading times on heavy custom levels. Visuals and judgement are unchanged."));

            var c = Main.Config;
            int on = (c.GcPause ? 1 : 0) + (c.EffectSplit ? 1 : 0) + (c.RecolorSplit ? 1 : 0) + (c.TweenGuard ? 1 : 0) + (c.SkipSameText ? 1 : 0)
                   + (c.ShaderWarm ? 1 : 0) + (c.ImagePrefetch ? 1 : 0) + (c.SkipAssetUnload ? 1 : 0) + (c.LegacyGfxJobs ? 1 : 0);
            string d = BootConfig.Describe();
            bool jobs = d.Contains("Jobified") || d.Contains("Split");

            GUILayout.BeginHorizontal();
            Stat(on + "/9", T("켜진 기능", "Features on"));
            GUILayout.Space(10);
            Stat(GcControl.Paused ? T("미루는 중", "Deferred") : T("대기", "Idle"), T("메모리 정리", "Memory cleanup"));
            GUILayout.Space(10);
            Stat(jobs ? T("켜짐", "On") : T("꺼짐", "Off"), T("멀티스레드 그리기", "Multithreaded rendering"));
            GUILayout.EndHorizontal();
            GUILayout.Space(10);

            InfoCard(new[]
            {
                T("마지막 맵 불러오기", "Last level load"), LoadSummary(),
                T("그래픽", "Graphics"), d.Replace("지금 ", ""),
            });

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(T("모두 권장값으로", "Reset to recommended"), sPrimary, GUILayout.Width(180), GUILayout.Height(36))) ResetDefaults();
            GUILayout.Space(14);
            GUILayout.Label(T("창 열기/닫기", "Open / close") + "   <color=#" + HexStr(Text) + ">" + Main.Config.WindowKey + "</color>", sDim, GUILayout.Height(36));
            GUILayout.EndHorizontal();
        }

        private void PagePlay()
        {
            var c = Main.Config;
            Heading(T("플레이", "Gameplay"), T("곡을 플레이하는 동안의 끊김을 줄입니다. 모두 켜 두는 것을 권장합니다.",
                "Reduces hitches while a level is playing. Keeping everything on is recommended."));
            bool ch = false;
            ch |= Option("gc", ref c.GcPause, T("메모리 정리 미루기", "Defer memory cleanup"),
                T("플레이 중 게임이 메모리를 정리하느라 잠깐 멈추는 것을 막습니다. 곡이 끝나고 몇 초 뒤 한 번에 정리합니다.",
                  "Stops the game from pausing to clean up memory mid-song. Cleanup runs once, a few seconds after the level ends."),
                T("효과 가장 큼", "biggest impact"));
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
            if (ch) Save();
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
                T("권장", "recommended")))
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

        private void PageAbout()
        {
            Heading("Stutter Fix", "v" + Main.Entry.Info.Version + "   " + (English ? (Edition.Dev ? "developer build" : "player build") : Edition.Name));
            GUILayout.BeginVertical(sCard);
            GUILayout.Label("naro & Claude", sBody);
            GUILayout.Space(4);
            GUILayout.Label(T("실제 맵에서 끊긴 순간을 하나씩 측정해 원인을 찾고, 원인마다 고친 모드입니다.",
                "Built by measuring each real hitch in real levels, finding its cause, and fixing it."), sDim);
            GUILayout.EndVertical();
            GUILayout.Space(10);
            InfoCard(new[]
            {
                T("모드를 끄면", "Turning it off"), T("UMM 에서 끄면 모든 변경을 즉시 되돌립니다. 멀티스레드 그리기는 다음 실행부터 원래대로 돌아갑니다.",
                    "Turning the mod off in UMM reverts everything immediately. Multithreaded rendering reverts on the next launch."),
                T("그래도 끊긴다면", "Still stuttering?"), T("필터가 아주 많이 겹치는 구간은 그래픽카드 한계이고, 백그라운드 프로그램이 순간 끊김을 만들 수도 있습니다.",
                    "Scenes stacking many full-screen filters are limited by the GPU, and background apps can cause occasional hitches."),
                T("소스", "Source"), "github.com/pding4569/StutterFix",
            });
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

        private bool Option(string key, ref bool value, string title, string desc, string tag)
        {
            GUILayout.BeginHorizontal(sCard);
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUILayout.Label(title, sBody, GUILayout.ExpandWidth(false));
            if (tag != null) { GUILayout.Space(10); GUILayout.Label(tag, sTag, GUILayout.ExpandWidth(false)); }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.Space(4);
            GUILayout.Label(desc, sDim);
            GUILayout.EndVertical();
            GUILayout.Space(22);
            GUILayout.BeginVertical(GUILayout.Width(44));
            GUILayout.FlexibleSpace();
            Rect r = GUILayoutUtility.GetRect(44, 24, GUILayout.Width(44), GUILayout.Height(24));
            GUILayout.FlexibleSpace();
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            Rect card = GUILayoutUtility.GetLastRect();
            GUILayout.Space(8);

            DrawSwitch(key, r, value);

            // 카드 아무 데나 눌러도 바뀐다
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0 && card.Contains(e.mousePosition))
            {
                e.Use();
                value = !value;
                return true;
            }
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

            var track = new Rect(r.x, r.y + 1, 44, 22);
            GUI.DrawTexture(track, tWhite, ScaleMode.StretchToFill, true, 0, Color.Lerp(OffTrack, OnTrack, k), 0, 11);
            float kx = Mathf.Lerp(track.x + 3, track.xMax - 19, k);
            GUI.DrawTexture(new Rect(kx, track.y + 3, 16, 16), tWhite, ScaleMode.StretchToFill, true, 0, Color.Lerp(White(0.75f), Ink, k), 0, 8);
        }

        private void Stat(string value, string label)
        {
            GUILayout.BeginVertical(sCard, GUILayout.ExpandWidth(true), GUILayout.Height(80));
            GUILayout.Label(value, sStat);
            GUILayout.Space(2);
            GUILayout.Label(label, sStatLabel);
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
                    GUILayout.Space(10);
                    Rect line = GUILayoutUtility.GetRect(1, 1, GUILayout.ExpandWidth(true), GUILayout.Height(1));
                    Fill(line, Line);
                    GUILayout.Space(10);
                }
                GUILayout.Label(kv[i], sSmall);
                GUILayout.Space(3);
                GUILayout.Label(kv[i + 1], sDim);
            }
            GUILayout.EndVertical();
            GUILayout.Space(10);
        }

        private void Fill(Rect r, Color c)
        {
            if (Event.current.type != EventType.Repaint) return;
            var old = GUI.color;
            GUI.color = new Color(c.r, c.g, c.b, c.a * old.a);
            GUI.DrawTexture(r, tWhite);
            GUI.color = old;
        }

        private void SetLanguage(string lang)
        {
            Main.Config.Language = lang;
            try { Main.Config.Save(Main.Entry); } catch { }
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
            try { font = Font.CreateDynamicFontFromOSFont(new[] { "Segoe UI", "Malgun Gothic", "Arial" }, 16); } catch { font = null; }
            if (font == null) font = GUI.skin.font;

            tWhite = Texture2D.whiteTexture;
            tMark = Mark(44);
            tSheen = Sheen(128);

            sWindow = Styled(Round(Glass, Rim, 18, 1), 20);
            sWindow.padding = new RectOffset(0, 0, 0, 0);
            sShadow = Styled(Shadow(44, 30), 44);
            sCard = Styled(Round(CardFill, CardRim, 12, 1), 14);
            sCard.padding = new RectOffset(18, 18, 14, 15);
            sCard.hover.background = Round(CardHoverFill, White(0.12f), 12, 1);

            sTitle = Label(16, Text, FontStyle.Bold);
            sSub = Label(12, Faint, FontStyle.Normal);
            sH1 = Label(24, Text, FontStyle.Normal);
            sLead = Label(13, Dim, FontStyle.Normal); sLead.wordWrap = true;
            sBody = Label(15, Text, FontStyle.Bold); sBody.wordWrap = true;
            sDim = Label(13, Dim, FontStyle.Normal); sDim.wordWrap = true;
            sSmall = Label(11, Faint, FontStyle.Normal);
            sTag = Label(11, Faint, FontStyle.Normal); sTag.padding = new RectOffset(0, 0, 4, 0);
            sStat = Label(22, Text, FontStyle.Normal);
            sStatLabel = Label(12, Faint, FontStyle.Normal);

            sNav = Styled(null, 10);
            sNav.normal.textColor = Dim; sNav.fontSize = 14; sNav.alignment = TextAnchor.MiddleLeft; sNav.padding = new RectOffset(14, 8, 0, 0);
            sNav.hover.background = Round(White(0.04f), Color.clear, 9, 0); sNav.hover.textColor = Text;
            sNavOn = new GUIStyle(sNav);
            sNavOn.normal.background = Round(White(0.09f), White(0.06f), 9, 1); sNavOn.normal.textColor = Text;
            sNavOn.hover.background = sNavOn.normal.background; sNavOn.hover.textColor = Text;

            sPrimary = Styled(Round(White(0.9f), Color.clear, 10, 0), 12);
            sPrimary.normal.textColor = Ink; sPrimary.alignment = TextAnchor.MiddleCenter; sPrimary.fontSize = 14; sPrimary.fontStyle = FontStyle.Bold;
            sPrimary.hover.background = Round(White(1f), Color.clear, 10, 0); sPrimary.hover.textColor = Ink;

            sSegBox = Styled(Round(White(0.05f), White(0.08f), 10, 1), 12);
            sSeg = Styled(null, 8);
            sSeg.normal.textColor = Dim; sSeg.alignment = TextAnchor.MiddleCenter; sSeg.fontSize = 13;
            sSeg.hover.textColor = Text;
            sSegOn = new GUIStyle(sSeg);
            sSegOn.normal.background = Round(White(0.14f), Color.clear, 8, 0); sSegOn.normal.textColor = Text;
            sSegOn.hover.background = sSegOn.normal.background;

            sClose = Styled(null, 10);
            sClose.normal.textColor = Dim; sClose.alignment = TextAnchor.MiddleCenter; sClose.fontSize = 22; sClose.padding = new RectOffset(0, 0, 0, 4);
            sClose.hover.background = Round(White(0.08f), Color.clear, 9, 0); sClose.hover.textColor = Text;

            sScroll = new GUIStyle { fixedWidth = 4, margin = new RectOffset(12, 0, 0, 0), border = new RectOffset(2, 2, 2, 2) };
            sScroll.normal.background = null;
            sThumb = new GUIStyle { fixedWidth = 4, border = new RectOffset(2, 2, 2, 2) };
            sThumb.normal.background = Round(White(0.18f), Color.clear, 2, 0);
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
        private static Texture2D NewTex(int w, int h)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        }

        // 크기 size 인 정사각형 안의 반지름 r 둥근 사각형까지의 거리 (안쪽이 음수)
        private static float RoundDist(float x, float y, float size, float r)
        {
            float h = size / 2f;
            float qx = Mathf.Abs(x - h) - (h - r), qy = Mathf.Abs(y - h) - (h - r);
            float ox = Mathf.Max(qx, 0), oy = Mathf.Max(qy, 0);
            return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0) - r;
        }

        // 채움색 + 테두리(두께 bw) 둥근 사각형. 9칸 나누기로 늘여 쓴다. 두 색 모두 투명도를 가질 수 있다.
        private static Texture2D Round(Color fill, Color border, int r, int bw)
        {
            int size = r * 2 + 4;
            var t = NewTex(size, size);
            var px = new Color[size * size];
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = RoundDist(x + 0.5f, y + 0.5f, size, r);
                    float cover = Mathf.Clamp01(0.5f - d);
                    Color c = fill;
                    if (bw > 0)
                    {
                        float inner = Mathf.Clamp01(-d - bw + 0.5f);   // 1 이면 테두리 안쪽
                        // 테두리를 채움 위에 겹쳐 그린 색
                        float ba = border.a * (1 - inner);
                        float a = ba + fill.a * (1 - ba);
                        Color rgb = a > 0 ? (border * ba + fill * fill.a * (1 - ba)) / a : fill;
                        c = new Color(rgb.r, rgb.g, rgb.b, a);
                    }
                    c.a *= cover;
                    px[y * size + x] = c;
                }
            t.SetPixels(px);
            t.Apply(false, false);
            return t;
        }

        // 위에서 아래로 사라지는 옅은 빛 (유리 윗면의 반사)
        private static Texture2D Sheen(int h)
        {
            var t = NewTex(1, h);
            for (int y = 0; y < h; y++)
            {
                float k = y / (float)(h - 1);                // 0 = 아래, 1 = 위 (텍스처 좌표)
                t.SetPixel(0, y, White(0.07f * k * k));
            }
            t.Apply(false, false);
            return t;
        }

        // 제목 옆의 작은 표시: 흰 막대 세 개 (프레임 그래프)
        private static Texture2D Mark(int s)
        {
            var t = NewTex(s, s);
            for (int y = 0; y < s; y++)
                for (int x = 0; x < s; x++)
                {
                    float u = (x + 0.5f) / s, v = (y + 0.5f) / s;
                    float a = 0;
                    a = Mathf.Max(a, Bar(u, v, 0.14f, 0.30f, 0.52f));
                    a = Mathf.Max(a, Bar(u, v, 0.42f, 0.58f, 0.92f));
                    a = Mathf.Max(a, Bar(u, v, 0.70f, 0.86f, 0.72f));
                    t.SetPixel(x, y, White(a * 0.92f));
                }
            t.Apply(false, false);
            return t;
        }

        // 아래에서 top 높이까지 올라오는 둥근 막대
        private static float Bar(float u, float v, float x0, float x1, float top)
        {
            float w = x1 - x0, r = w / 2f, cx = (x0 + x1) / 2f;
            float bottom = 0.08f;
            float cy = Mathf.Clamp(v, bottom + r, top - r);
            float d = Mathf.Sqrt((u - cx) * (u - cx) + (v - cy) * (v - cy)) - r;
            return Mathf.Clamp01(0.5f - d * 44f);
        }

        // 창 뒤의 부드러운 그림자
        private static Texture2D Shadow(int half, int blur)
        {
            int n = half * 2;
            var t = NewTex(n, n);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = RoundDist(x + 0.5f, y + 0.5f, n, blur + 6) + blur;
                    float a = Mathf.Clamp01(1f - d / blur);
                    t.SetPixel(x, y, new Color(0, 0, 0, a * a * 0.45f));
                }
            t.Apply(false, false);
            return t;
        }
    }
}
