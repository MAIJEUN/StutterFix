using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using DG.Tweening;
using DG.Tweening.Core;
using DG.Tweening.Plugins.Options;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 장식 이동의 "길이 0 인 즉시 이동" 을 애니메이션 없이 처리하기 위한 준비 (1단계: 검증만).
    //
    // 측정 (Arche): 장식 이동이 만든 애니메이션의 70% 가 길이 0. 229초 한 프레임에 4만 3천 개를 만들고, 게임용 DOTween 의
    // Done() 이 길이 0 이면 그 자리에서 Complete 한다(끝내기 44,081번 중 44,078번이 장식 이동 안). 다음 갱신 때 또 정리한다.
    // 즉시 옮기기 하나에 "만들기 -> 끝내기 -> 정리" 가 다 일어나서 한 프레임 수백 ms 가 됐다.
    //
    // 게임 코드의 모양 (IL 로 확인, 속성마다):
    //   DOTween.To(getter, setter, 목표값, 길이).SetEase(ease)[.SetOptions(축, 스냅)].OnUpdate(cb)[.OnComplete(cb)].Done()
    //   종류는 float, Vector2, Color 세 가지.
    // 길이 0 이면 결과는 "목표값을 넣고 OnUpdate, OnComplete 를 부르는 것" 이어야 한다. 그런데 DOTween 이 0/0 을 어떻게 다루는지에
    // 따라 값이 비트 단위로 다를 수 있어서, 바꾸기 전에 모든 즉시 이동에서 "우리가 계산한 값" 과 "DOTween 이 실제로 넣은 값" 을 비교한다.
    // 이 단계에서는 게임 동작을 바꾸지 않는다(전부 원래 DOTween 으로 처리).
    internal static class ZeroTween
    {
        internal static long Checked, MatchEnd, MatchFormula, Mismatch, NoCallback;
        internal static string FirstMismatch = "";
        internal static bool Patched;

        private class Info { public Func<object> Get; public object End; }
        private static readonly Dictionary<Tween, Info> pending = new Dictionary<Tween, Info>();

        internal static void Install(Harmony h)
        {
            MethodBase start = null;
            foreach (var m in typeof(ffxMoveDecorationsPlus).GetMethods(AccessTools.all))
                if (m.Name == "StartEffect" && m.DeclaringType == typeof(ffxMoveDecorationsPlus) && !m.IsAbstract) start = m;
            if (start == null) return;
            h.Patch(start, transpiler: new HarmonyMethod(typeof(ZeroTween), nameof(Transpiler)));
            Main.Entry.Logger.Log("[즉시 이동 검증] 설치" + (Patched ? "" : " - 모양이 달라 적용 안 함"));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int n = 0;
            foreach (var c in code)
            {
                var mi = c.operand as MethodInfo;
                if (mi == null) continue;
                MethodInfo rep = null;
                if (mi.DeclaringType == typeof(DOTween) && mi.Name == "To")
                {
                    var p = mi.GetParameters();
                    if (p.Length == 4 && p[2].ParameterType == typeof(float)) rep = AccessTools.Method(typeof(ZeroTween), nameof(ToF));
                    else if (p.Length == 4 && p[2].ParameterType == typeof(Vector2)) rep = AccessTools.Method(typeof(ZeroTween), nameof(ToV));
                    else if (p.Length == 4 && p[2].ParameterType == typeof(Color)) rep = AccessTools.Method(typeof(ZeroTween), nameof(ToC));
                }
                else if (mi.DeclaringType == typeof(TweenExtensions) && mi.Name == "Done" && mi.IsGenericMethod)
                {
                    var t = mi.GetGenericArguments()[0];
                    if (t == typeof(TweenerCore<float, float, FloatOptions>)) rep = AccessTools.Method(typeof(ZeroTween), nameof(DoneF));
                    else if (t == typeof(TweenerCore<Vector2, Vector2, VectorOptions>)) rep = AccessTools.Method(typeof(ZeroTween), nameof(DoneV));
                    else if (t == typeof(TweenerCore<Color, Color, ColorOptions>)) rep = AccessTools.Method(typeof(ZeroTween), nameof(DoneC));
                    else if (t == typeof(Tweener)) rep = AccessTools.Method(typeof(ZeroTween), nameof(DoneT));
                }
                if (rep != null) { c.operand = rep; c.opcode = OpCodes.Call; n++; }
            }
            Patched = n > 0;
            return code;
        }

        // ── 만들기: 원래대로 만들고, 길이 0 이면 비교용 정보를 남긴다 ─────────────
        public static TweenerCore<float, float, FloatOptions> ToF(DOGetter<float> g, DOSetter<float> s, float end, float dur)
        {
            var t = DOTween.To(g, s, end, dur);
            if (dur <= 0f && t != null) pending[t] = new Info { Get = () => g(), End = end };
            return t;
        }
        public static TweenerCore<Vector2, Vector2, VectorOptions> ToV(DOGetter<Vector2> g, DOSetter<Vector2> s, Vector2 end, float dur)
        {
            var t = DOTween.To(g, s, end, dur);
            if (dur <= 0f && t != null) pending[t] = new Info { Get = () => g(), End = end };
            return t;
        }
        public static TweenerCore<Color, Color, ColorOptions> ToC(DOGetter<Color> g, DOSetter<Color> s, Color end, float dur)
        {
            var t = DOTween.To(g, s, end, dur);
            if (dur <= 0f && t != null) pending[t] = new Info { Get = () => g(), End = end };
            return t;
        }

        // ── 끝내기: 예상값을 계산해 두고, 원래 Done 을 부른 직후 실제 값과 비교한다 (Done 은 그 자리에서 Complete 한다) ──
        public static TweenerCore<float, float, FloatOptions> DoneF(TweenerCore<float, float, FloatOptions> t) { var c = Pre(t); var r = t.Done(); Post(c); return r; }
        public static TweenerCore<Vector2, Vector2, VectorOptions> DoneV(TweenerCore<Vector2, Vector2, VectorOptions> t) { var c = Pre(t); var r = t.Done(); Post(c); return r; }
        public static TweenerCore<Color, Color, ColorOptions> DoneC(TweenerCore<Color, Color, ColorOptions> t) { var c = Pre(t); var r = t.Done(); Post(c); return r; }
        public static Tweener DoneT(Tweener t) { var c = Pre(t); var r = t.Done(); Post(c); return r; }

        private class Case { public Info Info; public object PredEnd, PredFormula; }

        private static Case Pre(Tween t)
        {
            Info info;
            if (t == null || !pending.TryGetValue(t, out info)) return null;
            pending.Remove(t);
            try
            {
                if (t.onUpdate == null && t.onComplete == null) NoCallback++;
                return new Case { Info = info, PredEnd = info.End, PredFormula = Formula(t, info.Get(), info.End) };
            }
            catch { return null; }
        }

        private static void Post(Case c)
        {
            if (c == null) return;
            try
            {
                object actual = c.Info.Get();
                Checked++;
                bool e = Same(actual, c.PredEnd), f = Same(actual, c.PredFormula);
                if (e) MatchEnd++;
                if (f) MatchFormula++;
                if (!e && !f)
                {
                    Mismatch++;
                    if (FirstMismatch.Length == 0) FirstMismatch = "실제 " + Show(actual) + ", 목표 " + Show(c.PredEnd) + ", 계산 " + Show(c.PredFormula);
                }
            }
            catch { }
        }

        // DOTween 플러그인과 같은 식: start + (end - start) * 1 (축 제한과 반올림 포함)
        private static object Formula(Tween t, object start, object end)
        {
            if (start is float)
            {
                float s = (float)start, e = (float)end;
                return s + (e - s) * 1f;
            }
            if (start is Color)
            {
                Color s = (Color)start, e = (Color)end;
                return s + (e - s) * 1f;
            }
            if (start is Vector2)
            {
                Vector2 s = (Vector2)start, e = (Vector2)end, ch = e - s;
                var tc = t as TweenerCore<Vector2, Vector2, VectorOptions>;
                var o = tc != null ? tc.plugOptions : default(VectorOptions);
                Vector2 r;
                switch (o.axisConstraint)
                {
                    case AxisConstraint.X: r = s; r.x = s.x + ch.x * 1f; if (o.snapping) r.x = Mathf.Round(r.x); break;
                    case AxisConstraint.Y: r = s; r.y = s.y + ch.y * 1f; if (o.snapping) r.y = Mathf.Round(r.y); break;
                    default: r = s + ch * 1f; if (o.snapping) { r.x = Mathf.Round(r.x); r.y = Mathf.Round(r.y); } break;
                }
                return r;
            }
            return end;
        }

        private static bool Same(object a, object b)
        {
            if (a is float && b is float) return BitConverter.ToInt32(BitConverter.GetBytes((float)a), 0) == BitConverter.ToInt32(BitConverter.GetBytes((float)b), 0);
            if (a is Vector2 && b is Vector2) { var x = (Vector2)a; var y = (Vector2)b; return Same(x.x, y.x) && Same(x.y, y.y); }
            if (a is Color && b is Color) { var x = (Color)a; var y = (Color)b; return Same(x.r, y.r) && Same(x.g, y.g) && Same(x.b, y.b) && Same(x.a, y.a); }
            return Equals(a, b);
        }

        private static string Show(object o) { return o is Vector2 ? ((Vector2)o).ToString("R") : o is Color ? ((Color)o).ToString("R") : o is float ? ((float)o).ToString("R") : "" + o; }

        internal static string Summary()
        {
            if (Checked == 0) return "검사한 즉시 이동 없음";
            return string.Format("즉시 이동 {0}개 검사: 목표값과 같음 {1}, 계산식과 같음 {2}, 둘 다 다름 {3}{4} | 콜백 없는 것 {5}",
                Checked, MatchEnd, MatchFormula, Mismatch, Mismatch > 0 ? " (예: " + FirstMismatch + ")" : "", NoCallback);
        }

        internal static void Reset() { Checked = MatchEnd = MatchFormula = Mismatch = NoCallback = 0; FirstMismatch = ""; pending.Clear(); }
    }
}
