#if DEV || AUTOTEST
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace StutterFix
{
    // Explicit diagnostic session only. No static subscription, object, query or patch while off.
    internal sealed class GpuProbe : MonoBehaviour
    {
        const string Id = "StutterFix.GpuProbe";
        const int ScopeCount = 512, FirstEffect = 35;
        [DllImport("gpuprobe", CallingConvention = CallingConvention.Cdecl)] static extern IntPtr gp_event();
        [DllImport("gpuprobe", CallingConvention = CallingConvention.Cdecl)] static extern ulong gp_status(int k);
        [DllImport("gpuprobe", CallingConvention = CallingConvention.Cdecl)] static extern ulong gp_pstat(int k);
        [DllImport("gpuprobe", CallingConvention = CallingConvention.Cdecl)] static extern int gp_snapshot([Out] ulong[] output, int length);
        sealed class CameraScope { internal Camera Camera; internal int Id; internal CommandBuffer End; }
        static GpuProbe instance;
        readonly Dictionary<Camera, CameraScope> cameras = new Dictionary<Camera, CameraScope>();
        readonly Dictionary<object, int> effects = new Dictionary<object, int>();
        readonly string[] names = new string[ScopeCount];
        CommandBuffer issue;
        RenderTexture anchor;
        IntPtr callback;
        int frame = -1, cameraCount, effectCount;
        bool sampled;
        long sampledFrames, unknownCameras, unknownEffects;
        readonly string[] counterNames = { "Draw Calls Count", "SetPass Calls Count", "Batches Count", "Triangles Count", "Vertices Count" };
        Unity.Profiling.ProfilerRecorder[] counters;
        readonly double[] counterSums = new double[5];
        readonly long[] counterFrames = new long[5];
        int counterResetFrame;
        void StartCounters()
        {
            counters = new Unity.Profiling.ProfilerRecorder[counterNames.Length];
            for (int i = 0; i < counters.Length; i++)
                counters[i] = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Render, counterNames[i], 1);
            ResetCounters();
        }
        void ResetCounters()
        {
            Array.Clear(counterSums, 0, counterSums.Length); Array.Clear(counterFrames, 0, counterFrames.Length);
            counterResetFrame = Time.frameCount;
        }
        void Update()
        {
            // LastValue is the last completed frame, not the current camera's partial work.
            if (counters == null || Time.frameCount <= counterResetFrame + 1) return;
            for (int i = 0; i < counters.Length; i++)
                if (counters[i].Valid && counters[i].Count > 0) { counterSums[i] += counters[i].LastValue; counterFrames[i]++; }
        }
        void ReportCounters()
        {
            for (int i = 0; i < counterNames.Length; i++)
                Main.Entry.Logger.Log(FormattableString.Invariant($"[gpuprobe] render_counter={counterNames[i].Replace(' ', '_')} scope=whole-frame valid={(counters != null && counters[i].Valid ? 1 : 0)} frames={counterFrames[i]} mean={(counterFrames[i] == 0 ? "unavailable" : (counterSums[i] / counterFrames[i]).ToString("F3", System.Globalization.CultureInfo.InvariantCulture))}"));
        }
        internal static void Command(string arg)
        {
            switch (arg.Trim().ToLowerInvariant())
            {
                case "on": StartProbe(); break;
                case "state": Report(); break;
                case "reset":
                    if (instance == null) throw new InvalidOperationException("gpuprobe off");
                    instance.Issue(5); instance.sampledFrames = instance.unknownCameras = instance.unknownEffects = 0;
                    instance.ResetCounters();
                    Main.Entry.Logger.Log("[gpuprobe] reset queued (existing hooks and queries retained)"); break;
                case "off": Stop(); break;
                default: throw new ArgumentException("gpuprobe on|state|off|reset");
            }
        }
        static void StartProbe()
        {
            if (GpuHide.Busy) throw new InvalidOperationException("gpuhide active; cancel it before restarting probe");
            Stop();
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11) throw new InvalidOperationException("gpuprobe requires D3D11");
            // Resolve the optional DLL before creating any Unity objects or hooks.
            IntPtr cb = gp_event();
            var go = new GameObject(Id); DontDestroyOnLoad(go);
            var p = go.AddComponent<GpuProbe>(); instance = p;
            try
            {
                p.callback = cb; p.issue = new CommandBuffer { name = Id };
                p.names[0] = "frame:first-camera-preCull->end-of-frame"; p.names[1] = "camera-sum:inclusive";
                p.anchor = new RenderTexture(1, 1, 0, RenderTextureFormat.ARGB32); p.anchor.Create();
                p.Issue(4, p.anchor.GetNativeTexturePtr()); // one-time device acquisition; never per-frame texture pointer access
                p.Refresh();
                p.StartCounters();
                Camera.onPreCull += PreCull; Camera.onPreRender += PreRender;
                p.StartCoroutine(p.EndFrames());
                Main.Entry.Logger.Log("[gpuprobe] on sample=1/8 cameras=" + p.cameraCount + " effects=" + p.effectCount + " install/warmup frames excluded; use on again after scene load");
            }
            catch { Stop(); throw; }
        }
        void Refresh()
        {
            var patched = new HashSet<MethodBase>(); var h = new Harmony(Id);
            foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (!c.gameObject.scene.IsValid()) continue;
                if (cameraCount == FirstEffect - 3) throw new InvalidOperationException("gpuprobe camera limit 32");
                int id = 3 + cameraCount++;
                var end = new CommandBuffer { name = Id + ":" + c.name };
                // AfterEverything is after image effects; camera times include their OnRenderImage scopes.
                end.IssuePluginEventAndData(callback, 2000 + id, IntPtr.Zero);
                cameras.Add(c, new CameraScope { Camera = c, Id = id, End = end });
                names[id] = "camera:" + c.name + "#" + c.GetInstanceID();
                c.AddCommandBuffer(CameraEvent.AfterEverything, end);
                foreach (var b in c.GetComponents<MonoBehaviour>())
                {
                    if (!b) continue;
                    var m = b.GetType().GetMethod("OnRenderImage", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(RenderTexture), typeof(RenderTexture) }, null);
                    if (m == null) continue;
                    if (FirstEffect + effectCount == ScopeCount) throw new InvalidOperationException("gpuprobe effect limit 477");
                    int e = FirstEffect + effectCount++;
                    effects.Add(b, e); names[e] = "effect:" + c.name + "/" + b.GetType().FullName + "#" + b.GetInstanceID();
                    if (patched.Add(m)) h.Patch(m, prefix: new HarmonyMethod(typeof(GpuProbe), nameof(Before)), finalizer: new HarmonyMethod(typeof(GpuProbe), nameof(After)));
                }
            }
        }
        void Issue(int id, IntPtr data = default)
        {
            issue.Clear(); issue.IssuePluginEventAndData(callback, id, data); Graphics.ExecuteCommandBuffer(issue);
        }
        static void PreCull(Camera c)
        {
            var p = instance; if (p == null) return;
            int f = Time.frameCount;
            if (p.frame == f) return;
            p.frame = f; p.sampled = (f & 7) == 0;
            if (p.sampled) { p.sampledFrames++; p.Issue(1); }
        }
        static void PreRender(Camera c)
        {
            var p = instance; if (p == null || !p.sampled) return;
            if (p.cameras.TryGetValue(c, out var scope)) p.Issue(1000 + scope.Id);
            else p.unknownCameras++;
        }
        public static void Before(object __instance, out int __state)
        {
            __state = 0; var p = instance; if (p == null || !p.sampled) return;
            if (p.effects.TryGetValue(__instance, out int id)) { p.Issue(1000 + id); __state = id; }
            else p.unknownEffects++;
        }
        public static Exception After(Exception __exception, int __state)
        {
            if (__state != 0 && instance != null) instance.Issue(2000 + __state);
            return __exception;
        }
        IEnumerator EndFrames()
        {
            var eof = new WaitForEndOfFrame();
            while (instance == this) { yield return eof; if (instance == this) { Issue(2); sampled = false; } }
        }
        internal static bool Active => instance != null;
        internal sealed class Window
        {
            internal ulong Completed;
            internal readonly Dictionary<string, double> Means = new Dictionary<string, double>();
            internal double Frame;
        }
        internal static Window Snapshot()
        {
            var p = instance;
            if (p == null) throw new InvalidOperationException("gpuprobe off");
            var data = new ulong[ScopeCount * 4];
            if (gp_snapshot(data, data.Length) == 0) return null;
            var result = new Window { Completed = data[0] };
            if (result.Completed == 0) return result;
            for (int i = 0; i < ScopeCount; i++)
                if (p.names[i] != null) result.Means[p.names[i]] = data[i*4+1] / 1e6 / result.Completed;
            result.Frame = data[1] / 1e6 / result.Completed;
            return result;
        }
        internal static void Report()
        {
            var p = instance;
            if (p == null) { Main.Entry.Logger.Log("[gpuprobe] off"); return; }
            var data = new ulong[ScopeCount * 4];
            if (gp_snapshot(data, data.Length) == 0) { Main.Entry.Logger.Log("[gpuprobe] snapshot busy; retry state"); return; }
            ulong n = data[0];
            p.ReportCounters();
            try { ulong pf = gp_pstat(0); if (pf > 0) Main.Entry.Logger.Log($"[gpuprobe] pipeline frames={pf} ps_invocations_per_frame={gp_pstat(1) / pf} raster_tris_per_frame={gp_pstat(2) / pf} clip_in_tris_per_frame={gp_pstat(3) / pf}"); } catch { }
            Main.Entry.Logger.Log($"[gpuprobe] ready={gp_status(0)} completed={n} requested={p.sampledFrames} invalid={gp_status(2)} ring_full={gp_status(3)} overflow={gp_status(4)} not_ready={gp_status(5)} errors={gp_status(6)} effect_outside_camera={gp_status(7)} unknown_camera={p.unknownCameras} unknown_effect={p.unknownEffects}");
            for (int i = 0; i < ScopeCount; i++)
            {
                if (p.names[i] == null || data[i * 4] == 0) continue;
                Main.Entry.Logger.Log(FormattableString.Invariant($"[gpuprobe] {p.names[i]} active_frames={data[i*4]} calls={data[i*4+3]} mean_frame_ms={(n == 0 ? 0 : data[i*4+1]/1e6/n):F4} mean_active_ms={data[i*4+1]/1e6/data[i*4]:F4} max_frame_ms={data[i*4+2]/1e6:F4}"));
            }
            Main.Entry.Logger.Log(FormattableString.Invariant($"[gpuprobe] residual_frame_minus_cameras_ms={(n == 0 ? 0 : ((double)data[1]-data[5])/1e6/n):F4}; effects nested inside camera: DO NOT ADD; nested Camera.Render can double-count cameras; interval is GPU elapsed, not GPU busy"));
        }
        internal static void Stop()
        {
            GpuSplit.Stop();
            GpuHide.Stop();
            var p = instance; if (p == null) return;
            instance = null;
            Camera.onPreCull -= PreCull; Camera.onPreRender -= PreRender;
            new Harmony(Id).UnpatchOwner(Id);
            p.StopAllCoroutines();
            if (p.counters != null) foreach (var counter in p.counters) counter.Dispose();
            foreach (var c in p.cameras.Values) { if (c.Camera) c.Camera.RemoveCommandBuffer(CameraEvent.AfterEverything, c.End); c.End.Release(); }
            // All native query COM objects are released by an ordered render-thread event, never the main thread.
            if (p.issue != null) { p.Issue(3); p.issue.Release(); }
            if (p.anchor) { p.anchor.Release(); Destroy(p.anchor); }
            Destroy(p.gameObject);
            Main.Entry.Logger.Log("[gpuprobe] off (hooks, camera buffers and query release queued)");
        }
    }
}
#endif
