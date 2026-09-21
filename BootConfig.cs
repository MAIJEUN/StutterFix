using System;
using System.IO;
using UnityEngine;

namespace StutterFix
{
    // 실행 옵션 없이 그래픽 작업 분산(legacy)을 켠다.
    //
    // 측정 결과 (같은 맵, 같은 PC):
    //   D3D12 + native 작업    : 평균 프레임 높음, 곡 중 그래픽 메모리를 새로 잡다가 60~80ms씩 멈춤
    //   D3D11 기본(Threaded)   : 멈춤 없음, 프레임 140
    //   D3D11 + legacy 작업     : 멈춤 없음, 프레임 160, 곡 전체 끊김 150번대 -> 93번
    //
    // `-force-gfx-jobs legacy` 실행 옵션과 같은 일을 게임 폴더의 boot.config 로 한다.
    // 엔진 안의 방식 번호: 0 Direct, 1 NonThreaded, 2 Threaded, 3 ClientWorkerJobs(=legacy),
    //                      4 ClientWorkerNativeJobs, 5 DirectNativeJobs, 6 SplitJobs
    // 게임에 들어 있는 값은 6(SplitJobs)인데 D3D11 에서는 지원되지 않아 2(Threaded)로 되돌아가고 있었다.
    //
    // 유니티는 시작할 때만 이 파일을 읽으므로 바꾼 뒤 한 번 재시작해야 적용된다.
    // 원래 파일은 백업해 두고, 설정에서 끄면 원래 값으로 돌려놓는다.
    // 게임 업데이트나 Steam 파일 검사로 되돌아가도 다음 실행 때 다시 적용한다.
    public static class BootConfig
    {
        private const string Key = "gfx-threading-mode";
        private const string Legacy = "3";

        internal static string Status = "";

        private static string ConfigPath { get { return Path.Combine(Application.dataPath, "boot.config"); } }
        private static string BackupPath { get { return ConfigPath + ".stutterfix-backup"; } }

        internal static void Apply(bool enable)
        {
            try
            {
                string path = ConfigPath;
                if (!File.Exists(path)) { Status = "boot.config 없음"; return; }

                var lines = File.ReadAllLines(path);
                int idx = Array.FindIndex(lines, l => l.StartsWith(Key + "=", StringComparison.Ordinal));
                string current = idx >= 0 ? lines[idx].Substring(Key.Length + 1).Trim() : null;

                string wanted;
                if (enable)
                {
                    wanted = Legacy;
                    if (!File.Exists(BackupPath)) File.Copy(path, BackupPath);
                }
                else
                {
                    if (!File.Exists(BackupPath)) { Status = Describe(); return; }   // 바꾼 적이 없다
                    var orig = Array.Find(File.ReadAllLines(BackupPath), l => l.StartsWith(Key + "=", StringComparison.Ordinal));
                    wanted = orig != null ? orig.Substring(Key.Length + 1).Trim() : null;
                }

                if (current == wanted) { Status = Describe(); return; }

                var list = new System.Collections.Generic.List<string>(lines);
                if (wanted == null) { if (idx >= 0) list.RemoveAt(idx); }
                else if (idx >= 0) list[idx] = Key + "=" + wanted;
                else list.Insert(0, Key + "=" + wanted);
                File.WriteAllLines(path, list.ToArray());

                Main.Entry.Logger.Log(string.Format("boot.config {0}: {1} -> {2} (다음 실행부터 적용)", Key, current ?? "없음", wanted ?? "없음"));
                Status = Describe() + " | 다음 실행부터 적용됩니다";
            }
            catch (Exception ex)
            {
                Status = "boot.config 수정 실패: " + ex.Message;
                Main.Entry.Logger.Error(Status);
            }
        }

        // 지금 실제로 돌고 있는 방식. 실행 옵션이 boot.config 보다 우선한다.
        internal static string Describe()
        {
            return "지금 " + SystemInfo.graphicsDeviceType + " / " + SystemInfo.renderingThreadingMode;
        }
    }
}
