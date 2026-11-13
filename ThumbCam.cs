using System;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 에디터의 워크숍 썸네일 카메라(WorkshopThumbnailMaker, "Thumbnail Maker") 끄기.
    // 이 카메라는 켜져 있어서 에디터에 있는 동안(곡 중 포함) 매 프레임 512x512 텍스처(thumbnail_rt)에 썸네일용 층(17)을 그린다.
    // 개발자용 측정(FrameScan): 메인 스레드만 프레임당 0.07~0.09ms, 그래픽카드도 따로 한 장 더 그린다.
    // 그 텍스처를 쓰는 곳은 MakeThumbnail 하나뿐이고, 거기서는 cam.Render() 로 직접 한 번 그린 뒤 읽는다(Render 는 꺼진 카메라에도 된다).
    // 화면의 RawImage 나 렌더러 재질 중 그 텍스처를 쓰는 것은 없다(FrameScan 으로 확인: 0개). 그래서 카메라 컴포넌트만 꺼 두면
    // 화면과 저장되는 썸네일은 그대로이고 매 프레임 그리기만 없어진다.
    internal static class ThumbCam
    {
        internal static bool Enabled = true;
        private static Camera cam;

        internal static void Install(Harmony h)
        {
            var t = AccessTools.TypeByName("WorkshopThumbnailMaker");
            var start = t != null ? AccessTools.Method(t, "Start") : null;
            if (start == null) { Main.Entry.Logger.Log("[썸네일 카메라] WorkshopThumbnailMaker.Start 없음 - 건너뜀"); return; }
            h.Patch(start, postfix: new HarmonyMethod(typeof(ThumbCam), nameof(StartPostfix)));
        }

        public static void StartPostfix(Component __instance)
        {
            try { cam = __instance.GetComponent<Camera>(); Apply(); }
            catch (Exception ex) { Main.Entry.Logger.Log("[썸네일 카메라] 실패: " + ex.Message); }
        }

        // 설정이 바뀌었을 때도 부른다
        internal static void Apply()
        {
            if (cam == null) return;
            // 다시 켤 때 그릴 텍스처가 없으면 화면에 직접 그려 게임 화면을 덮는다: 그때는 꺼 둔 채로 둔다
            if (!Enabled && cam.targetTexture == null) { Main.Entry.Logger.Log("[썸네일 카메라] 그릴 텍스처가 없어 다시 켜지 않음"); return; }
            cam.enabled = !Enabled;
        }
    }
}
