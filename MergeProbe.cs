using System.Reflection;
using System;
using System.Collections.Generic;
using DG.Tweening;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // (개발자용, 측정만) 장식 이동 효과가 만든 애니메이션 중 "같은 프레임 안에서 다음 효과에 바로 덮어써지는" 것이 얼마나 되나.
    //
    // Arche 229초: 한 프레임에 애니메이션 약 43,000개가 만들어지고 44,000개가 끝났는데, 끝난 뒤 살아 있는 것은 1,200개였다.
    // 장식 이동 효과 여러 개가 같은 장식의 같은 속성을 연달아 덮어써서, 앞 효과의 애니메이션은 한 번도 돌지 못하고
    // "끝 상태로 옮기기(Complete)" 만 하고 사라진다. 이것을 처음부터 만들지 않고 합치면 크게 줄 수 있다.
    // 합치기 전에, 실제로 그런 것이 몇 개이고 비용의 몇 할인지 잰다. 동작은 바꾸지 않는다.
    internal static class MergeProbe
    {
        private static int inMove;
        private static int frame = -1;
        private static readonly HashSet<Tween> madeThisFrame = new HashSet<Tween>();
        private static int frameMade, frameSame;

        internal static long Made, SameFrameKilled, OlderKilled;
        internal static int WorstMade, WorstSame;
        internal static float WorstAt;

        internal static void Install(Harmony h)
        {
            MethodBase start = null;
            foreach (var m in typeof(ffxMoveDecorationsPlus).GetMethods(AccessTools.all))
                if (m.Name == "StartEffect" && m.DeclaringType == typeof(ffxMoveDecorationsPlus) && !m.IsAbstract) start = m;
            var tm = AccessTools.TypeByName("DG.Tweening.Core.TweenManager");
            var add = tm != null ? AccessTools.Method(tm, "AddActiveTween") : null;
            var kill = AccessTools.Method(typeof(TweenExtensions), "Kill", new[] { typeof(Tween), typeof(bool) });
            if (start == null || add == null || kill == null) { Main.Entry.Logger.Log("[덮어쓰기 측정] 대상 없음"); return; }
            h.Patch(start, prefix: new HarmonyMethod(typeof(MergeProbe), nameof(Enter)), finalizer: new HarmonyMethod(typeof(MergeProbe), nameof(Exit)));
            h.Patch(add, postfix: new HarmonyMethod(typeof(MergeProbe), nameof(Added)));
            h.Patch(kill, prefix: new HarmonyMethod(typeof(MergeProbe), nameof(Killing)));
            Main.Entry.Logger.Log("[덮어쓰기 측정] 설치");
        }

        private static void Enter() { inMove++; }
        private static Exception Exit(Exception __exception) { if (inMove > 0) inMove--; return __exception; }

        private static void NewFrame()
        {
            if (Time.frameCount == frame) return;
            if (frameMade > WorstMade) { WorstMade = frameMade; WorstSame = frameSame; WorstAt = Time.realtimeSinceStartup; }
            frame = Time.frameCount;
            madeThisFrame.Clear();
            frameMade = frameSame = 0;
        }

        private static void Added(Tween t)
        {
            if (inMove == 0 || t == null) return;
            NewFrame();
            madeThisFrame.Add(t);
            Made++; frameMade++;
        }

        private static void Killing(Tween t, bool complete)
        {
            if (inMove == 0 || !complete || t == null || !t.active) return;
            NewFrame();
            if (madeThisFrame.Contains(t)) { SameFrameKilled++; frameSame++; }
            else OlderKilled++;
        }

        internal static string Summary()
        {
            NewFrame();
            if (Made == 0) return "장식 이동 애니메이션 없음";
            return string.Format("장식 이동이 만든 애니메이션 {0}개 중 같은 프레임에 바로 덮어써진 것 {1}개 ({2:F0}%), 이전 프레임 것을 덮어쓴 것 {3}개 | 가장 많이 만든 프레임: {4}개 중 {5}개가 같은 프레임에 덮어써짐",
                Made, SameFrameKilled, 100.0 * SameFrameKilled / Made, OlderKilled, WorstMade, WorstSame);
        }

        internal static void Reset() { Made = SameFrameKilled = OlderKilled = 0; WorstMade = WorstSame = 0; madeThisFrame.Clear(); frameMade = frameSame = 0; }
    }
}
