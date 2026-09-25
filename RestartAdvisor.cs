using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 게임 재시작 버튼과 "지금 재시작하면 좋은 때" 안내.
    //   - 멀티스레드 그리기(boot.config)를 바꿨다: 유니티는 시작할 때만 이 파일을 읽는다
    //   - 모드 파일이 새 버전으로 바뀌었다: 게임을 다시 켜야 새 DLL 이 올라온다
    //   - 게임이 메모리를 많이 쓰고 있다: 맵을 여러 번 열면 해제되지 않은 이미지·메모리가 쌓여 끊김(메모리 정리, VRAM 부족)이 늘어난다
    //   - 그래픽 메모리가 거의 찼다(곡 밖에서): 다음 맵에서 VRAM 부족 끊김이 나기 쉽다
    //   - 오래 켜 두었다(맵 20번 이상 또는 3시간 이상): 위와 같은 이유로 쌓인 것을 비운다
    // 재시작은 이 게임 프로세스가 끝난 뒤 같은 실행 파일을 같은 인자로 다시 켠다(PowerShell 이 기다렸다가 켬).
    internal static class RestartAdvisor
    {
        private static int songs;
        private static float startedAt = -1f;
        private static DateTime dllTime;
        private static string dllPath;
        private static float nextCheck;
        private static readonly List<string> reasons = new List<string>();

        internal static void Init()
        {
            startedAt = Time.realtimeSinceStartup;
            try
            {
                dllPath = Path.Combine(Main.Entry.Path, "StutterFix.dll");
                dllTime = File.GetLastWriteTimeUtc(dllPath);
            }
            catch { dllPath = null; }
        }

        internal static void SongStarted() { songs++; }

        // 재시작하면 좋은 이유들 (2초마다 다시 본다)
        internal static List<string> Reasons()
        {
            if (Time.realtimeSinceStartup < nextCheck) return reasons;
            nextCheck = Time.realtimeSinceStartup + 2f;
            reasons.Clear();
            try
            {
                if (BootConfig.Status != null && BootConfig.Status.Contains("다음 실행"))
                    reasons.Add(SettingsWindow.T("멀티스레드 그리기 설정을 바꿨습니다 (다시 켜야 적용)", "Multithreaded rendering was changed (applies after restart)"));
                if (dllPath != null && File.Exists(dllPath) && File.GetLastWriteTimeUtc(dllPath) > dllTime.AddSeconds(1))
                    reasons.Add(SettingsWindow.T("모드 파일이 새 버전으로 바뀌었습니다", "The mod file was updated"));
                float game = SystemMonitor.RamGameMB, total = SystemMonitor.RamTotalMB;
                if (game > 0 && total > 0 && (game > total * 0.5f || SystemMonitor.RamLoad >= 90f))   // RamLoad 는 시스템 메모리 사용률(%)
                    reasons.Add(string.Format(SettingsWindow.T("게임이 메모리를 많이 쓰고 있습니다 ({0:F1}GB)", "The game is using a lot of memory ({0:F1} GB)"), game / 1024f));
                int vram = SystemInfo.graphicsMemorySize;
                if (!Hitch.Playing && vram > 0 && SystemMonitor.VramUsedMB > vram * 0.9f)
                    reasons.Add(SettingsWindow.T("그래픽 메모리가 거의 찼습니다", "Graphics memory is almost full"));
                float hours = (Time.realtimeSinceStartup - startedAt) / 3600f;
                if (songs >= 20 || (startedAt >= 0 && hours >= 3f))
                    reasons.Add(string.Format(SettingsWindow.T("오래 켜 두었습니다 (맵 {0}번, {1:F1}시간)", "Running for a long time ({0} levels, {1:F1} h)"), songs, hours));
            }
            catch { }
            return reasons;
        }


        // ── 재시작 뒤 하던 맵 다시 열기 (에디터) ──
        // 에디터에서 맵을 열어 둔 채 재시작하면, 다시 켠 뒤 에디터로 가서 같은 파일을 연다(게임의 scnEditor.OpenLevel(경로) 그대로).
        // 저장 안 된 편집이 있으면 재시작하지 않는다: 게임을 끄면 그 편집이 사라지고, 게임이 끄기를 막으면
        // 기다리던 재시작 프로세스가 나중에 형이 게임을 끌 때 멋대로 다시 켜 버린다.
        internal static string LastBlock = "";
        internal static float LastBlockAt = -100f;
        private static int reopenStep;        // 0 없음, 1 첫 화면 기다림, 2 에디터 기다림
        private static float reopenStart, reopenWait;
        private static string reopenPath;

        private static scnEditor Editor() { try { return ADOBase.isLevelEditor ? scnEditor.instance : null; } catch { return null; } }

        private static readonly System.Reflection.MethodInfo unsavedGetter = AccessTools.PropertyGetter(typeof(scnEditor), "unsavedChanges");
        internal static string Blocked()
        {
            try { var e = Editor(); if (e != null && unsavedGetter != null && (bool)unsavedGetter.Invoke(e, null)) return SettingsWindow.T("에디터에 저장 안 된 변경이 있습니다. 저장한 뒤 재시작하세요", "The editor has unsaved changes. Save first, then restart"); }
            catch { }
            return null;
        }

        private static string ReopenTarget()
        {
            try
            {
                if (Editor() == null) return "";
                string p = ADOBase.levelPath;
                return !string.IsNullOrEmpty(p) && File.Exists(p) ? p : "";
            }
            catch { return ""; }
        }

        internal static bool WillReopen() { return ReopenTarget().Length > 0; }
        internal static string RecentBlock() { return Time.realtimeSinceStartup - LastBlockAt < 6f ? LastBlock : null; }

        internal static void StartReopen()
        {
            string p = Main.Config.ReopenLevel;
            if (string.IsNullOrEmpty(p)) return;
            Main.Config.ReopenLevel = "";                 // 한 번만 (실패해도 다음 실행에서 되풀이하지 않게)
            try { Main.Config.Save(Main.Entry); } catch { }
            if (!File.Exists(p)) { Main.Entry.Logger.Log("[재시작] 다시 열 맵이 없음: " + p); return; }
            reopenPath = p; reopenStep = 1; reopenStart = Time.realtimeSinceStartup; reopenWait = 0f;
            Main.Entry.Logger.Log("[재시작] 하던 맵을 다시 엽니다: " + p);
        }

        internal static void Tick()
        {
            if (reopenStep == 0) return;
            if (Time.realtimeSinceStartup - reopenStart > 90f) { reopenStep = 0; Main.Entry.Logger.Log("[재시작] 맵 다시 열기 시간 초과"); return; }
            try
            {
                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                if (reopenStep == 1)
                {
                    // 첫 메뉴 화면(시작 화면·불러오기·인트로가 아닌 첫 장면)이 뜨고 2초 기다린 뒤 에디터로 (게임이 준비를 끝내게)
                    if (scene == "" || scene == "scnSplash" || scene == "scnLoading" || scene == "scnIntro") { reopenWait = 0f; return; }
                    reopenWait += Time.unscaledDeltaTime;
                    if (reopenWait < 2f) return;
                    // ADOBase.GoToLevelEditor() 본문 그대로 (인스턴스 함수라 직접 부를 물체가 없다)
                    DG.Tweening.DOTween.KillAll(false);
                    ADOBase.loader.LoadScene("scnEditor");
                    reopenStep = 2; reopenWait = 0f;
                }
                else if (reopenStep == 2)
                {
                    var e = scene == "scnEditor" ? scnEditor.instance : null;
                    if (e == null) { reopenWait = 0f; return; }
                    reopenWait += Time.unscaledDeltaTime;
                    if (reopenWait < 1f) return;
                    reopenStep = 0;
                    var open = AccessTools.Method(typeof(scnEditor), "OpenLevel", new[] { typeof(string) });
                    if (open == null) { Main.Entry.Logger.Log("[재시작] scnEditor.OpenLevel(string) 없음"); return; }
                    open.Invoke(e, new object[] { reopenPath });
                    Main.Entry.Logger.Log("[재시작] 에디터에서 맵 열기: " + reopenPath);
                }
            }
            catch (Exception ex) { reopenStep = 0; Main.Entry.Logger.Log("[재시작] 맵 다시 열기 실패: " + (ex.InnerException ?? ex).Message); }
        }

        // 게임 종료 (저장 안 된 편집이 있으면 하지 않는다)
        internal static void Quit()
        {
            var block = Blocked();
            if (block != null) { LastBlock = block.Replace("재시작하세요", "종료하세요").Replace("then restart", "then quit"); LastBlockAt = Time.realtimeSinceStartup; Main.Entry.Logger.Log("[종료] 하지 않음: " + block); return; }
            Main.Entry.Logger.Log("[종료] 게임을 끕니다");
            try { Main.Config.ReopenLevel = ""; Main.Config.Save(Main.Entry); } catch { }
            Application.Quit();
        }

        // 게임을 끄고 다시 켠다
        internal static void Restart(bool reopen)
        {
            var block = Blocked();
            if (block != null) { LastBlock = block; LastBlockAt = Time.realtimeSinceStartup; Main.Entry.Logger.Log("[재시작] 하지 않음: " + block); return; }
            try { Main.Config.ReopenLevel = reopen ? ReopenTarget() : ""; } catch { }
            try
            {
                var me = Process.GetCurrentProcess();
                string exe = me.MainModule.FileName;
                var args = Environment.GetCommandLineArgs();
                var sb = new System.Text.StringBuilder();
                for (int i = 1; i < args.Length; i++) sb.Append(i > 1 ? " " : "").Append('"').Append(args[i].Replace("\"", "\\\"")).Append('"');
                string q = exe.Replace("'", "''");
                string argList = sb.ToString().Replace("'", "''");
                string ps = "Wait-Process -Id " + me.Id + " -ErrorAction SilentlyContinue; Start-Process -FilePath '" + q + "'"
                    + (argList.Length > 0 ? " -ArgumentList '" + argList + "'" : "") + " -WorkingDirectory '" + Path.GetDirectoryName(exe).Replace("'", "''") + "'";
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -WindowStyle Hidden -Command \"" + ps.Replace("\"", "\\\"") + "\"")
                { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
                // 모드 로더(Doorstop, winhttp.dll)는 "이미 붙었음" 을 DOORSTOP_ 환경 변수로 남기고, 이 값은 게임이 띄운 프로세스로 물려진다.
                // 그대로 두면 새로 켠 게임에서 Doorstop 이 이미 붙은 줄 알고 모드를 안 불러온다. 재시작용 프로세스에서는 지운다.
                var keys = new List<string>();
                foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables()) { var k = kv.Key as string; if (k != null && k.StartsWith("DOORSTOP", StringComparison.OrdinalIgnoreCase)) keys.Add(k); }
                foreach (var k in keys) psi.EnvironmentVariables.Remove(k);
                Main.Entry.Logger.Log("[재시작] 지운 모드 로더 표시: " + (keys.Count > 0 ? string.Join(", ", keys.ToArray()) : "없음"));
                Process.Start(psi);
                Main.Entry.Logger.Log("[재시작] 게임을 다시 켭니다");
                try { Main.Config.Save(Main.Entry); } catch { }
                Application.Quit();
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[재시작] 실패: " + ex.Message); }
        }
    }
}
