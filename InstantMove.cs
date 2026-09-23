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
    // 장식 이동 효과의 "길이 0" 속성을 게임 코드의 애니메이션 만들기 과정 없이 처리한다.
    //
    // 측정 (Arche 효과 몰림 프레임, 개발자용 116ms): 장식 이동 96ms 중 즉시 이동 대역 만들기 4.7ms, Done(값·콜백) 41.8ms,
    // 이전 것 끊기 1.8ms, 나머지 약 48ms. 나머지는 게임 코드가 속성마다 하는 일이다: 클로저 1개 + 델리게이트 4개(getter,
    // setter, OnUpdate, OnComplete) 할당, 이징·콜백 설정, 사전 저장. 효과 몰림 프레임에 4만 3천 번.
    //
    // 속성 블록의 모양 (IL, 키는 TweenType):
    //   [값이 있으면] 클로저 생성 -> eventTweens 에서 이전 것 찾아 Kill(true) -> DOTween.To(...).SetEase.OnUpdate.OnComplete.Done()
    //   -> eventTweens[키] = 결과
    // 길이 0 이면 (1.3.0 즉시 이동 + 1.3.7 OnUpdate 건너뛰기) 결과는 "이전 것 Kill(true) -> OnComplete 호출 한 번" 이다.
    // OnComplete 9개를 IL 로 확인한 최종 호출:
    //   1 위치X  dec.SetPositionX(startPos.x + targetPos.x, dec.pivotOffsetVec)     2 위치Y  (y 로 같음)
    //   12 시차오프셋X  dec.SetParallaxOffsetX(targetParallaxOffset.x)               13 (y)
    //   3 피벗X  dec.SetPivotX(targetPivot.x)                                        4 (y)
    //   5 회전  dec.SetRotation(targetRot)   9 색  dec.SetColor(targetColor)   10 불투명도  dec.SetOpacity(targetOpacity)
    // 그래서 각 블록 앞에 "길이 0 이면 여기서 처리하고 블록 끝으로" 를 끼운다. 사전에는 이미 끝난 대역을 넣는다
    // (원래도 끝난 애니메이션이 들어 있었고, 다음 Kill 은 아무것도 안 한다). 크기, 시차 배율은 OnComplete 가 없어 그대로 둔다.
    //
    // 검증 (개발자용): 64번에 한 번은 원래 코드로 돌리고, 모드가 예측한 최종값과 원래 코드가 만든 값을 비트 단위로 비교한다.
    internal static class InstantMove
    {
        internal static bool Enabled = true;
        internal static bool Patched;
        internal static int Blocks;
        internal static long Handled, Checked, Mismatch;
        internal static string First = "";
        private static long counter;
        private static Tween dead;

        private static readonly AccessTools.FieldRef<ffxPlusBase, float> durRef = AccessTools.FieldRefAccess<ffxPlusBase, float>("duration");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, Vector2> tPos = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, Vector2>("targetPos");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, Vector2> tPar = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, Vector2>("targetParallaxOffset");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, Vector2> tPiv = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, Vector2>("targetPivot");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, float> tRot = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, float>("targetRot");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, Color> tCol = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, Color>("targetColor");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, float> tOpa = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, float>("targetOpacity");
        private static readonly AccessTools.FieldRef<scrDecoration, Vector2> pivotPosRef = AccessTools.FieldRefAccess<scrDecoration, Vector2>("pivotPosVec");
        private static readonly AccessTools.FieldRef<scrDecoration, Vector2> pivotOffRef = AccessTools.FieldRefAccess<scrDecoration, Vector2>("pivotOffsetVec");
        private static readonly AccessTools.FieldRef<scrDecoration, Vector2> parOffRef = AccessTools.FieldRefAccess<scrDecoration, Vector2>("parallaxOffset");
        private static readonly AccessTools.FieldRef<scrDecoration, float> rotRef = AccessTools.FieldRefAccess<scrDecoration, float>("rotAngle");
        private static readonly AccessTools.FieldRef<scrDecoration, Color> colRef = AccessTools.FieldRefAccess<scrDecoration, Color>("color");
        private static readonly AccessTools.FieldRef<scrDecoration, float> opaRef = AccessTools.FieldRefAccess<scrDecoration, float>("opacity");

        private delegate void SetXY(scrDecoration d, float v, Vector2 off);
        private static SetXY setPosX, setPosY;
        private static Action<scrDecoration, float> setParX, setParY, setPivX, setPivY, setRot, setOpa;
        private static Action<scrDecoration, Color> setCol;

        // 클로저 번호 -> (키, 도우미 이름). 번호와 키가 둘 다 맞아야 끼운다.
        private static readonly Dictionary<string, KeyValuePair<int, string>> blocks = new Dictionary<string, KeyValuePair<int, string>>
        {
            { "<>c__DisplayClass45_2", new KeyValuePair<int, string>(1, nameof(PosX)) },
            { "<>c__DisplayClass45_3", new KeyValuePair<int, string>(2, nameof(PosY)) },
            { "<>c__DisplayClass45_4", new KeyValuePair<int, string>(12, nameof(ParX)) },
            { "<>c__DisplayClass45_5", new KeyValuePair<int, string>(13, nameof(ParY)) },
            { "<>c__DisplayClass45_6", new KeyValuePair<int, string>(3, nameof(PivX)) },
            { "<>c__DisplayClass45_7", new KeyValuePair<int, string>(4, nameof(PivY)) },
            { "<>c__DisplayClass45_8", new KeyValuePair<int, string>(5, nameof(Rot)) },
            { "<>c__DisplayClass45_9", new KeyValuePair<int, string>(9, nameof(Col)) },
            { "<>c__DisplayClass45_10", new KeyValuePair<int, string>(10, nameof(Opa)) },
        };

        internal static void Install(Harmony h)
        {
            try
            {
                MethodBase start = null;
                foreach (var m in typeof(ffxMoveDecorationsPlus).GetMethods(AccessTools.all))
                    if (m.Name == "StartEffect" && m.DeclaringType == typeof(ffxMoveDecorationsPlus) && !m.IsAbstract) start = m;
                if (start == null) return;
                var d = typeof(scrDecoration);
                setPosX = AccessTools.MethodDelegate<SetXY>(AccessTools.Method(d, "SetPositionX", new[] { typeof(float), typeof(Vector2) }));
                setPosY = AccessTools.MethodDelegate<SetXY>(AccessTools.Method(d, "SetPositionY", new[] { typeof(float), typeof(Vector2) }));
                setParX = AccessTools.MethodDelegate<Action<scrDecoration, float>>(AccessTools.Method(d, "SetParallaxOffsetX", new[] { typeof(float) }));
                setParY = AccessTools.MethodDelegate<Action<scrDecoration, float>>(AccessTools.Method(d, "SetParallaxOffsetY", new[] { typeof(float) }));
                setPivX = AccessTools.MethodDelegate<Action<scrDecoration, float>>(AccessTools.Method(d, "SetPivotX", new[] { typeof(float) }));
                setPivY = AccessTools.MethodDelegate<Action<scrDecoration, float>>(AccessTools.Method(d, "SetPivotY", new[] { typeof(float) }));
                setRot = AccessTools.MethodDelegate<Action<scrDecoration, float>>(AccessTools.Method(d, "SetRotation", new[] { typeof(float) }));
                setOpa = AccessTools.MethodDelegate<Action<scrDecoration, float>>(AccessTools.Method(d, "SetOpacity", new[] { typeof(float) }));
                setCol = AccessTools.MethodDelegate<Action<scrDecoration, Color>>(AccessTools.Method(d, "SetColor", new[] { typeof(Color) }));
                dead = AccessTools.CreateInstance<TweenerCore<float, float, FloatOptions>>();   // 끝난 애니메이션 자리(active = false)
                h.Patch(start, transpiler: new HarmonyMethod(typeof(InstantMove), nameof(Transpiler)) { priority = Priority.Last },
                    postfix: new HarmonyMethod(typeof(InstantMove), nameof(After)));
                Main.Entry.Logger.Log("[즉시 이동 직접] 설치: 속성 블록 " + Blocks + "개 (예상 9개)");
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[즉시 이동 직접] 설치 실패: " + ex.Message); }
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            Blocks = 0;
            try
            {
                // 필요한 것: 장식 클로저(loc1)의 dec 필드, 위치 블록용 클로저(DisplayClass45_1)의 startPos 와 그 지역 변수, eventTweens 지역 변수(loc2)
                FieldInfo decField = null, startPosField = null;
                object loc1 = null;
                CodeInstruction ldDict = null, ldC1 = null;
                for (int i = 0; i < code.Count; i++)
                {
                    var fi = code[i].operand as FieldInfo;
                    if (fi != null && fi.Name == "dec" && fi.DeclaringType.Name == "<>c__DisplayClass45_0" && decField == null) decField = fi;
                    if (fi != null && fi.Name == "startPos" && fi.DeclaringType.Name == "<>c__DisplayClass45_1") startPosField = fi;
                    if (fi != null && fi.Name == "eventTweens" && code[i].opcode == OpCodes.Ldfld && ldDict == null && i + 1 < code.Count)
                        ldDict = Load(code[i + 1]);   // 바로 다음 stloc 이 사전 지역 변수
                    var ci = code[i].operand as ConstructorInfo;
                    if (code[i].opcode == OpCodes.Newobj && ci != null && ci.DeclaringType.Name == "<>c__DisplayClass45_1" && i + 1 < code.Count) ldC1 = Load(code[i + 1]);
                    if (code[i].opcode == OpCodes.Newobj && ci != null && ci.DeclaringType.Name == "<>c__DisplayClass45_0" && loc1 == null && i + 1 < code.Count) loc1 = code[i + 1];
                }
                var ldLoc1 = loc1 == null ? null : Load((CodeInstruction)loc1);
                if (decField == null || startPosField == null || ldDict == null || ldC1 == null || ldLoc1 == null)
                {
                    Main.Entry.Logger.Log("[즉시 이동 직접] 필요한 지역 변수를 못 찾아 적용 안 함");
                    return code;
                }

                for (int i = code.Count - 1; i >= 1; i--)
                {
                    var ci = code[i].operand as ConstructorInfo;
                    if (code[i].opcode != OpCodes.Newobj || ci == null) continue;
                    KeyValuePair<int, string> b;
                    if (!blocks.TryGetValue(ci.DeclaringType.Name, out b)) continue;
                    // 바로 앞이 블록을 건너뛰는 조건 분기여야 한다 (값 없음 -> 블록 끝)
                    var br = code[i - 1];
                    if (!(br.opcode == OpCodes.Brtrue || br.opcode == OpCodes.Brtrue_S || br.opcode == OpCodes.Brfalse || br.opcode == OpCodes.Brfalse_S)) continue;
                    // 블록 안의 TryGetValue 키가 예상과 같아야 한다
                    int key = -1;
                    for (int j = i + 1; j < Math.Min(code.Count, i + 12); j++)
                    {
                        var mi = code[j].operand as MethodInfo;
                        if (mi != null && mi.Name == "TryGetValue") { key = KeyOf(code[j - 2]); break; }
                    }
                    if (key != b.Key) { Main.Entry.Logger.Log("[즉시 이동 직접] " + ci.DeclaringType.Name + " 키가 " + key + " (예상 " + b.Key + ") - 건너뜀"); continue; }

                    var end = br.operand;   // 분기 대상. Label 타입 이름은 이 빌드 환경에서 참조할 수 없어 그대로 넘긴다
                    var ins = new List<CodeInstruction>();
                    ins.Add(new CodeInstruction(OpCodes.Ldarg_0));
                    ins.Add(ldLoc1.Clone());
                    ins.Add(new CodeInstruction(OpCodes.Ldfld, decField));
                    ins.Add(ldDict.Clone());
                    if (b.Key == 1 || b.Key == 2)
                    {
                        ins.Add(ldC1.Clone());
                        ins.Add(new CodeInstruction(OpCodes.Ldflda, startPosField));
                        ins.Add(new CodeInstruction(OpCodes.Ldfld, AccessTools.Field(typeof(Vector2), b.Key == 1 ? "x" : "y")));
                    }
                    ins.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(InstantMove), b.Value)));
                    ins.Add(new CodeInstruction(OpCodes.Brtrue, end));
                    // 원래 newobj 로 들어오던 분기 표시가 있으면 우리 첫 명령으로 옮긴다
                    code[i].MoveLabelsTo(ins[0]);
                    code.InsertRange(i, ins);
                    Blocks++;
                }
                Patched = Blocks > 0;
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[즉시 이동 직접] 끼우기 실패, 원래대로 둠: " + ex.Message); return instructions; }
            return code;
        }

        // stloc 계열을 같은 변수의 ldloc 으로
        private static CodeInstruction Load(CodeInstruction st)
        {
            var op = st.opcode;
            if (op == OpCodes.Stloc_0) return new CodeInstruction(OpCodes.Ldloc_0);
            if (op == OpCodes.Stloc_1) return new CodeInstruction(OpCodes.Ldloc_1);
            if (op == OpCodes.Stloc_2) return new CodeInstruction(OpCodes.Ldloc_2);
            if (op == OpCodes.Stloc_3) return new CodeInstruction(OpCodes.Ldloc_3);
            if (op == OpCodes.Stloc_S) return new CodeInstruction(OpCodes.Ldloc_S, st.operand);
            if (op == OpCodes.Stloc) return new CodeInstruction(OpCodes.Ldloc, st.operand);
            return null;
        }

        private static int KeyOf(CodeInstruction c)
        {
            var op = c.opcode;
            if (op == OpCodes.Ldc_I4_0) return 0; if (op == OpCodes.Ldc_I4_1) return 1; if (op == OpCodes.Ldc_I4_2) return 2; if (op == OpCodes.Ldc_I4_3) return 3;
            if (op == OpCodes.Ldc_I4_4) return 4; if (op == OpCodes.Ldc_I4_5) return 5; if (op == OpCodes.Ldc_I4_6) return 6; if (op == OpCodes.Ldc_I4_7) return 7;
            if (op == OpCodes.Ldc_I4_8) return 8;
            if (op == OpCodes.Ldc_I4_S) return Convert.ToInt32(c.operand);
            if (op == OpCodes.Ldc_I4) return Convert.ToInt32(c.operand);
            return -1;
        }

        // ── 도우미: true 면 블록을 처리했다(블록 끝으로 건너뜀), false 면 원래 코드가 돈다 ──
        private static bool Use(ffxMoveDecorationsPlus fx)
        {
            lastSampled = false;
            if (!Enabled || durRef(fx) > 0f) return false;
            if (pending.Count > 0) Verify();
            if (Edition.Dev && (++counter % 64) == 0) { lastSampled = true; return false; }   // 표본: 원래 코드로 돌리고 아래에서 대조
            if (Edition.Dev) t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            return true;
        }

        // 이전 애니메이션 끊기. 사전에 이미 우리 "끝난 대역" 이 들어 있으면 끊을 것도, 다시 넣을 것도 없다(결과가 같다).
        private static bool wasDead;
        private static void Kill(Dictionary<global::TweenType, Tween> d, int key)
        {
            Tween t;
            wasDead = false;
            if (!d.TryGetValue((global::TweenType)key, out t)) return;
            if (ReferenceEquals(t, dead)) { wasDead = true; return; }
            if (t != null) t.Kill(true);
        }

        private static long t0;
        internal static double FrameMs; internal static int FrameN;
        private static bool Done(Dictionary<global::TweenType, Tween> d, int key)
        {
            if (!wasDead) d[(global::TweenType)key] = dead;
            Handled++;
            if (Edition.Dev)
            {
                long dt = System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                FrameN++; FrameMs += dt * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (MoveProf.On) MoveProf.Helper(key, dt);
            }
            return true;
        }
        internal static void ResetFrame() { FrameMs = 0; FrameN = 0; }
        internal static string FrameSummary() { return FrameN == 0 ? "" : string.Format(", 직접 처리 {0}번 {1:F1}ms", FrameN, FrameMs); }

        // ── 값이 그대로면 설정 함수를 부르지 않는다 ──
        // 1.3.8 개발자용 쪼개기(Arche 가장 무거운 프레임): 장식 1만 4천 개 효과에서 위치X 14,086번 중 14,086번, 위치Y 14,063번,
        // 색 13,745번이 "이미 같은 값" 이었고 거의 다 투명한 채로 남았다. 그래도 설정 함수가 돌아 19.5ms 를 썼다.
        // 부르지 않아도 되는 조건 (부르든 안 부르든 게임 상태가 똑같은 경우만):
        //   위치 : 투명 장식 위치 미루기가 이 장식을 미루는 중이고(이미 목록에 있음) 저장된 값이 같다 -> SetPosition 은 같은 값 저장만 한다
        //   색/불투명도 : 투명해서 안 그리는 중인 일반 이미지 장식이고, 색·불투명도가 같고, 다시 계산한 그리기 색이 지금과 비트 단위로 같다
        //          -> ApplyColor 는 같은 색을 다시 넣을 뿐이다 (그리기 끄기 상태, 잠든 장식 상태도 그대로)
        // 개발자용은 건너뛸 것 16개 중 1개를 일부러 불러, 부르기 전후 상태(필드, 엔진 색, 그리기 끄기, 미루기 목록)가 같은지 대조한다.
        internal static bool SkipSame = true;
        internal static long SameSkipped, SameChecked, SameMismatch;
        internal static string SameFirst = "";
        private static long sameCounter;
        private static bool NoopOn { get { return SkipSame && Hitch.Playing; } }

        private static readonly AccessTools.FieldRef<scrDecoration, Color> rcRef = AccessTools.FieldRefAccess<scrDecoration, Color>("rendererColor");
        private static readonly AccessTools.FieldRef<scrDecoration, bool> stickRef = AccessTools.FieldRefAccess<scrDecoration, bool>("stickToFloor");
        private static readonly AccessTools.FieldRef<scrDecoration, scrFloor> floorRef = AccessTools.FieldRefAccess<scrDecoration, scrFloor>("parentFloor");
        private static readonly AccessTools.FieldRef<scrFloor, float> floorOpaRef = AccessTools.FieldRefAccess<scrFloor, float>("opacity");
        private static readonly AccessTools.FieldRef<scrVisualDecoration, SpriteRenderer> srRef = AccessTools.FieldRefAccess<scrVisualDecoration, SpriteRenderer>("spriteRenderer");
        private static readonly AccessTools.FieldRef<scrDecoration, Transform> childRef = AccessTools.FieldRefAccess<scrDecoration, Transform>("childTransform");

        private static bool PosNoop(scrDecoration dec, Vector2 p) { return NoopOn && InvisibleSkip.LazyNoop(dec, p); }

        private static bool ColorNoop(scrDecoration dec, Color c, float o)
        {
            if (!NoopOn || dec.GetType() != typeof(scrVisualDecoration) || !InvisibleSkip.IsHidden(dec)) return false;
            var cc = colRef(dec);
            if (!(Eq(cc.r, c.r) && Eq(cc.g, c.g) && Eq(cc.b, c.b) && Eq(cc.a, c.a) && Eq(opaRef(dec), o))) return false;
            float f = 1f;
            if (stickRef(dec)) { var fl = floorRef(dec); if ((object)fl == null) return false; f = floorOpaRef(fl); }
            float a = c.a * o * f;   // ApplyColor 와 같은 순서: (색 알파 x 불투명도) x 타일 불투명도
            var rc = rcRef(dec);
            return Eq(rc.r, c.r) && Eq(rc.g, c.g) && Eq(rc.b, c.b) && Eq(rc.a, a);
        }

        // true 면 설정 함수를 건너뛴다. 개발자용 표본이면 false 를 돌려 원래대로 부르게 하고, 부른 뒤 SameAfter 에서 대조한다.
        private struct Snap { public Vector2 Pp, Po; public bool Lz, Hid, Fro; public Color Rc, Col, Src; public float Opa; public Vector3 Child; }
        private static Snap snap; private static bool snapPending;
        private static bool Skip(scrDecoration dec)
        {
            if (Edition.Dev && (++sameCounter & 15) == 0) { snap = Take(dec); snapPending = true; return false; }
            SameSkipped++;
            return true;
        }
        private static Snap Take(scrDecoration dec)
        {
            var s = new Snap { Pp = pivotPosRef(dec), Po = pivotOffRef(dec), Lz = InvisibleSkip.InLazy(dec), Hid = InvisibleSkip.IsHidden(dec), Rc = rcRef(dec), Col = colRef(dec), Opa = opaRef(dec) };
            var v = dec as scrVisualDecoration; var r = (object)v == null ? null : srRef(v);
            if (r != null) { s.Src = r.color; s.Fro = r.forceRenderingOff; }
            var ch = childRef(dec); if (ch != null) s.Child = ch.localPosition;
            return s;
        }
        private static void SameAfter(scrDecoration dec, string what)
        {
            if (!snapPending) return;
            snapPending = false;
            var b = Take(dec); var a = snap;
            SameChecked++;
            bool same = V2(a.Pp, b.Pp) && V2(a.Po, b.Po) && a.Lz == b.Lz && a.Hid == b.Hid && a.Fro == b.Fro && C4(a.Rc, b.Rc) && C4(a.Col, b.Col) && C4(a.Src, b.Src)
                && Eq(a.Opa, b.Opa) && Eq(a.Child.x, b.Child.x) && Eq(a.Child.y, b.Child.y) && Eq(a.Child.z, b.Child.z);
            if (same) return;
            SameMismatch++;
            if (SameFirst.Length < 500) SameFirst += string.Format(" [{0}: 미루기 {1}->{2}, 안그림 {3}->{4}, 그리기색 {5}->{6}, 엔진색 {7}->{8}, 안쪽 {9}->{10}]",
                what, a.Lz, b.Lz, a.Fro, b.Fro, a.Rc.ToString("R"), b.Rc.ToString("R"), a.Src.ToString("R"), b.Src.ToString("R"), a.Child.ToString("F5"), b.Child.ToString("F5"));
        }
        private static bool V2(Vector2 a, Vector2 b) { return Eq(a.x, b.x) && Eq(a.y, b.y); }
        private static bool C4(Color a, Color b) { return Eq(a.r, b.r) && Eq(a.g, b.g) && Eq(a.b, b.b) && Eq(a.a, b.a); }
        internal static string SameSummary()
        {
            if (SameSkipped == 0 && SameChecked == 0) return "";
            return " | 값이 그대로라 설정 건너뜀 " + SameSkipped + "번" + (Edition.Dev ? " (대조 " + SameChecked + "번 중 다름 " + SameMismatch + SameFirst + ")" : "");
        }

        // 개발자용 쪼개기(MoveProf): 설정 함수 시간, 이미 같은 값이었는지, 투명한 채로 남았는지
        private static bool mHid; private static long mT;
        private static bool P { get { return Edition.Dev && MoveProf.On; } }
        private static void M0(scrDecoration dec) { mHid = InvisibleSkip.IsHidden(dec); mT = System.Diagnostics.Stopwatch.GetTimestamp(); }
        private static void M1(int key, scrDecoration dec, bool same) { MoveProf.Setter(key, System.Diagnostics.Stopwatch.GetTimestamp() - mT, same, mHid && InvisibleSkip.IsHidden(dec)); }
        private static bool Eq(float a, float b) { return Bits(a) == Bits(b); }

        public static bool PosX(ffxMoveDecorationsPlus fx, scrDecoration dec, Dictionary<global::TweenType, Tween> d, float startX)
        {
            if (!Use(fx)) { Expect(dec, 1, startX + tPos(fx).x); return false; }
            Kill(d, 1); float v = startX + tPos(fx).x;
            var p = pivotPosRef(dec); p.x = v;
            if (PosNoop(dec, p) && Skip(dec)) { if (P) MoveProf.Skipped(1); return Done(d, 1); }
            if (P) { bool same = Eq(pivotPosRef(dec).x, v); M0(dec); setPosX(dec, v, pivotOffRef(dec)); M1(1, dec, same); }
            else setPosX(dec, v, pivotOffRef(dec));
            if (Edition.Dev) SameAfter(dec, "위치X");
            return Done(d, 1);
        }
        public static bool PosY(ffxMoveDecorationsPlus fx, scrDecoration dec, Dictionary<global::TweenType, Tween> d, float startY)
        {
            if (!Use(fx)) { Expect(dec, 2, startY + tPos(fx).y); return false; }
            Kill(d, 2); float v = startY + tPos(fx).y;
            var p = pivotPosRef(dec); p.y = v;
            if (PosNoop(dec, p) && Skip(dec)) { if (P) MoveProf.Skipped(2); return Done(d, 2); }
            if (P) { bool same = Eq(pivotPosRef(dec).y, v); M0(dec); setPosY(dec, v, pivotOffRef(dec)); M1(2, dec, same); }
            else setPosY(dec, v, pivotOffRef(dec));
            if (Edition.Dev) SameAfter(dec, "위치Y");
            return Done(d, 2);
        }
        public static bool ParX(ffxMoveDecorationsPlus fx, scrDecoration dec, Dictionary<global::TweenType, Tween> d)
        {
            if (!Use(fx)) { Expect(dec, 12, tPar(fx).x); return false; }
            Kill(d, 12); float v = tPar(fx).x;
            if (P) { bool same = Eq(parOffRef(dec).x, v); M0(dec); setParX(dec, v); M1(12, dec, same); } else setParX(dec, v);
            return Done(d, 12);
        }
        public static bool ParY(ffxMoveDecorationsPlus fx, scrDecoration dec, Dictionary<global::TweenType, Tween> d)
        {
            if (!Use(fx)) { Expect(dec, 13, tPar(fx).y); return false; }
            Kill(d, 13); float v = tPar(fx).y;
            if (P) { bool same = Eq(parOffRef(dec).y, v); M0(dec); setParY(dec, v); M1(13, dec, same); } else setParY(dec, v);
            return Done(d, 13);
        }
        public static bool PivX(ffxMoveDecorationsPlus fx, scrDecoration dec, Dictionary<global::TweenType, Tween> d)
        {
            if (!Use(fx)) { Expect(dec, 3, tPiv(fx).x); return false; }
            Kill(d, 3); float v = tPiv(fx).x;
            if (P) { bool same = Eq(pivotOffRef(dec).x, v); M0(dec); setPivX(dec, v); M1(3, dec, same); } else setPivX(dec, v);
            return Done(d, 3);
        }
        public static bool PivY(ffxMoveDecorationsPlus fx, scrDecoration dec, Dictionary<global::TweenType, Tween> d)
        {
            if (!Use(fx)) { Expect(dec, 4, tPiv(fx).y); return false; }
            Kill(d, 4); float v = tPiv(fx).y;
            if (P) { bool same = Eq(pivotOffRef(dec).y, v); M0(dec); setPivY(dec, v); M1(4, dec, same); } else setPivY(dec, v);
            return Done(d, 4);
        }
        public static bool Rot(ffxMoveDecorationsPlus fx, scrDecoration dec, Dictionary<global::TweenType, Tween> d)
        {
            if (!Use(fx)) { Expect(dec, 5, tRot(fx)); return false; }
            Kill(d, 5); float v = tRot(fx);
            if (P) { bool same = Eq(rotRef(dec), v); M0(dec); setRot(dec, v); M1(5, dec, same); } else setRot(dec, v);
            return Done(d, 5);
        }
        public static bool Col(ffxMoveDecorationsPlus fx, scrDecoration dec, Dictionary<global::TweenType, Tween> d)
        {
            if (!Use(fx)) { ExpectC(dec, tCol(fx)); return false; }
            Kill(d, 9); Color v = tCol(fx);
            if (ColorNoop(dec, v, opaRef(dec)) && Skip(dec)) { if (P) MoveProf.Skipped(9); return Done(d, 9); }
            if (P) { var c = colRef(dec); bool same = Eq(c.r, v.r) && Eq(c.g, v.g) && Eq(c.b, v.b) && Eq(c.a, v.a); M0(dec); setCol(dec, v); M1(9, dec, same); }
            else setCol(dec, v);
            if (Edition.Dev) SameAfter(dec, "색");
            return Done(d, 9);
        }
        public static bool Opa(ffxMoveDecorationsPlus fx, scrDecoration dec, Dictionary<global::TweenType, Tween> d)
        {
            if (!Use(fx)) { Expect(dec, 10, tOpa(fx)); return false; }
            Kill(d, 10); float v = tOpa(fx);
            if (ColorNoop(dec, colRef(dec), v) && Skip(dec)) { if (P) MoveProf.Skipped(10); return Done(d, 10); }
            if (P) { bool same = Eq(opaRef(dec), v); M0(dec); setOpa(dec, v); M1(10, dec, same); } else setOpa(dec, v);
            if (Edition.Dev) SameAfter(dec, "불투명도");
            return Done(d, 10);
        }

        // ── 개발자용 대조: 원래 코드로 돈 표본이 끝난 뒤 값이 모드 예측과 같은지 ──
        private struct Check { public scrDecoration D; public int Key; public float V; public Color C; }
        private static readonly List<Check> pending = new List<Check>();
        private static void Expect(scrDecoration d, int key, float v) { if (Edition.Dev && IsSample()) pending.Add(new Check { D = d, Key = key, V = v }); }
        private static void ExpectC(scrDecoration d, Color c) { if (Edition.Dev && IsSample()) pending.Add(new Check { D = d, Key = 9, C = c }); }
        // 길이가 있는 효과(원래 코드가 도는 게 정상)는 대조하지 않는다. 표본으로 원래 코드를 돌린 경우만.
        private static bool IsSample() { return lastSampled; }
        private static bool lastSampled;

        private static void Verify()
        {
            foreach (var c in pending)
            {
                if (c.D == null) continue;
                float actual; bool same;
                switch (c.Key)
                {
                    case 1: actual = pivotPosRef(c.D).x; break;
                    case 2: actual = pivotPosRef(c.D).y; break;
                    case 12: actual = parOffRef(c.D).x; break;
                    case 13: actual = parOffRef(c.D).y; break;
                    case 3: actual = pivotOffRef(c.D).x; break;
                    case 4: actual = pivotOffRef(c.D).y; break;
                    case 5: actual = rotRef(c.D); break;
                    case 10: actual = opaRef(c.D); break;
                    case 9:
                        var a = colRef(c.D);
                        same = Bits(a.r) == Bits(c.C.r) && Bits(a.g) == Bits(c.C.g) && Bits(a.b) == Bits(c.C.b) && Bits(a.a) == Bits(c.C.a);
                        Count(same, "색", a.ToString("R"), c.C.ToString("R"));
                        continue;
                    default: continue;
                }
                same = Bits(actual) == Bits(c.V);
                Count(same, "키 " + c.Key, actual.ToString("R"), c.V.ToString("R"));
            }
            pending.Clear();
        }
        // float 의 비트 (BitConverter.GetBytes 는 부를 때마다 배열을 만들어서 뜨거운 길에서 쓰면 안 된다)
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FU { [System.Runtime.InteropServices.FieldOffset(0)] public float F; [System.Runtime.InteropServices.FieldOffset(0)] public int I; }
        internal static int Bits(float f) { FU u = default(FU); u.F = f; return u.I; }
        private static void Count(bool same, string what, string actual, string mine)
        {
            Checked++;
            if (same) return;
            Mismatch++;
            if (First.Length < 400) First += " [" + what + ": 원래 " + actual + ", 모드 " + mine + "]";
        }

        // 효과 하나가 끝나면 남은 대조를 한다
        public static void After() { if (pending.Count > 0) Verify(); }

        internal static string Summary()
        {
            if (Handled == 0 && Checked == 0) return "";
            return " | 즉시 이동 직접 처리 " + Handled + "번" + (Edition.Dev ? " (대조 " + Checked + "번 중 다름 " + Mismatch + First + ")" : "") + SameSummary();
        }
        internal static void Reset() { Handled = Checked = Mismatch = 0; First = ""; pending.Clear(); SameSkipped = SameChecked = SameMismatch = 0; SameFirst = ""; }
    }
}
