using System.Collections.Generic;
using BlendModes;
using UnityEngine;

namespace StutterFix
{
    // 블렌드 장식을 화면 복사 없이 그리기.
    //
    // 게임의 블렌드 장식(BlendModeEffect)은 모드와 상관없이 전부 "화면을 복사해서 섞는" 셰이더(…/Grab)를 쓴다.
    // 장식 하나를 그릴 때마다 화면 전체(3440x1440)를 복사하므로, 화면에 1500개가 보이면 그 복사만으로 90ms 가 된다.
    // 그런데 몇몇 모드는 복사 없이 그래픽카드의 기본 섞기만으로 같은 값이 나온다 (a = 장식 알파, s = 장식 색, d = 화면):
    //   Linear Dodge(더하기) : d + a*s                      = Blend SrcAlpha One          (Particles/Additive)
    //   Screen              : d + a*s*(1-d)                = Blend One OneMinusSrcColor  (Particles/Additive (Soft), s 에 a 를 곱해 둠)
    //   Multiply            : d * lerp(1, s, a)            = Blend Zero SrcColor         (Particles/Multiply)
    // 이 셋은 게임에 들어 있는 기본 셰이더로 재질만 바꿔 끼운다. Difference/Overlay 등은 복사가 꼭 필요해서 그대로 둔다.
    // (한 번만 복사하는 UnifiedGrab 은 빠르지만, 뒤에 그려진 것을 못 봐서 모양이 달라졌다)
    internal static class FastBlend
    {
        private static Material add, screen, multiply;
        private static bool looked;
        private static readonly Dictionary<BlendModeEffect, Renderer> swapped = new Dictionary<BlendModeEffect, Renderer>();
        internal static bool On { get { return swapped.Count > 0; } }

        private static Material Make(string shader)
        {
            var s = Shader.Find(shader);
            if (s == null) return null;
            var m = new Material(s) { hideFlags = HideFlags.HideAndDontSave, name = "StutterFix." + shader };
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", new Color(0.5f, 0.5f, 0.5f, 0.5f));   // 셰이더가 2배를 곱하므로 0.5 = 원래 색
            return m;
        }

        private static void Look()
        {
            if (looked) return;
            looked = true;
            add = Make("Legacy Shaders/Particles/Additive");
            screen = Make("Legacy Shaders/Particles/Additive (Soft)");
            multiply = Make("Legacy Shaders/Particles/Multiply");
            Main.Entry.Logger.Log(string.Format("[블렌드] 대신 쓸 셰이더: 더하기 {0}, 스크린 {1}, 곱하기 {2}", add != null, screen != null, multiply != null));
        }

        private static Material For(BlendMode mode)
        {
            switch (mode)
            {
                case BlendMode.LinearDodge: return add;
                case BlendMode.Screen: return screen;
                case BlendMode.Multiply: return multiply;
                default: return null;
            }
        }

        internal static void Toggle()
        {
            try
            {
                if (On) { Restore(); return; }
                Look();
                int skipped = 0;
                var cams = new List<string>();
                foreach (var c in Camera.allCameras) cams.Add(c.name + (c.allowHDR ? "(HDR)" : ""));
                foreach (var b in Object.FindObjectsByType<BlendModeEffect>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (b == null || !b.enabled) continue;
                    var mat = For(b.BlendMode);
                    var r = b.GetComponent<Renderer>();
                    if (mat == null || r == null || b.MaskMode != MaskMode.Disabled) { skipped++; continue; }
                    b.enabled = false;            // 먼저 끈다 (끌 때 에셋이 원래 재질로 되돌린다)
                    r.sharedMaterial = mat;
                    swapped[b] = r;
                }
                Main.Entry.Logger.Log(string.Format("[블렌드] 복사 없이 그리기 켬: {0}개 바꿈, {1}개 그대로 | 카메라 {2}", swapped.Count, skipped, string.Join(", ", cams)));
            }
            catch (System.Exception ex) { Main.Entry.Logger.Log("[블렌드] 복사 없이 그리기 실패: " + ex); }
        }

        private static void Restore()
        {
            int n = 0;
            foreach (var kv in swapped)
            {
                if (kv.Key == null) continue;
                kv.Key.enabled = true;
                kv.Key.SetMaterialDirty();
                n++;
            }
            swapped.Clear();
            Main.Entry.Logger.Log("[블렌드] 복사 없이 그리기 끔: " + n + "개 원래대로");
        }
    }
}
