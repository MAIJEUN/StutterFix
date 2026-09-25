using System;
using System.Collections.Generic;
using System.Reflection;
using DG.Tweening;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 길이 0 인 장식 이동 효과를 게임 코드 대신 모드의 루프로 돈다.
    //
    // 1.3.8 개발자용 쪼개기(Arche 가장 무거운 프레임): 장식 1만 4천 개 효과에서 속성 처리 말고도 "반복·클로저·태그 목록" 에
    // 17ms, 크기·시차 배율 대역에 6ms 가 들었다. 게임 코드는 장식마다 클로저 객체를 두세 개 만들고, 태그 목록을 LINQ
    // (Where -> SelectMany -> Distinct) 여러 겹으로 훑고, 크기·시차 배율마다 델리게이트 두 개와 애니메이션 대역을 거친다.
    //
    // 이 루프는 StartEffect(IL 로 확인)와 같은 순서로 같은 일을 한다. 속성 하나하나는 끼운 도우미와 같은 함수(InstantMove.C*)를 쓴다:
    //   효과 앞: (targetScale 이 있으면) targetScaleV2 = (s, s). AdjustDurationForHardbake 는 커스텀 맵에서 아무것도 안 한다.
    //   대상: 태그 순서대로 taggedDecorations[태그] 를 이어 붙이고 처음 나온 것만 (Distinct 와 같은 순서, 유니티 객체 비교 = 참조 비교)
    //   장식마다: 배치 방식 -> [이동 고정이 아니면] 위치 X/Y -> 시차 오프셋 X/Y -> 피벗 X/Y -> 회전 -> 크기 X/Y
    //            -> 색 -> 불투명도 -> 시차 배율 -> 보이기 -> 깊이
    // 맡지 않는 것(원래 코드로 돈다): 길이가 있는 효과, 이미지/원래 크기/부드럽게/마스크 계열을 바꾸는 효과, 공식 맵,
    //   가장 낮은 그래픽 설정, 태그나 장식 목록에 null 이 있는 경우, 플레이 중이 아닐 때.
    //
    // 검증 (개발자용): 16번에 1번, 루프가 끝난 상태를 기록한 뒤 같은 효과를 원래 코드로 한 번 더 돌린다. 길이 0 효과는 절대값을
    // 넣으므로(상대 이동은 검증에서 뺌) 루프가 원래와 같은 일을 했다면 두 번째 실행은 아무것도 바꾸지 않아야 한다.
    // 장식마다 값·엔진 상태를 비교해 다른 것을 센다.
    internal static class FastMove
    {
        internal static bool Enabled = true;
        internal static bool Installed;
        internal static long Effects, DecoCount, Fallbacks, Checked, CheckedDecos, Mismatch;
        internal static string First = "";
        private static readonly long[] why = new long[8];

        private static readonly AccessTools.FieldRef<ffxPlusBase, float> durRef = AccessTools.FieldRefAccess<ffxPlusBase, float>("duration");
        private static readonly AccessTools.FieldRef<ffxPlusBase, Ease> easeRef = AccessTools.FieldRefAccess<ffxPlusBase, Ease>("ease");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, List<string>> tagsRef = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, List<string>>("targetTags");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, scrDecorationManager> mgrRef = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, scrDecorationManager>("decManager");
        private static readonly AccessTools.FieldRef<scrDecorationManager, Dictionary<string, List<scrDecoration>>> taggedRef = AccessTools.FieldRefAccess<scrDecorationManager, Dictionary<string, List<scrDecoration>>>("taggedDecorations");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, float> tScale = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, float>("targetScale");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, Vector2> tScaleV2 = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, Vector2>("targetScaleV2");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, Vector2> tPos = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, Vector2>("targetPos");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, Vector2> tParOff = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, Vector2>("targetParallaxOffset");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, Vector2> tPiv = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, Vector2>("targetPivot");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, Vector2> tParallax = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, Vector2>("targetParallax");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, DecPlacementType> mtRef = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, DecPlacementType>("movementType");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, int> tDepth = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, int>("targetDepth");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, bool>
            mtUsed = B("movementTypeUsed"), fdt = B("forceDontTweenMovement"), posUsed = B("positionUsed"), parOffUsed = B("parallaxOffsetUsed"),
            pivUsed = B("pivotUsed"), rotUsed = B("rotationUsed"), scaleUsed = B("scaleUsed"), colUsed = B("colorUsed"), opaUsed = B("opacityUsed"),
            parUsed = B("parallaxUsed"), visUsed = B("visibleUsed"), visible = B("visible"), depthUsed = B("depthUsed"),
            imgUsed = B("imageFilenameUsed"), sizeUsed = B("originalSizeUsed"), smoothUsed = B("smoothingUsed"), maskTypeUsed = B("maskingTypeUsed"),
            maskTargetUsed = B("maskingTargetUsed"), maskDepthUsed = B("useMaskingDepthUsed"), maskFrontUsed = B("maskingFrontDepthUsed"), maskBackUsed = B("maskingBackDepthUsed");
        private static AccessTools.FieldRef<ffxMoveDecorationsPlus, bool> B(string f) { return AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, bool>(f); }

        private static readonly AccessTools.FieldRef<scrDecoration, Dictionary<global::TweenType, Tween>> tweensRef = AccessTools.FieldRefAccess<scrDecoration, Dictionary<global::TweenType, Tween>>("eventTweens");
        private static readonly AccessTools.FieldRef<scrDecoration, Vector2> startPosRef = AccessTools.FieldRefAccess<scrDecoration, Vector2>("startPos");
        private static readonly AccessTools.FieldRef<scrDecoration, Vector2> pivotPosRef = AccessTools.FieldRefAccess<scrDecoration, Vector2>("pivotPosVec");
        private static readonly AccessTools.FieldRef<scrDecoration, bool> forceHideRef = AccessTools.FieldRefAccess<scrDecoration, bool>("forceHide");
        private static Action<scrDecoration, DecPlacementType> setPlacement;
        private static Action<scrDecoration, bool> setVisible;
        private static Action<scrDecoration, int> setDepth;
        private static MethodBase start;

        private sealed class RefEq : IEqualityComparer<scrDecoration>
        {
            internal static readonly RefEq I = new RefEq();
            public bool Equals(scrDecoration a, scrDecoration b) { return ReferenceEquals(a, b); }
            public int GetHashCode(scrDecoration o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }
        private static readonly List<scrDecoration> list = new List<scrDecoration>();
        private static bool running, bypass;

        internal static void Install(Harmony h)
        {
            try
            {
                foreach (var m in typeof(ffxMoveDecorationsPlus).GetMethods(AccessTools.all))
                    if (m.Name == "StartEffect" && m.DeclaringType == typeof(ffxMoveDecorationsPlus) && !m.IsAbstract) start = m;
                if (start == null) { Main.Entry.Logger.Log("[장식 이동 루프] StartEffect 없음 - 적용 안 함"); return; }
                var d = typeof(scrDecoration);
                setPlacement = AccessTools.MethodDelegate<Action<scrDecoration, DecPlacementType>>(AccessTools.Method(d, "SetPlacementType", new[] { typeof(DecPlacementType) }));
                setVisible = AccessTools.MethodDelegate<Action<scrDecoration, bool>>(AccessTools.Method(d, "SetVisible", new[] { typeof(bool) }));
                setDepth = AccessTools.MethodDelegate<Action<scrDecoration, int>>(AccessTools.Method(d, "SetDepth", new[] { typeof(int) }));
                h.Patch(start, prefix: new HarmonyMethod(typeof(FastMove), nameof(Prefix)) { priority = Priority.Last });
                Installed = true;
                Main.Entry.Logger.Log("[장식 이동 루프] 설치");
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[장식 이동 루프] 설치 실패: " + ex.Message); }
        }

        // 다른 모드 코드(효과 나누기 등)가 원래 실행을 막았으면 아무것도 안 한다. 맡으면 false(원래 코드 건너뜀).
        public static bool Prefix(ffxMoveDecorationsPlus __instance, object[] __args, bool __runOriginal)
        {
            if (!__runOriginal) return false;
            if (bypass || running || !Enabled || !Installed || !Hitch.Playing) return Orig(__instance);
            if (!InstantMove.Enabled || !InstantMove.Patched || !ZeroTween.Enabled || !ZeroTween.Patched || !ZeroTween.CanEase) return Orig(__instance);
            running = true;
            try
            {
                // 미리 확인이 "아무것도 안 바꾼다" 고 확인해 둔 효과: 효과 앞부분의 필드 쓰기만 하고 건너뛴다
                if (Precheck.Active != 0 && Precheck.TrySkip(__instance))
                {
                    if (!float.IsNaN(tScale(__instance))) tScaleV2(__instance) = new Vector2(tScale(__instance), tScale(__instance));
                    if (Edition.Dev && (Precheck.Used & 1) == 1) VerifyNoop(__instance);
                    return false;
                }
                if (!Take(__instance)) return Orig(__instance);   // 원래 코드가 돈다 (아직 아무것도 안 바꿨다)
                bool sample = Edition.Dev && durRef(__instance) <= 0f && ((Effects + 1) % 16) == 1;   // 길이 있는 효과는 원래 코드로 다시 돌리면 애니메이션을 끊어 버려 검증하지 않는다
                if (sample) { hidBefore.Clear(); foreach (var dec in src) hidBefore.Add(InvisibleSkip.IsHidden(dec)); }
                Run(__instance);
                if (sample) Verify(__instance, __args);
                return false;
            }
            catch (Exception ex)
            {
                // 루프 도중 예외: 게임의 설정 함수가 던진 것이다. 원래 코드도 같은 장식에서 던졌을 것이므로 다시 돌리지 않는다.
                if (First.Length < 300) First += " [예외: " + ex.GetType().Name + " " + ex.Message + "]";
                return false;
            }
            finally { running = false; }
        }

        // 원래 코드로 돈다. 원래 코드는 대상 장식의 애니메이션 사전을 바꿀 수 있으므로, 미리 확인 중인 계획이 그 장식을 보고 있으면 취소한다.
        private static bool Orig(ffxMoveDecorationsPlus fx)
        {
            if (Precheck.Active != 0)
            {
                var mgr = mgrRef(fx);
                Precheck.TouchTargets(tagsRef(fx), (object)mgr == null ? null : taggedRef(mgr));
            }
            return true;
        }

        // ── 미리 확인이 쓰는 것 ──
        internal sealed class ShapeInfo { public List<scrDecoration> Targets; public readonly List<List<scrDecoration>> Sources = new List<List<scrDecoration>>(); public string Tag; public bool Pos, Px, Py, Col, Opa; public Vector2 Tp; public Color Tc; public float To; public int Keys; }
        private static readonly ShapeInfo shape = new ShapeInfo();
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, Color> tCol = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, Color>("targetColor");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, float> tOpa = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, float>("targetOpacity");
        // 미리 확인할 수 있는 효과면 모양(대상 목록, 쓰는 속성, 넣을 값)을 돌려준다: 길이 0, 위치·색·불투명도만, 상대 이동 아님, 태그 하나.
        // 돌려주는 것은 다시 쓰는 객체다(바로 복사해서 쓸 것).
        internal static ShapeInfo Shape(ffxMoveDecorationsPlus fx)
        {
            if (durRef(fx) > 0f) return Why("길이 있음");
            if (imgUsed(fx) || sizeUsed(fx) || smoothUsed(fx) || maskTypeUsed(fx) || maskTargetUsed(fx) || maskDepthUsed(fx) || maskFrontUsed(fx) || maskBackUsed(fx)) return Why("이미지·마스크");
            if (mtUsed(fx) && (int)mtRef(fx) != 7) return Why("배치 방식");
            if (parUsed(fx) || visUsed(fx) || depthUsed(fx)) return Why(parUsed(fx) ? "시차 배율" : visUsed(fx) ? "보이기" : "깊이");
            bool move = !fdt(fx);
            var tp = tPos(fx);
            bool px = move && posUsed(fx) && !float.IsNaN(tp.x), py = move && posUsed(fx) && !float.IsNaN(tp.y), pos = px || py;
            if (pos && (int)mtRef(fx) == 7) return Why("상대 이동");
            if (move)
            {
                var a = tParOff(fx); if (parOffUsed(fx) && (!float.IsNaN(a.x) || !float.IsNaN(a.y))) return Why("시차 오프셋");
                var b = tPiv(fx); if (pivUsed(fx) && (!float.IsNaN(b.x) || !float.IsNaN(b.y))) return Why("피벗");
                if (rotUsed(fx)) return Why("회전");
                if (scaleUsed(fx))
                {
                    Vector2 sc = !float.IsNaN(tScale(fx)) ? new Vector2(tScale(fx), tScale(fx)) : tScaleV2(fx);
                    if (!float.IsNaN(sc.x) || !float.IsNaN(sc.y)) return Why("크기");
                }
            }
            bool col = colUsed(fx), opa = opaUsed(fx);
            if (!pos && !col && !opa) return Why("바꾸는 것 없음");
            var tags = tagsRef(fx); var mgr = mgrRef(fx);
            if (tags == null || tags.Count == 0 || (object)mgr == null) return Why("대상 없음");
            var dict = taggedRef(mgr); List<scrDecoration> l;
            if (dict == null) return Why("대상 없음");
            // 태그 순서대로 있는 목록만 (원래 코드: Where(있는 태그) -> SelectMany -> Distinct). 여러 개면 합친 목록은 미리 확인이 만든다.
            shape.Sources.Clear();
            for (int i = 0; i < tags.Count; i++)
            {
                if (tags[i] == null) return Why("태그에 null");
                if (!dict.TryGetValue(tags[i], out l)) continue;
                if (l == null) return Why("대상 없음");
                shape.Sources.Add(l);
            }
            if (shape.Sources.Count == 0) return Why("대상 없음");
            shape.Targets = shape.Sources.Count == 1 ? shape.Sources[0] : null; shape.Tag = tags[0]; shape.Pos = pos; shape.Px = px; shape.Py = py; shape.Col = col; shape.Opa = opa;
            shape.Tp = tp; shape.Tc = tCol(fx); shape.To = tOpa(fx);
            shape.Keys = (px ? 1 << 1 : 0) | (py ? 1 << 2 : 0) | (col ? 1 << 9 : 0) | (opa ? 1 << 10 : 0);
            return shape;
        }
        // 미리 확인이 부른다: 주어진 장식들에만 이 효과를 루프로 적용 (건드려진 장식만 원래 경로로)
        internal static void RunOn(ffxMoveDecorationsPlus fx, List<scrDecoration> decs)
        {
            var saved = src; src = decs;
            bool ns = InstantMove.NoSample; InstantMove.NoSample = true;   // 개발자용 표본 대조(원래 함수 부르기)는 이 경로에서 끈다 - 검증 재실행과 같은 길로
            try { Run(fx); } finally { src = saved; InstantMove.NoSample = ns; }
        }
        internal static string LastWhy = "";
        private static ShapeInfo Why(string w) { LastWhy = w; return null; }
        // 효과의 대상 수 (태그별 목록 길이 합, 중복 포함)
        internal static int TargetCount(ffxMoveDecorationsPlus fx)
        {
            var tags = tagsRef(fx); var mgr = mgrRef(fx);
            if (tags == null || (object)mgr == null) return 0;
            var dict = taggedRef(mgr); if (dict == null) return 0;
            int n = 0; List<scrDecoration> l;
            foreach (var t in tags) if (t != null && dict.TryGetValue(t, out l) && l != null) n += l.Count;
            return n;
        }
        internal static IEqualityComparer<scrDecoration> DecoEq { get { return RefEq.I; } }
        internal static bool IsClean(List<scrDecoration> l, int ver) { int at; return cleanAt.TryGetValue(l, out at) && at == ver; }
        internal static void MarkClean(List<scrDecoration> l, int ver) { cleanAt[l] = ver; }
        // 키 묶음 중 실제로 돌고 있는 애니메이션이 없는가 (칸이 없거나, 끝난 대역이거나, 이미 끝난 애니메이션).
        // 원래 코드는 이런 칸을 "끊고(아무 일 없음) 끝난 대역을 넣는다". 칸이 새로 생기거나 끝난 것끼리 바뀌는 것 말고는 달라지는 게 없다
        // (게임 코드에서 이 사전을 읽는 곳은 효과 시작과 장식 삭제뿐이고, 둘 다 끝난 애니메이션에는 아무것도 안 한다).
        internal static bool NoLiveMask(Dictionary<global::TweenType, Tween> d, int mask)
        {
            if (d == null) return false;
            var dead = InstantMove.Dead;
            foreach (var kv in d)
            {
                int key = (int)kv.Key;
                if (key < 0 || key >= 31 || (mask & (1 << key)) == 0) continue;
                var t = kv.Value;
                if (t != null && !ReferenceEquals(t, dead) && (t.active || DecoAnim.IsRunning(t))) return false;
            }
            return true;
        }
        // 키 묶음(비트 = TweenType 번호)이 사전에 모두 있고 전부 "끝난 대역" 인가
        internal static bool AllDeadMask(Dictionary<global::TweenType, Tween> d, int mask)
        {
            if (mask == 0 || d == null) return false;
            int need = 0; for (int m = mask; m != 0; m &= m - 1) need++;
            if (d.Count < need) return false;
            var dead = InstantMove.Dead; int found = 0;
            foreach (var kv in d)
            {
                int key = (int)kv.Key;
                if (key < 0 || key >= 31 || (mask & (1 << key)) == 0) continue;
                if (!ReferenceEquals(kv.Value, dead)) return false;
                found++;
            }
            return found == need;
        }

        // 개발자용: 미리 확인으로 건너뛴 효과를 실제로 돌려(원래 함수 표본 대조는 끄고) 대상 상태가 하나도 안 바뀌는지 본다
        private static void VerifyNoop(ffxMoveDecorationsPlus fx)
        {
            if (!Take(fx)) return;
            var decs = new List<scrDecoration>(); var a = new List<S>();
            for (int i = 0; i < src.Count; i += 4) { decs.Add(src[i]); a.Add(Snap(src[i])); }
            InstantMove.NoSample = true;
            MoveProf.Pause = true;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { Run(fx); }
            finally { InstantMove.NoSample = false; MoveProf.Pause = false; MoveProf.Exclude(System.Diagnostics.Stopwatch.GetTimestamp() - t0); }
            int bad = 0; string first = "";
            for (int i = 0; i < decs.Count; i++)
            {
                string diff = Diff(a[i], Snap(decs[i]));
                if (diff == null) continue;
                if (diff.StartsWith("미루기 목록") && InvisibleSkip.IsTruthSample(decs[i])) { TruthDiff++; continue; }   // 개발자용 정답 표본: 게임 함수가 미루지 않고 바로 반영한 장식
                if (diff.StartsWith("미루기 목록") && !a[i].Lz && a[i].Hid && Precheck.ShownBefore.Contains(decs[i])) { Precheck.VerifyExplained++; continue; }   // 따로 처리할 때 보이다가 같은 효과의 색으로 투명해진 장식
                bad++;
                if (first.Length < 300) first += " [" + decs[i].name + ": " + diff + "]";
            }
            Precheck.VerifyResult(decs.Count, bad, first);
        }

        // 맡을 수 있는지 보고, 맡으면 대상 목록을 만든다. 여기까지는 게임 상태를 바꾸지 않는다.
        private static bool Take(ffxMoveDecorationsPlus fx)
        {
            if (durRef(fx) > 0f)
            {
                if (Edition.Dev || Main.MeasureBuild) CountAnim(fx);
                if (!CanAnim(fx)) return No(0);   // 길이 있는 효과: 모드 애니메이터가 맡을 수 있을 때만 (DecoAnim)
            }
            if (!ADOBase.customLevel) return No(1);                                        // 공식 맵: 길이 보정(AdjustDurationForHardbake)이 있다
            if ((int)ADOBase.controller.visualQuality == 10) return No(2);                 // 원래 코드의 그래픽 설정 검사는 원래대로
            if (AnyImg(fx) && !ImgSafe(fx)) return No(3);                                // 이미지·마스크: 준비 실패나 원래 코드가 예외를 낼 이름이면 원래대로
            var tags = tagsRef(fx); var mgr = mgrRef(fx);
            if (tags == null || (object)mgr == null) return No(4);
            var dict = taggedRef(mgr);
            if (dict == null) return No(4);
            // 대상 목록. 태그가 하나이고 그 목록에 중복·null 이 없으면(목록이 바뀔 때만 다시 확인) 게임 목록을 그대로 쓴다.
            // 예전에는 효과마다 중복 거르기 집합을 비웠는데, 1만 4천 개 효과 한 번에 집합이 커진 뒤로는 비울 때마다
            // 큰 배열 전체를 지워서, 장식이 몇 개 없는 효과 수백 개가 몰린 프레임(개발자용 640개)에서 비용이 컸다.
            // 이제는 "이번 효과 번호" 를 적어 두는 방식이라 비울 일이 없다.
            if (tags.Count == 1)
            {
                var tag = tags[0];
                if (tag == null) return No(5);
                List<scrDecoration> l;
                if (!dict.TryGetValue(tag, out l)) { list.Clear(); src = list; return true; }
                if (l == null) return No(5);
                if (Clean(l)) { src = l; return ParallaxOk(fx) || No(0); }
            }
            serial++;
            list.Clear();
            for (int i = 0; i < tags.Count; i++)
            {
                var tag = tags[i];
                if (tag == null) return No(5);
                List<scrDecoration> l;
                if (!dict.TryGetValue(tag, out l)) continue;
                if (l == null) return No(5);
                for (int j = 0; j < l.Count; j++)
                {
                    var dec = l[j];
                    if ((object)dec == null) return No(5);
                    int s;
                    if (stamp.TryGetValue(dec, out s) && s == serial) continue;
                    stamp[dec] = serial;
                    list.Add(dec);
                }
            }
            src = list;
            return ParallaxOk(fx) || No(0);   // 시차 부품 없는 장식이 섞인 시차 배율 애니메이션은 원래대로
        }

        private static List<scrDecoration> src;
        private static readonly Dictionary<scrDecoration, int> stamp = new Dictionary<scrDecoration, int>(RefEq.I);
        private static int serial;
        private sealed class ListEq : IEqualityComparer<List<scrDecoration>>
        {
            internal static readonly ListEq I = new ListEq();
            public bool Equals(List<scrDecoration> a, List<scrDecoration> b) { return ReferenceEquals(a, b); }
            public int GetHashCode(List<scrDecoration> o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }
        private static readonly Dictionary<List<scrDecoration>, int> cleanAt = new Dictionary<List<scrDecoration>, int>(ListEq.I);   // 중복·null 없음을 확인한 목록 -> 그때의 버전
        private static AccessTools.FieldRef<List<scrDecoration>, int> versionRef;
        private static bool versionTried;
        // 목록에 중복과 null 이 없는가 (Distinct 를 거쳐도 그대로인 목록). 목록 버전이 그대로면 다시 보지 않는다.
        private static bool Clean(List<scrDecoration> l)
        {
            if (!versionTried) { versionTried = true; try { versionRef = AccessTools.FieldRefAccess<List<scrDecoration>, int>("_version"); } catch { versionRef = null; } }
            if (versionRef == null) return false;
            int ver = versionRef(l), at;
            if (cleanAt.TryGetValue(l, out at) && at == ver) return true;
            serial++;
            for (int j = 0; j < l.Count; j++)
            {
                var dec = l[j];
                if ((object)dec == null) return false;
                int s;
                if (stamp.TryGetValue(dec, out s) && s == serial) return false;
                stamp[dec] = serial;
            }
            cleanAt[l] = ver;
            return true;
        }
        // ── (측정용) 길이 있는 장식 이동 효과가 쓰는 속성 조합 ──
        // 모드 쪽 애니메이터를 어느 속성부터 맡을지 정하려고, 효과마다 쓰는 속성(애니메이션 키)과 대상 수를 센다.
        private static readonly Dictionary<int, long[]> animMix = new Dictionary<int, long[]>();   // 키 묶음 -> [효과 수, 대상 장식 수]
        private static void CountAnim(ffxMoveDecorationsPlus fx)
        {
            bool move = !fdt(fx);
            var tp = tPos(fx); var a = tParOff(fx); var b = tPiv(fx);
            Vector2 sc = !float.IsNaN(tScale(fx)) ? new Vector2(tScale(fx), tScale(fx)) : tScaleV2(fx);
            int k = 0;
            if (move && posUsed(fx)) { if (!float.IsNaN(tp.x)) k |= 1 << 1; if (!float.IsNaN(tp.y)) k |= 1 << 2; }
            if (move && parOffUsed(fx)) { if (!float.IsNaN(a.x)) k |= 1 << 12; if (!float.IsNaN(a.y)) k |= 1 << 13; }
            if (move && pivUsed(fx)) { if (!float.IsNaN(b.x)) k |= 1 << 3; if (!float.IsNaN(b.y)) k |= 1 << 4; }
            if (move && rotUsed(fx)) k |= 1 << 5;
            if (move && scaleUsed(fx)) { if (!float.IsNaN(sc.x)) k |= 1 << 7; if (!float.IsNaN(sc.y)) k |= 1 << 8; }
            if (colUsed(fx)) k |= 1 << 9;
            if (opaUsed(fx)) k |= 1 << 10;
            if (parUsed(fx)) k |= 1 << 11;
            if (mtUsed(fx)) k |= 1 << 20;
            if (visUsed(fx) || depthUsed(fx)) k |= 1 << 21;
            if (imgUsed(fx) || sizeUsed(fx) || smoothUsed(fx) || maskTypeUsed(fx) || maskTargetUsed(fx) || maskDepthUsed(fx) || maskFrontUsed(fx) || maskBackUsed(fx)) k |= 1 << 22;
            if (move && posUsed(fx) && (int)mtRef(fx) == 7) k |= 1 << 23;
            long[] v;
            if (!animMix.TryGetValue(k, out v)) { v = new long[2]; animMix[k] = v; }
            v[0]++; v[1] += TargetCount(fx);
        }
        private static string MixName(int k)
        {
            var sb = new System.Text.StringBuilder();
            string[] n = { "", "위치X", "위치Y", "피벗X", "피벗Y", "회전", "", "크기X", "크기Y", "색", "불투명도", "시차배율", "시차X", "시차Y" };
            for (int i = 1; i < n.Length; i++) if ((k & (1 << i)) != 0 && n[i].Length > 0) sb.Append(sb.Length > 0 ? "+" : "").Append(n[i]);
            if ((k & (1 << 20)) != 0) sb.Append("+배치");
            if ((k & (1 << 21)) != 0) sb.Append("+보이기·깊이");
            if ((k & (1 << 22)) != 0) sb.Append("+이미지·마스크");
            if ((k & (1 << 23)) != 0) sb.Append("(상대)");
            return sb.Length > 0 ? sb.ToString() : "없음";
        }
        internal static string AnimSummary()
        {
            if (animMix.Count == 0) return "";
            var list = new List<KeyValuePair<int, long[]>>(animMix);
            list.Sort((x, y) => y.Value[1].CompareTo(x.Value[1]));
            var sb = new System.Text.StringBuilder(" | 길이 있는 장식 이동 (대상 장식 수 많은 순):");
            for (int i = 0; i < list.Count && i < 8; i++) sb.AppendFormat(" {0} 효과 {1}개 장식 {2}개,", MixName(list[i].Key), list[i].Value[0], list[i].Value[1]);
            return sb.ToString().TrimEnd(',');
        }
        // ── 이미지·원래 크기·부드럽게·마스크 블록 (원래 코드 IL 2526~2881 과 같은 순서) ──
        // 깊이 다음에: 파티클 장식이면 파티클 이미지, 이미지 장식이면 이미지 -> 원래 크기 -> 부드럽게(값 넣고 SetSprite(null, true))
        //   -> 마스크 종류 -> 마스크 대상 -> 마스크 깊이 사용 -> 앞/뒤 마스크 깊이
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, string> tImg = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, string>("targetImageFilename");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, Vector2> tSize = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, Vector2>("targetOriginalSize");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, bool> tSmooth = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, bool>("targetSmoothing");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, MaskingType> tMaskType = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, MaskingType>("targetMaskingType");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, string> tMaskTarget = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, string>("targetmaskingTarget");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, bool> tUseMaskDepth = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, bool>("targetUseMaskingDepth");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, int> tFront = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, int>("targetMaskingFrontDepth");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, int> tBack = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, int>("targetMaskingBackDepth");
        private static readonly AccessTools.FieldRef<scrDecorationManager, TextureManager> imageHolderRef = AccessTools.FieldRefAccess<scrDecorationManager, TextureManager>("imageHolder");
        private static readonly AccessTools.FieldRef<TextureManager, Dictionary<string, TextureManager.CustomSprite>> spritesRef = AccessTools.FieldRefAccess<TextureManager, Dictionary<string, TextureManager.CustomSprite>>("customSprites");
        private static readonly AccessTools.FieldRef<scrVisualDecoration, bool> smoothingRef = AccessTools.FieldRefAccess<scrVisualDecoration, bool>("smoothing");
        private static Action<scrVisualDecoration, TextureManager.CustomSprite, bool> setSprite;
        private static Action<scrVisualDecoration, Vector2> setTexScale;
        private static Action<scrVisualDecoration, MaskingType> setMaskType;
        private static Action<scrVisualDecoration, string> setMaskTarget;
        private static Action<scrParticleDecoration, TextureManager.CustomSprite> particleSetSprite;
        private static Action<scrVisualDecoration, bool, int?, int?> maskDepth3;
        private static Action<scrVisualDecoration, int?, int?> maskDepth2;
        private static bool imgReady;
        private static bool ImgInstall()
        {
            if (imgReady) return true;
            try
            {
                var v = typeof(scrVisualDecoration);
                setSprite = AccessTools.MethodDelegate<Action<scrVisualDecoration, TextureManager.CustomSprite, bool>>(AccessTools.Method(v, "SetSprite", new[] { typeof(TextureManager.CustomSprite), typeof(bool) }));
                setTexScale = AccessTools.MethodDelegate<Action<scrVisualDecoration, Vector2>>(AccessTools.Method(v, "SetTextureScaleMultiplier", new[] { typeof(Vector2) }));
                setMaskType = AccessTools.MethodDelegate<Action<scrVisualDecoration, MaskingType>>(AccessTools.Method(v, "SetMaskingType", new[] { typeof(MaskingType) }));
                setMaskTarget = AccessTools.MethodDelegate<Action<scrVisualDecoration, string>>(AccessTools.Method(v, "SetMaskingTarget", new[] { typeof(string) }));
                foreach (var m in typeof(scrParticleDecoration).GetMethods(AccessTools.all))
                    if (m.Name == "SetSprite" && m.GetParameters().Length == 1 && m.DeclaringType == typeof(scrParticleDecoration)) particleSetSprite = AccessTools.MethodDelegate<Action<scrParticleDecoration, TextureManager.CustomSprite>>(m);
                foreach (var m in v.GetMethods(AccessTools.all))
                {
                    if (m.Name != "SetMaskingDepth" || m.DeclaringType != v) continue;
                    if (m.GetParameters().Length == 3) maskDepth3 = AccessTools.MethodDelegate<Action<scrVisualDecoration, bool, int?, int?>>(m);
                    else if (m.GetParameters().Length == 2) maskDepth2 = AccessTools.MethodDelegate<Action<scrVisualDecoration, int?, int?>>(m);
                }
                imgReady = setSprite != null && setTexScale != null && setMaskType != null && setMaskTarget != null && particleSetSprite != null && maskDepth3 != null && maskDepth2 != null;
            }
            catch (Exception ex) { if (First.Length < 300) First += " [이미지 블록 준비 실패: " + ex.Message + "]"; imgReady = false; }
            return imgReady;
        }
        private static void ImageBlock(ffxMoveDecorationsPlus fx, scrDecoration dec)
        {
            bool isVisual = dec is scrVisualDecoration, isParticle = dec is scrParticleDecoration;
            if (isParticle && imgUsed(fx))
            {
                string fn = tImg(fx);
                bool has = !string.IsNullOrEmpty(fn);
                var sprites = spritesRef(imageHolderRef(scrDecorationManager.instance));
                particleSetSprite((scrParticleDecoration)dec, has ? sprites[fn] : null);   // 원래도 사전 [] (없는 이름이면 Take 에서 원래 코드로 돌렸다)
            }
            if (!isVisual) return;
            var vis = (scrVisualDecoration)dec;
            if (imgUsed(fx))
            {
                var sprites = spritesRef(imageHolderRef(scrDecorationManager.instance));
                TextureManager.CustomSprite cs;
                string key = tImg(fx) ?? string.Empty;
                if (!sprites.TryGetValue(key, out cs)) cs = null;   // GetValueOrDefault(키, null)
                setSprite(vis, cs, false);
            }
            if (sizeUsed(fx)) setTexScale(vis, tSize(fx));
            if (smoothUsed(fx)) { smoothingRef(vis) = tSmooth(fx); setSprite(vis, null, true); }
            if (maskTypeUsed(fx)) setMaskType(vis, tMaskType(fx));
            if (maskTargetUsed(fx)) setMaskTarget(vis, tMaskTarget(fx));
            if (maskDepthUsed(fx)) maskDepth3(vis, tUseMaskDepth(fx), null, null);
            if (maskFrontUsed(fx) || maskBackUsed(fx))
                maskDepth2(vis, maskFrontUsed(fx) ? (int?)tFront(fx) : null, maskBackUsed(fx) ? (int?)tBack(fx) : null);
        }
        // 원래 코드와 똑같이 돌릴 수 있는가: 준비가 됐고, 이미지 사전이 있고, 파티클 장식이 쓸 이름이 사전에 있다
        // (원래 코드는 파티클 장식에 사전 [] 를 써서 없는 이름이면 예외로 루프가 멈춘다 -> 그런 효과는 원래 코드로)
        private static bool ImgSafe(ffxMoveDecorationsPlus fx)
        {
            if (!ImgInstall()) return false;
            var mgr = scrDecorationManager.instance;
            if ((object)mgr == null) return false;
            var holder = imageHolderRef(mgr);
            if ((object)holder == null) return false;
            var sprites = spritesRef(holder);
            if (sprites == null) return false;
            if (imgUsed(fx)) { string fn = tImg(fx); if (!string.IsNullOrEmpty(fn) && !sprites.ContainsKey(fn)) return false; }
            return true;
        }
        private static bool AnyImg(ffxMoveDecorationsPlus fx)
        {
            return imgUsed(fx) || sizeUsed(fx) || smoothUsed(fx) || maskTypeUsed(fx) || maskTargetUsed(fx) || maskDepthUsed(fx) || maskFrontUsed(fx) || maskBackUsed(fx);
        }

        // 길이 있는 효과를 모드 애니메이터(DecoAnim)로 맡을 수 있는가: 위치·회전·크기·색·불투명도만 (피벗·시차 오프셋·시차 배율이 섞이면 원래대로),
        // 곡 시작 직후(되감기 구간)가 아님
        private static bool CanAnim(ffxMoveDecorationsPlus fx)
        {
            if (!DecoAnim.Active || EffectBudget.InGrace) return false;
            if (DecoAnim.CanPivotParallax) return true;   // 피벗·시차 오프셋·시차 배율도 모드 애니메이터가 맡는다 (시차 부품 확인은 대상 목록을 만든 뒤 ParallaxOk)
            if (parUsed(fx)) return false;
            if (!fdt(fx))
            {
                var a = tParOff(fx); if (parOffUsed(fx) && (!float.IsNaN(a.x) || !float.IsNaN(a.y))) return false;
                var b = tPiv(fx); if (pivUsed(fx) && (!float.IsNaN(b.x) || !float.IsNaN(b.y))) return false;
            }
            return true;
        }
        // 시차 배율 블록은 만들 때 dec.parallax.multiplier 를 읽는다. 시차 부품이 없는 장식이 섞이면 원래 코드는 그 자리에서 예외가 나므로
        // (뒤 장식은 처리 안 됨) 똑같이 흉내 낼 수 없다. 그런 효과는 원래대로 둔다.
        private static bool ParallaxOk(ffxMoveDecorationsPlus fx)
        {
            if (!(durRef(fx) > 0f) || !parUsed(fx)) return true;
            for (int i = 0; i < src.Count; i++) if (!DecoAnim.HasParallax(src[i])) return false;
            return true;
        }
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, float> tRot = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, float>("targetRot");
        internal static long AnimEffects, ImgEffects;
        private static bool No(int w) { why[w]++; Fallbacks++; if (Edition.Dev) MoveProf.Fallback(w); return false; }

        private static void Run(ffxMoveDecorationsPlus fx)
        {
            Effects++; DecoCount += src.Count;
            if (!float.IsNaN(tScale(fx))) tScaleV2(fx) = new Vector2(tScale(fx), tScale(fx));
            Vector2 sc = tScaleV2(fx);
            bool placement = mtUsed(fx) && (int)mtRef(fx) != 7, relative = (int)mtRef(fx) == 7, move = !fdt(fx);
            bool pos = move && posUsed(fx), parOff = move && parOffUsed(fx), piv = move && pivUsed(fx), rot = move && rotUsed(fx), scale = move && scaleUsed(fx);
            bool col = colUsed(fx), opa = opaUsed(fx), par = parUsed(fx), vis = visUsed(fx), dep = depthUsed(fx), img = AnyImg(fx);
            var tp = tPos(fx); var tpo = tParOff(fx); var tpv = tPiv(fx);
            bool px = !float.IsNaN(tp.x), py = !float.IsNaN(tp.y), pox = !float.IsNaN(tpo.x), poy = !float.IsNaN(tpo.y), pvx = !float.IsNaN(tpv.x), pvy = !float.IsNaN(tpv.y);
            bool sx = !float.IsNaN(sc.x), sy = !float.IsNaN(sc.y);
            bool anim = durRef(fx) > 0f;   // 길이 있는 효과: 모드 애니메이터(DecoAnim)로
            float dur = durRef(fx); var ease = easeRef(fx);
            float k = (!anim && (scale || par)) ? ZeroTween.EaseEnd(ease) : 1f;
            if (anim) AnimEffects++;
            if (img) ImgEffects++;
            Vector2 parTarget = tParallax(fx) / 100f;
            var mt = mtRef(fx); bool visV = visible(fx); int depth = tDepth(fx);

            // 이 효과가 장식마다 쓰는 애니메이션 키 (NoKill 확인용)
            Array.Clear(used, 0, used.Length); usedCount = 0;
            if (pos) { if (px) Use(1); if (py) Use(2); }
            if (parOff) { if (pox) Use(12); if (poy) Use(13); }
            if (piv) { if (pvx) Use(3); if (pvy) Use(4); }
            if (rot) Use(5);
            if (scale) { if (sx) Use(7); if (sy) Use(8); }
            if (col) Use(9);
            if (opa) Use(10);
            if (par) Use(11);

            InstantMove.InLoop = true;
            try
            {
                var targets = src;
                for (int i = 0; i < targets.Count; i++)
                {
                    var dec = targets[i];
                    var d = tweensRef(dec);
                    if (anim)
                    {
                        // 원래 블록 순서: 배치 -> 위치X/Y -> 시차 오프셋X/Y -> 피벗X/Y -> 회전 -> 크기X/Y -> 색 -> 불투명도 -> 시차 배율 -> 보이기 -> 깊이
                        if (Precheck.Active != 0) InvisibleSkip.TouchDeco(dec, "애니메이션 시작");   // 애니메이션 칸이 살아 있게 된다
                        if (placement) setPlacement(dec, mt);
                        if (pos)
                        {
                            Vector2 sp = relative ? pivotPosRef(dec) : startPosRef(dec);
                            if (px) DecoAnim.Pos(dec, d, 1, sp.x, tp.x, dur, ease);
                            if (py) DecoAnim.Pos(dec, d, 2, sp.y, tp.y, dur, ease);
                        }
                        if (parOff) { if (pox) DecoAnim.ParOff(dec, d, 12, tpo.x, dur, ease); if (poy) DecoAnim.ParOff(dec, d, 13, tpo.y, dur, ease); }
                        if (piv) { if (pvx) DecoAnim.Piv(dec, d, 3, tpv.x, dur, ease); if (pvy) DecoAnim.Piv(dec, d, 4, tpv.y, dur, ease); }
                        if (rot) DecoAnim.Rot(dec, d, tRot(fx), dur, ease);
                        if (scale) { if (sx) DecoAnim.Scale(dec, d, 7, sc, dur, ease); if (sy) DecoAnim.Scale(dec, d, 8, sc, dur, ease); }
                        if (col) DecoAnim.Col(dec, d, tCol(fx), dur, ease);
                        if (opa) DecoAnim.Opa(dec, d, tOpa(fx), dur, ease);
                        if (par) DecoAnim.Par(dec, d, parTarget, dur, ease);
                        if (vis) setVisible(dec, visV ? !forceHideRef(dec) : false);
                        if (dep) setDepth(dec, depth);
                        if (img) ImageBlock(fx, dec);   // 깊이 다음: 이미지·원래 크기·부드럽게·마스크
                        continue;
                    }
                    InstantMove.DecoStart(AllDead(d));
                    if (placement) setPlacement(dec, mt);
                    if (pos)
                    {
                        Vector2 sp = relative ? pivotPosRef(dec) : startPosRef(dec);
                        if (px) { InstantMove.Begin(); InstantMove.CPosX(fx, dec, d, sp.x); }
                        if (py) { InstantMove.Begin(); InstantMove.CPosY(fx, dec, d, sp.y); }
                    }
                    if (parOff)
                    {
                        if (pox) { InstantMove.Begin(); InstantMove.CParX(fx, dec, d); }
                        if (poy) { InstantMove.Begin(); InstantMove.CParY(fx, dec, d); }
                    }
                    if (piv)
                    {
                        if (pvx) { InstantMove.Begin(); InstantMove.CPivX(fx, dec, d); }
                        if (pvy) { InstantMove.Begin(); InstantMove.CPivY(fx, dec, d); }
                    }
                    if (rot) { InstantMove.Begin(); InstantMove.CRot(fx, dec, d); }
                    if (scale)
                    {
                        if (sx) { InstantMove.Begin(); InstantMove.CScale(dec, d, 7, sc, k); }
                        if (sy) { InstantMove.Begin(); InstantMove.CScale(dec, d, 8, sc, k); }
                    }
                    if (col) { InstantMove.Begin(); InstantMove.CCol(fx, dec, d); }
                    if (opa) { InstantMove.Begin(); InstantMove.COpa(fx, dec, d); }
                    if (par) { InstantMove.Begin(); InstantMove.CParMul(dec, d, parTarget, k); }
                    InstantMove.DecoEnd();
                    if (vis) setVisible(dec, visV ? !forceHideRef(dec) : false);
                    if (dep) setDepth(dec, depth);
                    if (img) ImageBlock(fx, dec);   // 깊이 다음: 이미지·원래 크기·부드럽게·마스크
                }
            }
            finally { InstantMove.InLoop = false; InstantMove.DecoEnd(); }
        }

        private static readonly bool[] used = new bool[32];
        private static int usedCount;
        private static void Use(int k) { if (!used[k]) { used[k] = true; usedCount++; } }
        // 이번 효과가 쓰는 키가 사전에 모두 있고 전부 "끝난 대역" 인가. 사전을 한 번 훑는 게 키마다 찾는 것(76ns)보다 싸다.
        // 하나라도 없거나 살아 있는(또는 다른) 애니메이션이면 false -> 키마다 원래 순서대로 끊는다(끊기가 값을 넣으므로 순서가 중요).
        private static bool AllDead(Dictionary<global::TweenType, Tween> d)
        {
            if (usedCount == 0 || d == null || d.Count < usedCount) return false;
            var dead = InstantMove.Dead;
            int found = 0;
            foreach (var kv in d)
            {
                int key = (int)kv.Key;
                if (key < 0 || key >= used.Length || !used[key]) continue;
                if (!ReferenceEquals(kv.Value, dead)) return false;
                found++;
            }
            return found == usedCount;
        }

        // ── 개발자용 검증: 루프 결과 뒤에 원래 코드를 한 번 더 돌려 아무것도 안 바뀌는지 ──
        private struct S
        {
            public Vector2 Pp, Po, Par, Scale, Mul; public float Rot, Opa; public Color Col, Rc, Src; public bool En, Lz, Hid, Fro, Live;
            public Vector3 Child, PivPos, PivScale; public Quaternion PivRot; public int Order; public Sprite Spr; public Material Mat;
        }
        private static readonly AccessTools.FieldRef<scrDecoration, Vector2> pivotOffRef = AccessTools.FieldRefAccess<scrDecoration, Vector2>("pivotOffsetVec");
        private static readonly AccessTools.FieldRef<scrDecoration, Vector2> parOffRef = AccessTools.FieldRefAccess<scrDecoration, Vector2>("parallaxOffset");
        private static readonly AccessTools.FieldRef<scrDecoration, Vector2> scaleRef = AccessTools.FieldRefAccess<scrDecoration, Vector2>("scaleVec");
        private static readonly AccessTools.FieldRef<scrDecoration, float> rotRef = AccessTools.FieldRefAccess<scrDecoration, float>("rotAngle");
        private static readonly AccessTools.FieldRef<scrDecoration, float> opaRef = AccessTools.FieldRefAccess<scrDecoration, float>("opacity");
        private static readonly AccessTools.FieldRef<scrDecoration, Color> colRef = AccessTools.FieldRefAccess<scrDecoration, Color>("color");
        private static readonly AccessTools.FieldRef<scrDecoration, Color> rcRef = AccessTools.FieldRefAccess<scrDecoration, Color>("rendererColor");
        private static readonly AccessTools.FieldRef<scrDecoration, bool> enRef = AccessTools.FieldRefAccess<scrDecoration, bool>("rendererEnabled");
        private static readonly AccessTools.FieldRef<scrDecoration, scrParallax> parRef = AccessTools.FieldRefAccess<scrDecoration, scrParallax>("parallax");
        private static readonly AccessTools.FieldRef<scrDecoration, Transform> childRef = AccessTools.FieldRefAccess<scrDecoration, Transform>("childTransform");
        private static readonly AccessTools.FieldRef<scrDecoration, Transform> pivotTransRef = AccessTools.FieldRefAccess<scrDecoration, Transform>("pivotTrans");
        private static readonly AccessTools.FieldRef<scrVisualDecoration, SpriteRenderer> srRef = AccessTools.FieldRefAccess<scrVisualDecoration, SpriteRenderer>("spriteRenderer");
        private static readonly List<S> before = new List<S>();
        private static readonly List<bool> hidBefore = new List<bool>();
        internal static long Explained, TruthDiff;   // TruthDiff: 개발자용 정답 표본이라 목록만 다른 것

        private static S Snap(scrDecoration dec)
        {
            var s = new S { Pp = pivotPosRef(dec), Po = pivotOffRef(dec), Par = parOffRef(dec), Scale = scaleRef(dec), Rot = rotRef(dec), Opa = opaRef(dec),
                Col = colRef(dec), Rc = rcRef(dec), En = enRef(dec), Lz = InvisibleSkip.InLazy(dec), Hid = InvisibleSkip.IsHidden(dec) };
            var p = parRef(dec); if (p != null) s.Mul = p.multiplier;
            var ch = childRef(dec); if (ch != null) s.Child = ch.localPosition;
            var pt = pivotTransRef(dec); if (pt != null) { s.PivPos = pt.localPosition; s.PivScale = pt.localScale; s.PivRot = pt.localRotation; }
            var v = dec as scrVisualDecoration; var r = (object)v == null ? null : srRef(v);
            if (r != null) { s.Src = r.color; s.Fro = r.forceRenderingOff; s.Order = r.sortingOrder; s.Spr = r.sprite; s.Mat = r.sharedMaterial; }
            var d = tweensRef(dec);
            if (d != null) foreach (var t in d.Values) if (t != null && t.active) { s.Live = true; break; }
            return s;
        }

        private static void Verify(ffxMoveDecorationsPlus fx, object[] args)
        {
            if (posUsed(fx) && !fdt(fx) && (int)mtRef(fx) == 7) return;   // 상대 이동은 두 번 돌리면 두 번 움직인다
            var decs = new List<scrDecoration>(src);
            before.Clear();
            foreach (var dec in decs) before.Add(Snap(dec));
            bypass = true;
            MoveProf.Pause = true;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { start.Invoke(fx, args); }
            catch (Exception ex) { if (First.Length < 300) First += " [검증 중 원래 코드 예외: " + (ex.InnerException ?? ex).Message + "]"; }
            finally { bypass = false; MoveProf.Pause = false; MoveProf.Exclude(System.Diagnostics.Stopwatch.GetTimestamp() - t0); }
            Checked++;
            for (int i = 0; i < decs.Count; i++)
            {
                var a = before[i]; var b = Snap(decs[i]);
                CheckedDecos++;
                string diff = Diff(a, b);
                // 루프가 보이는 장식을 옮긴 뒤 같은 효과의 색·불투명도로 투명해졌다면, 두 번째 실행에서는 투명한 상태라 위치가 미루기 목록으로 간다.
                // 첫 실행 때 보였으니 루프가 원래 코드와 같은 일을 한 것이다(값은 같고 목록만 다름). 따로 센다.
                if (diff != null && !a.Lz && b.Lz && a.Hid && i < hidBefore.Count && !hidBefore[i] && diff.StartsWith("미루기 목록")) { Explained++; continue; }
                if (diff == null) continue;
                if (diff.StartsWith("미루기 목록") && InvisibleSkip.IsTruthSample(decs[i])) { TruthDiff++; continue; }   // 개발자용 정답 표본: 게임 함수가 미루지 않고 바로 반영한 장식
                Mismatch++;
                if (First.Length < 700) First += " [" + decs[i].name + ": " + diff + "]";
            }
        }

        private static bool E(float a, float b) { return InstantMove.Bits(a) == InstantMove.Bits(b); }
        private static bool E(Vector2 a, Vector2 b) { return E(a.x, b.x) && E(a.y, b.y); }
        private static bool E(Color a, Color b) { return E(a.r, b.r) && E(a.g, b.g) && E(a.b, b.b) && E(a.a, b.a); }
        // 크기·시차 배율은 "지금 값 + (목표 - 지금 값) x 끝점" 이라 두 번째 실행에서 마지막 자리가 달라질 수 있다. 그만큼만 허용한다.
        private static bool Near(float a, float b) { return Mathf.Abs(a - b) <= 1e-6f * Mathf.Max(1f, Mathf.Abs(a)); }
        private static bool Near(Vector2 a, Vector2 b) { return Near(a.x, b.x) && Near(a.y, b.y); }
        private static bool Near(Vector3 a, Vector3 b) { return (a - b).sqrMagnitude <= 1e-10f * Mathf.Max(1f, a.sqrMagnitude); }

        private static string Diff(S a, S b)
        {
            if (!E(a.Pp, b.Pp)) return "위치 " + a.Pp.ToString("R") + " -> " + b.Pp.ToString("R");
            if (!E(a.Po, b.Po)) return "피벗 " + a.Po.ToString("R") + " -> " + b.Po.ToString("R");
            if (!E(a.Par, b.Par)) return "시차 오프셋 " + a.Par.ToString("R") + " -> " + b.Par.ToString("R");
            if (!E(a.Rot, b.Rot)) return "회전 " + a.Rot.ToString("R") + " -> " + b.Rot.ToString("R");
            if (!E(a.Opa, b.Opa)) return "불투명도 " + a.Opa.ToString("R") + " -> " + b.Opa.ToString("R");
            if (!E(a.Col, b.Col)) return "색 " + a.Col.ToString("R") + " -> " + b.Col.ToString("R");
            if (!E(a.Rc, b.Rc)) return "그리기 색 " + a.Rc.ToString("R") + " -> " + b.Rc.ToString("R");
            // 안 그리는 장식의 엔진 색은 보이지 않고, 다시 보이는 순간 게임이 새로 넣는다 (투명한 채 색만 저장하는 길)
            if (!(a.Hid && b.Hid) && !E(a.Src, b.Src)) return "엔진 색 " + a.Src.ToString("R") + " -> " + b.Src.ToString("R");
            if (!Near(a.Scale, b.Scale)) return "크기 " + a.Scale.ToString("R") + " -> " + b.Scale.ToString("R");
            if (!Near(a.Mul, b.Mul)) return "시차 배율 " + a.Mul.ToString("R") + " -> " + b.Mul.ToString("R");
            if (a.En != b.En) return "보이기 " + a.En + " -> " + b.En;
            if (a.Lz != b.Lz) return "미루기 목록 " + a.Lz + " -> " + b.Lz;
            if (a.Hid != b.Hid || a.Fro != b.Fro) return "안 그림 " + a.Fro + " -> " + b.Fro;
            if (a.Order != b.Order) return "깊이 " + a.Order + " -> " + b.Order;
            if (!ReferenceEquals(a.Spr, b.Spr)) return "이미지";
            if (!ReferenceEquals(a.Mat, b.Mat)) return "재질";
            if (a.Live != b.Live) return "살아있는 애니메이션 " + a.Live + " -> " + b.Live;
            if (!Near(a.Child, b.Child)) return "안쪽 위치 " + a.Child.ToString("F5") + " -> " + b.Child.ToString("F5");
            if (!Near(a.PivPos, b.PivPos)) return "바깥 위치 " + a.PivPos.ToString("F5") + " -> " + b.PivPos.ToString("F5");
            if (!Near(a.PivScale, b.PivScale)) return "바깥 크기 " + a.PivScale.ToString("F5") + " -> " + b.PivScale.ToString("F5");
            if (Quaternion.Angle(a.PivRot, b.PivRot) > 0.001f) return "바깥 회전 " + Quaternion.Angle(a.PivRot, b.PivRot).ToString("F4") + "도";
            return null;
        }

        internal static string Summary()
        {
            if (Effects == 0 && Fallbacks == 0) return "";
            string s = string.Format(" | 장식 이동 루프: 효과 {0}개(장식 {1}개), 원래 코드로 넘긴 효과 {2}개 [길이 있음 {3}, 공식 맵 {4}, 그래픽 설정 {5}, 이미지 준비 안 됨·없는 이미지 {6}, 대상 없음 {7}, null {8}], 이미지·마스크 바꾸는 효과 맡음 {9}개",
                Effects, DecoCount, Fallbacks, why[0], why[1], why[2], why[3], why[4], why[5], ImgEffects);
            if (Edition.Dev) s += " (검증 " + Checked + "번, 장식 " + CheckedDecos + "개 중 다름 " + Mismatch + ", 보이다 투명해져서 목록만 다른 것 " + Explained + ", 개발자용 정답 표본이라 목록만 다른 것 " + TruthDiff + First + ")";
            else if (First.Length > 0) s += First;
            s += Precheck.Summary();
            s += AnimSummary();
            s += DecoAnim.Summary();
            return s;
        }
        internal static void Reset() { animMix.Clear(); DecoAnim.ResetStats(); AnimEffects = ImgEffects = 0; Precheck.ResetStats(); stamp.Clear(); cleanAt.Clear(); Effects = DecoCount = Fallbacks = Checked = CheckedDecos = Mismatch = Explained = TruthDiff = 0; First = ""; Array.Clear(why, 0, why.Length); }
    }
}
