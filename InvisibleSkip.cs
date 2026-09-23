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
        // 목록 조회를 유니티 객체 비교(가상 호출 두 번) 대신 참조 비교로 한다. 같은 장식이면 같은 C# 객체다.
        private sealed class RefEq : IEqualityComparer<SpriteRenderer>
        {
            internal static readonly RefEq Instance = new RefEq();
            public bool Equals(SpriteRenderer a, SpriteRenderer b) { return ReferenceEquals(a, b); }
            public int GetHashCode(SpriteRenderer o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }

        internal static bool Enabled = true;

        private static readonly AccessTools.FieldRef<scrVisualDecoration, SpriteRenderer> rendererRef =
            AccessTools.FieldRefAccess<scrVisualDecoration, SpriteRenderer>("spriteRenderer");
        private static readonly AccessTools.FieldRef<scrDecoration, Color> colorRef =
            AccessTools.FieldRefAccess<scrDecoration, Color>("rendererColor");

        private static readonly HashSet<SpriteRenderer> hidden = new HashSet<SpriteRenderer>(RefEq.Instance);
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
                InstallLazy(h);
                Main.Entry.Logger.Log("[투명 장식] 설치");
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[투명 장식] 설치 실패: " + ex.Message); }
        }

        // Arche 는 장식 28,835개 중 28,811개가 투명한 채로 깔려 있고, 효과 몰림 프레임에 색·투명도 변경이 수천 개씩 몰린다.
        // 그래서 이 자리는 호출 한 번이 싸야 한다. 대부분은 "투명 -> 투명" 이라 할 일이 없는데, 처음 판에서는 그때도
        // 엔진 값(forceRenderingOff)을 읽었다. 이제는 우리 목록만 보고 끝낸다. 엔진 값은 실제로 바꿀 때만 건드린다.
        private static readonly HashSet<SpriteRenderer> rejected = new HashSet<SpriteRenderer>(RefEq.Instance);   // 알파를 믿을 수 없는 셰이더
        internal static long Calls, Toggles, Ticks;
        internal static double WorstFrameMs;
        private static double frameMs;
        private static int frame = -1;

        public static void After(scrVisualDecoration __instance)
        {
            if (!Enabled) return;
            // 시간 재기 자체가 호출 한 번 비용과 비슷해서(첫 판: 286만 번에 492ms), 64번에 한 번만 재고 64배로 친다.
            bool sample = (++Calls & 63) == 0;
            long t0 = sample ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try { Work(__instance); } catch { }
            if (!sample) return;
            // 이 기능이 효과 몰림 프레임을 늘리지 않는지 보려고, 쓴 시간과 가장 많이 쓴 프레임을 (추정해서) 잰다.
            long d = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 64;
            Ticks += d;
            int f = Time.frameCount;
            if (f != frame) { frame = f; frameMs = 0; }
            frameMs += d * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (frameMs > WorstFrameMs) WorstFrameMs = frameMs;
        }

        private static void Work(scrVisualDecoration inst)
        {
            var r = rendererRef(inst);
            if ((object)r == null) return;
            if (colorRef(inst).a <= 0f)
            {
                if (hidden.Contains(r) || rejected.Contains(r)) return;   // 대부분(98%)이 여기서 끝난다: 투명 -> 투명
                if (!AlphaMeansVisibility(r)) { rejected.Add(r); return; }
                r.forceRenderingOff = true;
                hidden.Add(r);
                Toggles++;
                if (hidden.Count > Peak) Peak = hidden.Count;
            }
            else if (hidden.Remove(r))
            {
                r.forceRenderingOff = false;
                Toggles++;
                ApplyLazy(inst);
            }
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
            ApplyAllLazy();
            foreach (var r in hidden)
                if (r != null) r.forceRenderingOff = false;
            hidden.Clear();
            rejected.Clear();
        }

        internal static void Uninstall() { RestoreAll(); }

        internal static string Summary()
        {
            // 사라진 장식(맵이 바뀜)은 목록에서 뺀다
            hidden.RemoveWhere(r => r == null);
            string s = "지금 안 그리는 투명 장식 " + hidden.Count + "개, 곡 중 최대 " + Peak + "개";
            if (SkippedShaders.Count > 0) s += " | 알파를 믿을 수 없어 건너뛴 셰이더: " + string.Join(", ", SkippedShaders);
            if (Compares > 0) s += " | 픽셀 비교 " + Compares + "번 중 화면이 달랐던 것 " + ComparesDiffer + "번";
            if (LazySkips > 0) s += " | 투명한 장식 위치 반영 미룸 " + LazySkips + "번, 보일 때 반영 " + LazyApplied + "번";
            s += string.Format(" | 색 바뀜 {0}번 확인, 그리기 켜고 끈 것 {1}번, 쓴 시간 약 {2:F1}ms, 가장 많이 쓴 프레임 약 {3:F2}ms (64번에 한 번 재서 추정)",
                Calls, Toggles, Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency, WorstFrameMs);
            return s;
        }

        // ── 투명한 장식은 위치를 보일 때 반영한다 ──
        // Arche 측정: 효과가 시작되는 순간(효과 몰림 프레임) 옮기는 장식의 95%(219,156개 중 207,480개)가 투명했다.
        // 위치 한 번 반영은 값 저장 + 엔진 변환 여러 번(위치, 시차, 회전, 크기)인데, 안 보이는 장식이라 쓸모가 없다.
        // 게임도 투명한 장식은 매 프레임의 위치 갱신(LogicUpdate -> UpdatePosition)을 건너뛴다.
        // 그래서 투명한 장식의 SetPosition 은 값(pivotPosVec, pivotOffsetVec)만 저장하고, 보이게 되는 순간(ApplyColor 에서
        // 그리기 목록에서 빠질 때) 저장된 값으로 SetPosition 을 한 번 불러 원래대로 반영한다.
        // 제외: 히트박스 장식(투명해도 충돌 판정에 위치가 필요), 마스크 장식(투명해도 다른 장식을 가림).
        internal static bool LazyMove = true;
        internal static long LazySkips, LazyApplied;
        private static readonly HashSet<scrDecoration> lazy = new HashSet<scrDecoration>();
        private static readonly AccessTools.FieldRef<scrDecoration, Vector2> pivotPosRef = AccessTools.FieldRefAccess<scrDecoration, Vector2>("pivotPosVec");
        private static readonly AccessTools.FieldRef<scrDecoration, Vector2> pivotOffRef = AccessTools.FieldRefAccess<scrDecoration, Vector2>("pivotOffsetVec");
        private static readonly AccessTools.FieldRef<scrDecoration, scrParallax> parallaxRef = AccessTools.FieldRefAccess<scrDecoration, scrParallax>("parallax");
        private static Func<scrVisualDecoration, bool> isMask;
        private static Action<scrDecoration, Vector2, Vector2> setPosition;

        private static void InstallLazy(Harmony h)
        {
            var set = AccessTools.Method(typeof(scrDecoration), "SetPosition", new[] { typeof(Vector2), typeof(Vector2) });
            isMask = AccessTools.MethodDelegate<Func<scrVisualDecoration, bool>>(AccessTools.Method(typeof(scrVisualDecoration), "isMask"));
            setPosition = AccessTools.MethodDelegate<Action<scrDecoration, Vector2, Vector2>>(set);
            h.Patch(set, prefix: new HarmonyMethod(typeof(InvisibleSkip), nameof(LazyPrefix)) { priority = Priority.First });
            Main.Entry.Logger.Log("[투명 장식] 위치 늦게 반영 설치");
        }

        public static bool LazyPrefix(scrDecoration __instance, Vector2 pivotPos, Vector2 pivotOffset)
        {
            if (!LazyMove || !Enabled || applyingAll || !Hitch.Playing) return true;   // 편집기에서는 선택 테두리가 이 위치를 쓴다
            // 보이는 장식은 필드 하나만 읽고 바로 원래대로 간다 (SetPosition 은 곡 하나에 500만 번 넘게 불린다)
            if (colorRef(__instance).a > 0f) return true;
            var v = __instance as scrVisualDecoration;
            if ((object)v == null || __instance.hitbox != 0) return true;
            var r = rendererRef(v);
            if ((object)r == null || !hidden.Contains(r) || isMask(v)) return true;
            if (parallaxRef(__instance) == null) return true;   // 원래 함수가 이때는 아무것도 안 한다
            pivotPosRef(__instance) = pivotPos;
            pivotOffRef(__instance) = pivotOffset;
            lazy.Add(__instance);
            LazySkips++;
            return false;
        }

        // 보이게 되는 순간 저장해 둔 위치를 반영한다
        private static void ApplyLazy(scrVisualDecoration v)
        {
            if (lazy.Count == 0 || !lazy.Remove(v)) return;
            if (v == null) return;
            LazyApplied++;
            setPosition(v, pivotPosRef(v), pivotOffRef(v));
        }

        // 곡이 끝날 때는 아직 "재생 중" 으로 보여서, 반영하려고 부른 SetPosition 이 도로 미뤄졌다. 반영하는 동안은 막는다.
        private static bool applyingAll;

        internal static void ApplyAllLazy()
        {
            if (lazy.Count == 0) return;
            var list = new List<scrDecoration>(lazy);
            lazy.Clear();
            applyingAll = true;
            try
            {
                foreach (var d in list)
                    if (d != null) { LazyApplied++; setPosition(d, pivotPosRef(d), pivotOffRef(d)); }
            }
            finally { applyingAll = false; }
        }

        internal static bool IsHidden(scrDecoration d)
        {
            var v = d as scrVisualDecoration;
            if ((object)v == null) return false;
            var r = rendererRef(v);
            return (object)r != null && hidden.Contains(r);
        }

        internal static void ResetPeak()
        {
            hidden.RemoveWhere(r => r == null); rejected.RemoveWhere(r => r == null);
            Peak = hidden.Count; Compares = ComparesDiffer = 0;
            Calls = Toggles = Ticks = 0; WorstFrameMs = 0; LazySkips = LazyApplied = 0;
            lazy.RemoveWhere(d => d == null);
        }

        // ── 개발자용: 정말 화면이 같은지 픽셀로 확인 ──
        // 곡 중 20초마다, 같은 프레임을 "투명 장식 뺀 채" 와 "다 그린 채" 로 두 번 그려 비교한다.
        // 알파가 0 이면 어떤 셰이더든 결과에 더해지는 것이 없어야 하므로 차이는 정확히 0 이어야 한다.
        private static float nextCompare;
        private static bool comparing;
        internal static int Compares, ComparesDiffer;

        internal static void DevTick()
        {
            if (!Enabled || comparing || !Hitch.Playing || hidden.Count == 0) return;
            if (Time.realtimeSinceStartup < nextCompare) return;
            nextCompare = Time.realtimeSinceStartup + 20f;
            if (PerfOverlay.Instance != null) PerfOverlay.Instance.StartCoroutine(CompareRun());
        }

        private static System.Collections.IEnumerator CompareRun()
        {
            comparing = true;
            yield return new WaitForEndOfFrame();
            Texture2D a = null, b = null;
            var list = new List<SpriteRenderer>();
            foreach (var r in hidden) if (r != null) list.Add(r);
            try
            {
                a = RenderCams();
                foreach (var r in list) r.forceRenderingOff = false;
                b = RenderCams();
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[투명 비교] 그리기 실패: " + ex.Message); }
            finally { foreach (var r in list) if (r != null) r.forceRenderingOff = true; }
            comparing = false;
            if (a == null || b == null) yield break;
            try
            {
                var pa = a.GetPixels32(); var pb = b.GetPixels32();
                int n = Mathf.Min(pa.Length, pb.Length), differ = 0, max = 0;
                for (int i = 0; i < n; i++)
                {
                    int d = Mathf.Max(Mathf.Abs(pa[i].r - pb[i].r), Mathf.Max(Mathf.Abs(pa[i].g - pb[i].g), Mathf.Abs(pa[i].b - pb[i].b)));
                    if (d > 0) { differ++; if (d > max) max = d; }
                }
                Compares++;
                string extra = "";
                if (differ > 0)
                {
                    ComparesDiffer++;
                    string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "StutterFix-invisible");
                    string stamp = DateTime.Now.ToString("HHmmss");
                    System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "뺀-" + stamp + ".png"), a.EncodeToPNG());
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(dir, "다그림-" + stamp + ".png"), b.EncodeToPNG());
                    extra = " | 저장: " + dir + " (" + stamp + ")";
                }
                Main.Entry.Logger.Log(string.Format("[투명 비교] 뺀 장식 {0}개 | {1}x{2} 중 다른 픽셀 {3}개 (최대 차이 {4}/255){5}",
                    list.Count, a.width, a.height, differ, max, extra));
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[투명 비교] 실패: " + ex.Message); }
            UnityEngine.Object.Destroy(a); UnityEngine.Object.Destroy(b);
        }

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
    }
}
