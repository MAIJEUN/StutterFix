using System;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 꺼진(안 보이는) 파티클 장식의 매 프레임 Update 를 건너뛴다.
    //
    // scrParticleDecoration.Update 는 장식마다 매 프레임 ParticleSystem 의 shape.scale 과 main.simulationSpeed 를 다시 쓴다(IL 확인).
    // 안 보이게 할 때(SetVisible(false)) 게임은 particleSystem.gameObject 만 끄고 장식 컴포넌트는 그대로라, 꺼진 파티클도
    // 네이티브 설정 두 번과 conductor 조회를 매 프레임 계속 한다. 파티클 장식을 수백~수천 개 깔아 두는 맵에서 쌓인다.
    //
    // 꺼진 파티클은 시뮬레이션도 그리기도 안 하므로 그 사이의 두 값은 화면에 영향이 없다. 다시 켜지는 순간
    // (SetVisible(true) 뒤) Update 가 쓰는 것과 같은 값을 바로 한 번 써서, 켜진 뒤 첫 시뮬레이션부터 원래와 같게 한다.
    // 그대로 원래 코드로 두는 경우: 곡 시작 자동 재생 대기(atStart && autoPlay), 재생 중이 아님, 장면 정리 중.
    // 에디터 기즈모 줄(gizmo.SetActive)은 건너뛸 때도 그대로 한다.
    // "투명한 장식 그리지 않기"(InvisibleSkip) 설정을 따라 켜고 끈다.
    internal static class ParticleSkip
    {
        internal static long Skipped, Restored;
        private static readonly AccessTools.FieldRef<scrParticleDecoration, Vector2> scaleRef = AccessTools.FieldRefAccess<scrParticleDecoration, Vector2>("scale");

        internal static void Install(Harmony h)
        {
            try
            {
                var t = typeof(scrParticleDecoration);
                var upd = AccessTools.Method(t, "Update");
                var vis = AccessTools.Method(t, "SetVisible", new[] { typeof(bool) });
                if (upd == null || vis == null) { Main.Entry.Logger.Log("[꺼진 파티클] 함수를 못 찾아 끔"); return; }
                h.Patch(upd, prefix: new HarmonyMethod(typeof(ParticleSkip), nameof(UpdatePrefix)));
                h.Patch(vis, postfix: new HarmonyMethod(typeof(ParticleSkip), nameof(SetVisiblePost)));
                Main.Entry.Logger.Log("[꺼진 파티클] 설치");
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[꺼진 파티클] 설치 실패: " + ex.Message); }
        }

        private static bool On { get { return InvisibleSkip.Enabled && Hitch.Playing && !SceneReset.Resetting; } }
        // 곡 시작 직후(중간부터 시작할 때 ScrubToTime 으로 파티클을 Simulate 하는 구간)는 원래대로 둔다
        private static bool SkipOn { get { return On && !EffectBudget.InGrace; } }

        public static bool UpdatePrefix(scrParticleDecoration __instance)
        {
            if (!SkipOn) return true;
            if (__instance.atStart && __instance.autoPlay) return true;   // 곡 시작 자동 재생은 원래대로
            var ps = __instance.particleSystem;
            if ((object)ps == null || ps == null || ps.gameObject.activeSelf) return true;
            // 원래 Update 의 나머지: atStart 면 autoPlay 를 false 로 (여기서는 이미 false), 에디터 기즈모
            if (__instance.atStart) __instance.autoPlay = false;
            if (ADOBase.isLevelEditor && __instance.gizmo != null) __instance.gizmo.SetActive(ADOBase.isEditingLevel);
            Skipped++;
            return false;
        }

        public static void SetVisiblePost(scrParticleDecoration __instance, bool visible)
        {
            if (!visible || !On) return;
            try
            {
                var ps = __instance.particleSystem;
                if (ps == null) return;
                // 원래 Update 와 같은 두 줄
                var shape = ps.shape; shape.scale = scaleRef(__instance);
                var main = ps.main; main.simulationSpeed = __instance.simulationSpeed * ADOBase.conductor.song.pitch;
                Restored++;
            }
            catch { }
        }

        internal static string Summary()
        {
            return Skipped == 0 ? "" : " | 꺼진 파티클 갱신 건너뜀 " + Skipped + "번, 켜질 때 반영 " + Restored + "번";
        }
        internal static void ResetStats() { Skipped = Restored = 0; }
    }
}
