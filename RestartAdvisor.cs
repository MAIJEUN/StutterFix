using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
                if (game > 0 && total > 0 && (game > total * 0.5f || SystemMonitor.RamLoad > 0.9f))
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

        // 게임을 끄고 다시 켠다
        internal static void Restart()
        {
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
                Process.Start(psi);
                Main.Entry.Logger.Log("[재시작] 게임을 다시 켭니다");
                try { Main.Config.Save(Main.Entry); } catch { }
                Application.Quit();
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[재시작] 실패: " + ex.Message); }
        }
    }
}
