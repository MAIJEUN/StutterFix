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
                if (Edition.Dev || Main.MeasureBuild) InstallSetters(h);
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[프레임 구성] 설치 실패: " + ex.Message); }
        }

        public static void TweenPre() { tweenT = Stopwatch.GetTimestamp(); inTween = true; setDepth = 0; }
        public static void TweenPost() { inTween = false; Tween += (Stopwatch.GetTimestamp() - tweenT) * TickMs; }
        public static void UpdPre() { updT = Stopwatch.GetTimestamp(); }
        public static void UpdPost() { DecoUpdate += (Stopwatch.GetTimestamp() - updT) * TickMs; }
        public static void LatePre() { lateT = Stopwatch.GetTimestamp(); }
        public static void LatePost() { DecoLate += (Stopwatch.GetTimestamp() - lateT) * TickMs; }

        // ── (측정용) 애니메이션 갱신 중 장식 설정 함수에 쓴 시간 ──
        // 애니메이션 갱신 시간 = DOTween 관리(이징 계산, 목록, 델리게이트) + 콜백이 부르는 장식 설정 함수(실제 위치·색 반영).
        // 모드 쪽 애니메이터로 바꾸면 앞쪽만 줄어든다. 둘의 비율을 보려고 설정 함수를 애니메이션 갱신 안에서만 잰다.
        internal static double TweenSet, LastTweenSet; internal static int TweenSetN, LastTweenSetN;
        private static bool inTween; private static int setDepth; private static long setT;
        internal static void InstallSetters(Harmony h)
        {
            var d = typeof(scrDecoration); int n = 0;
            foreach (var name in new[] { "SetPositionX", "SetPositionY", "SetParallaxOffsetX", "SetParallaxOffsetY", "SetPivotX", "SetPivotY", "SetRotation", "SetColor", "SetOpacity", "SetScale" })
                foreach (var m in d.GetMethods(AccessTools.all))
                {
                    if (m.Name != name || m.DeclaringType != d || m.IsAbstract) continue;
                    try { h.Patch(m, prefix: new HarmonyMethod(typeof(FrameParts), nameof(SetPre)) { priority = Priority.First }, postfix: new HarmonyMethod(typeof(FrameParts), nameof(SetPost)) { priority = Priority.Last }); n++; } catch { }
                }
            Main.Entry.Logger.Log("[프레임 구성] 설정 함수 " + n + "개 잼 (측정용)");
        }
        public static void SetPre() { if (inTween && setDepth++ == 0) setT = Stopwatch.GetTimestamp(); }
        public static void SetPost() { if (inTween && setDepth > 0 && --setDepth == 0) { TweenSet += (Stopwatch.GetTimestamp() - setT) * TickMs; TweenSetN++; } }

        // 매 프레임 (EffectScan.ResetFrame 과 같은 때)
        internal static void ResetFrame()
        {
            LastTween = Tween; LastDecoUpdate = DecoUpdate; LastDecoLate = DecoLate; LastTweenSet = TweenSet; LastTweenSetN = TweenSetN; TweenSet = 0; TweenSetN = 0;
            Tween = DecoUpdate = DecoLate = 0;
        }
    }
}
