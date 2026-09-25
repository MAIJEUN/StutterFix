using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 곡 시작 전에 셰이더를 미리 준비시킨다.
    //
    // 효과 나누기와 색 나누기 뒤에 남은 곡 중 끊김 두 곳은 필터가 바뀌는 순간이었다.
    //   137.1초: Glow_Color 가 켜진 그 프레임, 게임 코드 밖(FinishFrameRendering) 22ms, 다음 프레임 GPU 41ms
    //   165.6초: Chromatical2 가 꺼진 직후, 게임 루프 밖에서 45ms, 이어서 GPU 47ms
    // 게임 코드는 짧고 그래픽 드라이버 쪽에서 멈췄다. 필터 셰이더를 처음 그릴 때 드라이버가
    // 셰이더를 만드는 비용이다. 맵이 올라온 뒤 곡 시작 시점에 한꺼번에 미리 데운다.
    //
    // 예전에는 Shader.WarmupAllShaders 만 불렀는데, 이것은 "이미 메모리에 올라온" 셰이더만 데운다.
    // 카메라 필터(CameraFilterPack)는 필터가 처음 켜질 때 Start 에서 Shader.Find 로 셰이더를 불러오므로
    // 곡 시작 때는 아직 없다. 그래서 필터를 처음 켜는 순간 드라이버가 셰이더를 만들며 200ms 멈췄다
    // (69.4초, 필터 3개가 켜진 다음 프레임: 게임 코드 3ms, GPU 208ms).
    // 지금은 이 맵의 필터 이벤트가 쓰는 필터를 찾아 셰이더를 불러오고, 작은 화면에 한 번씩 그려 드라이버가
    // 셰이더를 미리 만들게 한다. 화면에 보이는 것은 없다. 같은 셰이더는 한 번만 데운다.
    public static class ShaderWarm
    {
        internal static bool Enabled = true;
        internal static string Last = "아직 안 함";
        private static int lastCount = -1;
        private static readonly HashSet<string> warmedFilters = new HashSet<string>();

        // 맵을 새로 불러왔을 때만 한다. 같은 맵을 다시 플레이할 때도 매번 필터 이벤트와 셰이더 목록을 훑었는데
        // (객체 수천 개를 뒤져 40ms 안팎), 곡 시작 직후라 "게임 처리" 끊김으로 잡혔다. 새로 준비할 것이 없으니 건너뛴다.
        internal static bool LevelChanged = true;

        // 곡 시작 때. 맵을 불러올 때(AfterLoad) 이미 다 했으면 새로 할 것이 없다.
        // 에디터에서 필터 이벤트를 새로 넣은 경우만 여기서 그 필터를 데운다(맵 이벤트를 훑는 것뿐이라 가볍다).
        internal static void MaybeRun()
        {
            if (!Enabled) return;
            if (LevelChanged) { Run("곡 시작"); return; }
            long t0 = Stopwatch.GetTimestamp();
            int n = 0;
            try { n = WarmFilters(); } catch { }
            if (n > 0) Main.Entry.Logger.Log(string.Format("[셰이더] 곡 시작: 새로 넣은 필터 셰이더 {0}개 미리 준비 {1:F0}ms", n, (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency));
            ModCost.Add(SettingsWindow.T("셰이더 준비", "Shader warm-up"), (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
        }

        // 맵을 다 불러온 직후(ImagePrefetch.End). 곡 중이 아니라서 여기서 멈춰도 플레이·소리에 영향이 없다.
        // 예전에는 전부 곡 시작 때 했다(곡 시작 첫 프레임이 400ms 가까이 늘었다).
        // 이 자리(scnGame.UpdateDecorationObjects 끝)는 에디터에서 되돌리기·붙여넣기 같은 편집 때도 불린다.
        // 셰이더 전체 목록 훑기와 WarmupAllShaders 는 맵이 바뀌었을 때만 하고, 편집 때는 새로 넣은 필터만 본다.
        private static string lastLevel;
        internal static void AfterLoad()
        {
            if (!Enabled || Hitch.Playing) return;
            string level = null;
            try { level = ADOBase.levelPath; } catch { }
            if (level != null && string.Equals(level, lastLevel, StringComparison.OrdinalIgnoreCase))
            {
                LevelChanged = false;
                int n = 0;
                try { n = WarmFilters(); } catch { }
                if (n > 0) Main.Entry.Logger.Log("[셰이더] 편집 뒤: 새로 넣은 필터 셰이더 " + n + "개 미리 준비");
                return;
            }
            lastLevel = level;
            Run("맵 불러온 뒤");
        }

        private static void Run(string where)
        {
            LevelChanged = false;
            long t0 = Stopwatch.GetTimestamp();
            int filters = 0;
            try { filters = WarmFilters(); }
            catch (Exception ex) { Main.Entry.Logger.Error("[셰이더] 필터 준비 실패: " + ex.Message); }
            try
            {
                int count = Resources.FindObjectsOfTypeAll<Shader>().Length;
                if (count != lastCount || filters > 0)
                {
                    lastCount = count;
                    Shader.WarmupAllShaders();
                }
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                ModCost.Add(SettingsWindow.T("셰이더 준비", "Shader warm-up"), ms);
                Last = where + ": 셰이더 " + count + "개, 새 필터 " + filters + "개(필터 텍스처 " + ResourcesLoaded + "개) 미리 준비 " + ms.ToString("F0") + "ms";
                Main.Entry.Logger.Log("[셰이더] " + Last);
            }
            catch (Exception ex) { Main.Entry.Logger.Error("[셰이더] 미리 준비 실패: " + ex.Message); }
        }

        // ── 카메라 필터 ─────────────────────────────────────────────────
        // r148 에서 확인한 것 (IL):
        //   고급 필터: ffxSetFilterAdvancedPlus.Setup 이 Type.GetType(filterName + ", Assembly-CSharp-firstpass") 로 클래스를 찾아
        //     카메라에 꺼진 채 AddComponent 한다. 필터가 처음 켜질 때 Start 에서 Shader.Find("...") 로 셰이더를 부른다.
        //     셰이더 이름은 클래스 이름과 다를 때가 있다(CameraFilterPack_AAA_SuperComputer -> "CameraFilterPack/AAA_Super_Computer").
        //   일반 필터: scrVfxPlus.filterToComp 의 컴포넌트, 클래스는 CameraFilterPackLegacy_* 등(예전 코드는 "CameraFilterPack_" 로
        //     시작하는 것만 봐서 일반 필터는 하나도 못 데웠다).
        // 그래서 이름을 짐작하지 않고, 필터 클래스의 Start/Awake/OnEnable IL 에서 "문자열 -> Shader.Find" 를 읽어 그 셰이더를 데운다.
        // 같은 자리에서 "문자열 -> Resources.Load" (필터가 처음 켜질 때 불러오는 텍스처)도 미리 불러 들고 있는다.
        // 필터 목록은 맵 데이터(레벨 이벤트)에서 읽는다. 효과 객체를 찾지 않아서 언제 불러도 같은 결과다.
        private static readonly HashSet<Type> seenTypes = new HashSet<Type>();
        private static readonly List<UnityEngine.Object> keepLoaded = new List<UnityEngine.Object>();   // 미리 불러온 필터 텍스처 (치워지지 않게)
        internal static int ResourcesLoaded;

        private static IEnumerable<Type> FilterTypes()
        {
            var types = new List<Type>();
            // 고급 필터: 맵의 이벤트에서 이름을 읽는다
            var lvl = ADOBase.customLevel;
            var data = (object)lvl != null && lvl != null ? lvl.levelData : null;
            if (data != null && data.levelEvents != null)
            {
                var names = new HashSet<string>();
                foreach (var ev in data.levelEvents)
                {
                    if (ev == null || ev.eventType != ADOFAI.LevelEventType.SetFilterAdvanced) continue;
                    string name = null;
                    try { name = ev.GetString("filter"); } catch { }
                    if (string.IsNullOrEmpty(name) || !names.Add(name)) continue;
                    Type t = null;
                    try { t = Type.GetType(name + ", Assembly-CSharp-firstpass"); } catch { }
                    if (t != null) types.Add(t);
                }
            }
            // 일반 필터: 게임이 카메라에 붙여 둔 필터 컴포넌트 전부 (맵에서 쓰는 것만 고르지 않는다 - 수십 개뿐이고 한 번만 데운다)
            try
            {
                var vfx = scrVfxPlus.instance;
                if ((object)vfx != null && vfx != null && vfx.filterToComp != null)
                    foreach (var c in vfx.filterToComp.Values) if ((object)c != null && c != null) types.Add(c.GetType());
            }
            catch { }
            return types;
        }

        private static int WarmFilters()
        {
            var shaders = new Dictionary<string, Shader>();
            foreach (var t in FilterTypes())
            {
                if (t == null || !seenTypes.Add(t)) continue;
                var shaderNames = new List<string>(); var resNames = new List<string>();
                foreach (var mn in new[] { "Start", "Awake", "OnEnable" })
                {
                    var m = AccessTools.Method(t, mn, Type.EmptyTypes);
                    if (m != null && m.DeclaringType == t) ScanStrings(m, shaderNames, resNames);
                }
                foreach (var sn in shaderNames)
                {
                    if (shaders.ContainsKey(sn) || warmedFilters.Contains(sn)) continue;
                    var s = Shader.Find(sn);
                    if (s != null) shaders[sn] = s;
                }
                foreach (var rn in resNames)
                {
                    try { var o = Resources.Load(rn); if (o != null) { keepLoaded.Add(o); ResourcesLoaded++; } } catch { }
                }
            }
            InstanceShaders(shaders);

            int warmed = 0;
            RenderTexture a = null, b = null;
            try
            {
                foreach (var kv in shaders)
                {
                    warmedFilters.Add(kv.Key);
                    if (a == null) { a = RenderTexture.GetTemporary(16, 16, 0); b = RenderTexture.GetTemporary(16, 16, 0); }
                    var m = new Material(kv.Value) { hideFlags = HideFlags.HideAndDontSave };
                    for (int p = 0; p < m.passCount; p++) Graphics.Blit(a, b, m, p);
                    UnityEngine.Object.Destroy(m);
                    warmed++;
                }
            }
            finally
            {
                if (a != null) RenderTexture.ReleaseTemporary(a);
                if (b != null) RenderTexture.ReleaseTemporary(b);
            }
            if (warmed > 0 && Edition.Dev) Main.Entry.Logger.Log("[셰이더] 이 맵의 필터 셰이더 " + warmed + "개 새로 데움");
            return warmed;
        }

        // 일반 필터 컴포넌트에 직렬화로 들어 있는 셰이더 (CameraMotionBlur 처럼 Shader.Find 없이 필드로 들고 있는 것)
        private static void InstanceShaders(Dictionary<string, Shader> shaders)
        {
            try
            {
                var vfx = scrVfxPlus.instance;
                if ((object)vfx == null || vfx == null || vfx.filterToComp == null) return;
                foreach (var c in vfx.filterToComp.Values)
                {
                    if ((object)c == null || c == null) continue;
                    foreach (var f in c.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (f.FieldType != typeof(Shader)) continue;
                        var s = f.GetValue(c) as Shader;
                        if (s != null && !warmedFilters.Contains(s.name) && !shaders.ContainsKey(s.name)) shaders[s.name] = s;
                    }
                }
            }
            catch { }
        }

        // 메서드 IL 에서 "ldstr 문자열" 바로 뒤가 Shader.Find / Resources.Load 호출인 것을 찾는다.
        // 바이트를 한 칸씩 훑으므로 피연산자 안의 0x72 를 잘못 볼 수 있지만, 토큰이 문자열 표(0x70)이고 바로 뒤 호출 대상이
        // 그 두 함수일 때만 쓰므로 잘못 잡을 일이 사실상 없고, 잘못 잡아도 없는 이름을 찾아보는 것뿐이다.
        private static void ScanStrings(MethodInfo m, List<string> shaderNames, List<string> resNames)
        {
            byte[] il;
            try { var body = m.GetMethodBody(); il = body != null ? body.GetILAsByteArray() : null; } catch { return; }
            if (il == null) return;
            var mod = m.Module;
            for (int i = 0; i + 10 <= il.Length; i++)
            {
                if (il[i] != 0x72) continue;                      // ldstr
                int tok = BitConverter.ToInt32(il, i + 1);
                if ((tok >> 24) != 0x70) continue;
                byte op = il[i + 5];
                if (op != 0x28 && op != 0x6F) continue;           // call / callvirt
                string s; MethodBase callee;
                try { s = mod.ResolveString(tok); callee = mod.ResolveMethod(BitConverter.ToInt32(il, i + 6)); } catch { continue; }
                if (callee == null || string.IsNullOrEmpty(s)) continue;
                if (callee.DeclaringType == typeof(Shader) && callee.Name == "Find") shaderNames.Add(s);
                else if (callee.DeclaringType == typeof(Resources) && callee.Name == "Load") resNames.Add(s);
            }
        }
    }
}
