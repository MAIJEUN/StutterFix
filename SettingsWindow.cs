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
        private float scale = 1f;
        private float fade;
        private readonly Dictionary<string, float> anim = new Dictionary<string, float>();

        private Font font;
        private GUIStyle sWindow, sShadow, sTitle, sSub, sH1, sLead, sBody, sDim, sSmall, sTag, sCard, sCardDark, sNav, sNavOn,
            sPrimary, sClose, sTab, sTabOn, sStat, sStatDark, sStatLabel, sStatLabelDark, sScroll, sThumb;
        private Texture2D tWhite, tMark;

        private const float W = 900f, H = 590f, SideW = 196f, HeaderH = 66f;

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
            try
            {
                float ease = Mathf.SmoothStep(0, 1, fade);
                if (Event.current.type == EventType.Repaint)
                {
                    GUI.color = new Color(0, 0, 0, 0.35f * ease);   // 뒤 게임 화면을 살짝 가린다
                    GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), tWhite);
                }
                GUI.color = new Color(1, 1, 1, ease);
                GUI.matrix = Matrix4x4.TRS(new Vector3(0, (1 - ease) * 10f * scale, 0), Quaternion.identity, new Vector3(scale, scale, 1f));
                SetUiBlocked(rect.Contains(Event.current.mousePosition));
                if (Event.current.type == EventType.Repaint)
                    sShadow.Draw(new Rect(rect.x - 34, rect.y - 22, rect.width + 68, rect.height + 70), false, false, false, false);
                rect = GUI.Window(0x5F1A, rect, DrawWindow, GUIContent.none, sWindow);
            }
            finally
            {
                GUI.color = oldColor;
                GUI.matrix = oldMatrix;
            }
        }

        // 창 내용은 OnGUI 가 끝난 뒤 따로 그려지므로, 스킨 바꿔 끼우기를 여기서 한다.
        private void DrawWindow(int id)
        {
            var oldBar = GUI.skin.verticalScrollbar;
            var oldThumb = GUI.skin.verticalScrollbarThumb;
            var oldColor = GUI.color;
            GUI.skin.verticalScrollbar = sScroll;         // 스크롤바 손잡이 모양은 이름으로 찾으므로 잠깐 바꿔 끼운다
            GUI.skin.verticalScrollbarThumb = sThumb;
            GUI.color = Color.white;
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
            // ── 제목줄
            GUI.DrawTexture(new Rect(28, 22, 24, 24), tMark);
            GUI.Label(new Rect(62, 14, 300, 24), "Stutter Fix", sTitle);
            GUI.Label(new Rect(62, 37, 400, 18), T("끊김 줄이기", "Stutter reduction") + "  ·  v" + Main.Entry.Info.Version, sSub);

            // 언어: 글자 탭 + 선택된 쪽 아래 짧은 검정 선
            float tx = W - 222;
            var ko = new Rect(tx, 20, 70, 28);
            var en = new Rect(tx + 74, 20, 76, 28);
            if (GUI.Button(ko, "한국어", English ? sTab : sTabOn)) SetLanguage("ko");
            if (GUI.Button(en, "English", English ? sTabOn : sTab)) SetLanguage("en");
            var sel = English ? en : ko;
            Fill(new Rect(sel.center.x - 9, sel.yMax + 1, 18, 2), Ink, 1);
            if (GUI.Button(new Rect(W - 56, 18, 34, 32), "×", sClose)) SetOpen(false);

            // ── 왼쪽 메뉴
            string[] pages = { T("홈", "Home"), T("플레이", "Gameplay"), T("맵 불러오기", "Level loading"), T("그래픽", "Graphics"), T("정보", "About") };
            for (int i = 0; i < pages.Length; i++)
            {
                var r = new Rect(18, HeaderH + 14 + i * 44, SideW - 30, 38);
                if (GUI.Button(r, pages[i], i == page ? sNavOn : sNav)) { page = i; scroll = Vector2.zero; }
            }
            GUI.Label(new Rect(30, H - 56, SideW - 30, 18), "naro & Claude", sSmall);
            GUI.Label(new Rect(30, H - 38, SideW - 30, 18), English ? (Edition.Dev ? "developer build" : "player build") : Edition.Name, sSmall);

            // ── 본문
            var body = new Rect(SideW + 14, HeaderH + 10, W - SideW - 32, H - HeaderH - 26);
            GUILayout.BeginArea(body);
            scroll = GUILayout.BeginScrollView(scroll, false, false, GUIStyle.none, sScroll, GUIStyle.none);
            GUILayout.BeginVertical(GUILayout.Width(body.width - 22));
            GUILayout.Space(6);   // 첫 카드 그림자가 잘리지 않게
            switch (page)
            {
                case 0: PageHome(); break;
                case 1: PagePlay(); break;
                case 2: PageLoad(); break;
                case 3: PageGraphics(); break;
                default: PageAbout(); break;
            }
            GUILayout.Space(8);
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
            GUILayout.EndArea();

            GUI.DragWindow(new Rect(0, 0, tx - 10, HeaderH));
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
            Stat(on + " / 9", T("켜진 기능", "Features on"), true);
            GUILayout.Space(14);
            Stat(GcControl.Paused ? T("미루는 중", "Deferred") : T("대기", "Idle"), T("메모리 정리", "Memory cleanup"), false);
            GUILayout.Space(14);
            Stat(jobs ? T("켜짐", "On") : T("꺼짐", "Off"), T("멀티스레드 그리기", "Multithreaded rendering"), false);
            GUILayout.EndHorizontal();
            GUILayout.Space(14);

            InfoCard(new[]
            {
                T("마지막 맵 불러오기", "Last level load"), LoadSummary(),
                T("그래픽", "Graphics"), d.Replace("지금 ", ""),
            });

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(T("모두 권장값으로", "Reset to recommended"), sPrimary, GUILayout.Width(170), GUILayout.Height(38))) ResetDefaults();
            GUILayout.Space(16);
            GUILayout.Label(T("창 열기/닫기", "Open / close") + "  <color=#" + HexStr(Ink) + "><b>" + Main.Config.WindowKey + "</b></color>", sDim, GUILayout.Height(38));
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

            var track = new Rect(r.x, r.y + 1, 44, 24);
            GUI.DrawTexture(track, tWhite, ScaleMode.StretchToFill, true, 0, Color.Lerp(TrackOff, Ink, k), 0, 12);
            float kx = Mathf.Lerp(track.x + 3, track.xMax - 21, k);
            GUI.DrawTexture(new Rect(kx, track.y + 3, 18, 18), tWhite, ScaleMode.StretchToFill, true, 0, Color.white, 0, 9);
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
            GUI.DrawTexture(r, tWhite, ScaleMode.StretchToFill, true, 0, c, 0, radius);
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
            tMark = Mark(48);

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
        private static Texture2D NewTex(int w, int h)
        {
            return new Texture2D(w, h, TextureFormat.RGBA32, false)
            { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, hideFlags = HideFlags.HideAndDontSave };
        }

        // 한 변이 size 인 정사각형 안의 반지름 r 둥근 사각형까지의 거리 (안쪽이 음수)
        private static float RoundDist(float x, float y, float size, float r)
        {
            float h = size / 2f;
            float qx = Mathf.Abs(x - h) - (h - r), qy = Mathf.Abs(y - h) - (h - r);
            float ox = Mathf.Max(qx, 0), oy = Mathf.Max(qy, 0);
            return Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0) - r;
        }

        // 둥근 카드: 채움 + 테두리(두께 bw) + 바깥 pad 만큼 아래로 살짝 떨어진 옅은 그림자(진하기 shadowA).
        // 9칸 나누기로 늘여 쓰고, 그림자 부분은 GUIStyle.overflow 로 카드 바깥에 그린다.
        private static Texture2D Card(Color fill, Color border, int r, int bw, int pad, float shadowA)
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
        private static Texture2D Shadow(int half, int blur)
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
