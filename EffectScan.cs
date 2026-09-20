using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace StutterFix
{
    // 어느 효과가 시작될 때 화면이 멈추는지 찍는다.
    //
    // 지금까지 좁혀진 것:
    //   137.8초의 441ms 중 402ms가 Update 단계 -> 그중 398ms가 scrVfxPlus.Update 한 번
    //   scrVfxPlus.Update 는 곡 위치가 되면 예약된 효과를 꺼내 StartEffect 를 부르는 일만 한다
    //   30~45ms짜리 잔펀치도 대부분 같은 함수다
    //
    // 즉 "맵 특정 구간에서 끊긴다"의 정체는 그 구간에서 시작되는 효과다.
    // 효과 종류마다 처음 쓸 때 셰이더나 화면 버퍼를 만드는 비용이 있을 수 있으므로
    // 이름과 걸린 시간, 몇 번째 사용인지까지 남긴다.
    public static class EffectScan
    {
        internal static bool Enabled = true;
        internal static float LogOverMs = 3f;

        internal static int StartedThisFrame;
        internal static double MsThisFrame;

        private static readonly System.Collections.Generic.Dictionary<string, int> useCount
            = new System.Collections.Generic.Dictionary<string, int>();

        internal static void Install(Harmony harmony)
        {
            try
            {
                var baseType = AccessTools.TypeByName("ffxPlusBase");
                if (baseType == null) { Main.Entry.Logger.Error("ffxPlusBase 없음"); return; }

                int count = 0;
                foreach (var t in baseType.Assembly.GetTypes())
                {
                    if (!baseType.IsAssignableFrom(t) || t.ContainsGenericParameters) continue;
                    var m = AccessTools.DeclaredMethod(t, "StartEffect");
                    if (m == null || m.IsAbstract) continue;
                    try
                    {
                        harmony.Patch(m,
                            prefix: new HarmonyMethod(typeof(EffectScan), nameof(Pre)),
                            postfix: new HarmonyMethod(typeof(EffectScan), nameof(Post)));
                        count++;
                    }
                    catch { }
                }
                Main.Entry.Logger.Log("[효과] StartEffect " + count + "개 감쌈");
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("[효과] 설치 실패: " + ex.Message);
            }
        }

        public static void Pre(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void Post(object __instance, long __state)
        {
            if (!Enabled) return;
            double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;

            StartedThisFrame++;
            MsThisFrame += ms;

            string name = __instance != null ? __instance.GetType().Name : "?";
            int n;
            useCount.TryGetValue(name, out n);
            useCount[name] = n + 1;

            // 처음 쓰는 효과인지 알아야 한다. 처음만 느리다면 미리 한 번 돌려두는 것으로 해결된다.
            if (ms >= LogOverMs)
                Main.Entry.Logger.Log(string.Format("[효과] {0} {1:F0}ms ({2}번째 사용)", name, ms, n + 1));
        }

        internal static string FrameSummary()
        {
            if (StartedThisFrame == 0) return "효과 시작 없음";
            return string.Format("효과 {0}개 시작, 합계 {1:F0}ms", StartedThisFrame, MsThisFrame);
        }

        internal static void ResetFrame()
        {
            StartedThisFrame = 0;
            MsThisFrame = 0;
        }
    }
}
