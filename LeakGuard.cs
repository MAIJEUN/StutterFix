using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 게임 메모리 누수 막기.
    //
    // 1) 사용자 지정 FPS 효과 화면 버퍼 (scrCamera.SetCustomFrameRate, IL 확인)
    //    켤 때마다 화면 크기 RenderTexture 를 새로 만들어 사각형(quad) 텍스처로 끼우는데, 이미 켜져 있을 때 또 켜면 이전 버퍼를 풀지 않는다
    //    (3440x1440 에서 한 번에 약 40MB 그래픽 메모리). 끌 때는 끼워져 있던 텍스처를 Release 만 하고 없애지 않으며, 켜진 적이 없어도
    //    게임 화면 버퍼(camRT) 자체를 Release 해서 다음 프레임에 다시 만들게 한다. scnGame.ResetScene 이 재시작마다 끄기를 부른다.
    //    -> 켤 때 이전 버퍼를 풀고 없앤다. 끌 때 끼워진 것이 camRT 면 Release 없이 원래대로 두고, 따로 만든 버퍼면 풀고 없앤다.
    //    화면 결과는 같다(끼워지는 텍스처, 필드 값 모두 원래와 같음).
    // 2) (개발자용) 누수 확인: 맵을 열 때와 장면이 바뀐 뒤 2초에, 남아 있는 텍스처·화면 버퍼·머티리얼·메시·오디오를 종류별·이름별로 세어
    //    지난번보다 늘어난 것을 로그에 남긴다. 맵을 여러 번 열고 닫았을 때 계속 늘어나는 것이 누수다.
    internal static class LeakGuard
    {
        internal static bool Enabled = true;
        internal static long FpsBuffersFreed, CamRTReleaseSkipped;
        private static readonly AccessTools.FieldRef<scrCamera, RenderTexture> camRTRef = AccessTools.FieldRefAccess<scrCamera, RenderTexture>("camRT");
        private static readonly AccessTools.FieldRef<scrCamera, MeshRenderer> quadMeshRef = AccessTools.FieldRefAccess<scrCamera, MeshRenderer>("camQuadMesh");
        private static readonly AccessTools.FieldRef<scrCamera, bool> customRef = AccessTools.FieldRefAccess<scrCamera, bool>("enableCustomFPS");
        private static FieldInfo frameRateField;

        internal static void Install(Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(scrCamera), "SetCustomFrameRate");
                frameRateField = AccessTools.Field(typeof(scrCamera), "frameRate");
                if (m == null || frameRateField == null || m.GetParameters().Length != 2) { Main.Entry.Logger.Log("[누수] SetCustomFrameRate 모양이 달라 끔"); return; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(LeakGuard), nameof(CfrPrefix)) { priority = Priority.First });
                Main.Entry.Logger.Log("[누수] 설치 (사용자 지정 FPS 화면 버퍼)");
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[누수] 설치 실패: " + ex.Message); }
        }

        public static bool CfrPrefix(scrCamera __instance, object[] __args)
        {
            if (!Enabled || Compat.QLeakGuard) return true;
            try
            {
                bool enable = (bool)__args[0];
                var qm = quadMeshRef(__instance);
                var camRT = camRTRef(__instance);
                if (qm == null) return true;
                var mat = qm.material;
                var old = mat.mainTexture as RenderTexture;
                bool ownBuffer = old != null && !ReferenceEquals(old, camRT) && !Fsr.IsOwn(old);   // FSR 출력 버퍼는 FSR 이 관리
                if (enable)
                {
                    // 이전에 만든 버퍼가 끼워져 있으면 풀고 없앤 뒤 원래 코드가 새로 만들게 둔다
                    if (ownBuffer) { mat.mainTexture = camRT; old.Release(); UnityEngine.Object.Destroy(old); FpsBuffersFreed++; }
                    return true;
                }
                // 끄기: 원래 코드와 같은 값을 넣되, camRT 는 Release 하지 않고 따로 만든 버퍼는 없앤다
                customRef(__instance) = false;
                frameRateField.SetValue(__instance, __args[1]);
                if (ownBuffer) { old.Release(); UnityEngine.Object.Destroy(old); FpsBuffersFreed++; }
                else if (old != null) CamRTReleaseSkipped++;
                mat.mainTexture = camRT;
                return false;
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[누수] 화면 버퍼 처리 실패, 원래대로: " + ex.Message); return true; }
        }

        // ── (개발자용) 누수 확인 ──
        private static float censusAt = -1f;
        private static string censusWhy = "";
        private static Dictionary<string, long> lastCount, lastBytes;
        private static Dictionary<string, int> lastNames;

        internal static void ScheduleCensus(string why, float delay)
        {
            if (!Edition.Dev) return;
            censusAt = Time.realtimeSinceStartup + delay; censusWhy = why;
        }
        internal static void Tick()
        {
            if (censusAt < 0f || Time.realtimeSinceStartup < censusAt || Hitch.Playing) return;
            censusAt = -1f;
            Census(censusWhy);
        }

        private static void Census(string why)
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var count = new Dictionary<string, long>(); var bytes = new Dictionary<string, long>(); var names = new Dictionary<string, int>();
                Add<RenderTexture>("화면버퍼", count, bytes, names, true);
                Add<Texture2D>("이미지", count, bytes, names, true);
                Add<Material>("머티리얼", count, bytes, names, true);
                Add<Mesh>("메시", count, bytes, names, false);
                Add<AudioClip>("오디오", count, bytes, names, true);
                Add<Sprite>("스프라이트", count, bytes, names, false);
                var sb = new System.Text.StringBuilder("[누수 확인] " + why + ":");
                foreach (var k in count.Keys)
                {
                    long c = count[k], b = bytes[k];
                    string d = "";
                    if (lastCount != null && lastCount.ContainsKey(k)) d = string.Format(" ({0:+#;-#;0}개, {1:+#;-#;0}MB)", c - lastCount[k], (b - lastBytes[k]) / 1048576);
                    sb.AppendFormat(" {0} {1}개 {2}MB{3} |", k, c, b / 1048576, d);
                }
                // 이름별로 늘어난 것 상위 12개
                if (lastNames != null)
                {
                    var grew = new List<KeyValuePair<string, int>>();
                    foreach (var kv in names) { int before; lastNames.TryGetValue(kv.Key, out before); if (kv.Value - before >= 3) grew.Add(new KeyValuePair<string, int>(kv.Key, kv.Value - before)); }
                    grew.Sort((a, b) => b.Value.CompareTo(a.Value));
                    sb.Append(" 늘어난 이름:");
                    for (int i = 0; i < grew.Count && i < 12; i++) sb.AppendFormat(" {0} +{1},", grew[i].Key, grew[i].Value);
                }
                long mono = GC.GetTotalMemory(false) / 1048576, native = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576;
                sb.AppendFormat(" 관리 힙 {0}MB, 엔진 메모리 {1}MB ({2}ms)", mono, native, sw.ElapsedMilliseconds);
                Main.Entry.Logger.Log(sb.ToString());
                lastCount = count; lastBytes = bytes; lastNames = names;
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[누수 확인] 실패: " + ex.Message); }
        }

        private static void Add<T>(string label, Dictionary<string, long> count, Dictionary<string, long> bytes, Dictionary<string, int> names, bool byName) where T : UnityEngine.Object
        {
            long c = 0, b = 0;
            foreach (var o in Resources.FindObjectsOfTypeAll<T>())
            {
                if (o == null) continue;
                c++;
                b += UnityEngine.Profiling.Profiler.GetRuntimeMemorySizeLong(o);
                if (byName)
                {
                    string n = label + ":" + (string.IsNullOrEmpty(o.name) ? "(이름 없음)" : o.name);
                    int v; names.TryGetValue(n, out v); names[n] = v + 1;
                }
            }
            count[label] = c; bytes[label] = b;
        }

        internal static string Summary()
        {
            if (FpsBuffersFreed + CamRTReleaseSkipped == 0) return "";
            return string.Format(" | 누수 막기: 사용자 지정 FPS 버퍼 {0}개 없앰, 게임 화면 버퍼 불필요한 해제 {1}번 막음", FpsBuffersFreed, CamRTReleaseSkipped);
        }
    }
}
