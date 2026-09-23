using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 한 프레임에 몰린 효과를 나눠서 시작한다.
    //
    // 측정으로 밝혀진 것:
    //   138.3초 프레임 471ms 중 424ms가 scrVfxPlus.Update 한 번
    //   그 안에서 효과 52개가 한꺼번에 시작됐다 (개당 약 8ms)
    //   타일을 칠하는 함수들은 그중 48ms뿐이고, 나머지는 효과 하나하나의 고정 비용이다
    //     - ffxRecolorFloorPlus : 타일 2000~6800개를 훑는다
    //     - ffxSetFilterAdvancedPlus : 필드마다 리플렉션을 돈다 (3700번째 사용에도 똑같이 느리다)
    //
    // 그래서 효과 자체를 빠르게 만드는 대신, 한 프레임이 쓸 시간을 정해 두고
    // 넘치는 것은 다음 프레임으로 넘긴다. 순서는 그대로 지킨다.
    // 400ms 한 번 멈추는 것보다 몇 프레임에 걸쳐 나눠 지는 편이 눈에 덜 띈다.
    public static class EffectBudget
    {
        internal static bool Enabled = true;
        internal static float BudgetMs = 10f;

        internal static int DeferredTotal;
        internal static int QueueLength { get { return queue.Count; } }

        private class Pending
        {
            public object Instance;
            public MethodBase Method;
            public object[] Args;
        }

        private static readonly List<Pending> queue = new List<Pending>();
        private static double usedMs;
        private static int frame = -1;
        private static bool replaying;
        private static int depth;

        // 한 번의 효과 시작이 내부에서 또 StartEffect 를 부른다(기본 클래스 -> 상속 클래스).
        // 시간을 두 번 더하지 않도록 가장 바깥 호출에서만 센다.
        // 곡을 중간부터 시작하면 게임이 그 지점까지의 효과를 한 프레임에 몰아서 적용한다.
        // 이걸 예산 초과로 보고 뒤로 미루면 적용 순서가 꼬여 이펙트가 이상하게 보였다.
        // 곡이 시작되거나 다시 시작된 직후에는 나누지 않고 그대로 통과시킨다.
        // 유예는 실제 시간만으로 재면 안 된다. 곡 준비 한 프레임이 3초를 넘으면(무거운 맵의 첫 판은 7~8초) 유예가 준비 도중에
        // 다 지나가서, 맵의 시작 효과 수천 개(Arche 4,344개)가 뒤로 밀리고 첫 판 시작 연출이 이상하게 보였다.
        // 그래서 곡 시작 뒤 60프레임, 그리고 첫 타일을 칠 때까지(실시간 모니터의 곡 시작 연출 구간)도 유예로 본다.
        private static float graceUntil;
        private static int graceFrame = -1;
        internal static void Suspend(float seconds) { graceUntil = Time.realtimeSinceStartup + seconds; graceFrame = Time.frameCount + 60; }
        internal static bool InGrace { get { return Time.realtimeSinceStartup < graceUntil || Time.frameCount <= graceFrame || PerfOverlay.InStartWindow; } }

        internal static bool ShouldRun(object instance, MethodBase method, object[] args)
        {
            if (!Enabled || replaying || RecolorSplit.Replaying) return true;   // 색 바꾸기 조각은 이미 나눠진 것이다
            if (InGrace) return true;

            if (Time.frameCount != frame)
            {
                frame = Time.frameCount;
                usedMs = 0;
                Drain();
            }

            if (depth > 0) return true;        // 이미 시작한 효과의 내부 호출
            // 앞에 밀린 것이 남아 있으면 순서를 지키려고 새 효과도 그 뒤에 선다(아래 예측 때문에 예산이 남아도 밀린 것이 있을 수 있다)
            if (queue.Count == 0)
            {
                double est = Estimate(instance);
                // 이번 프레임의 첫 효과는 무거워도 돌린다(효과 하나를 쪼개면 장식이 늦게 움직여서 예전에 되돌렸다).
                // 이미 뭔가 돌았으면, 이 효과까지 돌리면 예산을 넘을 것 같을 때 다음 프레임 맨 앞으로 미룬다.
                if (usedMs < BudgetMs && (usedMs <= 0.0 || usedMs + est <= BudgetMs)) { curCount = lastCount; return true; }
                if (usedMs < BudgetMs) DeferredByEstimate++;
            }

            queue.Add(new Pending { Instance = instance, Method = method, Args = args });
            DeferredTotal++;
            return false;
        }

        // ── 장식 이동 효과의 비용 예측 ──
        // Arche 효과 몰림: 장식 이동 효과 하나가 장식 수천 개를 옮기면 그것만 30ms(개발자용) 였다. 예전 규칙("이번 프레임에 10ms
        // 안 썼으면 시작")은 9ms 쓴 뒤에도 30ms 짜리를 시작해서, 한 프레임이 "무거운 효과들의 합" 이 됐다.
        // 비용은 옮길 장식 수에 거의 비례한다. 대상 장식 수는 taggedDecorations[태그] 목록 길이의 합이라 사전 조회 몇 번으로 센다
        // (같은 장식이 여러 태그에 있으면 조금 많게 센다 - 넉넉한 쪽이라 괜찮다). 장식 하나당 비용은 실행할 때마다 재서 학습한다.
        internal static long DeferredByEstimate;
        internal static string Summary() { return DeferredByEstimate > 0 ? string.Format(" | 효과 나누기: 무거울 것 같아 다음 프레임으로 미룬 효과 {0}개 (장식 하나당 {1:F2}us 로 학습)", DeferredByEstimate, PerDecoMs * 1000) : ""; }
        internal static double PerDecoMs = 0.0015;   // 처음 값. 실행하며 맞춰 간다
        private static int lastCount, curCount;
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, List<string>> tagsRef =
            AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, List<string>>("targetTags");
        private static readonly AccessTools.FieldRef<scrDecorationManager, Dictionary<string, List<scrDecoration>>> taggedRef =
            AccessTools.FieldRefAccess<scrDecorationManager, Dictionary<string, List<scrDecoration>>>("taggedDecorations");

        private static double Estimate(object instance)
        {
            lastCount = 0;
            var fx = instance as ffxMoveDecorationsPlus;
            if ((object)fx == null) return 0.0;
            try
            {
                var tags = tagsRef(fx);
                var dm = scrDecorationManager.instance;
                if (tags == null || (object)dm == null) return 0.0;
                var map = taggedRef(dm);
                if (map == null) return 0.0;
                int n = 0;
                for (int i = 0; i < tags.Count; i++)
                {
                    List<scrDecoration> list;
                    if (tags[i] != null && map.TryGetValue(tags[i], out list) && list != null) n += list.Count;
                }
                lastCount = n;
                return n * PerDecoMs;
            }
            catch { return 0.0; }
        }

        private static void Learn(int count, double ms)
        {
            if (count < 50) return;   // 장식이 적으면 고정 비용이 커서 하나당 비용이 튄다
            double per = ms / count;
            PerDecoMs = PerDecoMs * 0.7 + per * 0.3;
        }

        // 기본 클래스와 상속 클래스의 StartEffect 가 겹쳐 불리므로, 바깥 호출에서만 한 번 기록한다.
        internal static bool OuterCall { get { return depth == 0; } }

        internal static void Enter() { depth++; }

        internal static void Exit(double ms)
        {
            depth--;
            if (depth > 0) return;
            depth = 0;
            if (!replaying) { usedMs += ms; if (curCount > 0) { Learn(curCount, ms); curCount = 0; } }   // 밀린 것을 처리할 때는 Drain 쪽에서 따로 센다
        }

        // 밀린 것을 먼저 처리한다. 새 효과보다 앞서 실행해야 순서가 뒤집히지 않는다.
        private static void Drain()
        {
            if (queue.Count == 0) return;

            replaying = true;
            bool guard = TweenFix.Begin();   // 밀어둔 효과도 같은 보호 아래서 실행한다
            int done = 0, failed = 0;
            double worst = 0;
            string worstName = "";
            long drainStart = Stopwatch.GetTimestamp();

            try
            {
                while (done < queue.Count && usedMs < BudgetMs)
                {
                    var p = queue[done];
                    // 이번 프레임에 이미 뭔가 돌았고 이것까지 돌리면 넘칠 것 같으면 다음 프레임 맨 앞으로 남긴다
                    double est = Estimate(p.Instance);
                    int cnt = lastCount;
                    if (usedMs > 0.0 && usedMs + est > BudgetMs) break;
                    done++;

                    // 이미 사라진 효과에 그대로 부르면 예외가 난다. 예외 하나 만드는 비용이
                    // 효과를 시작하는 비용보다 커서, 밀린 것을 비우는 순간이 도로 끊김이 된다.
                    var uo = p.Instance as UnityEngine.Object;
                    if (uo == null) continue;

                    long t0 = Stopwatch.GetTimestamp();
                    try { p.Method.Invoke(p.Instance, p.Args); }
                    catch { failed++; }
                    double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

                    usedMs += ms;
                    Learn(cnt, ms);
                    if (ms > worst) { worst = ms; worstName = p.Instance.GetType().Name; }
                }
            }
            finally
            {
                TweenFix.End(guard);
                replaying = false;
                if (done > 0) queue.RemoveRange(0, done);
            }

            double total = (Stopwatch.GetTimestamp() - drainStart) * 1000.0 / Stopwatch.Frequency;
            // 밀린 효과를 실행한 시간은 게임 효과 자체의 비용이다(효과 시간으로 따로 잡힌다). 모드 작업으로 세면
            // 모니터가 "모드 작업 (밀린 효과 실행)" 이라고 모드 탓으로 보여 줘서 뺐다. 실시간 모니터에는 "효과 몰림" 으로 나온다.
            if (total > BudgetMs * 2)
                Main.Entry.Logger.Log(string.Format(
                    "[효과나누기] 밀린 것 {0}개 처리에 {1:F0}ms (예산 {2:F0}ms), 실패 {3}개, 최악 {4} {5:F0}ms, 남은 대기 {6}개",
                    done, total, BudgetMs, failed, worstName, worst, queue.Count));
        }

        // 효과가 하나도 시작되지 않는 프레임에도 밀린 것을 비워야 한다.
        internal static void Tick()
        {
            if (!Enabled) return;
            if (Time.frameCount != frame)
            {
                frame = Time.frameCount;
                usedMs = 0;
            }
            Drain();
        }

        internal static void Reset()
        {
            queue.Clear();
            RecolorSplit.Reset();
            usedMs = 0;
            depth = 0;
            replaying = false;
        }
    }
}
