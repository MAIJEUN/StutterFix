using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 맵 열기·재생 시작 로딩 줄이기.
    //
    // 1) 이미지 파일 수정 시각 캐시 (기본 켜짐)
    //    TextureManager.GetOrAddSprite / AddTexture 는 부를 때마다 new FileInfo(경로).LastWriteTimeUtc 로 디스크에서 파일 시각을 읽어
    //    "파일이 바뀌었으면 다시 불러오기" 를 한다(IL 확인). 장식마다 불리므로 Arche 를 에디터에서 재생하면 5만 7천 번(0.87초).
    //    같은 프레임 안에서는 같은 파일의 시각을 한 번만 읽고 기억해 둔다. 프레임이 바뀌면 잊으므로, 파일을 고친 뒤 다시 재생하거나
    //    맵을 다시 열면 전처럼 바뀐 것을 알아챈다(한 번의 불러오기 도중에 파일이 바뀌는 경우만 다음 불러오기로 미뤄진다).
    // 2) (개발자용) 에디터 클릭용 충돌 상자 켜고 끄기(scrDecoration.SetCollider) 비용을 GameObject 켜기/끄기와 충돌 상자 켜기/끄기로 나눠 잰다.
    //    Arche 에서 재생 시작·편집 복귀 때마다 2.7초. 어느 쪽이 비싼지 보고 줄일 방법을 정한다.
    internal static class LoadFix
    {
        internal static bool CacheFileTimes = true;
        internal static long TimeHits, TimeMisses;
        private static readonly Dictionary<string, DateTime> times = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private static int timesFrame = -1;
        private static int replaced;

        internal static void Install(Harmony h)
        {
            try
            {
                foreach (var name in new[] { "GetOrAddSprite", "AddTexture" })
                    foreach (var m in typeof(TextureManager).GetMethods(AccessTools.all))
                        if (m.Name == name && !m.IsAbstract) h.Patch(m, transpiler: new HarmonyMethod(typeof(LoadFix), nameof(TimeTranspiler)));
                Main.Entry.Logger.Log("[로딩] 이미지 파일 시각 캐시 설치 (바꾼 곳 " + replaced + ")");
                if (Edition.Dev)
                {
                    var sc = AccessTools.Method(typeof(scrDecoration), "SetCollider", new[] { typeof(bool) });
                    if (sc != null && sc.DeclaringType == typeof(scrDecoration)) h.Patch(sc, prefix: new HarmonyMethod(typeof(LoadFix), nameof(SetColliderProbe)));
                }
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[로딩] 설치 실패: " + ex.Message); }
        }

        // newobj FileInfo(string) ; callvirt FileSystemInfo.get_LastWriteTimeUtc  ->  call LoadFix.WriteTime(string) ; nop
        public static IEnumerable<CodeInstruction> TimeTranspiler(IEnumerable<CodeInstruction> ins)
        {
            var ctor = AccessTools.Constructor(typeof(FileInfo), new[] { typeof(string) });
            var getter = AccessTools.PropertyGetter(typeof(FileSystemInfo), "LastWriteTimeUtc");
            var list = new List<CodeInstruction>(ins);
            for (int i = 0; i + 1 < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Newobj && ReferenceEquals(list[i].operand, ctor)
                    && (list[i + 1].opcode == OpCodes.Callvirt || list[i + 1].opcode == OpCodes.Call) && ReferenceEquals(list[i + 1].operand, getter)
                    )
                {
                    list[i].opcode = OpCodes.Call; list[i].operand = AccessTools.Method(typeof(LoadFix), nameof(WriteTime));
                    list[i + 1].opcode = OpCodes.Nop; list[i + 1].operand = null;
                    replaced++;
                }
            }
            return list;
        }

        public static DateTime WriteTime(string path)
        {
            if (!CacheFileTimes || path == null) return new FileInfo(path).LastWriteTimeUtc;
            int f = Time.frameCount;
            if (f != timesFrame) { times.Clear(); timesFrame = f; }
            DateTime t;
            if (times.TryGetValue(path, out t)) { TimeHits++; return t; }
            t = new FileInfo(path).LastWriteTimeUtc;   // 없는 파일, 잘못된 경로도 원래와 같은 값·예외
            times[path] = t; TimeMisses++;
            return t;
        }

        // ── (개발자용) 충돌 상자 켜고 끄기 비용 ──
        private static readonly System.Reflection.FieldInfo colField = AccessTools.Field(typeof(scrDecoration), "editorCollider");
        internal static long ColCalls, ColActiveTicks, ColEnableTicks, ColActiveChanged, ColEnableChanged;
        private static bool colLogged;
        public static bool SetColliderProbe(scrDecoration __instance, bool __0)
        {
            var c = colField == null ? null : colField.GetValue(__instance) as Behaviour;
            if (c == null) return false;   // 원래 코드도 null 이면 아무것도 안 한다
            var go = c.gameObject;
            if (!colLogged)
            {
                colLogged = true;
                var names = new List<string>();
                foreach (var comp in go.GetComponents<Component>()) names.Add(comp.GetType().Name);
                Main.Entry.Logger.Log("[로딩] 충돌 상자 오브젝트 구성: " + string.Join(", ", names.ToArray()) + ", 자식 " + go.transform.childCount + "개, 종류 " + c.GetType().Name);
            }
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (go.activeSelf != __0) ColActiveChanged++;
            go.SetActive(__0);
            long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
            if (c.enabled != __0) ColEnableChanged++;
            c.enabled = __0;
            long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
            ColCalls++; ColActiveTicks += t1 - t0; ColEnableTicks += t2 - t1;
            return false;
        }

        // ── 3) 에디터 재생 시작 때 장식 다시 설정 한 번 줄이기 ──
        // scnEditor.Play 는 scnGame.ReloadAssets 안에서 장식 전체를 한 번 다시 설정(ResetDecorations)하고, 곧이어 scnGame.Play 의
        // FinishCustomLevelLoading 에서 또 한 번 한다(Arche: 1.8초 + 1.9초). 첫 번째는 "쓰는 이미지 표시"(MarkAllUnused -> 설정 중
        // GetOrAddSprite -> Unload 로 안 쓰는 이미지 내리기)와 태그 목록 다시 만들기를 겸하는데, 그 사이에 태그 목록을 읽는 곳은
        // 필터 효과 준비(ffxSetFilterAdvancedPlus.Setup) 정도다. 지난 다시 설정 뒤로 장식 데이터(목록, 순서, 각 장식 이벤트의 모든 값)가
        // 하나도 바뀌지 않았으면 태그 목록도 그때와 똑같으므로, 첫 번째 다시 설정과 짝인 표시/내리기를 건너뛴다(안 쓰는 이미지는 다음
        // 맵 열기 때 내려간다). 조금이라도 바뀌었으면 원래대로 한다.
        // 개발자용: 건너뛴 재생과 안 건너뛴 재생을 번갈아 하고, 재생 준비가 끝난 순간 모든 장식의 상태를 비교한다.
        internal static bool SkipDoubleReset = true;
        internal static long ResetsSkipped, ResetsKept;
        private static bool inEditorPlay, inReload, skipping, haveFp, lastPlaySkipped;
        private static long lastFp;
        private static int devPlays;
        private static readonly AccessTools.FieldRef<scrDecorationManager, List<scrDecoration>> allRef = AccessTools.FieldRefAccess<scrDecorationManager, List<scrDecoration>>("allDecorations");
        private static readonly System.Reflection.FieldInfo dataField = AccessTools.Field(AccessTools.TypeByName("ADOFAI.LevelEvent") ?? AccessTools.TypeByName("LevelEvent"), "data");

        internal static void InstallDoubleReset(Harmony h)
        {
            try
            {
                var play = AccessTools.Method(typeof(scnEditor), "Play");
                var reload = AccessTools.Method(typeof(scnGame), "ReloadAssets");
                var reset = AccessTools.Method(typeof(scrDecorationManager), "ResetDecorations");
                var mark = AccessTools.Method(typeof(TextureManager), "MarkAllUnused");
                var unload = AccessTools.Method(typeof(TextureManager), "Unload");
                if (play == null || reload == null || reset == null || mark == null || unload == null || dataField == null) { Main.Entry.Logger.Log("[로딩] 장식 다시 설정 줄이기: 게임 코드 모양이 달라 끔"); return; }
                h.Patch(play, prefix: new HarmonyMethod(typeof(LoadFix), nameof(PlayPrefix)), postfix: new HarmonyMethod(typeof(LoadFix), nameof(PlayPostfix)), finalizer: new HarmonyMethod(typeof(LoadFix), nameof(PlayFinalizer)));
                h.Patch(reload, prefix: new HarmonyMethod(typeof(LoadFix), nameof(ReloadPrefix)), finalizer: new HarmonyMethod(typeof(LoadFix), nameof(ReloadFinalizer)));
                h.Patch(reset, prefix: new HarmonyMethod(typeof(LoadFix), nameof(ResetPrefix)) { priority = Priority.First }, postfix: new HarmonyMethod(typeof(LoadFix), nameof(ResetPostfix)));
                h.Patch(mark, prefix: new HarmonyMethod(typeof(LoadFix), nameof(SkipIfSkipping)));
                h.Patch(unload, prefix: new HarmonyMethod(typeof(LoadFix), nameof(SkipIfSkipping)));
                Main.Entry.Logger.Log("[로딩] 에디터 재생 시작 때 장식 다시 설정 줄이기 설치");
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[로딩] 장식 다시 설정 줄이기 설치 실패: " + ex.Message); }
        }

        public static void PlayPrefix() { inEditorPlay = true; }
        public static void PlayPostfix() { if (Edition.Dev) VerifyAfterPlay(); }
        public static Exception PlayFinalizer(Exception __exception) { inEditorPlay = false; inReload = false; skipping = false; return __exception; }
        public static void ReloadPrefix()
        {
            inReload = true; skipping = false;
            if (!inEditorPlay || !SkipDoubleReset || !haveFp) return;
            try
            {
                bool same = Fingerprint() == lastFp;
                skipping = same && (!Edition.Dev || (devPlays++ % 2 == 1));   // 개발자용은 번갈아
                if (!same) Main.Entry.Logger.Log("[로딩] 장식이 바뀌어 장식 다시 설정을 원래대로 두 번 함");
            }
            catch (Exception ex) { skipping = false; Main.Entry.Logger.Log("[로딩] 장식 지문 실패, 원래대로: " + ex.Message); }
            lastPlaySkipped = skipping;
        }
        public static Exception ReloadFinalizer(Exception __exception) { inReload = false; skipping = false; return __exception; }
        public static bool SkipIfSkipping() { return !(inReload && skipping); }
        public static bool ResetPrefix()
        {
            if (inReload && skipping) { ResetsSkipped++; return false; }
            if (inReload) ResetsKept++;
            return true;
        }
        public static void ResetPostfix()
        {
            // 에디터에서만 지문을 남긴다 (재생 준비 끝의 다시 설정이 마지막)
            try { if (SkipDoubleReset && ADOBase.isLevelEditor) { lastFp = Fingerprint(); haveFp = true; } }
            catch { haveFp = false; }
        }

        // 장식 목록(순서, 객체), 각 장식의 이벤트 객체와 그 모든 값
        private static long Fingerprint()
        {
            var mgr = scrDecorationManager.instance;
            var all = mgr == null ? null : allRef(mgr);
            if (all == null) return 0;
            unchecked
            {
                long h = 1469598103934665603L ^ all.Count;
                foreach (var d in all)
                {
                    h = (h ^ System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(d)) * 1099511628211L;
                    if ((object)d == null) continue;
                    var ev = d.sourceLevelEvent;
                    if (ev == null) continue;
                    h = (h ^ System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(ev)) * 1099511628211L;
                    var data = dataField.GetValue(ev) as System.Collections.IDictionary;
                    if (data == null) continue;
                    long eh = data.Count;
                    foreach (System.Collections.DictionaryEntry kv in data)   // 순서와 상관없게 더한다
                        eh += (long)(kv.Key == null ? 0 : kv.Key.GetHashCode()) * 31 + ValueHash(kv.Value);
                    h = (h ^ eh) * 1099511628211L;
                }
                return h;
            }
        }
        private static long ValueHash(object v)
        {
            if (v == null) return 7;
            if (v is string) return v.GetHashCode();
            var e = v as System.Collections.IEnumerable;
            if (e != null) { long h = 17; unchecked { foreach (var x in e) h = h * 31 + ValueHash(x); } return h; }
            return v.GetHashCode();
        }

        // (개발자용) 재생 준비가 끝난 순간 모든 장식 상태를 비교
        private static readonly AccessTools.FieldRef<scrDecoration, Vector2> pivotPosRef = AccessTools.FieldRefAccess<scrDecoration, Vector2>("pivotPosVec");
        private static readonly AccessTools.FieldRef<scrDecoration, float> rotRef = AccessTools.FieldRefAccess<scrDecoration, float>("rotAngle");
        private static readonly AccessTools.FieldRef<scrDecoration, Vector2> scaleRef = AccessTools.FieldRefAccess<scrDecoration, Vector2>("scaleVec");
        private static readonly AccessTools.FieldRef<scrDecoration, Color> colRef2 = AccessTools.FieldRefAccess<scrDecoration, Color>("color");
        private static readonly AccessTools.FieldRef<scrDecoration, float> opaRef = AccessTools.FieldRefAccess<scrDecoration, float>("opacity");
        private static long[] lastSnap; private static long lastSnapFp; private static bool lastSnapSkipped;
        internal static long VerifyN, VerifyDiffs;
        private static void VerifyAfterPlay()
        {
            try
            {
                var mgr = scrDecorationManager.instance;
                var all = mgr == null ? null : allRef(mgr);
                if (all == null || !haveFp) return;
                var snap = new long[all.Count + 1];
                for (int i = 0; i < all.Count; i++)
                {
                    var d = all[i];
                    if ((object)d == null) continue;
                    unchecked
                    {
                        long h = pivotPosRef(d).GetHashCode() * 31L + rotRef(d).GetHashCode();
                        h = h * 31 + scaleRef(d).GetHashCode(); h = h * 31 + colRef2(d).GetHashCode(); h = h * 31 + opaRef(d).GetHashCode();
                        h = h * 31 + (d.GetVisible() ? 1 : 0); h = h * 31 + d.hitbox.GetHashCode();
                        var t = d.transform; h = h * 31 + t.position.GetHashCode(); h = h * 31 + t.rotation.GetHashCode(); h = h * 31 + t.lossyScale.GetHashCode();
                        var v = d as scrVisualDecoration;
                        if (v != null) foreach (var r in v.GetComponentsInChildren<SpriteRenderer>(true)) { h = h * 31 + (r.sprite == null ? 0 : r.sprite.GetInstanceID()); h = h * 31 + (r.enabled ? 1 : 0); h = h * 31 + r.color.GetHashCode(); }
                        snap[i] = h;
                    }
                }
                snap[all.Count] = TagCount(mgr);
                if (lastSnap != null && lastSnap.Length == snap.Length && lastSnapFp == lastFp && lastSnapSkipped != lastPlaySkipped)
                {
                    int diff = 0;
                    for (int i = 0; i < snap.Length; i++) if (snap[i] != lastSnap[i]) diff++;
                    VerifyN++; VerifyDiffs += diff;
                    Main.Entry.Logger.Log(string.Format("[로딩 검증] 장식 {0}개: 첫 번째 다시 설정을 건너뛴 재생과 한 재생의 재생 준비 끝 상태 비교 - 다른 것 {1}개{2}", all.Count, diff, diff > 0 && snap[all.Count] != lastSnap[all.Count] ? " (태그 목록 다름)" : ""));
                }
                lastSnap = snap; lastSnapFp = lastFp; lastSnapSkipped = lastPlaySkipped;
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[로딩 검증] 실패: " + ex.Message); }
        }
        private static readonly System.Reflection.FieldInfo tagField = AccessTools.Field(typeof(scrDecorationManager), "taggedDecorations");
        private static long TagCount(scrDecorationManager mgr)
        {
            var d = tagField == null ? null : tagField.GetValue(mgr) as System.Collections.IDictionary;
            if (d == null) return -1;
            long n = d.Count;
            foreach (System.Collections.DictionaryEntry kv in d) { var c = kv.Value as System.Collections.ICollection; n = n * 31 + (c == null ? 0 : c.Count) + (kv.Key == null ? 0 : kv.Key.GetHashCode()); }
            return n;
        }

        internal static void ResetStats() { TimeHits = TimeMisses = 0; ResetsSkipped = ResetsKept = 0; ColCalls = ColActiveTicks = ColEnableTicks = ColActiveChanged = ColEnableChanged = 0; }

        internal static string Summary()
        {
            string s = "";
            if (TimeHits + TimeMisses > 0) s += string.Format(" | 이미지 파일 시각: {0}번 중 디스크 {1}번", TimeHits + TimeMisses, TimeMisses);
            if (ResetsSkipped + ResetsKept > 0) s += string.Format(" | 재생 준비 장식 다시 설정: 건너뜀 {0}번, 함 {1}번", ResetsSkipped, ResetsKept);
            if (ColCalls > 0)
            {
                double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                s += string.Format(" | 충돌 상자 {0}번: 오브젝트 켜기/끄기 {1:F0}ms (바뀐 것 {2}), 충돌 상자 켜기/끄기 {3:F0}ms (바뀐 것 {4})", ColCalls, ColActiveTicks * f, ColActiveChanged, ColEnableTicks * f, ColEnableChanged);
            }
            return s;
        }
    }
}
