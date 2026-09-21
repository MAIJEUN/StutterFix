using System;
using System.Collections.Generic;
using UnityEngine.Profiling;

namespace StutterFix
{
    // 그리기 안에서 CPU가 무엇에 시간을 쓰는지 유니티 내부 계측으로 본다.
    //
    // PresentMon으로 확인한 사실: 박자마다 오는 75ms 프레임에서 GPU는 6~7ms만 일했다.
    // 막히는 것은 CPU이고, 위치는 Camera.Render 안(69ms)이다. 컬링은 0.2ms였다.
    // Camera.Render 안에는 정렬, 묶어 그리기(배칭), 글자/캔버스 처리, 스크립트 콜백이 있다.
    // 유니티는 이 구간마다 이름 붙은 계측점을 갖고 있으므로, 그 값을 직접 읽는다.
    public static class SamplerWatch
    {
        private static readonly List<Recorder> recorders = new List<Recorder>();
        private static readonly List<string> names = new List<string>();
        internal static bool Available;

        private static readonly string[] Keywords =
        {
            "Render", "Batch", "Canvas", "Sprite", "Text", "Mesh", "Draw", "Culling",
            "Sort", "Blit", "Image", "Shader", "SetPass", "Gfx", "Material", "Transparent",
        };

        internal static void Shutdown()
        {
            foreach (var r in recorders) { try { r.enabled = false; } catch { } }
            recorders.Clear();
            names.Clear();
            Available = false;
        }

        internal static void Install()
        {
            try
            {
                var all = new List<string>();
                Sampler.GetNames(all);

                foreach (var n in all)
                {
                    bool match = false;
                    foreach (var k in Keywords)
                        if (n.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) { match = true; break; }
                    if (!match) continue;

                    var r = Recorder.Get(n);
                    if (r == null || !r.isValid) continue;
                    r.enabled = true;
                    recorders.Add(r);
                    names.Add(n);
                }

                Available = recorders.Count > 0;
                Main.Entry.Logger.Log($"[계측점] 전체 {all.Count}개 중 {recorders.Count}개 켬");
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("[계측점] 설치 실패: " + ex.Message);
            }
        }

        // 직전 프레임에서 오래 걸린 내부 구간들. 끊겼을 때만 부르므로 문자열을 만들어도 된다.
        internal static string Top(int count)
        {
            if (!Available) return "계측점 없음 (이 빌드에서는 막혀 있음)";

            var used = new bool[recorders.Count];
            var sb = new System.Text.StringBuilder();
            for (int n = 0; n < count; n++)
            {
                int best = -1;
                long bestNs = 0;
                for (int i = 0; i < recorders.Count; i++)
                {
                    if (used[i]) continue;
                    long ns = recorders[i].elapsedNanoseconds;
                    if (ns <= bestNs) continue;
                    bestNs = ns;
                    best = i;
                }
                if (best < 0 || bestNs < 1000000) break;   // 1ms 미만은 생략
                used[best] = true;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(names[best]).Append(' ').Append((bestNs / 1000000.0).ToString("F1"))
                  .Append("ms(").Append(recorders[best].sampleBlockCount).Append("회)");
            }
            return sb.Length > 0 ? sb.ToString() : "내부 구간은 다 짧음";
        }
    }
}
