using System.Collections.Generic;

namespace StutterFix
{
    // 모드 UI(설정 창, 실시간 모니터) 위를 누를 때 뒤의 게임 UI 가 같이 눌리지 않게 EventSystem 을 잠깐 끈다.
    //
    // 창마다 따로 끄고 켜면 한쪽이 켠 것을 다른 쪽이 다시 끄는 식으로 꼬인다. 여러 곳의 요청을 모아서
    // 하나라도 막아 달라고 하면 끄고, 아무도 없으면 켠다.
    // 끈 EventSystem 은 직접 들고 있어야 한다. 끄는 순간 EventSystem.current 가 비어서, 예전에는 다시 켤
    // 대상을 못 찾아 창을 닫은 뒤 게임 클릭이 영영 먹통이 됐다.
    internal static class UiInputBlock
    {
        private static readonly HashSet<object> owners = new HashSet<object>();
        private static UnityEngine.EventSystems.EventSystem blocked;

        internal static void Set(object owner, bool block)
        {
            bool changed = block ? owners.Add(owner) : owners.Remove(owner);
            if (!changed) return;
            try
            {
                if (owners.Count > 0)
                {
                    if (blocked != null) return;
                    var es = UnityEngine.EventSystems.EventSystem.current;
                    if (es == null || !es.enabled) return;
                    es.enabled = false;
                    blocked = es;
                }
                else if (blocked != null)
                {
                    var es = blocked;
                    blocked = null;
                    if (es != null) es.enabled = true;   // 씬이 바뀌어 사라졌으면 할 일 없음
                }
            }
            catch { blocked = null; }
        }
    }
}
