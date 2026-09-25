using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using HarmonyLib;
using UnityModManagerNet;

namespace StutterFix
{
    // 다른 모드와 겹치는 기능 알아채기.
    // Quartz(최적화 모듈)가 켜져 있으면 그 설정 파일(Mods/Quartz/UserData/Optimizer.json)을 읽어, 같은 일을 하는 기능끼리 겹치는지 본다.
    //   - 같은 함수를 두 모드가 고치면 순서에 따라 한쪽이 헛돌거나 두 번 일하므로, 겹치는 우리 기능은 쉬게 하고(Quartz 가 계속 하게) 화면에 알린다.
    //   - 메모리 정리(부드러운 GC)는 우리 쪽이 재시작 때 쌓인 양을 보고 정리를 건너뛰므로 둘 다 켜져 있어도 두 번 치우지는 않는다. 대신 Quartz 쪽은
    //     실패·재시작마다 치우므로 끄는 것을 권한다(화면에 안내만 하고 다른 모드 설정 파일은 건드리지 않는다).
    // 그리고 모드가 고친 게임 함수 중 다른 모드도 고치는 것을 로그에 한 번 남긴다(겹침 문제를 로그로 찾을 수 있게).
    internal static class Compat
    {
        internal static bool Quartz, QuartzOptimizer;
        internal static bool QSmoothGC, QCollectOnLoad, QPriority, QSkipIdleParticles, QPauseOffscreenParticles, QSkipNoOpFilters, QLeakGuard, QLossyTexture, QFastBloom, QCacheScreenScale;
        private static float nextRead = -100f;
        private static DateTime optTime;

        // PACL2: "메모리 최적화" 기능 + "이미지 손실 압축 허용" 이 켜져 있으면 이미지를 올릴 때마다 메인 스레드에서 압축한다
        // (MemoryOptimizerPatches.Texture2DApplyPrefix -> Texture2D.Compress(false), IL 확인). 이때 이미지 미리 풀기에서 미리 압축해 둔다.
        internal static bool Pacl2, Pacl2Lossy;
        private static DateTime pacl2Time;
        private static void RefreshPacl2()
        {
            try
            {
                var m = UnityModManager.FindMod("PACL2");
                Pacl2 = m != null && m.Active;
                if (!Pacl2) { Pacl2Lossy = false; pacl2Time = default(DateTime); return; }
                string f = Path.Combine(m.Path, "Settings.json");
                if (!File.Exists(f)) { Pacl2Lossy = false; return; }
                var t = File.GetLastWriteTimeUtc(f);
                if (t == pacl2Time) return;
                pacl2Time = t;
                string j = File.ReadAllText(f);
                bool feature = Regex.IsMatch(j, "\"MemoryOptimizer\"\\s*:\\s*\\{\\s*\"Enabled\"\\s*:\\s*true");
                Pacl2Lossy = feature && Flag(j, "AllowLossyCompression");
                Main.Entry.Logger.Log("[다른 모드] PACL2 메모리 최적화 " + (feature ? "켜짐" : "꺼짐") + ", 이미지 손실 압축 " + (Pacl2Lossy ? "켜짐 (이미지 미리 풀기에서 여러 코어로 미리 압축)" : "꺼짐"));
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[다른 모드] PACL2 설정 읽기 실패: " + ex.Message); }
        }

        // 2초마다만 파일을 다시 본다 (설정 창에서 매 프레임 불려도 싸게)
        internal static void Refresh()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < nextRead) return;
            nextRead = now + 2f;
            RefreshPacl2();
            try
            {
                var m = UnityModManager.FindMod("Quartz");
                Quartz = m != null && m.Active;
                if (!Quartz) { QuartzOptimizer = false; SetAll(false); return; }
                string ud = Path.Combine(m.Path, "UserData");
                string installed = Path.Combine(Path.Combine(ud, "Module"), "installed.json");
                QuartzOptimizer = !File.Exists(installed) || Regex.IsMatch(File.ReadAllText(installed), "\"optimizer\"\\s*:\\s*\\{\\s*\"enabled\"\\s*:\\s*true");
                string opt = Path.Combine(ud, "Optimizer.json");
                if (!QuartzOptimizer || !File.Exists(opt)) { SetAll(false); return; }
                var t = File.GetLastWriteTimeUtc(opt);
                if (t == optTime) return;
                optTime = t;
                string j = File.ReadAllText(opt);
                QSmoothGC = Flag(j, "SmoothGC"); QCollectOnLoad = Flag(j, "CollectOnLevelLoad"); QPriority = Flag(j, "BoostProcessPriority");
                QSkipIdleParticles = Flag(j, "SkipIdleParticles"); QPauseOffscreenParticles = Flag(j, "PauseOffscreenParticles");
                QSkipNoOpFilters = Flag(j, "SkipNoOpScreenFilters"); QLeakGuard = Flag(j, "LeakGuard"); QLossyTexture = Flag(j, "LossyTextureCompression");
                QFastBloom = Flag(j, "FastBloom"); QCacheScreenScale = Flag(j, "CacheScreenScale");
                Main.Entry.Logger.Log("[다른 모드] Quartz 최적화: " + Describe());
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[다른 모드] Quartz 설정 읽기 실패: " + ex.Message); }
        }

        private static void SetAll(bool v)
        {
            QSmoothGC = QCollectOnLoad = QPriority = QSkipIdleParticles = QPauseOffscreenParticles = QSkipNoOpFilters = QLeakGuard = QLossyTexture = QFastBloom = QCacheScreenScale = v;
            optTime = default(DateTime);
        }
        private static bool Flag(string json, string key) { return Regex.IsMatch(json, "\"" + key + "\"\\s*:\\s*true"); }

        internal static string Describe()
        {
            if (!Quartz) return "없음";
            if (!QuartzOptimizer) return "최적화 모듈 꺼짐";
            var on = new List<string>();
            if (QSmoothGC) on.Add("부드러운 GC"); if (QCollectOnLoad) on.Add("로드 시 힙 정리"); if (QPriority) on.Add("우선순위");
            if (QSkipIdleParticles) on.Add("변화 없는 파티클"); if (QPauseOffscreenParticles) on.Add("화면 밖 파티클"); if (QSkipNoOpFilters) on.Add("효과 없는 필터");
            if (QLeakGuard) on.Add("메모리 누수"); if (QLossyTexture) on.Add("텍스처 압축"); if (QFastBloom) on.Add("빠른 블룸"); if (QCacheScreenScale) on.Add("화면 크기 재조정");
            return on.Count > 0 ? string.Join(", ", on.ToArray()) : "켜진 것 없음";
        }

        // 설정 창에 보일 겹침 안내 (한 줄씩)
        internal static List<string> Notes()
        {
            Refresh();
            var n = new List<string>();
            var c = Main.Config;
            if (Pacl2Lossy)
                n.Add(c.ImagePrefetch
                    ? SettingsWindow.T("PACL2 '이미지 손실 압축': PACL2 는 이미지를 한 장씩 메인 스레드에서 압축해 맵 불러오기가 느려집니다(Arche 11.5초). 이 모드가 이미지 미리 풀기에서 여러 코어로 미리 압축해 대신 넣습니다.",
                        "PACL2 'lossy image compression': PACL2 compresses images one at a time on the main thread, slowing level loads (Arche 11.5 s). This mod pre-compresses them on several cores during parallel image loading and hands the result over.")
                    : SettingsWindow.T("PACL2 '이미지 손실 압축': 맵 불러오기가 느려집니다. '맵 불러오기' 페이지의 '이미지 빠르게 불러오기' 를 켜면 여러 코어로 미리 압축합니다.",
                        "PACL2 'lossy image compression' slows level loads. Turn on 'Parallel image loading' to pre-compress on several cores."));
            if (Quartz && QuartzOptimizer && QLossyTexture)
                n.Add(SettingsWindow.T("Quartz '손실 텍스처 압축': 이미지를 한 장씩 메인 스레드에서 불러와 압축하고 이 모드의 이미지 미리 풀기도 건너뜁니다. 끄고 저사양 페이지의 '이미지 압축해서 불러오기' 를 쓰는 것을 권합니다.",
                    "Quartz 'Lossy texture compression' loads and compresses images one at a time on the main thread and bypasses this mod's parallel loading. Turn it off and use 'Compress images on load' on the Low-end page instead."));
            if (!Quartz || !QuartzOptimizer) return n;
            if (QSmoothGC && c.GcPause)
                n.Add(SettingsWindow.T("메모리 정리: Quartz '부드러운 GC' 와 같은 일을 합니다. 둘 다 켜 두어도 문제는 없지만, Quartz 쪽은 실패·재시작마다 정리해서 재시작이 느려질 수 있습니다. Quartz 쪽을 끄는 것을 권합니다 (이 모드는 쌓인 양이 적으면 건너뜁니다).",
                    "Memory cleanup: same job as Quartz 'Smooth GC'. Both on is safe, but Quartz cleans on every fail/restart, which can slow restarts. Turning Quartz's off is recommended (this mod skips when little has built up)."));
            if (QPriority && c.LowPriority)
                n.Add(SettingsWindow.T("게임 우선순위: Quartz '프로세스 우선순위 높이기' 와 겹칩니다. 이 모드 쪽(높음)이 적용되고, 끄면 Quartz 설정으로 돌아갑니다.",
                    "Game priority: overlaps Quartz 'Boost process priority'. This mod's (High) applies; turning it off returns to Quartz's."));
            if (QSkipIdleParticles)
                n.Add(SettingsWindow.T("변화 없는 파티클 갱신 건너뛰기: Quartz 가 이미 하고 있어 이 모드 쪽은 쉽니다.", "Idle particle updates: Quartz already does this, so this mod's version is idle."));
            if (QPauseOffscreenParticles && c.LowPauseParticles)
                n.Add(SettingsWindow.T("화면 밖 파티클 멈추기: Quartz 가 이미 하고 있어 이 모드 쪽은 쉽니다.", "Pause off-screen particles: Quartz already does this, so this mod's version is idle."));
            return n;
        }

        // 모드가 고친 게임 함수 중 다른 모드도 고친 것 (게임이 켜지고 조금 뒤 한 번 로그에 남긴다)
        private static bool logged;
        internal static void LogSharedPatches()
        {
            if (logged) return;
            logged = true;
            try
            {
                var shared = new List<string>();
                foreach (var m in Harmony.GetAllPatchedMethods())
                {
                    var info = Harmony.GetPatchInfo(m);
                    if (info == null) continue;
                    bool mine = false; var others = new HashSet<string>();
                    foreach (var o in info.Owners) { if (o.StartsWith("StutterFix", StringComparison.OrdinalIgnoreCase)) mine = true; else others.Add(o); }
                    if (mine && others.Count > 0) shared.Add((m.DeclaringType != null ? m.DeclaringType.Name + "." : "") + m.Name + " (" + string.Join(", ", new List<string>(others).ToArray()) + ")");
                }
                Main.Entry.Logger.Log("[다른 모드] 같은 게임 함수를 고치는 곳 " + shared.Count + "개" + (shared.Count > 0 ? ": " + string.Join(" | ", shared.ToArray()) : ""));
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[다른 모드] 겹침 확인 실패: " + ex.Message); }
        }
    }
}
