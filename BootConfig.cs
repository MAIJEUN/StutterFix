using System;
using System.Collections.Generic;
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
    // 유니티는 boot.config 의 줄을 실행 옵션처럼 읽는다(wait-for-native-debugger 같은 키가 실행 옵션과 같은 이름이다).
    // 그래서 `-force-gfx-jobs legacy` 를 `force-gfx-jobs=legacy` 한 줄로 넣는다.
    //
    // 처음에는 gfx-threading-mode 를 3(ClientWorkerJobs)으로 바꿨는데 엔진이 되돌림 메시지도 없이
    // Threaded 로 떴다. 그 키는 그래픽 작업이 따로 켜져 있을 때만 쓰이는 것으로 보인다. 그 값은 원래대로 둔다.
    //
    // 유니티는 시작할 때만 이 파일을 읽으므로 바꾼 뒤 한 번 재시작해야 적용된다.
    // 원래 파일은 백업해 두고, 설정에서 끄거나 모드를 끄면 원래 내용으로 돌려놓는다.
    // 게임 업데이트나 Steam 파일 검사로 되돌아가도 다음 실행 때 다시 적용한다.
    public static class BootConfig
    {
        private const string JobsKey = "force-gfx-jobs";
        private const string JobsValue = "legacy";
        private const string ModeKey = "gfx-threading-mode";

        internal static string Status = "";

        private static string ConfigPath { get { return Path.Combine(Application.dataPath, "boot.config"); } }
        private static string BackupPath { get { return ConfigPath + ".stutterfix-backup"; } }

        private static int Find(List<string> lines, string key)
        {
            return lines.FindIndex(l => l.StartsWith(key + "=", StringComparison.Ordinal));
        }

        private static string Value(List<string> lines, string key)
        {
            int i = Find(lines, key);
            return i >= 0 ? lines[i].Substring(key.Length + 1).Trim() : null;
        }

        private static bool Set(List<string> lines, string key, string value)
        {
            int i = Find(lines, key);
            string cur = i >= 0 ? lines[i].Substring(key.Length + 1).Trim() : null;
            if (cur == value) return false;
            if (value == null) lines.RemoveAt(i);
            else if (i >= 0) lines[i] = key + "=" + value;
            else lines.Insert(0, key + "=" + value);
            return true;
        }

        internal static void Apply(bool enable)
        {
            try
            {
                string path = ConfigPath;
                if (!File.Exists(path)) { Status = "boot.config 없음"; return; }

                var lines = new List<string>(File.ReadAllLines(path));
                if (enable && !File.Exists(BackupPath)) File.Copy(path, BackupPath);

                // 원래 값은 백업에서 가져온다. 백업이 없으면 바꾼 적이 없다는 뜻이다.
                string originalMode = null;
                if (File.Exists(BackupPath))
                    originalMode = Value(new List<string>(File.ReadAllLines(BackupPath)), ModeKey);

                bool changed = false;
                changed |= Set(lines, JobsKey, enable ? JobsValue : null);
                if (originalMode != null) changed |= Set(lines, ModeKey, originalMode);   // 예전 버전이 3으로 바꿔 둔 것을 되돌린다

                if (changed)
                {
                    File.WriteAllLines(path, lines.ToArray());
                    Main.Entry.Logger.Log("boot.config 수정: " + (enable ? JobsKey + "=" + JobsValue + " 추가" : JobsKey + " 제거") + " (다음 실행부터 적용)");
                    Status = Describe() + " | 다음 실행부터 적용됩니다";
                }
                else Status = Describe();
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
