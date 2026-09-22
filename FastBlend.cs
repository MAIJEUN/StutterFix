using System.Collections.Generic;
using BlendModes;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 블렌드 장식을 화면 복사 없이 그리기.
    //
    // 게임의 블렌드 장식(BlendModeEffect, "Blend Modes" 에셋)은 모드와 상관없이 전부 "화면을 복사해서 섞는" 셰이더
    // (Hidden/BlendModes/SpritesDefault/Grab)를 쓴다. 장식 하나를 그릴 때마다 화면 전체를 복사하므로, 3440x1440 에
    // 1500개가 보이면(CICADA3302 초반) 그 복사만으로 프레임이 90ms 가 됐다.
    // 그런데 몇몇 모드는 복사 없이 그래픽카드의 기본 섞기만으로 같은 값이 나온다 (a = 장식 알파, s = 장식 색, d = 화면):
    //   Linear Dodge(더하기) : d + a*s                      = Blend SrcAlpha One          (Particles/Additive)
    //   Screen              : d + a*s*(1-d)                = Blend One OneMinusSrcColor  (Particles/Additive (Soft))
    //   Multiply            : d * lerp(1, s, a)            = Blend Zero SrcColor         (Particles/Multiply)
    // 게임에 들어 있는 기본 셰이더로 재질만 바꿔 끼운다. 이 게임 빌드에는 더하기용만 들어 있다(스크린/곱하기는 없음).
    // 같은 프레임을 두 방식으로 그려 비교했을 때 세 장면 모두 픽셀 차이 0 이었다.
    // Difference/Overlay 등은 복사가 꼭 필요해서 그대로 둔다. 한 번만 복사하는 UnifiedGrab 은 모양이 달라져서 쓰지 않는다.
    //
    // 바꾸는 법: 블렌드 효과 컴포넌트를 끄고(끌 때 에셋이 원래 재질로 되돌린다) 렌더러에 우리 재질을 끼운다.
    // 게임이 블렌드 모드를 바꾸면(scrVisualDecoration.SetBlendMode) 컴포넌트가 다시 켜지고 재질을 새로 만드는데,
    // 그 순간(SetMaterialProperties)을 잡아 다음 프레임에 다시 판단한다. 모드가 없음이 되면 게임이 알아서 일반 재질로 바꾼다.
    // 건너뛰는 것: 스프라이트가 아닌 것, 스프라이트 마스크를 쓰는 것(우리 셰이더는 마스크를 모름), 에셋 마스크,
    //             점 샘플링인데 이미지가 점 샘플링이 아닌 것(에셋은 셰이더에서 점 샘플링을 하지만 우리 셰이더는 못 한다).
    internal static class FastBlend
    {
        internal static bool Enabled;
        private static bool active;       // 지금 바꿔 끼우는 중인가 (Enabled 가 바뀌면 따라간다)
        private static bool suspended;    // 비교 촬영 중에는 잠깐 멈춘다

        private static Material add, screen, multiply;
        private static bool looked;
        private static readonly HashSet<BlendModeEffect> swapped = new HashSet<BlendModeEffect>();
        private static readonly List<BlendModeEffect> queue = new List<BlendModeEffect>();
        internal static int Count { get { return swapped.Count; } }
        internal static string Status = "";

        internal static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(BlendModeEffect), "SetMaterialProperties"),
                postfix: new HarmonyMethod(typeof(FastBlend), nameof(AfterMaterial)));
        }

        internal static void Uninstall() { if (active) RestoreAll(); active = false; }

        // 에셋이 재질을 새로 정했다: 다음 프레임에 우리 것으로 바꿀 수 있는지 본다
        private static void AfterMaterial(BlendModeEffect __instance)
        {
            if (active && !suspended && __instance != null) queue.Add(__instance);
        }

        internal static void Tick()
        {
            if (Enabled != active)
            {
                active = Enabled;
                if (active) SwapAll(); else RestoreAll();
            }
            if (queue.Count == 0) return;
            if (!active || suspended) { queue.Clear(); return; }
            // 들어온 것을 한 프레임에 다 바꾼다. 한 프레임에 3ms 씩 나눠 본 적이 있는데, 그러면 아직 안 바뀐 장식들이
            // 그동안 화면 복사 방식으로 그려져서(1755개면 한 프레임 90ms) 곡 시작 직후 여러 프레임이 느려졌다.
            // 맵 불러오기/곡 시작 때 한 번에 몰려 오지만 그 순간은 불러오기로 적히므로, 한 번에 끝내는 편이 낫다.
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = 0; i < queue.Count; i++) TrySwap(queue[i]);   // 도중에 더 들어온 것도 같이 처리된다
            queue.Clear();
            ModCost.Add(SettingsWindow.T("블렌드 장식 바꾸기", "Blend swap"), (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        }

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
            if (Edition.Dev) Main.Entry.Logger.Log(string.Format("[블렌드] 대신 쓸 셰이더: 더하기 {0}, 스크린 {1}, 곱하기 {2}", add != null, screen != null, multiply != null));
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

        private static bool IsOurs(Material m) { return m != null && (m == add || m == screen || m == multiply); }

        private static bool TrySwap(BlendModeEffect b)
        {
            try
            {
                if (b == null || !b.enabled) return false;
                Look();
                var r = b.GetComponent<SpriteRenderer>();
                var mat = For(b.BlendMode);
                bool ok = mat != null && r != null && b.MaskMode == MaskMode.Disabled && r.maskInteraction == SpriteMaskInteraction.None;
                if (ok && b.UsePointSampling)
                {
                    var sp = r.sprite;
                    ok = sp != null && sp.texture != null && sp.texture.filterMode == FilterMode.Point;
                }
                if (!ok)
                {
                    // 우리가 바꿨던 장식을 게임이 다른 모드(예: Difference)로 다시 켰다. 에셋은 모드가 바뀌어도 같은 재질의
                    // 설정만 바꾸고 렌더러에 재질을 다시 넣지 않아서, 우리 더하기 재질이 그대로 남아 모양이 달라졌다.
                    // 재질을 새로 만들게 해서(SetMaterialProperties(true)) 에셋이 렌더러에 다시 넣게 한다.
                    if (r != null && IsOurs(r.sharedMaterial)) Reapply(b);
                    swapped.Remove(b);
                    return false;
                }
                b.enabled = false;            // 먼저 끈다 (끌 때 에셋이 원래 재질로 되돌린다)
                r.sharedMaterial = mat;
                swapped.Add(b);
                return true;
            }
            catch { return false; }
        }

        private static readonly System.Reflection.MethodInfo setProps = AccessTools.Method(typeof(BlendModeEffect), "SetMaterialProperties");
        private static void Reapply(BlendModeEffect b)
        {
            try { if (setProps != null) setProps.Invoke(b, new object[] { true }); else b.SetMaterialDirty(); }
            catch { }
        }

        private static void SwapAll()
        {
            int n = 0;
            try
            {
                foreach (var b in Object.FindObjectsByType<BlendModeEffect>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (TrySwap(b)) n++;
            }
            catch { }
            Status = n + "";
            if (Edition.Dev) Main.Entry.Logger.Log("[블렌드] 복사 없이 그리기 켬: " + n + "개 바꿈");
        }

        private static void RestoreAll()
        {
            int n = 0;
            foreach (var b in swapped)
            {
                if (b == null) continue;
                try
                {
                    // 그 사이 게임이 모드를 없음으로 바꿨다면 이미 일반 재질이다. 우리 재질을 쓰고 있을 때만 되돌린다.
                    var r = b.GetComponent<SpriteRenderer>();
                    if (r == null || !IsOurs(r.sharedMaterial)) continue;
                    b.enabled = true;
                    Reapply(b);   // 재질을 새로 만들어 렌더러에 다시 넣게 한다 (SetMaterialDirty 만으로는 안 넣는다)
                    n++;
                }
                catch { }
            }
            swapped.Clear();
            queue.Clear();
            if (Edition.Dev) Main.Entry.Logger.Log("[블렌드] 복사 없이 그리기 끔: " + n + "개 원래대로");
        }

        // ── (개발자용) 비교 촬영 ──────────────────────────────────────
        // 같은 프레임을 원래 방식과 복사 없는 방식으로 두 번 그려 비교한다.
        // %TEMP%\StutterFix-blend\ 에 원래/새방식/차이(8배) png 를 남기고 로그에 수치를 적는다.
        internal static void Compare(MonoBehaviour host)
        {
            if (host != null) host.StartCoroutine(CompareRun());
        }

        // 게임 카메라들을 깊이 순서대로 한 장에 직접 그린다 (화면 UI 는 빠진다).
        private static Texture2D RenderCams()
        {
            int w = Screen.width, h = Screen.height;
            var rt = RenderTexture.GetTemporary(w, h, 24, RenderTextureFormat.ARGB32);
            var cams = new List<Camera>(Camera.allCameras);
            cams.RemoveAll(c => c == null || c.targetTexture != null);
            cams.Sort((x, y) => x.depth.CompareTo(y.depth));
            var prev = RenderTexture.active;
            RenderTexture.active = rt; GL.Clear(true, true, Color.black);
            foreach (var c in cams) { c.targetTexture = rt; c.Render(); c.targetTexture = null; }
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0); tex.Apply();
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(rt);
            return tex;
        }

        private static System.Collections.IEnumerator CompareRun()
        {
            suspended = true;
            if (swapped.Count > 0) { RestoreAll(); yield return null; yield return null; }   // 원래 재질은 다음 Update 에 돌아온다
            yield return new WaitForEndOfFrame();
            Texture2D a = null, b = null;
            try { a = RenderCams(); SwapAll(); b = RenderCams(); }
            catch (System.Exception ex) { Main.Entry.Logger.Log("[블렌드 비교] 그리기 실패: " + ex.Message); }
            if (!active) RestoreAll();
            suspended = false;
            if (a == null || b == null) yield break;
            try
            {
                var po = a.GetPixels32(); var pf = b.GetPixels32();
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
                string stamp = System.DateTime.Now.ToString("HHmmss");
                System.IO.Directory.CreateDirectory(dir);
                var dt = new Texture2D(a.width, a.height);
                dt.SetPixels32(diff); dt.Apply();
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "원래-" + stamp + ".png"), a.EncodeToPNG());
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "새방식-" + stamp + ".png"), b.EncodeToPNG());
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "차이-" + stamp + ".png"), dt.EncodeToPNG());
                Object.Destroy(dt);
                Main.Entry.Logger.Log(string.Format("[블렌드 비교] {0}x{1} | 평균 차이 {2:F3}/255, 최대 {3}/255 | 2 넘게 다른 픽셀 {4:F2}%, 8 넘게 {5:F2}% | 전체 밝기 원래 {6:F2} 새 {7:F2} | {8} ({9})",
                    a.width, a.height, (double)sum / n, max, 100.0 * over2 / n, 100.0 * over8 / n,
                    (double)brightO / n / 3, (double)brightF / n / 3, dir, stamp));
            }
            catch (System.Exception ex) { Main.Entry.Logger.Log("[블렌드 비교] 실패: " + ex.Message); }
            Object.Destroy(a); Object.Destroy(b);
        }
    }
}
