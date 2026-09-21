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
        private static readonly Dictionary<string, long> bytes = new Dictionary<string, long>();
        private static long lastHeap;
        private static PlayerLoopSystem original;
        private static bool installed;
        private static long lastStamp;
        private static string lastName;
        private static float sinceReport;
        private static int frames;
        private static float sinceComp;
        private static float runtime;

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
            runtime = 0f;
            lastStamp = Stopwatch.GetTimestamp();
            lastName = null;
            Main.Entry.Logger.Log("loop profiler installed");
            // 씬 집계와 함수별 시간 측정은 같이 켜지 않는다.
            // 둘 다 자기가 메모리를 잡아서(문자열, 배열) 할당량 측정을 오염시킨다.
        }

        private static PlayerLoopSystem MakeMarker(string name)
        {
            return new PlayerLoopSystem
            {
                type = typeof(Marker),
                updateDelegate = () => Record(name)
            };
        }

        // 측정기가 호출될 때마다, 직전 측정기 이후 흐른 시간과 늘어난 힙을 "직전 단계"의 몫으로 기록한다.
        //
        // 할당량이 중요하다. Update 계열을 전부 뒤졌을 때 1722MB 중 64MB(3.7%)밖에 설명되지 않았다.
        // 나머지는 Update 밖 - 코루틴이든 렌더링이든 - 어느 단계인지는 여기서만 알 수 있다.
        private static void Record(string name)
        {
            long now = Stopwatch.GetTimestamp();
            long heap = GC.GetTotalMemory(false);
            if (lastName != null)
            {
                long cur;
                totals.TryGetValue(lastName, out cur);
                totals[lastName] = cur + (now - lastStamp);

                long grown = heap - lastHeap;
                if (grown > 0)
                {
                    bytes.TryGetValue(lastName, out cur);
                    bytes[lastName] = cur + grown;
                }
            }
            lastStamp = now;
            lastHeap = heap;
            lastName = name;
        }

        internal static void Shutdown() { if (installed) Uninstall(); }

        private static void Uninstall()
        {
            PlayerLoop.SetPlayerLoop(original);
            installed = false;
            totals.Clear();
            bytes.Clear();
            lastName = null;
            Main.Entry.Logger.Log("loop profiler uninstalled");
        }

        internal static void Tick(float dt)
        {
            if (!installed) return;
            // 켠 뒤 20초가 지나면 스스로 끈다. 수동으로 끄다 보면 보고 전에 종료되는 일이 잦았다.
            runtime += dt;
            if (runtime >= 15f) { Uninstall(); return; }

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
            for (int i = 0; i < list.Count && i < 10; i++)
                parts.Add($"{list[i].Key} {list[i].Value:F0}ms/s");

            var alloc = new List<KeyValuePair<string, double>>();
            double totalMb = 0;
            foreach (var kv in bytes)
            {
                double mb = kv.Value / 1048576.0 / window;
                totalMb += mb;
                if (mb >= 1.0) alloc.Add(new KeyValuePair<string, double>(kv.Key, mb));
            }
            bytes.Clear();

            alloc.Sort((a, b) => b.Value.CompareTo(a.Value));
            var allocParts = new List<string>();
            for (int i = 0; i < alloc.Count && i < 8; i++)
                allocParts.Add($"{alloc[i].Key} {alloc[i].Value:F0}MB/s");

            LastReport = $"FPS {frames / window:F0} | 할당 {totalMb:F0}MB/s: " + string.Join(", ", allocParts.ToArray());
            frames = 0;
            Main.Entry.Logger.Log("[loop] " + LastReport);
            Main.Entry.Logger.Log("[loop] 시간: " + string.Join(", ", parts.ToArray()));
        }
    }
}
