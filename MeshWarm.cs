using System;
using System.Collections.Generic;
using System.Diagnostics;
using ADOFAI;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 타일 색 바꾸기 효과가 곡 중에 만들 타일 메시를 재생 준비 때 미리 만들어 두기.
    //
    // 2026-10-04 Windflower(게임 화면) 94.7초·99.0초 42ms: 엔진 단계 LateUpdate 33ms = scrController.LateUpdate 의 FloorMesh.UpdateAllRequired.
    // 타일 색 바꾸기(ffxRecolorFloorPlus)가 스타일을 바꾸면 타일마다 SetTrackStyle 이 메시 길이·폭을 바꾸고, 다음 LateUpdate 에서 메시를
    // 다시 고른다. 메시는 "각도0,각도1,폭,길이,곡선점수" 문자열 키로 캐시되는데(FloorMesh.cache, 지우지 않음), 처음 보는 키는 새로 만든다
    // (하나 약 45us). 99.0초: 1,102개 중 604개를 새로 만들어 31.8ms. 같은 1,102개를 다시 고를 때(모두 캐시)는 3.7ms.
    //
    // 재생 준비(ApplyEventsToFloors) 끝에, 붙은 색 바꾸기 효과 중 SetTrackStyle 을 부르는 것(스타일·타일 그림·그림 배율 사용)의 범위 타일마다
    // 곡 중에 생길 키를 게임과 같은 계산으로 구해(UpdateAngle 의 각도·곡선점수, SetTrackStyle 의 길이·폭, 속성의 "거의 같으면 안 바꿈"까지)
    // 캐시에 없는 것만 그 타일 자신의 FloorMesh 로 만든다(값 5개와 키를 잠깐 바꿔 GetPositions -> GenerateMesh, 끝나면 되돌림. 속성 설정
    // 함수를 거치지 않으므로 "다시 고를 타일" 목록에는 안 들어간다). 곡 중에 그 타일이 만들었을 것과 같은 객체·같은 값이다.
    //   숨긴 FloorMesh 하나를 돌려쓰지 않는 까닭: GetPositions 는 중간값 curvatureSliceAngle 을 모서리 각이 120도 미만일 때만 새로 쓰고,
    //   경로가 겹치는 타일(pathOverlaps)에서는 그 전에 읽는다(cornerCenterApothem). 120~180도이면서 겹치는 타일(폭을 키운 맵)은 그 객체가
    //   앞서 남긴 값을 쓴다. 이 값은 각도와 곡선점수로만 정해져 같은 타일이면 늘 같지만, 돌려쓰는 객체에는 다른 타일의 값이 남아 있다.
    // 키에 없는 값(육각형, 인셋)이 타일마다 다르면 하지 않는다(어느 타일이 먼저 만드느냐에 따라 내용이 달라질 수 있어서). 보석 스타일(sprite 키)과
    // 자유 이동 타일도 건너뛴다.
    // 개발자용 검증: 미리 만든 키를 곡 중에 타일이 처음 고를 때, 미리 만든 것을 캐시에서 잠깐 빼 게임이 원래대로 만들게 하고
    //   정점·삼각형·UV·색·충돌 다각형을 비교한다(캐시에는 게임이 만든 것이 남는다).
    internal static class MeshWarm
    {
        internal static bool Enabled = true;
        internal static long Made;
        internal static long VerifyN, VerifyDiff;
        private static readonly HashSet<string> warmed = new HashSet<string>();     // (개발자용) 미리 만든 키
        private static readonly HashSet<string> verified = new HashSet<string>();
        private static readonly AccessTools.FieldRef<FloorMesh, float> a0Ref = AccessTools.FieldRefAccess<FloorMesh, float>("angle0");
        private static readonly AccessTools.FieldRef<FloorMesh, float> a1Ref = AccessTools.FieldRefAccess<FloorMesh, float>("angle1");
        private static readonly AccessTools.FieldRef<FloorMesh, float> wRef = AccessTools.FieldRefAccess<FloorMesh, float>("width");
        private static readonly AccessTools.FieldRef<FloorMesh, float> lRef = AccessTools.FieldRefAccess<FloorMesh, float>("length");
        private static readonly AccessTools.FieldRef<FloorMesh, int> cpRef = AccessTools.FieldRefAccess<FloorMesh, int>("curvaturePoints");
        private static readonly AccessTools.FieldRef<FloorMesh, bool> sprRef = AccessTools.FieldRefAccess<FloorMesh, bool>("isSprite");
        private static readonly AccessTools.FieldRef<FloorMesh, bool> hexRef = AccessTools.FieldRefAccess<FloorMesh, bool>("isHexagon");
        private static readonly AccessTools.FieldRef<FloorMesh, bool> finRef = AccessTools.FieldRefAccess<FloorMesh, bool>("useFInset2");
        private delegate void GetPos(FloorMesh m, float a0, float a1, float w, float l, int cp);
        private static GetPos getPositions;
        private static bool fin;   // 이 맵 타일들의 인셋 값 (모두 같을 때만 미리 만든다)
        private static readonly HashSet<long> seen = new HashSet<long>();
        private static List<scrFloor> lastFloors; private static long lastFp; private static int lastCache;
        internal static void ForgetLevel() { lastFloors = null; lastFp = 0; }

        internal static void Install(Harmony h)
        {
            var gp = AccessTools.Method(typeof(FloorMesh), "GetPositions", new[] { typeof(float), typeof(float), typeof(float), typeof(float), typeof(int) });
            var aetf = AccessTools.Method(typeof(scnGame), "ApplyEventsToFloors", new[] { typeof(List<scrFloor>), typeof(LevelData), typeof(scrLevelMaker), typeof(List<LevelEvent>) });
            if (gp == null || aetf == null) { Main.Entry.Logger.Log("[타일 메시] 게임 코드 모양이 달라 미리 만들기 안 함"); return; }
            getPositions = AccessTools.MethodDelegate<GetPos>(gp);
            h.Patch(aetf, postfix: new HarmonyMethod(typeof(MeshWarm), nameof(Postfix)) { priority = Priority.Last });
            if (Edition.Dev)
            {
                var um = AccessTools.Method(typeof(FloorMesh), "UpdateMesh");
                if (um != null) h.Patch(um, prefix: new HarmonyMethod(typeof(MeshWarm), nameof(VerifyPre)), postfix: new HarmonyMethod(typeof(MeshWarm), nameof(VerifyPost)));
            }
        }

        // ── (개발자용) 미리 만든 메시를 게임이 만든 것과 비교 ──
        public static void VerifyPre(FloorMesh __instance, out FloorMesh.MeshCache __state)
        {
            __state = null;
            try
            {
                if (warmed.Count == 0 || sprRef(__instance)) return;
                string key = string.Format("{0},{1},{2},{3},{4}", a0Ref(__instance), a1Ref(__instance), wRef(__instance), lRef(__instance), cpRef(__instance));
                FloorMesh.MeshCache mc;
                if (!warmed.Contains(key) || !verified.Add(key) || !FloorMesh.cache.TryGetValue(key, out mc)) return;
                __state = mc;
                FloorMesh.cache.Remove(key);   // 게임이 원래대로 새로 만든다
            }
            catch { __state = null; }
        }
        public static void VerifyPost(FloorMesh __instance, FloorMesh.MeshCache __state)
        {
            if (__state == null) return;
            try
            {
                FloorMesh.MeshCache now;
                if (__instance.cacheKey == null || !FloorMesh.cache.TryGetValue(__instance.cacheKey, out now)) return;
                VerifyN++;
                string d = Diff(__state, now);
                if (d == null) return;
                VerifyDiff++;
                if (VerifyDiff <= 10) Main.Entry.Logger.Log("[타일 메시 검증] 미리 만든 것과 게임이 만든 것이 다름: 키 " + __instance.cacheKey + " (" + d + ")");
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[타일 메시 검증] 실패: " + ex.Message); }
        }
        private static string Diff(FloorMesh.MeshCache a, FloorMesh.MeshCache b)
        {
            if (a.mesh == null || b.mesh == null) return "메시 없음";
            if (!Same(a.mesh.vertices, b.mesh.vertices)) return "정점";
            if (!Same(a.mesh.triangles, b.mesh.triangles)) return "삼각형";
            if (!Same(a.mesh.uv, b.mesh.uv)) return "UV";
            if (!Same(a.mesh.uv2, b.mesh.uv2)) return "UV2";
            if (!Same(a.mesh.colors, b.mesh.colors)) return "색";
            if (!Same(a.polygon, b.polygon)) return "충돌 다각형";
            return null;
        }
        private static bool Same<T>(T[] x, T[] y) where T : IEquatable<T>
        {
            if (x == null || y == null) return x == y;
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++) if (!x[i].Equals(y[i])) return false;
            return true;
        }

        public static void Postfix()
        {
            if (!Enabled) return;
            try { Run(); } catch (Exception ex) { Main.Entry.Logger.Log("[타일 메시] 미리 만들기 실패: " + ex.Message); }
        }

        private static void Run()
        {
            var lm = ADOBase.lm;
            var ctrl = ADOBase.controller;
            if (lm == null || ctrl == null || lm.listFloors == null) { if (Edition.Dev) Main.Entry.Logger.Log("[타일 메시] 레벨 없음"); return; }
            var floors = lm.listFloors;
            // 키에 없는 값(육각형, 인셋)이 타일마다 다르면 같은 키를 어느 타일이 먼저 만드느냐에 따라 내용이 달라진다.
            // 육각형 타일이 있으면 하지 않고, 인셋은 모든 타일이 같은 값일 때 그 값으로 만든다.
            int finT = 0, finF = 0;
            for (int i = 0; i < floors.Count; i++)
            {
                var fmr = floors[i] == null ? null : floors[i].floorRenderer as FloorMeshRenderer;
                if (fmr == null || fmr.floorMesh == null) continue;
                if (hexRef(fmr.floorMesh)) { if (Edition.Dev) Main.Entry.Logger.Log("[타일 메시] 육각형 타일이 있어 미리 만들기 안 함 (타일 " + i + ")"); return; }
                if (finRef(fmr.floorMesh)) finT++; else finF++;
            }
            if (finT > 0 && finF > 0) { if (Edition.Dev) Main.Entry.Logger.Log("[타일 메시] 인셋 값이 타일마다 달라 미리 만들기 안 함 (켬 " + finT + ", 끔 " + finF + ")"); return; }
            fin = finT > 0;
            // 같은 타일 목록에 같은 효과 구성이면(에디터 재생·나가기를 되풀이) 키는 이미 다 캐시에 있다. 키 문자열을 다시 만들지 않는다.
            long fp = floors.Count;
            for (int fi = 0; fi < floors.Count; fi++)
            {
                var fl = floors[fi];
                if (fl == null || fl.plusEffects == null) continue;
                for (int k = 0; k < fl.plusEffects.Count; k++)
                {
                    var rc = fl.plusEffects[k] as ffxRecolorFloorPlus;
                    if ((object)rc == null) continue;
                    unchecked { fp = fp * 31 + rc.start; fp = fp * 31 + rc.end; fp = fp * 31 + rc.gapLength; fp = fp * 31 + (int)rc.style; fp = fp * 31 + (rc.usedStyle ? 1 : 0) + (rc.usedTrackTexture ? 2 : 0) + (rc.usedTrackTextureScale ? 4 : 0); }
                }
            }
            if (ReferenceEquals(floors, lastFloors) && fp == lastFp && FloorMesh.cache.Count >= lastCache) return;
            long t0 = Stopwatch.GetTimestamp();
            Vector2 dim = ctrl.baseFloorDimensions;
            int effects = 0, tiles = 0, made = 0;
            seen.Clear();
            for (int fi = 0; fi < floors.Count; fi++)
            {
                var fl = floors[fi];
                if (fl == null || fl.plusEffects == null) continue;
                for (int k = 0; k < fl.plusEffects.Count; k++)
                {
                    var rc = fl.plusEffects[k] as ffxRecolorFloorPlus;
                    if ((object)rc == null) continue;
                    bool calls = rc.usedStyle || (rc.usedTrackTexture && !string.IsNullOrEmpty(rc.trackTexture)) || rc.usedTrackTextureScale;
                    if (!calls || rc.style == TrackStyle.Gems) continue;
                    effects++;
                    int s = Math.Min(rc.start, rc.end), e = Math.Max(rc.start, rc.end);
                    if (s < 0) s = 0;
                    for (int i = s; i <= e && i < floors.Count; i += 1 + Math.Max(0, rc.gapLength))
                    {
                        // 같은 타일·같은 스타일은 키가 같다(키 문자열 만들기가 비싸서 한 번만). PLUM MEGAMIX: 효과 663개가 8만 8천 칸
                        if (!seen.Add(((long)i << 8) | (uint)(int)rc.style)) continue;
                        tiles++;
                        if (Warm(floors[i], rc.style, dim)) made++;
                    }
                }
            }
            Made += made;
            lastFloors = floors; lastFp = fp; lastCache = FloorMesh.cache.Count;
            if (made > 0 || Edition.Dev)
                Main.Entry.Logger.Log(string.Format("[타일 메시] 색 바꾸기 효과 {0}개, 타일·스타일 {1}가지 -> 새로 미리 만든 메시 {2}개 {3:F0}ms (캐시 {4}개)",
                    effects, tiles, made, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency, FloorMesh.cache.Count)
                    + (Edition.Dev ? string.Format(" | 검증: 지금까지 곡 중에 쓰인 미리 만든 메시 {0}개 중 게임이 만든 것과 다른 것 {1}개", VerifyN, VerifyDiff) : ""));
        }

        // 곡 중 RecolorFloor 가 이 타일에 하는 것: UpdateAngle(false) -> SetTrackStyle(style) -> (다음 LateUpdate) UpdateMesh
        private static bool Warm(scrFloor fl, TrackStyle style, Vector2 dim)
        {
            if (fl == null || fl.freeroamGenerated || fl.freeroamArea != null) return false;
            var fmr = fl.floorRenderer as FloorMeshRenderer;
            if (fmr == null || fmr.floorMesh == null) return false;
            var fm = fmr.floorMesh;
            // UpdateAngle: SetAngle(num, num2) -> _angle0/_angle1 (거의 같으면 그대로), _curvaturePoints
            float n0 = (MathF.PI / 2f - (float)fl.entryangle) % (MathF.PI * 2f);
            float n1 = (MathF.PI / 2f - (float)fl.exitangle) % (MathF.PI * 2f);
            float ang0 = (n0 + 0f) * 57.29578f;
            float ang1 = n1 * 57.29578f;
            float a0 = Mathf.Approximately(ang0, a0Ref(fm)) ? a0Ref(fm) : ang0;
            float a1 = Mathf.Approximately(ang1, a1Ref(fm)) ? a1Ref(fm) : ang1;
            int cp = fl.midSpin ? 3 : 40;
            // SetTrackStyle 의 길이·폭
            float x = dim.x, y = dim.y;
            float num = (x + 0.03f) * fl.lengthMult;
            float width = (y + 0.0375f) * fl.widthMult;
            switch (style)
            {
                case TrackStyle.Basic: break;
                case TrackStyle.Minimal: num /= fl.lengthMult; num -= 0.03f; num *= fl.lengthMult; break;
                case TrackStyle.Neon: break;
                case TrackStyle.NeonLight: break;
                default: num = x * fl.lengthMult; width = y * fl.widthMult; break;
            }
            float len = Mathf.Approximately(num, lRef(fm)) ? lRef(fm) : num;
            float wid = Mathf.Approximately(width, wRef(fm)) ? wRef(fm) : width;
            string key = string.Format("{0},{1},{2},{3},{4}", a0, a1, wid, len, cp);   // FloorMesh.UpdateMesh 와 같은 모양
            if (FloorMesh.cache.ContainsKey(key)) return false;
            // 이 타일 자신의 FloorMesh 로 만든다: 값과 키를 잠깐 바꾸고(필드 직접, "다시 고를 타일" 목록에 안 들어감) 끝나면 되돌린다
            float s0 = a0Ref(fm), s1 = a1Ref(fm), sw = wRef(fm), sl = lRef(fm); int scp = cpRef(fm); bool sspr = sprRef(fm); string skey = fm.cacheKey;
            try
            {
                a0Ref(fm) = a0; a1Ref(fm) = a1; wRef(fm) = wid; lRef(fm) = len; cpRef(fm) = cp; sprRef(fm) = false;
                fm.cacheKey = key;
                getPositions(fm, a0 * (MathF.PI / 180f), a1 * (MathF.PI / 180f), wid, len, cp);
                fm.GenerateMesh();
            }
            finally
            {
                a0Ref(fm) = s0; a1Ref(fm) = s1; wRef(fm) = sw; lRef(fm) = sl; cpRef(fm) = scp; sprRef(fm) = sspr; fm.cacheKey = skey;
            }
            bool ok = FloorMesh.cache.ContainsKey(key);
            if (ok && Edition.Dev) warmed.Add(key);
            return ok;
        }

        internal static void Release()
        {
            lastFloors = null; warmed.Clear(); verified.Clear();
        }
    }
}
