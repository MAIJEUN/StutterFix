using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace StutterFix
{
    // 모드 UI(설정 창, 실시간 모니터) 위를 누를 때 뒤의 게임이 같이 반응하지 않게 한다.
    //
    // 예전에는 EventSystem 을 잠깐 껐는데, 끄는 동안 EventSystem.current 가 비어서 게임 코드
    // (에디터 마우스 처리 scnEditor.HandleMouseActions, 플레이어 입력 scrPlayer.ValidInputWasTriggered)가
    // 매 프레임 NullReferenceException 을 냈다. 모니터 아이콘 위에 마우스가 있기만 해도 한 판에 2만 7천 번,
    // 그 로그를 쓰는 비용이 그대로 끊김이 됐다.
    //
    // 지금은 EventSystem 을 건드리지 않는다. 모드 UI 가 있는 자리에 보이지 않는 uGUI 판(투명 Image)을
    // 맨 위 캔버스에 깐다. 게임은 "마우스가 UI 위에 있다"고 보고 뒤의 타일/버튼을 누르지 않는다.
    // IMGUI 로 그리는 모드 UI 는 이 판과 상관없이 마우스를 받는다.
    internal static class UiInputBlock
    {
        private static GameObject root;
        private static readonly Dictionary<object, RectTransform> blocks = new Dictionary<object, RectTransform>();

        // guiRect: 화면 픽셀 좌표(왼쪽 위가 0,0 인 IMGUI 기준). 빈 사각형이면 치운다.
        internal static void Place(object owner, Rect guiRect)
        {
            if (guiRect.width <= 0 || guiRect.height <= 0) { Clear(owner); return; }
            try
            {
                RectTransform rt;
                if (!blocks.TryGetValue(owner, out rt) || rt == null)
                {
                    EnsureRoot();
                    var go = new GameObject("Block", typeof(RectTransform));
                    go.transform.SetParent(root.transform, false);
                    var img = go.AddComponent<Image>();
                    img.color = new Color(0, 0, 0, 0);
                    img.raycastTarget = true;
                    img.canvasRenderer.cullTransparentMesh = true;   // 그리지는 않는다
                    rt = (RectTransform)go.transform;
                    rt.anchorMin = rt.anchorMax = rt.pivot = Vector2.zero;
                    blocks[owner] = rt;
                }
                if (!rt.gameObject.activeSelf) rt.gameObject.SetActive(true);
                // uGUI 는 왼쪽 아래가 0,0 이다
                rt.anchoredPosition = new Vector2(guiRect.x, Screen.height - guiRect.yMax);
                rt.sizeDelta = new Vector2(guiRect.width, guiRect.height);
            }
            catch { }
        }

        internal static void Clear(object owner)
        {
            RectTransform rt;
            if (blocks.TryGetValue(owner, out rt) && rt != null && rt.gameObject.activeSelf) rt.gameObject.SetActive(false);
        }

        internal static void Remove(object owner)
        {
            RectTransform rt;
            if (!blocks.TryGetValue(owner, out rt)) return;
            blocks.Remove(owner);
            if (rt != null) Object.Destroy(rt.gameObject);
            if (blocks.Count == 0 && root != null) { Object.Destroy(root); root = null; }
        }

        private static void EnsureRoot()
        {
            if (root != null) return;
            root = new GameObject("StutterFix.InputBlock");
            Object.DontDestroyOnLoad(root);
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 32000;   // 게임 UI 보다 위
            root.AddComponent<GraphicRaycaster>();
        }
    }
}
