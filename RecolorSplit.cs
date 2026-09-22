using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace StutterFix
{
    // 타일 색 바꾸기 효과 하나를 타일 구간으로 쪼개 여러 프레임에 나눠 칠한다.
    //
    // ffxRecolorFloorPlus 한 번이 start~end 타일마다 스타일/색/애니메이션을 건다(타일당 약 6us).
    // 이 맵은 한 번에 타일 4800~6800개를 칠해서 103초(41ms), 108초(36ms), 136초(31ms, 30ms) 끊김을 만든다.
    // 효과 몰림 나누기는 효과 "단위"로만 미룰 수 있어서 효과 하나가 무거운 경우는 못 막는다.
    //
    // 원래 코드 (IL 로 확인):
    //   AdjustDurationForHardbake();            공식 레벨에서만 duration 을 재생 속도로 나눈다
    //   if (end < start) start 와 end 를 맞바꿈
    //   for (i = start; i <= end; i += 1 + gapLength) { 타일 i 칠하기 }
    //   펄스 색의 기준점으로 start 필드를 그대로 쓴다
    //
    // 그래서 start/end 필드는 건드리지 않는다(펄스 색이 바뀐다). 루프의 시작값과 끝값만 트랜스파일러로 바꾼다.
    //   첫 호출: 앞쪽 ChunkTiles 칸만 칠하고, 나머지는 조각으로 줄 세운다
    //   다음 프레임들: 같은 효과를 조각의 범위로 다시 부른다. duration 조정은 두 번 하지 않는다
    // 칠하는 값은 원래와 똑같고, 먼 타일이 몇 프레임 늦게 칠해질 뿐이다.
    //
    // 순서: 새 색 바꾸기가 밀린 조각과 겹치는 타일을 칠하려 하면 밀린 조각을 먼저 마저 칠한다.
    public static class RecolorSplit
    {
        internal static bool Enabled = true;
        internal static int ChunkTiles = 400;        // 한 조각의 타일 수 (약 2.5ms)
        internal static float FrameMs = 4f;          // 밀린 조각을 프레임마다 이만큼만 칠한다

        internal static long SplitEffects, DeferredTiles, FlushedForOrder;
        internal static bool Patched;

        private class Piece { public ffxRecolorFloorPlus Effect; public int Start, End; }
        private static readonly List<Piece> pending = new List<Piece>();

        private static readonly AccessTools.FieldRef<ffxRecolorFloorPlus, int> startRef = AccessTools.FieldRefAccess<ffxRecolorFloorPlus, int>("start");
        private static readonly AccessTools.FieldRef<ffxRecolorFloorPlus, int> endRef = AccessTools.FieldRefAccess<ffxRecolorFloorPlus, int>("end");
        private static readonly AccessTools.FieldRef<ffxRecolorFloorPlus, int> gapRef = AccessTools.FieldRefAccess<ffxRecolorFloorPlus, int>("gapLength");

        private static MethodInfo startEffect, adjust;
        private static Piece replay;                  // 지금 다시 부르는 조각
        private static ffxRecolorFloorPlus loopOwner; // 루프 끝값을 바꿔 줄 호출
        private static int loopEnd;

        internal static bool Replaying { get { return replay != null; } }

        internal static void Install(Harmony harmony)
        {
            try
            {
                // 인자 목록을 지정해 찾으면 못 찾았다(선택 인자가 있는 것으로 보인다). EffectScan 처럼 이름으로 찾는다.
                foreach (var m in typeof(ffxRecolorFloorPlus).GetMethods(AccessTools.all))
                    if (m.Name == "StartEffect" && m.DeclaringType == typeof(ffxRecolorFloorPlus) && !m.IsAbstract) startEffect = m;
                foreach (var m in typeof(ffxPlusBase).GetMethods(AccessTools.all))
                    if (m.Name == "AdjustDurationForHardbake") adjust = m;
                if (startEffect == null || adjust == null)
                {
                    Main.Entry.Logger.Error("RecolorSplit: 대상 없음 (StartEffect " + (startEffect != null) + ", Adjust " + (adjust != null) + ")");
                    return;
                }
                if (startEffect.GetParameters().Length > 0)
                    Main.Entry.Logger.Log("RecolorSplit: StartEffect 인자 " + startEffect.GetParameters().Length + "개");
                harmony.Patch(startEffect, transpiler: new HarmonyMethod(typeof(RecolorSplit), nameof(Transpiler)));
                Main.Entry.Logger.Log("patched ffxRecolorFloorPlus.StartEffect (구간 나누기" + (Patched ? ")" : " - 모양이 달라 적용 안 함)"));
            }
            catch (Exception ex) { Main.Entry.Logger.Error("RecolorSplit 설치 실패: " + ex.Message); }
        }

        // 세 군데를 바꾼다. 하나라도 못 찾으면 원래 코드를 그대로 둔다.
        //   call AdjustDurationForHardbake      -> AdjustOnce(this)
        //   ldfld start; stloc.2  (루프 시작값) -> ldfld start; ldarg.0; call LoopFirst
        //   ldfld end; ble        (루프 조건)   -> ldfld end; ldarg.0; call LoopLast
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var fStart = AccessTools.Field(typeof(ffxRecolorFloorPlus), "start");
            var fEnd = AccessTools.Field(typeof(ffxRecolorFloorPlus), "end");
            int iAdjust = -1, iFirst = -1, iLast = -1;

            for (int i = 0; i < code.Count - 1; i++)
            {
                var c = code[i];
                var next = code[i + 1];
                if (iAdjust < 0 && c.Calls(adjust)) iAdjust = i;
                else if (iFirst < 0 && c.LoadsField(fStart) && next.opcode == OpCodes.Stloc_2) iFirst = i;
                else if (c.LoadsField(fEnd) && (next.opcode == OpCodes.Ble || next.opcode == OpCodes.Ble_S)) iLast = i;
            }

            if (iAdjust < 0 || iFirst < 0 || iLast < 0 || iFirst > iLast)
            {
                Patched = false;
                return code;
            }

            // 뒤에서부터 끼워 넣어야 앞의 위치가 밀리지 않는다.
            code.InsertRange(iLast + 1, new[] { new CodeInstruction(OpCodes.Ldarg_0), CodeInstruction.Call(typeof(RecolorSplit), nameof(LoopLast)) });
            code.InsertRange(iFirst + 1, new[] { new CodeInstruction(OpCodes.Ldarg_0), CodeInstruction.Call(typeof(RecolorSplit), nameof(LoopFirst)) });
            code[iAdjust].opcode = OpCodes.Call;   // 분기 표시(label)는 그대로 두고 부르는 대상만 바꾼다
            code[iAdjust].operand = AccessTools.Method(typeof(RecolorSplit), nameof(AdjustOnce));
            Patched = true;
            return code;
        }

        // 조각을 다시 부를 때는 duration 을 또 나누면 안 된다.
        public static void AdjustOnce(ffxPlusBase self)
        {
            if (replay != null) return;
            adjust.Invoke(self, null);
        }

        // 루프 시작 직전에 한 번 불린다. 여기서 이번 호출을 쪼갤지 정한다.
        public static int LoopFirst(int start, ffxRecolorFloorPlus self)
        {
            try
            {
                if (replay != null && replay.Effect == self)
                {
                    loopOwner = self;
                    loopEnd = replay.End;
                    return replay.Start;
                }

                int end = endRef(self);   // 맞바꾸기가 끝난 뒤라 start <= end
                int step = 1 + Math.Max(0, gapRef(self));
                long tiles = (end - (long)start) / step + 1;
                bool split = Enabled && tiles > ChunkTiles && !EffectBudget.InGrace;
                long span = (long)ChunkTiles * step;
                int nowEnd = split ? (int)Math.Min(end, start + span - step) : end;

                // 지금 칠할 구간과 겹치는 밀린 조각만 먼저 칠한다 (이 안에서 다른 호출이 끝까지 돈다).
                // 예전에는 전체 구간과 겹치는 것을 다 칠해서, 137초에 같은 구간 색 바꾸기가 연달아 오자
                // 밀린 4800칸을 한 프레임에 칠했다(24ms). 나머지 구간은 새 조각을 줄 맨 뒤에 세우므로
                // 타일마다 옛 조각 -> 새 조각 순서가 그대로 지켜진다.
                FlushOverlapping(start, nowEnd);

                loopOwner = self;
                loopEnd = end;
                if (!split) return start;

                loopEnd = nowEnd;
                for (long from = start + span; from <= end; from += span)
                    pending.Add(new Piece { Effect = self, Start = (int)from, End = (int)Math.Min(end, from + span - step) });

                SplitEffects++;
                DeferredTiles += tiles - ChunkTiles;
            }
            catch (Exception ex) { Main.Entry.Logger.Error("RecolorSplit: " + ex.Message); loopOwner = null; }
            return start;
        }

        // 루프 조건에서 매번 불린다.
        public static int LoopLast(int end, ffxRecolorFloorPlus self)
        {
            return ReferenceEquals(self, loopOwner) ? loopEnd : end;
        }

        private static void FlushOverlapping(int s, int e)
        {
            for (int i = 0; i < pending.Count; )
            {
                var p = pending[i];
                if (p.End < s || p.Start > e) { i++; continue; }
                pending.RemoveAt(i);
                Run(p);
                FlushedForOrder++;
            }
        }

        // 밀린 조각을 프레임마다 FrameMs 만큼 칠한다.
        internal static void Tick()
        {
            if (pending.Count == 0) return;
            long t0 = Stopwatch.GetTimestamp();
            long limit = (long)(FrameMs / 1000.0 * Stopwatch.Frequency);
            while (pending.Count > 0 && Stopwatch.GetTimestamp() - t0 < limit)
            {
                var p = pending[0];
                pending.RemoveAt(0);
                Run(p);
            }
        }

        private static void Run(Piece p)
        {
            if (p.Effect == null) return;   // 맵이 바뀌어 사라진 효과
            var saved = loopOwner;
            int savedEnd = loopEnd;
            bool guard = TweenFix.Begin();   // 애니메이션 정리 O(n^2) 방지는 여기서도 필요하다
            long runStart = Stopwatch.GetTimestamp();
            replay = p;
            try { startEffect.Invoke(p.Effect, DefaultArgs()); }
            catch (Exception ex) { Main.Entry.Logger.Error("RecolorSplit 조각 실패: " + (ex.InnerException ?? ex).Message); }
            finally
            {
                replay = null;
                ModCost.Add(SettingsWindow.T("타일 색 나눠 칠하기", "Tile recolor batch"), (Stopwatch.GetTimestamp() - runStart) * 1000.0 / Stopwatch.Frequency);
                TweenFix.End(guard);
                loopOwner = saved;
                loopEnd = savedEnd;
            }
        }

        private static object[] defaultArgs;

        private static object[] DefaultArgs()
        {
            if (defaultArgs != null) return defaultArgs;
            var ps = startEffect.GetParameters();
            defaultArgs = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
                defaultArgs[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                    : ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null;
            return defaultArgs;
        }

        // 곡이 끝나거나 다시 시작하면 밀린 것을 버린다.
        internal static void Reset() { pending.Clear(); replay = null; loopOwner = null; }

        internal static int Pending { get { return pending.Count; } }
    }
}
