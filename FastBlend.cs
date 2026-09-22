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

        // (개발자용) 같은 장면을 원래 방식과 복사 없는 방식으로 한 장씩 찍어 비교한다. 게임을 일시정지한 채로 쓴다.
        // %TEMP%\StutterFix-blend\ 에 원래.png, 새방식.png, 차이.png(차이를 8배로 키움)를 남기고 로그에 수치를 적는다.
        internal static void Compare(MonoBehaviour host)
        {
            if (host != null) host.StartCoroutine(CompareRun());
        }

        private static System.Collections.IEnumerator CompareRun()
        {
            bool was = On;
            yield return new WaitForEndOfFrame();
            var a = ScreenCapture.CaptureScreenshotAsTexture();
            Toggle();
            yield return null; yield return null;
            yield return new WaitForEndOfFrame();
            var b = ScreenCapture.CaptureScreenshotAsTexture();
            Toggle();
            try
            {
                var orig = was ? b : a;
                var fast = was ? a : b;
                var po = orig.GetPixels32(); var pf = fast.GetPixels32();
                int n = Mathf.Min(po.Length, pf.Length), over2 = 0, over8 = 0, max = 0;
                long sum = 0, brightO = 0, brightF = 0;
                var diff = new Color32[n];
                for (int i = 0; i < n; i++)
                {
                    int dr = pf[i].r - po[i].r, dg = pf[i].g - po[i].g, db = pf[i].b - po[i].b;
                    int d = Mathf.Max(Mathf.Abs(dr), Mathf.Max(Mathf.Abs(dg), Mathf.Abs(db)));
                    sum += d; if (d > max) max = d; if (d > 2) over2++; if (d > 8) over8++;
                    brightO += po[i].r + po[i].g + po[i].b; brightF += pf[i].r + pf[i].g + pf[i].b;
                    byte v = (byte)Mathf.Min(255, d * 8);
                    diff[i] = new Color32(v, v, v, 255);
                }
                string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StutterFix-blend");
                System.IO.Directory.CreateDirectory(dir);
                var dt = new Texture2D(orig.width, orig.height);
                dt.SetPixels32(diff); dt.Apply();
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "원래.png"), orig.EncodeToPNG());
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "새방식.png"), fast.EncodeToPNG());
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "차이.png"), dt.EncodeToPNG());
                Object.Destroy(dt);
                Main.Entry.Logger.Log(string.Format("[블렌드 비교] {0}x{1} | 평균 차이 {2:F3}/255, 최대 {3}/255 | 2 넘게 다른 픽셀 {4:F2}%, 8 넘게 {5:F2}% | 전체 밝기 원래 {6:F2} 새 {7:F2} | {8}",
                    orig.width, orig.height, (double)sum / n, max, 100.0 * over2 / n, 100.0 * over8 / n,
                    (double)brightO / n / 3, (double)brightF / n / 3, dir));
            }
            catch (System.Exception ex) { Main.Entry.Logger.Log("[블렌드 비교] 실패: " + ex.Message); }
            Object.Destroy(a); Object.Destroy(b);
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
