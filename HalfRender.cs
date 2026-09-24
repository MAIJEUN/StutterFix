using System;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // (저사양 실험) 반만 그리고, 사이 프레임은 카메라만 옮기기.
    // 플레이 중 게임은 카메라 3개(정적 배경, 움직이는 배경, 본 화면)를 텍스처(camRT)에 그린 뒤 OverlayCam 이 사각형(quad)으로
    // 화면에 낸다(scnGame.Play 가 항상 SetupRTCam(true)). 두 프레임에 한 번만 세 카메라를 그리고, 사이 프레임에는 세 카메라를 꺼서
    // 지난 그림을 그대로 두되, 그사이 본 카메라가 움직인 만큼 사각형을 밀고·돌리고·키워 보여 준다(VR 의 재투영과 같은 생각).
    // 직교 카메라: 화면 좌표 u = R(-θ)(p - pos)/S (S = 세로 절반). 옛 그림의 u0 가 새 화면에서 놓일 자리
    //   u1 = R(θ0-θ1)·u0·(S0/S1) + R(-θ1)(pos0 - pos1)/S1.
    // 지연을 늘리지 않는다(앞 프레임을 기다리지 않음). 대가: 행성·장식·필터는 절반 속도로 갱신, 정적 배경 그림은 사이 프레임에
    // 카메라를 따라 조금 밀림, 빠르게 움직이면 가장자리가 잠깐 빔. 그래픽카드가 한계인 컴퓨터용.
    // 게임의 "사용자 지정 프레임레이트" 연출(enableCustomFPS)이 켜져 있으면 물러난다.
    internal static class HalfRender
    {
        internal static bool Enabled;
        internal static long Rendered, Skipped;
        private static readonly AccessTools.FieldRef<scrCamera, Camera> camRef = AccessTools.FieldRefAccess<scrCamera, Camera>("camobj");
        private static readonly AccessTools.FieldRef<scrCamera, Camera> bgRef = AccessTools.FieldRefAccess<scrCamera, Camera>("BGcam");
        private static readonly AccessTools.FieldRef<scrCamera, Camera> bgsRef = AccessTools.FieldRefAccess<scrCamera, Camera>("Bgcamstatic");
        private static readonly AccessTools.FieldRef<scrCamera, Camera> ovRef = AccessTools.FieldRefAccess<scrCamera, Camera>("Overlaycam");
        private static readonly AccessTools.FieldRef<scrCamera, GameObject> quadRef = AccessTools.FieldRefAccess<scrCamera, GameObject>("quad");
        private static readonly AccessTools.FieldRef<scrCamera, bool> customRef = AccessTools.FieldRefAccess<scrCamera, bool>("enableCustomFPS");
        private static readonly AccessTools.FieldRef<scrCamera, bool> useRTRef = AccessTools.FieldRefAccess<scrCamera, bool>("useRTCam");

        private static bool skipping;                 // 이번 프레임은 세 카메라를 안 그림
        private static bool offMain, offBg, offBgs;  // 우리가 끈 카메라 (다시 켤 것만 기억)
        private static bool haveState;
        private static Vector3 pos0; private static float size0, rot0;   // 마지막으로 그린 프레임의 본 카메라
        private static Vector3 qPos; private static Quaternion qRot; private static Vector3 qScale; private static bool quadMoved;
        private static Transform quadT;
        private static bool hooked;
        private static int parity;

        internal static void Install(Harmony h)
        {
            try
            {
                var lu = AccessTools.Method(typeof(scrCamera), "LateUpdate");
                if (lu != null) h.Patch(lu, postfix: new HarmonyMethod(typeof(HalfRender), nameof(LatePostfix)) { priority = Priority.Last });
                if (!hooked) { Camera.onPreCull += PreCull; Camera.onPostRender += PostRender; hooked = true; }
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[저사양] 반만 그리기 설치 실패: " + ex.Message); }
        }

        internal static void Shutdown()
        {
            Restore();
            if (hooked) { Camera.onPreCull -= PreCull; Camera.onPostRender -= PostRender; hooked = false; }
        }

        // 프레임 끝(scrCamera.LateUpdate 뒤): 이번 프레임을 그릴지 정하고 카메라를 켜고 끈다
        public static void LatePostfix(scrCamera __instance)
        {
            try
            {
                bool active = Enabled && Hitch.Playing && useRTRef(__instance) && !customRef(__instance);
                if (!active) { Restore(); return; }
                var main = camRef(__instance);
                if (main == null) { Restore(); return; }
                parity ^= 1;
                bool skip = parity == 1 && haveState;
                if (skip && !skipping)
                {
                    var bg = bgRef(__instance); var bgs = bgsRef(__instance);
                    offMain = main.enabled; if (offMain) main.enabled = false;
                    offBg = bg != null && bg.enabled; if (offBg) bg.enabled = false;
                    offBgs = bgs != null && bgs.enabled; if (offBgs) bgs.enabled = false;
                    skipping = true;
                }
                else if (!skip && skipping) EnableBack(__instance);
                if (skip) Skipped++; else Rendered++;
            }
            catch (Exception ex) { Enabled = false; Restore(); Main.Entry.Logger.Log("[저사양] 반만 그리기 오류로 끔: " + ex.Message); }
        }

        private static void EnableBack(scrCamera sc)
        {
            if (offMain) { var c = camRef(sc); if (c != null) c.enabled = true; }
            if (offBg) { var c = bgRef(sc); if (c != null) c.enabled = true; }
            if (offBgs) { var c = bgsRef(sc); if (c != null) c.enabled = true; }
            offMain = offBg = offBgs = false;
            skipping = false;
        }

        private static void Restore()
        {
            try
            {
                var sc = scrCamera.instance;
                if (skipping && sc != null) EnableBack(sc);
                skipping = false;
                ResetQuad();
                haveState = false;
            }
            catch { }
        }

        private static void ResetQuad()
        {
            if (quadMoved && quadT != null) { quadT.localPosition = qPos; quadT.localRotation = qRot; quadT.localScale = qScale; }
            quadMoved = false;
        }

        // 카메라가 그리기 직전: 본 카메라를 그리면 그 상태를 기록, OverlayCam 을 그릴 때 사이 프레임이면 사각형을 옮긴다
        private static void PreCull(Camera cam)
        {
            if (!Enabled) return;
            try
            {
                var sc = scrCamera.instance;
                if (sc == null) return;
                if (cam == camRef(sc))
                {
                    // 진짜로 그리는 프레임: 사각형을 원래 자리로, 본 카메라 상태 기록
                    var q = quadRef(sc); quadT = q != null ? q.transform : null;
                    if (quadT != null)
                    {
                        if (quadMoved) ResetQuad();
                        qPos = quadT.localPosition; qRot = quadT.localRotation; qScale = quadT.localScale;
                    }
                    var t = cam.transform;
                    pos0 = t.position; rot0 = t.eulerAngles.z; size0 = cam.orthographicSize;
                    haveState = cam.orthographic && size0 > 0f;
                }
                else if (skipping && haveState && cam == ovRef(sc) && quadT != null)
                {
                    var main = camRef(sc);
                    if (main == null) return;
                    var t = main.transform;
                    Vector3 pos1 = t.position; float rot1 = t.eulerAngles.z, size1 = main.orthographicSize;
                    if (size1 <= 0f) return;
                    float k = size0 / size1;
                    float dRot = Mathf.DeltaAngle(rot1, rot0);   // θ0 - θ1
                    Vector2 d = new Vector2(pos0.x - pos1.x, pos0.y - pos1.y) / size1;   // (pos0 - pos1)/S1
                    float r = -rot1 * Mathf.Deg2Rad;
                    Vector2 shift = new Vector2(d.x * Mathf.Cos(r) - d.y * Mathf.Sin(r), d.x * Mathf.Sin(r) + d.y * Mathf.Cos(r));   // R(-θ1)·d
                    float H = cam.orthographicSize;   // OverlayCam 세로 절반 = 사각형 세로 절반
                    quadT.localPosition = qPos + new Vector3(shift.x * H, shift.y * H, 0f);
                    quadT.localRotation = qRot * Quaternion.Euler(0f, 0f, dRot);
                    quadT.localScale = new Vector3(qScale.x * k, qScale.y * k, qScale.z);
                    quadMoved = true;
                }
            }
            catch { }
        }

        // OverlayCam 까지 다 그린 직후: 끈 카메라를 바로 다시 켠다. 다음 프레임의 게임 스크립트가 Camera.main 등을 찾을 때
        // 꺼져 있으면 안 되므로, 카메라가 꺼져 있는 시간은 "이번 프레임 그리기" 동안뿐이다.
        private static void PostRender(Camera cam)
        {
            if (!skipping) return;
            try
            {
                var sc = scrCamera.instance;
                if (sc != null && cam == ovRef(sc)) EnableBack(sc);
            }
            catch { }
        }

        internal static string Summary()
        {
            if (Rendered + Skipped == 0) return "";
            return string.Format(", 반만 그리기: 그린 프레임 {0}, 건너뛴 프레임 {1}", Rendered, Skipped);
        }
    }
}
