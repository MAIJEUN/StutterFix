using System;
using System.Diagnostics;
using UnityEngine;

namespace StutterFix
{
    // 곡 시작 전에 메모리에 올라온 셰이더를 미리 준비시킨다.
    //
    // 효과 나누기와 색 나누기 뒤에 남은 곡 중 끊김 두 곳은 필터가 바뀌는 순간이었다.
    //   137.1초: Glow_Color 가 켜진 그 프레임, 게임 코드 밖(FinishFrameRendering) 22ms, 다음 프레임 GPU 41ms
    //   165.6초: Chromatical2 가 꺼진 직후, 게임 루프 밖에서 45ms, 이어서 GPU 47ms
    // 게임 코드는 짧고 그래픽 드라이버 쪽에서 멈췄다. 필터 셰이더를 처음 그릴 때 드라이버가
    // 셰이더를 만드는 비용일 수 있으므로, 맵이 올라온 뒤 곡 시작 시점에 한꺼번에 미리 데운다.
    // 화면에 그리는 것은 없고, 곡 시작 멈춤(맵 로딩 3.5초)에 조금 더해질 뿐이다.
    // 새 셰이더가 올라왔을 때만 다시 한다(같은 맵을 다시 시작할 때는 건너뜀).
    public static class ShaderWarm
    {
        internal static bool Enabled = true;
        internal static string Last = "아직 안 함";
        private static int lastCount = -1;

        internal static void MaybeRun()
        {
            if (!Enabled) return;
            try
            {
                int count = Resources.FindObjectsOfTypeAll<Shader>().Length;
                if (count == lastCount) return;
                lastCount = count;

                long t0 = Stopwatch.GetTimestamp();
                Shader.WarmupAllShaders();
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                ModCost.Add(SettingsWindow.T("셰이더 준비", "Shader warm-up"), ms);
                Last = "셰이더 " + count + "개 미리 준비 " + ms.ToString("F0") + "ms";
                Main.Entry.Logger.Log("[셰이더] " + Last);
            }
            catch (Exception ex) { Main.Entry.Logger.Error("[셰이더] 미리 준비 실패: " + ex.Message); }
        }
    }
}
