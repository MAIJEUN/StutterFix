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
        private static float graceUntil;
        internal static void Suspend(float seconds) { graceUntil = Time.realtimeSinceStartup + seconds; }
        internal static bool InGrace { get { return Time.realtimeSinceStartup < graceUntil; } }

        internal static bool ShouldRun(object instance, MethodBase method, object[] args)
        {
            if (!Enabled || replaying || RecolorSplit.Replaying) return true;   // 색 바꾸기 조각은 이미 나눠진 것이다
            if (Time.realtimeSinceStartup < graceUntil) return true;

            if (Time.frameCount != frame)
            {
                frame = Time.frameCount;
                usedMs = 0;
                Drain();
            }

            if (depth > 0) return true;        // 이미 시작한 효과의 내부 호출
            if (usedMs < BudgetMs) return true;

            queue.Add(new Pending { Instance = instance, Method = method, Args = args });
            DeferredTotal++;
            return false;
        }

        // 기본 클래스와 상속 클래스의 StartEffect 가 겹쳐 불리므로, 바깥 호출에서만 한 번 기록한다.
        internal static bool OuterCall { get { return depth == 0; } }

        internal static void Enter() { depth++; }

        internal static void Exit(double ms)
        {
            depth--;
            if (depth > 0) return;
            depth = 0;
            if (!replaying) usedMs += ms;   // 밀린 것을 처리할 때는 Drain 쪽에서 따로 센다
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
            ModCost.Add(SettingsWindow.T("밀린 효과 실행", "Deferred effects"), total);
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
