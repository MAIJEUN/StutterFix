using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace StutterFix
{
    // 순간 끊김의 원인: 한 박자에 맵 이벤트 수백 개가 동시에 터져 한 프레임에 몰린다.
    // A/B 실험에서 이벤트 적용을 전부 막으면 최악 프레임이 38.9ms -> 30.3ms (-22%) 였다.
    // 평균 FPS는 거의 그대로였으므로, 총량이 아니라 "한 프레임에 몰리는 것"이 문제다.
    //
    // 그래서 없애지 않고 나눈다. 한 프레임에 정해진 개수만 처리하고 나머지는 다음 프레임으로 넘긴다.
    // 이벤트 순서는 큐로 유지하므로 결과는 같고, 몇 프레임(수십 ms) 늦어질 뿐이다.
    public static class EventSpread
    {
        internal static bool Enabled;
        internal static int Budget = 60;          // 한 프레임에 처리할 이벤트 수
        internal static long Spread, Immediate;
        internal static int QueueLength { get { return queue.Count; } }

        private struct Job
        {
            public object Target;
            public MethodBase Method;
        }

        private static readonly Queue<Job> queue = new Queue<Job>();
        private static int usedThisFrame;
        private static bool running;
        private static Harmony harmony;
        private static bool patched;

        internal static void Install()
        {
            if (patched) return;
            try
            {
                harmony = new Harmony("StutterFix.EventSpread");
                var type = AccessTools.TypeByName("ffxPlusBase");
                foreach (var m in type.GetMethods(AccessTools.all))
                {
                    if (m.Name != "StartEffect" || m.ContainsGenericParameters || m.IsAbstract) continue;
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(EventSpread), nameof(Prefix)));
                }
                patched = true;
                Main.Entry.Logger.Log("event spread patched");
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("event spread patch failed: " + ex.Message);
            }
        }

        public static bool Prefix(object __instance, MethodBase __originalMethod)
        {
            if (!Enabled || running) return true;

            if (usedThisFrame < Budget)
            {
                usedThisFrame++;
                Immediate++;
                return true;                  // 예산 안이면 지금 처리한다
            }

            queue.Enqueue(new Job { Target = __instance, Method = __originalMethod });
            Spread++;
            return false;                     // 예산을 넘으면 다음 프레임으로 넘긴다
        }

        internal static void Tick(float dt)
        {
            usedThisFrame = 0;
            if (!Enabled || queue.Count == 0) return;

            // 밀린 것이 너무 쌓이면 연출이 눈에 띄게 늦어지므로, 밀릴수록 조금씩 더 처리한다.
            int budget = Budget;
            if (queue.Count > 500) budget = Budget * 3;
            else if (queue.Count > 200) budget = Budget * 2;

            running = true;
            try
            {
                for (int i = 0; i < budget && queue.Count > 0; i++)
                {
                    var job = queue.Dequeue();
                    if (job.Target == null) continue;
                    try { job.Method.Invoke(job.Target, null); }
                    catch { }
                    usedThisFrame++;
                }
            }
            finally { running = false; }
        }

        internal static void ResetStats() { Spread = Immediate = 0; queue.Clear(); }

        internal static string Status
        {
            get
            {
                long t = Spread + Immediate;
                if (t == 0) return "(호출 없음)";
                return "분산 " + Spread.ToString("N0") + " / 즉시 " + Immediate.ToString("N0") +
                       " (" + (100.0 * Spread / t).ToString("F0") + "% 분산), 대기 " + queue.Count;
            }
        }
    }
}
