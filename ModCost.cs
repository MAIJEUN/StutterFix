namespace StutterFix
{
    // 이 모드 자신이 한 프레임에 쓴 시간을 모은다. 실시간 모니터가 "모드 때문에 끊긴 것"을 따로 가릴 때 쓴다.
    //
    // 모드가 프레임 안에서 직접 시간을 쓰는 곳:
    //   밀린 효과 실행       (EffectBudget.Drain, 원래 게임 일을 뒤 프레임으로 옮긴 것)
    //   타일 색 나눠 칠하기   (RecolorSplit.Run)
    //   메모리 정리          (GcControl.Resume, 곡이 끝난 뒤 한 번에 치울 때)
    //   셰이더 미리 준비      (ShaderWarm)
    //   모니터 그리기        (PerfOverlay)
    // 값 몇 개를 더하기만 하므로 비용은 없다. 프레임이 바뀔 때 Hitch.Tick 이 ResetFrame 을 부른다.
    internal static class ModCost
    {
        internal static double FrameMs, LastFrameMs;
        internal static string Top = "", LastTop = "";
        private static double topMs, lastTopMs;
        internal static double LastTopMs { get { return lastTopMs; } }
        internal static double TopMs { get { return topMs; } }

        internal static void Add(string what, double ms)
        {
            FrameMs += ms;
            if (ms > topMs) { topMs = ms; Top = what; }
        }

        internal static void ResetFrame()
        {
            LastFrameMs = FrameMs; LastTop = Top; lastTopMs = topMs;
            FrameMs = 0; Top = ""; topMs = 0;
        }
    }
}
