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
        internal static bool Enabled = Edition.Dev;   // 통계/로그. 효과 나누기는 이 값과 상관없이 돈다
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

                // 같은 이름의 함수가 여러 개인 타입이 있다. AccessTools.DeclaredMethod 는 그럴 때 예외를 던지고,
                // 그 예외 하나 때문에 설치가 통째로 중단됐었다. 이름으로 전부 찾아 하나씩 따로 감싼다.
                int count = 0;
                foreach (var t in baseType.Assembly.GetTypes())
                {
                    if (!baseType.IsAssignableFrom(t) || t.ContainsGenericParameters) continue;
                    foreach (var m in t.GetMethods(AccessTools.all))
                    {
                        if (m.Name != "StartEffect" || m.DeclaringType != t) continue;
                        if (m.IsAbstract || m.ContainsGenericParameters) continue;
                        try
                        {
                            harmony.Patch(m,
                                prefix: new HarmonyMethod(typeof(EffectScan), nameof(Pre)),
                                postfix: new HarmonyMethod(typeof(EffectScan), nameof(Post)));
                            count++;
                        }
                        catch { }
                    }
                }
                // StartEffect 28개가 합계 1ms인데 scrVfxPlus.Update 는 404ms였다.
                // 시간이 효과 시작이 아니라 그 앞의 걸러내기에 있을 수 있으므로 그쪽도 같이 센다.
                int checks = 0;
                if (Edition.Dev) foreach (var m in baseType.GetMethods(AccessTools.all))
                {
                    if (m.Name != "IsAllowedByVisualSettings" || m.IsAbstract) continue;
                    try
                    {
                        harmony.Patch(m,
                            prefix: new HarmonyMethod(typeof(EffectScan), nameof(CheckPre)),
                            postfix: new HarmonyMethod(typeof(EffectScan), nameof(CheckPost)));
                        checks++;
                    }
                    catch { }
                }

                Main.Entry.Logger.Log("[효과] StartEffect " + count + "개, 걸러내기 " + checks + "개 감쌈");
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("[효과] 설치 실패: " + ex.Message);
            }
        }

        // ── 타일 등장 연출 ────────────────────────────────────────────
        // 28~40초의 박자 끊김 프레임마다 시작된 효과는 전부 ffxFloorAppearPlus 였다.
        // 이 효과 자체는 타일 하나에 위치/크기/투명도 애니메이션을 거는 가벼운 일이다.
        // 그런데 곡 내내 박자마다 나오는데 끊김은 그 구간에만 있다. 그 구간 타일에
        // 무거운 것(딸린 장식 등)이 붙어 있는지 보려고 등장할 때마다 딸린 렌더러 수를 남긴다.
        internal static bool LogFloorAppear;   // 결론 남(평범한 타일, 원인 아님). 다시 볼 때만 켠다

        private static FieldInfo floorField, animTypeField;

        private static void DescribeFloorAppear(object instance)
        {
            try
            {
                var t = instance.GetType();
                if (floorField == null) floorField = AccessTools.Field(t, "floor");
                if (animTypeField == null) animTypeField = AccessTools.Field(t, "animType");

                var floor = floorField != null ? floorField.GetValue(instance) as UnityEngine.Component : null;
                if (floor == null) { Main.Entry.Logger.Log("[타일등장] 대상 없음"); return; }

                var tr = floor.transform;
                int renderers = floor.GetComponentsInChildren<UnityEngine.Renderer>(true).Length;
                Main.Entry.Logger.Log(string.Format("[타일등장] {0} | 방식 {1} | 자식 {2}개, 렌더러 {3}개",
                    floor.name, animTypeField != null ? animTypeField.GetValue(instance) : "?", tr.childCount, renderers));
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[타일등장] 읽기 실패: " + ex.Message); }
        }

        // false 를 돌려주면 그 효과는 이번 프레임에 시작하지 않고 다음 프레임으로 밀린다.
        public static bool Pre(object __instance, MethodBase __originalMethod, object[] __args, out long __state)
        {
            __state = Stopwatch.GetTimestamp();
            // GetType().Name 은 부를 때마다 문자열을 새로 만든다. 효과가 시작될 때마다 돌던 자리라,
            // 기록이 꺼진 플레이어용에서는 아예 들어오지 않게 순서를 바꿨다.
            if (LogFloorAppear && __instance != null && GcControl.Paused && EffectBudget.OuterCall
                && __instance.GetType().Name == "ffxFloorAppearPlus") DescribeFloorAppear(__instance);
            if (!EffectBudget.ShouldRun(__instance, __originalMethod, __args)) return false;
            EffectBudget.Enter();
            return true;
        }

        public static void Post(object __instance, long __state)
        {
            double ms = (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
            EffectBudget.Exit(ms);
            // 실시간 모니터가 "왜 끊겼는지"를 가리려면 플레이어용에서도 프레임마다 효과 시작 시간 합계가 필요하다.
            // 이미 잰 값을 더하기만 하므로 비용은 없다.
            if (EffectBudget.OuterCall) { FrameEffectMs += ms; FrameN++; if (__instance is ffxMoveDecorationsPlus) FrameMoveMs += ms; }
            if (!Enabled) return;

            string name = __instance != null ? __instance.GetType().Name : "?";

            // 기본 클래스 -> 상속 클래스로 겹쳐 불리므로 바깥 호출만 센다 (예전엔 개수와 시간이 두 배로 찍혔다).
            // 이름은 종류별 개수와 시간으로 묶는다. 순서대로 300자를 이어 붙이던 때는 103초 끊김의
            // 주범(색 바꾸기 41ms 한 개)이 이동 효과 이름들에 밀려 잘려 나갔다.
            if (EffectBudget.OuterCall)
            {
                StartedThisFrame++;
                MsThisFrame += ms;
                NameStat st;
                if (!namesThisFrame.TryGetValue(name, out st)) { st = new NameStat(); namesThisFrame[name] = st; }
                st.Count++;
                st.Ms += ms;
            }
            int n;
            useCount.TryGetValue(name, out n);
            useCount[name] = n + 1;

            // 처음 쓰는 효과인지 알아야 한다. 처음만 느리다면 미리 한 번 돌려두는 것으로 해결된다.
            // 2400번째 사용에도 그대로 느린 것이 확인됐으므로, 이제는 무엇을 얼마나 건드리는지를 본다.
            if (ms >= LogOverMs && EffectBudget.OuterCall)
                Main.Entry.Logger.Log(string.Format("[효과] {0} {1:F0}ms ({2}번째 사용) {3}", name, ms, n + 1, Detail(__instance)));
        }

        // 효과가 몇 개의 타일을 건드리는지 본다.
        // ffxRecolorFloorPlus.StartEffect 는 start~end 구간의 타일마다
        // UpdateAngle / SetTrackStyle / ColorFloor 를 부르고 타일마다 애니메이션을 만든다.
        // 구간이 넓으면 한 번 시작하는 데 수십 ms가 걸리는 것이 당연하다.
        private static string Detail(object instance)
        {
            if (instance == null) return "";
            try
            {
                var t = instance.GetType();
                var start = AccessTools.Field(t, "start");
                var end = AccessTools.Field(t, "end");
                if (start == null || end == null) return "";
                int s = Convert.ToInt32(start.GetValue(instance));
                int e = Convert.ToInt32(end.GetValue(instance));

                string extra = "";
                var dur = AccessTools.Field(t, "colorAnimDuration") ?? AccessTools.Field(t, "duration");
                if (dur != null) extra = ", 지속 " + Convert.ToDouble(dur.GetValue(instance)).ToString("F2");

                return "타일 " + s + "~" + e + " (" + (e - s + 1) + "개)" + extra;
            }
            catch { return ""; }
        }

        private class NameStat { public int Count; public double Ms; }
        // 매 프레임 비우지 않고 0으로 되돌려 재사용한다 (프레임마다 새로 만들면 그 자체가 할당이다)
        private static readonly System.Collections.Generic.Dictionary<string, NameStat> namesThisFrame
            = new System.Collections.Generic.Dictionary<string, NameStat>();

        private static string NamesByCost()
        {
            var list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, NameStat>>();
            foreach (var kv in namesThisFrame) if (kv.Value.Count > 0) list.Add(kv);
            list.Sort((a, b) => b.Value.Ms.CompareTo(a.Value.Ms));
            var sb = new System.Text.StringBuilder();
            foreach (var kv in list)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(kv.Key.Replace("Plus", "")).Append(" ×").Append(kv.Value.Count).Append(' ').Append(kv.Value.Ms.ToString("F0")).Append("ms");
            }
            return sb.ToString();
        }

        internal static int ChecksThisFrame;
        internal static double CheckMsThisFrame;

        public static void CheckPre(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void CheckPost(long __state)
        {
            if (!Enabled) return;
            ChecksThisFrame++;
            CheckMsThisFrame += (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
        }

        internal static string FrameSummary()
        {
            return string.Format("효과 {0}개 시작 {1:F0}ms [{4}], 걸러내기 {2}회 {3:F0}ms",
                StartedThisFrame, MsThisFrame, ChecksThisFrame, CheckMsThisFrame, NamesByCost());
        }

        // 지난 프레임과 이번 프레임의 효과 시작 시간 합계 (항상 켜짐)
        internal static double FrameEffectMs, LastFrameEffectMs, FrameMoveMs, LastFrameMoveMs;   // 그중 장식 이동
        internal static int FrameN, LastFrameN;

        internal static void ResetFrame()
        {
            LastFrameEffectMs = FrameEffectMs; LastFrameMoveMs = FrameMoveMs; LastFrameN = FrameN;
            FrameMoveMs = 0; FrameN = 0;
            FrameEffectMs = 0;
            StartedThisFrame = 0;
            MsThisFrame = 0;
            ChecksThisFrame = 0;
            CheckMsThisFrame = 0;
            foreach (var st in namesThisFrame.Values) { st.Count = 0; st.Ms = 0; }
        }
    }
}
