using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityModManagerNet;

namespace StutterFix
{
    // 모드마다 매 프레임 얼마나 메모리를 새로 잡아먹는지 잰다.
    //
    // 곡 중에는 GC를 꺼 두므로 힙은 줄지 않고 늘어나기만 한다.
    // 그래서 함수 전후의 힙 차이가 곧 그 함수가 새로 잡은 양이다. (정리와 섞이지 않아 값이 깨끗하다)
    //
    // 지금 이 게임은 초당 100MB 넘게 새로 잡고 있어서, GC를 아무리 미뤄도 몇십 초면 한계에 닿는다.
    // 누가 그만큼 잡는지부터 알아야 한다.
    internal static class ModWatch
    {
        private class Stat
        {
            public long Bytes;
            public long Ticks;
            public long TotalBytes;
        }

        private static readonly Dictionary<string, Stat> stats = new Dictionary<string, Stat>();
        private static bool installed;
        private static float window;

        internal static string Summary = "(아직 없음)";

        // 다른 모드의 갱신 함수를 감싸 두었으므로, 내려갈 때 원래 것으로 돌려놓아야 한다.
        // 그러지 않으면 다시 불러올 때마다 한 겹씩 더 감싸지고, 옛 코드가 계속 불린다.
        private class Original
        {
            public UnityModManager.ModEntry Entry;
            public Action<UnityModManager.ModEntry, float> Update, Late, Fixed;
        }

        private static readonly List<Original> originals = new List<Original>();

        internal static void Shutdown()
        {
            foreach (var o in originals)
            {
                try
                {
                    o.Entry.OnUpdate = o.Update;
                    o.Entry.OnLateUpdate = o.Late;
                    o.Entry.OnFixedUpdate = o.Fixed;
                }
                catch { }
            }
            originals.Clear();
            installed = false;
        }

        internal static void Install()
        {
            if (installed) return;
            installed = true;
            try
            {
                foreach (var entry in UnityModManager.modEntries)
                {
                    if (entry == null || entry.Info == null) continue;
                    if (entry.Info.Id == Main.Entry.Info.Id) continue;

                    string id = entry.Info.Id;
                    originals.Add(new Original { Entry = entry, Update = entry.OnUpdate, Late = entry.OnLateUpdate, Fixed = entry.OnFixedUpdate });
                    var update = entry.OnUpdate;
                    if (update != null)
                        entry.OnUpdate = (e, dt) => Measure(id, () => update(e, dt));

                    var late = entry.OnLateUpdate;
                    if (late != null)
                        entry.OnLateUpdate = (e, dt) => Measure(id, () => late(e, dt));

                    var fixedUp = entry.OnFixedUpdate;
                    if (fixedUp != null)
                        entry.OnFixedUpdate = (e, dt) => Measure(id, () => fixedUp(e, dt));
                }
                Main.Entry.Logger.Log("mod watch installed");
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("mod watch install failed: " + ex.Message);
            }
        }

        private static void Measure(string id, Action body)
        {
            long h0 = GC.GetTotalMemory(false);
            long t0 = Stopwatch.GetTimestamp();
            body();
            long bytes = GC.GetTotalMemory(false) - h0;
            long ticks = Stopwatch.GetTimestamp() - t0;

            Stat s;
            if (!stats.TryGetValue(id, out s)) { s = new Stat(); stats[id] = s; }
            if (bytes > 0) { s.Bytes += bytes; s.TotalBytes += bytes; }
            s.Ticks += ticks;
        }

        internal static void Tick(float dt)
        {
            window += dt;
            if (window < 2f) return;

            string best = null;
            double bestMb = 0;
            var parts = new List<string>();
            foreach (var kv in stats)
            {
                double mb = kv.Value.Bytes / 1048576.0 / window;
                double ms = kv.Value.Ticks * 1000.0 / Stopwatch.Frequency / window;
                if (mb >= 1 || ms >= 1)
                    parts.Add(string.Format("{0} {1:F0}MB/s {2:F0}ms/s", kv.Key, mb, ms));
                if (mb > bestMb) { bestMb = mb; best = kv.Key; }
                kv.Value.Bytes = 0;
                kv.Value.Ticks = 0;
            }
            window = 0f;

            Summary = parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "모드들은 거의 안 씀";
            Top = best != null && bestMb >= 1 ? string.Format("{0} {1:F0}MB/s", best, bestMb) : "없음";
        }

        internal static string Top = "없음";

        internal static void Report()
        {
            var parts = new List<string>();
            foreach (var kv in stats)
            {
                double mb = kv.Value.TotalBytes / 1048576.0;
                if (mb >= 10) parts.Add(string.Format("{0} {1:F0}MB", kv.Key, mb));
                kv.Value.TotalBytes = 0;
            }
            Main.Entry.Logger.Log("[모드별 할당] " + (parts.Count > 0 ? string.Join(", ", parts.ToArray()) : "모드들은 거의 안 씀 (게임 본체가 잡는 중)"));
        }
    }
}
