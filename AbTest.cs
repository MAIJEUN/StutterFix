using System;
using DG.Tweening;
using UnityEngine;

namespace StutterFix
{
    // 무거운 구간이 짧게 지나가서 손으로 비교하기 어려울 때 쓰는 자동 A/B 테스트.
    // 일정 시간마다 애니메이션(tween)을 멈췄다 재생하면서 각 상태의 FPS를 따로 모은다.
    // 두 상태의 FPS 차이가 크면 애니메이션 처리가 병목이라는 뜻이다.
    public static class AbTest
    {
        internal static bool Running;
        internal static float PhaseSeconds = 0.8f;
        internal static string Summary = "(테스트 안 함)";

        private static bool paused;
        private static float phaseElapsed;
        private static int phaseFrames;
        private static float phaseWorst;   // 이번 구간에서 가장 느렸던 프레임(ms)

        private static float pausedTime, playingTime;
        private static int pausedFrames, playingFrames;

        internal static void Toggle()
        {
            Running = !Running;
            if (Running)
            {
                pausedTime = playingTime = 0f;
                pausedFrames = playingFrames = 0;
                phaseElapsed = 0f;
                phaseFrames = 0;
                paused = false;
                SafePlay();
                Main.Entry.Logger.Log("[ab] started");
            }
            else
            {
                SafePlay();
                Main.Entry.Logger.Log("[ab] stopped - " + Summary);
            }
        }

        internal static void Tick(float dt)
        {
            if (!Running) return;

            // PauseAll은 호출 시점에 존재하는 tween만 멈춘다. 맵은 매 프레임 새 tween을 만들어내므로
            // 멈춤 구간 내내 계속 불러줘야 실제로 애니메이션이 멎은 상태가 된다.
            if (paused) SafePause();

            phaseElapsed += dt;
            phaseFrames++;

            if (dt * 1000f > phaseWorst) phaseWorst = dt * 1000f;

            if (paused) { pausedTime += dt; pausedFrames++; }
            else { playingTime += dt; playingFrames++; }

            if (phaseElapsed < PhaseSeconds) return;

            float fps = phaseFrames / phaseElapsed;
            int tweens = 0;
            try { tweens = DOTween.TotalPlayingTweens(); } catch { }
            Main.Entry.Logger.Log($"[ab] {(paused ? "멈춤" : "재생")} 구간: {fps:F0} fps, 최악 {phaseWorst:F1}ms ({phaseFrames}프레임 / {phaseElapsed:F1}초), 재생중 tween {tweens}개");

            phaseElapsed = 0f;
            phaseFrames = 0;
            phaseWorst = 0f;
            paused = !paused;
            if (paused) SafePause(); else SafePlay();

            float pf = pausedTime > 0 ? pausedFrames / pausedTime : 0;
            float yf = playingTime > 0 ? playingFrames / playingTime : 0;
            Summary = $"멈춤 {pf:F0} fps / 재생 {yf:F0} fps";
        }

        private static void SafePause() { try { DOTween.PauseAll(); } catch { } }
        private static void SafePlay() { try { DOTween.PlayAll(); } catch { } }
    }
}
