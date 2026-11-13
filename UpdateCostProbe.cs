#if DEV || AUTOTEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using DG.Tweening;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // Opt-in diagnostic patches, installed by begin before playback, removed by end/Unload.
    // No logs, reflection, file IO or per-call allocations on the sampled hot path.
    internal static class UpdateCostProbe
    {
        private const string Id = "StutterFix.updatecost";
        private static Harmony harmony;
        private static bool active;
        internal static bool InWindow;
        private static long frames, scans, observationTicks, timerFloor, sampledDecorations;
        private static readonly HashSet<scrDecoration> decorations = new HashSet<scrDecoration>();
        private static double animMs;
        private static readonly Dictionary<MethodBase, int> slots = new Dictionary<MethodBase, int>();
        private static readonly List<string> names = new List<string>();
        private static readonly long[,] stateCalls = new long[64,4], stateTicks = new long[64,4], stateSamples = new long[64,4];
        private static readonly long[] calls = new long[64], samples = new long[64], ticks = new long[64];
        private static readonly long[] groups = new long[32], eases = new long[64];
        private static readonly long[] colorCalls = new long[8], colorSame = new long[8];
        private static readonly Dictionary<GroupKey, int> grouping = new Dictionary<GroupKey, int>();
        private static long dictDecorations, dictLookups, dictWrites;
        private static string patchList = "";
        private struct GroupKey : IEquatable<GroupKey>
        {
            internal scrDecoration D; internal int P, T; internal Ease E;
            public bool Equals(GroupKey x) { return ReferenceEquals(D, x.D) && P == x.P && T == x.T && E == x.E; }
            public override bool Equals(object x) { return x is GroupKey && Equals((GroupKey)x); }
            public override int GetHashCode() { unchecked { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(D) * 397 ^ P ^ T * 31 ^ (int)E; } }
        }
        public struct State { internal long Start; internal int Slot, Bucket; }
        internal static string Command(string arg)
        {
            if (arg == "end") { string result = Report(); Stop(); return result; }
            if (arg != "begin" && arg != "verify") return "updatecost begin|verify|end (song 252 <= t < 278)";
            Stop();
            Array.Clear(stateCalls,0,stateCalls.Length); Array.Clear(stateTicks,0,stateTicks.Length); Array.Clear(stateSamples,0,stateSamples.Length);
            Array.Clear(calls, 0, calls.Length); Array.Clear(samples, 0, samples.Length); Array.Clear(ticks, 0, ticks.Length);
            Array.Clear(groups, 0, groups.Length); Array.Clear(eases, 0, eases.Length);
            Array.Clear(colorCalls, 0, 8); Array.Clear(colorSame, 0, 8);
            frames = scans = observationTicks = sampledDecorations = dictDecorations = dictLookups = dictWrites = 0; animMs = 0;
            UpdateEvalMemo.Calls = UpdateEvalMemo.Hits = UpdateEvalMemo.Checks = UpdateEvalMemo.Mismatch = 0;
            UpdateEvalMemo.Verify = arg == "verify";
            timerFloor = 0;
            long calibration = Stopwatch.GetTimestamp();
            for (int i = 0; i < 4096; i++) { long a = Stopwatch.GetTimestamp(); long b = Stopwatch.GetTimestamp(); timerFloor += b - a; }
            timerFloor /= 4096;
            observationTicks = Stopwatch.GetTimestamp() - calibration;
            try
            {
                harmony = new Harmony(Id);
                Add(typeof(DecoAnim), "Step"); Add(typeof(DecoAnim), "StepPair");
                Add(typeof(DecoAnim), "Apply"); Add(typeof(DecoAnim), "OnUpdate");
                Add(typeof(DecoAnim), "PrepareNative"); Add(typeof(ZeroTween), "Eval");
                Add(typeof(scrDecoration), "SetPosition"); Add(typeof(scrDecoration), "UpdateScreenClamp");
                Add(typeof(scrDecoration), "UpdatePosition"); Add(typeof(scrDecoration), "SetColor");
                Add(typeof(scrDecoration), "SetOpacity"); Add(typeof(scrVisualDecoration), "ApplyColor");
                Add(typeof(scrVisualDecoration), "UpdateRendererEnable");
                Add(typeof(InvisibleSkip), "LazyPrefix"); Add(typeof(InvisibleSkip), "After");
                Add(typeof(Dormancy), "WakePost"); Add(typeof(MoveApply), "SkipForLate");
                Add(typeof(MoveApply), "DeferTransform"); Add(typeof(FastMove), "Run");
                active = true;
                return "armed [252,278), every method 1/16 sample; frame group census 1/16; inclusive times overlap. " +
                    "PairXY=" + DecoAnim.PairXY + " Prof=" + DecoAnim.Prof + " Native=" + NativeMotion.Enabled +
                    " memo=" + UpdateEvalMemo.Enabled + " verify=" + UpdateEvalMemo.Verify + "\n" + patchList;
            }
            catch (Exception ex) { Stop(); return "install failed, removed: " + ex; }
        }
        private static void Add(Type type, string name)
        {
            bool found = false;
            foreach (var m in type.GetMethods(AccessTools.all))
            {
                if (m.Name != name || m.DeclaringType != type || m.IsAbstract || m.ContainsGenericParameters) continue;
                found = true;
                if (slots.Count >= calls.Length) throw new InvalidOperationException("probe capacity");
                int index = slots.Count; slots.Add(m, index); names.Add(type.Name + "." + name);
                var info = Harmony.GetPatchInfo(m);
                patchList += type.Name + "." + name + ":";
                if (info != null)
                {
                    foreach (var p in info.Prefixes) patchList += " pre=" + p.owner + "/" + p.PatchMethod.DeclaringType.Name + "." + p.PatchMethod.Name + "@" + p.priority;
                    foreach (var p in info.Postfixes) patchList += " post=" + p.owner + "/" + p.PatchMethod.DeclaringType.Name + "." + p.PatchMethod.Name + "@" + p.priority;
                    foreach (var p in info.Transpilers) patchList += " il=" + p.owner + "/" + p.PatchMethod.DeclaringType.Name + "." + p.PatchMethod.Name;
                    foreach (var p in info.Finalizers) patchList += " final=" + p.owner + "/" + p.PatchMethod.Name;
                }
                patchList += "\n";
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(UpdateCostProbe), (m.IsStatic && (type == typeof(InvisibleSkip) && name == "After" || type == typeof(Dormancy) && name == "WakePost")) ? nameof(BeforeArgument) : !m.IsStatic && typeof(scrDecoration).IsAssignableFrom(type) ? nameof(BeforeInstance) : nameof(Before)) { priority = int.MaxValue },
                    finalizer: new HarmonyMethod(typeof(UpdateCostProbe), nameof(After)) { priority = int.MinValue });
            }
            if (!found) patchList += type.Name + "." + name + ": MISSING (not measured)\n";
        }
        public static void Before(MethodBase __originalMethod, out State __state)
        {
            __state = new State { Bucket = -1 };
            if (!InWindow) return;
            int slot = slots[__originalMethod];
            if ((++calls[slot] & 15) != 0) return;
            __state.Slot = slot; __state.Start = Stopwatch.GetTimestamp();
        }
        public static void BeforeArgument(scrVisualDecoration __0, MethodBase __originalMethod, out State __state)
        { BeforeInstance(__0, __originalMethod, out __state); }
        public static void BeforeInstance(scrDecoration __instance, MethodBase __originalMethod, out State __state)
        {
            __state = new State { Bucket = -1 };
            if (!InWindow) return;
            int slot = slots[__originalMethod];
            // Classification itself is diagnostic overhead; it is outside the timed body.
            int bucket = (InvisibleSkip.IsHidden(__instance) ? 1 : 0) + (Dormancy.ProbeDormant(__instance) ? 2 : 0);
            stateCalls[slot,bucket]++;
            if ((++calls[slot] & 15) != 0) return;
            __state.Slot = slot; __state.Bucket = bucket; __state.Start = Stopwatch.GetTimestamp();
        }
        public static Exception After(Exception __exception, State __state)
        {
            if (__state.Start != 0)
            {
                long elapsed = Stopwatch.GetTimestamp() - __state.Start;
                ticks[__state.Slot] += elapsed; samples[__state.Slot]++;
                if (__state.Bucket >= 0) { stateTicks[__state.Slot,__state.Bucket] += elapsed; stateSamples[__state.Slot,__state.Bucket]++; }
            }
            return __exception;
        }
        internal static void Frame(List<DecoAnim.Rec> recs)
        {
            InWindow = false;
            if (!active) return;
            double song = scrConductor.instance != null ? scrConductor.instance.songposition_minusi : -1;
            if (!Hitch.Playing || song < 252 || song >= 278) return;
            InWindow = true; frames++;
            if ((frames & 15) != 0) return;
            long t = Stopwatch.GetTimestamp(); grouping.Clear(); decorations.Clear(); scans++;
            for (int i = 0; i < recs.Count; i++)
            {
                var r = recs[i]; if (!r.Running || (object)r.D == null) continue;
                decorations.Add(r.D);
                var key = new GroupKey { D = r.D, P = InstantMove.Bits(r.Pos), T = InstantMove.Bits(r.Dur), E = r.E };
                int n; grouping.TryGetValue(key, out n); grouping[key] = n + 1;
                int e = (int)r.E; if (e >= 0 && e < eases.Length) eases[e]++;
            }
            sampledDecorations += decorations.Count;
            foreach (var pair in grouping) groups[Math.Min(pair.Value, groups.Length - 1)]++;
            observationTicks += Stopwatch.GetTimestamp() - t;
        }
        internal static void FrameEnd(double ms) { if (InWindow) animMs += ms; }
        internal static void ColorStep(DecoAnim.Rec r, Color current, float opacity)
        {
            // Hidden versus visible, and key. Dormancy is not inferred from visibility.
            int bucket = (Dormancy.ProbeDormant(r.D) ? 4 : 0) + (InvisibleSkip.IsHidden(r.D) ? 2 : 0) + (r.Key == 10 ? 1 : 0);
            colorCalls[bucket]++;
            bool same = r.Key == 10 ? InstantMove.Bits(opacity) == InstantMove.Bits(r.FLast) :
                InstantMove.Bits(current.r) == InstantMove.Bits(r.CLast.r) && InstantMove.Bits(current.g) == InstantMove.Bits(r.CLast.g) &&
                InstantMove.Bits(current.b) == InstantMove.Bits(r.CLast.b) && InstantMove.Bits(current.a) == InstantMove.Bits(r.CLast.a);
            if (same) colorSame[bucket]++;
        }
        internal static void DictionaryDecoration(bool animated) { if (InWindow && animated) dictDecorations++; }
        internal static void DictionaryRead() { if (InWindow) dictLookups++; }
        internal static void DictionaryWrite() { if (InWindow) dictWrites++; }
        private static string Report()
        {
            var s = new StringBuilder();
            s.AppendFormat("frames={0} censusFrames={1} DecoAnim={2:F6}ms/frame Native={3} Prof={4}\n", frames, scans, frames == 0 ? 0 : animMs / frames, NativeMotion.Enabled, DecoAnim.Prof);
            double us = 1000000.0 / Stopwatch.Frequency;
            for (int i = 0; i < names.Count; i++)
                s.AppendFormat("{0}: calls={1} samples={2} estimatedMs/frame={3:F6} us/call={4:F6} (inclusive)\n", names[i], calls[i], samples[i],
                    frames == 0 || samples[i] == 0 ? 0 : ticks[i] * us / samples[i] * calls[i] / frames / 1000, samples[i] == 0 ? 0 : ticks[i] * us / samples[i]);
            for (int i=0;i<names.Count;i++) for(int b=0;b<4;b++)
                if(stateCalls[i,b]!=0) s.AppendFormat("{0}/{1}/{2}: calls={3} samples={4} us/call={5:F6} estimatedMs/frame={6:F6}\n",
                    names[i],(b&1)==0?"visible":"hidden",b<2?"awake":"dormant",stateCalls[i,b],stateSamples[i,b],
                    stateSamples[i,b]==0?0:stateTicks[i,b]*us/stateSamples[i,b],
                    stateSamples[i,b]==0||frames==0?0:stateTicks[i,b]*us/stateSamples[i,b]*stateCalls[i,b]/frames/1000);
            s.AppendFormat("sampled active decorations/frame={0:F3}; DecoAnim us/decoration estimate={1:F6}\n", scans == 0 ? 0 : (double)sampledDecorations / scans, frames == 0 || sampledDecorations == 0 ? 0 : animMs * 1000 / frames * scans / sampledDecorations);
            s.Append("group sizes (sampled pre-step Pos bits/Dur bits/Ease; groups, not records): ");
            for (int i = 1; i < groups.Length; i++) if (groups[i] != 0) s.Append(i + "=" + groups[i] + " ");
            s.Append("\nEase sampled records: ");
            for (int i = 0; i < eases.Length; i++) if (eases[i] != 0) s.Append(((Ease)i) + "=" + eases[i] + " ");
            for (int i = 0; i < 8; i++) s.AppendFormat("\n{0}/{1}: steps={2} bitSame={3}", ((i & 2) == 0 ? "visible" : "hidden") + (i < 4 ? "/awake" : "/dormant"), (i & 1) == 0 ? "color" : "opacity", colorCalls[i], colorSame[i]);
            s.AppendFormat("\nanimation dictionary decorations={0} TryGetValue={1} writes={2} reads/decoration={3:F3}", dictDecorations, dictLookups, dictWrites, dictDecorations == 0 ? 0 : (double)dictLookups / dictDecorations);
            s.AppendFormat("\nmemo calls={0} hits={1} checks={2} mismatch={3}", UpdateEvalMemo.Calls, UpdateEvalMemo.Hits, UpdateEvalMemo.Checks, UpdateEvalMemo.Mismatch);
            s.AppendFormat("\nobservation census+calibration={0:F6}ms total; empty timestamp pair={1:F6}us. Patch dispatch/counters/color classification are UNMEASURED overhead; use unprobed A/B. No exclusive-time subtraction.", observationTicks * us / 1000, timerFloor * us);
            return s.ToString();
        }
        internal static void Stop()
        {
            active = InWindow = false; UpdateEvalMemo.Verify = false;
            if (harmony != null) { harmony.UnpatchOwner(Id); harmony = null; }
            slots.Clear(); names.Clear(); grouping.Clear(); decorations.Clear(); patchList = "";
        }
    }
}
#endif
