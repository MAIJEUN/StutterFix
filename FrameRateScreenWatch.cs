using System;
using System.Diagnostics;
using HarmonyLib;

namespace StutterFix
{
    // 박자마다 오던 75ms 끊김의 유력한 원인.
    //
    // 맵의 "커스텀 프레임레이트" 연출은 화면을 일정 간격으로만 갱신한다.
    // 그 간격마다 scrCamera.LateUpdate -> CheckUpdateFrameRateScreen -> UpdateCustomFrameRateScreen 이
    // 카메라 3개를 Camera.Render 로 수동으로 통째로 다시 그리고 Graphics.Blit 한다.
    //
    // 지금까지의 단서와 전부 맞는다.
    //   1초 간격 반복 / 메모리 변화 0 / GPU는 6~7ms로 한가(PresentMon)
    //   그리는 도중 스크립트 콜백은 다 짧음 / 시간은 전부 메인 카메라 Camera.Render 안
    //   (수동 렌더 명령이 렌더 스레드에 쌓이고, 프레임 끝의 메인 카메라가 그것을 기다린다)
    //
    // 이 함수가 불린 프레임이 끊긴 프레임과 겹치는지 확인한다.
    public static class FrameRateScreenWatch
    {
        internal static int CallsThisFrame;
        internal static double MsThisFrame;
        internal static int TotalCalls;

        internal static void Install(Harmony harmony)
        {
            try
            {
                var cam = AccessTools.TypeByName("scrCamera");
                var m = cam == null ? null : AccessTools.Method(cam, "UpdateCustomFrameRateScreen");
                if (m == null) { Main.Entry.Logger.Error("UpdateCustomFrameRateScreen 없음"); return; }
                harmony.Patch(m,
                    prefix: new HarmonyMethod(typeof(FrameRateScreenWatch), nameof(Pre)),
                    postfix: new HarmonyMethod(typeof(FrameRateScreenWatch), nameof(Post)));
                Main.Entry.Logger.Log("frame-rate screen watch installed");
            }
            catch (Exception ex) { Main.Entry.Logger.Error("frame-rate screen watch 실패: " + ex.Message); }
        }

        public static void Pre(out long __state) { __state = Stopwatch.GetTimestamp(); }

        public static void Post(long __state)
        {
            CallsThisFrame++;
            TotalCalls++;
            MsThisFrame += (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
        }

        internal static string Info()
        {
            return CallsThisFrame == 0
                ? "프레임레이트 연출 없음"
                : string.Format("프레임레이트 연출 수동렌더 {0}회 {1:F1}ms (누적 {2}회)", CallsThisFrame, MsThisFrame, TotalCalls);
        }

        internal static void Reset() { CallsThisFrame = 0; MsThisFrame = 0; }
    }
}
