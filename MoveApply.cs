using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 장식 이동 효과가 도는 동안, 장식마다 "위치 마무리 작업" 을 한 번만 하게 모은다.
    //
    // 측정 (Arche, 즉시 이동 하나 기준): 값 넣기 0.23us, OnUpdate 4.27us, OnComplete 0.35us.
    // OnUpdate 는 게임의 scrDecoration.SetPositionX / SetPositionY / SetColor 를 부르고,
    // SetPositionX/Y 는 둘 다 scrDecoration.SetPosition 으로 들어간다. SetPosition 끝에는 매번
    //   UpdateScreenClamp()  화면 크기 기준 위치 다시 계산
    //   UpdatePosition()     카메라/시차 기준 위치 다시 계산
    // 이 붙어 있다. 한 장식의 X 와 Y 를 따로 옮기면 이 마무리가 두 번 돈다.
    //
    // 그래서 장식 이동 효과가 도는 동안에는 이 두 가지를 미뤄 두고, 효과 하나가 끝날 때 장식마다 한 번씩만 한다.
    // 미루는 범위가 효과 하나 안이라, 다른 코드가 그 사이에 장식 위치를 읽는 일은 없다.
    // 효과가 끝나면(예외가 나도) 반드시 비운다.
    internal static class MoveApply
    {
        internal static bool Enabled = true;
        internal static bool Patched;
        internal static int PatchedCount;
        internal static long Calls, Flushed;   // 미룬 횟수 / 실제로 한 횟수 (차이가 아낀 양)

        private static Action<scrDecoration> clamp, update;
        // 편집기에서 플레이하면 SetPosition 마다 편집기 피벗 표시까지 갱신한다. 장식과 상관없는 전역 작업이라 효과당 한 번이면 된다.
        private static Action<ADOFAI.DecorationPivot, bool> pivotCross;
        private static bool pivotDirty;
        internal static long PivotCalls, PivotDone;
        // (측정) 프레임 단위로 묶으면 몇 번이 될지
        private static readonly HashSet<scrDecoration> frameSet = new HashSet<scrDecoration>();
        private static int frameNo = -1;
        internal static long FrameUnique;
        private static readonly List<scrDecoration> dirty = new List<scrDecoration>();
        private static readonly HashSet<scrDecoration> inList = new HashSet<scrDecoration>();
        private static int depth;

        internal static void Install(Harmony h)
        {
            try
            {
                var set = AccessTools.Method(typeof(scrDecoration), "SetPosition");
                var mClamp = AccessTools.Method(typeof(scrDecoration), "UpdateScreenClamp");
                var mUpdate = AccessTools.Method(typeof(scrDecoration), "UpdatePosition");
                if (set == null || mClamp == null || mUpdate == null) { Main.Entry.Logger.Log("[장식 마무리] 대상 없음"); return; }
                clamp = (Action<scrDecoration>)Delegate.CreateDelegate(typeof(Action<scrDecoration>), mClamp);
                update = (Action<scrDecoration>)Delegate.CreateDelegate(typeof(Action<scrDecoration>), mUpdate);
                var mPivot = AccessTools.Method(typeof(ADOFAI.DecorationPivot), "UpdatePivotCrossImage");
                if (mPivot != null) pivotCross = (Action<ADOFAI.DecorationPivot, bool>)Delegate.CreateDelegate(typeof(Action<ADOFAI.DecorationPivot, bool>), mPivot);

                MethodBase start = null;
                foreach (var m in typeof(ffxMoveDecorationsPlus).GetMethods(AccessTools.all))
                    if (m.Name == "StartEffect" && m.DeclaringType == typeof(ffxMoveDecorationsPlus) && !m.IsAbstract) start = m;
                if (start == null) return;

                h.Patch(set, transpiler: new HarmonyMethod(typeof(MoveApply), nameof(Transpiler)));
                h.Patch(mUpdate, transpiler: new HarmonyMethod(typeof(MoveApply), nameof(Transpiler)));   // 마무리 쪽 위치 쓰기도 같은 값이면 건너뛴다
                if (Edition.Dev) h.Patch(set, prefix: new HarmonyMethod(typeof(MoveApply), nameof(ProfStart)), finalizer: new HarmonyMethod(typeof(MoveApply), nameof(ProfEnd)));
                h.Patch(start, prefix: new HarmonyMethod(typeof(MoveApply), nameof(Enter)), finalizer: new HarmonyMethod(typeof(MoveApply), nameof(Exit)));
                var mgrLate = AccessTools.Method(typeof(scrDecorationManager), "LateUpdate");
                var mgrUpd = AccessTools.Method(typeof(scrDecorationManager), "Update");
                if (mgrUpd != null) h.Patch(mgrUpd, prefix: new HarmonyMethod(typeof(MoveApply), nameof(ManagerUpdatePrefix)), transpiler: new HarmonyMethod(typeof(MoveApply), nameof(ManagerUpdateTranspiler)));
                var mLogic = AccessTools.Method(typeof(scrDecoration), "LogicUpdate");
                if (mLogic != null) logicUpdate = (Action<scrDecoration, bool>)Delegate.CreateDelegate(typeof(Action<scrDecoration, bool>), mLogic);
                if (mgrLate != null) h.Patch(mgrLate, prefix: new HarmonyMethod(typeof(MoveApply), nameof(ManagerLatePrefix)), postfix: new HarmonyMethod(typeof(MoveApply), nameof(ManagerLatePostfix)),
                    transpiler: logicUpdate != null ? new HarmonyMethod(typeof(MoveApply), nameof(ManagerLateTranspiler)) : null);
                Main.Entry.Logger.Log("[장식 마무리] 설치" + (Patched ? "" : " - 모양이 달라 적용 안 함"));
            }
            catch (Exception ex) { Main.Entry.Logger.Error("[장식 마무리] 설치 실패: " + ex.Message); }
        }

        // SetPosition 끝의 두 호출만 우리 것으로 바꾼다.
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int n = 0;
            // "ADOBase.editor 를 가져와 유니티 객체 검사" 한 쌍은 통째로 EditorVisible() 하나로 바꾼다
            for (int i = 0; i + 1 < code.Count; i++)
            {
                var a = code[i].operand as MethodInfo; var b = code[i + 1].operand as MethodInfo;
                if (a == null || b == null) continue;
                if (a.Name != "get_editor" || a.DeclaringType != typeof(ADOBase) || b.Name != "op_Implicit" || b.DeclaringType != typeof(UnityEngine.Object)) continue;
                code[i].operand = AccessTools.Method(typeof(MoveApply), nameof(EditorVisible));
                code[i].opcode = OpCodes.Call;
                code[i + 1].opcode = OpCodes.Nop; code[i + 1].operand = null;
            }
            foreach (var c in code)
            {
                var mi = c.operand as MethodInfo;
                if (mi == null) continue;
                if (mi.DeclaringType != typeof(scrDecoration) && mi.DeclaringType != typeof(ADOFAI.DecorationPivot)
                    && mi.DeclaringType != typeof(UnityEngine.Transform) && mi.DeclaringType != typeof(ADOBase)) continue;
                if (mi.Name == "get_editor" && mi.DeclaringType == typeof(ADOBase)) { c.operand = AccessTools.Method(typeof(MoveApply), nameof(EditorOrNull)); c.opcode = OpCodes.Call; }
                else if (mi.Name == "set_localPosition") { c.operand = AccessTools.Method(typeof(MoveApply), nameof(SetLocal)); c.opcode = OpCodes.Call; }
                else if (mi.Name == "UpdatePivotCrossImage" && pivotCross != null) { c.operand = AccessTools.Method(typeof(MoveApply), nameof(PivotNow)); c.opcode = OpCodes.Call; }
                else if (mi.Name == "UpdateScreenClamp") { c.operand = AccessTools.Method(typeof(MoveApply), nameof(ClampNow)); c.opcode = OpCodes.Call; n++; }
                else if (mi.Name == "UpdatePosition") { c.operand = AccessTools.Method(typeof(MoveApply), nameof(UpdateNow)); c.opcode = OpCodes.Call; n++; }
            }
            PatchedCount += n;
            Patched = PatchedCount >= 2;   // SetPosition 과 UpdatePosition 두 군데를 각각 고친다
            return code;
        }

        // 편집기 피벗 표시는 화면에 하나뿐인 편집기 UI 다. 그런데 장식 위치를 넣을 때마다 갱신해서 곡 하나에 578만 번 불렸다
        // (길이가 있는 애니메이션이 매 프레임 장식 위치를 넣기 때문). 프레임당 한 번만 한다.
        private static ADOFAI.DecorationPivot pivotObj;
        private static bool pivotArg;
        public static void PivotNow(ADOFAI.DecorationPivot p, bool arg)
        {
            PivotCalls++;
            if (profNow && p2 == p1) p2 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (Enabled) { pivotDirty = true; pivotObj = p; pivotArg = arg; return; }
            PivotDone++;
            pivotCross(p, arg);
        }

        // 프레임마다 한 번 (모드 갱신에서 부른다)
        internal static void Tick()
        {
            if (!pivotDirty) return;
            pivotDirty = false;
            PivotDone++;
            try { pivotCross(pivotObj, pivotArg); } catch { }
        }

        // 유니티는 값이 같아도 transform 에 쓰면 자식까지 "바뀜" 처리를 한다(장식은 자식이 여럿이다).
        // 장식 이동은 같은 값을 다시 넣는 경우가 많으므로, 같은 값이면 쓰지 않는다. 읽기는 쓰기보다 훨씬 싸다.
        internal static long PosWrites, PosSkips;

        // 편집기에서 플레이하면 장식을 옮길 때마다 "선택 테두리와 피벗 표시" 를 갱신하려고 편집기를 확인한다.
        // 측정: SetPosition 한 번에 크기 배율 0.17us, 위치 쓰기 ~0, 편집기 검사 0.76us, 나머지 0.72us.
        // 곡 하나에 SetPosition 이 약 500만 번 불리므로 이 검사만 몇 초가 된다. 그런데 곡이 도는 동안에는 편집기 UI 가 보이지 않는다.
        // 그래서 재생 중에는 편집기를 없는 것으로 보여 이 부분을 건너뛴다. 편집 화면으로 돌아가면 다시 원래대로 동작한다.
        internal static long EditorSkips;
        public static scnEditor EditorOrNull()
        {
            if (Enabled && Hitch.Playing) { EditorSkips++; return null; }
            return ADOBase.editor;
        }

        // "편집기가 있나" 는 유니티 객체 검사(op_Implicit)까지 도는데, 재생 중에는 물어볼 것도 없다.
        // 두 호출을 하나로 합쳐 그 검사도 없앤다.
        public static bool EditorVisible()
        {
            if (Enabled && Hitch.Playing) { EditorSkips++; return false; }
            return ADOBase.editor != null;
        }

        // (개발자용) SetPosition 안에서 어디에 시간이 가는지 64번에 한 번 잰다.
        // 구간: 시작 -> 크기 배율 계산 -> transform 쓰기 -> 편집기 검사 -> 마무리 표시 -> 끝
        internal static long ProfN;
        internal static double ProfScale, ProfWrite, ProfEditor, ProfRest;
        private static long profCounter;
        private static bool profNow;
        private static long p0, p1, p2, p3;

        // 개발자용: 옮긴 장식이 투명(그리기에서 뺀 것)인지 센다. 효과 시작 안(depth>0)과 밖(애니메이션 진행 중)을 나눈다.
        // 투명한 장식의 움직임을 화면에 늦게 반영해도 되는지(보일 때 한 번에) 가늠하려는 것이다.
        internal static long MovesIn, MovesOut, HiddenIn, HiddenOut, HiddenHitbox;

        public static void ProfStart(scrDecoration __instance)
        {
            if (Edition.Dev && __instance != null)
            {
                bool inEffect = depth > 0;
                if (inEffect) MovesIn++; else MovesOut++;
                if (InvisibleSkip.IsHidden(__instance))
                {
                    if (inEffect) HiddenIn++; else HiddenOut++;
                    if (__instance.hitbox != 0) HiddenHitbox++;
                }
            }
            ProfStartTimer();
        }

        private static void ProfStartTimer() { profNow = Edition.Dev && (++profCounter % 64) == 0; if (profNow) { p0 = System.Diagnostics.Stopwatch.GetTimestamp(); p1 = p2 = p3 = p0; } }

        public static Exception ProfEnd(Exception __exception)
        {
            if (profNow)
            {
                profNow = false;
                double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                ProfN++;
                ProfScale += (p1 - p0) * f;
                ProfWrite += (p2 - p1) * f;
                ProfEditor += (p3 - p2) * f;
                ProfRest += (System.Diagnostics.Stopwatch.GetTimestamp() - p3) * f;
            }
            return __exception;
        }

        internal static string ProfSummary()
        {
            if (ProfN == 0) return "";
            return string.Format(" | SetPosition 표본 {0}개 평균: 크기 배율 {1:F2}us, 위치 쓰기 {2:F2}us, 편집기 검사 {3:F2}us, 나머지 {4:F2}us",
                ProfN, ProfScale * 1000 / ProfN, ProfWrite * 1000 / ProfN, ProfEditor * 1000 / ProfN, ProfRest * 1000 / ProfN);
        }

        public static void SetLocal(UnityEngine.Transform t, UnityEngine.Vector3 v)
        {
            if (profNow && p1 == p0) p1 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (t == null) return;
            var cur = t.localPosition;
            if (cur.x == v.x && cur.y == v.y && cur.z == v.z) { PosSkips++; return; }
            PosWrites++;
            t.localPosition = v;
        }

        // ── Update 단계에서 보이는 장식의 위치 재계산은 LateUpdate 에 맡긴다 ──
        // 게임은 LateUpdate(scrDecorationManager.LateUpdate -> LogicUpdate)에서 보이는 장식마다 매 프레임 UpdatePosition 을 처음부터 다시 한다.
        // UpdatePosition 을 부르는 곳은 SetPosition 과 LogicUpdate 두 곳뿐이다. 그래서 Update 단계(애니메이션 진행, 효과 시작)에서
        // SetPosition 이 부르는 UpdatePosition 은, 보이는 장식이라면 같은 프레임에 그대로 덮어써진다.
        // X, Y 이동이 따로 걸린 장식은 한 프레임에 같은 계산을 세 번 하고 마지막 한 번만 화면에 남았다.
        // UpdatePosition 은 그 순간의 값(장식 값, 카메라, 부모 타일)만으로 정해지고 이전 호출 결과를 쓰지 않는다(시차 SetTrans 포함).
        // 안전장치: 히트박스 장식 제외(Update 단계 충돌 판정이 위치를 읽음), 지난 프레임에 게임 LateUpdate 가 실제로 돌았을 때만,
        // 그리고 LateUpdate 시점에 안 보이게 돼서 게임이 갱신하지 않는 장식은 끝에서 원래대로 갱신한다.
        internal static bool LateSkip = true;
        internal static long LateSkips, LateFixups, LateNotInList, LateChecked, LateMismatch;
        private static int managerLateFrame = -10;
        private static readonly List<scrDecoration> lateSkipped = new List<scrDecoration>();
        private static readonly HashSet<scrDecoration> lateSkippedSet = new HashSet<scrDecoration>(RefEqDeco.Instance);

        private sealed class RefEqDeco : IEqualityComparer<scrDecoration>
        {
            internal static readonly RefEqDeco Instance = new RefEqDeco();
            public bool Equals(scrDecoration a, scrDecoration b) { return ReferenceEquals(a, b); }
            public int GetHashCode(scrDecoration o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }

        private static bool SkipForLate(scrDecoration d)
        {
            if (!LateSkip || !Enabled || (object)d == null || !Hitch.Playing) return false;
            if (managerLateFrame != UnityEngine.Time.frameCount - 1) return false;   // 이번 프레임 LateUpdate 가 이미 시작됐거나, 지난 프레임에 안 돌았다
            if (d.hitbox != 0 || !d.GetVisible()) return false;
            if (lateSkippedSet.Add(d)) lateSkipped.Add(d);
            LateSkips++;
            return true;
        }

        public static void ManagerLatePrefix() { managerLateFrame = UnityEngine.Time.frameCount; Dormancy.NewFrame(); }

        private static HashSet<scrDecoration> devAll;
        private static readonly AccessTools.FieldRef<scrDecorationManager, List<scrDecoration>> allRef =
            AccessTools.FieldRefAccess<scrDecorationManager, List<scrDecoration>>("allDecorations");

        public static void ManagerLatePostfix(scrDecorationManager __instance)
        {
            if (lateSkipped.Count == 0) return;
            if (Edition.Dev && devAll == null) { try { devAll = new HashSet<scrDecoration>(allRef(__instance), RefEqDeco.Instance); } catch { devAll = new HashSet<scrDecoration>(RefEqDeco.Instance); } }
            for (int i = 0; i < lateSkipped.Count; i++)
            {
                var d = lateSkipped[i];
                if (d == null) continue;
                try
                {
                    if (!d.GetVisible()) { update(d); LateFixups++; continue; }   // 게임이 이번엔 안 해 줬다
                    if (!Edition.Dev) continue;
                    // 개발자용: 게임 목록에 없는 장식이면 LogicUpdate 가 안 돌아서 위치가 안 바뀐다 -> 세고 원래대로 갱신
                    if (!devAll.Contains(d)) { LateNotInList++; update(d); continue; }
                    // 개발자용: 32개 중 1개는 게임이 LateUpdate 에서 만든 값이 "지금 값으로 한 번 더 계산" 과 같은지 본다
                    if ((LateChecked++ & 31) != 0) continue;
                    var p = pivotRef(d);
                    if (p == null) continue;
                    Vector3 a = p.localPosition, s = p.localScale; Quaternion r = p.rotation;
                    update(d);
                    if ((a - p.localPosition).sqrMagnitude > 1e-8f || (s - p.localScale).sqrMagnitude > 1e-8f || Quaternion.Angle(r, p.rotation) > 0.01f) LateMismatch++;
                }
                catch { }
            }
            lateSkipped.Clear();
            lateSkippedSet.Clear();
        }

        private static readonly AccessTools.FieldRef<scrDecoration, Transform> pivotRef = AccessTools.FieldRefAccess<scrDecoration, Transform>("pivotTrans");

        // ── 매 프레임 장식 순회에서 "아무것도 안 바뀌는" 호출 빼기 ──
        // scrDecorationManager.LateUpdate 는 매 프레임 장식 전부(Arche 28,835개)에 LogicUpdate(disableV15Features) 를 부른다. 평소에도 2ms.
        // scrVisualDecoration 의 LogicUpdate 를 IL 로 따라가면:
        //   GetVisible() 이 false 면 UpdatePosition 없음 / disable 이 true 면 UpdateShader 는 EnableMeshRenderer(false) 뿐이고
        //   meshRendererEnabled 가 이미 false 면 그것도 아무것도 안 함 / hitbox 가 0 이면 UpdateHitboxState 없음.
        // 이 조건이 모두 맞으면 호출해도 바뀌는 것이 없으므로 부르지 않는다. (scrVisualDecoration 은 LogicUpdate 를 덮어쓰지 않는다)
        internal static bool LogicSkip = true;
        internal static long LogicSkips, LogicCalls;
        private static readonly AccessTools.FieldRef<scrVisualDecoration, bool> meshOnRef = AccessTools.FieldRefAccess<scrVisualDecoration, bool>("meshRendererEnabled");
        private static Action<scrDecoration, bool> logicUpdate;

        public static void LogicMaybe(scrDecoration d, bool disableShader)
        {
            LogicCalls++;
            if (LogicSkip && Dormancy.Enabled && Dormancy.IsDormant(d, disableShader))
            { LogicSkips++; Dormancy.Sleep(d); return; }
            logicUpdate(d, disableShader);
        }

        public static void ManagerUpdatePrefix() { Dormancy.HitboxNewFrame(); }

        public static IEnumerable<CodeInstruction> ManagerUpdateTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            int lists = 0;
            foreach (var c in instructions)
            {
                var fi = c.operand as FieldInfo;
                if (c.opcode == OpCodes.Ldfld && fi != null && fi.Name == "allDecorations" && fi.DeclaringType == typeof(scrDecorationManager))
                {
                    c.opcode = OpCodes.Call;
                    c.operand = AccessTools.Method(typeof(Dormancy), nameof(Dormancy.HitboxList));
                    lists++;
                }
                yield return c;
            }
            if (lists != 2) Main.Entry.Logger.Log("[히트박스 순회] 목록 읽기 " + lists + "곳 (예상 2곳)");
        }

        public static IEnumerable<CodeInstruction> ManagerLateTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            int n = 0;
            int lists = 0;
            foreach (var c in instructions)
            {
                var mi = c.operand as MethodInfo;
                if (mi != null && mi.Name == "LogicUpdate" && mi.DeclaringType == typeof(scrDecoration))
                {
                    c.opcode = OpCodes.Call;
                    c.operand = AccessTools.Method(typeof(MoveApply), nameof(LogicMaybe));
                    n++;
                }
                var fi = c.operand as FieldInfo;
                if (c.opcode == OpCodes.Ldfld && fi != null && fi.Name == "allDecorations" && fi.DeclaringType == typeof(scrDecorationManager))
                {
                    c.opcode = OpCodes.Call;
                    c.operand = AccessTools.Method(typeof(Dormancy), nameof(Dormancy.List));
                    lists++;
                }
                yield return c;
            }
            if (n != 1 || lists != 2) Main.Entry.Logger.Log("[장식 순회] LogicUpdate 호출 " + n + "곳, 목록 읽기 " + lists + "곳 (예상 1곳, 2곳)");
        }

        public static void ClampNow(scrDecoration d)
        {
            if (profNow && p3 == p2) p3 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (depth > 0 && Enabled) { Mark(d); return; }   // 미룬다 (효과가 끝날 때 한 번)
            clamp(d);
        }

        public static void UpdateNow(scrDecoration d)
        {
            if (SkipForLate(d)) return;
            if (depth > 0 && Enabled) { Mark(d); return; }
            update(d);
        }

        private static void Mark(scrDecoration d)
        {
            Calls++;
            if (d == null) return;
            if (inList.Add(d)) dirty.Add(d);
            if (UnityEngine.Time.frameCount != frameNo) { frameNo = UnityEngine.Time.frameCount; frameSet.Clear(); }
            if (frameSet.Add(d)) FrameUnique++;   // 프레임 단위로 묶었다면 이만큼만 했을 것
        }

        public static void Enter() { if (Enabled) depth++; }

        public static Exception Exit(Exception __exception)
        {
            if (depth > 0 && --depth == 0) Flush();
            return __exception;
        }

        // (개발자용) 마무리 계산에 실제로 얼마나 쓰는지
        internal static double FlushMs;

        private static void Flush()
        {
            long t0 = Edition.Dev ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            for (int i = 0; i < dirty.Count; i++)
            {
                var d = dirty[i];
                if (d == null) continue;
                try { clamp(d); if (!SkipForLate(d)) update(d); Flushed++; } catch { }
            }
            if (Edition.Dev) FlushMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            dirty.Clear();
            inList.Clear();

        }

        internal static string Summary()
        {
            if (Calls == 0) return "미룬 것 없음";
            return string.Format("위치 마무리 {0}번을 {1}번으로 줄임 ({2:F0}% 절약, 마무리에 쓴 시간 {7:F0}ms) | 편집기 피벗 갱신 {4}번을 {5}번으로 | 위치 쓰기 {8}번 중 같은 값이라 건너뜀 {9}번{6}",
                Calls, Flushed, 100.0 * (Calls - Flushed) / Calls, FrameUnique, PivotCalls, PivotDone, Patched ? "" : " (적용 안 됨)", FlushMs, PosWrites + PosSkips, PosSkips)
                + " | 재생 중 편집기 검사 건너뜀 " + EditorSkips + "번" + ProfSummary()
                + (LogicCalls > 0 ? string.Format(" | 매 프레임 장식 순회 {0}번 중 바뀌는 게 없어 뺀 것 {1}번", LogicCalls, LogicSkips) : "")
                + Dormancy.Summary()
                + InstantMove.Summary() + FastMove.Summary()
                + (LateSkips > 0 ? string.Format(" | 보이는 장식 위치 재계산을 LateUpdate 에 맡김 {0}번 (안 보이게 돼서 대신 갱신 {1}번){2}", LateSkips, LateFixups, Edition.Dev ? string.Format(", 검사 {0}개 중 다름 {1}, 게임 목록에 없음 {2}", LateChecked / 32, LateMismatch, LateNotInList) : "") : "")
                + (MovesIn + MovesOut > 0 ? string.Format(" | 옮긴 장식 중 투명: 효과 시작 안 {0}/{1}, 애니메이션 진행 중 {2}/{3} (그중 히트박스 {4})", HiddenIn, MovesIn, HiddenOut, MovesOut, HiddenHitbox) : "");
        }

        internal static void ResetMoves() { MovesIn = MovesOut = HiddenIn = HiddenOut = HiddenHitbox = 0; LateSkips = LateFixups = LateNotInList = LateChecked = LateMismatch = 0; devAll = null; LogicSkips = LogicCalls = 0; Dormancy.ResetStats(); InstantMove.Reset(); FastMove.Reset(); }
        internal static void Reset() { Calls = Flushed = PivotCalls = PivotDone = FrameUnique = 0; FlushMs = 0; PosWrites = PosSkips = 0; ProfN = 0; ProfScale = ProfWrite = ProfEditor = ProfRest = 0; EditorSkips = 0; frameSet.Clear(); ResetMoves(); }
    }
}
