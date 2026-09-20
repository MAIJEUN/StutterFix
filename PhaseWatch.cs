using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.LowLevel;

namespace StutterFix
{
    // 프레임이 끊긴 그 순간, 어느 단계가 시간을 잡아먹었는지 알려준다.
    //
    // 유니티는 매 프레임 내부 단계(입력 -> 업데이트 -> 애니메이션 -> 렌더링)를 순서대로 돈다.
    // 그 사이사이에 표시를 심어 두고, 각 단계가 걸린 시간을 프레임 단위로 들고 있는다.
    // 끊긴 프레임이 나오면 직전 프레임의 기록을 그대로 꺼내 쓴다.
    //
    // 매 프레임 도는 코드라 문자열이나 목록을 만들지 않는다.
    // 이름은 설치할 때 한 번만 만들고, 시간은 숫자 배열에만 더한다.
    public static class PhaseWatch
    {
        private struct Marker { public int Index; }

        private static string[] names = new string[0];
        private static long[] ticks = new long[0];
        private static long[] lastFrame = new long[0];

        private static PlayerLoopSystem original;
        private static bool installed;
        private static long lastStamp;
        private static int lastIndex = -1;
        private static int lastRecordFrame;

        internal static bool Installed { get { return installed; } }

        internal static void Install()
        {
            if (installed) return;
            try
            {
                original = PlayerLoop.GetCurrentPlayerLoop();
                var root = PlayerLoop.GetCurrentPlayerLoop();

                var nameList = new List<string>();
                var newTop = new List<PlayerLoopSystem>();

                foreach (var top in root.subSystemList)
                {
                    string topName = top.type != null ? top.type.Name : "?";
                    var children = new List<PlayerLoopSystem>();

                    if (top.subSystemList != null)
                    {
                        foreach (var child in top.subSystemList)
                        {
                            string name = topName + "/" + (child.type != null ? child.type.Name : "?");
                            children.Add(MakeMarker(nameList.Count));
                            nameList.Add(name);
                            children.Add(child);
                        }
                    }
                    children.Add(MakeMarker(nameList.Count));
                    nameList.Add(topName + "/end");

                    var copy = top;
                    copy.subSystemList = children.ToArray();
                    newTop.Add(copy);
                }

                root.subSystemList = newTop.ToArray();
                PlayerLoop.SetPlayerLoop(root);

                names = nameList.ToArray();
                ticks = new long[names.Length];
                lastFrame = new long[names.Length];
                lastStamp = Stopwatch.GetTimestamp();
                lastIndex = -1;
                installed = true;
                Main.Entry.Logger.Log("phase watch installed (" + names.Length + " markers)");
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("phase watch 설치 실패: " + ex.Message);
            }
        }

        internal static void Uninstall()
        {
            if (!installed) return;
            try { PlayerLoop.SetPlayerLoop(original); } catch { }
            installed = false;
        }

        private static PlayerLoopSystem MakeMarker(int index)
        {
            return new PlayerLoopSystem
            {
                type = typeof(Marker),
                updateDelegate = () => Record(index)
            };
        }

        private static void Record(int index)
        {
            long now = Stopwatch.GetTimestamp();

            // 첫 표시가 다시 돌아왔으면 한 프레임이 끝난 것이다. 직전 프레임 기록으로 옮겨 둔다.
            if (index == 0)
            {
                Array.Copy(ticks, lastFrame, ticks.Length);
                Array.Clear(ticks, 0, ticks.Length);
                lastRecordFrame = Time.frameCount;
            }
            else if (lastIndex >= 0)
            {
                ticks[lastIndex] += now - lastStamp;
            }

            lastStamp = now;
            lastIndex = index;
        }

        // 직전 프레임에서 가장 오래 걸린 단계들. 끊겼을 때만 부르므로 여기서는 문자열을 만들어도 된다.
        internal static string TopOfLastFrame(int count)
        {
            if (!installed) return "측정 안 함";
            // 다른 모드가 나중에 루프를 통째로 바꾸면 우리 표시가 지워진다. 옛날 값을 진짜처럼 읽지 않도록 확인한다.
            if (Time.frameCount - lastRecordFrame > 2) return "표시가 지워짐 (다른 모드가 루프를 바꿈)";

            var sb = new System.Text.StringBuilder();
            for (int n = 0; n < count; n++)
            {
                int best = -1;
                long bestTicks = 0;
                for (int i = 0; i < lastFrame.Length; i++)
                {
                    if (lastFrame[i] <= bestTicks) continue;
                    if (sb.ToString().Contains(names[i])) continue;   // 이미 넣은 것은 건너뛴다
                    bestTicks = lastFrame[i];
                    best = i;
                }
                if (best < 0) break;

                double ms = bestTicks * 1000.0 / Stopwatch.Frequency;
                if (ms < 1.0) break;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(names[best]).Append(' ').Append(ms.ToString("F0")).Append("ms");
            }
            return sb.Length > 0 ? sb.ToString() : "단계별로는 다 짧음 (엔진 바깥이나 드라이버 쪽)";
        }
    }
}
