using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.LowLevel;

namespace StutterFix
{
    // 유니티는 매 프레임 내부 단계(입력, 업데이트, 애니메이션, 렌더링 준비 등)를 순서대로 실행한다.
    // 그 단계 사이사이에 측정용 시스템을 끼워 넣어, 어느 단계가 시간을 잡아먹는지 알아낸다.
    // 게임 스크립트가 프레임 시간의 8%뿐인데 프레임이 20ms씩 걸리는 이유를 찾기 위한 도구.
    public static class LoopProfiler
    {
        private struct Marker { public string Name; }

        private static readonly Dictionary<string, long> totals = new Dictionary<string, long>();
        private static PlayerLoopSystem original;
        private static bool installed;
        private static long lastStamp;
        private static string lastName;
        private static float sinceReport;
        private static int frames;

        internal static bool Running => installed;
        internal static string LastReport = "(측정 안 함)";

        internal static void Toggle()
        {
            if (installed) Uninstall(); else Install();
        }

        private static void Install()
        {
            original = PlayerLoop.GetCurrentPlayerLoop();
            var root = PlayerLoop.GetCurrentPlayerLoop();

            var newTop = new List<PlayerLoopSystem>();
            foreach (var top in root.subSystemList)
            {
                string topName = top.type != null ? top.type.Name : "?";
                var children = new List<PlayerLoopSystem>();

                if (top.subSystemList != null)
                {
                    foreach (var child in top.subSystemList)
                    {
                        string name = topName + "/" + (child.type != null ? child.type.Name : "?");
                        children.Add(MakeMarker(name));
                        children.Add(child);
                    }
                }
                children.Add(MakeMarker(topName + "/end"));

                var copy = top;
                copy.subSystemList = children.ToArray();
                newTop.Add(copy);
            }

            root.subSystemList = newTop.ToArray();
            PlayerLoop.SetPlayerLoop(root);
            installed = true;
            lastStamp = Stopwatch.GetTimestamp();
            lastName = null;
            Main.Entry.Logger.Log("loop profiler installed");
        }

        private static PlayerLoopSystem MakeMarker(string name)
        {
            return new PlayerLoopSystem
            {
                type = typeof(Marker),
                updateDelegate = () => Record(name)
            };
        }

        // 측정기가 호출될 때마다, 직전 측정기 이후 흐른 시간을 "직전 단계"의 몫으로 기록한다.
        private static void Record(string name)
        {
            long now = Stopwatch.GetTimestamp();
            if (lastName != null)
            {
                long d = now - lastStamp;
                long cur;
                totals.TryGetValue(lastName, out cur);
                totals[lastName] = cur + d;
            }
            lastStamp = now;
            lastName = name;
        }

        private static void Uninstall()
        {
            PlayerLoop.SetPlayerLoop(original);
            installed = false;
            totals.Clear();
            lastName = null;
            Main.Entry.Logger.Log("loop profiler uninstalled");
        }

        internal static void Tick(float dt)
        {
            if (!installed) return;
            frames++;
            sinceReport += dt;
            if (sinceReport < 1f) return;

            float window = sinceReport;
            sinceReport = 0f;

            var list = new List<KeyValuePair<string, double>>();
            foreach (var kv in totals)
            {
                double ms = kv.Value * 1000.0 / Stopwatch.Frequency / window;
                if (ms >= 3.0) list.Add(new KeyValuePair<string, double>(kv.Key, ms));
            }
            totals.Clear();

            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var parts = new List<string>();
            for (int i = 0; i < list.Count && i < 12; i++)
            {
                parts.Add($"{list[i].Key} {list[i].Value:F0}ms/s");
            }

            LastReport = $"FPS {frames / window:F0} | " + string.Join(", ", parts);
            frames = 0;
            Main.Entry.Logger.Log("[loop] " + LastReport);
        }
    }
}
