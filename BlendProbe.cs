using System.Collections.Generic;
using System.Linq;
using BlendModes;
using UnityEngine;

namespace StutterFix
{
    // (개발자용) 블렌드 장식이 GPU 를 얼마나 먹는지 알아보기 위한 조사 도구.
    //
    // 장식의 블렌드 모드는 "Blend Modes" 에셋(BlendModeEffect)이 그린다. 이 에셋은 화면과 섞기 위해
    // 물체마다 화면 전체를 복사(GrabPass)할 수 있는데, 3440x1440 에서 화면에 보이는 블렌드 장식 수만큼 복사하면
    // 그것만으로 프레임이 수십 ms 가 된다. "한 프레임에 한 번만 복사(UnifiedGrab)" 모드로 바꾸면 빨라지지만,
    // 블렌드 장식끼리 겹친 곳은 앞 장식의 결과를 못 보게 되어 모양이 달라질 수 있다.
    //
    //   F10        : 지금 켜진 블렌드 장식을 모드/설정/셰이더별로 세어 로그에 적는다 (화면에 보이는 수 포함)
    //   Shift+F10  : 실험 - 모든 블렌드 장식을 "한 번만 복사" 로 바꾼다 / 다시 누르면 원래대로
    //   F11        : 실험 - 더하기/스크린/곱하기 장식을 복사 없이 그리기 (FastBlend) / 다시 누르면 원래대로
    internal static class BlendProbe
    {
        private static readonly Dictionary<BlendModeEffect, bool> changed = new Dictionary<BlendModeEffect, bool>();
        internal static bool UnifiedOn { get { return changed.Count > 0; } }

        internal static void Tick()
        {
            if (Input.GetKeyDown(KeyCode.F11)) { FastBlend.Toggle(); Report(); }
            if (!Input.GetKeyDown(KeyCode.F10)) return;
            bool shift = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (shift) ToggleUnified(); else Report();
        }

        private static BlendModeEffect[] All()
        {
            return Object.FindObjectsByType<BlendModeEffect>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        }

        internal static void Report()
        {
            try
            {
                var all = All();
                int enabled = 0, visible = 0;
                var groups = new Dictionary<string, int[]>();
                foreach (var b in all)
                {
                    if (b == null || !b.enabled) continue;
                    enabled++;
                    var r = b.GetComponent<Renderer>();
                    bool vis = r != null && r.isVisible;
                    if (vis) visible++;
                    var mat = r != null ? r.sharedMaterial : null;
                    string key = string.Format("{0} / {1} / 한번복사={2} / fb={3} / 공유={4} / {5} / 셰이더 {6}",
                        b.BlendMode, b.RenderMode, b.UnifiedGrabEnabled, b.FramebufferEnabled, b.ShareMaterial,
                        r != null ? r.GetType().Name : "렌더러 없음", mat != null && mat.shader != null ? mat.shader.name : "-");
                    int[] c;
                    if (!groups.TryGetValue(key, out c)) groups[key] = c = new int[2];
                    c[0]++; if (vis) c[1]++;
                }
                Main.Entry.Logger.Log(string.Format("[블렌드] 전체 {0}개, 켜짐 {1}개, 화면에 보임 {2}개, 화면 {3}x{4}{5}",
                    all.Length, enabled, visible, Screen.width, Screen.height, UnifiedOn ? " (실험: 한 번만 복사 켜짐)" : ""));
                foreach (var kv in groups.OrderByDescending(k => k.Value[0]).Take(20))
                    Main.Entry.Logger.Log(string.Format("[블렌드]   {0}개 (보임 {1}) : {2}", kv.Value[0], kv.Value[1], kv.Key));
            }
            catch (System.Exception ex) { Main.Entry.Logger.Log("[블렌드] 조사 실패: " + ex.Message); }
        }

        private static void ToggleUnified()
        {
            try
            {
                if (changed.Count > 0)
                {
                    foreach (var kv in changed) if (kv.Key != null) kv.Key.UnifiedGrabEnabled = kv.Value;
                    int n = changed.Count;
                    changed.Clear();
                    Main.Entry.Logger.Log("[블렌드] 실험 끔: " + n + "개 원래대로");
                    return;
                }
                foreach (var b in All())
                {
                    if (b == null || b.UnifiedGrabEnabled) continue;
                    changed[b] = b.UnifiedGrabEnabled;
                    b.UnifiedGrabEnabled = true;
                }
                Main.Entry.Logger.Log("[블렌드] 실험 켬: " + changed.Count + "개를 한 번만 복사로");
                Report();
            }
            catch (System.Exception ex) { Main.Entry.Logger.Log("[블렌드] 실험 실패: " + ex.Message); }
        }
    }
}
