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
                // 모드를 다 꺼도 박자 끊김은 남고 오히려 커졌다(75ms -> 115ms). 효과를 줄여 주는 모드가
                // 덜어 주던 것이 원인이라는 뜻이다. 박자마다 장식을 수백 개씩 켜고 끄면, 유니티는 다음
                // 그리기에서 그려야 할 목록을 다시 짠다. 장식이 켜지고 꺼지는 횟수를 센다.
                var deco = AccessTools.TypeByName("scrDecoration");
                if (deco != null)
                {
                    foreach (var dt in deco.Assembly.GetTypes())
                    {
                        if (!deco.IsAssignableFrom(dt)) continue;
                        foreach (var m in dt.GetMethods(AccessTools.all))
                        {
                            if (m.Name != "SetVisible" || m.DeclaringType != dt || m.IsAbstract) continue;
                            try { harmony.Patch(m, prefix: new HarmonyMethod(typeof(ParticleTextWatch), nameof(CountVisible))); }
                            catch { }
                        }
                    }
                }

                Font.textureRebuilt += OnFontRebuilt;
                Main.Entry.Logger.Log("particle/text watch installed");
            }
            catch (Exception ex) { Main.Entry.Logger.Error("particle/text watch 실패: " + ex.Message); }
        }

        // 학교 PC에서는 TextGenerator 폭주가 없었다. 이 PC에만 있는 무언가가 글자를 매 프레임
        // 34번씩 다시 넣는다는 뜻이다. 다른 모드가 게임 함수 안에 끼어들어(Harmony) 부르면
        // 모드별 갱신 측정에는 안 잡히므로, 누가 부르는지 호출 경로를 직접 남긴다.
        private static int stackSamples;
        private static int lastSampleFrame = -1000;

        public static void CountSetText()
        {
            setTextCounter++;
            if (stackSamples >= 3 || !GcControl.Paused) return;   // 곡 중에만, 판마다 3번
            if (Time.frameCount - lastSampleFrame < 300) return;  // 서로 다른 순간에서 뽑는다
            lastSampleFrame = Time.frameCount;
            stackSamples++;

            var lines = Environment.StackTrace.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var sb = new System.Text.StringBuilder();
            int shown = 0;
            foreach (var l in lines)
            {
                string s = l.Trim();
                if (s.IndexOf("System.Environment", StringComparison.Ordinal) >= 0) continue;
                if (s.IndexOf("ParticleTextWatch", StringComparison.Ordinal) >= 0) continue;
                sb.Append("\n      ").Append(s);
                if (++shown >= 12) break;
            }
            Main.Entry.Logger.Log("[글자 호출경로 " + stackSamples + "/3]" + sb);
        }

        // 같은 글자를 다시 넣는 것은 싸지만, 처음 보는 글자나 크기가 들어오면 유니티가
        // 폰트 텍스처를 통째로 다시 만든다. 엔진 내부 작업이라 스크립트 측정에는 안 잡히고,
        // 메인 스레드에서 그리기 직전에 일어나며 GPU는 거의 안 쓴다. 지금까지의 단서와 모두 맞는다.
        private static int visibleCounter;
        public static void CountVisible() { visibleCounter++; }

        private static int fontRebuilds;
        private static string lastFont = "";

        private static void OnFontRebuilt(Font f)
        {
            fontRebuilds++;
            if (f != null) lastFont = f.name;
        }

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
                stackSamples = 0;
            }
            catch (Exception ex) { Main.Entry.Logger.Error("[파티클] 목록 실패: " + ex.Message); }
        }

        internal static void Shutdown()
        {
            Font.textureRebuilt -= OnFontRebuilt;
        }

        internal static void Tick()
        {
            SetTextThisFrame = setTextCounter;
            setTextCounter = 0;
            fontRebuilds = 0;
            visibleCounter = 0;
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
            return string.Format("파티클 {0}개 재생중, 입자 {1}개(최대 한 곳 {2}) | 글자 바뀜 {3}회 | 폰트 재생성 {4}회{5} | 장식 켜기/끄기 {6}회",
                playing, particles, max, setTextCounter, fontRebuilds, fontRebuilds > 0 ? " (" + lastFont + ")" : "", visibleCounter);
        }
    }
}
