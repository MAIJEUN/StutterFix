using System;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // (개발자용) 저사양 화면 기능 자동 검증. 사람이 눈으로 보고 판단하지 않아도 되게, 같은 순간을 두 방식으로 그려 비교한다.
    //   해상도 낮추기: 지금 상태를 원래 해상도로 따로 그린 것 vs 낮춘 해상도로 그려 사각형으로 늘린 것(OverlayCam 결과).
    //     작게 줄여 평균 픽셀 차이를 재고, 한두 픽셀 밀어 가며 가장 잘 맞는 자리를 찾는다(밀림·뒤집힘·잘림이 있으면 (0,0) 이 아님).
    //   반만 그리기: 건너뛰는 프레임에서 진짜 화면을 몰래 그리고, 옮겨 보여 주는 화면과 옮기지 않은 화면이 각각 얼마나 다른지 잰다.
    //     옮긴 쪽이 작아야 계산이 맞다.
    // 그리는 도중에는 다른 카메라를 그릴 수 없으므로 그리기 전(scrCamera.LateUpdate 뒤)에 한다. 15초마다 한 번.
    internal static class RenderVerify
    {
        private static float next;
        private static int turn;
        internal static long ScaleN, HalfN, HalfBetter, VarN;
        internal static readonly double[] VarSum = new double[16];
        internal static double ScaleDiffSum, HalfFixSum, HalfRawSum;
        internal static int ScaleMisaligned;
        private const int W = 192, H = 80;
        private static Texture2D readTex;
        private static readonly AccessTools.FieldRef<scrCamera, Camera> camRef = AccessTools.FieldRefAccess<scrCamera, Camera>("camobj");
        private static readonly AccessTools.FieldRef<scrCamera, Camera> bgRef = AccessTools.FieldRefAccess<scrCamera, Camera>("BGcam");
        private static readonly AccessTools.FieldRef<scrCamera, Camera> bgsRef = AccessTools.FieldRefAccess<scrCamera, Camera>("Bgcamstatic");
        private static readonly AccessTools.FieldRef<scrCamera, Camera> ovRef = AccessTools.FieldRefAccess<scrCamera, Camera>("Overlaycam");
        private static readonly AccessTools.FieldRef<scrCamera, RenderTexture> rtRef = AccessTools.FieldRefAccess<scrCamera, RenderTexture>("camRT");
        private static readonly AccessTools.FieldRef<scrCamera, GameObject> quadRef = AccessTools.FieldRefAccess<scrCamera, GameObject>("quad");
        private static readonly AccessTools.FieldRef<scrCamera, MeshRenderer> quadMeshRef = AccessTools.FieldRefAccess<scrCamera, MeshRenderer>("camQuadMesh");

        internal static void Install(Harmony h)
        {
            if (!Edition.Dev) return;
            var lu = AccessTools.Method(typeof(scrCamera), "LateUpdate");
            if (lu != null) h.Patch(lu, postfix: new HarmonyMethod(typeof(RenderVerify), nameof(LatePostfix)) { priority = Priority.Last });   // HalfRender 결정 뒤 (같은 우선순위면 먼저 등록된 HalfRender 가 먼저. -1 은 Harmony 에서 "지정 안 함" 이라 쓰면 안 됨)
        }

        public static void LatePostfix(scrCamera __instance)
        {
            if (!Hitch.Playing || Time.realtimeSinceStartup < next) return;
            // 두 기능이 다 켜져 있으면 번갈아 검증한다. 반만 그리기 검증은 건너뛰는 프레임에서만, 해상도 검증은 그리는 프레임에서만.
            bool wantHalf = HalfRender.Enabled && (turn % 2 == 0 || LowEnd.EffectivePct >= 100);
            bool half = wantHalf && HalfRender.SkippingNow;
            bool scale = !wantHalf && LowEnd.EffectivePct < 100 && !HalfRender.SkippingNow;
            if (!half && !scale) return;
            next = Time.realtimeSinceStartup + 10f; turn++;
            HalfRender.Suppress = true;
            try { if (half) { Fsr.Suppress = true; CheckHalf(__instance); } else CheckScale(__instance); }   // 반만 그리기 검증은 진짜 화면과 같은 보통 늘리기로
            catch (Exception ex) { Main.Entry.Logger.Log("[저사양 검증] 실패: " + ex.Message); }
            finally { HalfRender.Suppress = false; Fsr.Suppress = false; }
        }

        // 세 카메라를 target 에 그린다 (꺼져 있어도 Render 는 된다)
        private static void RenderScene(scrCamera sc, RenderTexture target)
        {
            foreach (var c in new[] { bgsRef(sc), bgRef(sc), camRef(sc) })
            {
                if (c == null) continue;
                var old = c.targetTexture;
                c.targetTexture = target;
                c.Render();
                c.targetTexture = old;
            }
        }
        private static RenderTexture OverlayShot(scrCamera sc)
        {
            var ov = ovRef(sc);
            var rt = RenderTexture.GetTemporary(Screen.width, Screen.height, 24);
            var old = ov.targetTexture;
            ov.targetTexture = rt; ov.Render(); ov.targetTexture = old;
            return rt;
        }
        private static Color32[] Small(Texture src)
        {
            var s = RenderTexture.GetTemporary(W, H, 0);
            Graphics.Blit(src, s);
            if (readTex == null) readTex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            RenderTexture.active = s;
            readTex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
            readTex.Apply(false);
            RenderTexture.active = prev;
            RenderTexture.ReleaseTemporary(s);
            return readTex.GetPixels32();
        }
        // 평균 차이(0~255)와 가장 잘 맞는 어긋남
        private static double Diff(Color32[] a, Color32[] b, int dx, int dy)
        {
            long sum = 0; int n = 0;
            for (int y = 2; y < H - 2; y++)
                for (int x = 2; x < W - 2; x++)
                {
                    var p = a[y * W + x]; var q = b[(y + dy) * W + x + dx];
                    sum += Math.Abs(p.r - q.r) + Math.Abs(p.g - q.g) + Math.Abs(p.b - q.b); n += 3;
                }
            return (double)sum / n;
        }

        private static void CheckScale(scrCamera sc)
        {
            var camRT = rtRef(sc);
            if (camRT == null) return;
            // 낮춘 해상도: 게임 텍스처에 지금 상태를 그려 OverlayCam 으로 늘린 결과 (게임도 이번 프레임에 다시 그리므로 해가 없다)
            RenderScene(sc, camRT);
            var shown = OverlayShot(sc);
            // 원래 해상도: 같은 상태를 화면 크기로
            var full = RenderTexture.GetTemporary(Screen.width, Screen.height, 24);
            RenderScene(sc, full);
            // FSR 이 켜져 있으면 같은 순간을 여러 방식으로 늘려 화면 크기 그대로 원래 해상도와 비교한다 (작게 줄이면 차이가 뭉개진다)
            //   전체 차이, 테두리(원래 화면에서 밝기가 크게 바뀌는 픽셀)만의 차이, 선명도(이웃 밝기 차이 평균)가 원래의 몇 %인지
            string fsrText = Fsr.Active ? CompareUpscalers(sc, full) : "";
            var a = Small(full); var b = Small(shown);
            RenderTexture.ReleaseTemporary(full); RenderTexture.ReleaseTemporary(shown);
            double best = double.MaxValue; int bx = 0, by = 0;
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) { double d = Diff(a, b, dx, dy); if (d < best) { best = d; bx = dx; by = dy; } }
            ScaleN++; ScaleDiffSum += best; if (bx != 0 || by != 0) ScaleMisaligned++;
            Main.Entry.Logger.Log(string.Format("[저사양 검증] 해상도 {0}% ({1}x{2}): 원래 해상도와 평균 차이 {3:F2}/255, 가장 잘 맞는 어긋남 ({4},{5}){6}",
                LowEnd.EffectivePct, camRT.width, camRT.height, best, bx, by,
                fsrText));
        }
        internal static long FsrN;
        private static readonly string[] VarName = { "보통", "EASU만", "RCAS0", "RCAS0.2", "RCAS0.5", "RCAS1", "RCAS2" };
        private static readonly float[] VarSharp = { -9f, -1f, 0f, 0.2f, 0.5f, 1f, 2f };
        internal static readonly double[] VAll = new double[7], VEdge = new double[7], VSharp = new double[7];
        private static string CompareUpscalers(scrCamera sc, RenderTexture full)
        {
            var F = Read(full);
            int W = full.width, H = full.height;
            // 원래 화면에서 밝기(초록)가 크게 바뀌는 픽셀 = 테두리
            var edge = new bool[F.Length];
            double nativeGrad = 0; long gn = 0;
            for (int y = 1; y < H; y++) for (int x = 1; x < W; x++)
            {
                int i = y * W + x;
                int g = Math.Abs(F[i].g - F[i - 1].g) + Math.Abs(F[i].g - F[i - W].g);
                nativeGrad += g; gn++;
                if (g > 32) edge[i] = true;
            }
            nativeGrad /= Math.Max(1, gn);
            var sb = new System.Text.StringBuilder(" | 늘리기 비교 (전체/테두리 차이, 선명도%):");
            try
            {
                for (int v = 0; v < VarName.Length; v++)
                {
                    RenderTexture shot;
                    if (v == 0) { Fsr.Suppress = true; try { shot = OverlayShot(sc); } finally { Fsr.Suppress = false; } }
                    else { Fsr.TestSharp = VarSharp[v]; try { shot = OverlayShot(sc); } finally { Fsr.TestSharp = -2f; } }
                    var P = Read(shot);
                    RenderTexture.ReleaseTemporary(shot);
                    long all = 0, en = 0, es = 0; double grad = 0;
                    for (int y = 1; y < H; y++) for (int x = 1; x < W; x++)
                    {
                        int i = y * W + x; var p = F[i]; var q = P[i];
                        int d = Math.Abs(p.r - q.r) + Math.Abs(p.g - q.g) + Math.Abs(p.b - q.b);
                        all += d;
                        if (edge[i]) { es += d; en++; }
                        grad += Math.Abs(q.g - P[i - 1].g) + Math.Abs(q.g - P[i - W].g);
                    }
                    double dAll = all / (3.0 * Math.Max(1, gn)), dEdge = es / (3.0 * Math.Max(1, en)), sharp = nativeGrad > 0 ? 100.0 * grad / Math.Max(1, gn) / nativeGrad : 0;
                    VAll[v] += dAll; VEdge[v] += dEdge; VSharp[v] += sharp;
                    sb.AppendFormat(" {0} {1:F2}/{2:F1}/{3:F0}%", VarName[v], dAll, dEdge, sharp);
                }
                FsrN++;
            }
            catch (Exception ex) { sb.Append(" 실패: " + ex.Message); }
            return sb.ToString();
        }
        private static Texture2D fullTex;
        private static Color32[] Read(RenderTexture rt)
        {
            if (fullTex == null || fullTex.width != rt.width || fullTex.height != rt.height) fullTex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            fullTex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            fullTex.Apply(false);
            RenderTexture.active = prev;
            return fullTex.GetPixels32();
        }
        private static double FullDiff(Color32[] a, Color32[] b)
        {
            long sum = 0; long n = 0;
            for (int i = 0; i < a.Length; i += 1) { var p = a[i]; var q = b[i]; sum += Math.Abs(p.r - q.r) + Math.Abs(p.g - q.g) + Math.Abs(p.b - q.b); n += 3; }
            return (double)sum / n;
        }

        private static void CheckHalf(scrCamera sc)
        {
            var camRT = rtRef(sc); var q = quadRef(sc); var qm = quadMeshRef(sc);
            if (camRT == null || q == null || qm == null) return;
            var t = q.transform;
            Vector3 p0 = t.localPosition, s0 = t.localScale; Quaternion r0 = t.localRotation;
            Vector3 movePx; float dRot, zoom;
            HalfRender.ApplyNow(sc);
            movePx = HalfRender.LastShiftPx; dRot = HalfRender.LastDRot; zoom = HalfRender.LastK;
            var fixedShot = OverlayShot(sc);
            HalfRender.ResetNow();
            var rawShot = OverlayShot(sc);   // 옮기지 않은 옛 그림
            // 진짜 화면: 지금 상태를 새로 그려 같은 사각형(원래 자리)으로
            var truthRT = RenderTexture.GetTemporary(camRT.width, camRT.height, 24);
            RenderScene(sc, truthRT);
            var mat = qm.material; var oldTex = mat.mainTexture;
            mat.mainTexture = truthRT;
            var truthShot = OverlayShot(sc);
            mat.mainTexture = oldTex;
            RenderTexture.ReleaseTemporary(truthRT);
            t.localPosition = p0; t.localScale = s0; t.localRotation = r0;
            var T = Small(truthShot); var F = Small(fixedShot); var R = Small(rawShot);
            RenderTexture.ReleaseTemporary(truthShot); RenderTexture.ReleaseTemporary(fixedShot); RenderTexture.ReleaseTemporary(rawShot);
            double df = Diff(F, T, 0, 0), dr = Diff(R, T, 0, 0);
            HalfN++; HalfFixSum += df; HalfRawSum += dr; if (df <= dr) HalfBetter++;
            Main.Entry.Logger.Log(string.Format("[저사양 검증] 반만 그리기: 진짜 화면과 평균 차이 - 옮긴 것 {0:F2}/255, 안 옮긴 것 {1:F2}/255 | 카메라 이동 ({2:F0},{3:F0})px 회전 {4:F1}도 확대 {5:F3}",
                df, dr, movePx.x, movePx.y, dRot, zoom));
        }

        internal static string Summary()
        {
            string s = "";
            if (ScaleN > 0) s += string.Format(" | 해상도 검증 {0}번: 평균 차이 {1:F2}/255, 어긋남 {2}번", ScaleN, ScaleDiffSum / ScaleN, ScaleMisaligned);
            if (FsrN > 0)
            {
                s += " | 늘리기 비교 " + FsrN + "번 평균 (전체/테두리 차이, 선명도%):";
                for (int v = 0; v < VarName.Length; v++) s += string.Format(" {0} {1:F2}/{2:F1}/{3:F0}%", VarName[v], VAll[v] / FsrN, VEdge[v] / FsrN, VSharp[v] / FsrN);
            }
            if (HalfN > 0) { s += string.Format(" | 반만 그리기 검증 {0}번: 옮긴 것 평균 차이 {1:F2}, 안 옮긴 것 {2:F2}, 옮긴 쪽이 나은 경우 {3}번 ", HalfN, HalfFixSum / HalfN, HalfRawSum / HalfN, HalfBetter); }
            return s;
        }
    }
}
