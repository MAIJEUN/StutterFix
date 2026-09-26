using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace StutterFix
{
    // 에디터 전환 줄이기: 편집으로 나가기, 에디터에서 죽고 다시 하기.
    //
    // 1) 장식 이미지 버리지 않기
    //    scnGame.ResetScene(편집으로 나가기, 죽고 다시 하기)은 ReloadAssets(force:false, reloadDecorations:false) 를 부른다.
    //    에디터에서는 여기서 MarkAllUnused -> (배경, 타일 이미지만 다시 "쓰는 중") -> Unload(onlyIfUnused) 가 돌아서
    //    장식 이미지가 전부 "안 씀" 으로 내려간다(Destroy). 곧바로 ResetScene 끝의 ResetDecorations 가 장식마다
    //    GetOrAddSprite 를 불러 같은 파일을 디스크에서 다시 읽고 푼다(메인 스레드 한 장씩, 미리 풀기 없이). (IL 확인)
    //    이 ReloadAssets 에서만 표시/내리기를 건너뛴다. 이미지는 그 판에 쓰던 것 그대로 남고, 파일이 바뀌었으면
    //    GetOrAddSprite 가 원래처럼 수정 시각을 비교해 다시 부른다. 맵 열기(force:true)와 재생 시작의 ReloadAssets 는 그대로.
    //    덤: 원래는 여기서 다시 부른 이미지가 원본 크기라, 큰 이미지 줄이기로 줄인 이미지도 편집으로 나가면 원본이 됐다.
    // 2) 에디터에서 죽고 다시 하기의 장식 다시 설정 한 번 줄이기
    //    scrController.ResetCustomLevel 은 에디터에서 한 번에 ResetScene -> scnGame.Play 를 한다. ResetScene 끝에서
    //    ResetDecorations 를 하고, Play 안의 FinishCustomLevelLoading 이 (에디터에서는) 또 ResetDecorations 를 한다.
    //    그 사이의 코드(카메라·지휘자·플레이어 되감기, 첫 타일 각도)는 장식을 읽지 않는다. 첫 번째를 건너뛴다.
    //    Play 가 중간에 멈추는 등 두 번째가 안 불렸으면 끝에서 원래 것을 대신 부른다.
    //    개발자용: 건너뜀 두 번, 안 건너뜀 두 번 차례로 하고 재생 준비가 끝난 순간 모든 장식 상태를 비교한다.
    // 3) 전환 시간 기록 (편집으로 나가기, 에디터 재생 시작, 죽고 다시 하기). 전환 때만 불린다.
    internal static class TransitionFix
    {
        internal static bool KeepImages = true;
        internal static bool SkipRestartReset = true;
        internal static long ImagesKept, ResetsSkipped, Fallbacks;
        private static bool keeping, inRestart, pending, skipThis;
        private static int devRestarts;
        private static FieldInfo spritesField;

        internal static void Install(Harmony h)
        {
            try
            {
                var reload = AccessTools.Method(typeof(scnGame), "ReloadAssets");
                var mark = AccessTools.Method(typeof(TextureManager), "MarkAllUnused");
                var unload = AccessTools.Method(typeof(TextureManager), "Unload");
                spritesField = AccessTools.Field(typeof(TextureManager), "customSprites");
                if (reload != null && mark != null && unload != null && reload.GetParameters().Length == 2)
                {
                    h.Patch(reload, prefix: new HarmonyMethod(typeof(TransitionFix), nameof(ReloadPrefix)), finalizer: new HarmonyMethod(typeof(TransitionFix), nameof(ReloadFinalizer)));
                    h.Patch(mark, prefix: new HarmonyMethod(typeof(TransitionFix), nameof(SkipWhileKeeping)));
                    h.Patch(unload, prefix: new HarmonyMethod(typeof(TransitionFix), nameof(SkipWhileKeeping)));
                }
                else Main.Entry.Logger.Log("[전환] 이미지 유지: 게임 코드 모양이 달라 끔");

                var co = AccessTools.Method(typeof(scrController), "ResetCustomLevel");
                var moveNext = co == null ? null : AccessTools.EnumeratorMoveNext(co);
                var reset = AccessTools.Method(typeof(scrDecorationManager), "ResetDecorations");
                if (moveNext != null && reset != null)
                {
                    h.Patch(moveNext, prefix: new HarmonyMethod(typeof(TransitionFix), nameof(RestartPrefix)), finalizer: new HarmonyMethod(typeof(TransitionFix), nameof(RestartFinalizer)));
                    h.Patch(reset, prefix: new HarmonyMethod(typeof(TransitionFix), nameof(ResetPrefix)));
                }
                else Main.Entry.Logger.Log("[전환] 다시 하기 장식 설정 줄이기: 게임 코드 모양이 달라 끔");

                foreach (var name in new[] { "SwitchToEditMode", "Play" })
                {
                    var m = AccessTools.Method(typeof(scnEditor), name);
                    if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(TransitionFix), nameof(TimePrefix)), finalizer: new HarmonyMethod(typeof(TransitionFix), nameof(TimeFinalizer)));
                }
                Main.Entry.Logger.Log("[전환] 설치");
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[전환] 설치 실패: " + ex.Message); }
        }

        // ── 1) 이미지 유지 ──
        public static void ReloadPrefix(scnGame __instance, bool force, bool reloadDecorations)
        {
            keeping = KeepImages && !force && !reloadDecorations && ADOBase.isLevelEditor;
            if (!keeping) return;
            try { var d = spritesField == null ? null : spritesField.GetValue(__instance.imgHolder) as IDictionary; if (d != null) ImagesKept += d.Count; } catch { }
        }
        public static Exception ReloadFinalizer(Exception __exception) { keeping = false; return __exception; }
        public static bool SkipWhileKeeping() { return !keeping; }

        // ── 2) 죽고 다시 하기 ──
        public static void RestartPrefix(out long __state)
        {
            __state = Stopwatch.GetTimestamp();
            inRestart = ADOBase.isLevelEditor; pending = false;
            skipThis = SkipRestartReset && inRestart && (!Edition.Dev || (devRestarts++ / 2) % 2 == 1);
            resetsAtStart = SceneReset.Count;
        }
        private static long resetsAtStart;
        public static bool ResetPrefix()
        {
            // ResetScene 안에서 부른 첫 번째만 (Play 쪽 두 번째는 원래대로 돌면서 미뤄 둔 것을 대신한다)
            if (inRestart && skipThis && SceneReset.Resetting && !pending) { pending = true; ResetsSkipped++; return false; }
            pending = false;
            return true;
        }
        public static Exception RestartFinalizer(Exception __exception, long __state)
        {
            bool was = inRestart;
            inRestart = false;
            if (pending)
            {
                pending = false; Fallbacks++;
                try { var mgr = scrDecorationManager.instance; if (mgr != null) mgr.ResetDecorations(); }
                catch (Exception ex) { Main.Entry.Logger.Log("[전환] 장식 다시 설정(대신 부름) 실패: " + ex.Message); }
            }
            if (SceneReset.Count != resetsAtStart)   // 이번 MoveNext 에서 실제로 되돌렸다 (게임 화면은 닦기 전환 뒤)
            {
                Log(was ? "에디터에서 다시 하기" : "다시 하기", __state, was && skipThis ? " (장식 다시 설정 1번 건너뜀)" : "");
                if (Edition.Dev && was) Verify(skipThis);
            }
            return __exception;
        }

        // ── 3) 시간 ──
        public static void TimePrefix(out long __state) { __state = Stopwatch.GetTimestamp(); }
        public static Exception TimeFinalizer(Exception __exception, long __state, MethodBase __originalMethod)
        {
            Log(__originalMethod.Name == "Play" ? "에디터 재생 시작" : "편집으로 나가기", __state, "");
            return __exception;
        }
        private static long lastKept;
        private static void Log(string what, long t0, string extra)
        {
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (ImagesKept != lastKept) { extra += " (이미지 " + (ImagesKept - lastKept) + "장 그대로 둠)"; lastKept = ImagesKept; }
            Main.Entry.Logger.Log(string.Format("[전환] {0} {1:F0}ms{2}", what, ms, extra));
        }

        // (개발자용) 건너뜀/안 건너뜀 재생 준비 끝 상태 비교
        private static long[] lastSnap; private static bool lastSkipped; private static long lastKey;
        private static void Verify(bool skipped)
        {
            try
            {
                var all = LoadFix.AllDecorations();
                if (all == null) return;
                var snap = LoadFix.Snapshot(all);
                long key = unchecked(all.Count * 1000003L + GCS.checkpointNum * 31L + LoadFix.TagHash());
                if (lastSnap != null && lastKey == key && lastSnap.Length == snap.Length)
                {
                    string detail;
                    int diff = LoadFix.Compare(all, lastSnap, snap, out detail);
                    string kind = lastSkipped == skipped ? (skipped ? "건너뜀끼리(기준선)" : "안 건너뜀끼리(기준선)") : "건너뜀 대 안 건너뜀";
                    Main.Entry.Logger.Log(string.Format("[전환 검증] {0}: 장식 {1}개 중 다시 하기 뒤 상태가 다른 것 {2}개{3}", kind, all.Count, diff, detail));
                }
                lastSnap = snap; lastSkipped = skipped; lastKey = key;
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[전환 검증] 실패: " + ex.Message); }
        }
    }
}
