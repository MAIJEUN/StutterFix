using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 투명도가 0 인 이미지 장식은 그리지 않는다.
    //
    // 측정: hello (BPM) 2026 의 가장 가벼운 구간에서 메인 스레드 6.1ms 중 스크립트는 1.8ms 뿐이고,
    // 가장 큰 덩어리는 화면 그리기 준비(FinishFrameRendering) 2.2ms 였다. 유니티의 스프라이트는 투명도가 0 이어도
    // 컬링, 정렬, 묶기를 거쳐 그리기 명령까지 나가고, GPU 도 큰 투명 이미지를 통째로 칠한다.
    // 맵은 나중에 나타날 이미지를 투명도 0 으로 깔아 두는 경우가 많다(Arche 에서 효과가 건드린 장식의 16% 가 투명도 0).
    //
    // 게임은 장식 색을 scrVisualDecoration.ApplyColor 한 곳에서만 칠한다(투명도 = 색의 알파 x 장식 불투명도 x 타일 불투명도).
    // 그 직후에 알파가 0 이면 renderer.forceRenderingOff 를 켠다. 이 스위치는 게임이 쓰는 renderer.enabled 와 별개라
    // 장식 켜기/끄기(SetVisible)와 서로 덮어쓰지 않는다. 게임 코드에는 forceRenderingOff 를 만지는 곳이 없다.
    // 알파가 다시 0 보다 커지면 같은 자리에서 바로 푼다.
    //
    // 알파가 0 이어도 무언가를 그릴 수 있는 셰이더(알파를 다른 용도로 쓰는 것)가 있을 수 있어,
    // 투명도가 곧 보이는 정도인 것으로 확인된 셰이더에만 적용한다.
    public static class InvisibleSkip
    {
        internal static bool Enabled = true;

        private static readonly AccessTools.FieldRef<scrVisualDecoration, SpriteRenderer> rendererRef =
            AccessTools.FieldRefAccess<scrVisualDecoration, SpriteRenderer>("spriteRenderer");
        private static readonly AccessTools.FieldRef<scrDecoration, Color> colorRef =
            AccessTools.FieldRefAccess<scrDecoration, Color>("rendererColor");

        private static readonly HashSet<SpriteRenderer> hidden = new HashSet<SpriteRenderer>();
        private static readonly Dictionary<Shader, bool> shaderOk = new Dictionary<Shader, bool>();
        internal static int Count { get { return hidden.Count; } }
        internal static int Peak;
        internal static readonly HashSet<string> SkippedShaders = new HashSet<string>();

        internal static void Install(Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(scrVisualDecoration), "ApplyColor");
                h.Patch(m, postfix: new HarmonyMethod(typeof(InvisibleSkip), nameof(After)));
                Main.Entry.Logger.Log("[투명 장식] 설치");
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[투명 장식] 설치 실패: " + ex.Message); }
        }

        public static void After(scrVisualDecoration __instance)
        {
            if (!Enabled) return;
            try
            {
                var r = rendererRef(__instance);
                if (r == null) return;
                bool zero = colorRef(__instance).a <= 0f;
                if (zero)
                {
                    if (r.forceRenderingOff || !AlphaMeansVisibility(r)) return;
                    r.forceRenderingOff = true;
                    hidden.Add(r);
                    if (hidden.Count > Peak) Peak = hidden.Count;
                }
                else if (r.forceRenderingOff && hidden.Remove(r))
                {
                    r.forceRenderingOff = false;
                }
            }
            catch { }
        }

        private static bool AlphaMeansVisibility(SpriteRenderer r)
        {
            var mat = r.sharedMaterial;
            var sh = mat != null ? mat.shader : null;
            if (sh == null) return false;
            bool ok;
            if (shaderOk.TryGetValue(sh, out ok)) return ok;
            string n = sh.name;
            ok = n.StartsWith("Sprites/", StringComparison.Ordinal)
              || n.StartsWith("Hidden/BlendModes/", StringComparison.Ordinal)
              || n == "Legacy Shaders/Particles/Additive";   // 블렌드 장식 빠르게 그리기가 바꿔 끼운 재질
            shaderOk[sh] = ok;
            if (!ok) SkippedShaders.Add(n);
            return ok;
        }

        // 끄거나 모드를 내릴 때, 그리고 맵을 새로 열 때 원래대로 돌려놓는다.
        internal static void RestoreAll()
        {
            foreach (var r in hidden)
                if (r != null) r.forceRenderingOff = false;
            hidden.Clear();
        }

        internal static void Uninstall() { RestoreAll(); }

        internal static string Summary()
        {
            // 사라진 장식(맵이 바뀜)은 목록에서 뺀다
            hidden.RemoveWhere(r => r == null);
            string s = "지금 안 그리는 투명 장식 " + hidden.Count + "개, 곡 중 최대 " + Peak + "개";
            if (SkippedShaders.Count > 0) s += " | 알파를 믿을 수 없어 건너뛴 셰이더: " + string.Join(", ", SkippedShaders);
            return s;
        }

        internal static void ResetPeak() { hidden.RemoveWhere(r => r == null); Peak = hidden.Count; }
    }
}
