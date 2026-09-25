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
            public float Time;   // 원래 시작해야 했던 때(실시간)
            public int Frame;
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
            if (replaying || RecolorSplit.Replaying) return true;   // 색 바꾸기 조각은 이미 나눠진 것이다
            // 꺼졌는데 밀린 것이 남아 있으면, 새 효과보다 먼저 모두 실행해 순서를 지킨다
            if (!Enabled) { if (queue.Count > 0 && depth == 0) DrainAll(); return true; }
            if (InGrace) return true;

            if (Time.frameCount != frame)
            {
                frame = Time.frameCount;
                usedMs = 0;
                Drain();
            }

            if (depth > 0) return true;        // 이미 시작한 효과의 내부 호출
            // 화면에만 영향을 주는 효과만 미룬다. 소리(ffxPlaySound 는 오디오 시계로 예약하는데 늦게 부르면 그 시각이 지나 늦게 울린다),
            // 판정·진행(ffxKillPlayer, ffxCheckpoint, ffxSetOffset, ffxSpeed, ffxSetInputEventPlus ...), 프레임 제한 같은 것은
            // 원래 프레임에 그대로 실행한다. 밀린 것보다 먼저 실행되지만, 화면 효과와 서로 값을 주고받지 않는다.
            if (!Deferrable(instance)) return true;
            // 앞에 밀린 것이 남아 있으면 순서를 지키려고 새 효과도 그 뒤에 선다
            if (queue.Count == 0 && usedMs < BudgetMs) return true;

            queue.Add(new Pending { Instance = instance, Method = method, Args = args, Time = UnityEngine.Time.realtimeSinceStartup, Frame = UnityEngine.Time.frameCount });
            DeferredTotal++;
            return false;
        }

        // 미뤄도 되는 효과 = 화면에만 영향을 주는 효과 (게임 코드로 확인한 이름만. 모르는 효과는 미루지 않는다)
        private static readonly HashSet<string> visualOnly = new HashSet<string>
        {
            "ffxMoveDecorationsPlus", "ffxRecolorFloorPlus", "ffxMoveFloorPlus", "ffxFlashPlus", "ffxSetFilterPlus", "ffxSetFilterAdvancedPlus",
            "ffxBloomPlus", "ffxCameraPlus", "ffxCustomBackgroundPlus", "ffxHallOfMirrorsPlus", "ffxScreenTilePlus", "ffxScreenScrollPlus",
            "ffxShakeScreenPlus", "ffxSetTextPlus", "ffxSetObjectPlus", "ffxSetParticlePlus", "ffxEmitParticlePlus",
            "ffxFloorAppearPlus", "ffxFloorDisappearPlus", "ffxTweenBlizzardPlus",
        };
        private static readonly Dictionary<Type, bool> deferrable = new Dictionary<Type, bool>();
        private static bool Deferrable(object instance)
        {
            if (instance == null) return false;
            var t = instance.GetType();
            bool ok;
            if (!deferrable.TryGetValue(t, out ok)) { ok = visualOnly.Contains(t.Name); deferrable[t] = ok; }
            if (!ok) return false;
            // 히트박스 장식이 있는 맵에서는 장식 이동을 미루면 히트박스(판정) 위치가 늦게 바뀐다
            if (instance is ffxMoveDecorationsPlus && MapHasHitbox()) return false;
            return true;
        }
        private static List<scrDecoration> hbList; private static int hbCount = -1; private static bool hbAny; private static int hbFrame = -1000;
        private static readonly AccessTools.FieldRef<scrDecorationManager, List<scrDecoration>> allDecoRef = AccessTools.FieldRefAccess<scrDecorationManager, List<scrDecoration>>("allDecorations");
        private static bool MapHasHitbox()
        {
            try
            {
                var mgr = scrDecorationManager.instance;
                var all = (object)mgr != null ? allDecoRef(mgr) : null;
                if (all == null) return false;
                // 히트박스 값은 장식 생성·Setup 때만 바뀐다. 목록이 같으면 1초(60프레임)에 한 번만 다시 센다
                if (ReferenceEquals(all, hbList) && all.Count == hbCount && Time.frameCount - hbFrame < 60) return hbAny;
                hbList = all; hbCount = all.Count; hbFrame = Time.frameCount; hbAny = false;
                for (int i = 0; i < all.Count; i++) { var d = all[i]; if ((object)d != null && d.hitbox != 0) { hbAny = true; break; } }
                return hbAny;
            }
            catch { return true; }   // 모르면 미루지 않는다
        }

        // 1.3.7 에서 "효과 비용 예측"(대상 장식 수 x 학습한 장식당 비용으로 무거운 효과를 다음 프레임으로 미루기)을 넣었다가 뺐다.
        // Arche 에서 예측으로 더 미룬 효과가 3개뿐이었고, 가장 무거운 효과 하나(21ms)가 바닥이라 최악 프레임이 그대로였다.
        // 뒤로 미룬 효과가 얼마나 늦게 시작했는지는 계속 잰다(연출이 늦어지는 정도를 눈 대신 숫자로 본다).
        internal static long LateN; internal static double LateSumMs; internal static float LateMaxMs; internal static int LateMaxFrames;
        internal static void ResetLate() { LateN = 0; LateSumMs = 0; LateMaxMs = 0; LateMaxFrames = 0; }
        internal static string Summary()
        {
            if (LateN == 0) return "";
            return string.Format(" | 효과 나누기: 뒤로 미룬 효과 {0}개, 늦게 시작한 정도 평균 {1:F1}ms 최대 {2:F1}ms ({3}프레임)",
                LateN, LateSumMs / LateN, LateMaxMs, LateMaxFrames);
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

                    // 얼마나 늦게 시작했나 (연출이 늦어지는 정도를 눈 대신 숫자로 본다)
                    float late = (UnityEngine.Time.realtimeSinceStartup - p.Time) * 1000f; int lateF = UnityEngine.Time.frameCount - p.Frame;
                    LateN++; LateSumMs += late; if (late > LateMaxMs) LateMaxMs = late; if (lateF > LateMaxFrames) LateMaxFrames = lateF;
                    long t0 = Stopwatch.GetTimestamp();
                    try { Invoker(p.Method)(p.Instance, p.Args); }
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
            // 밀린 효과를 실행한 시간은 게임 효과 자체의 비용이다(효과 시간으로 따로 잡힌다). 모드 작업으로 세면
            // 모니터가 "모드 작업 (밀린 효과 실행)" 이라고 모드 탓으로 보여 줘서 뺐다. 실시간 모니터에는 "효과 몰림" 으로 나온다.
            if (total > BudgetMs * 2)
                Main.Entry.Logger.Log(string.Format(
                    "[효과나누기] 밀린 것 {0}개 처리에 {1:F0}ms (예산 {2:F0}ms), 실패 {3}개, 최악 {4} {5:F0}ms, 남은 대기 {6}개",
                    done, total, BudgetMs, failed, worstName, worst, queue.Count));
        }

        // 밀린 효과를 MethodBase.Invoke 로 부르면 호출마다 인자 검사와 리플렉션 경로를 탄다. 효과 종류(StartEffect 수십 개)마다
        // 한 번 IL 로 만든 호출기를 쓴다. 가상 호출(callvirt)이라 Invoke 와 같은 함수에 간다(밀리는 것은 가장 바깥 호출뿐).
        private static readonly Dictionary<MethodBase, FastInvokeHandler> invokers = new Dictionary<MethodBase, FastInvokeHandler>();
        private static FastInvokeHandler Invoker(MethodBase m)
        {
            FastInvokeHandler h;
            if (invokers.TryGetValue(m, out h)) return h;
            var mi = m as MethodInfo;
            try { h = mi != null ? MethodInvoker.GetHandler(mi) : null; } catch { h = null; }
            if (h == null) h = (inst, args) => m.Invoke(inst, args);
            invokers[m] = h;
            return h;
        }

        // 효과가 하나도 시작되지 않는 프레임에도 밀린 것을 비워야 한다.
        // 기능을 꺼도 이미 밀어 둔 것은 끝까지 실행한다 (예전에는 꺼진 뒤 대기열이 영영 안 돌아 그 효과들이 빠졌다).
        internal static void Tick()
        {
            if (!Enabled && queue.Count == 0) return;
            if (Time.frameCount != frame)
            {
                frame = Time.frameCount;
                usedMs = 0;
            }
            Drain();
        }

        // 모드를 내릴 때: 밀린 효과와 타일 색 조각을 버리지 않고 지금 한꺼번에 실행한다 (원래 게임은 이미 다 실행했을 것들)
        internal static void FlushAll()
        {
            DrainAll();
            RecolorSplit.FlushAll();
        }
        private static void DrainAll()
        {
            float saved = BudgetMs;
            try { BudgetMs = float.MaxValue; usedMs = 0; Drain(); }
            finally { BudgetMs = saved; }
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
