#if DEV || AUTOTEST
using System;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // Explicit, expensive diagnostic: actual source and original filter output from the SAME callback.
    internal static class FilterIdentityProbe
    {
        internal static bool Running;
        const string Id = "StutterFix.FilterIdentityProbe";
        sealed class Result { internal int Samples, Bad, Max; }
        static readonly Dictionary<Type, long[]> sightings = new Dictionary<Type, long[]>();
        static readonly Dictionary<string, Result> results = new Dictionary<string, Result>();
        public sealed class Capture
        {
            public RenderTexture OriginalDestination, Output, Bypass;
            public string Key;
        }
        internal static void Command(string arg)
        {
            switch (arg.Trim().ToLowerInvariant())
            {
                case "on":
                    Stop(); results.Clear(); sightings.Clear();
                    foreach (var c in FilterIdentity.Candidates.Values) sightings[c.Type] = new long[2];
                    var h = new Harmony(Id);
                    foreach (var c in FilterIdentity.Candidates.Values)
                        h.Patch(AccessTools.Method(c.Type, "OnRenderImage", new[]{typeof(RenderTexture),typeof(RenderTexture)}),
                            prefix: new HarmonyMethod(typeof(FilterIdentityProbe), nameof(Before)),
                            postfix: new HarmonyMethod(typeof(FilterIdentityProbe), nameof(After)),
                            finalizer: new HarmonyMethod(typeof(FilterIdentityProbe), nameof(Final)));
                    Running = true; break;
                case "off": Stop(); break;
                case "state": State(); break;
                case "approve":
                    Stop();
                    var lines = new List<string>{FilterIdentity.EnvironmentKey};
                    foreach (var c in FilterIdentity.Candidates.Values) c.Approved.Clear();
                    foreach (var kv in results)
                        if (kv.Value.Samples >= 16 && kv.Value.Bad == 0)
                        {
                            lines.Add(kv.Key);
                            var parts = kv.Key.Split('|');
                            if (FilterIdentity.Parse(parts[1], out var key))
                                foreach (var c in FilterIdentity.Candidates.Values) if (c.Type.Name == parts[0]) c.Approved.Add(key);
                        }
                    File.WriteAllLines(FilterIdentity.CertificatePath, lines);
                    State(); Main.Entry.Logger.Log("[필터 대조] 승인 기록 " + (lines.Count-1) + "개. 미관측/실패 필터는 원래대로"); break;
                default: throw new Exception("filteridentity on|off|state|approve");
            }
        }
        internal static void Stop() { Running = false; new Harmony(Id).UnpatchOwner(Id); }
        static void State()
        {
            foreach (var c in FilterIdentity.Candidates.Values)
            {
                sightings.TryGetValue(c.Type, out var n);
                Main.Entry.Logger.Log("[필터 관측] " + c.Type.Name + " 호출 " + (n == null ? 0 : n[0]) + " 중 중립 값 " + (n == null ? 0 : n[1]));
            }
            foreach (var kv in results) Main.Entry.Logger.Log("[필터 대조] " + kv.Key + " 표본 " + kv.Value.Samples + " 실패 " + kv.Value.Bad + " 최대 차이 " + kv.Value.Max + "/255");
        }
        public static void Before(object __instance, RenderTexture __0, ref RenderTexture __1, out Capture __state)
        {
            __state = null;
            if (!Running || !FilterIdentity.Candidates.TryGetValue(__instance.GetType(), out var c)) return;
            var count = sightings[c.Type]; count[0]++;
            if (!c.Neutral(__instance)) return;
            count[1]++;
            if (__0 == null || __0.format != RenderTextureFormat.ARGB32) return;
            string key = c.Type.Name + "|" + FilterIdentity.Context(__0);
            if (!results.TryGetValue(key, out var r)) results[key] = r = new Result();
            // Bound readback cost; spread observations over the song, not every frame.
            if (Time.frameCount % 120 != 0) return;
            // Match the observed destination; unsupported HDR or differing destinations cannot certify.
            if (__1 != null && (__1.format != __0.format || __1.sRGB != __0.sRGB || __1.width != __0.width || __1.height != __0.height)) return;
            var s = new Capture { OriginalDestination = __1, Key = key };
            __state = s;
            s.Output = RenderTexture.GetTemporary(__0.descriptor);
            s.Bypass = RenderTexture.GetTemporary(__0.descriptor);
            Graphics.Blit(__0, s.Bypass);
            __1 = s.Output;
        }
        static Color32[] Read(RenderTexture rt)
        {
            var active = RenderTexture.active;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
            try { RenderTexture.active = rt; tex.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0); tex.Apply(); return tex.GetPixels32(); }
            finally { RenderTexture.active = active; UnityEngine.Object.Destroy(tex); }
        }
        public static void After(Capture __state)
        {
            if (__state == null) return;
            var s = __state;
            Graphics.Blit(s.Output, s.OriginalDestination);
            var a = Read(s.Output); var b = Read(s.Bypass); int max = 0;
            for (int i=0;i<a.Length;i++)
            {
                max = Math.Max(max, Math.Abs(a[i].r-b[i].r)); max = Math.Max(max,Math.Abs(a[i].g-b[i].g));
                max = Math.Max(max,Math.Abs(a[i].b-b[i].b)); max = Math.Max(max,Math.Abs(a[i].a-b[i].a));
            }
            var r = results[s.Key]; r.Samples++; r.Max = Math.Max(r.Max,max);
            if (max > 1) r.Bad++;
        }
        public static Exception Final(Exception __exception, Capture __state)
        {
            if (__state != null)
            {
                if (__exception != null) results[__state.Key].Bad++;
                if (__state.Output != null) RenderTexture.ReleaseTemporary(__state.Output);
                if (__state.Bypass != null) RenderTexture.ReleaseTemporary(__state.Bypass);
            }
            return __exception;
        }
    }
}
#endif
