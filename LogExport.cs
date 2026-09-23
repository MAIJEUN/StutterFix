using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEngine;
using UnityModManagerNet;

namespace StutterFix
{
    // 문제 보고용 로그 묶기. 플레이어가 설정 창 "정보"(또는 UMM)에서 버튼을 누르면 바탕화면에 zip 하나를 만든다.
    // 이 파일을 디스코드 narooh 에게 DM 으로 보내 달라고 안내한다. 자동으로 어디에 올리지는 않는다.
    //
    // 담는 것:
    //   보고서.txt      : 모드 버전/설정, 컴퓨터 사양(CPU/GPU/RAM/화면), 설치된 모드 목록, 이번 실행의 끊김 기록
    //   Player.log      : 이번 실행의 게임 로그 (모드 오류와 로딩 기록이 여기 남는다)
    //   Player-prev.log : 직전 실행의 게임 로그 (게임이 튕겼다면 원인이 여기 있다)
    //   Settings.xml    : 이 모드의 설정 파일
    // 로그 안의 윈도우 사용자 이름(C:\Users\이름\...)은 <사용자> 로 가린다.
    internal static class LogExport
    {
        internal static string LastPath = "";
        internal static string LastError = "";

        internal static string Export()
        {
            LastError = "";
            try
            {
                string desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrEmpty(desk) || !Directory.Exists(desk)) desk = Path.GetTempPath();
                string path = Path.Combine(desk, "StutterFix-log-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");

                string logDir = Application.persistentDataPath;   // ...\LocalLow\7th Beat Games\A Dance of Fire and Ice
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    AddText(zip, "보고서.txt", Report());
                    AddFile(zip, "Player.log", Path.Combine(logDir, "Player.log"));
                    AddFile(zip, "Player-prev.log", Path.Combine(logDir, "Player-prev.log"));
                    if (Main.Entry != null) AddFile(zip, "Settings.xml", Path.Combine(Main.Entry.Path, "Settings.xml"));
                }
                LastPath = path;
                return path;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                return null;
            }
        }

        // 파일 탐색기에서 만든 파일을 골라 보여 준다
        internal static void Reveal()
        {
            try
            {
                if (File.Exists(LastPath)) System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + LastPath + "\"");
            }
            catch { }
        }

        private static string Mask(string s)
        {
            try
            {
                string user = Environment.UserName;
                if (!string.IsNullOrEmpty(user) && user.Length >= 2)
                {
                    s = s.Replace("\\Users\\" + user, "\\Users\\<사용자>").Replace("/Users/" + user, "/Users/<사용자>");
                }
            }
            catch { }
            return s;
        }

        private static void AddText(ZipArchive zip, string name, string text)
        {
            var e = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.Optimal);
            using (var w = new StreamWriter(e.Open(), new UTF8Encoding(true))) w.Write(Mask(text));
        }

        private static void AddFile(ZipArchive zip, string name, string src)
        {
            try
            {
                if (!File.Exists(src)) return;
                string text;
                // 게임이 쓰는 중인 파일이라 공유 모드로 연다
                using (var f = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(f, Encoding.UTF8)) text = r.ReadToEnd();
                AddText(zip, name, text);
            }
            catch (Exception ex) { AddText(zip, name + ".읽기실패.txt", ex.Message); }
        }

        private static string Report()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Stutter Fix 문제 보고");
            sb.AppendLine("만든 시각: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            try { sb.AppendLine("모드: v" + Main.Entry.Info.Version + " (" + Edition.Name + ")"); } catch { }
            sb.AppendLine("게임: " + Application.version + " / Unity " + Application.unityVersion);
            sb.AppendLine();

            sb.AppendLine("── 컴퓨터 ──");
            sb.AppendLine("OS: " + SystemInfo.operatingSystem);
            sb.AppendLine("CPU: " + SystemInfo.processorType + " (" + SystemInfo.processorCount + "스레드)");
            sb.AppendLine("RAM: " + SystemInfo.systemMemorySize + "MB");
            sb.AppendLine("GPU: " + SystemInfo.graphicsDeviceName + " (" + SystemInfo.graphicsMemorySize + "MB, " + SystemInfo.graphicsDeviceType + ", " + SystemInfo.graphicsDeviceVersion + ")");
            sb.AppendLine("화면: " + Screen.width + "x" + Screen.height + " " + Screen.fullScreenMode + ", 주사율 " + Screen.currentResolution.refreshRateRatio.value.ToString("F0") + "Hz");
            sb.AppendLine("수직동기: " + QualitySettings.vSyncCount + ", 목표 FPS: " + Application.targetFrameRate);
            try { sb.AppendLine(BootConfig.Describe()); } catch { }
            sb.AppendLine();

            sb.AppendLine("── 이 모드 설정 ──");
            var c = Main.Config;
            if (c != null)
            {
                sb.AppendLine("메모리 정리 미루기 " + On(c.GcPause) + ", 효과 몰림 나누기 " + On(c.EffectSplit) + ", 타일 색 나누기 " + On(c.RecolorSplit));
                sb.AppendLine("애니메이션 최적화 " + On(c.TweenGuard) + ", 글자 장식 최적화 " + On(c.SkipSameText) + ", 그래픽 미리 준비 " + On(c.ShaderWarm));
                sb.AppendLine("즉시 이동 최적화 " + On(c.ZeroTween) + ", 즉시 이동 직접 처리 " + On(c.InstantDirect) + ", 투명 장식 빠른 처리 " + On(c.SkipSame) + ", 장식 이동 루프 " + On(c.FastLoop) + ", 미리 확인 " + On(c.Precheck) + ", 장식 애니메이션 직접 처리 " + On(c.DecoAnim) + ", 장식 위치 계산 줄이기 " + On(c.MoveFinish)
                    + ", 장식 순회 줄이기 " + On(c.DormantSkip) + ", 투명한 장식 그리지 않기 " + On(c.SkipInvisible) + ", 투명한 장식 위치 미루기 " + On(c.LazyHidden));
                sb.AppendLine("블렌드 빠르게 " + On(c.FastBlend) + " (지금 " + FastBlend.Count + "개), 이미지 빠르게 불러오기 " + On(c.ImagePrefetch)
                    + ", 큰 이미지 줄이기 " + (c.ImageMaxSide == ImagePrefetch.Auto ? "자동 (" + ImagePrefetch.AutoNote + ")" : c.ImageMaxSide > 0 ? c.ImageMaxSide + "" : "끔") + ", 정리 건너뛰기 " + On(c.SkipAssetUnload) + ", 멀티스레드 그리기 " + On(c.LegacyGfxJobs));
            }
            sb.AppendLine();

            sb.AppendLine("── 설치된 모드 ──");
            try
            {
                foreach (var m in UnityModManager.modEntries)
                    sb.AppendLine(string.Format("{0} {1} {2}{3}", m.Info.Id, m.Info.Version, m.Enabled ? "켜짐" : "꺼짐", m.ErrorOnLoading ? " (불러오기 오류)" : ""));
            }
            catch (Exception ex) { sb.AppendLine("목록을 읽지 못함: " + ex.Message); }
            sb.AppendLine();

            sb.AppendLine("── 이번 실행의 끊김 기록 (실시간 모니터) ──");
            try { sb.Append(PerfOverlay.ReportText()); } catch (Exception ex) { sb.AppendLine("기록을 읽지 못함: " + ex.Message); }
            return sb.ToString();
        }

        private static string On(bool b) { return b ? "켜짐" : "꺼짐"; }
    }
}
