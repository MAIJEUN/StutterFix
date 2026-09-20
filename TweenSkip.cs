using System;
using DG.Tweening;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 색 변화 애니메이션(TweenColor)이 병목이다.
    //
    // 측정: 무거운 구간에서 scrFloor.ColorFloor 가 초당 24,317회 호출되는데
    //   - TweenColor  1,374 ms/s (14,918회, 1회당 0.092ms)  <- 비용의 98%
    //   - SetColor       13 ms/s (29,025회, 1회당 0.0005ms) <- 180배 빠름
    // 즉 색을 칠하는 것이 아니라 "애니메이션 객체를 만드는 것"이 느리다.
    //
    // 아주 짧은 애니메이션은 눈으로 구분되지 않으므로 최종 색을 즉시 적용하고 객체를 만들지 않는다.
    public static class TweenSkip
    {
        internal static bool Enabled;
        internal static float Threshold = 0.2f;    // 이 길이 이하면 즉시 적용 (초)
        internal static long Skipped, Kept;

        private static Action<scrFloor, Color> setColor;
        private static Harmony harmony;
        private static bool patched;

        internal static void Install()
        {
            if (patched) return;
            try
            {
                var type = typeof(scrFloor);
                var set = AccessTools.Method(type, "SetColor", new[] { typeof(Color) });
                setColor = (Action<scrFloor, Color>)Delegate.CreateDelegate(typeof(Action<scrFloor, Color>), set);

                var tween = AccessTools.Method(type, "TweenColor");
                harmony = new Harmony("StutterFix.TweenSkip");
                harmony.Patch(tween, prefix: new HarmonyMethod(typeof(TweenSkip), nameof(Prefix)));
                patched = true;
                Main.Entry.Logger.Log("tween skip patched");
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("tween skip patch failed: " + ex.Message);
            }
        }

        // TweenColor(Color, float duration, Ease, float) -> Tween
        public static bool Prefix(scrFloor __instance, Color __0, float __1, ref Tween __result)
        {
            if (!Enabled || __1 > Threshold)
            {
                Kept++;
                return true;
            }

            try
            {
                setColor(__instance, __0);
                __result = null;      // 호출부는 반환된 트윈을 보관만 한다
                Skipped++;
                return false;
            }
            catch
            {
                return true;          // 문제가 생기면 원래 동작으로 되돌린다
            }
        }

        internal static void ResetStats() { Skipped = Kept = 0; }

        internal static string Status
        {
            get
            {
                long t = Skipped + Kept;
                if (t == 0) return "(호출 없음)";
                return "즉시적용 " + Skipped.ToString("N0") + " / 애니메이션 유지 " + Kept.ToString("N0") +
                       " (" + (100.0 * Skipped / t).ToString("F0") + "% 생략)";
            }
        }
    }
}
