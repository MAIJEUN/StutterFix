using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 누가 메모리를 잡아먹는지 찾는다.
    //
    // 시간 측정은 초당 수만 번 불리는 함수에서 측정 비용이 실제 비용을 덮어버려
    // 엉뚱한 범인을 지목했었다(README의 "측정에서 배운 것"). 할당량 측정은 그 함정이 없다.
    // 곡 중에는 GC가 멈춰 있어 힙이 늘기만 하므로, 함수 전후의 힙 차이가 곧 그 함수가 새로 잡은 양이다.
    //
    // 씬에 실제로 살아 있는 컴포넌트의 Update/LateUpdate/FixedUpdate 를 전부 감싸서
    // 15초 동안 모은 뒤, 많이 잡은 순서대로 보여준다.
    public static class AllocScan
    {
        private class Stat
        {
            public long Bytes;
            public int Calls;
        }

        private static readonly Dictionary<string, Stat> stats = new Dictionary<string, Stat>();
        private static readonly object gate = new object();
        private static Harmony harmony;

        internal static bool Running;
        internal static string LastReport = "(아직 없음)";

        private static float elapsed;
        private static long startHeap;
        private static int patchedCount;

        internal static float Seconds = 15f;

        internal static void Toggle()
        {
            if (Running) Stop(); else Start();
        }

        private static void Start()
        {
            try
            {
                lock (gate) stats.Clear();
                harmony = new Harmony("StutterFix.AllocScan");
                var patchWatch = System.Diagnostics.Stopwatch.StartNew();

                var types = new HashSet<Type>();
                foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                {
                    if (mb == null || !mb.isActiveAndEnabled) continue;
                    types.Add(mb.GetType());
                }

                var done = new HashSet<MethodBase>();
                patchedCount = 0;
                foreach (var t in types)
                {
                    foreach (var name in new[] { "Update", "LateUpdate", "FixedUpdate" })
                    {
                        try
                        {
                            var m = AccessTools.Method(t, name);
                            if (m == null || m.IsAbstract || m.ContainsGenericParameters) continue;
                            if (!done.Add(m)) continue;   // 부모에게 물려받은 같은 함수는 한 번만
                            harmony.Patch(m,
                                prefix: new HarmonyMethod(typeof(AllocScan), nameof(Pre)),
                                postfix: new HarmonyMethod(typeof(AllocScan), nameof(Post)));
                            patchedCount++;
                        }
                        catch { }
                    }
                }

                PatchCoroutines(done);

                startHeap = GC.GetTotalMemory(false);
                elapsed = 0f;
                Running = true;
                Main.Entry.Logger.Log($"[할당추적] 시작: 컴포넌트 {types.Count}종, 함수 {patchedCount}개 (감싸는 데 {patchWatch.ElapsedMilliseconds}ms), {Seconds:F0}초");
                if (!GcControl.Paused)
                    Main.Entry.Logger.Log("[할당추적] 주의: GC가 도는 중이라 값이 부정확하다. 곡을 재생하면서 켜야 한다.");
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("[할당추적] 시작 실패: " + ex.Message);
                Running = false;
            }
        }

        // 엔진 단계별 측정에서 할당의 거의 전부가 Update/ScriptRunDelayedDynamicFrameRate,
        // 즉 코루틴 단계에서 나왔다(110MB/s). 코루틴 본체는 컴파일러가 만든 숨은 클래스의
        // MoveNext 에 들어 있으므로, IEnumerator 를 구현한 클래스들의 MoveNext 를 전부 감싼다.
        private static void PatchCoroutines(HashSet<MethodBase> done)
        {
            int count = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string an = asm.GetName().Name;
                if (an.StartsWith("System") || an.StartsWith("Unity") || an.StartsWith("mscorlib")
                    || an.StartsWith("netstandard") || an.StartsWith("Mono") || an.StartsWith("0Harmony")
                    || an.StartsWith("I18N") || an.StartsWith("Microsoft")) continue;

                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }

                foreach (var t in types)
                {
                    try
                    {
                        if (!typeof(System.Collections.IEnumerator).IsAssignableFrom(t)) continue;
                        if (t.ContainsGenericParameters) continue;
                        var m = AccessTools.DeclaredMethod(t, "MoveNext");
                        if (m == null || m.IsAbstract) continue;
                        if (!done.Add(m)) continue;
                        harmony.Patch(m,
                            prefix: new HarmonyMethod(typeof(AllocScan), nameof(Pre)),
                            postfix: new HarmonyMethod(typeof(AllocScan), nameof(Post)));
                        count++;
                        if (count >= 1500) break;
                    }
                    catch { }
                }
                if (count >= 1500) break;
            }
            patchedCount += count;
            Main.Entry.Logger.Log("[할당추적] 코루틴 " + count + "개 감쌈");
        }

        internal static void Shutdown() { Stop(); }

        private static void Stop()
        {
            if (!Running) return;
            Running = false;
            try { harmony.UnpatchAll("StutterFix.AllocScan"); } catch { }
            Report();
        }

        // Boehm GC 에서 GetTotalMemory 는 힙 크기를 그대로 돌려준다. 새 덩어리를 받을 때만 값이 뛰므로
        // 한 번의 호출로는 못 잡지만, 수만 번 모으면 누가 얼마나 잡는지 비율은 정확히 드러난다.
        public static void Pre(out long __state)
        {
            __state = GC.GetTotalMemory(false);
        }

        public static void Post(MethodBase __originalMethod, object __instance, long __state)
        {
            long delta = GC.GetTotalMemory(false) - __state;
            if (delta <= 0) return;

            var type = __instance != null ? __instance.GetType() : __originalMethod.DeclaringType;
            // 코루틴은 컴파일러가 만든 숨은 클래스라 이름만으론 어디 것인지 모른다. 바깥 클래스까지 붙인다.
            string owner = type.DeclaringType != null ? type.DeclaringType.Name + "+" + type.Name : type.Name;
            string key = owner + "." + __originalMethod.Name;
            lock (gate)
            {
                Stat s;
                if (!stats.TryGetValue(key, out s)) { s = new Stat(); stats[key] = s; }
                s.Bytes += delta;
                s.Calls++;
            }
        }

        internal static void Tick(float dt)
        {
            if (!Running) return;
            elapsed += dt;
            if (elapsed >= Seconds) Stop();
        }

        private static void Report()
        {
            long grown = GC.GetTotalMemory(false) - startHeap;
            var list = new List<KeyValuePair<string, Stat>>();
            lock (gate)
            {
                foreach (var kv in stats) list.Add(kv);
                stats.Clear();
            }
            list.Sort((a, b) => b.Value.Bytes.CompareTo(a.Value.Bytes));

            double window = Math.Max(0.1f, elapsed);
            double explained = 0;
            var lines = new List<string>();
            for (int i = 0; i < list.Count && i < 15; i++)
            {
                double mb = list[i].Value.Bytes / 1048576.0;
                lines.Add(string.Format("{0} {1:F0}MB/s ({2:F0}회/s)",
                    list[i].Key, mb / window, list[i].Value.Calls / window));
            }
            foreach (var kv in list) explained += kv.Value.Bytes / 1048576.0;

            LastReport = string.Format("{0:F0}초 동안 힙 +{1:F0}MB ({2:F0}MB/s) 중 {3:F0}MB 설명됨",
                window, grown / 1048576.0, grown / 1048576.0 / window, explained);

            Main.Entry.Logger.Log("[할당추적] " + LastReport);
            foreach (var line in lines) Main.Entry.Logger.Log("[할당추적]   " + line);
            if (lines.Count == 0)
                Main.Entry.Logger.Log("[할당추적]   Update 계열에서는 아무것도 안 잡힌다 -> 코루틴이나 엔진 콜백 쪽이다");
        }
    }
}
