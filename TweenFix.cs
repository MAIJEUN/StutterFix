using System;
using System.Reflection;
using HarmonyLib;

namespace StutterFix
{
    // 맵 중간 끊김의 진짜 원인.
    //
    // 측정 (138초 지점, 한 프레임 435ms):
    //   TweenManager.AddActiveTween        384ms (5143회)
    //   TweenManager.ReorganizeActiveTweens 382ms (4981회)   <- 위 함수 안에서 불린다
    //
    // ffxRecolorFloorPlus 는 타일마다 옛 애니메이션을 죽이고 새로 만든다.
    // DOTween 은 애니메이션을 죽이면 목록에 구멍을 내고, 새로 만들 때 그 구멍을 메우려고
    // 목록 전체를 훑는다. 타일 4815개면 4815 x 4815 / 2 = 약 1160만 번이다.
    //
    //   AddActiveTween:  if (_requiresActiveReorganization) ReorganizeActiveTweens();
    //                    _activeTweens[totActiveTweens] = t;   <- 빈자리가 앞에서부터 차 있어야 한다
    //
    // 그런데 DOTween 은 자기 갱신 도중에 죽는 애니메이션을 위해 이미 답을 갖고 있다.
    //
    //   Kill:  if (TweenManager.isUpdateLoop) t.active = false;   // 목록은 그대로 둔다
    //          else TweenManager.Despawn(t);                      // 구멍을 낸다
    //
    // 그래서 효과가 도는 동안만 isUpdateLoop 를 켜 둔다.
    // 죽은 애니메이션은 목록에 남아 표시만 되고, DOTween 이 다음 갱신에서 한 번에 치운다.
    // 구멍이 없으니 재정렬도 없다. 1160만 번이 0번이 된다.
    public static class TweenFix
    {
        internal static bool Enabled = true;
        internal static long Guarded;

        private static FieldInfo isUpdateLoopField;
        private static bool ready;

        internal static void Install(Harmony harmony)
        {
            try
            {
                var tweenManager = AccessTools.TypeByName("DG.Tweening.Core.TweenManager");
                if (tweenManager == null) { Main.Entry.Logger.Error("TweenManager 없음"); return; }

                isUpdateLoopField = AccessTools.Field(tweenManager, "isUpdateLoop");
                if (isUpdateLoopField == null) { Main.Entry.Logger.Error("isUpdateLoop 없음"); return; }

                var vfx = AccessTools.TypeByName("scrVfxPlus");
                if (vfx == null) { Main.Entry.Logger.Error("scrVfxPlus 없음"); return; }

                var update = AccessTools.Method(vfx, "Update");
                if (update == null) { Main.Entry.Logger.Error("scrVfxPlus.Update 없음"); return; }

                // 예외가 나도 반드시 원래대로 돌려놓아야 한다. 켜진 채로 남으면 DOTween 이 망가진다.
                harmony.Patch(update,
                    prefix: new HarmonyMethod(typeof(TweenFix), nameof(Before)),
                    finalizer: new HarmonyMethod(typeof(TweenFix), nameof(After)));

                ready = true;
                Main.Entry.Logger.Log("tween fix installed");
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("tween fix 실패: " + ex.Message);
            }
        }

        // 밀어둔 효과를 다시 실행할 때도 같은 보호가 필요하다.
        internal static bool Begin()
        {
            if (!ready || !Enabled) return false;
            try
            {
                if ((bool)isUpdateLoopField.GetValue(null)) return false;   // 이미 켜져 있으면 건드리지 않는다
                isUpdateLoopField.SetValue(null, true);
                Guarded++;
                return true;
            }
            catch { return false; }
        }

        internal static void End(bool entered)
        {
            if (!entered) return;
            try { isUpdateLoopField.SetValue(null, false); }
            catch { }
        }

        public static void Before(out bool __state)
        {
            __state = Begin();
        }

        public static void After(bool __state)
        {
            End(__state);
        }
    }
}
