using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityModManagerNet;

namespace StutterFix
{
    // 프레임마다 도는 게임 함수들의 실제 소요 시간을 잰다.
    // 어떤 함수가 39ms를 잡아먹는지 확인하는 용도. 측정 자체에도 약간의 비용이 들기 때문에
    // 원인을 찾은 뒤에는 꺼두는 것이 좋다.
    public static class Profiler
    {
        private class Stat
        {
            public long Ticks;
            public int Calls;
        }

        private static readonly Dictionary<string, Stat> stats = new Dictionary<string, Stat>();
        private static readonly object gate = new object();
        private static Harmony harmony;
        private static float sinceReport;
        private static int frames;
        private static int lastGc0;

        internal static bool Running { get; private set; }
        internal static string LastReport = "(아직 없음)";

        private static readonly string[][] Targets =
        {
            // Update를 실제로 가진 컴포넌트들 - 엔진 측정에서 Update 단계가 68%로 나와 직접 확인한다
            new[] { "BlendModeEffect", "Update" },
            new[] { "scrFloor", "Update" },
            new[] { "scrPlanet", "Update" },
            new[] { "scrRing", "Update" },
            new[] { "scrVfxPlus", "Update" },
            new[] { "ffxPlusBase", "StartEffect" },
            new[] { "scrFloor", "ColorFloor" },
            new[] { "scrFloor", "SetColor" },
            new[] { "scrFloor", "TweenColor" },
            new[] { "FloorRenderer", "set_color" },
            new[] { "scrFloor", "SetTrackStyle" },
            new[] { "scrFloor", "UpdateAngle" },
            new[] { "ffxPlusBase", "IsAllowedByVisualSettings" },
            new[] { "scrVfxPlus", "MakeNewFilterDictionary" },
            new[] { "scrHUDText", "Update" },
            new[] { "PropertyControl_Text", "Update" },
            new[] { "Updater", "Update" },
            new[] { "Ticker", "Update" },
            new[] { "scrCamera", "Update" },
            new[] { "scrController", "Update" },
            new[] { "scrConductor", "Update" },
        };

        internal static void Start()
        {
            if (Running) return;
            harmony = new Harmony("StutterFix.Profiler");

            int patched = 0;
            foreach (var t in Targets)
            {
                try
                {
                    var type = AccessTools.TypeByName(t[0]);
                    if (type == null) continue;

                    // 같은 이름의 오버로드가 여럿일 수 있다. AccessTools.Method는 그럴 때 예외를 던지므로
                    // 이름이 같은 메서드를 전부 찾아 각각 패치한다.
                    foreach (var method in type.GetMethods(AccessTools.all))
                    {
                        if (method.Name != t[1]) continue;
                        if (method.IsAbstract || method.ContainsGenericParameters) continue;
                        try
                        {
                            harmony.Patch(method,
                                prefix: new HarmonyMethod(typeof(Profiler), nameof(Pre)),
                                postfix: new HarmonyMethod(typeof(Profiler), nameof(Post)));
                            patched++;
                            if (t[1] == "ColorFloor" || t[1] == "TweenColor" || t[1] == "SetColor")
                                Main.Entry.Logger.Log("[sig] " + method.DeclaringType.Name + "." + method.ToString());
                        }
                        catch (Exception ex)
                        {
                            Main.Entry.Logger.Error($"profiler patch failed for {t[0]}.{t[1]}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Main.Entry.Logger.Error($"profiler target failed {t[0]}.{t[1]}: {ex.Message}");
                }
            }

            Running = true;
            Main.Entry.Logger.Log($"profiler started ({patched} methods)");
        }

        internal static void Stop()
        {
            if (!Running) return;
            harmony.UnpatchAll("StutterFix.Profiler");
            Running = false;
            lock (gate) stats.Clear();
            Main.Entry.Logger.Log("profiler stopped");
        }

        public static void Pre(MethodBase __originalMethod, out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        // ffxPlusBase.StartEffect 처럼 부모 클래스에 정의된 메서드는 어떤 자식 타입이 느린지 알아야 하므로
        // 선언 타입 대신 실제 인스턴스 타입으로 집계한다.

        public static void Post(MethodBase __originalMethod, object __instance, long __state)
        {
            long delta = Stopwatch.GetTimestamp() - __state;
            string owner = __instance != null ? __instance.GetType().Name : __originalMethod.DeclaringType.Name;
            string key = owner + "." + __originalMethod.Name;
            lock (gate)
            {
                Stat s;
                if (!stats.TryGetValue(key, out s))
                {
                    s = new Stat();
                    stats[key] = s;
                }
                s.Ticks += delta;
                s.Calls++;
            }
        }

        internal static bool AutoScan;
        private static float sinceScan;

        internal static void Tick(float dt)
        {
            // 씬 스캔은 프로파일러와 별개로 동작한다 (스캔 자체가 무거워서 5초 간격).
            if (AutoScan)
            {
                sinceScan += dt;
                if (sinceScan >= 5f)
                {
                    sinceScan = 0f;
                    SceneScan.Run();
                }
            }

            if (!Running) return;
            frames++;
            sinceReport += dt;
            if (sinceReport < 1f) return;

            float window = sinceReport;
            sinceReport = 0f;

            var lines = new List<string>();
            double totalMs = 0;
            lock (gate)
            {
                foreach (var kv in stats)
                {
                    double ms = kv.Value.Ticks * 1000.0 / Stopwatch.Frequency;
                    totalMs += ms;
                    lines.Add($"{kv.Key}: {ms / window:F1} ms/s, {kv.Value.Calls / window:F0} calls/s");
                }
                stats.Clear();
            }

            // 이 구간이 무거운 구간인지 알 수 있도록 프레임 수와 GC 상황도 같이 남긴다.
            int gc0 = GC.CollectionCount(0);
            long heap = GC.GetTotalMemory(false);
            lines.Insert(0, $"FPS {frames / window:F0} (frame {1000f * window / Math.Max(1, frames):F1} ms)");
            lines.Insert(1, $"측정합계 {totalMs / window:F1} ms/s");
            lines.Insert(2, $"GC0 {gc0 - lastGc0}회/s, heap {heap / 1048576.0:F0}MB");
            // 맵이 프레임 제한을 걸었는지 확인하는 용도. 부하와 제한은 증상이 비슷해서 값을 직접 봐야 한다.
            lines.Insert(3, $"targetFrameRate {Application.targetFrameRate}, vSync {QualitySettings.vSyncCount}, captureFramerate {Time.captureFramerate}");
            try
            {
                lines.Insert(3, $"재생 중 tween {DG.Tweening.DOTween.TotalPlayingTweens()}개 / 활성 {DG.Tweening.DOTween.TotalActiveTweens()}개");
            }
            catch { }
            lastGc0 = gc0;
            frames = 0;

            if (lines.Count == 0) return;
            lines.Sort();
            LastReport = string.Join("  |  ", lines);
            Main.Entry.Logger.Log("[profile] " + LastReport);
        }
    }
}
