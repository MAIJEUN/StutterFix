using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using DG.Tweening;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 장식 이동 효과 하나를 장식 단위로 쪼개 여러 프레임에 나눠 시작한다.
    //
    // ffxMoveDecorationsPlus 한 번이 태그로 찾은 장식 수백 개마다 속성별 애니메이션(DOTween)을 끝내고 새로 만든다.
    // Arche 229초: 장식 이동 90개가 133ms(애니메이션 끝내기 44,156번), 다음 프레임 176개가 78ms. 효과 하나가 78ms 인 것도 있었다.
    // 효과 몰림 나누기는 효과 "단위" 로만 미룰 수 있어서, 효과 하나가 무거우면 못 막는다.
    //
    // 원래 코드 (IL 로 확인):
    //   AdjustDurationForHardbake();                       공식 레벨에서만 duration 을 재생 속도로 나눈다
    //   foreach (dec in decManager.GetTaggedDecorations(targetTags)) { 장식 하나 처리 }
    //
    // 바꾸는 것: GetTaggedDecorations 가 돌려준 목록을 우리 목록으로 감싼다.
    //   첫 호출: 4ms 가 지나면 목록을 거기서 끝낸다. 나머지 장식은 "조각" 으로 줄 세운다.
    //   다음 프레임들: 같은 효과를 다시 불러, 목록이 남은 장식부터 이어서 나오게 한다. duration 조정은 두 번 하지 않는다.
    //   늦게 처리한 장식이 새로 만든 애니메이션은 늦은 시간만큼 앞으로 감는다(Goto). 그래서 다음 프레임부터는
    //   원래 있어야 할 위치와 같고, 차이는 "늦은 장식이 한 프레임 동안 이전 모습에 머무는 것" 뿐이다.
    //
    // 순서: 새 장식 이동 효과가 밀린 조각과 같은 장식을 건드리면, 밀린 조각을 먼저 마저 처리한다
    //       (늦게 처리된 옛 애니메이션이 새 애니메이션을 덮어쓰지 않게).
    // 곡 시작 직후(효과 몰림 나누기와 같은 유예 시간)와 곡이 아닐 때(편집 화면)는 나누지 않는다.
    public static class MoveSplit
    {
        internal static bool Enabled = true;
        internal static float FirstMs = 4f;   // 효과가 처음 불린 프레임에 쓸 시간
        internal static float FrameMs = 4f;   // 밀린 조각을 프레임마다 이만큼만 처리한다

        internal static long SplitEffects, DeferredDecos, Flushed;
        internal static bool Patched;

        private class Piece
        {
            public ffxMoveDecorationsPlus Effect;
            public scrPlanet Planet;
            public List<scrDecoration> List;
            public int Next;
            public float At;          // 원래 시작했어야 할 시각 (Time.time)
            public bool Queued;
        }

        private static readonly List<Piece> pending = new List<Piece>();
        private static Piece replay;          // 지금 다시 부르는 조각
        private static float replayBudget;    // 그 호출에서 쓸 수 있는 시간(ms). 무한이면 마저 처리
        private static MethodInfo startEffect, adjust;
        private static readonly AccessTools.FieldRef<scrDecoration, Dictionary<TweenType, Tween>> tweensRef =
            AccessTools.FieldRefAccess<scrDecoration, Dictionary<TweenType, Tween>>("eventTweens");

        internal static bool Replaying { get { return replay != null; } }
        internal static int Pending { get { return pending.Count; } }

        internal static void Install(Harmony harmony)
        {
            try
            {
                foreach (var m in typeof(ffxMoveDecorationsPlus).GetMethods(AccessTools.all))
                    if (m.Name == "StartEffect" && m.DeclaringType == typeof(ffxMoveDecorationsPlus) && !m.IsAbstract) startEffect = m;
                foreach (var m in typeof(ffxPlusBase).GetMethods(AccessTools.all))
                    if (m.Name == "AdjustDurationForHardbake") adjust = m;
                if (startEffect == null || adjust == null) { Main.Entry.Logger.Error("MoveSplit: 대상 없음"); return; }
                harmony.Patch(startEffect, transpiler: new HarmonyMethod(typeof(MoveSplit), nameof(Transpiler)));
                Main.Entry.Logger.Log("patched ffxMoveDecorationsPlus.StartEffect (장식 나누기" + (Patched ? ")" : " - 모양이 달라 적용 안 함)"));
            }
            catch (Exception ex) { Main.Entry.Logger.Error("MoveSplit 설치 실패: " + ex.Message); }
        }

        // 두 군데를 바꾼다. 하나라도 못 찾으면 원래 코드를 그대로 둔다.
        //   call AdjustDurationForHardbake                 -> call AdjustOnce(this)
        //   callvirt GetTaggedDecorations(IEnumerable)     -> 그 뒤에 ldarg.0; ldarg.1; call Wrap
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int a = -1, g = -1;
            for (int i = 0; i < code.Count; i++)
            {
                var mi = code[i].operand as MethodInfo;
                if (mi == null) continue;
                if (mi.Name == "AdjustDurationForHardbake" && a < 0) a = i;
                else if (mi.Name == "GetTaggedDecorations" && g < 0 && mi.ReturnType == typeof(IEnumerable<scrDecoration>)) g = i;
            }
            Patched = false;
            if (a < 0 || g < 0) return code;
            code[a] = new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MoveSplit), nameof(AdjustOnce))).MoveLabelsFrom(code[a]);
            code.InsertRange(g + 1, new[]
            {
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldarg_1),
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MoveSplit), nameof(Wrap))),
            });
            Patched = true;
            return code;
        }

        public static void AdjustOnce(ffxPlusBase self)
        {
            if (replay != null) return;   // 다시 부를 때는 이미 조정했다
            try { adjust.Invoke(self, null); } catch { }
        }

        // 효과가 장식 목록을 받는 곳
        public static IEnumerable<scrDecoration> Wrap(IEnumerable<scrDecoration> src, ffxMoveDecorationsPlus self, scrPlanet planet)
        {
            if (replay != null && replay.Effect == self) return new Timed(replay, replayBudget, true);

            var list = new List<scrDecoration>(src);
            if (pending.Count > 0) FlushOverlapping(list);
            if (!Enabled || !Hitch.Playing || EffectBudget.InGrace || list.Count < 16) return list;
            var piece = new Piece { Effect = self, Planet = planet, List = list, At = Time.time };
            return new Timed(piece, FirstMs, false);
        }

        // 새 효과가 건드릴 장식이 밀린 조각에 있으면, 그 조각까지(앞의 것 포함, 순서대로) 마저 처리한다.
        private static void FlushOverlapping(List<scrDecoration> list)
        {
            int last = -1;
            HashSet<scrDecoration> set = null;
            for (int p = 0; p < pending.Count; p++)
            {
                var pc = pending[p];
                if (set == null) set = new HashSet<scrDecoration>(list);
                for (int i = pc.Next; i < pc.List.Count; i++)
                    if (set.Contains(pc.List[i])) { last = p; break; }
            }
            if (last < 0) return;
            for (int p = 0; p <= last && pending.Count > 0; p++)
            {
                var pc = pending[0];
                Run(pc, float.PositiveInfinity);
                if (pending.Count > 0 && pending[0] == pc) pending.RemoveAt(0);   // 실패해도 다시 돌지 않게
                Flushed++;
            }
        }

        private static void Run(Piece pc, float budgetMs)
        {
            if (pc.Effect == null) { pending.Remove(pc); return; }
            var prevReplay = replay; var prevBudget = replayBudget;
            replay = pc; replayBudget = budgetMs;
            try { startEffect.Invoke(pc.Effect, new object[] { pc.Planet }); }
            catch { pending.Remove(pc); }
            finally { replay = prevReplay; replayBudget = prevBudget; }
        }

        // 밀린 조각을 프레임마다 조금씩
        internal static void Tick()
        {
            if (pending.Count == 0 || replay != null) return;
            long t0 = Stopwatch.GetTimestamp();
            double used = 0;
            bool guard = TweenFix.Begin();   // 밀린 효과와 같은 보호 아래서 (애니메이션 목록 재정렬 막기)
            try
            {
                while (pending.Count > 0 && used < FrameMs)
                {
                    var pc = pending[0];
                    Run(pc, (float)(FrameMs - used));
                    if (pending.Count > 0 && pending[0] == pc && pc.Next >= pc.List.Count) pending.RemoveAt(0);
                    used = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                }
            }
            finally { TweenFix.End(guard); }
            ModCost.Add(SettingsWindow.T("장식 이동 나눠 하기", "Split decoration moves"), used);
        }

        internal static void Reset() { pending.Clear(); }

        // 장식 목록. 시간이 다 되면 목록을 거기서 끝내고 나머지를 조각으로 남긴다.
        private sealed class Timed : IEnumerable<scrDecoration>, IEnumerator<scrDecoration>
        {
            private readonly Piece pc;
            private readonly float budgetMs;
            private readonly bool late;          // 다시 부른 것 (애니메이션을 늦은 만큼 감는다)
            private readonly long start;
            private int yielded;
            private scrDecoration cur;
            private readonly List<Tween> before = new List<Tween>(12);

            public Timed(Piece pc, float budgetMs, bool late)
            {
                this.pc = pc; this.budgetMs = budgetMs; this.late = late;
                start = Stopwatch.GetTimestamp();
            }

            public IEnumerator<scrDecoration> GetEnumerator() { return this; }
            IEnumerator IEnumerable.GetEnumerator() { return this; }
            public scrDecoration Current { get { return cur; } }
            object IEnumerator.Current { get { return cur; } }
            public void Reset() { }

            public bool MoveNext()
            {
                CatchUp();
                while (pc.Next < pc.List.Count)
                {
                    if (yielded > 0 && (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency >= budgetMs)
                    {
                        // 시간이 다 됐다. 나머지는 조각으로 남긴다.
                        if (!pc.Queued)
                        {
                            pc.Queued = true;
                            pending.Add(pc);
                            SplitEffects++;
                            DeferredDecos += pc.List.Count - pc.Next;
                        }
                        return false;
                    }
                    var d = pc.List[pc.Next++];
                    if (d == null) continue;   // 그 사이 사라진 장식
                    cur = d;
                    yielded++;
                    if (late) Snapshot(d);
                    return true;
                }
                if (pc.Queued) pending.Remove(pc);   // 다 끝났다
                return false;
            }

            public void Dispose() { CatchUp(); }

            private void Snapshot(scrDecoration d)
            {
                before.Clear();
                var t = tweensRef(d);
                if (t != null) foreach (var tw in t.Values) before.Add(tw);
            }

            // 방금 처리한 장식이 새로 만든 애니메이션을, 원래 시작했어야 할 시각만큼 앞으로 감는다
            private void CatchUp()
            {
                var d = cur;
                cur = null;
                if (!late || d == null) return;
                try
                {
                    float elapsed = (Time.time - pc.At) * DOTween.timeScale;
                    if (elapsed <= 0f) return;
                    var t = tweensRef(d);
                    if (t == null) return;
                    foreach (var tw in t.Values)
                    {
                        if (tw == null || before.Contains(tw) || !tw.IsActive()) continue;
                        tw.Goto(elapsed * tw.timeScale, true);
                    }
                }
                catch { }
            }
        }
    }
}
