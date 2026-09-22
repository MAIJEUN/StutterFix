using System.Reflection.Emit;
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
            h.Patch(start, transpiler: new HarmonyMethod(typeof(MergeProbe), nameof(CountTranspiler)));
            var comp = AccessTools.Method(tm, "Complete");
            if (comp != null) h.Patch(comp, prefix: new HarmonyMethod(typeof(MergeProbe), nameof(Completing)));
            Main.Entry.Logger.Log("[덮어쓰기 측정] 설치");
        }

        private static void Enter() { inMove++; }
        private static Exception Exit(Exception __exception) { if (inMove > 0) inMove--; if (inMove == 0) CountDurations(); return __exception; }

        private static void NewFrame()
        {
            if (Time.frameCount == frame) return;
            if (frameMade > WorstMade) { WorstMade = frameMade; WorstSame = frameSame; WorstAt = Time.realtimeSinceStartup; }
            if (frameComplete > WorstComplete) { WorstComplete = frameComplete; WorstCompleteInMove = frameCompleteInMove; }
            if (frameDecos > WorstFrameDecos) { WorstFrameDecos = frameDecos; WorstFrameEffects = frameEffects; WorstFrameZero = ZeroTween.Fast - frameZeroStart; }
            frameComplete = frameCompleteInMove = 0;
            frameDecos = frameEffects = 0;
            frameZeroStart = ZeroTween.Fast;
            frame = Time.frameCount;
            madeThisFrame.Clear();
            frameMade = frameSame = 0;
        }

        private static void Added(Tween t)
        {
            if (inMove == 0 || t == null) return;
            NewFrame();
            madeThisFrame.Add(t);
            madeInEffect.Add(t);
            Made++; frameMade++;
        }

        private static void Killing(Tween t, bool complete)
        {
            if (inMove == 0 || !complete || t == null || !t.active) return;
            NewFrame();
            if (madeThisFrame.Contains(t)) { SameFrameKilled++; frameSame++; }
            else OlderKilled++;
        }

        // 두 번째 가설: 길이 0 인 "즉시 이동" 을 애니메이션으로 만들고 바로 끝내는 비용.
        // 효과 하나가 끝날 때 그 효과가 만든 애니메이션의 길이를 본다. 끝내기(Complete) 가 어디서 불리는지도 센다.
        private static readonly List<Tween> madeInEffect = new List<Tween>();
        internal static long ZeroDur, WithDur, CompleteInMove, CompleteOutside;
        private static int frameComplete;
        internal static int WorstComplete, WorstCompleteInMove;
        private static int frameCompleteInMove;

        private static void Completing()
        {
            NewFrame();
            frameComplete++;
            if (inMove > 0) { CompleteInMove++; frameCompleteInMove++; } else CompleteOutside++;
        }

        private static void CountDurations()
        {
            foreach (var t in madeInEffect)
            {
                try { if (t != null && t.Duration(false) <= 0f) ZeroDur++; else WithDur++; } catch { }
            }
            madeInEffect.Clear();
        }

        // 장식 이동 효과가 한 번에 장식을 몇 개나 처리하는지 (프레임 단위로 가장 심한 곳을 기록)
        internal static long Decos, Effects;
        private static int frameDecos, frameEffects;
        internal static int WorstFrameDecos, WorstFrameEffects;
        internal static long WorstFrameZero;
        private static long frameZeroStart;

        // 안 보이는 장식(불투명도 0, 렌더러 꺼짐, 오브젝트 꺼짐)이 얼마나 되는지도 센다.
        internal static long Invisible, ZeroOpacity, RendererOff, ObjOff;
        private static readonly AccessTools.FieldRef<scrDecoration, float> opacityRef = AccessTools.FieldRefAccess<scrDecoration, float>("opacity");
        private static readonly AccessTools.FieldRef<scrDecoration, bool> rendererOnRef = AccessTools.FieldRefAccess<scrDecoration, bool>("rendererEnabled");

        public static IEnumerable<scrDecoration> Count(IEnumerable<scrDecoration> src)
        {
            NewFrame();
            frameEffects++; Effects++;
            foreach (var d in src)
            {
                frameDecos++; Decos++;
                try
                {
                    bool zero = opacityRef(d) <= 0f, roff = !rendererOnRef(d), ooff = d != null && !d.gameObject.activeInHierarchy;
                    if (zero) ZeroOpacity++;
                    if (roff) RendererOff++;
                    if (ooff) ObjOff++;
                    if (zero || roff || ooff) Invisible++;
                }
                catch { }
                yield return d;
            }
        }

        public static IEnumerable<CodeInstruction> CountTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            for (int i = 0; i < code.Count; i++)
            {
                var mi = code[i].operand as MethodInfo;
                if (mi == null || mi.Name != "GetTaggedDecorations" || mi.ReturnType != typeof(IEnumerable<scrDecoration>)) continue;
                code.Insert(i + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MergeProbe), nameof(Count))));
                break;
            }
            return code;
        }

        internal static string Summary()
        {
            NewFrame();
            if (Made == 0) return "장식 이동 애니메이션 없음";
            return string.Format("장식 이동이 만든 애니메이션 {0}개 중 같은 프레임에 바로 덮어써진 것 {1}개 ({2:F0}%), 이전 프레임 것을 덮어쓴 것 {3}개 | 가장 많이 만든 프레임: {4}개 중 {5}개가 같은 프레임에 덮어써짐",
                Made, SameFrameKilled, 100.0 * SameFrameKilled / Made, OlderKilled, WorstMade, WorstSame)
                + string.Format(" || 효과 {0}번이 장식 {1}개 처리 (안 보이는 장식 {5}개 = 불투명도 0 {6}, 렌더러 꺼짐 {7}, 오브젝트 꺼짐 {8}) | 가장 많은 프레임: 효과 {2}개, 장식 {3}개, 그중 즉시 이동 처리 {4}개",
                    Effects, Decos, WorstFrameEffects, WorstFrameDecos, WorstFrameZero, Invisible, ZeroOpacity, RendererOff, ObjOff)
                + string.Format(" || 길이 0 인 즉시 이동 {0}개 ({1:F0}%), 길이 있는 것 {2}개 | 끝내기(Complete) 장식 이동 안 {3}번, 밖(DOTween 갱신 등) {4}번 | 끝내기가 가장 많은 프레임: {5}번 중 장식 이동 안 {6}번",
                ZeroDur, 100.0 * ZeroDur / Math.Max(1, ZeroDur + WithDur), WithDur, CompleteInMove, CompleteOutside, WorstComplete, WorstCompleteInMove);
        }

        internal static void Reset() { Invisible = ZeroOpacity = RendererOff = ObjOff = 0; Decos = Effects = 0; frameDecos = frameEffects = 0; WorstFrameDecos = WorstFrameEffects = 0; WorstFrameZero = 0; Made = SameFrameKilled = OlderKilled = 0; WorstMade = WorstSame = 0; madeThisFrame.Clear(); frameMade = frameSame = 0; ZeroDur = WithDur = CompleteInMove = CompleteOutside = 0; WorstComplete = WorstCompleteInMove = 0; frameComplete = frameCompleteInMove = 0; }
    }
}
