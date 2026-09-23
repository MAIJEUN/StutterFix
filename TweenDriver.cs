using System;
using System.Collections.Generic;
using System.Reflection;
using DG.Tweening;
using DG.Tweening.Core;
using DG.Tweening.Core.Enums;
using DG.Tweening.Plugins.Core;
using DG.Tweening.Plugins.Options;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 장식 이동 애니메이션의 "중간 프레임" 을 DOTween 대신 가벼운 목록에서 돌린다.
    //
    // 측정 (Arche 50초 구간, 보이는 장식 수천 개가 동시에 움직임): 메인 스레드 19ms 중 DOTween 갱신이 가장 큰 덩어리.
    // 게임 쪽 콜백(값 적용)은 여러 번 깎았고, 남은 것은 DOTween 이 애니메이션마다 하는 일(가상 호출, 상태 검사, 안전 모드 래퍼)이다.
    //
    // DOTween(게임판) 을 IL 로 따라가 보면 길이 있는 애니메이션 한 프레임은:
    //   d = Δt(또는 독립 시간) x timeScale, |d| < 1e-6 이면 건너뜀 -> 시작 전이면 Startup -> 위치 += d
    //   -> 끝에 닿으면 완료 처리(값, OnUpdate, OnComplete, 정리), 아니면 위치만 바꾸고 값 넣기 -> OnUpdate.
    //   값 = 시작값 + 변화량 x Ease(위치, 길이) (Vector2 는 축 고정이면 현재 값에서 그 축만, Color 는 알파만 옵션이면 알파만).
    // 그래서 역할을 나눈다:
    //   첫 프레임(Startup: 시작값 읽기)과 끝나는 프레임(완료, OnComplete, 정리)은 DOTween 이 그대로 한다.
    //   그 사이 프레임만 모드가 한다: 애니메이션을 DOTween 목록에서 멈춤(isPlaying=false)으로 두고, TweenManager.Update 앞에서
    //   같은 Δt 로 위치를 올리고 같은 식으로 값을 넣고 OnUpdate 를 부른다. 다음 걸음에 끝에 닿으면 isPlaying 을 되돌려
    //   바로 이어지는 DOTween 루프가 자기 계산으로 끝낸다.
    // 누가 애니메이션을 건드리면(Kill, 풀에서 재사용, Goto, 다시 재생) 바로 DOTween 에 돌려준다(위치/재생 상태/setter 가 달라짐으로 안다).
    // 일시정지는 Time.timeScale=0 이라 Δt 가 0 이 되어 DOTween 처럼 건너뛴다.
    //
    // 검증 (개발자용): 16프레임에 한 번, 모드가 넣은 값과 같은 순간 DOTween 플러그인이 계산한 값을 비트 단위로 비교한다.
    // 또 16개 중 1개는 DOTween 에 맡겨 두고, 모드가 예측한 다음 위치와 DOTween 이 실제로 만든 위치를 비교한다.
    internal static class TweenDriver
    {
        internal static bool Enabled = true;
        internal static long Registered, TakenOver, Driven, HandedBack, Released, Errors;
        internal static long ValueChecked, ValueMismatch, TimeChecked, TimeMismatch;
        internal static string FirstMismatch = "";
        internal static int PeakDriven;

        private static readonly AccessTools.FieldRef<Tween, float> posRef = AccessTools.FieldRefAccess<Tween, float>("<position>k__BackingField");
        private static readonly AccessTools.FieldRef<Tween, bool> playingRef = AccessTools.FieldRefAccess<Tween, bool>("isPlaying");
        private static readonly AccessTools.FieldRef<Tween, bool> startupRef = AccessTools.FieldRefAccess<Tween, bool>("startupDone");
        private static readonly AccessTools.FieldRef<Tween, bool> indepRef = AccessTools.FieldRefAccess<Tween, bool>("isIndependentUpdate");
        private static readonly AccessTools.FieldRef<Tween, bool> activeRef = AccessTools.FieldRefAccess<Tween, bool>("<active>k__BackingField");
        private static readonly AccessTools.FieldRef<Tween, bool> delayDoneRef = AccessTools.FieldRefAccess<Tween, bool>("delayComplete");
        private static readonly AccessTools.FieldRef<Tween, bool> backRef = AccessTools.FieldRefAccess<Tween, bool>("isBackwards");
        private static readonly AccessTools.FieldRef<Tween, bool> invRef = AccessTools.FieldRefAccess<Tween, bool>("isInverted");
        private static readonly AccessTools.FieldRef<Tween, bool> seqRef = AccessTools.FieldRefAccess<Tween, bool>("isSequenced");
        private static readonly AccessTools.FieldRef<Tween, bool> completeRef = AccessTools.FieldRefAccess<Tween, bool>("isComplete");
        private static readonly AccessTools.FieldRef<Tween, int> loopsRef = AccessTools.FieldRefAccess<Tween, int>("loops");
        private static readonly AccessTools.FieldRef<Tween, float> durRef = AccessTools.FieldRefAccess<Tween, float>("duration");
        private static readonly AccessTools.FieldRef<Tween, float> tsRef = AccessTools.FieldRefAccess<Tween, float>("timeScale");
        private static readonly AccessTools.FieldRef<Tween, Ease> easeRef = AccessTools.FieldRefAccess<Tween, Ease>("easeType");
        private static readonly AccessTools.FieldRef<Tween, EaseFunction> customRef = AccessTools.FieldRefAccess<Tween, EaseFunction>("customEase");
        private static readonly AccessTools.FieldRef<Tween, float> overRef = AccessTools.FieldRefAccess<Tween, float>("easeOvershootOrAmplitude");
        private static readonly AccessTools.FieldRef<Tween, float> periodRef = AccessTools.FieldRefAccess<Tween, float>("easePeriod");
        private static readonly AccessTools.FieldRef<Tween, bool> relRef = AccessTools.FieldRefAccess<Tween, bool>("<isRelative>k__BackingField");

        private delegate float EvalFn(Ease ease, EaseFunction custom, float time, float duration, float overshoot, float period);
        private static EvalFn easeEval;
        private static AccessTools.FieldRef<bool> isUpdateLoop;
        private static bool ready;

        private sealed class EF { public TweenerCore<float, float, FloatOptions> T; public DOSetter<float> S; public float Pos; public bool Shadow; public float Expect; }
        private sealed class EV { public TweenerCore<Vector2, Vector2, VectorOptions> T; public DOSetter<Vector2> S; public float Pos; public bool Shadow; public float Expect; }
        private sealed class EC { public TweenerCore<Color, Color, ColorOptions> T; public DOSetter<Color> S; public float Pos; public bool Shadow; public float Expect; }

        private static readonly List<EF> pendF = new List<EF>(), runF = new List<EF>();
        private static readonly List<EV> pendV = new List<EV>(), runV = new List<EV>();
        private static readonly List<EC> pendC = new List<EC>(), runC = new List<EC>();
        private static readonly List<EF> shF = new List<EF>();
        private static readonly List<EV> shV = new List<EV>();
        private static readonly List<EC> shC = new List<EC>();
        private static long regCounter, frameCounter;

        internal static void Install(Harmony h)
        {
            try
            {
                var em = AccessTools.TypeByName("DG.Tweening.Core.Easing.EaseManager");
                var ev = em == null ? null : AccessTools.Method(em, "Evaluate", new[] { typeof(Ease), typeof(EaseFunction), typeof(float), typeof(float), typeof(float), typeof(float) });
                var tm = AccessTools.TypeByName("DG.Tweening.Core.TweenManager");
                var upd = tm == null ? null : AccessTools.Method(tm, "Update", new[] { typeof(UpdateType), typeof(float), typeof(float) });
                if (ev == null || upd == null) { Main.Entry.Logger.Log("[애니메이션 직접 진행] 대상 없음 - 적용 안 함"); return; }
                easeEval = (EvalFn)Delegate.CreateDelegate(typeof(EvalFn), ev);
                isUpdateLoop = AccessTools.StaticFieldRefAccess<bool>(AccessTools.Field(tm, "isUpdateLoop"));
                h.Patch(upd, prefix: new HarmonyMethod(typeof(TweenDriver), nameof(Before)), postfix: new HarmonyMethod(typeof(TweenDriver), nameof(After)));
                ready = true;
                Main.Entry.Logger.Log("[애니메이션 직접 진행] 설치");
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[애니메이션 직접 진행] 설치 실패: " + ex.Message); }
        }

        // 조건: 반복 없음, 앞으로, 지연 없음, 묶음(시퀀스) 아님. 장식 이동이 만드는 애니메이션은 모두 이 모양이다.
        private static bool Simple(Tween t)
        {
            return activeRef(t) && loopsRef(t) == 1 && !backRef(t) && !invRef(t) && !seqRef(t) && durRef(t) > 0f;
        }

        private static bool TakeShadow() { return Edition.Dev && (++regCounter % 16) == 0; }

        internal static void Register(TweenerCore<float, float, FloatOptions> t)
        {
            if (!ready || !Enabled || t == null || !Simple(t)) return;
            Registered++;
            var e = new EF { T = t, S = t.setter };
            if (TakeShadow()) { e.Shadow = true; shF.Add(e); } else pendF.Add(e);
        }
        internal static void Register(TweenerCore<Vector2, Vector2, VectorOptions> t)
        {
            if (!ready || !Enabled || t == null || !Simple(t)) return;
            Registered++;
            var e = new EV { T = t, S = t.setter };
            if (TakeShadow()) { e.Shadow = true; shV.Add(e); } else pendV.Add(e);
        }
        internal static void Register(TweenerCore<Color, Color, ColorOptions> t)
        {
            if (!ready || !Enabled || t == null || !Simple(t)) return;
            Registered++;
            var e = new EC { T = t, S = t.setter };
            if (TakeShadow()) { e.Shadow = true; shC.Add(e); } else pendC.Add(e);
        }

        // 아직 우리 것인가: 살아 있고, 풀에서 재사용되지 않았고(setter 가 같다), 누가 다시 재생하거나 옮기지 않았다.
        private static bool Still(Tween t, Delegate savedSetter, Delegate setter, float pos)
        {
            return activeRef(t) && ReferenceEquals(setter, savedSetter) && !playingRef(t) && posRef(t) == pos && !completeRef(t);
        }

        // 넘겨받기: DOTween 이 첫 프레임(Startup)을 끝낸 뒤 멈춤으로 돌리고 우리 목록으로 옮긴다
        private static void Take<TE>(List<TE> pend, List<TE> run, Func<TE, Tween> tw, Func<TE, Delegate> saved, Func<TE, Delegate> cur, Action<TE, float> setPos) where TE : class
        {
            if (pend.Count == 0) return;
            int w = 0;
            for (int i = 0; i < pend.Count; i++)
            {
                var e = pend[i]; var t = tw(e);
                if (!activeRef(t) || !ReferenceEquals(saved(e), cur(e)) || completeRef(t)) continue;   // 이미 끝났거나 죽었다
                if (!startupRef(t) || !playingRef(t)) { pend[w++] = e; continue; }                     // 아직 DOTween 이 한 번도 안 돌렸다
                if (!delayDoneRef(t)) continue;
                playingRef(t) = false;
                setPos(e, posRef(t));
                run.Add(e);
                TakenOver++;
            }
            pend.RemoveRange(w, pend.Count - w);
        }

        public static void Before(UpdateType updateType, float deltaTime, float independentTime)
        {
            if (!ready || updateType != UpdateType.Normal) return;
            if (!Enabled || !Hitch.Playing) { ReleaseAll(); return; }
            bool prev = isUpdateLoop();
            isUpdateLoop() = true;   // DOTween 루프 안처럼: 콜백 안의 Kill 은 표시만 하고 목록은 그대로 둔다
            try
            {
                Take(pendF, runF, e => e.T, e => e.S, e => e.T.setter, (e, p) => e.Pos = p);
                Take(pendV, runV, e => e.T, e => e.S, e => e.T.setter, (e, p) => e.Pos = p);
                Take(pendC, runC, e => e.T, e => e.S, e => e.T.setter, (e, p) => e.Pos = p);
                bool check = Edition.Dev && (++frameCounter % 16) == 0;
                DriveF(deltaTime, independentTime, check);
                DriveV(deltaTime, independentTime, check);
                DriveC(deltaTime, independentTime, check);
                int n = runF.Count + runV.Count + runC.Count;
                if (n > PeakDriven) PeakDriven = n;
                if (Edition.Dev) ExpectShadows(deltaTime, independentTime);
            }
            finally { isUpdateLoop() = prev; }
        }

        // 한 걸음: 끝에 닿으면 DOTween 에 돌려주고(true), 아니면 위치를 올린다. 반환: 0 = 계속, 1 = 돌려줌/놓음, 2 = 이번 프레임은 쉼
        private static int Step(Tween t, float dt, float idt, ref float pos)
        {
            float d = (indepRef(t) ? idt : dt) * tsRef(t);
            if (d < 1E-06f && d > -1E-06f) return 2;
            float to = pos + d;
            if (to >= durRef(t)) { playingRef(t) = true; HandedBack++; return 1; }   // 바로 이어지는 DOTween 루프가 같은 계산으로 끝낸다
            posRef(t) = to; pos = to;
            return 0;
        }

        private static float K(Tween t, float pos) { return easeEval(easeRef(t), customRef(t), pos, durRef(t), overRef(t), periodRef(t)); }

        private static void Callback(Tween t)
        {
            var cb = t.onUpdate;
            if (cb == null) return;
            try { cb(); } catch (Exception ex) { if (Errors++ < 5) Main.Entry.Logger.Log("[애니메이션 직접 진행] OnUpdate 오류 (DOTween 안전 모드처럼 넘어감): " + ex.Message); }
        }

        private static void DriveF(float dt, float idt, bool check)
        {
            int w = 0;
            for (int i = 0; i < runF.Count; i++)
            {
                var e = runF[i]; var t = e.T;
                if (!Still(t, e.S, t.setter, e.Pos)) { Released++; continue; }
                int r = Step(t, dt, idt, ref e.Pos);
                if (r == 1) continue;
                runF[w++] = e;
                if (r == 2) continue;
                float k = K(t, e.Pos);
                float v = t.startValue + t.changeValue * k;
                if (t.plugOptions.snapping) v = (float)Math.Round(v);
                if (check) CheckF(t, e.Pos, v);
                try { t.setter(v); } catch (Exception ex) { Fail(t, ex); continue; }
                Driven++;
                Callback(t);
            }
            runF.RemoveRange(w, runF.Count - w);
        }

        private static void DriveV(float dt, float idt, bool check)
        {
            int w = 0;
            for (int i = 0; i < runV.Count; i++)
            {
                var e = runV[i]; var t = e.T;
                if (!Still(t, e.S, t.setter, e.Pos)) { Released++; continue; }
                int r = Step(t, dt, idt, ref e.Pos);
                if (r == 1) continue;
                runV[w++] = e;
                if (r == 2) continue;
                float k = K(t, e.Pos);
                Vector2 s = t.startValue, c = t.changeValue, v;
                var o = t.plugOptions;
                try
                {
                    switch (o.axisConstraint)
                    {
                        case AxisConstraint.X: v = t.getter(); v.x = s.x + c.x * k; if (o.snapping) v.x = (float)Math.Round(v.x); break;
                        case AxisConstraint.Y: v = t.getter(); v.y = s.y + c.y * k; if (o.snapping) v.y = (float)Math.Round(v.y); break;
                        default: v = s; v.x += c.x * k; v.y += c.y * k; if (o.snapping) { v.x = (float)Math.Round(v.x); v.y = (float)Math.Round(v.y); } break;
                    }
                    if (check) CheckV(t, e.Pos, v);
                    t.setter(v);
                }
                catch (Exception ex) { Fail(t, ex); continue; }
                Driven++;
                Callback(t);
            }
            runV.RemoveRange(w, runV.Count - w);
        }

        private static void DriveC(float dt, float idt, bool check)
        {
            int w = 0;
            for (int i = 0; i < runC.Count; i++)
            {
                var e = runC[i]; var t = e.T;
                if (!Still(t, e.S, t.setter, e.Pos)) { Released++; continue; }
                int r = Step(t, dt, idt, ref e.Pos);
                if (r == 1) continue;
                runC[w++] = e;
                if (r == 2) continue;
                float k = K(t, e.Pos);
                Color s = t.startValue, c = t.changeValue, v;
                try
                {
                    if (t.plugOptions.alphaOnly) { v = t.getter(); v.a = s.a + c.a * k; }
                    else { v = s; v.r += c.r * k; v.g += c.g * k; v.b += c.b * k; v.a += c.a * k; }
                    if (check) CheckC(t, e.Pos, v);
                    t.setter(v);
                }
                catch (Exception ex) { Fail(t, ex); continue; }
                Driven++;
                Callback(t);
            }
            runC.RemoveRange(w, runC.Count - w);
        }

        // 값 넣기에서 예외가 나면 DOTween 에 돌려준다(DOTween 은 같은 예외로 그 애니메이션을 죽인다). 이 경우 목록에는 남지만
        // 다음 프레임 Still 검사에서 playing 이라 빠진다.
        private static void Fail(Tween t, Exception ex)
        {
            playingRef(t) = true;
            if (Errors++ < 5) Main.Entry.Logger.Log("[애니메이션 직접 진행] 값 넣기 오류, DOTween 에 돌려줌: " + ex.Message);
        }

        // 모드를 끄거나 곡이 끝나면 전부 DOTween 에 돌려준다(멈춤을 풀어 원래대로 흐르게)
        internal static void ReleaseAll()
        {
            Give(runF, e => e.T, e => e.S, e => e.T.setter, e => e.Pos);
            Give(runV, e => e.T, e => e.S, e => e.T.setter, e => e.Pos);
            Give(runC, e => e.T, e => e.S, e => e.T.setter, e => e.Pos);
            pendF.Clear(); pendV.Clear(); pendC.Clear(); shF.Clear(); shV.Clear(); shC.Clear();
        }

        private static void Give<TE>(List<TE> run, Func<TE, Tween> tw, Func<TE, Delegate> saved, Func<TE, Delegate> cur, Func<TE, float> pos)
        {
            foreach (var e in run)
            {
                var t = tw(e);
                if (Still(t, saved(e), cur(e), pos(e))) playingRef(t) = true;
            }
            run.Clear();
        }

        // ── 개발자용 검증 ─────────────────────────────────────────────
        private static readonly AccessTools.FieldRef<TweenerCore<float, float, FloatOptions>, ABSTweenPlugin<float, float, FloatOptions>> plugF =
            AccessTools.FieldRefAccess<TweenerCore<float, float, FloatOptions>, ABSTweenPlugin<float, float, FloatOptions>>("tweenPlugin");
        private static readonly AccessTools.FieldRef<TweenerCore<Vector2, Vector2, VectorOptions>, ABSTweenPlugin<Vector2, Vector2, VectorOptions>> plugV =
            AccessTools.FieldRefAccess<TweenerCore<Vector2, Vector2, VectorOptions>, ABSTweenPlugin<Vector2, Vector2, VectorOptions>>("tweenPlugin");
        private static readonly AccessTools.FieldRef<TweenerCore<Color, Color, ColorOptions>, ABSTweenPlugin<Color, Color, ColorOptions>> plugC =
            AccessTools.FieldRefAccess<TweenerCore<Color, Color, ColorOptions>, ABSTweenPlugin<Color, Color, ColorOptions>>("tweenPlugin");
        private static float capF; private static Vector2 capV; private static Color capC;
        private static readonly DOSetter<float> captureF = x => capF = x;
        private static readonly DOSetter<Vector2> captureV = x => capV = x;
        private static readonly DOSetter<Color> captureC = x => capC = x;

        private static void CheckF(TweenerCore<float, float, FloatOptions> t, float pos, float mine)
        {
            try
            {
                plugF(t).EvaluateAndApply(t.plugOptions, t, relRef(t), t.getter, captureF, pos, t.startValue, t.changeValue, durRef(t), false, 0, UpdateNotice.None);
                ValueChecked++;
                if (BitConverter.ToInt32(BitConverter.GetBytes(capF), 0) != BitConverter.ToInt32(BitConverter.GetBytes(mine), 0)) Miss("float", capF.ToString("R"), mine.ToString("R"));
            }
            catch { }
        }
        private static void CheckV(TweenerCore<Vector2, Vector2, VectorOptions> t, float pos, Vector2 mine)
        {
            try
            {
                plugV(t).EvaluateAndApply(t.plugOptions, t, relRef(t), t.getter, captureV, pos, t.startValue, t.changeValue, durRef(t), false, 0, UpdateNotice.None);
                ValueChecked++;
                if (!Same(capV.x, mine.x) || !Same(capV.y, mine.y)) Miss("Vector2/" + t.plugOptions.axisConstraint, capV.ToString("R"), mine.ToString("R"));
            }
            catch { }
        }
        private static void CheckC(TweenerCore<Color, Color, ColorOptions> t, float pos, Color mine)
        {
            try
            {
                plugC(t).EvaluateAndApply(t.plugOptions, t, relRef(t), t.getter, captureC, pos, t.startValue, t.changeValue, durRef(t), false, 0, UpdateNotice.None);
                ValueChecked++;
                if (!Same(capC.r, mine.r) || !Same(capC.g, mine.g) || !Same(capC.b, mine.b) || !Same(capC.a, mine.a)) Miss("Color", capC.ToString("R"), mine.ToString("R"));
            }
            catch { }
        }
        private static bool Same(float a, float b) { return BitConverter.ToInt32(BitConverter.GetBytes(a), 0) == BitConverter.ToInt32(BitConverter.GetBytes(b), 0); }
        private static void Miss(string kind, string dotween, string mine)
        {
            ValueMismatch++;
            if (FirstMismatch.Length < 300) FirstMismatch += " [" + kind + " DOTween " + dotween + ", 모드 " + mine + "]";
        }

        // 시간 검증: DOTween 에 맡겨 둔 표본은 DOTween 루프 전에 "다음 위치" 를 우리 식으로 예측해 두고, 루프 뒤에 실제와 비교한다
        private static void ExpectShadows(float dt, float idt)
        {
            Expect(shF, e => e.T, (e, v) => e.Expect = v, dt, idt);
            Expect(shV, e => e.T, (e, v) => e.Expect = v, dt, idt);
            Expect(shC, e => e.T, (e, v) => e.Expect = v, dt, idt);
        }
        private static void Expect<TE>(List<TE> sh, Func<TE, Tween> tw, Action<TE, float> set, float dt, float idt)
        {
            int w = 0;
            for (int i = 0; i < sh.Count; i++)
            {
                var e = sh[i]; var t = tw(e);
                if (!activeRef(t) || completeRef(t)) continue;
                sh[w++] = e;
                if (!startupRef(t) || !playingRef(t)) { set(e, float.NaN); continue; }
                float d = (indepRef(t) ? idt : dt) * tsRef(t);
                float to = posRef(t) + d;
                set(e, (d < 1E-06f && d > -1E-06f) ? posRef(t) : (to >= durRef(t) ? float.NaN : to));
            }
            sh.RemoveRange(w, sh.Count - w);
        }

        public static void After(UpdateType updateType)
        {
            if (!ready || !Edition.Dev || updateType != UpdateType.Normal) return;
            Compare(shF, e => e.T, e => e.Expect);
            Compare(shV, e => e.T, e => e.Expect);
            Compare(shC, e => e.T, e => e.Expect);
        }
        private static void Compare<TE>(List<TE> sh, Func<TE, Tween> tw, Func<TE, float> exp)
        {
            foreach (var e in sh)
            {
                float x = exp(e);
                if (float.IsNaN(x)) continue;
                var t = tw(e);
                if (!activeRef(t)) continue;
                TimeChecked++;
                if (!Same(posRef(t), x)) { TimeMismatch++; if (FirstMismatch.Length < 300) FirstMismatch += " [시간 DOTween " + posRef(t).ToString("R") + ", 모드 " + x.ToString("R") + "]"; }
            }
        }

        internal static string Summary()
        {
            if (Registered == 0) return "";
            string s = string.Format(" | 애니메이션 직접 진행: 등록 {0}개, 넘겨받음 {1}개, 모드가 진행한 프레임 {2}번, 끝에서 DOTween 에 돌려줌 {3}번, 중간에 놓음 {4}번, 동시 최대 {5}개",
                Registered, TakenOver, Driven, HandedBack, Released, PeakDriven);
            if (Edition.Dev) s += string.Format(" | 검증: 값 {0}번 중 다름 {1}, 시간 {2}번 중 다름 {3}{4}", ValueChecked, ValueMismatch, TimeChecked, TimeMismatch, FirstMismatch);
            if (Errors > 0) s += " | 오류 " + Errors;
            return s;
        }

        internal static void ResetStats()
        {
            Registered = TakenOver = Driven = HandedBack = Released = Errors = 0;
            ValueChecked = ValueMismatch = TimeChecked = TimeMismatch = 0; FirstMismatch = ""; PeakDriven = 0;
        }
    }
}
