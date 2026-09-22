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
        internal static long Calls, Flushed;   // 미룬 횟수 / 실제로 한 횟수 (차이가 아낀 양)

        private static Action<scrDecoration> clamp, update;
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
                if (mi == null || mi.DeclaringType != typeof(scrDecoration)) continue;
                if (mi.Name == "UpdateScreenClamp") { c.operand = AccessTools.Method(typeof(MoveApply), nameof(ClampNow)); c.opcode = OpCodes.Call; n++; }
                else if (mi.Name == "UpdatePosition") { c.operand = AccessTools.Method(typeof(MoveApply), nameof(UpdateNow)); c.opcode = OpCodes.Call; n++; }
            }
            Patched = n == 2;
            return code;
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
            if (d != null && inList.Add(d)) dirty.Add(d);
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
            return string.Format("위치 마무리 {0}번을 {1}번으로 줄임 ({2:F0}% 절약){3}", Calls, Flushed, 100.0 * (Calls - Flushed) / Calls, Patched ? "" : " (적용 안 됨)");
        }

        internal static void Reset() { Calls = Flushed = 0; }
    }
}
