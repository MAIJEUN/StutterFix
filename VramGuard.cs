using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace StutterFix
{
    // 큰 이미지 줄이기 "자동": 실제로 VRAM 이 모자라 끊긴 맵만 기억해 두었다가, 다음에 불러올 때 한 단계씩 줄인다.
    //
    // 처음에는 맵을 열 때 이미지 전체 크기로 어림해서 미리 줄였는데, 전체 크기로는 끊김을 예측할 수 없었다.
    //   CICADA3302: PNG 2,800장, 원본으로 올리면 13GB. 그래도 원본으로 끊김 없이 돌았다. 어림으로는 1024 까지 줄여 화질만 버렸다.
    //   이미지 2,000장 맵: 어림 9.5GB. 원본에서 VRAM 95% 가 되며 카메라가 움직일 때마다 150~200ms 멈췄다. 3072 로 줄이자 해결.
    // 그래픽카드는 한꺼번에 화면에 쓰이는 이미지만 VRAM 에 올려 두기 때문에, 중요한 것은 전체 양이 아니라 그 순간의 양이다.
    //
    // 그래서 지금은 처음에는 원본으로 불러오고, 플레이/편집 중에 "VRAM 90% 이상 + GPU 가 멈춘 45ms 넘는 끊김"이
    // 두 번 나오면 그 맵을 한 단계 낮춰 기억한다 (원본 -> 3072 -> 2048 -> 1536 -> 1024). 다음에 불러올 때 그 한도로 줄인다.
    // 알림으로 알려 주고, 설정 창에서 기억한 맵을 지울 수 있다.
    internal static class VramGuard
    {
        private static readonly int[] steps = { 0, 3072, 2048, 1536, 1024 };
        internal static string Level = "";
        internal static int CurrentCap;
        private static int events;
        private static bool noted;

        internal static int Next(int cap)
        {
            for (int i = 0; i < steps.Length - 1; i++) if (steps[i] == cap) return steps[i + 1];
            if (cap > 3072) return 3072;
            return cap;   // 1024 가 마지막
        }

        // ── 기억 (Settings.VramCaps: "경로\t한도" 줄들) ─────────────────────
        private static Dictionary<string, int> Load()
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var s = Main.Config != null ? Main.Config.VramCaps : null;
            if (string.IsNullOrEmpty(s)) return d;
            foreach (var line in s.Split('\n'))
            {
                int tab = line.LastIndexOf('\t');
                int v;
                if (tab > 0 && int.TryParse(line.Substring(tab + 1), out v)) d[line.Substring(0, tab)] = v;
            }
            return d;
        }

        private static void Save(Dictionary<string, int> d)
        {
            var sb = new StringBuilder();
            foreach (var kv in d) sb.Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
            Main.Config.VramCaps = sb.ToString();
            try { Main.Config.Save(Main.Entry); } catch { }
        }

        internal static int CapFor(string level)
        {
            int v;
            return !string.IsNullOrEmpty(level) && Load().TryGetValue(level, out v) ? v : 0;
        }

        internal static int Remembered { get { return Load().Count; } }
        internal static void Forget() { if (Main.Config != null) { Main.Config.VramCaps = ""; try { Main.Config.Save(Main.Entry); } catch { } } }

        internal static void OnLevelLoaded(string level, int cap, bool decoding)
        {
            level = level ?? "";
            if (decoding || level != Level) CurrentCap = decoding ? cap : 0;
            Level = level;
            events = 0;
            noted = false;
        }

        // ── 곡/편집 중 감시 ─────────────────────────────────────────────
        internal static bool Watching { get { return ImagePrefetch.MaxSide == ImagePrefetch.Auto && Level.Length > 0 && !noted; } }

        internal static void Tick()
        {
            bool want = Watching;
            SystemMonitor.Keep = want;
            if (want) SystemMonitor.Start();
        }

        // 실시간 모니터가 "GPU 과부하" 로 가린 끊김마다 불린다 (그 프레임의 GPU 시간이 70% 넘게 차지).
        // 예전에는 "VRAM 90% + 45ms 넘는 프레임" 만 봐서, VRAM 이 높은 맵의 게임 처리(CPU) 끊김이나 효과 몰림까지
        // VRAM 부족으로 잘못 기억했다(Arche: 효과 몰림 147ms, 게임 처리 100~120ms 인데 3072 로 기억).
        // 이미지를 줄여도 CPU 끊김은 그대로이므로, GPU 가 멈춘 끊김만 센다.
        internal static void GpuHitch(float ms)
        {
            if (!Watching || ms < 45f || PerfOverlay.IsLoadingNow || ImagePrefetch.Running) return;
            float used = SystemMonitor.VramUsedMB;
            long total = SystemInfo.graphicsMemorySize;
            if (used <= 0 || total <= 0 || used < total * 0.9f) return;
            if (++events < 2) return;

            int next = Next(CurrentCap);
            noted = true;
            if (next == CurrentCap) return;
            var d = Load();
            d[Level] = next;
            Save(d);
            string what = SettingsWindow.T("그래픽 메모리가 가득 차서(", "VRAM was full (") + used.ToString("F0") + "/" + total + "MB"
                + SettingsWindow.T(") 끊겼습니다. 다음에 이 맵을 불러올 때 큰 이미지를 긴 변 ", ") and caused stutter. Next time this level loads, large images will be capped at ")
                + next + SettingsWindow.T(" 으로 줄입니다", " px");
            Main.Entry.Logger.Log("[이미지] 자동: VRAM 부족으로 끊김 -> 이 맵은 다음부터 긴 변 " + next + " (" + Level + ")");
            PerfOverlay.Notice(SettingsWindow.T("VRAM 부족", "VRAM full"), what);
        }
    }
}
