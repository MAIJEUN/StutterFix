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
        internal static bool Suppress;   // 개발자용 검증이 카메라를 직접 그리는 동안 콜백을 멈춘다
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
        private static HalfRenderRunner runner;
        private static int parity;

        internal static void Install(Harmony h)
        {
            try
            {
                var lu = AccessTools.Method(typeof(scrCamera), "LateUpdate");
                if (lu != null) h.Patch(lu, postfix: new HarmonyMethod(typeof(HalfRender), nameof(LatePostfix)) { priority = Priority.Last });
                if (!hooked) { Camera.onPreCull += PreCull; hooked = true; }
                if (runner == null) { var go = new GameObject("StutterFix.HalfRender"); UnityEngine.Object.DontDestroyOnLoad(go); go.hideFlags = HideFlags.HideAndDontSave; runner = go.AddComponent<HalfRenderRunner>(); }
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[저사양] 반만 그리기 설치 실패: " + ex.Message); }
        }

        internal static void Shutdown()
        {
            Restore();
            if (hooked) { Camera.onPreCull -= PreCull; hooked = false; }
            if (runner != null) { UnityEngine.Object.Destroy(runner.gameObject); runner = null; }
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

        // 되돌리기: 사각형이 아직 우리가 옮겨 둔 그대로일 때만 원래 값으로. 그사이 게임이 바꿨으면(플레이 시작 때 크기를 새로 정함 등)
        // 게임 값을 그대로 둔다. 예전에는 지난 프레임에 기억한 값으로 무조건 덮어써서, 게임이 새로 정한 크기를 옛 크기로 되돌려
        // 사각형이 안 보이게 됐고(화면 전체가 검게), 게임은 텍스처를 다시 만들 때만 크기를 정하므로 장면을 다시 불러올 때까지 남았다.
        private static Vector3 mPos, mScale; private static Quaternion mRot;   // 우리가 넣은 값
        private static void ResetQuad()
        {
            if (quadMoved && quadT != null && quadT.localPosition == mPos && quadT.localScale == mScale && quadT.localRotation == mRot)
            { quadT.localPosition = qPos; quadT.localRotation = qRot; quadT.localScale = qScale; }
            quadMoved = false;
        }

        // ── 그리는 순서 ──
        // 게임의 OverlayCam(화면에 내보내는 카메라)은 깊이가 세 카메라보다 앞이라 먼저 그려진다. 즉 원래 게임도 "한 프레임 전에 그린
        // camRT" 를 보여 준다. 그래서 보정 기준은 "원래 게임이라면 이번 프레임에 보여 줬을 카메라" 다:
        //   OverlayCam 이 먼저면 한 프레임 전 카메라, 세 카메라가 먼저면 지금 카메라. 그림이 원래 게임과 같은 나이면 옮기지 않는다.
        // 순서는 진짜로 그리는 프레임에서 알아낸다(본 카메라를 그릴 때 OverlayCam 이 이번 프레임에 이미 그렸는가).
        private static int contentFrame = -10, overlayFrame = -10;
        private static bool overlayFirst = true;
        private static Vector3 lastPos; private static float lastRot, lastSize; private static bool haveLast;
        internal static long ReallySkipped, NotSkipped;   // 건너뛰기로 한 프레임에 본 카메라가 실제로 안 그렸는가 (검증)

        private static void PreCull(Camera cam)
        {
            if (!Enabled || Suppress) return;
            try
            {
                var sc = scrCamera.instance;
                if (sc == null) return;
                int f = Time.frameCount;
                if (cam == camRef(sc))
                {
                    // 본 카메라가 그린다: 이 그림의 카메라 상태를 기록
                    overlayFirst = overlayFrame == f;
                    if (skipping) NotSkipped++;
                    var q = quadRef(sc); quadT = q != null ? q.transform : null;
                    var t = cam.transform;
                    pos0 = t.position; rot0 = t.eulerAngles.z; size0 = cam.orthographicSize;
                    haveState = cam.orthographic && size0 > 0f;
                    contentFrame = f;
                }
                else if (cam == ovRef(sc))
                {
                    overlayFrame = f;
                    if (quadT == null || !haveState) return;
                    // 원래 게임이 이번 프레임에 보여 줬을 그림의 나이: OverlayCam 이 먼저면 한 프레임 전, 아니면 이번 프레임
                    int expected = overlayFirst ? f - 1 : f;
                    if (contentFrame >= expected || !haveLast && overlayFirst) { ResetQuad(); return; }
                    if (overlayFirst) ApplyTo(sc, lastPos, lastRot, lastSize);
                    else { var t = camRef(sc).transform; ApplyTo(sc, t.position, t.eulerAngles.z, camRef(sc).orthographicSize); }
                }
            }
            catch { }
        }

        internal static bool SkippingNow { get { return skipping && haveState && quadT != null; } }
        internal static void ResetNow() { ResetQuad(); }
        // (개발자용 검증) 지금 카메라 기준으로 옮긴다: 보정 계산식이 맞는지 "지금 새로 그린 진짜 화면" 과 비교하려고
        internal static void ApplyNow(scrCamera sc) { ApplyNow(sc, Variant); }
        internal static void ApplyNow(scrCamera sc, int v)
        {
            var main = camRef(sc);
            if (main != null) ApplyVariant(sc, main.transform.position, main.transform.eulerAngles.z, main.orthographicSize, v);
        }
        // 옛 그림(pos0, rot0, size0 카메라로 그린 것)을 목표 카메라(pos1, rot1, size1)에서 본 것처럼 사각형을 옮긴다
        private static void ApplyTo(scrCamera sc, Vector3 pos1, float rot1, float size1) { ApplyVariant(sc, pos1, rot1, size1, Variant); }
        // 공식 변형 (개발자용 검증이 16가지를 다 그려 보고 진짜 화면에 가장 가까운 것을 찾는다):
        //   bit0 회전 합치는 순서(0: qRot*E, 1: E*qRot), bit1 회전 방향 반대, bit2 가로 이동 반대, bit3 세로 이동 반대
        internal static int Variant = 0;   // 검증 결과 0 (16가지 중 평균 오차 가장 작음)
        internal static long BigJumps;
        internal static Vector3 LastShiftPx; internal static float LastDRot, LastK;
        internal static void ApplyVariant(scrCamera sc, Vector3 pos1, float rot1, float size1, int v)
        {
            try
            {
                var cam = ovRef(sc);
                if (cam == null || quadT == null || size1 <= 0f) return;
                float k = size0 / size1;
                float dRot = Mathf.DeltaAngle(rot1, rot0);   // θ0 - θ1
                Vector2 d = new Vector2(pos0.x - pos1.x, pos0.y - pos1.y) / size1;   // (pos0 - pos1)/S1
                float r = -rot1 * Mathf.Deg2Rad;
                Vector2 shift = new Vector2(d.x * Mathf.Cos(r) - d.y * Mathf.Sin(r), d.x * Mathf.Sin(r) + d.y * Mathf.Cos(r));   // R(-θ1)·d
                float H = cam.orthographicSize;   // OverlayCam 세로 절반 = 사각형 세로 절반
                // 원래 값은 옮기기 바로 직전의 지금 값으로 기억한다(게임이 그사이 바꾼 값을 그대로 받음)
                if (!quadMoved) { qPos = quadT.localPosition; qRot = quadT.localRotation; qScale = quadT.localScale; }
                // 카메라가 한 프레임에 크게 튀면(연출 전환 등) 옮기지 않는다. 검증: 105x88px 이동 + 7.3도 회전 + 6% 확대 장면에서
                // 옮긴 것은 모든 공식이 오차 37/255, 안 옮긴 것은 3/255 (화면에 붙은 장식처럼 카메라를 따라가는 것은 옮기면 어긋남).
                float movePx = shift.magnitude * Screen.height / 2f;
                if (movePx > 48f || Mathf.Abs(dRot) > 3f || Mathf.Abs(k - 1f) > 0.03f) { BigJumps++; ResetQuad(); return; }
                float dr = (v & 2) != 0 ? -dRot : dRot;
                float sx = (v & 4) != 0 ? -shift.x : shift.x, sy = (v & 8) != 0 ? -shift.y : shift.y;
                LastShiftPx = new Vector3(shift.x * Screen.height / 2f, shift.y * Screen.height / 2f, 0f); LastDRot = dRot; LastK = k;
                quadT.localPosition = mPos = qPos + new Vector3(sx * H, sy * H, 0f);
                quadT.localRotation = mRot = (v & 1) != 0 ? Quaternion.Euler(0f, 0f, dr) * qRot : qRot * Quaternion.Euler(0f, 0f, dr);
                quadT.localScale = mScale = new Vector3(qScale.x * k, qScale.y * k, qScale.z);
                quadMoved = true;
            }
            catch { }
        }

        // 한 프레임 그리기가 전부 끝난 뒤(WaitForEndOfFrame): 이번 프레임 본 카메라 상태를 "한 프레임 전" 으로 기억하고, 끈 카메라를 켠다.
        // OverlayCam 이 먼저 그려지므로 그 직후에 켜면 이번 프레임에 세 카메라가 그냥 그려질 수 있다. 다음 프레임 게임 스크립트가
        // Camera.main 을 찾기 전에는 켜져 있어야 하므로 여기서 켠다.
        internal static void EndOfFrame()
        {
            if (!Enabled) return;
            try
            {
                var sc = scrCamera.instance;
                if (sc == null) return;
                var main = camRef(sc);
                if (main != null) { var t = main.transform; lastPos = t.position; lastRot = t.eulerAngles.z; lastSize = main.orthographicSize; haveLast = true; }
                if (skipping) { if (contentFrame != Time.frameCount) ReallySkipped++; EnableBack(sc); }
            }
            catch { }
        }

        internal static string Summary()
        {
            if (Rendered + Skipped == 0) return "";
            return string.Format(", 반만 그리기: 그린 프레임 {0}, 건너뛴 프레임 {1} (실제로 안 그린 것 {2}, 건너뛰려다 그려진 것 {3}), 그리는 순서 {4}, 카메라가 크게 튀어 안 옮긴 것 {5}", Rendered, Skipped, ReallySkipped, NotSkipped, overlayFirst ? "화면 카메라 먼저" : "세 카메라 먼저", BigJumps);
        }
    }
}

namespace StutterFix
{
    // 매 프레임 그리기가 모두 끝난 직후에 HalfRender.EndOfFrame 을 부른다
    internal class HalfRenderRunner : MonoBehaviour
    {
        private readonly WaitForEndOfFrame wait = new WaitForEndOfFrame();
        private System.Collections.IEnumerator Start()
        {
            while (true)
            {
                yield return wait;
                HalfRender.EndOfFrame();
            }
        }
    }
}
