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
    // 장식 이동의 "길이 0 인 즉시 이동" 을 애니메이션 없이 처리한다.
    //
    // 측정 (Arche): 장식 이동이 만든 애니메이션의 70% 가 길이 0. 229초 한 프레임에 4만 3천 개를 만들고, 게임용 DOTween 의
    // Done() 이 길이 0 이면 그 자리에서 Complete 한다(끝내기 44,081번 중 44,078번이 장식 이동 안). 다음 갱신 때 또 정리한다.
    // 즉시 옮기기 하나에 "만들기 -> 끝내기 -> 정리" 가 다 일어나서 한 프레임 수백 ms 가 됐다(효과 하나 539ms 도 있었다).
    //
    // 게임 코드의 모양 (IL 로 확인, 속성마다):
    //   DOTween.To(getter, setter, 목표값, 길이).SetEase(ease)[.SetOptions(축, 스냅)].OnUpdate(cb)[.OnComplete(cb)].Done()
    //   종류는 float, Vector2, Color 세 가지.
    // 길이 0 이면 DOTween 이 하는 일은: 시작값 = getter(), 변화량 = 목표 - 시작(float 로 저장),
    //   setter(시작 + 변화량 x 이징(끝점)), OnUpdate, OnComplete. 그리고 애니메이션은 죽는다.
    // 그래서 To 에서는 애니메이션 대신 "대역" 하나를 돌려주고(뒤의 SetEase/SetOptions/OnUpdate/OnComplete 가 거기에 값을 적는다),
    // Done 에서 위의 일을 똑같이 한다. 대역은 종류마다 하나를 돌려 쓴다(To 와 Done 사이에 다른 코드가 끼지 않는다).
    // 게임이 저장해 둔 대역에 나중에 Kill 을 불러도, 대역은 꺼져 있어서 DOTween 이 아무것도 안 한다(원래도 이미 죽은 애니메이션).
    //
    // 검증 (같은 계산을 DOTween 결과와 비트 단위로 비교, Arche 264,739개):
    //   처음엔 76개(모두 float)가 끝자리가 달랐다. DOTween 은 변화량을 float 필드에 저장하면서 한 번 반올림하는데 그걸 빼먹었다.
    //   개발자용은 64개 중 1개를 계속 원래 DOTween 으로 처리하고 값을 비교해 로그에 남긴다.
    internal static class ZeroTween
    {
        internal static bool Enabled = true;
        internal static int SampleEvery = Edition.Dev ? 64 : 0;   // 이만큼에 한 번은 원래대로 처리해 비교한다 (0 = 안 함)
        internal static bool Patched;

        internal static long Fast, Checked, Mismatch, NoCallback;
        internal static string FirstMismatch = "";
        private static long counter;

        internal static void Install(Harmony h)
        {
            MethodBase start = null;
            foreach (var m in typeof(ffxMoveDecorationsPlus).GetMethods(AccessTools.all))
                if (m.Name == "StartEffect" && m.DeclaringType == typeof(ffxMoveDecorationsPlus) && !m.IsAbstract) start = m;
            if (start == null) return;
            var em = AccessTools.TypeByName("DG.Tweening.Core.Easing.EaseManager");
            var ev = em == null ? null : AccessTools.Method(em, "Evaluate", new[] { typeof(Ease), typeof(EaseFunction), typeof(float), typeof(float), typeof(float), typeof(float) });
            if (ev == null) { Main.Entry.Logger.Log("[즉시 이동] 이징 함수를 못 찾아 적용 안 함"); return; }
            easeEval = (EvalFn)Delegate.CreateDelegate(typeof(EvalFn), ev);
            try { activeRef = AccessTools.FieldRefAccess<Tween, bool>("<active>k__BackingField"); }
            catch { activeRef = null; }
            if (activeRef == null) { Main.Entry.Logger.Log("[즉시 이동] 대역을 만들 수 없어 적용 안 함"); return; }
            h.Patch(start, transpiler: new HarmonyMethod(typeof(ZeroTween), nameof(Transpiler)));
            Main.Entry.Logger.Log("[즉시 이동] 설치" + (Patched ? "" : " - 모양이 달라 적용 안 함"));
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

        // ── 이징 ─────────────────────────────────────────────────────
        private delegate float EvalFn(Ease ease, EaseFunction custom, float time, float duration, float overshoot, float period);
        private static EvalFn easeEval;
        // active 는 자동 속성이라 뒷 필드를 직접 다룬다. MethodInfo.Invoke 로 켜고 끄면 즉시 이동 하나에 리플렉션이 두 번 들어가서
        // (한 프레임 4만 번) 아끼는 것보다 더 비쌌다. 실제로 A/B 에서 끈 쪽 147ms, 켠 쪽 176ms 로 뒤집혔다.
        private static AccessTools.FieldRef<Tween, bool> activeRef;
        private static readonly AccessTools.FieldRef<Tween, Ease> easeTypeRef = AccessTools.FieldRefAccess<Tween, Ease>("easeType");
        private static readonly AccessTools.FieldRef<Tween, EaseFunction> customEaseRef = AccessTools.FieldRefAccess<Tween, EaseFunction>("customEase");
        private static readonly AccessTools.FieldRef<Tween, float> overshootRef = AccessTools.FieldRefAccess<Tween, float>("easeOvershootOrAmplitude");
        private static readonly AccessTools.FieldRef<Tween, float> periodRef = AccessTools.FieldRefAccess<Tween, float>("easePeriod");
        // 같은 이징이면 끝점 값도 같다. 즉시 이동마다 부르는 곳이라 마지막 것을 기억해 둔다(맵은 보통 한두 가지만 쓴다).
        private static Ease lastEase = (Ease)(-1); private static float lastOver, lastPeriod, lastK;
        private static float EaseAtEnd(Tween t)
        {
            var e = easeTypeRef(t); float ov = overshootRef(t), pe = periodRef(t);
            var custom = customEaseRef(t);
            if (custom == null && e == lastEase && ov == lastOver && pe == lastPeriod) return lastK;
            float k = easeEval(e, custom, 1f, 1f, ov, pe);
            if (custom == null) { lastEase = e; lastOver = ov; lastPeriod = pe; lastK = k; }
            return k;
        }

        // DOTween 플러그인과 같은 계산. 변화량은 float 로 한 번 반올림해 둔다(DOTween 은 changeValue 필드에 저장한다).
        private static float Calc(float s, float e, float k) { float ch = e - s; float m = ch * k; return s + m; }
        private static Vector2 Calc(Vector2 s, Vector2 e, float k, VectorOptions o)
        {
            Vector2 ch = e - s, r;
            switch (o.axisConstraint)
            {
                case AxisConstraint.X: r = s; r.x = s.x + ch.x * k; if (o.snapping) r.x = Mathf.Round(r.x); break;
                case AxisConstraint.Y: r = s; r.y = s.y + ch.y * k; if (o.snapping) r.y = Mathf.Round(r.y); break;
                default: r = s + ch * k; if (o.snapping) { r.x = Mathf.Round(r.x); r.y = Mathf.Round(r.y); } break;
            }
            return r;
        }
        private static Color Calc(Color s, Color e, float k) { Color ch = e - s; return s + ch * k; }

        // ── 대역 ─────────────────────────────────────────────────────
        private static TweenerCore<float, float, FloatOptions> pF;
        private static TweenerCore<Vector2, Vector2, VectorOptions> pV;
        private static TweenerCore<Color, Color, ColorOptions> pC;
        private static DOGetter<float> gF; private static DOSetter<float> sF; private static float eF;
        private static DOGetter<Vector2> gV; private static DOSetter<Vector2> sV; private static Vector2 eV;
        private static DOGetter<Color> gC; private static DOSetter<Color> sC; private static Color eC;

        private static T Proxy<T>(ref T p) where T : Tween
        {
            if (p == null) p = AccessTools.CreateInstance<T>();
            p.onUpdate = null; p.onComplete = null;
            easeTypeRef(p) = DOTween.defaultEaseType;
            customEaseRef(p) = null;
            overshootRef(p) = DOTween.defaultEaseOvershootOrAmplitude;
            periodRef(p) = DOTween.defaultEasePeriod;
            activeRef(p) = true;
            return p;
        }

        private static bool UseFast(float dur)
        {
            if (!Enabled || dur > 0f) return false;
            counter++;
            return SampleEvery <= 0 || counter % SampleEvery != 0;
        }

        public static TweenerCore<float, float, FloatOptions> ToF(DOGetter<float> g, DOSetter<float> s, float end, float dur)
        {
            if (!UseFast(dur)) return Remember(DOTween.To(g, s, end, dur), dur, () => g(), end);
            var p = Proxy(ref pF);
            p.plugOptions = default(FloatOptions);
            gF = g; sF = s; eF = end;
            return p;
        }
        public static TweenerCore<Vector2, Vector2, VectorOptions> ToV(DOGetter<Vector2> g, DOSetter<Vector2> s, Vector2 end, float dur)
        {
            if (!UseFast(dur)) return Remember(DOTween.To(g, s, end, dur), dur, () => g(), end);
            var p = Proxy(ref pV);
            p.plugOptions = default(VectorOptions);
            gV = g; sV = s; eV = end;
            return p;
        }
        public static TweenerCore<Color, Color, ColorOptions> ToC(DOGetter<Color> g, DOSetter<Color> s, Color end, float dur)
        {
            if (!UseFast(dur)) return Remember(DOTween.To(g, s, end, dur), dur, () => g(), end);
            var p = Proxy(ref pC);
            p.plugOptions = default(ColorOptions);
            gC = g; sC = s; eC = end;
            return p;
        }

        // ── Done ─────────────────────────────────────────────────────
        public static TweenerCore<float, float, FloatOptions> DoneF(TweenerCore<float, float, FloatOptions> t)
        {
            if (t != null && ReferenceEquals(t, pF) && t.active) { Finish(t, 0); return t; }
            var c = Pre(t); var r = t.Done(); Post(c); return r;
        }
        public static TweenerCore<Vector2, Vector2, VectorOptions> DoneV(TweenerCore<Vector2, Vector2, VectorOptions> t)
        {
            if (t != null && ReferenceEquals(t, pV) && t.active) { Finish(t, 1); return t; }
            var c = Pre(t); var r = t.Done(); Post(c); return r;
        }
        public static TweenerCore<Color, Color, ColorOptions> DoneC(TweenerCore<Color, Color, ColorOptions> t)
        {
            if (t != null && ReferenceEquals(t, pC) && t.active) { Finish(t, 2); return t; }
            var c = Pre(t); var r = t.Done(); Post(c); return r;
        }
        public static Tweener DoneT(Tweener t)
        {
            if (t != null && ReferenceEquals(t, pV) && t.active) { Finish(pV, 1); return t; }
            var c = Pre(t); var r = t.Done(); Post(c); return r;
        }

        // DOTween 의 Complete 와 같은 순서: 값 넣기 -> OnUpdate -> OnComplete. 대역은 콜백 전에 끈다(콜백 안에서 또 쓸 수 있게).
        private static void Finish(Tween t, int kind)
        {
            var onUpdate = t.onUpdate; var onComplete = t.onComplete;
            if (onUpdate == null && onComplete == null) NoCallback++;
            bool prof = Edition.Dev && (Fast % 64) == 0;
            long t0 = prof ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            try
            {
                float k = EaseAtEnd(t);
                if (kind == 0) sF(Calc(gF(), eF, k));
                else if (kind == 1) sV(Calc(gV(), eV, k, pV.plugOptions));
                else sC(Calc(gC(), eC, k));
            }
            catch (Exception ex) { Log(ex); }
            long t1 = prof ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            t.onUpdate = null; t.onComplete = null;
            gF = null; sF = null; gV = null; sV = null; gC = null; sC = null;
            activeRef(t) = false;
            Fast++;
            if (prof) { Note(onUpdate, true); Note(onComplete, false); }
            if (onUpdate != null) { try { onUpdate(); } catch (Exception ex) { Log(ex); } }
            long t2 = prof ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (onComplete != null) { try { onComplete(); } catch (Exception ex) { Log(ex); } }
            if (prof)
            {
                double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                ProfN++; ProfSet += (t1 - t0) * f; ProfUpdate += (t2 - t1) * f; ProfComplete += (System.Diagnostics.Stopwatch.GetTimestamp() - t2) * f;
            }
        }

        // ── (개발자용) 남은 비용 쪼개기: 값 넣기 / OnUpdate / OnComplete 에 각각 얼마나 쓰는지, 같은 콜백이 얼마나 반복되는지 ──
        internal static long ProfN;
        internal static double ProfSet, ProfUpdate, ProfComplete;
        private static readonly Dictionary<string, long> cbCount = new Dictionary<string, long>();
        private static long dupUpdate;
        private static string lastCb = "";
        private static object lastCbTarget;

        private static void Note(TweenCallback cb, bool isUpdate)
        {
            if (cb == null) return;
            try
            {
                string key = cb.Method.DeclaringType != null ? cb.Method.DeclaringType.Name + "." + cb.Method.Name : cb.Method.Name;
                long n; cbCount.TryGetValue(key, out n); cbCount[key] = n + 1;
                if (isUpdate)
                {
                    if (key == lastCb && ReferenceEquals(cb.Target, lastCbTarget)) dupUpdate++;
                    lastCb = key; lastCbTarget = cb.Target;
                }
            }
            catch { }
        }

        internal static string Profile()
        {
            if (ProfN == 0) return "";
            var top = new List<string>();
            foreach (var kv in cbCount) top.Add(kv.Key + " " + kv.Value);
            top.Sort((a, b) => b.Length.CompareTo(a.Length));
            return string.Format(" || 표본 {0}개 평균: 값 넣기 {1:F2}us, OnUpdate {2:F2}us, OnComplete {3:F2}us | 바로 앞과 같은 OnUpdate {4}회 | 콜백 종류: {5}",
                ProfN, ProfSet * 1000 / ProfN, ProfUpdate * 1000 / ProfN, ProfComplete * 1000 / ProfN, dupUpdate, string.Join(", ", top.ToArray()));
        }

        private static int logged;
        private static void Log(Exception ex) { if (logged++ < 5) Main.Entry.Logger.Log("[즉시 이동] 오류 (DOTween 안전 모드처럼 넘어감): " + ex.Message); }

        // ── (개발자용) 비교: 표본은 원래 DOTween 으로 처리하고, 우리 계산과 결과를 비교한다 ─────────
        private class Info { public Func<object> Get; public object End; }
        private static readonly Dictionary<Tween, Info> sample = new Dictionary<Tween, Info>();
        private class Case { public Info Info; public object Pred; public string Kind; }

        private static T Remember<T>(T t, float dur, Func<object> get, object end) where T : Tween
        {
            if (dur <= 0f && t != null && SampleEvery > 0) sample[t] = new Info { Get = get, End = end };
            return t;
        }

        private static Case Pre(Tween t)
        {
            Info info;
            if (t == null || !sample.TryGetValue(t, out info)) return null;
            sample.Remove(t);
            try
            {
                object s = info.Get(), pred;
                float k = EaseAtEnd(t);
                if (s is float) pred = Calc((float)s, (float)info.End, k);
                else if (s is Color) pred = Calc((Color)s, (Color)info.End, k);
                else
                {
                    var tc = t as TweenerCore<Vector2, Vector2, VectorOptions>;
                    pred = Calc((Vector2)s, (Vector2)info.End, k, tc != null ? tc.plugOptions : default(VectorOptions));
                }
                return new Case { Info = info, Pred = pred, Kind = s.GetType().Name + "/" + easeTypeRef(t) };
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
                if (!Same(actual, c.Pred))
                {
                    Mismatch++;
                    if (FirstMismatch.Length == 0) FirstMismatch = c.Kind + " 실제 " + Show(actual) + ", 계산 " + Show(c.Pred);
                }
            }
            catch { }
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
            return string.Format("애니메이션 없이 처리 {0}개 | 표본 비교 {1}개 중 다름 {2}{3} | 콜백 없는 것 {4}",
                Fast, Checked, Mismatch, Mismatch > 0 ? " (예: " + FirstMismatch + ")" : "", NoCallback) + Profile();
        }

        internal static void Reset() { ProfN = 0; ProfSet = ProfUpdate = ProfComplete = 0; dupUpdate = 0; cbCount.Clear(); Fast = Checked = Mismatch = NoCallback = 0; FirstMismatch = ""; sample.Clear(); }
    }
}
