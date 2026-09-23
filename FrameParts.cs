using System;
using System.Diagnostics;
using HarmonyLib;

namespace StutterFix
{
    // 플레이어용에서도 무거운 프레임이 무엇으로 이뤄졌는지 알기 위한 가벼운 시간 재기.
    // 프레임마다 세 곳(애니메이션 갱신 DOTweenComponent.Update, 장식 갱신 scrDecorationManager.Update/LateUpdate)의 앞뒤에서 시간을 한 번씩만 잰다.
    // 효과 시작 시간은 EffectScan 이 이미 잰다. 곡 요약에 가장 무거운 프레임 5개(곡 시간과 구성)를 남긴다.
    internal static class FrameParts
    {
        internal static double Tween, DecoUpdate, DecoLate, LastTween, LastDecoUpdate, LastDecoLate;
        private static long tweenT, updT, lateT;
        private static readonly double TickMs = 1000.0 / Stopwatch.Frequency;

        internal static void Install(Harmony h)
        {
            try
            {
                var dt = AccessTools.TypeByName("DG.Tweening.Core.DOTweenComponent");
                var m = dt == null ? null : AccessTools.Method(dt, "Update");
                if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(FrameParts), nameof(TweenPre)) { priority = Priority.First }, postfix: new HarmonyMethod(typeof(FrameParts), nameof(TweenPost)) { priority = Priority.Last });
                var u = AccessTools.Method(typeof(scrDecorationManager), "Update");
                if (u != null) h.Patch(u, prefix: new HarmonyMethod(typeof(FrameParts), nameof(UpdPre)) { priority = Priority.First }, postfix: new HarmonyMethod(typeof(FrameParts), nameof(UpdPost)) { priority = Priority.Last });
                var l = AccessTools.Method(typeof(scrDecorationManager), "LateUpdate");
                if (l != null) h.Patch(l, prefix: new HarmonyMethod(typeof(FrameParts), nameof(LatePre)) { priority = Priority.First }, postfix: new HarmonyMethod(typeof(FrameParts), nameof(LatePost)) { priority = Priority.Last });
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[프레임 구성] 설치 실패: " + ex.Message); }
        }

        public static void TweenPre() { tweenT = Stopwatch.GetTimestamp(); }
        public static void TweenPost() { Tween += (Stopwatch.GetTimestamp() - tweenT) * TickMs; }
        public static void UpdPre() { updT = Stopwatch.GetTimestamp(); }
        public static void UpdPost() { DecoUpdate += (Stopwatch.GetTimestamp() - updT) * TickMs; }
        public static void LatePre() { lateT = Stopwatch.GetTimestamp(); }
        public static void LatePost() { DecoLate += (Stopwatch.GetTimestamp() - lateT) * TickMs; }

        // 매 프레임 (EffectScan.ResetFrame 과 같은 때)
        internal static void ResetFrame()
        {
            LastTween = Tween; LastDecoUpdate = DecoUpdate; LastDecoLate = DecoLate;
            Tween = DecoUpdate = DecoLate = 0;
        }
    }
}
