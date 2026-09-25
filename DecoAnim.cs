using System;
using System.Collections.Generic;
using DG.Tweening;
using DG.Tweening.Core;
using DG.Tweening.Plugins.Options;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 장식 애니메이션 직접 처리: 길이 있는 장식 이동 효과의 애니메이션을 DOTween 대신 모드가 돌린다.
    //
    // 측정 (측정용 플레이어 빌드, Arche 64초 부근 25ms 프레임): 애니메이션 갱신 7~7.5ms 중 장식 설정 함수가 3.7~4.0ms,
    // 나머지 약 3.5ms 가 DOTween 관리(목록 순회, 위치 계산, 플러그인, 델리게이트 두 번). 길이 있는 장식 이동은 한 판에
    // "위치X+위치Y+회전+크기X+크기Y" 약 4만 6천 효과(애니메이션 23만 개), "색" 1만 3천 효과(8만 개)가 대부분이다.
    //
    // 원래(IL 로 확인, 속성마다): 이전 것 Kill(true) -> 시작값을 클로저에 담음 -> DOTween.To(getter, setter, 끝값, 길이).SetEase(ease)
    //   [.SetOptions(축)].OnUpdate(설정 함수(클로저 값)).OnComplete(설정 함수(목표값)) -> 사전에 저장.
    //   위치: 시작 = 만들 때 pivotPosVec 의 그 축, OnUpdate/OnComplete = SetPositionX/Y(값, 부를 때의 pivotOffsetVec)
    //   회전/색/불투명도: 시작 = 만들 때 rotAngle/color/opacity, OnUpdate/OnComplete = SetRotation/SetColor/SetOpacity
    //   크기(X, Y 따로): getter = 지금 scaleVec (시작값은 첫 갱신 때 읽음), setter = SetScale(지금 scaleVec 에서 그 축만 바꾼 값), 콜백 없음
    // DOTween 의 진행(DOTween.dll IL 로 확인): 프레임마다 dt = Time.deltaTime(또는 smoothDeltaTime) x DOTween.timeScale,
    //   |dt| < 1e-6 이면 건너뜀. 첫 갱신 때 시작값·변화량(끝 - 시작, float). 위치 += dt (float), 끝 이상이면 끝으로.
    //   값 = 시작 + 변화량 x 이징(위치, 길이) -> setter -> OnUpdate -> (끝났으면) OnComplete. Kill(true) 는 끝 위치로 같은 순서.
    //   목록 순서(만든 순서)대로 갱신한다.
    // 여기서도 똑같이 한다. 게임 사전에는 "모드 애니메이션 표"(꺼진 DOTween 객체, id 에 기록)를 넣고, TweenExtensions.Kill 을
    // 가로채 표를 끊으면 이 기록을 DOTween 과 똑같이 완료한다. DOTween.KillAll 도 따라간다. 게임 일시정지는 Time.timeScale 이라 dt 로 따라온다.
    // 곡 시작 직후(곡 중간부터 시작할 때 되감기, ScrubToTime)는 만들지 않는다(원래 DOTween).
    //
    // 개발자용 검증: 64개 중 1개는 진짜 DOTween 애니메이션을 옆에 같이 돌려(값은 따로 받아 둠, 장식은 안 건드림) 매 프레임 두 값을
    // 비트 단위로 비교하고, 끝나는 프레임도 비교한다.
    internal static class DecoAnim
    {
        internal static bool Enabled = true;
        internal static bool Installed;
        internal static long Created, Completed, Killed, Dropped, Frames, VerifyN, VerifySteps, VerifyMismatch, Errors;
        // (측정용) 매 프레임 DOTween 갱신 앞에서 가장 먼저 돌아서 다른 측정(함수별, 애니메이션 갱신)에 안 잡혔다. 따로 잰다.
        internal static readonly bool Prof = Edition.Dev || Main.MeasureBuild;
        private static long profN;
        internal static readonly long[] ProfN = new long[16], ProfApply = new long[16], ProfSet = new long[16];
        internal static long Steps;
        internal static double LastFrameMs;   // 이번 프레임 모드 애니메이션 갱신 시간
        internal static int Peak;
        internal static double UpdateMs;
        internal static readonly long[] MismatchByKey = new long[16];
        internal static string First = "";

        internal sealed class Rec
        {
            public scrDecoration D; public int Key; public Tween Proxy;
            public float Dur, Pos; public bool Started, Running = true, Stepped;
            public Ease E; public float Over, Period;
            public float FStart, FEnd, FChange, FLast;          // 1,2 위치  5 회전  10 불투명도
            public Color CStart, CEnd, CChange, CLast;          // 9 색
            public Vector2 VStart, VEnd, VChange, VLast;        // 7 크기X  8 크기Y
            // 개발자용 짝 (진짜 DOTween)
            public Tween Shadow; public float SF; public Color SC; public Vector2 SV; public bool SDone, SStepped;
            public bool InList;   // recs 목록에 들어 있음 (다시 쓰면 두 번 진행되므로 목록에서 빠진 것만 다시 쓴다)
        }
        internal static long Reused;

        private static readonly List<Rec> recs = new List<Rec>();
        private static readonly AccessTools.FieldRef<scrDecoration, Vector2> pivotPosRef = AccessTools.FieldRefAccess<scrDecoration, Vector2>("pivotPosVec");
        private static readonly AccessTools.FieldRef<scrDecoration, Vector2> pivotOffRef = AccessTools.FieldRefAccess<scrDecoration, Vector2>("pivotOffsetVec");
        private static readonly AccessTools.FieldRef<scrDecoration, float> rotRef = AccessTools.FieldRefAccess<scrDecoration, float>("rotAngle");
        private static readonly AccessTools.FieldRef<scrDecoration, Color> colRef = AccessTools.FieldRefAccess<scrDecoration, Color>("color");
        private static readonly AccessTools.FieldRef<scrDecoration, float> opaRef = AccessTools.FieldRefAccess<scrDecoration, float>("opacity");
        private static readonly AccessTools.FieldRef<scrDecoration, Vector2> scaleRef = AccessTools.FieldRefAccess<scrDecoration, Vector2>("scaleVec");
        private delegate void SetXY(scrDecoration d, float v, Vector2 off);
        private static SetXY setPosX, setPosY;
        private static Action<scrDecoration, float> setRot, setOpa;
        private static Action<scrDecoration, Color> setCol;
        private static Action<scrDecoration, Vector2> setScale;
        // 피벗·시차 (원래 블록도 회전과 같은 모양: 시작값 = 만들 때 값, OnUpdate/OnComplete = 설정 함수)
        private static readonly AccessTools.FieldRef<scrDecoration, Vector2> parOffRef = AccessTools.FieldRefAccess<scrDecoration, Vector2>("parallaxOffset");
        private static readonly AccessTools.FieldRef<scrDecoration, scrParallax> parallaxRef = AccessTools.FieldRefAccess<scrDecoration, scrParallax>("parallax");
        private static Action<scrDecoration, float> setPivX, setPivY, setParOffX, setParOffY;
        internal static bool CanPivotParallax;

        internal static void Install(Harmony h)
        {
            try
            {
                if (!ZeroTween.CanEase) { Main.Entry.Logger.Log("[장식 애니메이션] 이징 함수가 없어 끔"); return; }
                var d = typeof(scrDecoration);
                setPosX = AccessTools.MethodDelegate<SetXY>(AccessTools.Method(d, "SetPositionX", new[] { typeof(float), typeof(Vector2) }));
                setPosY = AccessTools.MethodDelegate<SetXY>(AccessTools.Method(d, "SetPositionY", new[] { typeof(float), typeof(Vector2) }));
                setRot = AccessTools.MethodDelegate<Action<scrDecoration, float>>(AccessTools.Method(d, "SetRotation", new[] { typeof(float) }));
                setOpa = AccessTools.MethodDelegate<Action<scrDecoration, float>>(AccessTools.Method(d, "SetOpacity", new[] { typeof(float) }));
                setCol = AccessTools.MethodDelegate<Action<scrDecoration, Color>>(AccessTools.Method(d, "SetColor", new[] { typeof(Color) }));
                setScale = AccessTools.MethodDelegate<Action<scrDecoration, Vector2>>(AccessTools.Method(d, "SetScale", new[] { typeof(Vector2) }));
                try
                {
                    setPivX = AccessTools.MethodDelegate<Action<scrDecoration, float>>(AccessTools.Method(d, "SetPivotX", new[] { typeof(float) }));
                    setPivY = AccessTools.MethodDelegate<Action<scrDecoration, float>>(AccessTools.Method(d, "SetPivotY", new[] { typeof(float) }));
                    setParOffX = AccessTools.MethodDelegate<Action<scrDecoration, float>>(AccessTools.Method(d, "SetParallaxOffsetX", new[] { typeof(float) }));
                    setParOffY = AccessTools.MethodDelegate<Action<scrDecoration, float>>(AccessTools.Method(d, "SetParallaxOffsetY", new[] { typeof(float) }));
                    CanPivotParallax = setPivX != null && setPivY != null && setParOffX != null && setParOffY != null;
                }
                catch (Exception ex) { CanPivotParallax = false; Main.Entry.Logger.Log("[장식 애니메이션] 피벗·시차 함수를 못 찾아 그 효과는 원래대로: " + ex.Message); }
                var kill = AccessTools.Method(typeof(TweenExtensions), "Kill", new[] { typeof(Tween), typeof(bool) });
                h.Patch(kill, prefix: new HarmonyMethod(typeof(DecoAnim), nameof(KillPrefix)) { priority = Priority.First });
                foreach (var m in typeof(DOTween).GetMethods(AccessTools.all))
                    if (m.Name == "KillAll" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(bool))
                        h.Patch(m, prefix: new HarmonyMethod(typeof(DecoAnim), nameof(KillAllPrefix)) { priority = Priority.First });
                // 게임은 "재생 중인 애니메이션 목록"(DOTween.PlayingTweens)을 받아 끊거나 완료하는 곳이 두 군데 있다 (IL 전체 검색):
                //   scnGame.ResetScene (편집기에서 플레이를 멈출 때 등): 목록의 것을 Kill(false)
                //   scrController.WaitForStartCo (체크포인트에서 시작): 목록의 것을 Complete(true)
                // 모드 표는 꺼진 객체라 이 목록에 없어서, 멈춘 뒤에도 모드 애니메이션이 계속 돌아 초기화된 장식(글자 등)을 다시 바꿔 놓았다.
                // 진행 중인 모드 표를 목록 끝에 넣어 원래 DOTween 애니메이션과 똑같이 끊기거나 완료되게 한다.
                var playing = AccessTools.Method(typeof(DOTween), "PlayingTweens");
                if (playing != null) h.Patch(playing, postfix: new HarmonyMethod(typeof(DecoAnim), nameof(PlayingPostfix)));
                else Main.Entry.Logger.Log("[장식 애니메이션] PlayingTweens 를 못 찾음");
                foreach (var m in typeof(TweenExtensions).GetMethods(AccessTools.all))
                    if (m.Name == "Complete" && m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(Tween))
                        h.Patch(m, prefix: new HarmonyMethod(typeof(DecoAnim), nameof(CompletePrefix)) { priority = Priority.First });
                var comp = AccessTools.TypeByName("DG.Tweening.Core.DOTweenComponent");
                var upd = comp == null ? null : AccessTools.Method(comp, "Update");
                if (upd == null) { Main.Entry.Logger.Log("[장식 애니메이션] DOTween 갱신 함수를 못 찾아 끔"); return; }
                h.Patch(upd, prefix: new HarmonyMethod(typeof(DecoAnim), nameof(UpdatePrefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(DecoAnim), nameof(UpdatePostfix)) { priority = Priority.Last });
                Installed = true;
                Main.Entry.Logger.Log("[장식 애니메이션] 설치");
            }
            catch (Exception ex) { Installed = false; Main.Entry.Logger.Log("[장식 애니메이션] 설치 실패: " + ex.Message); }
        }

        internal static bool Active { get { return Enabled && Installed; } }
        internal static bool IsRunning(Tween t) { var r = t.id as Rec; return r != null && r.Running; }

        // ── 만들기 (FastMove 루프가 원래 블록 순서대로 부른다) ──
        // 원래 코드: 사전에 있으면 Kill(true) (null 이어도 부른다)
        private static void KillKey(Dictionary<global::TweenType, Tween> d, int key)
        {
            Tween t;
            if (d.TryGetValue((global::TweenType)key, out t)) t.Kill(true);
        }
        private static Rec NewRec(scrDecoration dec, Dictionary<global::TweenType, Tween> d, int key, float dur, Ease ease)
        {
            // 같은 칸에 끝난 표가 있으면 다시 쓴다 (그 칸만 이 표를 가리키고, 방금 끊었다). 없으면 새 꺼진 객체 (active = false)
            // 표 안의 기록(Rec)도 목록에서 빠졌으면 같이 다시 쓴다. 한 판에 수십만 개가 생기던 것이라(곡 중엔 GC 를 멈춰 두어 그대로 쌓였다)
            // 같은 장식·같은 속성을 계속 옮기는 맵에서는 대부분 새로 만들지 않는다.
            Tween old; Rec oldR = null; Rec r;
            bool reuseProxy = d.TryGetValue((global::TweenType)key, out old) && (object)old != null && (oldR = old.id as Rec) != null && !oldR.Running && (!Edition.Dev || oldR.Shadow == null);
            if (reuseProxy && !oldR.InList && ReferenceEquals(oldR.D, dec) && oldR.Key == key)
            {
                r = oldR; Reused++;
                r.Pos = 0f; r.Started = false; r.Running = true; r.Stepped = false;
                r.Shadow = null; r.SDone = false; r.SStepped = false;
                r.Dur = dur; r.E = ease;
            }
            else r = new Rec { D = dec, Key = key, Dur = dur, E = ease };
            ZeroTween.EaseParams(ease, out r.Over, out r.Period);
            r.Proxy = reuseProxy ? old : AccessTools.CreateInstance<TweenerCore<float, float, FloatOptions>>();
            r.Proxy.id = r;
            d[(global::TweenType)key] = r.Proxy;
            recs.Add(r); r.InList = true; Created++;
            if (recs.Count > Peak) Peak = recs.Count;
            return r;
        }
        internal static void Pos(scrDecoration dec, Dictionary<global::TweenType, Tween> d, int key, float startAxis, float target, float dur, Ease ease)
        {
            KillKey(d, key);
            var pp = pivotPosRef(dec);
            var r = NewRec(dec, d, key, dur, ease);
            r.FStart = key == 1 ? pp.x : pp.y;
            r.FEnd = startAxis + target;
            MaybeShadowF(r);
        }
        internal static void Rot(scrDecoration dec, Dictionary<global::TweenType, Tween> d, float target, float dur, Ease ease)
        {
            KillKey(d, 5);
            float start = rotRef(dec);
            var r = NewRec(dec, d, 5, dur, ease);
            r.FStart = start; r.FEnd = target;
            MaybeShadowF(r);
        }
        internal static void Opa(scrDecoration dec, Dictionary<global::TweenType, Tween> d, float target, float dur, Ease ease)
        {
            KillKey(d, 10);
            float start = opaRef(dec);
            var r = NewRec(dec, d, 10, dur, ease);
            r.FStart = start; r.FEnd = target;
            MaybeShadowF(r);
        }
        internal static void Col(scrDecoration dec, Dictionary<global::TweenType, Tween> d, Color target, float dur, Ease ease)
        {
            KillKey(d, 9);
            var start = colRef(dec);
            var r = NewRec(dec, d, 9, dur, ease);
            r.CStart = start; r.CEnd = target;
            if (Sample()) r.Shadow = DOTween.To(() => r.CStart, c => { r.SC = c; r.SStepped = true; }, r.CEnd, r.Dur).SetEase(ease).OnComplete(() => r.SDone = true);
        }
        internal static void Scale(scrDecoration dec, Dictionary<global::TweenType, Tween> d, int key, Vector2 target, float dur, Ease ease)
        {
            KillKey(d, key);
            var r = NewRec(dec, d, key, dur, ease);
            r.VEnd = target;
            if (Sample())
                r.Shadow = DOTween.To(() => r.Started ? r.VStart : scaleRef(r.D), v => { r.SV = v; r.SStepped = true; }, r.VEnd, r.Dur).SetEase(ease)
                    .SetOptions(key == 7 ? AxisConstraint.X : AxisConstraint.Y).OnComplete(() => r.SDone = true);
        }
        // 피벗 X/Y (키 3/4): 시작 = 만들 때 pivotOffsetVec 의 그 축, OnUpdate/OnComplete = SetPivotX/Y
        internal static void Piv(scrDecoration dec, Dictionary<global::TweenType, Tween> d, int key, float target, float dur, Ease ease)
        {
            KillKey(d, key);
            var po = pivotOffRef(dec);
            var r = NewRec(dec, d, key, dur, ease);
            r.FStart = key == 3 ? po.x : po.y; r.FEnd = target;
            MaybeShadowF(r);
        }
        // 시차 오프셋 X/Y (키 12/13): 시작 = 만들 때 parallaxOffset 의 그 축, OnUpdate/OnComplete = SetParallaxOffsetX/Y
        internal static void ParOff(scrDecoration dec, Dictionary<global::TweenType, Tween> d, int key, float target, float dur, Ease ease)
        {
            KillKey(d, key);
            var po = parOffRef(dec);
            var r = NewRec(dec, d, key, dur, ease);
            r.FStart = key == 12 ? po.x : po.y; r.FEnd = target;
            MaybeShadowF(r);
        }
        // 시차 배율 (키 11): getter 는 만들 때 담은 값(바뀌지 않음), setter = parallax.multiplier 에 바로 씀, 축 제한 없음, 콜백 없음
        internal static void Par(scrDecoration dec, Dictionary<global::TweenType, Tween> d, Vector2 target, float dur, Ease ease)
        {
            KillKey(d, 11);
            var start = parallaxRef(dec).multiplier;   // 원래 코드도 여기서 읽는다 (시차 부품이 없으면 원래도 예외 -> CanAnim 에서 거른다)
            var r = NewRec(dec, d, 11, dur, ease);
            r.VStart = start; r.VEnd = target;
            if (Sample())
                r.Shadow = DOTween.To(() => r.VStart, v => { r.SV = v; r.SStepped = true; }, r.VEnd, r.Dur).SetEase(ease).OnComplete(() => r.SDone = true);
        }
        internal static bool HasParallax(scrDecoration dec) { return parallaxRef(dec) != null; }

        private static long sampleCounter;
        private static bool Sample() { return Edition.Dev && (++sampleCounter & 63) == 0; }
        private static void MaybeShadowF(Rec r)
        {
            if (Sample()) r.Shadow = DOTween.To(() => r.FStart, v => { r.SF = v; r.SStepped = true; }, r.FEnd, r.Dur).SetEase(r.E).OnComplete(() => r.SDone = true);
        }

        // ── 진행 ──
        private static void Startup(Rec r)
        {
            r.Started = true;
            switch (r.Key)
            {
                case 9: r.CChange = r.CEnd - r.CStart; break;
                case 7: case 8: r.VStart = scaleRef(r.D); r.VChange = r.VEnd - r.VStart; break;
                case 11: r.VChange = r.VEnd - r.VStart; break;   // 시작값은 만들 때 담았다
                default: r.FChange = r.FEnd - r.FStart; break;
            }
        }
        // setter (값 계산 + 넣기)
        private static void Apply(Rec r, float pos)
        {
            float e = ZeroTween.Eval(r.E, pos, r.Dur, r.Over, r.Period);
            switch (r.Key)
            {
                // DOTween ColorPlugin 과 같은 모양: 성분마다 "시작 += 변화 x 이징" (색 연산자로 하면 곱을 한 번 더 반올림해 끝자리가 달랐다)
                case 9: { var c = r.CStart; c.r += r.CChange.r * e; c.g += r.CChange.g * e; c.b += r.CChange.b * e; c.a += r.CChange.a * e; r.CLast = c; break; }
                case 7: { var v = scaleRef(r.D); v.x = r.VStart.x + r.VChange.x * e; r.VLast = v; setScale(r.D, v); break; }
                case 8: { var v = scaleRef(r.D); v.y = r.VStart.y + r.VChange.y * e; r.VLast = v; setScale(r.D, v); break; }
                // DOTween Vector2Plugin (축 제한 없음) 과 같은 모양: 성분마다 "시작 += 변화 x 이징" 후 setter
                case 11: { var v = r.VStart; v.x += r.VChange.x * e; v.y += r.VChange.y * e; r.VLast = v; parallaxRef(r.D).multiplier = v; break; }
                default: r.FLast = r.FStart + r.FChange * e; break;
            }
        }
        private static void OnUpdate(Rec r)
        {
            switch (r.Key)
            {
                case 1: setPosX(r.D, r.FLast, pivotOffRef(r.D)); break;
                case 2: setPosY(r.D, r.FLast, pivotOffRef(r.D)); break;
                case 3: setPivX(r.D, r.FLast); break;
                case 4: setPivY(r.D, r.FLast); break;
                case 5: setRot(r.D, r.FLast); break;
                case 12: setParOffX(r.D, r.FLast); break;
                case 13: setParOffY(r.D, r.FLast); break;
                case 9: setCol(r.D, r.CLast); break;
                case 10: setOpa(r.D, r.FLast); break;
            }
        }

        private static void OnComplete(Rec r)
        {
            switch (r.Key)
            {
                case 1: setPosX(r.D, r.FEnd, pivotOffRef(r.D)); break;
                case 2: setPosY(r.D, r.FEnd, pivotOffRef(r.D)); break;
                case 3: setPivX(r.D, r.FEnd); break;
                case 4: setPivY(r.D, r.FEnd); break;
                case 5: setRot(r.D, r.FEnd); break;
                case 12: setParOffX(r.D, r.FEnd); break;
                case 13: setParOffY(r.D, r.FEnd); break;
                case 9: setCol(r.D, r.CEnd); break;
                case 10: setOpa(r.D, r.FEnd); break;
            }
        }
        private static void Step(Rec r, float td)
        {
            if (!r.Started) Startup(r);
            float to = r.Pos + td;
            bool done = false;
            if (to >= r.Dur) { to = r.Dur; done = true; }
            r.Pos = to;
            r.Stepped = true;
            if (Prof && (++profN & 15) == 0) { long a0 = System.Diagnostics.Stopwatch.GetTimestamp(); Apply(r, to); long a1 = System.Diagnostics.Stopwatch.GetTimestamp(); OnUpdate(r); long a2 = System.Diagnostics.Stopwatch.GetTimestamp(); int k = r.Key & 15; ProfN[k]++; ProfApply[k] += a1 - a0; ProfSet[k] += a2 - a1; }
            else { Apply(r, to);
            OnUpdate(r); }
            if (done) { r.Running = false; Completed++; OnComplete(r); }
        }
        // Kill(true) / KillAll(true): 끝 위치로 한 번 (시작 전이면 시작 처리부터)
        private static void Complete(Rec r)
        {
            if (!r.Running) return;
            if (!r.Started) Startup(r);
            r.Pos = r.Dur;
            r.Running = false;
            r.Stepped = true;
            try { Apply(r, r.Dur); OnUpdate(r); OnComplete(r); }
            catch (Exception ex) { Error(ex); }
            if (Edition.Dev && r.Shadow != null && r.Shadow.active) { try { r.Shadow.Complete(true); } catch { } }
        }

        public static bool KillPrefix(Tween t, bool complete)
        {
            if ((object)t == null) return true;
            var r = t.id as Rec;
            if (r == null) return true;
            if (complete) { Killed++; Complete(r); }
            else if (r.Running) { r.Running = false; Dropped++; if (r.Shadow != null) r.Shadow.Kill(false); }
            return false;   // 표 자체는 꺼진 객체라 DOTween 에 넘길 것이 없다
        }
        // Complete(t) / Complete(t, withCallbacks): 트위너는 어느 쪽이든 끝값 + OnComplete (DOTween 과 같음)
        public static bool CompletePrefix(Tween t)
        {
            if ((object)t == null) return true;
            var r = t.id as Rec;
            if (r == null) return true;
            if (r.Running) { Killed++; Complete(r); }
            return false;
        }
        internal static long Listed;
        public static void PlayingPostfix(List<Tween> __0, ref List<Tween> __result)
        {
            int n = 0;
            for (int i = 0; i < recs.Count; i++) if (recs[i].Running) n++;
            if (n == 0) return;
            // 원래 DOTween 은 진행 중인 것이 없으면 null 을 돌려준다. 모드 표가 있으면 원래는 진행 중인 DOTween 애니메이션이었으므로 목록을 만든다.
            // 게임은 넘긴 목록(ResetScene)이나 돌려받은 목록(WaitForStartCo) 중 하나를 쓴다. 둘 다 채운다 (같은 목록이면 한 번)
            if (__0 != null) AddRunning(__0);
            if (__result == null) __result = __0 ?? AddRunning(new List<Tween>(n));
            else if (!ReferenceEquals(__result, __0)) AddRunning(__result);
            Listed += n;
            Main.Entry.Logger.Log("[장식 애니메이션] 게임이 재생 중인 애니메이션 목록을 가져감 (멈춤/체크포인트 시작): 모드 애니메이션 " + n + "개를 함께 넘김");
        }
        private static List<Tween> AddRunning(List<Tween> list)
        {
            for (int i = 0; i < recs.Count; i++) if (recs[i].Running) list.Add(recs[i].Proxy);
            return list;
        }
        public static void KillAllPrefix(bool complete)
        {
            if (recs.Count == 0) { LastFrameMs = 0; return; }
            var copy = recs.ToArray();
            foreach (var r in copy)
            {
                if (complete) Complete(r);
                else if (r.Running) { r.Running = false; Dropped++; }
                r.InList = false;
            }
            recs.Clear();
        }

        // scnGame.ResetScene 앞(SceneReset): 게임이 재생 중인 DOTween 을 Kill() (완료 없이) 하는 것과 같이, 진행 중인 표를 그 자리에서 버린다.
        // 모드 표는 꺼진 객체라 게임이 받는 DOTween.PlayingTweens 목록에 없어서, 안 버리면 에디터에서도 계속 돌며 장식을 곡 중 값으로 되돌렸다.
        internal static void DropAll() { KillAllPrefix(false); }

        // DOTweenComponent.Update 앞:DOTween 과 같은 dt 로, 만든 순서대로 진행
        public static void UpdatePrefix()
        {
            if (recs.Count == 0) { LastFrameMs = 0; return; }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            float dt = (DOTween.useSmoothDeltaTime ? Time.smoothDeltaTime : Time.deltaTime) * DOTween.timeScale;
            float td = dt * 1f;   // 애니메이션마다의 timeScale 은 1
            Frames++;
            if (!(td < 1E-06f && td > -1E-06f))
            {
                for (int i = 0; i < recs.Count; i++)
                {
                    var r = recs[i];
                    if (!r.Running) continue;
                    Steps++;
                    try { Step(r, td); }
                    catch (Exception ex) { r.Running = false; Error(ex); }   // DOTween 안전 모드: 예외가 나면 그 애니메이션을 끝냄
                }
            }
            if (!Edition.Dev) Compact();
            LastFrameMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            UpdateMs += LastFrameMs;
        }

        // 개발자용: DOTween 이 짝을 갱신한 뒤 비교
        public static void UpdatePostfix()
        {
            if (!Edition.Dev || recs.Count == 0) return;
            for (int i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                if (r.Shadow == null) { r.Stepped = false; continue; }
                if (r.Stepped || r.SStepped)
                {
                    VerifySteps++;
                    string diff = null;
                    if (r.Stepped != r.SStepped) diff = "진행한 프레임이 다름 (모드 " + r.Stepped + ", DOTween " + r.SStepped + ")";
                    else if (r.Key == 9) { if (!C4(r.CLast, r.SC)) diff = "색 " + r.CLast.ToString("R") + " / " + r.SC.ToString("R"); }
                    else if (r.Key == 7) { if (B(r.VLast.x) != B(r.SV.x)) diff = "크기X " + r.VLast.x.ToString("R") + " / " + r.SV.x.ToString("R"); }
                    else if (r.Key == 8) { if (B(r.VLast.y) != B(r.SV.y)) diff = "크기Y " + r.VLast.y.ToString("R") + " / " + r.SV.y.ToString("R"); }
                    else if (r.Key == 11) { if (B(r.VLast.x) != B(r.SV.x) || B(r.VLast.y) != B(r.SV.y)) diff = "시차배율 " + r.VLast.ToString("R") + " / " + r.SV.ToString("R"); }
                    else if (B(r.FLast) != B(r.SF)) diff = "키 " + r.Key + " " + r.FLast.ToString("R") + " / " + r.SF.ToString("R");
                    if (diff == null && (!r.Running) != r.SDone) diff = "끝난 프레임이 다름 (모드 " + (!r.Running) + ", DOTween " + r.SDone + ")";
                    if (diff != null) { VerifyMismatch++; MismatchByKey[r.Key]++; if (First.Length < 600) First += " [" + diff + ", 위치 " + r.Pos.ToString("R") + "/" + r.Dur.ToString("R") + "]"; }
                }
                r.Stepped = false; r.SStepped = false;
                if (!r.Running) { VerifyN++; if (r.Shadow.active) r.Shadow.Kill(false); r.Shadow = null; }
            }
            Compact();
        }
        private static bool C4(Color a, Color b) { return B(a.r) == B(b.r) && B(a.g) == B(b.g) && B(a.b) == B(b.b) && B(a.a) == B(b.a); }
        private static int B(float f) { return InstantMove.Bits(f); }

        private static void Compact()
        {
            int w = 0;
            for (int i = 0; i < recs.Count; i++)
            {
                var r = recs[i];
                if (r.Running || (Edition.Dev && r.Shadow != null)) recs[w++] = r;
                else r.InList = false;
            }
            if (w < recs.Count) recs.RemoveRange(w, recs.Count - w);
        }

        private static void Error(Exception ex)
        {
            Errors++;
            if (First.Length < 600) First += " [예외: " + ex.GetType().Name + " " + ex.Message + "]";
        }

        // 설정을 끄면 진행 중인 것을 DOTween 처럼 끝까지 가게 할 수는 없으니 완료시킨다 (Kill(true) 와 같음)
        internal static void FinishAll() { KillAllPrefix(true); }

        internal static string Summary()
        {
            if (Created == 0) return "";
            string s = string.Format(" | 장식 애니메이션 직접 처리: 만든 것 {0}개(동시 최대 {1}개), 끝까지 감 {2}, 끊겨서 완료 {3}, 버림 {4}, 기록 다시 씀 {8}, 갱신에 쓴 시간 {5:F0}ms ({6}프레임), 게임이 멈출 때 넘겨준 것 {9}{7}",
                Created, Peak, Completed, Killed, Dropped, UpdateMs, Frames, Errors > 0 ? ", 예외 " + Errors : "", Reused, Listed);
            if (Edition.Dev) s += " (검증: 진짜 DOTween 과 나란히 " + VerifyN + "개, 프레임 " + VerifySteps + "번 중 다름 " + VerifyMismatch + " [위치X " + MismatchByKey[1] + ", 위치Y " + MismatchByKey[2] + ", 회전 " + MismatchByKey[5] + ", 크기X " + MismatchByKey[7] + ", 크기Y " + MismatchByKey[8] + ", 색 " + MismatchByKey[9] + ", 불투명도 " + MismatchByKey[10] + ", 피벗 " + (MismatchByKey[3] + MismatchByKey[4]) + ", 시차배율 " + MismatchByKey[11] + ", 시차 " + (MismatchByKey[12] + MismatchByKey[13]) + "]" + First + ")";
            else if (First.Length > 0) s += First;
            if (Prof && Steps > 0)
            {
                double us = UpdateMs * 1000.0 / Steps, tk = System.Diagnostics.Stopwatch.Frequency / 1e6;
                s += string.Format(" | 애니메이션 한 번 진행 평균 {0:F2}us (진행 {1}번, 프레임당 {2:F0}번)", us, Steps, Frames > 0 ? (double)Steps / Frames : 0);
                string[] nm = { "", "위치X", "위치Y", "피벗X", "피벗Y", "회전", "", "크기X", "크기Y", "색", "불투명도", "시차배율", "시차X", "시차Y" };
                for (int k = 1; k <= 13; k++) if (ProfN[k] > 0) s += string.Format(", {0} 계산 {1:F2}+설정 {2:F2}us", nm[k], ProfApply[k] / tk / ProfN[k], ProfSet[k] / tk / ProfN[k]);
            }
            return s;
        }
        internal static void ResetStats() { Array.Clear(MismatchByKey, 0, 16); Created = Completed = Killed = Dropped = Frames = VerifyN = VerifySteps = VerifyMismatch = Errors = 0; Peak = 0; UpdateMs = 0; First = ""; Reused = 0; Steps = 0; Array.Clear(ProfN, 0, 16); Array.Clear(ProfApply, 0, 16); Array.Clear(ProfSet, 0, 16); }
    }
}
