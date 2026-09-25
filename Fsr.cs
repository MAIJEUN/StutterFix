using System;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 저사양: 작게 그린 게임 화면을 AMD FSR 1 로 늘린다 (EASU: 가장자리를 살려 늘리기 → RCAS: 선명도 보정). MIT 라이선스, fsr/license.txt.
    // 셰이더는 유니티 에디터로 만든 번들(fsr/ 폴더에서 빌드)을 DLL 안에 넣어 두고 처음 쓸 때 불러온다.
    // 게임 화면 흐름: 세 카메라 → camRT(작게) → Overlaycam 이 사각형(quad, 텍스처 camRT)으로 화면에 늘려 그림.
    // Overlaycam 을 그리기 직전(onPreCull)에 camRT → 화면 크기 두 장으로 EASU·RCAS 를 돌리고 사각형 텍스처를 결과로 바꿔 끼운다.
    // 이번 프레임에 카메라가 안 그렸으면(반만 그리기가 건너뛴 프레임) 지난 결과를 그대로 쓴다.
    internal static class Fsr
    {
        internal static bool Enabled;
        internal static float Sharpness = 0.2f;   // RCAS 단계 (0 = 가장 선명, 클수록 약함). AMD 기본 0.2
        internal static bool Ready, Failed;
        internal static bool Suppress;              // 검증기가 보통 늘리기와 비교할 때
        internal static long Frames, Reused;
        private static Material easu, rcas;
        private static RenderTexture mid, outRT;
        private static int content, done = -1;
        private static bool hooked;
        private static Material quadMat;
        private static RenderTexture lastCamRT;
        private static readonly AccessTools.FieldRef<scrCamera, Camera> camRef = AccessTools.FieldRefAccess<scrCamera, Camera>("camobj");
        private static readonly AccessTools.FieldRef<scrCamera, Camera> ovRef = AccessTools.FieldRefAccess<scrCamera, Camera>("Overlaycam");
        private static readonly AccessTools.FieldRef<scrCamera, RenderTexture> rtRef = AccessTools.FieldRefAccess<scrCamera, RenderTexture>("camRT");
        private static readonly AccessTools.FieldRef<scrCamera, MeshRenderer> quadMeshRef = AccessTools.FieldRefAccess<scrCamera, MeshRenderer>("camQuadMesh");

        internal static bool Active { get { return Enabled && Ready && LowEnd.EffectivePct < 100; } }

        internal static void Apply()
        {
            if (Enabled && !Ready && !Failed) Load();
            // 자동 해상도로 배율이 오가도 되게 켜져 있으면 늘 걸어 두고, 100% 일 때는 PreCull 이 바로 돌아간다
            if (Enabled && Ready) { if (!hooked) { Camera.onPreCull += PreCull; hooked = true; } }
            else { RestoreQuad(); Unhook(); }
        }

        private static void Unhook()
        {
            if (hooked) { Camera.onPreCull -= PreCull; hooked = false; }
            FreeRTs();
        }
        private static void FreeRTs()
        {
            if (mid != null) { mid.Release(); UnityEngine.Object.Destroy(mid); mid = null; }
            if (outRT != null) { outRT.Release(); UnityEngine.Object.Destroy(outRT); outRT = null; }
            done = -1;
        }

        internal static void Shutdown() { RestoreQuad(); Unhook(); }

        private static void Load()
        {
            try
            {
                byte[] data;
                using (var s = typeof(Fsr).Assembly.GetManifestResourceStream("StutterFix.fsr"))
                {
                    if (s == null) throw new Exception("번들이 DLL 에 없음");
                    data = new byte[s.Length];
                    int n = 0; while (n < data.Length) { int r = s.Read(data, n, data.Length - n); if (r <= 0) break; n += r; }
                }
                var ab = AssetBundle.LoadFromMemory(data);
                if (ab == null) throw new Exception("번들을 열 수 없음");
                var e = ab.LoadAsset<Shader>("Assets/Fsr/FsrEasu.shader");
                var r2 = ab.LoadAsset<Shader>("Assets/Fsr/FsrRcas.shader");
                ab.Unload(false);
                if (e == null || r2 == null) throw new Exception("셰이더가 번들에 없음");
                if (!e.isSupported || !r2.isSupported) throw new Exception("이 그래픽카드/그래픽 방식에서 지원 안 됨 (" + SystemInfo.graphicsDeviceType + ")");
                easu = new Material(e) { hideFlags = HideFlags.HideAndDontSave };
                rcas = new Material(r2) { hideFlags = HideFlags.HideAndDontSave };
                Ready = true;
                Main.Entry.Logger.Log("[저사양] FSR 1 셰이더 불러옴 (" + SystemInfo.graphicsDeviceType + ")");
            }
            catch (Exception ex) { Failed = true; Main.Entry.Logger.Log("[저사양] FSR 1 사용 불가: " + ex.Message); }
        }

        private static void PreCull(Camera c)
        {
            try
            {
                var sc = scrCamera.instance;
                if (sc == null) return;
                if (c == camRef(sc)) { content++; return; }
                if (c != ovRef(sc)) return;
                var camRT = rtRef(sc); var qm = quadMeshRef(sc);
                if (camRT == null || qm == null) return;
                if (quadMat == null || lastCamRT != camRT) { quadMat = qm.material; lastCamRT = camRT; }
                var tex = quadMat.mainTexture;
                bool ours = outRT != null && tex == outRT;
                if (tex != camRT && !ours) return;   // 게임이 다른 텍스처를 쓰는 중(사용자 지정 FPS 등)이면 손대지 않음
                if (Suppress || !Active) { if (ours) quadMat.mainTexture = camRT; return; }
                int W = Screen.width, H = Screen.height;
                if (camRT.width >= W && camRT.height >= H) { if (ours) quadMat.mainTexture = camRT; return; }
                if (outRT == null || outRT.width != W || outRT.height != H || outRT.format != camRT.format)
                {
                    if (ours) quadMat.mainTexture = camRT;
                    FreeRTs(); ours = false;
                    mid = new RenderTexture(W, H, 0, camRT.format) { filterMode = FilterMode.Point, hideFlags = HideFlags.HideAndDontSave };
                    outRT = new RenderTexture(W, H, 0, camRT.format) { filterMode = FilterMode.Bilinear, hideFlags = HideFlags.HideAndDontSave };
                    mid.Create(); outRT.Create();
                }
                if (done == content && ours) { Reused++; return; }
                float iw = camRT.width, ih = camRT.height;
                easu.SetVector("_Con0", new Vector4(iw / W, ih / H, 0.5f * iw / W - 0.5f, 0.5f * ih / H - 0.5f));
                easu.SetVector("_Con1", new Vector4(1f / iw, 1f / ih, 1f / iw, -1f / ih));
                easu.SetVector("_Con2", new Vector4(-1f / iw, 2f / ih, 1f / iw, 2f / ih));
                easu.SetVector("_Con3", new Vector4(0f, 4f / ih, 0f, 0f));
                rcas.SetVector("_RcasCon", new Vector4(Mathf.Pow(2f, -Sharpness), 0f, 0f, 0f));
                var prev = RenderTexture.active;
                Graphics.Blit(camRT, mid, easu);
                Graphics.Blit(mid, outRT, rcas);
                RenderTexture.active = prev;
                quadMat.mainTexture = outRT;
                done = content; Frames++;
            }
            catch (Exception ex) { Failed = true; Ready = false; Main.Entry.Logger.Log("[저사양] FSR 1 실패, 끔: " + ex.Message); RestoreQuad(); }
        }

        private static void RestoreQuad()
        {
            try
            {
                if (quadMat != null && outRT != null && quadMat.mainTexture == outRT && lastCamRT != null) quadMat.mainTexture = lastCamRT;
            }
            catch { }
        }

        internal static string Summary()
        {
            if (!Enabled) return "";
            return Failed ? " | FSR 1: 사용 불가" : string.Format(" | FSR 1: {0}프레임 (지난 결과 다시 씀 {1}번)", Frames, Reused);
        }
    }
}
