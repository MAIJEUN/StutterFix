using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // Camera.Render 안에서 도는 스크립트 콜백을 잰다.
    //
    // 지금까지 좁힌 것:
    //   박자마다 75ms, GPU는 6~7ms(PresentMon) -> CPU 문제
    //   Camera.Render 안에서 69ms, 컬링은 0.2ms
    //   렌더 스레드 대기(Gfx.WaitForRenderThread)는 4~6ms -> 메인 스레드가 직접 일하는 시간
    //
    // 그리는 도중 메인 스레드에서 도는 스크립트는 이 콜백들뿐이다.
    // 이름으로 전부 찾아 감싸고, 끊긴 프레임에서 오래 걸린 것을 보여준다.
    public static class RenderCallbackScan
    {
        private static readonly string[] Callbacks =
        {
            "OnWillRenderObject", "OnRenderObject", "OnPreRender", "OnPostRender",
            "OnRenderImage", "OnPreCull", "OnBecameVisible", "OnBecameInvisible",
        };

        private class Slot { public string Name; public long Ticks; public int Calls; }
        private static readonly Dictionary<MethodBase, Slot> slots = new Dictionary<MethodBase, Slot>();
        private static readonly List<Slot> all = new List<Slot>();

        internal static void Install(Harmony harmony)
        {
            int count = 0;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string an = asm.GetName().Name;
                    if (an.StartsWith("System") || an.StartsWith("mscorlib") || an.StartsWith("netstandard")
                        || an.StartsWith("Mono") || an.StartsWith("0Harmony") || an.StartsWith("Microsoft")) continue;

                    Type[] types;
                    try { types = asm.GetTypes(); } catch { continue; }

                    foreach (var t in types)
                    {
                        if (!typeof(MonoBehaviour).IsAssignableFrom(t) || t.ContainsGenericParameters) continue;
                        foreach (var name in Callbacks)
                        {
                            MethodInfo m;
                            try { m = AccessTools.DeclaredMethod(t, name); } catch { continue; }
                            if (m == null || m.IsAbstract || slots.ContainsKey(m)) continue;
                            try
                            {
                                var slot = new Slot { Name = t.Name + "." + name };
                                slots[m] = slot;
                                all.Add(slot);
                                harmony.Patch(m,
                                    prefix: new HarmonyMethod(typeof(RenderCallbackScan), nameof(Pre)),
                                    postfix: new HarmonyMethod(typeof(RenderCallbackScan), nameof(Post)));
                                count++;
                            }
                            catch { }
                        }
                    }
                }

                Main.Entry.Logger.Log("[그리기콜백] " + count + "개 감쌈");
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("[그리기콜백] 설치 실패: " + ex.Message);
            }
        }

        public static void Pre(out long __state) { __state = Stopwatch.GetTimestamp(); }

        public static void Post(MethodBase __originalMethod, long __state)
        {
            Slot s;
            if (!slots.TryGetValue(__originalMethod, out s)) return;
            s.Ticks += Stopwatch.GetTimestamp() - __state;
            s.Calls++;
        }

        internal static string Top(int count)
        {
            var sb = new System.Text.StringBuilder();
            var used = new bool[all.Count];
            for (int n = 0; n < count; n++)
            {
                int best = -1;
                long bestT = 0;
                for (int i = 0; i < all.Count; i++)
                {
                    if (used[i] || all[i].Ticks <= bestT) continue;
                    bestT = all[i].Ticks;
                    best = i;
                }
                if (best < 0) break;
                double ms = bestT * 1000.0 / Stopwatch.Frequency;
                if (ms < 0.5) break;
                used[best] = true;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(all[best].Name).Append(' ').Append(ms.ToString("F1")).Append("ms(")
                  .Append(all[best].Calls).Append("회)");
            }
            return sb.Length > 0 ? sb.ToString() : "그리기 콜백은 다 짧음 (엔진 내부 작업)";
        }

        internal static void Reset()
        {
            for (int i = 0; i < all.Count; i++) { all[i].Ticks = 0; all[i].Calls = 0; }
        }

        // 패치는 Main 이 ID로 한꺼번에 푼다. 여기서는 "이미 감쌌음" 기록만 지워 다시 켤 때 새로 감싸게 한다.
        internal static void Shutdown()
        {
            slots.Clear();
            all.Clear();
        }
    }
}
