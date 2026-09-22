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

        internal static void MaybeRun()
        {
            if (!Enabled) return;
            long t0 = Stopwatch.GetTimestamp();
            int filters = 0;
            try { filters = WarmFilters(); }
            catch (Exception ex) { Main.Entry.Logger.Error("[셰이더] 필터 준비 실패: " + ex.Message); }
            try
            {
                int count = Resources.FindObjectsOfTypeAll<Shader>().Length;
                if (count == lastCount && filters == 0) return;
                lastCount = count;

                Shader.WarmupAllShaders();
                double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                ModCost.Add(SettingsWindow.T("셰이더 준비", "Shader warm-up"), ms);
                Last = "셰이더 " + count + "개, 새 필터 " + filters + "개 미리 준비 " + ms.ToString("F0") + "ms";
                Main.Entry.Logger.Log("[셰이더] " + Last);
            }
            catch (Exception ex) { Main.Entry.Logger.Error("[셰이더] 미리 준비 실패: " + ex.Message); }
        }

        // ── 카메라 필터 ─────────────────────────────────────────────────
        private static Type advType, plusType;
        private static FieldInfo advName, advTypeField, plusFilter;
        private static PropertyInfo plusMap;
        private static bool looked;

        private static void Look()
        {
            if (looked) return;
            looked = true;
            advType = AccessTools.TypeByName("ffxSetFilterAdvancedPlus");
            plusType = AccessTools.TypeByName("ffxSetFilterPlus");
            if (advType != null) { advName = AccessTools.Field(advType, "filterName"); advTypeField = AccessTools.Field(advType, "filterType"); }
            if (plusType != null) { plusFilter = AccessTools.Field(plusType, "filter"); plusMap = AccessTools.Property(plusType, "filterToComp"); }
        }

        private static int WarmFilters()
        {
            Look();
            var shaders = new Dictionary<string, Shader>();

            // 고급 필터 이벤트: 필터 이름(=CameraFilterPack 클래스 이름)
            if (advType != null)
                foreach (var o in UnityEngine.Object.FindObjectsByType(advType, FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    Type t = advTypeField != null ? advTypeField.GetValue(o) as Type : null;
                    if (t == null && advName != null)
                    {
                        string n = advName.GetValue(o) as string;
                        if (!string.IsNullOrEmpty(n)) t = AccessTools.TypeByName(n) ?? AccessTools.TypeByName("CameraFilterPack_" + n);
                    }
                    AddType(shaders, t, null);
                }

            // 일반 필터 이벤트: 게임이 필터 종류마다 카메라에 붙여 둔 컴포넌트
            if (plusType != null && plusMap != null && plusFilter != null)
            {
                System.Collections.IDictionary map = null;
                foreach (var o in UnityEngine.Object.FindObjectsByType(plusType, FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (map == null) { try { map = plusMap.GetValue(o, null) as System.Collections.IDictionary; } catch { } }
                    if (map == null) break;
                    var key = plusFilter.GetValue(o);
                    var comp = key != null && map.Contains(key) ? map[key] as Component : null;
                    if (comp != null) AddType(shaders, comp.GetType(), comp);
                }
            }

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

        // 필터 클래스의 셰이더를 찾는다. CameraFilterPack_Glow_Glow -> "CameraFilterPack/Glow_Glow".
        // 이름 규칙이 안 맞으면 컴포넌트의 Start 를 불러(셰이더를 찾는 일만 한다) SCShader 를 읽는다.
        private static void AddType(Dictionary<string, Shader> shaders, Type t, Component inst)
        {
            if (t == null || !t.Name.StartsWith("CameraFilterPack_")) return;
            string key = t.Name;
            if (shaders.ContainsKey(key) || warmedFilters.Contains(key)) return;
            Shader s = Shader.Find("CameraFilterPack/" + t.Name.Substring("CameraFilterPack_".Length));
            if (s == null && inst != null)
            {
                try
                {
                    var f = AccessTools.Field(t, "SCShader");
                    if (f != null)
                    {
                        s = f.GetValue(inst) as Shader;
                        if (s == null) { var start = AccessTools.Method(t, "Start"); if (start != null) start.Invoke(inst, null); s = f.GetValue(inst) as Shader; }
                    }
                }
                catch { }
            }
            if (s != null) shaders[key] = s;
        }
    }
}
