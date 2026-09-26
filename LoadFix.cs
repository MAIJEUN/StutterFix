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
                // 에디터 클릭용 충돌 상자를 끌 때 넣은 반대 순서로 (아래 ToggleReverse 설명)
                var tog = AccessTools.Method(typeof(scrDecorationManager), "ToggleClickableBoxColliderForLevelEditor");
                var sc = AccessTools.Method(typeof(scrDecoration), "SetCollider", new[] { typeof(bool) });
                if (tog != null && sc != null && tog.GetParameters().Length == 1)
                {
                    setCollider = AccessTools.MethodDelegate<Action<scrDecoration, bool>>(sc);   // 가상 호출 (scrObjectDecoration 도 원래대로)
                    h.Patch(tog, prefix: new HarmonyMethod(typeof(LoadFix), nameof(ToggleReverse)));
                }
                if (Edition.Dev)
                {
                    var apply = AccessTools.Method(typeof(Texture2D), "Apply", new[] { typeof(bool), typeof(bool) });
                    if (apply != null) h.Patch(apply, prefix: new HarmonyMethod(typeof(LoadFix), nameof(ApplyPrefix)), postfix: new HarmonyMethod(typeof(LoadFix), nameof(ApplyPostfix)));
                    foreach (var m in typeof(Texture2D).GetMethods())
                        if (m.Name == "Compress") h.Patch(m, prefix: new HarmonyMethod(typeof(LoadFix), nameof(ApplyPrefix)), postfix: new HarmonyMethod(typeof(LoadFix), nameof(CompressPostfix)));
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
        // 에디터 클릭용 충돌 상자 끄기 (scrDecorationManager.ToggleClickableBoxColliderForLevelEditor)
        // 원래: 장식 목록 앞에서부터 SetCollider(값) (IL). 재생 시작 때 끄기가 Arche 에서 2.2~2.7초, 편집 복귀 때 켜기는 0.02초.
        // 오브젝트를 켜 둔 채 충돌 상자만 꺼도(enabled) 똑같이 2.4초가 들어서, 비용은 "물리에서 충돌 상자 빼기" 자체다. 켜기와 100배 차이가
        // 나는 것은 물리 엔진(Box2D)이 리지드바디 없는 충돌 상자를 한 고정 몸체의 목록에 넣고(맨 앞에 추가), 뺄 때 목록을 앞에서부터 찾기
        // 때문으로 보인다: 넣은 순서대로 빼면 매번 목록 끝까지 뒤진다(2만 8천 x 2만 8천). 끌 때만 목록을 뒤에서부터 돌면 매번 맨 앞에서
        // 찾는다. 부르는 함수와 값은 원래와 같고(가상 호출이라 scrObjectDecoration 도 자기 것), 순서만 거꾸로다. 켤 때는 원래대로 둔다.
        internal static bool ReverseToggle = true;
        private static Action<scrDecoration, bool> setCollider;
        internal static long ToggleTicks;
        public static bool ToggleReverse(bool __0)
        {
            if (!ReverseToggle || __0 || setCollider == null) return true;
            var mgr = scrDecorationManager.instance;
            var all = mgr == null ? null : allRef(mgr);
            if (all == null) return true;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            for (int i = all.Count - 1; i >= 0; i--) setCollider(all[i], false);   // 원래는 foreach (null 이면 원래도 예외)
            ToggleTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0; ColCalls += all.Count;
            return false;
        }

        // (개발자용) 이미지 올리기(Texture2D.Apply) 비용: 크기와 시간
        [ThreadStatic] private static long applyT0;
        internal static long ApplyN, ApplyTicks, ApplyBytes; internal static string ApplySlow = ""; private static double applySlowMs;
        public static void ApplyPrefix() { applyT0 = System.Diagnostics.Stopwatch.GetTimestamp(); }
        public static void ApplyPostfix(Texture2D __instance)
        {
            long dt = System.Diagnostics.Stopwatch.GetTimestamp() - applyT0;
            ApplyN++; ApplyTicks += dt;
            long bytes = 0; try { bytes = (long)__instance.width * __instance.height * 4; } catch { }
            ApplyBytes += bytes;
            double ms = dt * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms > applySlowMs) { applySlowMs = ms; ApplySlow = string.Format("{0}x{1} {2} {3:F0}ms", __instance.width, __instance.height, __instance.format, ms); }
            string k = __instance.format.ToString(); double v; applyByFormat.TryGetValue(k, out v); applyByFormat[k] = v + ms;
            int c; applyCountByFormat.TryGetValue(k, out c); applyCountByFormat[k] = c + 1;
        }
        private static readonly Dictionary<string, double> applyByFormat = new Dictionary<string, double>();
        private static readonly Dictionary<string, int> applyCountByFormat = new Dictionary<string, int>();
        internal static long CompressN; internal static double CompressMs;
        public static void CompressPostfix() { CompressN++; CompressMs += (System.Diagnostics.Stopwatch.GetTimestamp() - applyT0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; }
        internal static string ApplySummary()
        {
            if (ApplyN == 0) return "";
            double ms = ApplyTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            string s = string.Format("이미지 올리기 {0}장 {1}MB {2:F0}ms ({3:F0}MB/s), 가장 느린 것 {4}", ApplyN, ApplyBytes / 1048576, ms, ms > 0 ? ApplyBytes / 1048576.0 / (ms / 1000.0) : 0, ApplySlow);
            s += " | 형식별:"; foreach (var kv in applyByFormat) s += string.Format(" {0} {1}장 {2:F0}ms", kv.Key, applyCountByFormat[kv.Key], kv.Value);
            if (CompressN > 0) s += string.Format(" | 압축(Compress) {0}번 {1:F0}ms", CompressN, CompressMs);
            ApplyN = ApplyTicks = ApplyBytes = 0; ApplySlow = ""; applySlowMs = 0; applyByFormat.Clear(); applyCountByFormat.Clear(); CompressN = 0; CompressMs = 0;
            return s;
        }

        // ── 3) 에디터 재생 시작 때 장식 다시 설정 한 번 줄이기 ──
        // scnEditor.Play 는 scnGame.ReloadAssets 안에서 장식 전체를 한 번 다시 설정(ResetDecorations)하고, 곧이어 scnGame.Play 의
        // FinishCustomLevelLoading 에서 또 한 번 한다(Arche: 1.8초 + 1.9초). 첫 번째는 "쓰는 이미지 표시"(MarkAllUnused -> 설정 중
        // GetOrAddSprite -> Unload 로 안 쓰는 이미지 내리기)와 태그 목록 다시 만들기를 겸하는데, 그 사이에 태그 목록을 읽는 곳은
        // 필터 효과 준비(ffxSetFilterAdvancedPlus.Setup) 정도다. 지난 다시 설정 뒤로 장식 데이터(목록, 순서, 각 장식 이벤트의 모든 값)가
        // 하나도 바뀌지 않았으면 태그 목록도 그때와 똑같으므로, 첫 번째 다시 설정과 짝인 표시/내리기를 건너뛴다(안 쓰는 이미지는 다음
        // 맵 열기 때 내려간다). 조금이라도 바뀌었으면 원래대로 한다.
        // 개발자용: 건너뛴 재생과 안 건너뛴 재생을 번갈아 하고, 재생 준비가 끝난 순간 모든 장식의 상태를 비교한다.
        // 검증(2026-09-26, Arche): 건너뛰지 않은 재생끼리도 안 보이는 장식 73개의 실제 위치가 매번 달랐고(원래 흔들림), 건너뛴 재생과의 차이도
        // 똑같은 73개(실제 위치)뿐이었다. 태그 목록, 그림, 색, 보임, 히트박스는 모두 같았다. 건너뛰기가 만든 차이는 0.
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
                skipping = same && (!Edition.Dev || (devPlays++ / 2) % 2 == 1);   // 개발자용은 안 건너뜀 두 번, 건너뜀 두 번 차례로 (계속 비교)
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
                    object raw = dataField.GetValue(ev);
                    long eh;
                    var typed = raw as Dictionary<string, object>;   // 박싱 없이 (Arche 2만 8천 개 x 값 30개)
                    if (typed != null)
                    {
                        eh = typed.Count;
                        foreach (var kv in typed) eh += (long)(kv.Key == null ? 0 : kv.Key.GetHashCode()) * 31 + ValueHash(kv.Value);   // 순서와 상관없게 더한다
                    }
                    else
                    {
                        var data = raw as System.Collections.IDictionary;
                        if (data == null) continue;
                        eh = data.Count;
                        foreach (System.Collections.DictionaryEntry kv in data) eh += (long)(kv.Key == null ? 0 : kv.Key.GetHashCode()) * 31 + ValueHash(kv.Value);
                    }
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
        // 장식마다 부분별 해시: 0 기준 위치·회전·크기, 1 색·불투명도, 2 보임, 3 히트박스, 4 실제 위치, 5 실제 회전·크기, 6 그림(스프라이트·켜짐·색)
        private const int Parts = 7;
        private static readonly string[] PartName = { "기준 위치/회전/크기", "색/불투명도", "보임", "히트박스", "실제 위치", "실제 회전/크기", "그림" };
        private static long[] lastSnap; private static long lastTags; private static long lastSnapFp; private static bool lastSnapSkipped;
        internal static long VerifyN, VerifyDiffs;
        private static void VerifyAfterPlay()
        {
            try
            {
                var mgr = scrDecorationManager.instance;
                var all = mgr == null ? null : allRef(mgr);
                if (all == null || !haveFp) return;
                var snap = Snapshot(all);
                long tags = TagCount(mgr);
                // 같은 장식 데이터로 연 두 재생끼리 비교: 건너뜀/안 건너뜀이 섞인 쌍과, 같은 방식끼리의 쌍(원래 흔들림 기준선)
                if (lastSnap != null && lastSnap.Length == snap.Length && lastSnapFp == lastFp)
                {
                    string detail;
                    int diff = Compare(all, lastSnap, snap, out detail);
                    string kind = lastSnapSkipped == lastPlaySkipped ? (lastPlaySkipped ? "건너뜀끼리(기준선)" : "안 건너뜀끼리(기준선)") : "건너뜀 대 안 건너뜀";
                    if (lastSnapSkipped != lastPlaySkipped) { VerifyN++; VerifyDiffs += diff; }
                    Main.Entry.Logger.Log(string.Format("[로딩 검증] {0}: 장식 {1}개 중 재생 준비 끝 상태가 다른 것 {2}개{3}{4}", kind, all.Count, diff,
                        detail, tags != lastTags ? ", 태그 목록 다름" : ", 태그 목록 같음"));
                }
                lastSnap = snap; lastTags = tags; lastSnapFp = lastFp; lastSnapSkipped = lastPlaySkipped;
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[로딩 검증] 실패: " + ex.Message); }
        }

        // 장식마다 부분별 해시 (TransitionFix 검증도 쓴다)
        internal static long[] Snapshot(List<scrDecoration> all)
        {
            var snap = new long[all.Count * Parts];
            for (int i = 0; i < all.Count; i++)
            {
                var d = all[i];
                if ((object)d == null) continue;
                unchecked
                {
                    int o = i * Parts;
                    snap[o] = pivotPosRef(d).GetHashCode() * 31L + rotRef(d).GetHashCode() * 7L + scaleRef(d).GetHashCode();
                    snap[o + 1] = colRef2(d).GetHashCode() * 31L + opaRef(d).GetHashCode();
                    snap[o + 2] = d.GetVisible() ? 1 : 0;
                    snap[o + 3] = d.hitbox.GetHashCode();
                    var t = d.transform; snap[o + 4] = t.position.GetHashCode(); snap[o + 5] = t.rotation.GetHashCode() * 31L + t.lossyScale.GetHashCode();
                    long g = 0;
                    var v = d as scrVisualDecoration;
                    if (v != null) foreach (var r in v.GetComponentsInChildren<SpriteRenderer>(true)) { g = g * 31 + (r.sprite == null ? 0 : r.sprite.GetInstanceID()); g = g * 31 + (r.enabled ? 1 : 0); g = g * 31 + r.color.GetHashCode(); }
                    snap[o + 6] = g;
                }
            }
            return snap;
        }

        // 다른 장식 수. detail: " (부분별 수) [처음 몇 개]"
        internal static int Compare(List<scrDecoration> all, long[] a, long[] b, out string detail)
        {
            int diff = 0; var byPart = new int[Parts]; var sb = new System.Text.StringBuilder();
            for (int i = 0; i < all.Count && (i + 1) * Parts <= a.Length && (i + 1) * Parts <= b.Length; i++)
            {
                bool any = false;
                for (int p = 0; p < Parts; p++) if (b[i * Parts + p] != a[i * Parts + p]) { byPart[p]++; any = true; }
                if (!any) continue;
                diff++;
                if (diff <= 6)
                {
                    var d = all[i]; string tag = "";
                    try { var ev = d.sourceLevelEvent; if (ev != null) tag = Convert.ToString(ev["tag"]); } catch { }
                    sb.AppendFormat(" [#{0} {1} 태그 '{2}' 보임 {3}:", i, d.GetType().Name, tag, d.GetVisible());
                    for (int p = 0; p < Parts; p++) if (b[i * Parts + p] != a[i * Parts + p]) sb.Append(" " + PartName[p]);
                    sb.Append("]");
                }
            }
            var parts = new List<string>(); for (int p = 0; p < Parts; p++) if (byPart[p] > 0) parts.Add(PartName[p] + " " + byPart[p]);
            detail = (parts.Count > 0 ? " (" + string.Join(", ", parts.ToArray()) + ")" : "") + sb.ToString();
            return diff;
        }
        internal static List<scrDecoration> AllDecorations() { var mgr = scrDecorationManager.instance; return mgr == null ? null : allRef(mgr); }
        internal static long TagHash() { var mgr = scrDecorationManager.instance; return mgr == null ? 0 : TagCount(mgr); }
        private static readonly System.Reflection.FieldInfo tagField = AccessTools.Field(typeof(scrDecorationManager), "taggedDecorations");
        private static long TagCount(scrDecorationManager mgr)
        {
            var d = tagField == null ? null : tagField.GetValue(mgr) as System.Collections.IDictionary;
            if (d == null) return -1;
            long n = d.Count;
            foreach (System.Collections.DictionaryEntry kv in d) { var c = kv.Value as System.Collections.ICollection; n = n * 31 + (c == null ? 0 : c.Count) + (kv.Key == null ? 0 : kv.Key.GetHashCode()); }
            return n;
        }

        internal static void ResetStats() { TimeHits = TimeMisses = 0; ResetsSkipped = ResetsKept = 0; ColCalls = ColActiveTicks = ColEnableTicks = ColActiveChanged = ColEnableChanged = 0; ToggleTicks = 0; }

        internal static string Summary()
        {
            string s = "";
            if (TimeHits + TimeMisses > 0) s += string.Format(" | 이미지 파일 시각: {0}번 중 디스크 {1}번", TimeHits + TimeMisses, TimeMisses);
            if (ResetsSkipped + ResetsKept > 0) s += string.Format(" | 재생 준비 장식 다시 설정: 건너뜀 {0}번, 함 {1}번", ResetsSkipped, ResetsKept);
            if (ColCalls > 0)
            {
                double f = 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                s += string.Format(" | 충돌 상자 끄기(뒤에서부터) {0}개 {1:F0}ms", ColCalls, ToggleTicks * f);
            }
            return s;
        }
    }
}
