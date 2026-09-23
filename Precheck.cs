using System;
using System.Collections.Generic;
using DG.Tweening;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 미리 확인: 곧 발동할 무거운 장식 이동 효과가 "아무것도 안 바꾸는지" 를 몇 초 앞서 여유 있는 프레임에 나눠 확인해 두고,
    // 발동하는 순간까지 대상이 하나도 안 바뀌었으면 효과를 통째로 건너뛴다. 결과는 원래와 똑같다.
    //
    // 1.3.9 측정(플레이어용, Arche): 효과가 가장 무거운 프레임 25ms 중 장식 이동 15ms. 대부분 장식 1만 4천 개 효과 하나이고
    // 그 장식들은 거의 다 이미 투명하고 넣을 값도 이미 가진 값이었다. 장식마다 확인하는 것 자체가 메모리 읽기라 줄지 않았다.
    // 효과는 한 판에 한 번씩만 발동하므로(ffxPlusBase.triggered) 지난번 결과를 재활용할 수는 없고, 미리 확인해야 한다.
    //
    // 맡는 효과: 장식 이동 루프(FastMove)가 맡는 길이 0 효과 중 위치·색·불투명도만 바꾸고(상대 이동 아님), 태그 하나, 대상 200개 이상.
    // 장식 하나가 "그대로" 인 조건 (FastMove 루프가 그 장식에 아무것도 안 바꾸는 조건과 같다):
    //   일반 이미지 장식, 타일에 붙지 않음, 이번 효과의 애니메이션 키가 모두 "끝난 대역", 안 그리는 중
    //   위치: 시차 부품이 없거나(원래 함수가 아무것도 안 함), 투명 장식 위치 미루기 대상이면서 이미 미루기 목록에 있고 저장된 값이 넣을 값과 같음
    //   색·불투명도: 넣을 값이 지금 값과 같고 다시 계산한 그리기 색도 같음 (InstantMove.ColorNoop)
    // 확인한 장식에는 지켜보기 표시를 붙인다. 표시가 붙은 장식을 바꾸는 모든 길(게임 코드 전체 IL 검색으로 확인)에서 확인을 취소한다:
    //   색·불투명도·그리기 색·보이게 됨 -> ApplyColor 뒤 (InvisibleSkip.Work)
    //   위치 -> SetPosition 앞 (InvisibleSkip.LazyPrefix), 모드가 바로 미룬 위치 (InvisibleSkip.LazyStore)
    //   기준 위치 -> SetPlacementType, 마스크 -> SetMaskingType
    //   애니메이션 사전 -> 원래 코드로 도는 장식 이동 효과(대상 전부), 파티클 설정 효과(전부 취소), 장식 삭제(맵 바뀜 -> 전부 취소)
    //   그 밖의 조건 값(히트박스, 시차 부품, 타일 붙이기, 형)은 장식을 만들 때만 바뀐다
    // 개발자용 검증: 건너뛴 효과 2번에 1번은 루프를 실제로 돌려(표본 대조는 끄고) 대상의 상태가 하나도 안 바뀌는지 비교한다.
    internal static class Precheck
    {
        internal static bool Enabled = true;
        internal static int Active;                 // 확인 중이거나 준비된 계획의 비트
        private const int MaxPlans = 30, MinTargets = 200;
        private const double LookSec = 3.0;
        private const double BudgetMs = 0.3;        // 한 프레임에 확인에 쓸 시간

        private sealed class Plan
        {
            public ffxMoveDecorationsPlus Fx; public int Bit; public List<scrDecoration> Targets; public int Ver; public string Tag;
            public bool Valid = true, Ready, Pos, Px, Py, Col, Opa; public int Checked;
            public Vector2 Tp; public Color Tc; public float To; public bool Clean; public int Keys;
            public HashSet<scrDecoration> Seen;
        }
        private static readonly Plan[] plans = new Plan[MaxPlans];
        private static readonly List<Plan> cleaning = new List<Plan>();   // 끝난 계획: 붙인 표시를 떼는 중 (그동안 비트를 다시 쓰지 않음)
        private static int cleanMask;
        private static int scanIdx, lastCur = -1;

        internal static long Created, Ready, Used, UsedDecos, Checks, VerifyN, VerifyDecos, VerifyMismatch;
        internal static long InvTouch, InvEffect, InvList, NotReady, NotNoop, NoBit, Resets;
        internal static string FirstNotNoop = "", VerifyFirst = "";

        private static readonly AccessTools.FieldRef<scrVfxPlus, List<ffxPlusBase>> effRef = AccessTools.FieldRefAccess<scrVfxPlus, List<ffxPlusBase>>("effects");
        private static readonly AccessTools.FieldRef<scrVfxPlus, int> idxRef = AccessTools.FieldRefAccess<scrVfxPlus, int>("currentVfxIndex");
        private static readonly AccessTools.FieldRef<scrVfxPlus, scrConductor> condRef = AccessTools.FieldRefAccess<scrVfxPlus, scrConductor>("cond");
        private static readonly AccessTools.FieldRef<ffxPlusBase, double> startRef = AccessTools.FieldRefAccess<ffxPlusBase, double>("startTime");
        private static readonly AccessTools.FieldRef<ffxPlusBase, double> offRef = AccessTools.FieldRefAccess<ffxPlusBase, double>("startEffectOffset");
        private static readonly AccessTools.FieldRef<ffxPlusBase, bool> trigRef = AccessTools.FieldRefAccess<ffxPlusBase, bool>("triggered");
        private static readonly AccessTools.FieldRef<scrDecoration, Dictionary<global::TweenType, Tween>> tweensRef = AccessTools.FieldRefAccess<scrDecoration, Dictionary<global::TweenType, Tween>>("eventTweens");
        private static readonly AccessTools.FieldRef<scrDecoration, Vector2> startPosRef = AccessTools.FieldRefAccess<scrDecoration, Vector2>("startPos");
        private static readonly AccessTools.FieldRef<scrDecoration, Vector2> pivotPosRef = AccessTools.FieldRefAccess<scrDecoration, Vector2>("pivotPosVec");
        private static readonly AccessTools.FieldRef<scrDecoration, bool> stickRef = AccessTools.FieldRefAccess<scrDecoration, bool>("stickToFloor");
        private static readonly AccessTools.FieldRef<scrDecoration, Color> colRef = AccessTools.FieldRefAccess<scrDecoration, Color>("color");
        private static readonly AccessTools.FieldRef<scrDecoration, float> opaRef = AccessTools.FieldRefAccess<scrDecoration, float>("opacity");
        private static AccessTools.FieldRef<List<scrDecoration>, int> versionRef;

        internal static void Install(Harmony h)
        {
            try
            {
                versionRef = AccessTools.FieldRefAccess<List<scrDecoration>, int>("_version");
                h.Patch(AccessTools.Method(typeof(scrVfxPlus), "Update"), postfix: new HarmonyMethod(typeof(Precheck), nameof(VfxPost)));
                h.Patch(AccessTools.Method(typeof(scrDecoration), "SetPlacementType"), prefix: new HarmonyMethod(typeof(Precheck), nameof(DecoTouchPre)));
                h.Patch(AccessTools.Method(typeof(scrVisualDecoration), "SetMaskingType"), prefix: new HarmonyMethod(typeof(Precheck), nameof(DecoTouchPre)));
                var sp = AccessTools.TypeByName("ffxSetParticlePlus");
                if (sp != null) foreach (var m in sp.GetMethods(AccessTools.all))
                    if (m.Name == "StartEffect" && m.DeclaringType == sp && !m.IsAbstract)
                        h.Patch(m, prefix: new HarmonyMethod(typeof(Precheck), nameof(ParticlePre)));
                Main.Entry.Logger.Log("[미리 확인] 설치" + (sp == null ? " (파티클 설정 효과 못 찾음 - 끔)" : ""));
                if (sp == null) Enabled = false;   // 애니메이션 사전을 바꾸는 길 하나를 못 막으면 쓰지 않는다
            }
            catch (Exception ex) { Enabled = false; Main.Entry.Logger.Log("[미리 확인] 설치 실패, 끔: " + ex.Message); }
        }

        public static void DecoTouchPre(scrDecoration __instance) { if (Active != 0) InvisibleSkip.TouchDeco(__instance); }
        public static void ParticlePre() { if (Active != 0) ResetAll(); }

        // 표시가 붙은 장식이 바뀌었다: 그 장식을 지켜보던 계획을 모두 취소
        internal static void Touch(int bits)
        {
            bits &= Active;
            for (int b = 0; bits != 0 && b < MaxPlans; b++, bits >>= 1)
            {
                if ((bits & 1) == 0) continue;
                var p = plans[b];
                if (p != null && p.Valid) { p.Valid = false; InvTouch++; }
            }
        }

        // 원래 코드로 도는 장식 이동 효과: 애니메이션 사전을 바꿀 수 있으므로 그 대상의 계획을 취소
        internal static void TouchTargets(List<string> tags, Dictionary<string, List<scrDecoration>> dict)
        {
            if (Active == 0 || tags == null || dict == null) return;
            long before = InvTouch;
            for (int i = 0; i < tags.Count; i++)
            {
                List<scrDecoration> l;
                if (tags[i] == null || !dict.TryGetValue(tags[i], out l) || l == null) continue;
                for (int j = 0; j < l.Count; j++) if ((object)l[j] != null) InvisibleSkip.TouchDeco(l[j]);
            }
            if (InvTouch != before) { InvEffect += InvTouch - before; InvTouch = before; }
        }

        private static bool On
        {
            get
            {
                return Enabled && FastMove.Enabled && FastMove.Installed && InstantMove.Enabled && InstantMove.Patched && InstantMove.SkipSame
                    && InvisibleSkip.Enabled && InvisibleSkip.LazyMove && Hitch.Playing;
            }
        }

        // ── 매 프레임 (게임이 이번 프레임의 효과를 꺼낸 직후) ──
        public static void VfxPost(scrVfxPlus __instance)
        {
            try { Tick(__instance); } catch (Exception ex) { ResetAll(); Enabled = false; Main.Entry.Logger.Log("[미리 확인] 오류로 끔: " + ex.Message); }
        }

        private static long t0; private static long budgetTicks;
        private static bool Over() { return System.Diagnostics.Stopwatch.GetTimestamp() - t0 > budgetTicks; }

        private static void Tick(scrVfxPlus vfx)
        {
            if (!On || !ADOBase.customLevel || (int)ADOBase.controller.visualQuality == 10) { if (Active != 0 || cleaning.Count > 0) ResetAll(); scanIdx = 0; lastCur = -1; return; }
            var list = effRef(vfx); var cond = condRef(vfx);
            if (list == null || (object)cond == null) return;
            int cur = idxRef(vfx);
            if (cur < lastCur) { ResetAll(); scanIdx = 0; }   // 다시 시작
            lastCur = cur;
            if (scanIdx < cur) scanIdx = cur;
            double now = cond.songposition_minusi;

            // 발동했거나(발동 조건에 안 맞아 건너뛴 것 포함) 지나간 계획은 끝낸다
            for (int b = 0; b < MaxPlans; b++)
            {
                var p = plans[b];
                // 발동한 뒤 1초가 지난 계획만 끝낸다 (효과 나누기로 한두 프레임 밀린 효과도 확인 결과를 쓸 수 있게)
                if (p != null && trigRef(p.Fx) && now > startRef(p.Fx) - offRef(p.Fx) + 1.0) { if (!p.Ready || !p.Valid) NotReady++; Free(p); }
            }
            // 효과가 몰린 프레임에는 일을 더하지 않는다
            if (EffectScan.FrameEffectMs > 3.0) return;
            t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            budgetTicks = (long)(BudgetMs * System.Diagnostics.Stopwatch.Frequency / 1000.0);

            Cleanup();
            if (Over()) return;

            int scanned = 0;
            while (scanIdx < list.Count)
            {
                var e = list[scanIdx];
                if ((object)e != null)
                {
                    if (startRef(e) - offRef(e) > now + LookSec) break;
                    var m = e as ffxMoveDecorationsPlus;
                    if ((object)m != null && !trigRef(m)) TryPlan(m);
                }
                scanIdx++;
                if ((++scanned & 15) == 0 && Over()) return;
            }

            // 먼저 발동할 계획부터 확인
            while (!Over())
            {
                Plan next = null;
                for (int b = 0; b < MaxPlans; b++)
                {
                    var p = plans[b];
                    if (p == null || p.Ready || !p.Valid) continue;
                    if (next == null || startRef(p.Fx) < startRef(next.Fx)) next = p;
                }
                if (next == null) break;
                Work(next);
            }
        }

        private static int FreeBit()
        {
            for (int b = 0; b < MaxPlans; b++) if (plans[b] == null && (cleanMask & (1 << b)) == 0) return b;
            return -1;
        }

        private static void TryPlan(ffxMoveDecorationsPlus fx)
        {
            var s = FastMove.Shape(fx);
            if (s == null) return;
            if (s.Targets.Count < MinTargets) return;
            int b = FreeBit();
            if (b < 0) { NoBit++; return; }
            var p = new Plan { Fx = fx, Bit = b, Targets = s.Targets, Ver = versionRef(s.Targets), Tag = s.Tag, Pos = s.Pos, Px = s.Px, Py = s.Py, Col = s.Col, Opa = s.Opa,
                Tp = s.Tp, Tc = s.Tc, To = s.To, Keys = s.Keys, Clean = FastMove.IsClean(s.Targets, versionRef(s.Targets)) };
            if (!p.Clean) p.Seen = new HashSet<scrDecoration>(FastMove.DecoEq);
            plans[b] = p; Active |= 1 << b; Created++;
        }

        private static void Work(Plan p)
        {
            var l = p.Targets; int bit = 1 << p.Bit; int n = 0;
            while (p.Checked < l.Count)
            {
                if (versionRef(l) != p.Ver) { p.Valid = false; InvList++; return; }
                var d = l[p.Checked];
                if ((object)d == null || (p.Seen != null && !p.Seen.Add(d))) { NotNoopWhy(p, "목록에 null 이나 중복"); return; }
                string why = NoopNow(p, d);
                if (why != null) { NotNoopWhy(p, why); return; }
                if (!InvisibleSkip.AddWatch(d, bit)) { NotNoopWhy(p, "안 그리는 목록에 없음"); return; }
                p.Checked++; Checks++;
                if ((++n & 15) == 0 && Over()) return;
            }
            p.Ready = true; Ready++;
            if (!p.Clean) { FastMove.MarkClean(l, p.Ver); p.Clean = true; p.Seen = null; }
        }

        private static void NotNoopWhy(Plan p, string why)
        {
            p.Valid = false; NotNoop++;
            if (FirstNotNoop.Length < 200 && FirstNotNoop.IndexOf(why, StringComparison.Ordinal) < 0) FirstNotNoop += (FirstNotNoop.Length > 0 ? ", " : "") + why;
        }

        // 이 장식에 대해 효과가 아무것도 안 바꾸는가. 바꾸면 그 이유를 돌려준다.
        private static string NoopNow(Plan p, scrDecoration d)
        {
            if (d.GetType() != typeof(scrVisualDecoration)) return "일반 이미지 장식 아님";
            if (stickRef(d)) return "타일에 붙은 장식";
            if (!FastMove.AllDeadMask(tweensRef(d), p.Keys)) return "애니메이션이 살아 있음";
            if (p.Pos)
            {
                if (!InvisibleSkip.NoParallax(d))
                {
                    if (!InvisibleSkip.LazyCan(d)) return "위치를 미룰 수 없는 장식";
                    if (!InvisibleSkip.InLazy(d)) return "위치가 미루기 목록에 없음";
                    var pp = pivotPosRef(d); var sp = startPosRef(d);
                    if (p.Px && InstantMove.Bits(pp.x) != InstantMove.Bits(sp.x + p.Tp.x)) return "위치가 다름";
                    if (p.Py && InstantMove.Bits(pp.y) != InstantMove.Bits(sp.y + p.Tp.y)) return "위치가 다름";
                }
            }
            if (p.Col && !InstantMove.ColorNoop(d, p.Tc, opaRef(d))) return "색이 다름";
            if (p.Opa && !InstantMove.ColorNoop(d, p.Col ? p.Tc : colRef(d), p.To)) return "불투명도가 다름";
            if (!InvisibleSkip.IsHidden(d)) return "보이는 장식";
            return null;
        }

        // FastMove.Prefix 가 부른다: 이 효과를 건너뛰어도 되면 true (효과 앞부분의 필드 쓰기는 FastMove 가 한다)
        internal static bool TrySkip(ffxMoveDecorationsPlus fx)
        {
            if (Active == 0) return false;
            Plan p = null;
            for (int b = 0; b < MaxPlans; b++) if (plans[b] != null && ReferenceEquals(plans[b].Fx, fx)) { p = plans[b]; break; }
            if (p == null) return false;
            bool ok = p.Ready && p.Valid && On && versionRef(p.Targets) == p.Ver;
            if (ok)
            {
                var s = FastMove.Shape(fx);   // 효과 값이 확인할 때와 같은지
                ok = s != null && ReferenceEquals(s.Targets, p.Targets) && s.Pos == p.Pos && s.Px == p.Px && s.Py == p.Py && s.Col == p.Col && s.Opa == p.Opa
                    && InstantMove.Bits(s.Tp.x) == InstantMove.Bits(p.Tp.x) && InstantMove.Bits(s.Tp.y) == InstantMove.Bits(p.Tp.y)
                    && s.Tc == p.Tc && InstantMove.Bits(s.To) == InstantMove.Bits(p.To);
            }
            if (!ok) { if (!p.Ready || !p.Valid) NotReady++; Free(p); return false; }
            Used++; UsedDecos += p.Targets.Count;
            Free(p);
            return true;
        }

        internal static void VerifyResult(int decos, int mismatch, string first)
        {
            VerifyN++; VerifyDecos += decos; VerifyMismatch += mismatch;
            if (mismatch > 0 && VerifyFirst.Length < 500) VerifyFirst += first;
        }

        private static void Free(Plan p)
        {
            if (plans[p.Bit] != p) return;
            plans[p.Bit] = null;
            Active &= ~(1 << p.Bit);
            if (p.Checked > 0) { cleanMask |= 1 << p.Bit; cleaning.Add(p); }
        }

        // 끝난 계획이 붙인 표시를 뗀다 (시간이 남는 프레임에 조금씩)
        private static void Cleanup()
        {
            int n = 0;
            while (cleaning.Count > 0)
            {
                var p = cleaning[cleaning.Count - 1];
                int bit = 1 << p.Bit;
                var l = p.Targets; int upto = Math.Min(p.Checked, l.Count);
                while (p.Checked > 0)
                {
                    p.Checked--;
                    if (p.Checked < upto) { var d = l[p.Checked]; if ((object)d != null) InvisibleSkip.ClearWatch(d, bit); }
                    if ((++n & 31) == 0 && Over()) return;
                }
                cleaning.RemoveAt(cleaning.Count - 1);
                cleanMask &= ~bit;
            }
        }

        internal static void ResetAll()
        {
            bool any = Active != 0;
            for (int b = 0; b < MaxPlans; b++) if (plans[b] != null) Free(plans[b]);
            if (any) Resets++;
        }

        internal static string Summary()
        {
            if (Created == 0 && Resets == 0) return "";
            string s = string.Format(" | 미리 확인: 계획 {0}개, 확인 끝남 {1}개, 건너뛴 효과 {2}개(장식 {3}개), 장식 확인 {4}번 | 못 쓴 것: 확인 뒤 바뀜 {5}번, 원래 코드 효과가 대상을 건드림 {6}번, 목록 바뀜 {7}번, 발동 전에 못 끝냄·취소 {8}번, 그대로가 아님 {9}번 [{10}], 자리 없음 {11}번, 전체 취소 {12}번",
                Created, Ready, Used, UsedDecos, Checks, InvTouch, InvEffect, InvList, NotReady, NotNoop, FirstNotNoop, NoBit, Resets);
            if (Edition.Dev) s += " (검증: 건너뛴 효과 " + VerifyN + "번을 실제로 돌려 장식 " + VerifyDecos + "개 중 바뀐 것 " + VerifyMismatch + VerifyFirst + ")";
            return s;
        }
        internal static void ResetStats()
        {
            ResetAll();
            Created = Ready = Used = UsedDecos = Checks = VerifyN = VerifyDecos = VerifyMismatch = 0;
            InvTouch = InvEffect = InvList = NotReady = NotNoop = NoBit = Resets = 0; FirstNotNoop = ""; VerifyFirst = "";
        }
    }
}
