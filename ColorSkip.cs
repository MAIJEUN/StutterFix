using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // scrFloor.ColorFloor 는 무거운 맵에서 초당 18,000번 넘게 호출된다.
    // (색 변경 이벤트 1회가 타일 545개를 칠하고, 그 이벤트가 초당 30회 이상 발생)
    //
    // 같은 타일에 완전히 같은 인자로 다시 칠하는 호출은 결과가 같으므로 건너뛴다.
    // 실제로 중복이 얼마나 되는지 세어보고 효과를 판단하기 위한 기능이다.
    public static class ColorSkip
    {
        internal static bool Enabled;
        internal static bool CountOnly = true;   // true면 세기만 하고 건너뛰지는 않는다
        internal static long Total, Redundant;

        private static readonly Dictionary<int, int> lastHash = new Dictionary<int, int>();
        private static Harmony harmony;
        private static bool patched;

        internal static void Install()
        {
            if (patched) return;
            try
            {
                harmony = new Harmony("StutterFix.ColorSkip");
                var type = AccessTools.TypeByName("scrFloor");
                foreach (var m in type.GetMethods(AccessTools.all))
                {
                    if (m.Name != "ColorFloor" || m.ContainsGenericParameters) continue;
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(ColorSkip), nameof(Prefix)));
                    patched = true;
                }
                Main.Entry.Logger.Log("color skip patched: " + patched);
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("color skip patch failed: " + ex.Message);
            }
        }

        public static bool Prefix(object __instance, object[] __args)
        {
            if (!Enabled) return true;

            int h = 17;
            for (int i = 0; i < __args.Length; i++)
            {
                var a = __args[i];
                h = h * 31 + (a == null ? 0 : a.GetHashCode());
            }

            int id = ((UnityEngine.Object)__instance).GetInstanceID();
            Total++;

            int prev;
            if (lastHash.TryGetValue(id, out prev) && prev == h)
            {
                Redundant++;
                if (!CountOnly) return false;   // 원래 함수를 실행하지 않는다
                return true;
            }

            lastHash[id] = h;
            return true;
        }

        internal static void Reset()
        {
            lastHash.Clear();
            Total = Redundant = 0;
        }

        internal static string Status
        {
            get
            {
                if (Total == 0) return "(호출 없음)";
                return $"전체 {Total:N0}회 중 중복 {Redundant:N0}회 ({100.0 * Redundant / Total:F1}%)" +
                       (CountOnly ? " — 세기만 함" : " — 건너뛰는 중");
            }
        }
    }
}
