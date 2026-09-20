using System;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 그리는 쪽에서 막히는 끊김을 들여다본다.
    //
    // 28~33초의 끊김은 성격이 다르다. 게임 코드는 2ms뿐이고
    // PostLateUpdate/FinishFrameRendering 이 70ms다. 즉 화면을 그리다가 막힌다.
    // 게다가 32.7초, 33.7초처럼 1초 간격으로 반복된다.
    //
    // 그릴 때 비싼 것은 대개 셋 중 하나다.
    //   1) 카메라를 줌아웃해서 한 번에 그릴 것이 많아짐
    //   2) 화면 전체를 다시 그리는 효과(필터)가 여러 겹 켜짐
    //   3) 블렌드 모드를 쓰는 물체가 많아짐 (각각 화면을 한 번씩 더 읽는다)
    //
    // 셋 다 숫자로 볼 수 있으므로 끊긴 순간의 값을 같이 남긴다.
    public static class RenderWatch
    {
        private static Camera cam;
        internal static int BlendModeCount;
        private static int blendThisFrame;

        internal static void Install(Harmony harmony)
        {
            try
            {
                var type = AccessTools.TypeByName("BlendModeEffect");
                if (type == null) return;
                var update = AccessTools.Method(type, "Update");
                if (update == null) return;

                // 켜져 있는 것만 Update 가 불리므로, 프레임당 호출 수가 곧 화면에 걸린 개수다.
                harmony.Patch(update, prefix: new HarmonyMethod(typeof(RenderWatch), nameof(CountBlend)));
                Main.Entry.Logger.Log("render watch installed");
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("render watch 실패: " + ex.Message);
            }
        }

        public static void CountBlend()
        {
            blendThisFrame++;
        }

        internal static void EndFrame()
        {
            BlendModeCount = blendThisFrame;
            blendThisFrame = 0;
        }

        internal static string Info()
        {
            try
            {
                if (cam == null) cam = Camera.main;
                if (cam == null) return "카메라 없음";

                int fx = 0;
                foreach (var b in cam.GetComponents<Behaviour>())
                {
                    if (b == null || !b.enabled) continue;
                    if (b is Camera) continue;
                    fx++;
                }

                return string.Format("카메라 크기 {0:F1}, 카메라 효과 {1}개, 블렌드 물체 {2}개",
                    cam.orthographicSize, fx, BlendModeCount);
            }
            catch { return "?"; }
        }
    }
}
