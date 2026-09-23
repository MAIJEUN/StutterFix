using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 개발자용: 효과 몰림 프레임의 장식 이동(ffxMoveDecorationsPlus.StartEffect)을 쪼개서 잰다.
    //
    // 1.3.7 기준 Arche 효과 몰림 프레임(개발자용 71ms)에서 장식 이동이 56ms, 그중 직접 처리 42,368번이 25.7ms였고
    // 나머지 약 24ms 는 어디에 쓰였는지 몰랐다. 이 파일은 그 나머지를 나눈다:
    //   태그 목록 훑기  : GetTaggedDecorations 가 돌려주는 LINQ(Where -> SelectMany -> Distinct)를 따로 한 번 끝까지 훑은 시간
    //   직접 처리       : 속성(키)별로 설정 함수 시간, 이미 같은 값이었던 수, 투명한 채로 남은 수
    //   다른 호출       : 배치 방식, 보이기, 깊이, 스프라이트, 마스크 등 효과가 장식마다 부르는 함수
    //   나머지          : 반복, 클로저 만들기, 형 검사 등 게임 코드 몸체
    // 시간 재기 자체도 비용이 있어서(한 번에 수십 ns) 직접 처리 쪽 숫자는 조금 부풀려진다.
    internal static class MoveProf
    {
        internal static bool Enabled = Edition.Dev;
        private static readonly double TickMs = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        internal static long TS() { return System.Diagnostics.Stopwatch.GetTimestamp(); }

        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, List<string>> tagsRef = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, List<string>>("targetTags");
        private static readonly AccessTools.FieldRef<ffxMoveDecorationsPlus, scrDecorationManager> mgrRef = AccessTools.FieldRefAccess<ffxMoveDecorationsPlus, scrDecorationManager>("decManager");
        private static readonly AccessTools.FieldRef<ffxPlusBase, float> durRef = AccessTools.FieldRefAccess<ffxPlusBase, float>("duration");
        private static MethodInfo getTagged;

        // 한 프레임 몫
        private class Frame
        {
            public int Effects, ZeroEffects, Decos, MaxDecos;
            public long Total, Enum, Sub, SubN;
            public long[] SetT = new long[16], HelpT = new long[16];
            public int[] SetN = new int[16], Same = new int[16], StayHidden = new int[16], Skip = new int[16];
            public double ZtMs;
            public void Clear()
            {
                Effects = ZeroEffects = Decos = MaxDecos = 0; Total = Enum = Sub = SubN = 0; ZtMs = 0;
                Array.Clear(SetT, 0, 16); Array.Clear(HelpT, 0, 16); Array.Clear(SetN, 0, 16); Array.Clear(Same, 0, 16); Array.Clear(StayHidden, 0, 16); Array.Clear(Skip, 0, 16);
            }
        }
        private static readonly Frame cur = new Frame();
        private static string worst = ""; private static double worstMs;
        private static int logged;

        internal static void Install(Harmony h)
        {
            if (!Enabled) return;
            try
            {
                MethodBase start = null;
                foreach (var m in typeof(ffxMoveDecorationsPlus).GetMethods(AccessTools.all))
                    if (m.Name == "StartEffect" && m.DeclaringType == typeof(ffxMoveDecorationsPlus) && !m.IsAbstract) start = m;
                foreach (var m in typeof(scrDecorationManager).GetMethods(AccessTools.all))
                {
                    if (m.Name != "GetTaggedDecorations" || m.IsGenericMethodDefinition) continue;
                    var p = m.GetParameters();
                    if (p.Length == 1 && p[0].ParameterType.IsAssignableFrom(typeof(List<string>)) && getTagged == null) getTagged = m;
                }
                h.Patch(start, prefix: new HarmonyMethod(typeof(MoveProf), nameof(Pre)) { priority = Priority.Last },
                    postfix: new HarmonyMethod(typeof(MoveProf), nameof(Post)) { priority = Priority.First });

                int subs = 0;
                var names = new HashSet<string> { "SetPlacementType", "SetVisible", "SetDepth", "SetSprite", "SetTextureScaleMultiplier",
                    "SetMaskingType", "SetMaskingTarget", "SetMaskingDepth" };
                foreach (var t in new[] { typeof(scrDecoration), typeof(scrVisualDecoration), typeof(scrParticleDecoration) })
                    foreach (var m in t.GetMethods(AccessTools.all))
                    {
                        if (m.DeclaringType != t || m.IsAbstract || !names.Contains(m.Name) || m.GetMethodBody() == null) continue;
                        try { h.Patch(m, prefix: new HarmonyMethod(typeof(MoveProf), nameof(SubPre)), postfix: new HarmonyMethod(typeof(MoveProf), nameof(SubPost))); subs++; }
                        catch { }
                    }
                Main.Entry.Logger.Log("[장식 이동 쪼개기] 설치 (다른 호출 " + subs + "개, 태그 목록 " + (getTagged != null ? getTagged.ToString() : "못 찾음") + ")");
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[장식 이동 쪼개기] 설치 실패: " + ex.Message); Enabled = false; }
        }

        private static bool inMove;
        private static long start;
        private static double ztStart;

        public static void Pre(ffxMoveDecorationsPlus __instance, bool __runOriginal)
        {
            if (!__runOriginal || inMove) return;
            cur.Effects++;
            if (durRef(__instance) <= 0f) cur.ZeroEffects++;
            // 태그 목록을 따로 한 번 끝까지 훑어 그 시간과 장식 수를 잰다 (원래 코드가 훑는 것과 같은 LINQ)
            if (getTagged != null)
            {
                try
                {
                    long e0 = TS();
                    var en = getTagged.Invoke(mgrRef(__instance), new object[] { tagsRef(__instance) }) as System.Collections.IEnumerable;
                    int n = 0;
                    if (en != null) foreach (var _ in en) n++;
                    cur.Enum += TS() - e0;
                    cur.Decos += n; if (n > cur.MaxDecos) cur.MaxDecos = n;
                }
                catch { }
            }
            inMove = true;
            ztStart = ZeroTween.FrameToMs + ZeroTween.FrameDoneMs;
            start = TS();
        }

        public static void Post(bool __runOriginal)
        {
            if (!inMove) return;
            cur.Total += TS() - start;
            cur.ZtMs += ZeroTween.FrameToMs + ZeroTween.FrameDoneMs - ztStart;
            inMove = false;
        }

        private static int subDepth; private static long subStart;
        public static void SubPre() { if (!inMove) return; if (subDepth++ == 0) subStart = TS(); }
        public static void SubPost() { if (!inMove || subDepth == 0) return; if (--subDepth == 0) { cur.Sub += TS() - subStart; cur.SubN++; } }

        // InstantMove 도우미가 부른다: 설정 함수 시간과 같은 값 여부
        internal static bool On { get { return Enabled && inMove; } }
        internal static void Setter(int key, long ticks, bool same, bool stayHidden)
        {
            cur.SetT[key] += ticks; cur.SetN[key]++;
            if (same) cur.Same[key]++;
            if (stayHidden) cur.StayHidden[key]++;
        }
        internal static void Helper(int key, long ticks) { cur.HelpT[key] += ticks; }
        internal static void Skipped(int key) { cur.Skip[key]++; }

        private static readonly int[] keys = { 1, 2, 12, 13, 3, 4, 5, 9, 10 };
        private static readonly string[] keyNames = { "위치X", "위치Y", "시차X", "시차Y", "피벗X", "피벗Y", "회전", "색", "불투명도" };

        // 매 프레임 끝 (Hitch 가 부른다)
        internal static void EndFrame()
        {
            if (!Enabled) return;
            double total = cur.Total * TickMs;
            if (total >= 15.0)
            {
                string s = Describe(total);
                if (total > worstMs) { worstMs = total; worst = s; }
                if (logged < 12) { logged++; Main.Entry.Logger.Log("[장식 이동 쪼개기] " + Time.frameCount + "프레임: " + s); }
            }
            cur.Clear();
        }

        private static string Describe(double total)
        {
            double help = 0, set = 0; int n = 0;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < keys.Length; i++)
            {
                int k = keys[i];
                if (cur.SetN[k] == 0 && cur.Skip[k] == 0) continue;
                double st = cur.SetT[k] * TickMs, ht = cur.HelpT[k] * TickMs;
                set += st; help += ht; n += cur.SetN[k] + cur.Skip[k];
                sb.AppendFormat(" {0} 건너뜀 {6}번, 부름 {1}번(같은 값 {2}, 투명 유지 {3}) 설정 {4:F1}ms 나머지 {5:F1}ms,", keyNames[i], cur.SetN[k], cur.Same[k], cur.StayHidden[k], st, ht - st, cur.Skip[k]);
            }
            double en = cur.Enum * TickMs, sub = cur.Sub * TickMs;
            double rest = total - help - sub - cur.ZtMs;
            return string.Format("효과 {0}개(길이 0 {1}개) 장식 {2}개(한 효과 최대 {3}) 총 {4:F1}ms | 직접 처리 {5}번 {6:F1}ms(그중 설정 함수 {7:F1}ms):{8} | 크기·시차배율 대역 {9:F1}ms | 다른 호출 {10}번 {11:F1}ms | 나머지(반복·클로저·태그 목록) {12:F1}ms | 태그 목록만 따로 훑기 {13:F1}ms",
                cur.Effects, cur.ZeroEffects, cur.Decos, cur.MaxDecos, total, n, help, set, sb.ToString().TrimEnd(','), cur.ZtMs, cur.SubN, sub, rest, en);
        }

        internal static string SongSummary()
        {
            if (!Enabled || worstMs <= 0) return "";
            string s = "[장식 이동 쪼개기] 곡에서 가장 무거운 프레임: " + worst;
            worst = ""; worstMs = 0; logged = 0;
            return s;
        }
    }
}
