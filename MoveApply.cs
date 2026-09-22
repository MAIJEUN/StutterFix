using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

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
                h.Patch(start, prefix: new HarmonyMethod(typeof(MoveApply), nameof(Enter)), finalizer: new HarmonyMethod(typeof(MoveApply), nameof(Exit)));
                Main.Entry.Logger.Log("[장식 마무리] 설치" + (Patched ? "" : " - 모양이 달라 적용 안 함"));
            }
            catch (Exception ex) { Main.Entry.Logger.Error("[장식 마무리] 설치 실패: " + ex.Message); }
        }

        // SetPosition 끝의 두 호출만 우리 것으로 바꾼다.
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int n = 0;
            foreach (var c in code)
            {
                var mi = c.operand as MethodInfo;
                if (mi == null || (mi.DeclaringType != typeof(scrDecoration) && mi.DeclaringType != typeof(ADOFAI.DecorationPivot))) continue;
                if (mi.Name == "UpdatePivotCrossImage" && pivotCross != null) { c.operand = AccessTools.Method(typeof(MoveApply), nameof(PivotNow)); c.opcode = OpCodes.Call; }
                else if (mi.Name == "UpdateScreenClamp") { c.operand = AccessTools.Method(typeof(MoveApply), nameof(ClampNow)); c.opcode = OpCodes.Call; n++; }
                else if (mi.Name == "UpdatePosition") { c.operand = AccessTools.Method(typeof(MoveApply), nameof(UpdateNow)); c.opcode = OpCodes.Call; n++; }
            }
            Patched = n >= 2; PatchedCount = n;
            return code;
        }

        // 편집기 피벗 표시는 화면에 하나뿐인 편집기 UI 다. 그런데 장식 위치를 넣을 때마다 갱신해서 곡 하나에 578만 번 불렸다
        // (길이가 있는 애니메이션이 매 프레임 장식 위치를 넣기 때문). 프레임당 한 번만 한다.
        private static ADOFAI.DecorationPivot pivotObj;
        private static bool pivotArg;
        public static void PivotNow(ADOFAI.DecorationPivot p, bool arg)
        {
            PivotCalls++;
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

        public static void ClampNow(scrDecoration d)
        {
            if (depth > 0 && Enabled) { Mark(d); return; }   // 미룬다 (효과가 끝날 때 한 번)
            clamp(d);
        }

        public static void UpdateNow(scrDecoration d)
        {
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

        private static void Flush()
        {
            for (int i = 0; i < dirty.Count; i++)
            {
                var d = dirty[i];
                if (d == null) continue;
                try { clamp(d); update(d); Flushed++; } catch { }
            }
            dirty.Clear();
            inList.Clear();

        }

        internal static string Summary()
        {
            if (Calls == 0) return "미룬 것 없음";
            return string.Format("위치 마무리 {0}번을 {1}번으로 줄임 ({2:F0}% 절약, 프레임 단위로 묶으면 {3}번) | 편집기 피벗 갱신 {4}번을 {5}번으로{6}",
                Calls, Flushed, 100.0 * (Calls - Flushed) / Calls, FrameUnique, PivotCalls, PivotDone, Patched ? "" : " (적용 안 됨)");
        }

        internal static void Reset() { Calls = Flushed = PivotCalls = PivotDone = FrameUnique = 0; frameSet.Clear(); }
    }
}
