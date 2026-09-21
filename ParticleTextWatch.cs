using System;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 박자마다 오는 75ms의 남은 후보 둘을 잰다.
    //
    // 지금까지 지운 것: 스크립트(그리기 콜백 394개 전부 짧음), 할당(0MB), GPU(6~7ms),
    // 필터(다 꺼도 그대로), 커스텀 프레임레이트 연출(7초에 한 번 1.8ms뿐).
    // 남은 것은 Camera.Render 안에서 메인 스레드가 엔진 작업을 기다리는 경우다.
    //
    //   파티클   : 박자마다 한꺼번에 터지면, 메인 스레드가 그리기 직전에 파티클 계산이 끝나길 기다린다.
    //   글자 장식 : 박자마다 글자가 바뀌면 캔버스를 통째로 다시 묶는데, 그 대기도 그리기 안에서 일어난다.
    public static class ParticleTextWatch
    {
        private static ParticleSystem[] systems = new ParticleSystem[0];
        private static ParticleSystemRenderer[] renderers = new ParticleSystemRenderer[0];

        internal static int SetTextThisFrame;
        private static int setTextCounter;

        internal static bool ForceParticlesOff;

        internal static void Install(Harmony harmony)
        {
            try
            {
                var t = AccessTools.TypeByName("scrTextDecoration");
                if (t == null) return;
                foreach (var m in t.GetMethods(AccessTools.all))
                {
                    if (m.Name != "SetText" || m.DeclaringType != t || m.IsAbstract) continue;
                    try { harmony.Patch(m, prefix: new HarmonyMethod(typeof(ParticleTextWatch), nameof(CountSetText))); }
                    catch { }
                }
                Main.Entry.Logger.Log("particle/text watch installed");
            }
            catch (Exception ex) { Main.Entry.Logger.Error("particle/text watch 실패: " + ex.Message); }
        }

        public static void CountSetText() { setTextCounter++; }

        // 파티클 목록은 곡이 시작될 때 한 번만 만든다. 매 프레임 찾으면 그 자체가 끊김이 된다.
        internal static void Refresh()
        {
            try
            {
                systems = UnityEngine.Object.FindObjectsByType<ParticleSystem>(FindObjectsSortMode.None);
                renderers = new ParticleSystemRenderer[systems.Length];
                for (int i = 0; i < systems.Length; i++)
                    renderers[i] = systems[i] != null ? systems[i].GetComponent<ParticleSystemRenderer>() : null;
                Main.Entry.Logger.Log("[파티클] 이 맵의 파티클 시스템 " + systems.Length + "개");
            }
            catch (Exception ex) { Main.Entry.Logger.Error("[파티클] 목록 실패: " + ex.Message); }
        }

        // 실험 스위치로 꺼 둔 것은 내려갈 때 되살린다.
        internal static void Shutdown()
        {
            if (!ForceParticlesOff) return;
            foreach (var r in renderers) if (r != null) r.enabled = true;
        }

        internal static void Tick()
        {
            SetTextThisFrame = setTextCounter;
            setTextCounter = 0;

            if (!ForceParticlesOff) return;
            for (int i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r != null && r.enabled) r.enabled = false;
            }
        }

        // 끊긴 프레임에서만 부른다.
        internal static string Info()
        {
            int playing = 0, particles = 0, max = 0;
            for (int i = 0; i < systems.Length; i++)
            {
                var s = systems[i];
                if (s == null || !s.isPlaying) continue;
                playing++;
                int n = s.particleCount;
                particles += n;
                if (n > max) max = n;
            }
            return string.Format("파티클 {0}개 재생중, 입자 {1}개(최대 한 곳 {2}) | 글자 바뀜 {3}회",
                playing, particles, max, setTextCounter);
        }
    }
}
