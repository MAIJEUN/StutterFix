using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // (개발자용) 필터 이벤트가 언제 무엇을 켜고 껐는지 최근 것만 기억해 둔다.
    // GPU 과부하 끊김이 났을 때 바로 앞 몇 프레임의 필터 변화를 같이 적어, 어떤 필터가 원인인지 좁힌다.
    // (69.4초의 200ms: 필터 셰이더를 미리 데워도 같은 자리에서 144ms 가 남았다)
    internal static class FilterTrace
    {
        private struct Rec { public int Frame; public string What; }
        private static readonly List<Rec> recent = new List<Rec>();
        private static FieldInfo advName, advOn, plusFilter, plusOn;

        internal static void Install(Harmony h)
        {
            var adv = AccessTools.TypeByName("ffxSetFilterAdvancedPlus");
            var plus = AccessTools.TypeByName("ffxSetFilterPlus");
            if (adv != null)
            {
                advName = AccessTools.Field(adv, "filterName"); advOn = AccessTools.Field(adv, "enableFilter");
                h.Patch(AccessTools.Method(adv, "StartEffect"), prefix: new HarmonyMethod(typeof(FilterTrace), nameof(Adv)));
            }
            if (plus != null)
            {
                plusFilter = AccessTools.Field(plus, "filter"); plusOn = AccessTools.Field(plus, "enableFilter");
                h.Patch(AccessTools.Method(plus, "StartEffect"), prefix: new HarmonyMethod(typeof(FilterTrace), nameof(Plus)));
            }
        }

        private static void Adv(object __instance)
        {
            try { Add(advName.GetValue(__instance) + (true.Equals(advOn.GetValue(__instance)) ? " 켬" : " 끔") + "(고급)"); } catch { }
        }

        private static void Plus(object __instance)
        {
            try { Add(plusFilter.GetValue(__instance) + (true.Equals(plusOn.GetValue(__instance)) ? " 켬" : " 끔")); } catch { }
        }

        private static void Add(string what)
        {
            recent.Add(new Rec { Frame = Time.frameCount, What = what });
            if (recent.Count > 400) recent.RemoveRange(0, 200);
        }

        // frame 과 그 앞 back 프레임 동안의 필터 변화 (같은 것은 개수로 묶음)
        internal static string Recent(int frame, int back)
        {
            var count = new Dictionary<string, int>();
            var order = new List<string>();
            for (int i = recent.Count - 1; i >= 0; i--)
            {
                var r = recent[i];
                if (r.Frame > frame) continue;
                if (r.Frame < frame - back) break;
                int c;
                if (count.TryGetValue(r.What, out c)) count[r.What] = c + 1;
                else { count[r.What] = 1; order.Add(r.What); }
            }
            if (order.Count == 0) return "없음";
            var sb = new StringBuilder();
            foreach (var w in order) { if (sb.Length > 0) sb.Append(", "); sb.Append(w); if (count[w] > 1) sb.Append(" ×").Append(count[w]); }
            return sb.ToString();
        }
    }
}
