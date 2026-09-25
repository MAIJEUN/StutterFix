using System;
using HarmonyLib;

namespace StutterFix
{
    // 곡 중간에 에디터로 나가거나 다시 시작할 때(scnGame.ResetScene) 모드가 들고 있던 곡 중 상태를 정리한다.
    //
    // 게임은 ResetScene 에서 DOTween.PlayingTweens 로 받은 애니메이션만 Kill() 하고 장식을 Setup 으로 되돌린다.
    // 모드가 게임 밖에 들고 있는 것은 이 정리에 안 잡혀서, 되돌린 뒤에도 곡 중 값을 다시 넣었다
    // (에디터에 장식이 남고, 다음 플레이에도 그대로 나옴).
    //   - 장식 애니메이션 직접 처리(DecoAnim): 진행 중인 표가 계속 돌았다
    //   - 효과 나누기(EffectBudget)·타일 색 나누기(RecolorSplit): 밀린 효과가 에디터 프레임에서 실행됐다
    //   - 투명 장식 위치 미루기(InvisibleSkip): 곡 종료 감지(Hitch)는 한 프레임 늦어서, ResetScene 안의 SetPosition 이 도로 미뤄졌다
    // Hitch 의 곡 종료 처리와 달리 이 자리는 게임이 정리하는 바로 그 순간이라 순서가 어긋나지 않는다.
    internal static class SceneReset
    {
        internal static bool Resetting;
        internal static long Count;

        internal static void Install(Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(scnGame), "ResetScene");
                if (m == null) { Main.Entry.Logger.Log("[장면 정리] scnGame.ResetScene 을 못 찾음"); return; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(SceneReset), nameof(Prefix)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(SceneReset), nameof(Finalizer)));
                Main.Entry.Logger.Log("[장면 정리] 설치");
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[장면 정리] 설치 실패: " + ex.Message); }
        }

        public static void Prefix()
        {
            Count++;
            // 게임의 Kill() 처럼 진행 중인 것은 그 자리에서 버린다 (뒤이어 게임이 Setup 으로 되돌린다)
            try { DecoAnim.DropAll(); } catch (Exception ex) { Main.Entry.Logger.Log("[장면 정리] 장식 애니메이션: " + ex.Message); }
            try { EffectBudget.Reset(); } catch (Exception ex) { Main.Entry.Logger.Log("[장면 정리] 효과 나누기: " + ex.Message); }
            // 미뤄 둔 위치를 먼저 반영해 목록을 비우고, ResetScene 동안은 미루지 않는다 (되돌린 위치가 곧바로 엔진에 들어가게)
            try { InvisibleSkip.ApplyAllLazy(); } catch (Exception ex) { Main.Entry.Logger.Log("[장면 정리] 투명 장식: " + ex.Message); }
            // 미리 확인해 둔 계획은 장식을 되돌리면 의미가 없다. 되돌리는 동안에는 위치 설정 알림(LazyPrefix)도 쉬므로 먼저 버린다.
            try { Precheck.ResetAll(); } catch (Exception ex) { Main.Entry.Logger.Log("[장면 정리] 미리 확인: " + ex.Message); }
            Resetting = true;
        }

        public static Exception Finalizer(Exception __exception)
        {
            Resetting = false;
            return __exception;
        }
    }
}
