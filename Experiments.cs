using System;
using System.Collections.Generic;
using HarmonyLib;

namespace StutterFix
{
    // 측정 도구로는 한계에 부딪혔다. 초당 수만 번 호출되는 함수에 시간 측정을 붙이면
    // 측정 비용이 실제 비용을 덮어버려, 무엇이 범인인지 가릴 수 없다.
    //
    // 그래서 재지 않고 "그 일을 아예 안 하게 막고" FPS가 뛰는지로 판별한다.
    // A/B 테스트(F8)가 0.8초마다 이것을 켰다 껐다 하므로 같은 구간끼리 비교된다.
    public static class Experiments
    {
        public enum Target
        {
            None,
            StartEffect,      // 모든 맵 이벤트 적용을 막는다
            ColorFloor,       // 타일 색칠만 막는다
            BlendMode,        // BlendModeEffect.Update 를 막는다
            FloorUpdate,      // scrFloor.Update 를 막는다
            TrackStyle,       // SetTrackStyle + UpdateAngle 을 막는다
        }

        internal static Target Selected = Target.StartEffect;
        internal static bool Active;              // A/B 테스트가 이 값을 켰다 껐다 한다
        internal static long Blocked;

        private static Harmony harmony;
        private static readonly HashSet<Target> installed = new HashSet<Target>();

        private static readonly Dictionary<Target, string[][]> Methods = new Dictionary<Target, string[][]>
        {
            { Target.StartEffect, new[] { new[] { "ffxPlusBase", "StartEffect" } } },
            { Target.ColorFloor,  new[] { new[] { "scrFloor", "ColorFloor" } } },
            { Target.BlendMode,   new[] { new[] { "BlendModeEffect", "Update" } } },
            { Target.FloorUpdate, new[] { new[] { "scrFloor", "Update" } } },
            { Target.TrackStyle,  new[] { new[] { "scrFloor", "SetTrackStyle" }, new[] { "scrFloor", "UpdateAngle" } } },
        };

        internal static void Install(Target t)
        {
            if (t == Target.None || installed.Contains(t)) return;
            try
            {
                if (harmony == null) harmony = new Harmony("StutterFix.Experiments");

                // 여러 대상이 동시에 설치되면 전부 함께 차단되어 결과를 가릴 수 없다.
                // 한 번에 하나만 남긴다.
                if (installed.Count > 0)
                {
                    harmony.UnpatchAll("StutterFix.Experiments");
                    installed.Clear();
                    Main.Entry.Logger.Log("previous experiments removed");
                }
                foreach (var pair in Methods[t])
                {
                    var type = AccessTools.TypeByName(pair[0]);
                    if (type == null) continue;
                    foreach (var m in type.GetMethods(AccessTools.all))
                    {
                        if (m.Name != pair[1] || m.ContainsGenericParameters || m.IsAbstract) continue;
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(Experiments), nameof(Prefix)));
                    }
                }
                installed.Add(t);
                Main.Entry.Logger.Log("experiment installed: " + t);
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("experiment install failed: " + ex.Message);
            }
        }

        // 선택된 실험이 켜져 있는 동안에는 원래 함수를 실행하지 않는다.
        public static bool Prefix()
        {
            if (!Active) return true;
            Blocked++;
            return false;
        }

        internal static string Status
        {
            get { return "대상 " + Selected + ", 지금 " + (Active ? "차단 중" : "통과") + ", 누적 차단 " + Blocked.ToString("N0"); }
        }
    }
}
