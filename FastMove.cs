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
        private static readonly HashSet<scrDecoration> seen = new HashSet<scrDecoration>(RefEq.I);
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
            if (bypass || running || !Enabled || !Installed || !Hitch.Playing) return true;
            if (!InstantMove.Enabled || !InstantMove.Patched || !ZeroTween.Enabled || !ZeroTween.Patched || !ZeroTween.CanEase) return true;
            running = true;
            try
            {
                if (!Take(__instance)) return true;   // 원래 코드가 돈다 (아직 아무것도 안 바꿨다)
                bool sample = Edition.Dev && ((Effects + 1) % 16) == 1;
                if (sample) { hidBefore.Clear(); foreach (var dec in list) hidBefore.Add(InvisibleSkip.IsHidden(dec)); }
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

        // 맡을 수 있는지 보고, 맡으면 대상 목록을 만든다. 여기까지는 게임 상태를 바꾸지 않는다.
        private static bool Take(ffxMoveDecorationsPlus fx)
        {
            if (durRef(fx) > 0f) return No(0);
            if (!ADOBase.customLevel) return No(1);                                        // 공식 맵: 길이 보정(AdjustDurationForHardbake)이 있다
            if ((int)ADOBase.controller.visualQuality == 10) return No(2);                 // 원래 코드의 그래픽 설정 검사는 원래대로
            if (imgUsed(fx) || sizeUsed(fx) || smoothUsed(fx) || maskTypeUsed(fx) || maskTargetUsed(fx) || maskDepthUsed(fx) || maskFrontUsed(fx) || maskBackUsed(fx)) return No(3);
            var tags = tagsRef(fx); var mgr = mgrRef(fx);
            if (tags == null || (object)mgr == null) return No(4);
            var dict = taggedRef(mgr);
            if (dict == null) return No(4);
            seen.Clear(); list.Clear();
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
                    if (seen.Add(dec)) list.Add(dec);
                }
            }
            return true;
        }
        private static bool No(int w) { why[w]++; Fallbacks++; return false; }

        private static void Run(ffxMoveDecorationsPlus fx)
        {
            Effects++; DecoCount += list.Count;
            if (!float.IsNaN(tScale(fx))) tScaleV2(fx) = new Vector2(tScale(fx), tScale(fx));
            Vector2 sc = tScaleV2(fx);
            bool placement = mtUsed(fx) && (int)mtRef(fx) != 7, relative = (int)mtRef(fx) == 7, move = !fdt(fx);
            bool pos = move && posUsed(fx), parOff = move && parOffUsed(fx), piv = move && pivUsed(fx), rot = move && rotUsed(fx), scale = move && scaleUsed(fx);
            bool col = colUsed(fx), opa = opaUsed(fx), par = parUsed(fx), vis = visUsed(fx), dep = depthUsed(fx);
            var tp = tPos(fx); var tpo = tParOff(fx); var tpv = tPiv(fx);
            bool px = !float.IsNaN(tp.x), py = !float.IsNaN(tp.y), pox = !float.IsNaN(tpo.x), poy = !float.IsNaN(tpo.y), pvx = !float.IsNaN(tpv.x), pvy = !float.IsNaN(tpv.y);
            bool sx = !float.IsNaN(sc.x), sy = !float.IsNaN(sc.y);
            float k = (scale || par) ? ZeroTween.EaseEnd(easeRef(fx)) : 1f;
            Vector2 parTarget = tParallax(fx) / 100f;
            var mt = mtRef(fx); bool visV = visible(fx); int depth = tDepth(fx);

            for (int i = 0; i < list.Count; i++)
            {
                var dec = list[i];
                var d = tweensRef(dec);
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
                if (vis) setVisible(dec, visV ? !forceHideRef(dec) : false);
                if (dep) setDepth(dec, depth);
            }
        }

        // ── 개발자용 검증: 루프 결과 뒤에 원래 코드를 한 번 더 돌려 아무것도 안 바뀌는지 ──
        private struct S
        {
            public Vector2 Pp, Po, Par, Scale, Mul; public float Rot, Opa; public Color Col, Rc, Src; public bool En, Lz, Hid, Fro, Live;
            public Vector3 Child, PivPos, PivScale; public Quaternion PivRot; public int Order;
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
        internal static long Explained;

        private static S Snap(scrDecoration dec)
        {
            var s = new S { Pp = pivotPosRef(dec), Po = pivotOffRef(dec), Par = parOffRef(dec), Scale = scaleRef(dec), Rot = rotRef(dec), Opa = opaRef(dec),
                Col = colRef(dec), Rc = rcRef(dec), En = enRef(dec), Lz = InvisibleSkip.InLazy(dec), Hid = InvisibleSkip.IsHidden(dec) };
            var p = parRef(dec); if (p != null) s.Mul = p.multiplier;
            var ch = childRef(dec); if (ch != null) s.Child = ch.localPosition;
            var pt = pivotTransRef(dec); if (pt != null) { s.PivPos = pt.localPosition; s.PivScale = pt.localScale; s.PivRot = pt.localRotation; }
            var v = dec as scrVisualDecoration; var r = (object)v == null ? null : srRef(v);
            if (r != null) { s.Src = r.color; s.Fro = r.forceRenderingOff; s.Order = r.sortingOrder; }
            var d = tweensRef(dec);
            if (d != null) foreach (var t in d.Values) if (t != null && t.active) { s.Live = true; break; }
            return s;
        }

        private static void Verify(ffxMoveDecorationsPlus fx, object[] args)
        {
            if (posUsed(fx) && !fdt(fx) && (int)mtRef(fx) == 7) return;   // 상대 이동은 두 번 돌리면 두 번 움직인다
            var decs = new List<scrDecoration>(list);
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
            if (!E(a.Src, b.Src)) return "엔진 색 " + a.Src.ToString("R") + " -> " + b.Src.ToString("R");
            if (!Near(a.Scale, b.Scale)) return "크기 " + a.Scale.ToString("R") + " -> " + b.Scale.ToString("R");
            if (!Near(a.Mul, b.Mul)) return "시차 배율 " + a.Mul.ToString("R") + " -> " + b.Mul.ToString("R");
            if (a.En != b.En) return "보이기 " + a.En + " -> " + b.En;
            if (a.Lz != b.Lz) return "미루기 목록 " + a.Lz + " -> " + b.Lz;
            if (a.Hid != b.Hid || a.Fro != b.Fro) return "안 그림 " + a.Fro + " -> " + b.Fro;
            if (a.Order != b.Order) return "깊이 " + a.Order + " -> " + b.Order;
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
            string s = string.Format(" | 장식 이동 루프: 효과 {0}개(장식 {1}개), 원래 코드로 넘긴 효과 {2}개 [길이 있음 {3}, 공식 맵 {4}, 그래픽 설정 {5}, 이미지·마스크 {6}, 대상 없음 {7}, null {8}]",
                Effects, DecoCount, Fallbacks, why[0], why[1], why[2], why[3], why[4], why[5]);
            if (Edition.Dev) s += " (검증 " + Checked + "번, 장식 " + CheckedDecos + "개 중 다름 " + Mismatch + ", 보이다 투명해져서 목록만 다른 것 " + Explained + First + ")";
            else if (First.Length > 0) s += First;
            return s;
        }
        internal static void Reset() { Effects = DecoCount = Fallbacks = Checked = CheckedDecos = Mismatch = Explained = 0; First = ""; Array.Clear(why, 0, why.Length); }
    }
}
