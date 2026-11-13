#if AUTOTEST
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace StutterFix
{
    internal sealed class PhaseCallbackProbe
    {
        const string Owner = "StutterFix.PhaseCallbackProbe";
        const int MaxDepth = 16;
        static PhaseCallbackProbe installed;
        readonly int mainThread = Environment.CurrentManagedThreadId;
        readonly Dictionary<MethodBase, int> methods = new Dictionary<MethodBase, int>();
        readonly List<string> names = new List<string>();
        readonly ulong[] childCycles = new ulong[MaxDepth];
        ulong[] sampleCycles, totalCycles;
        int[] sampleCalls, totalCalls;
        int depth, complete, rejected, nested;
        bool invalid;
        internal PhaseCycleProbe Window;

        public struct Scope
        {
            internal int Row, Depth, Frame;
            internal ulong Start;
            internal bool Started;
        }

        internal static PhaseCallbackProbe Install(string mode)
        {
            if (installed != null) throw new InvalidOperationException("Callback probe already installed");
            var probe = new PhaseCallbackProbe();
            if (mode == "manager")
            {
                probe.Add(typeof(scrDecorationManager), "LateUpdate");
                probe.Add(typeof(scrDecorationManager), "UpdateDecorationTilingLateUpdate");
                probe.Add(typeof(scrDecorationManager), "UpdateAlphaMaskCacheLateUpdate");
                probe.Add(typeof(SpriteAlphaMaskUtils), "RefreshMaskCache");
                probe.Add(typeof(scrCamera), "CheckUpdateFrameRateScreen");
                probe.Add(typeof(TightLate), nameof(TightLate.List), typeof(scrDecorationManager));
                probe.Add(typeof(Dormancy), nameof(Dormancy.List), typeof(scrDecorationManager));
                probe.Add(typeof(MoveApply), nameof(MoveApply.ManagerLatePostfix), typeof(scrDecorationManager));
                probe.Add(typeof(TightLate), nameof(TightLate.EngineLatePostfix));
                probe.Add(typeof(DecoBake), nameof(DecoBake.GpuLiveFrame));
            }
            else if (mode == "gpudeco")
            {
                var bake = typeof(DecoBake);
                var slice = bake.GetNestedType("GpuLiveSlice", BindingFlags.NonPublic);
                if (slice == null) throw new MissingMemberException(bake.FullName, "GpuLiveSlice");
                probe.Add(bake, nameof(DecoBake.GpuLiveFrame));
                probe.Add(bake, "DispatchGpuLiveSlice", typeof(double));
                probe.Add(bake, "AdvanceGpuLiveSources");
                probe.Add(bake, "FlushGpuImagesBeforeVisible");
                probe.Add(slice, "FlushDirty");
                probe.Add(typeof(GpuDecoGpu), nameof(GpuDecoGpu.UploadSourceE),
                    typeof(float[]), typeof(float[]), typeof(int), typeof(int));
                probe.Add(bake, "CheckDrawCollapseBeforeDispatch", typeof(double));
                probe.Add(bake, "DispatchDrawCollapse", typeof(double), typeof(UnityEngine.Vector4), typeof(float));
            }
            else
            {
                var tween = typeof(DG.Tweening.DOTween).Assembly.GetType("DG.Tweening.Core.DOTweenComponent", true);
                probe.Add(typeof(scrVfxPlus), "Update");
                probe.Add(typeof(scrDecorationManager), "Update");
                probe.Add(typeof(scrDecorationManager), "LateUpdate");
                probe.Add(typeof(scrController), "Update");
                probe.Add(typeof(scrController), "LateUpdate");
                probe.Add(typeof(scrConductor), "Update");
                probe.Add(typeof(scrCamera), "Update");
                probe.Add(typeof(scrCamera), "LateUpdate");
                probe.Add(typeof(scnGame), "Update");
                probe.Add(typeof(scrHUDText), "Update");
                probe.Add(tween, "Update");
                probe.Add(tween, "LateUpdate");
            }
            int count = probe.names.Count * 2;
            probe.sampleCycles = new ulong[count];
            probe.totalCycles = new ulong[count];
            probe.sampleCalls = new int[count];
            probe.totalCalls = new int[count];
            var harmony = new Harmony(Owner);
            var prefix = new HarmonyMethod(typeof(PhaseCallbackProbe), nameof(Begin)) { priority = Priority.First };
            var finalizer = new HarmonyMethod(typeof(PhaseCallbackProbe), nameof(End)) { priority = Priority.Last };
            try
            {
                foreach (var method in probe.methods.Keys) harmony.Patch(method, prefix: prefix, finalizer: finalizer);
                installed = probe;
                return probe;
            }
            catch
            {
                harmony.UnpatchOwner(Owner);
                throw;
            }
        }

        void Add(Type type, string name, params Type[] parameters)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var method = type.GetMethod(name, flags, null, parameters, null);
            if (method == null || method.IsAbstract || method.ContainsGenericParameters)
                throw new MissingMethodException(type.FullName, name);
            methods.Add(method, names.Count);
            names.Add(type.Name + "." + name);
        }

        internal string Ready => "phase_callbacks ready=" + names.Count + " names=" + string.Join(",", names);

        internal static void Stop()
        {
            var probe = installed;
            if (probe == null) return;
            probe.Window = null;
            new Harmony(Owner).UnpatchOwner(Owner);
            installed = null;
        }

        internal void StartWindow(PhaseCycleProbe window)
        {
            Array.Clear(totalCycles, 0, totalCycles.Length);
            Array.Clear(totalCalls, 0, totalCalls.Length);
            complete = rejected = nested = 0;
            Window = window;
        }

        internal void BeginFrame()
        {
            depth = 0;
            invalid = false;
            Array.Clear(sampleCycles, 0, sampleCycles.Length);
            Array.Clear(sampleCalls, 0, sampleCalls.Length);
        }

        internal void CompleteFrame()
        {
            if (invalid || depth != 0)
            {
                rejected++;
                return;
            }
            for (int i = 0; i < totalCycles.Length; i++)
            {
                totalCycles[i] += sampleCycles[i];
                totalCalls[i] += sampleCalls[i];
            }
            complete++;
        }

        public static void Begin(MethodBase __originalMethod, out Scope __state)
        {
            __state = default;
            var probe = installed;
            var window = probe?.Window;
            if (window == null || probe.invalid) return;
            int phase = window.CallbackPhase;
            if (phase < 0 || Environment.CurrentManagedThreadId != probe.mainThread) return;
            if (probe.depth == MaxDepth || !probe.methods.TryGetValue(__originalMethod, out int method) ||
                !window.ReadCallbackCycles(out ulong cycles))
            {
                probe.invalid = true;
                return;
            }
            int row = phase * probe.names.Count + method;
            probe.childCycles[probe.depth] = 0;
            __state = new Scope { Row = row, Depth = probe.depth++, Frame = window.SampleFrame, Start = cycles, Started = true };
        }

        public static Exception End(Scope __state, Exception __exception)
        {
            if (!__state.Started) return __exception;
            var probe = installed;
            var window = probe?.Window;
            if (window == null || probe.invalid) return __exception;
            if (window.SampleFrame != __state.Frame || window.CallbackPhase != __state.Row / probe.names.Count ||
                probe.depth != __state.Depth + 1 ||
                !window.ReadCallbackCycles(out ulong end) || end < __state.Start)
            {
                probe.invalid = true;
                return __exception;
            }
            ulong inclusive = end - __state.Start;
            int depth = --probe.depth;
            ulong children = probe.childCycles[depth];
            if (children > inclusive)
            {
                probe.invalid = true;
                return __exception;
            }
            if (depth > 0)
            {
                probe.childCycles[depth - 1] += inclusive;
                probe.nested++;
            }
            probe.sampleCycles[__state.Row] += inclusive - children;
            probe.sampleCalls[__state.Row]++;
            return __exception;
        }

        internal string Report(ulong update, ulong lateUpdate)
        {
            var text = new StringBuilder();
            text.Append("phase_callbacks frames=").Append(complete).Append(" rejected=").Append(rejected)
                .Append(" nested_scopes=").Append(nested).Append(" installed=").Append(names.Count);
            for (int phase = 0; phase < 2; phase++)
            {
                ulong denominator = phase == 0 ? update : lateUpdate;
                string name = phase == 0 ? "Update" : "LateUpdate";
                ulong covered = 0;
                for (int i = 0; i < names.Count; i++) covered += totalCycles[phase * names.Count + i];
                if (covered > denominator) throw new InvalidOperationException("Callback cycles exceed sampled phase");
                text.Append("\nphase_callback_coverage phase=").Append(name).Append(" cycles=").Append(denominator)
                    .Append(" covered_cycles=").Append(covered).Append(" remainder_cycles=").Append(denominator - covered);
                for (int i = 0; i < names.Count; i++)
                {
                    int row = phase * names.Count + i;
                    if (totalCalls[row] == 0) continue;
                    text.Append("\nphase_callback phase=").Append(name).Append(" name=").Append(names[i])
                        .Append(" calls=").Append(totalCalls[row]).Append(" self_cycles=").Append(totalCycles[row])
                        .Append(" phase_pct=").Append((100d * totalCycles[row] / denominator).ToString("F4", CultureInfo.InvariantCulture));
                }
            }
            return text.ToString();
        }
    }
}
#endif
