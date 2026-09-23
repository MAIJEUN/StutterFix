using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 끊긴 프레임에서 어느 게임 함수가 시간을 먹었는지 찍는다.
    //
    // 단계별 측정으로 범위가 좁혀졌다. 137.8초의 441ms 중 402ms가
    // Update/ScriptRunBehaviourUpdate, 즉 게임 스크립트의 Update 안이었다.
    // 두 판 연속 같은 지점이라 재현되는 현상이다. 이제 함수 이름까지 가면 된다.
    //
    // 시간 측정은 초당 수만 번 불리는 함수에서 측정 비용이 실제 비용을 덮는다는 걸 겪었지만,
    // 여기서는 400ms짜리 하나를 지목하는 것이 목적이라 그 정도 오차는 문제가 되지 않는다.
    // 대신 곡 내내 켜 두므로 호출당 비용을 최소로 한다. 문자열도 사전 검색도 최소한만.
    public static class SlowScan
    {
        private class Slot
        {
            public string Name;
            public long Ticks;
            public int Calls;
            // 곡 전체와 10초 구간별 누적. 끊긴 프레임이 아니라 평소 프레임에 매번 드는 비용을 보려고 둔다.
            public long SongTicks;
            public long SongCalls;
            public long[] Bucket;
        }

        private static readonly Dictionary<MethodBase, Slot> slots = new Dictionary<MethodBase, Slot>();
        private static readonly List<Slot> all = new List<Slot>();
        private static Harmony harmony;

        // 기본으로 꺼 둔다. 곡이 시작될 때 함수 134개를 감싸는 데 4초가 걸려서,
        // 그 자체가 맵 초반의 가장 큰 끊김이었다. 원인을 찾을 때만 켠다.
        internal static bool Enabled = Edition.Dev;   // 개발자용은 켜 둔다 (원인 추적용, 곡 시작이 몇 초 느려짐)
        internal static bool Installed;

        // 곡이 시작될 때 한 번만 감싼다. 맵마다 등장하는 컴포넌트가 다르다.
        internal static void InstallOnce()
        {
            if (Installed || !Enabled) return;
            Installed = true;
            try
            {
                var watch = Stopwatch.StartNew();
                harmony = new Harmony("StutterFix.SlowScan");

                var types = new HashSet<Type>();
                foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
                {
                    if (mb == null || !mb.isActiveAndEnabled) continue;
                    types.Add(mb.GetType());
                }

                int count = 0;
                foreach (var t in types)
                {
                    foreach (var name in new[] { "Update", "LateUpdate" })
                    {
                        try
                        {
                            var m = AccessTools.Method(t, name);
                            if (m == null || m.IsAbstract || m.ContainsGenericParameters) continue;
                            if (slots.ContainsKey(m)) continue;

                            var slot = new Slot { Name = m.DeclaringType.Name + "." + name, Bucket = new long[PerfOverlay.MaxBuckets] };
                            slots[m] = slot;
                            all.Add(slot);

                            harmony.Patch(m,
                                prefix: new HarmonyMethod(typeof(SlowScan), nameof(Pre)),
                                postfix: new HarmonyMethod(typeof(SlowScan), nameof(Post)));
                            count++;
                        }
                        catch { }
                    }
                }
                // 타일 하나하나를 건드리는 함수들. 효과 하나가 타일 2000~4700개를 칠하므로
                // 그 안에서 어디에 시간이 가는지 알아야 고칠 자리가 정해진다.
                // 호출 수가 많아 측정 비용이 섞이지만, 20ms짜리 안에서 셋 중 누가 큰지 가리는 데는 충분하다.
                foreach (var target in new[]
                {
                    new[] { "scrFloor", "ColorFloor" },
                    new[] { "scrFloor", "SetTrackStyle" },
                    new[] { "scrFloor", "UpdateAngle" },
                    new[] { "scrFloor", "SetColor" },
                    // 한 효과가 382ms를 쓰는데 타일 함수는 4815번뿐이었다. 타일 루프가 아니라는 뜻이다.
                    // 남은 후보는 애니메이션 정리다. DOTween 의 Kill 은 살아 있는 애니메이션 목록 전체를
                    // 훑기 때문에, 목록이 길어지면 한 번 부르는 데 드는 비용이 같이 커진다.
                    new[] { "TweenExtensions", "Kill" },
                    new[] { "DOTween", "Kill" },
                    new[] { "TweenManager", "FilteredOperation" },
                    new[] { "TweenManager", "Despawn" },
                    // 타일 함수 17ms + 애니메이션 정리 15ms = 32ms 뿐인데 효과 하나가 386ms였다.
                    // 남은 354ms는 타일당 73us. 타일마다 장식을 찾아 도는 코드가 유력하다.
                    new[] { "scrDecorationManager", "GetTaggedDecorations" },
                    new[] { "scrDecorationManager", "GetDecoration" },
                    new[] { "scrDecorationManager", "GetDecorationIndex" },
                    new[] { "scrDecorationManager", "UpdateDecorationTiling" },
                    new[] { "scrFloor", "SetSprite" },
                    new[] { "scrFloor", "UpdateTrackTexture" },
                    // IL을 끝까지 푸니 타일마다 DOTween.To(...).SetEase(...) 로 애니메이션을 하나씩 만든다.
                    // DOTween.To 는 제네릭이라 직접 감쌀 수 없지만, 만들어진 애니메이션은 전부
                    // TweenManager 를 거친다. 이 클래스 전체를 재서 어디로 가는지 본다.
                    new[] { "TweenManager", "*" },
                })
                {
                    var t = AccessTools.TypeByName(target[0]);
                    if (t == null) continue;
                    foreach (var m in t.GetMethods(AccessTools.all))
                    {
                        bool all_ = target[1] == "*";
                        if (!all_ && m.Name != target[1]) continue;
                        if (m.DeclaringType != t) continue;
                        if (all_ && (m.Name.StartsWith("get_") || m.Name.StartsWith("set_"))) continue;
                        if (m.IsAbstract || m.ContainsGenericParameters) continue;
                        if (slots.ContainsKey(m)) continue;
                        try
                        {
                            var slot = new Slot { Name = target[0] + "." + m.Name, Bucket = new long[PerfOverlay.MaxBuckets] };
                            slots[m] = slot;
                            all.Add(slot);
                            harmony.Patch(m,
                                prefix: new HarmonyMethod(typeof(SlowScan), nameof(Pre)),
                                postfix: new HarmonyMethod(typeof(SlowScan), nameof(Post)));
                            count++;
                        }
                        catch { }
                    }
                }

                // 무거운 구간(보이는 장식 수천 개가 동시에 움직임)에서 애니메이션 한 번 갱신이 어디에 쓰이는지 보려고,
                // 장식 이동 효과가 만든 콜백(람다)과 장식 쪽 함수들을 감싼다. DOTween 갱신 시간에서 이것들을 빼면 DOTween 자체 비용이다.
                var extra = new List<MethodInfo>();
                foreach (var nt in typeof(ffxMoveDecorationsPlus).GetNestedTypes(AccessTools.all))
                    foreach (var m in nt.GetMethods(AccessTools.all))
                        if (m.DeclaringType == nt && m.Name.Contains("<StartEffect>b__") && !m.IsAbstract && !m.ContainsGenericParameters) extra.Add(m);
                foreach (var n in new[] { "SetPosition", "SetScale", "SetRotation", "SetOpacity", "SetColor" })
                {
                    var m = AccessTools.Method(typeof(scrDecoration), n);
                    if (m != null) extra.Add(m);
                }
                var ac = AccessTools.Method(typeof(scrVisualDecoration), "ApplyColor");
                if (ac != null) extra.Add(ac);
                foreach (var m in extra)
                {
                    if (slots.ContainsKey(m)) continue;
                    try
                    {
                        string nm = m.DeclaringType.Name.StartsWith("<>c") ? "이동콜백" + m.Name.Substring(m.Name.IndexOf("b__")) + "(" + m.DeclaringType.Name.Replace("<>c__DisplayClass", "") + ")" : m.DeclaringType.Name + "." + m.Name;
                        var slot = new Slot { Name = nm, Bucket = new long[PerfOverlay.MaxBuckets] };
                        slots[m] = slot; all.Add(slot);
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(SlowScan), nameof(Pre)), postfix: new HarmonyMethod(typeof(SlowScan), nameof(Post)));
                        count++;
                    }
                    catch { }
                }
                Main.Entry.Logger.Log($"[느린함수] {count}개 감쌈 ({watch.ElapsedMilliseconds}ms)");
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("[느린함수] 설치 실패: " + ex.Message);
            }
        }

        public static void Pre(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void Post(MethodBase __originalMethod, long __state)
        {
            Slot s;
            if (!slots.TryGetValue(__originalMethod, out s)) return;
            long d = Stopwatch.GetTimestamp() - __state;
            s.Ticks += d;
            s.Calls++;
            int b = PerfOverlay.SongBucket;
            if (b >= 0) { s.SongTicks += d; s.SongCalls++; s.Bucket[b] += d; }
        }

        // 지난 프레임(정확히는 지난 측정 이후) 가장 오래 걸린 함수들.
        internal static string Top(int count)
        {
            if (!Installed) return "측정 안 함";
            var sb = new System.Text.StringBuilder();
            for (int n = 0; n < count; n++)
            {
                Slot best = null;
                for (int i = 0; i < all.Count; i++)
                {
                    var s = all[i];
                    if (s.Ticks <= 0) continue;
                    if (best != null && s.Ticks <= best.Ticks) continue;
                    if (sb.ToString().Contains(s.Name)) continue;
                    best = s;
                }
                if (best == null) break;
                double ms = best.Ticks * 1000.0 / Stopwatch.Frequency;
                if (ms < 1.0) break;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(best.Name).Append(' ').Append(ms.ToString("F0")).Append("ms(")
                  .Append(best.Calls).Append("회)");
            }
            return sb.Length > 0 ? sb.ToString() : "게임 함수들은 다 짧음";
        }

        internal static void Reset()
        {
            for (int i = 0; i < all.Count; i++) { all[i].Ticks = 0; all[i].Calls = 0; }
        }

        internal static void ResetSong()
        {
            for (int i = 0; i < all.Count; i++) { all[i].SongTicks = 0; all[i].SongCalls = 0; System.Array.Clear(all[i].Bucket, 0, all[i].Bucket.Length); }
        }

        // 곡이 끝나면 "평소 프레임 하나에 어느 함수가 얼마나 드나" 를 남긴다. 곡 전체 평균과, 가장 가벼운 10초 구간.
        // 감싼 함수끼리 서로를 부르면(DOTween 갱신 안의 TweenManager 등) 시간이 겹쳐 잡힌다. 순위를 보는 용도다.
        internal static void ReportSong()
        {
            if (!Installed || all.Count == 0) return;
            int frames = PerfOverlay.SongFrameCount;
            if (frames < 30) return;
            Main.Entry.Logger.Log("[프레임 비용] 곡 평균, 프레임당: " + Rank(s => s.SongTicks, frames, s => s.SongCalls, 15));
            int best, bestFrames;
            if (PerfOverlay.BestBucket(out best, out bestFrames))
                Main.Entry.Logger.Log("[프레임 비용] 가장 가벼운 구간 " + best * 10 + "초, 프레임당: " + Rank(s => s.Bucket[best], bestFrames, null, 12));
            int worst, worstFrames;
            if (PerfOverlay.WorstBucket(out worst, out worstFrames))
                Main.Entry.Logger.Log("[프레임 비용] 가장 무거운 구간 " + worst * 10 + "초, 프레임당: " + Rank(s => s.Bucket[worst], worstFrames, null, 25));
        }

        private static string Rank(Func<Slot, long> ticks, int frames, Func<Slot, long> calls, int count)
        {
            var list = new List<Slot>(all);
            list.Sort((a, b) => ticks(b).CompareTo(ticks(a)));
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < list.Count && i < count; i++)
            {
                double ms = ticks(list[i]) * 1000.0 / Stopwatch.Frequency / frames;
                if (ms < 0.02) break;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(list[i].Name).Append(' ').Append(ms.ToString("F2")).Append("ms");
                if (calls != null) sb.Append(" (").Append((calls(list[i]) / (double)frames).ToString("F1")).Append("회)");
            }
            return sb.Length > 0 ? sb.ToString() : "없음";
        }

        // 패치는 Main 이 ID로 한꺼번에 푼다. 여기서는 "이미 감쌌음" 기록만 지워 다시 켤 때 새로 감싸게 한다.
        internal static void Shutdown()
        {
            slots.Clear();
            all.Clear();
            Installed = false;
        }
    }
}
