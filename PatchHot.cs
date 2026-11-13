#if DEV || AUTOTEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace StutterFix
{
    // Public only so the generated wrappers can call us across assembly boundaries.
    public static class PatchHot
    {
        private const string Id = "StutterFix.PatchHot";
        private static long[] calls = Array.Empty<long>(), samples = Array.Empty<long>(), ticks = Array.Empty<long>();
        private static string[] names = Array.Empty<string>();
        private static int[] patchCounts = Array.Empty<int>();
        private static bool enabled, clockPlaying;
        private static int mainThread;
        private static long playingStart, playingTicks;
        private static readonly Dictionary<MethodBase, Type> hooks = new Dictionary<MethodBase, Type>();
        private static ModuleBuilder module;

        internal static void Tick(bool playing)
        {
            bool active = enabled && playing;
            if (active == clockPlaying) return;
            long now = Stopwatch.GetTimestamp();
            if (active) playingStart = now; else playingTicks += now - playingStart;
            clockPlaying = active;
        }

        private static bool Ours(Patch p) => p.owner != Id && p.owner.StartsWith("StutterFix", StringComparison.Ordinal);
        private static int Count(Patches p) => p == null ? 0 : p.Prefixes.Count(Ours) + p.Postfixes.Count(Ours) + p.Transpilers.Count(Ours) + p.Finalizers.Count(Ours);
        private static string Name(MethodBase m) => m.DeclaringType?.FullName + "." + m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.FullName)) + ") [" + m.Module.Name + "]";

        internal static void On()
        {
            if (enabled) return;
            if (Hitch.Playing) throw new InvalidOperationException("patchhot on 은 곡 밖에서 실행하세요. 설치 비용이 초반 끊김을 만듭니다.");
            // Snapshot before adding our own hooks. Includes engine/DOTween/other-mod targets too.
            var targets = Harmony.GetAllPatchedMethods().Where(m => Count(Harmony.GetPatchInfo(m)) > 0).OrderBy(Name, StringComparer.Ordinal).ToArray();
            calls = new long[targets.Length]; samples = new long[targets.Length]; ticks = new long[targets.Length];
            names = targets.Select(Name).ToArray(); patchCounts = targets.Select(m => Count(Harmony.GetPatchInfo(m))).ToArray();
            mainThread = Thread.CurrentThread.ManagedThreadId;
            playingTicks = 0; clockPlaying = false;
            var h = new Harmony(Id);
            int installed = 0;
            // Mono PrepareMethod is a no-op; compile hot helpers before the song.
            typeof(PatchHot).GetMethod(nameof(Begin)).MethodHandle.GetFunctionPointer();
            typeof(PatchHot).GetMethod(nameof(End)).MethodHandle.GetFunctionPointer();
            try
            {
                for (int i = 0; i < targets.Length; i++)
                {
                    // Native/open generic methods cannot be safely wrapped; explicitly report gaps.
                    if (targets[i].ContainsGenericParameters || targets[i].GetMethodBody() == null)
                    { Log("제외(관리 IL 없음/열린 제네릭): " + names[i]); continue; }
                    if (!hooks.TryGetValue(targets[i], out var wrapper))
                    {
                        if (module == null) module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("StutterFix.PatchHot.Hooks"), AssemblyBuilderAccess.Run).DefineDynamicModule("Hooks");
                        var t = module.DefineType("Hook" + hooks.Count, TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed);
                        // Embed slot in a static field: stable wrapper reuse across off/on with changed snapshots.
                        var slotField = t.DefineField("Slot", typeof(int), FieldAttributes.Public | FieldAttributes.Static);
                        var pre = t.DefineMethod("Before", MethodAttributes.Public | MethodAttributes.Static, typeof(void), new[] { typeof(long).MakeByRefType() });
                        pre.DefineParameter(1, ParameterAttributes.Out, "__state");
                        var il = pre.GetILGenerator(); il.Emit(OpCodes.Ldsfld, slotField); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, typeof(PatchHot).GetMethod(nameof(Begin))); il.Emit(OpCodes.Ret);
                        var post = t.DefineMethod("After", MethodAttributes.Public | MethodAttributes.Static, typeof(void), new[] { typeof(long) });
                        post.DefineParameter(1, ParameterAttributes.None, "__state");
                        il = post.GetILGenerator(); il.Emit(OpCodes.Ldsfld, slotField); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, typeof(PatchHot).GetMethod(nameof(End))); il.Emit(OpCodes.Ret);
                        wrapper = t.CreateTypeInfo().AsType(); hooks.Add(targets[i], wrapper);
                    }
                    wrapper.GetField("Slot").SetValue(null, i);
                    wrapper.GetMethod("Before").MethodHandle.GetFunctionPointer();
                    wrapper.GetMethod("After").MethodHandle.GetFunctionPointer();
                    h.Patch(targets[i], prefix: new HarmonyMethod(wrapper.GetMethod("Before")) { priority = int.MaxValue }, postfix: new HarmonyMethod(wrapper.GetMethod("After")) { priority = int.MinValue });
                    installed++;
                }
                enabled = true;
                Log("켬: " + installed + "/" + targets.Length + " 대상, 주 스레드 곡 중 호출만, 시간 1/64. 원래 함수·다른 패치·안쪽 호출 포함; 모드 비용 아님.");
            }
            catch { h.UnpatchOwner(Id); enabled = false; throw; }
        }

        // No reflection, dictionary lookup, allocation or lock on the invocation path.
        public static void Begin(int slot, out long stamp)
        {
            stamp = 0;
            if (!enabled || !Hitch.Playing || Thread.CurrentThread.ManagedThreadId != mainThread) return;
            if ((++calls[slot] & 63) == 0) stamp = Stopwatch.GetTimestamp();
        }
        public static void End(int slot, long stamp)
        {
            if (stamp == 0 || !enabled || Thread.CurrentThread.ManagedThreadId != mainThread) return;
            ticks[slot] += Stopwatch.GetTimestamp() - stamp; samples[slot]++;
        }
        internal static void Off()
        {
            Tick(false); enabled = false;
            new Harmony(Id).UnpatchOwner(Id);
        }
        internal static void State()
        {
            Tick(Hitch.Playing);
            double seconds = (playingTicks + (clockPlaying ? Stopwatch.GetTimestamp() - playingStart : 0)) / (double)Stopwatch.Frequency;
            Log((enabled ? "켬" : "끔") + ", 곡 중 " + seconds.ToString("F3") + "초; 표본 1/64, 설치 시점 패치 수. 예외 호출은 시간 표본에서 빠질 수 있음.");
            foreach (int i in Enumerable.Range(0, names.Length).OrderByDescending(i => calls[i]).ThenBy(i => names[i], StringComparer.Ordinal))
                Log(string.Format("{0} 호출 {1} (초당 {2:F2}) 표본 평균 {3:F3} µs, 우리 패치 수 {4}, 표본 {5}{6}", names[i], calls[i], seconds > 0 ? calls[i] / seconds : 0,
                    samples[i] == 0 ? 0 : ticks[i] * 1000000.0 / Stopwatch.Frequency / samples[i], patchCounts[i], samples[i], samples[i] == 0 ? " (표본 없음)" : ""));
        }
        private static void Log(string text) => Main.Entry.Logger.Log("[패치 호출] " + text);
    }
}
#endif
