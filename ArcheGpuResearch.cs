#if DEV || AUTOTEST
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // Picture-changing diagnostic only. No callbacks, native queries or component enable writes.
    internal static class ArcheGpuResearch
    {
        const string Id = "StutterFix.ArcheGpuResearch";
        static readonly Dictionary<int, MonoBehaviour> effects = new Dictionary<int, MonoBehaviour>();
        sealed class Observation { internal long Calls; internal string FirstInput; }
        static readonly Dictionary<MonoBehaviour, Observation> observations = new Dictionary<MonoBehaviour, Observation>();
        static MonoBehaviour selected;
        static long bypassed;
        static bool active;

        internal static void Command(string arg)
        {
            switch (arg.Trim())
            {
                case "on": Start(); break;
                case "state": Report(); break;
                case "stamp": Stamp(); break;
                case "timeline": Timeline(); break;
                case "off": Stop(); break;
                default: throw new ArgumentException("archegpu on|state|stamp|timeline|off");
            }
        }

        static void Start()
        {
            Stop();
            if (Hitch.Playing) throw new InvalidOperationException("archegpu on: 재생 전에 실행하세요.");
            try
            {
                var h = new Harmony(Id);
                var patched = new HashSet<MethodInfo>();
                foreach (var c in Resources.FindObjectsOfTypeAll<Camera>().OrderBy(c => c.GetInstanceID()))
                {
                    if (!c.gameObject.scene.IsValid()) continue;
                    var rt = c.targetTexture;
                    Log("camera=" + c.GetInstanceID() + " enabled=" + c.enabled + " active=" + c.gameObject.activeInHierarchy
                        + " depth=" + c.depth + " clear=" + c.clearFlags + " mask=" + c.cullingMask
                        + " hdr=" + c.allowHDR + " msaa=" + c.allowMSAA + " target="
                        + (rt ? rt.GetInstanceID() + ":" + rt.width + "x" + rt.height + ":" + rt.format + ":depth=" + rt.depth + ":samples=" + rt.antiAliasing : "backbuffer"));
                    foreach (var b in c.GetComponents<MonoBehaviour>())
                    {
                        if (!b) continue;
                        // Walk declarations explicitly; reject overloaded/ambiguous callback shapes.
                        MethodInfo method = null;
                        for (var t = b.GetType(); t != null && method == null; t = t.BaseType)
                        {
                            var matches = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                                .Where(m => m.Name == "OnRenderImage").ToArray();
                            if (matches.Length == 0) continue;
                            if (matches.Length != 1) throw new InvalidOperationException("ambiguous OnRenderImage: " + t.FullName);
                            var m = matches[0]; var p = m.GetParameters();
                            if (m.ReturnType != typeof(void) || m.IsGenericMethod || (m.CallingConvention & CallingConventions.VarArgs) != 0
                                || p.Length != 2 || p[0].ParameterType != typeof(RenderTexture) || p[1].ParameterType != typeof(RenderTexture))
                                throw new InvalidOperationException("unsupported OnRenderImage: " + t.FullName);
                            method = m;
                        }
                        if (method == null) continue;
                        effects.Add(b.GetInstanceID(), b);
                        observations.Add(b, new Observation());
                        if (patched.Add(method)) h.Patch(method, prefix: new HarmonyMethod(typeof(ArcheGpuResearch), nameof(Before)) { priority = Priority.First });
                        Log("effect=" + b.GetInstanceID() + " camera=" + c.GetInstanceID() + " type=" + b.GetType().FullName + " enabled=" + b.enabled);
                    }
                }
                active = true; Main.Config.ArcheGpuResearch = true;
                Log("on effects=" + effects.Count + " picture_changing_diagnostic=1; fxoff <effect-id|unique-type-name>|none");
            }
            catch { Stop(); throw; }
        }

        internal static void Select(string arg)
        {
            arg = arg.Trim();
            if (arg == "none" || arg == "off") { selected = null; bypassed = 0; Log("fxoff none"); return; }
            if (!active) throw new InvalidOperationException("archegpu on required before playback");
            int id;
            MonoBehaviour effect;
            if (int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
                effects.TryGetValue(id, out effect);
            else
            {
                var matches = effects.Values.Where(b => b && (b.GetType().FullName == arg || b.GetType().Name == arg)).ToArray();
                if (matches.Length != 1) throw new ArgumentException("fxoff: 형식 이름은 정확히 한 인스턴스여야 합니다. effect-id를 사용하세요.");
                effect = matches[0]; id = effect.GetInstanceID();
            }
            if (!effect) throw new ArgumentException("fxoff: 현재 세션 effect-id 하나를 지정하세요.");
            selected = effect; bypassed = 0;
            Log("fxoff id=" + id + " type=" + effect.GetType().FullName + " picture_changed=1 state_in_callback_not_advanced=1");
        }

        public static bool Before(MonoBehaviour __instance, RenderTexture __0, RenderTexture __1)
        {
            if (!active || !Main.Config.ArcheGpuResearch || !Hitch.Playing) return true;
            if (observations.TryGetValue(__instance, out var observation))
            {
                if (observation.Calls++ == 0)
                    observation.FirstInput = (__0 ? __0.width + "x" + __0.height + ":" + __0.format + ":sRGB=" + __0.sRGB + ":samples=" + __0.antiAliasing : "null")
                        + "->" + (__1 ? __1.width + "x" + __1.height + ":" + __1.format : "backbuffer");
            }
            if (!selected || !ReferenceEquals(__instance, selected)) return true;
            if (!__0 || ReferenceEquals(__0, __1)) return true;
            Graphics.Blit(__0, __1); bypassed++; return false;
        }

        static void Report()
        {
            Stamp();
            Log("active=" + active + " selected=" + (selected ? selected.GetInstanceID() : 0) + " bypassed=" + bypassed
                + " song=" + (scrConductor.instance ? scrConductor.instance.songposition_minusi.ToString("R", CultureInfo.InvariantCulture) : "unavailable"));
            foreach (var pair in effects)
            {
                var b = pair.Value;
                if (!b) { Log("effect=" + pair.Key + " destroyed=1; refresh before next playback"); continue; }
                var values = new List<string>();
                foreach (var f in b.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
                    if (f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType == typeof(Color))
                        values.Add(f.Name + "=" + Convert.ToString(f.GetValue(b), CultureInfo.InvariantCulture));
                var observation = observations[b];
                Log("effect=" + pair.Key + " type=" + b.GetType().FullName + " enabled=" + b.enabled + " active=" + b.gameObject.activeInHierarchy
                    + " calls=" + observation.Calls + " first_callback_rt=" + (observation.FirstInput ?? "unobserved") + " " + string.Join(" ", values));
            }
        }

        static void Stamp()
        {
            Log("stamp song=" + (scrConductor.instance ? scrConductor.instance.songposition_minusi.ToString("R", CultureInfo.InvariantCulture) : "unavailable")
                + " qpc=" + System.Diagnostics.Stopwatch.GetTimestamp() + " qpc_frequency=" + System.Diagnostics.Stopwatch.Frequency
                + " utc=" + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }

        static void Timeline()
        {
            if (Hitch.Playing) throw new InvalidOperationException("timeline: 편집 상태에서만 실행하세요.");
            if (ADOBase.lm == null || ADOBase.lm.listFloors == null) throw new InvalidOperationException("맵을 먼저 여세요.");
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StutterFix");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "archegpu-floors-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".tsv");
            string digest;
            using (var sha = System.Security.Cryptography.SHA256.Create())
            using (var input = File.OpenRead(ADOBase.levelPath))
                digest = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "").ToLowerInvariant();
            using (var w = new StreamWriter(path))
            {
                w.WriteLine("floor\tentry_seconds");
                var floors = ADOBase.lm.listFloors;
                for (int i = 0; i < floors.Count; i++) if (floors[i] != null)
                    w.WriteLine(i.ToString(CultureInfo.InvariantCulture) + "\t" + floors[i].entryTime.ToString("R", CultureInfo.InvariantCulture));
            }
            File.WriteAllText(Path.ChangeExtension(path, ".sha256"), digest);
            Log("timeline=" + path + " game_entryTime=1; join offline, angleOffset/duration/repeats require separate handling");
        }

        internal static void Stop()
        {
            // Gate first so a failed unpatch cannot leave a picture-changing path active.
            active = false; selected = null; bypassed = 0; effects.Clear(); observations.Clear();
            if (Main.Config != null) Main.Config.ArcheGpuResearch = false;
            new Harmony(Id).UnpatchOwner(Id);
        }
        static void Log(string value) { Main.Entry.Logger.Log("[archegpu] " + value); }
    }
}
#endif
