using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 매 프레임 장식 순회에서 "잠든" 장식을 아예 빼 둔다.
    //
    // scrDecorationManager.LateUpdate 는 매 프레임 장식 전부(Arche 28,835개)를 훑으며 LogicUpdate 를 부른다.
    // 1.3.4 이후 측정: 그중 99.4% 는 호출해도 바뀌는 게 없는 장식이었고, 호출을 빼도(MoveApply.LogicMaybe) 2만 8천 개를
    // 훑는 것 자체(목록 읽기 + 유니티 null 검사 + 확인)가 남아 평소 프레임에 1.3~1.7ms 가 들었다.
    //
    // 잠든 장식 = scrVisualDecoration 이고, disableV15Features 맵이고, 히트박스 없음, 메시 꺼짐, GetVisible() == false.
    // 이때 LogicUpdate 는 아무것도 바꾸지 않는다(MoveApply.LogicMaybe 설명 참고). 게임이 순회할 목록(allDecorations)을
    // 깨어 있는 장식만 담은 목록으로 바꿔 준다.
    //
    // 잠든 조건을 바꿀 수 있는 값과, 게임 코드에서 그 값을 쓰는 곳(IL 전체 검색):
    //   rendererColor       -> scrVisualDecoration.ApplyColor (+ 생성자)
    //   rendererEnabled     -> scrVisualDecoration.SetVisible (+ 생성자)
    //   maskingType         -> scrVisualDecoration.SetMaskingType
    //   meshRendererEnabled -> scrVisualDecoration.EnableMeshRenderer
    //   hitbox              -> scrDecoration.Awake, scrDecoration.Setup
    // 이 함수들이 불리면 그 장식을 깨운다. 장식 목록 자체가 바뀌면(맵 다시 만들기) 처음부터 다시 만든다.
    // 안전망: 1초에 한 번(개발자용은 30프레임마다) 잠든 장식을 전부 다시 검사해, 깨웠어야 할 것을 놓쳤으면 세고 깨운다.
    public static class Dormancy
    {
        internal static bool Enabled = true;

        private sealed class RefEq : IEqualityComparer<scrDecoration>
        {
            internal static readonly RefEq Instance = new RefEq();
            public bool Equals(scrDecoration a, scrDecoration b) { return ReferenceEquals(a, b); }
            public int GetHashCode(scrDecoration o) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o); }
        }

        private static readonly AccessTools.FieldRef<scrDecorationManager, List<scrDecoration>> allRef =
            AccessTools.FieldRefAccess<scrDecorationManager, List<scrDecoration>>("allDecorations");
        private static readonly AccessTools.FieldRef<scrVisualDecoration, bool> meshOnRef =
            AccessTools.FieldRefAccess<scrVisualDecoration, bool>("meshRendererEnabled");
        private static AccessTools.FieldRef<List<scrDecoration>, int> versionRef;

        private static readonly List<scrDecoration> awake = new List<scrDecoration>();
        private static readonly HashSet<scrDecoration> dormant = new HashSet<scrDecoration>(RefEq.Instance);
        private static readonly List<scrDecoration> woken = new List<scrDecoration>();
        private static readonly HashSet<scrDecoration> inAwake = new HashSet<scrDecoration>(RefEq.Instance);   // awake 목록에 이미 있는지 (같은 장식이 두 번 들어가 한 프레임에 두 번 처리되는 것을 막는다)
        private static List<scrDecoration> source;
        private static int sourceVersion = -1, sourceCount = -1;
        private static bool prepared, lastDisable, broken;
        private static float nextAudit;

        internal static long Rebuilds, Slept, Wakes, Missed, Audits, AwakeFrames, AwakeTotal;
        internal static int AllCount, LastAwake;   // LastAwake: 이번 프레임 깨어 있어 훑은 장식 수

        internal static void Install(Harmony h)
        {
            try
            {
                try { versionRef = AccessTools.FieldRefAccess<List<scrDecoration>, int>("_version"); }
                catch { versionRef = null; }
                var post = new HarmonyMethod(typeof(Dormancy), nameof(WakePost));
                foreach (var name in new[] { "ApplyColor", "SetVisible", "SetMaskingType", "EnableMeshRenderer" })
                {
                    var m = AccessTools.Method(typeof(scrVisualDecoration), name);
                    if (m == null) { broken = true; Main.Entry.Logger.Log("[잠든 장식] " + name + " 없음 - 끔"); return; }
                    h.Patch(m, postfix: post);
                }
                foreach (var name in new[] { "Setup", "Awake" })
                {
                    var m = AccessTools.Method(typeof(scrDecoration), name);
                    if (m != null) h.Patch(m, postfix: new HarmonyMethod(typeof(Dormancy), nameof(WakeBase)));
                }
                Main.Entry.Logger.Log("[잠든 장식] 설치" + (versionRef == null ? " (목록 버전 못 읽음 - 개수로만 확인)" : ""));
            }
            catch (Exception ex) { broken = true; Main.Entry.Logger.Log("[잠든 장식] 설치 실패: " + ex.Message); }
        }

        public static void WakePost(scrVisualDecoration __instance) { Wake(__instance); }
        public static void WakeBase(scrDecoration __instance) { hbDirty = true; Wake(__instance); }

        private static void Wake(scrDecoration d)
        {
            if (dormant.Count == 0 || (object)d == null) return;
            if (dormant.Remove(d)) { woken.Add(d); Wakes++; }
        }

        // 게임 LateUpdate 앞에서 부른다
        internal static void NewFrame() { prepared = false; }

        // scrDecorationManager.LateUpdate 안의 "this.allDecorations" 를 이것으로 바꾼다(루프 앞과 루프 안에서 불린다)
        public static List<scrDecoration> List(scrDecorationManager mgr)
        {
            var all = allRef(mgr);
            if (!Enabled || broken || !MoveApply.LogicSkip || all == null || !Hitch.Playing) { ResetState(); return all; }
            if (prepared) return awake;
            prepared = true;
            try { Prepare(all); }
            catch (Exception ex) { broken = true; Main.Entry.Logger.Log("[잠든 장식] 오류로 끔: " + ex.Message); ResetState(); return all; }
            AwakeFrames++; AwakeTotal += awake.Count; AllCount = all.Count; LastAwake = awake.Count;
            if (Edition.Dev || Main.MeasureBuild) CountOffscreen();
            return awake;
        }

        private static void Prepare(List<scrDecoration> all)
        {
            bool disable = false;
            try { disable = ADOBase.controller != null && ADOBase.controller.disableV15Features; } catch { }
            int ver = versionRef != null ? versionRef(all) : 0;
            if (!ReferenceEquals(all, source) || all.Count != sourceCount || ver != sourceVersion || disable != lastDisable)
            {
                // 목록이 바뀌었다: 전부 깨운 채로 다시 시작한다
                source = all; sourceCount = all.Count; sourceVersion = ver; lastDisable = disable;
                awake.Clear(); awake.AddRange(all);
                inAwake.Clear(); foreach (var d in all) if ((object)d != null) inAwake.Add(d);
                dormant.Clear(); woken.Clear();
                Rebuilds++;
                return;
            }

            // 지난 프레임에 잠든 것으로 표시된 것을 빼고, 그 사이 깨운 것을 넣는다
            if (dormant.Count > 0)
            {
                int w = 0;
                for (int i = 0; i < awake.Count; i++)
                {
                    var d = awake[i];
                    if ((object)d != null && dormant.Contains(d)) { inAwake.Remove(d); continue; }
                    awake[w++] = d;
                }
                if (w < awake.Count) awake.RemoveRange(w, awake.Count - w);
            }
            if (woken.Count > 0) { foreach (var d in woken) if (inAwake.Add(d)) awake.Add(d); woken.Clear(); }

            // 안전망: 잠든 장식을 다시 검사한다
            float now = Time.realtimeSinceStartup;
            if (dormant.Count > 0 && (Edition.Dev ? Time.frameCount % 30 == 0 : now >= nextAudit))
            {
                nextAudit = now + 1f;
                Audits++;
                List<scrDecoration> miss = null;
                foreach (var d in dormant)
                    if (d != null && !IsDormant(d, disable)) (miss ?? (miss = new List<scrDecoration>())).Add(d);
                if (miss != null) foreach (var d in miss) { dormant.Remove(d); if (inAwake.Add(d)) awake.Add(d); Missed++; }
            }
        }

        // ── 히트박스 순회 ──
        // scrDecorationManager.Update 는 매 프레임 장식 전부에 CheckHitboxHit 을 부른다(1.3.5 측정: 가벼운 구간에서도 0.78ms).
        // CheckHitboxHit 은 히트박스가 없으면(hitbox == 0) 첫 줄에서 끝난다. hitbox 와 hitboxDetectTarget 을 쓰는 곳은
        // 게임 전체에서 scrDecoration.Awake 와 Setup 뿐이다(IL 전체 검색). 그래서 히트박스 있는 장식만 담은 목록을 넘긴다.
        // 목록이 바뀌거나 Awake/Setup 이 불리면 다시 만든다.
        private static readonly List<scrDecoration> hitboxList = new List<scrDecoration>();
        private static List<scrDecoration> hbSource;
        private static int hbVersion = -1, hbCount = -1;
        private static bool hbDirty = true, hbPrepared;
        internal static long HitboxRebuilds, HitboxFrames, HitboxAudits, HitboxMissed;
        internal static int HitboxCount, HitboxAll;

        internal static void HitboxNewFrame() { hbPrepared = false; }

        public static List<scrDecoration> HitboxList(scrDecorationManager mgr)
        {
            var all = allRef(mgr);
            if (!Enabled || broken || all == null || !Hitch.Playing) return all;
            if (hbPrepared) return hitboxList;
            hbPrepared = true;
            int ver = versionRef != null ? versionRef(all) : 0;
            if (hbDirty || !ReferenceEquals(all, hbSource) || all.Count != hbCount || ver != hbVersion)
            {
                hitboxList.Clear();
                for (int i = 0; i < all.Count; i++)
                {
                    var d = all[i];
                    if ((object)d == null || d.hitbox != 0) hitboxList.Add(d);   // null 은 원래대로 둔다(원래 코드가 그대로 부른다)
                }
                hbSource = all; hbCount = all.Count; hbVersion = ver; hbDirty = false;
                HitboxRebuilds++;
            }
            HitboxFrames++; HitboxCount = hitboxList.Count; HitboxAll = all.Count;
            if (Edition.Dev && Time.frameCount % 30 == 0)
            {
                int n = 0;
                for (int i = 0; i < all.Count; i++) { var d = all[i]; if ((object)d == null || d.hitbox != 0) n++; }
                HitboxAudits++;
                if (n != hitboxList.Count) HitboxMissed++;
            }
            return hitboxList;
        }

        // MoveApply.LogicMaybe 가 "이 장식은 호출해도 바뀌는 게 없다" 고 판단했을 때 부른다. 다음 프레임부터 목록에서 뺀다.
        internal static void Sleep(scrDecoration d)
        {
            if (!Enabled || broken || source == null) return;
            if (dormant.Add(d)) Slept++;
        }

        // (측정용) 매 프레임 훑는(깨어 있는) 장식 중 실제로 화면에 안 그려진 것(카메라 밖) 수. 화면 밖 장식 갱신을 미루면 얼마나 줄지 가늠한다.
        internal static int LastOffscreen;
        private static readonly AccessTools.FieldRef<scrVisualDecoration, SpriteRenderer> srOff = AccessTools.FieldRefAccess<scrVisualDecoration, SpriteRenderer>("spriteRenderer");
        private static void CountOffscreen()
        {
            int n = 0;
            for (int i = 0; i < awake.Count; i++)
            {
                var v = awake[i] as scrVisualDecoration;
                if ((object)v == null) continue;
                var r = srOff(v);
                if (r != null && r.enabled && !r.forceRenderingOff && !r.isVisible) n++;
            }
            LastOffscreen = n;
        }

        // r148: scrController.disableV15Features 는 생성자에서 true 로 정해지고 게임 어디서도 바꾸지 않는다(IL 전체 검색).
        // 맵 파일의 disableV15Features 값과 상관없이 장식 순회는 늘 이 조건(disableShader == true)으로 돈다.
        internal static bool IsDormant(scrDecoration d, bool disableShader)
        {
            return disableShader && d.hitbox == 0 && d.GetType() == typeof(scrVisualDecoration)
                && !meshOnRef((scrVisualDecoration)d) && !d.GetVisible();
        }

        private static void ResetState()
        {
            if (source == null && dormant.Count == 0) return;
            source = null; sourceCount = -1; sourceVersion = -1;
            awake.Clear(); dormant.Clear(); woken.Clear(); inAwake.Clear();
        }

        internal static string Summary()
        {
            if (AwakeFrames == 0 && HitboxFrames == 0) return "";
            string hb = HitboxFrames > 0 ? string.Format(" | 히트박스 순회: 전체 {0}개 중 히트박스 있는 {1}개만 (목록 새로 만듦 {2}번{3})", HitboxAll, HitboxCount, HitboxRebuilds, Edition.Dev ? ", 안전망 검사 " + HitboxAudits + "번에 어긋남 " + HitboxMissed : "") : "";
            return hb + string.Format(" | 매 프레임 순회: 전체 {0}개 중 평균 {1:F0}개만 훑음 (잠재움 {2}번, 깨움 {3}번, 목록 새로 만듦 {4}번, 안전망 검사 {5}번에 놓친 깨움 {6}개)",
                AllCount, (double)AwakeTotal / AwakeFrames, Slept, Wakes, Rebuilds, Audits, Missed);
        }

        internal static void ResetStats() { Rebuilds = Slept = Wakes = Missed = Audits = AwakeFrames = AwakeTotal = 0; HitboxRebuilds = HitboxFrames = HitboxAudits = HitboxMissed = 0; }
    }
}
