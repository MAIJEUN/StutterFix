#if DEV || AUTOTEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // Opt-in, sampled, nested EXCLUSIVE wall-clock attribution. No renderer enumeration.
    internal static class TileCost
    {
        private const int Stride = 16;
        private static readonly string[] Names = { "floor-update-other", "floor-start/zero-other", "floor-apply/read/write", "mesh-update", "color/style/opacity", "tilecull", "recolor-defer", "ease-eval", "game-floor-update/late-other" };
        private static readonly long[] ticks = new long[9], calls = new long[9];
        private static readonly Dictionary<MethodBase, int> stages = new Dictionary<MethodBase, int>();
        private static readonly Dictionary<string, long> reasons = new Dictionary<string, long>();
        private static Harmony harmony;
        private static bool active;
        private static int firstFrame, lastSampleFrame, sampledFrames;
        private static long started, roots, rootCalls, failures;
        private static long fallback0, real0, zero0, errors0;
        [ThreadStatic] private static Scope top;
        private static readonly Scope[] pool = MakePool();
        private static int depth;
        private static Scope[] MakePool() { var p = new Scope[128]; for (int i = 0; i < p.Length; i++) p[i] = new Scope(); return p; }
        internal sealed class Scope { internal long Start, Children; internal int Stage; internal Scope Parent; }
        private static void Log(string s) { Main.Entry.Logger.Log("[타일비용] " + s); }
        private static void Reason(string s) { long n; reasons.TryGetValue(s, out n); reasons[s] = n + 1; Log("reason=" + s + " count=" + (n + 1)); }
        private static bool Sampled { get { return active && (Time.frameCount - firstFrame) % Stride == 0; } }
        internal static long EvalBegin() { return Sampled ? Stopwatch.GetTimestamp() : 0; }
        internal static void EvalEnd(long stamp)
        {
            if (stamp == 0) return;
            long elapsed = Stopwatch.GetTimestamp() - stamp;
            ticks[7] += elapsed; calls[7]++;
            if (top != null) top.Children += elapsed;
        }
        internal static void Command(string arg)
        {
            if (arg == "end") { End(); return; }
            if (arg != "begin") { Reason("usage: tilecost begin/end"); return; }
            if (active) { Reason("already-active"); return; }
            if (!Hitch.Playing) { Reason("not-playing"); return; }
            if (TileSim.Capturing) { Reason("capture-active"); return; }
            Array.Clear(ticks, 0, ticks.Length); Array.Clear(calls, 0, calls.Length);
            reasons.Clear(); stages.Clear(); roots = rootCalls = failures = 0; top = null; depth = 0;
            firstFrame = Time.frameCount; lastSampleFrame = -1; sampledFrames = 0;
            fallback0 = FloorAnim.Fallbacks; real0 = FloorAnim.Real; zero0 = FloorAnim.ZeroWrites; errors0 = FloorAnim.Errors;
            harmony = new Harmony("StutterFix.TileCost.Dev");
            // Resolve once. Never patch Unity native property setters.
            Patch(typeof(FloorAnim), "UpdatePrefix", 0);
            foreach (var n in new[] { "StartPrefix", "ZeroPos", "ZeroRot", "ZeroScale" }) Patch(typeof(FloorAnim), n, 1);
            Patch(typeof(FloorAnim), "Apply", 2);
            Patch(typeof(FloorMesh), "UpdateAllRequired", 3);
            foreach (var n in new[] { "ColorFloor", "SetTrackStyle", "SetColor", "TweenColor", "TweenOpacity" }) Patch(typeof(scrFloor), n, 4);
            Patch(typeof(FloorMeshRenderer), "set_color", 4);
            Patch(typeof(FloorSpriteRenderer), "set_color", 4);
            Patch(typeof(scrFloor), "Update", 8); Patch(typeof(scrFloor), "LateUpdate", 8);
            Patch(typeof(TileCull), "Tick", 5); Patch(typeof(TileCull), "Step", 5);
            foreach (var n in new[] { "AfterStep", "Apply", "ColorFloorPrefix", "SetTrackStylePrefix" }) Patch(typeof(RecolorDefer), n, 6);
            started = Stopwatch.GetTimestamp(); active = true;
            Log("begin stride=" + Stride + " patched=" + stages.Count + " patch-failures=" + failures + " sampled exclusive QPC; capture/bench와 분리");
        }
        private static void Patch(Type type, string name, int stage)
        {
            int found = 0;
            foreach (var m in type.GetMethods(AccessTools.all))
            {
                if (m.Name != name || m.DeclaringType != type || m.IsAbstract || m.ContainsGenericParameters) continue;
                found++;
                try
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(TileCost), nameof(Pre)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(TileCost), nameof(Final)) { priority = Priority.Last });
                    stages[m] = stage;
                }
                catch (Exception ex) { failures++; Reason("patch-failed:" + type.Name + "." + name + ":" + ex.GetType().Name); }
            }
            if (found == 0) { failures++; Reason("method-missing:" + type.Name + "." + name); }
        }
        private static void Pre(MethodBase __originalMethod, out Scope __state)
        {
            __state = null;
            if (!Sampled) return;
            int frame = Time.frameCount;
            if (lastSampleFrame != frame) { lastSampleFrame = frame; sampledFrames++; }
            if (depth == pool.Length) { failures++; return; }
            __state = pool[depth++]; __state.Children = 0;
            __state.Stage = stages[__originalMethod]; __state.Parent = top; __state.Start = Stopwatch.GetTimestamp();
            top = __state;
        }
        private static Exception Final(Exception __exception, Scope __state)
        {
            if (__state == null) return __exception;
            long elapsed = Stopwatch.GetTimestamp() - __state.Start;
            ticks[__state.Stage] += elapsed - __state.Children; calls[__state.Stage]++;
            if (__state.Parent != null) __state.Parent.Children += elapsed;
            else { roots += elapsed; rootCalls++; }
            top = __state.Parent;
            depth--;
            if (__exception != null) { failures++; Reason("observed-exception:" + __exception.GetType().Name); }
            return __exception; // never swallow or replace game exceptions
        }
        private static double Ms(long value) { return value * 1000.0 / Stopwatch.Frequency; }
        private static void End()
        {
            if (!active) { Reason("not-active"); return; }
            active = false;
            int frames = Math.Max(1, Time.frameCount - firstFrame + 1);
            int selectedFrames = (frames - 1) / Stride + 1;
            long total = 0;
            for (int i = 0; i < ticks.Length; i++)
            {
                total += ticks[i];
                Log(Names[i] + " calls(sampled)=" + calls[i] + " ticks=" + ticks[i] + " total-ms=" + Ms(ticks[i]).ToString("F6")
                    + " ms/sampled-frame=" + (Ms(ticks[i]) / selectedFrames).ToString("F6"));
            }
            long timerStart = Stopwatch.GetTimestamp();
            for (int i = 0; i < 10000; i++) Stopwatch.GetTimestamp();
            double timerNs = (Stopwatch.GetTimestamp() - timerStart) * 1e9 / Stopwatch.Frequency / 10000;
            Log("end frames=" + frames + " selected-frames=" + selectedFrames + " sampled-frames-with-calls=" + sampledFrames + " root-calls=" + rootCalls
                + " root-ms=" + Ms(roots).ToString("F6") + " unaccounted-ms=" + Ms(roots - total).ToString("F6")
                + " elapsed-ms=" + Ms(Stopwatch.GetTimestamp() - started).ToString("F3") + " qpc-call-ns~=" + timerNs.ToString("F1")
                + " failures=" + failures + " original-fallbacks=" + (FloorAnim.Fallbacks - fallback0)
                + " real-overlap=" + (FloorAnim.Real - real0) + " zero-writes=" + (FloorAnim.ZeroWrites - zero0) + " floor-errors=" + (FloorAnim.Errors - errors0)
                + " | unaccounted는 계측 루트 내부만; 프레임 전체/GPU/미계측 DOTween 내부는 제외; 계측 오버헤드 포함");
            Shutdown();
        }
        internal static void Shutdown()
        {
            active = false;
            if (harmony != null) { harmony.UnpatchOwner(harmony.Id); harmony = null; }
            stages.Clear(); top = null; depth = 0;
        }
        internal static bool Active { get { return active; } }
    }
}
#endif
