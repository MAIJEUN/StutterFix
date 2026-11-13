#if DEV || AUTOTEST
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace StutterFix
{
    internal sealed class SCEmptyUpdate : MonoBehaviour { void Update() { } }
    internal sealed class SCEmptyLate : MonoBehaviour { void LateUpdate() { } }

    // 진단 전용(자동 시험 scriptcensus ...). 엔진이 MonoBehaviour 의 Update/LateUpdate 를 부르는 "호출 자체"의 비용을 재고,
    // 어떤 종류가 몇 개 불리는지 센다. 곡 시작 뒤 한 번 부르고 끝낸다. 성능 A/B 판에서는 쓰지 않는다.
    //   count            : Update/LateUpdate 를 가진 켜진 스크립트를 종류별(어셈블리 포함)로 센다
    //   off <형식이름>   : 그 이름의 켜진 스크립트를 모두 enabled=false (엔진이 호출을 아예 안 한다)
    //   on               : off 로 끈 것을 되돌린다
    //   dummy <N>        : 빈 Update 와 빈 LateUpdate 를 N 개씩 만든다(호출 비용 재기)
    //   dummyoff         : 만든 것을 지운다
    internal static class ScriptCensus
    {
        static readonly List<Component> off = new List<Component>();
        static GameObject holder;
        static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        static void SetOn(Component c, bool on)
        {
            var b = c as Behaviour; if (b != null) { b.enabled = on; return; }
            var r = c as Renderer; if (r != null) r.enabled = on;
        }

        static HarmonyLib.Harmony colorH; static long scCalls, scSameMat, scSameCached, scFrames0; static int scFrame0;
        static readonly HarmonyLib.AccessTools.FieldRef<FloorRenderer, Color> cachedColorRef = HarmonyLib.AccessTools.FieldRefAccess<FloorRenderer, Color>("cachedColor");
        static bool SameC(Color a, Color b) { return a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a; }
        // 진단: 항상 원래 코드를 실행한다(건너뛰지 않음). 세터 앞에서 현재 머티리얼 색·기억한 색과 같은지만 센다.
        static bool ColorPre(FloorMeshRenderer __instance, Color value)
        {
            scCalls++;
            try
            {
                if (SameC(cachedColorRef(__instance), value)) scSameCached++;
                var m = __instance.material;
                if (m != null && SameC(m.GetColor(scrFloor.ShaderProperty_Color), value)) scSameMat++;
            }
            catch { }
            return true;
        }

        static bool Has(Type t, string name)
        {
            for (var x = t; x != null && x != typeof(MonoBehaviour) && x != typeof(object); x = x.BaseType)
                if (x.GetMethod(name, Any, null, Type.EmptyTypes, null) != null) return true;
            return false;
        }

        internal static string Command(string arg)
        {
            var p = (arg ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string cmd = p.Length > 0 ? p[0] : "";
            if (cmd == "count")
            {
                var by = new Dictionary<Type, int[]>();
                foreach (var mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
                {
                    if (!mb || !mb.gameObject.scene.IsValid() || !mb.isActiveAndEnabled) continue;
                    var t = mb.GetType();
                    bool u = Has(t, "Update"), l = Has(t, "LateUpdate");
                    if (!u && !l) continue;
                    int[] c; if (!by.TryGetValue(t, out c)) by[t] = c = new int[3];
                    c[0]++; c[1] += u ? 1 : 0; c[2] += l ? 1 : 0;
                }
                var list = new List<KeyValuePair<Type, int[]>>(by);
                list.Sort((a, b) => (b.Value[1] + b.Value[2]).CompareTo(a.Value[1] + a.Value[2]));
                var sb = new StringBuilder(); int calls = 0;
                foreach (var kv in list) calls += kv.Value[1] + kv.Value[2];
                sb.Append("종류 ").Append(list.Count).Append("개, 프레임당 호출 약 ").Append(calls).Append("번 |");
                for (int i = 0; i < list.Count && i < 40; i++)
                    sb.Append(' ').Append(list[i].Key.Name).Append('[').Append(list[i].Key.Assembly.GetName().Name).Append("]=")
                      .Append(list[i].Value[0]).Append("(U").Append(list[i].Value[1]).Append("/L").Append(list[i].Value[2]).Append(')');
                return sb.ToString();
            }
            if (cmd == "off" && p.Length > 1)
            {
                int n = 0;
                foreach (var mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
                {
                    if (!mb || !mb.gameObject.scene.IsValid() || !mb.isActiveAndEnabled || mb.GetType().Name != p[1]) continue;
                    off.Add(mb); SetOn(mb, false); n++;
                }
                return p[1] + " 끔 " + n + "개";
            }
            if (cmd == "on")
            {
                int n = 0; foreach (var b in off) if (b) { SetOn(b, true); n++; }
                off.Clear(); return "되돌림 " + n + "개";
            }
            if (cmd == "dummy" && p.Length > 1)
            {
                int n = int.Parse(p[1]);
                if (!holder) { holder = new GameObject("SCDummies"); UnityEngine.Object.DontDestroyOnLoad(holder); }
                for (int i = 0; i < n; i++) { holder.AddComponent<SCEmptyUpdate>(); holder.AddComponent<SCEmptyLate>(); }
                return "빈 Update/LateUpdate 각 " + n + "개 추가";
            }
            if (cmd == "census")
            {
                int masks = 0, masksOn = 0, vin = 0, vout = 0, srs = 0, back = 0;
                var kinds = new Dictionary<string, int>();
                foreach (var m in Resources.FindObjectsOfTypeAll<SpriteMask>())
                { if (!m || !m.gameObject.scene.IsValid()) continue; masks++; if (m.enabled && m.gameObject.activeInHierarchy) masksOn++; }
                foreach (var r in Resources.FindObjectsOfTypeAll<SpriteRenderer>())
                {
                    if (!r || !r.gameObject.scene.IsValid() || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    srs++;
                    if (r.maskInteraction == SpriteMaskInteraction.VisibleInsideMask) vin++;
                    else if (r.maskInteraction == SpriteMaskInteraction.VisibleOutsideMask) vout++;
                }
                var sb = new StringBuilder();
                sb.Append("SpriteMask ").Append(masks).Append("개(켜짐 ").Append(masksOn).Append(") | 켜진 SpriteRenderer ").Append(srs)
                  .Append("개: 마스크 안쪽만 ").Append(vin).Append(", 바깥쪽만 ").Append(vout);
                return sb.ToString();
            }
            if (cmd == "masks" && p.Length > 1)
            {
                if (p[1] == "off")
                {
                    int n = 0;
                    foreach (var m in Resources.FindObjectsOfTypeAll<SpriteMask>())
                    { if (!m || !m.gameObject.scene.IsValid() || !m.enabled) continue; off.Add(m); SetOn(m, false); n++; }
                    return "SpriteMask 끔 " + n + "개 (scriptcensus on 으로 되돌림)";
                }
            }
            if (cmd == "deco")
            {   // 깨어 있는(쉬지 않는) 장식을 종류 조합별로 센다: H=히트박스, M=마스크, S=메시렌더러, v=GetVisible, e=렌더러 켜짐, a0=알파 0
                var meshOn = HarmonyLib.AccessTools.FieldRefAccess<scrVisualDecoration, bool>("meshRendererEnabled");
                var rendOn = HarmonyLib.AccessTools.FieldRefAccess<scrDecoration, bool>("rendererEnabled");
                var rendCol = HarmonyLib.AccessTools.FieldRefAccess<scrDecoration, Color>("rendererColor");
                var by = new Dictionary<string, int>(); int total = 0, awake = 0, other = 0;
                foreach (var d in Resources.FindObjectsOfTypeAll<scrDecoration>())
                {
                    if (!d || !d.gameObject.scene.IsValid()) continue;
                    total++;
                    var v = d as scrVisualDecoration;
                    if (v == null) { other++; continue; }
                    if (Dormancy.IsDormant(d, true)) continue;
                    awake++;
                    string k = (d.hitbox != 0 ? "H" : "") + (v.isMask() ? "M" : "") + (meshOn(v) ? "S" : "")
                        + (v.GetVisible() ? "v" : "-") + (rendOn(d) ? "e" : "-") + (rendCol(d).a == 0f ? "a0" : "");
                    int c; by.TryGetValue(k, out c); by[k] = c + 1;
                }
                var l = new List<KeyValuePair<string, int>>(by); l.Sort((a, b) => b.Value.CompareTo(a.Value));
                var sb = new StringBuilder("장식 " + total + "개(그 밖 종류 " + other + ") 중 깨어 있는 시각 장식 " + awake + "개:");
                for (int i = 0; i < l.Count && i < 14; i++) sb.Append(' ').Append(l[i].Key).Append('=').Append(l[i].Value);
                return sb.ToString();
            }
            if (cmd == "errcanvas")
            {   // 판정 오차 막대: 눈금이 들어 있는 wrapperRectTransform 에 하위 Canvas 를 붙여 눈금 변경이 막대 전체 재배치를 부르지 않게 한다
                var meter = default(scrHitErrorMeter);
                foreach (var m in Resources.FindObjectsOfTypeAll<scrHitErrorMeter>()) if (m && m.gameObject.scene.IsValid()) { meter = m; break; }
                if (!meter) return "scrHitErrorMeter 없음";
                var wrap = HarmonyLib.AccessTools.Field(typeof(scrHitErrorMeter), "wrapperRectTransform").GetValue(meter) as RectTransform;
                if (!wrap) return "wrapperRectTransform 없음";
                var root = meter.GetComponentInParent<Canvas>();
                var sb = new StringBuilder("루트 캔버스 " + (root ? root.name : "-") + " 자식:");
                if (root) for (int i = 0; i < root.transform.childCount; i++) { var ch = root.transform.GetChild(i); sb.Append(' ').Append(ch.name).Append('(').Append(ch.GetComponentsInChildren<UnityEngine.UI.Graphic>(true).Length).Append(')'); }
                sb.Append(" | wrapper 부모=").Append(wrap.parent ? wrap.parent.name : "-").Append(" 형제=").Append(wrap.parent ? wrap.parent.childCount : 0).Append(" wrapper 자식=").Append(wrap.childCount)
                  .Append(" 그래픽=").Append(wrap.GetComponentsInChildren<UnityEngine.UI.Graphic>(true).Length);
                if (p.Length > 1 && p[1] == "on")
                {
                    var c = wrap.GetComponent<Canvas>();
                    if (!c) { c = wrap.gameObject.AddComponent<Canvas>(); sb.Append(" | 하위 캔버스 붙임"); } else sb.Append(" | 이미 있음");
                }
                if (p.Length > 1 && p[1] == "off") { var c = wrap.GetComponent<Canvas>(); if (c) { UnityEngine.Object.Destroy(c); sb.Append(" | 떼어냄"); } }
                return sb.ToString();
            }
            if (cmd == "colorstat")
            {
                string a = p.Length > 1 ? p[1] : "show";
                if (a == "on")
                {
                    if (colorH != null) return "이미 켬";
                    colorH = new HarmonyLib.Harmony("StutterFix.colorstat");
                    var set = HarmonyLib.AccessTools.PropertySetter(typeof(FloorMeshRenderer), "color");
                    colorH.Patch(set, prefix: new HarmonyLib.HarmonyMethod(typeof(ScriptCensus), nameof(ColorPre)));
                    scCalls = scSameMat = scSameCached = 0; scFrame0 = Time.frameCount;
                    return "FloorMeshRenderer.color 세터 세기 시작";
                }
                if (a == "off") { if (colorH != null) { colorH.UnpatchOwner("StutterFix.colorstat"); colorH = null; } return "끔"; }
                int fr = Math.Max(1, Time.frameCount - scFrame0);
                return "프레임 " + fr + "개 동안 세터 호출 " + scCalls + "번(프레임당 " + (scCalls / (double)fr).ToString("F1") + "), 현재 머티리얼 색과 같은 것 "
                    + scSameMat + "(" + (scCalls > 0 ? (100.0 * scSameMat / scCalls).ToString("F0") : "0") + "%), 기억한 색(cachedColor)과 같은 것 " + scSameCached;
            }
            if (cmd == "layers")
            {   // 레이어별 켜진 렌더러: 종류, 스프라이트 알파 0, forceRenderingOff, 화면에 보임(isVisible), 재질 이름 상위
                var by = new Dictionary<string, int[]>(); var mats = new Dictionary<string, Dictionary<string, int>>();
                foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
                {
                    if (!r || !r.gameObject.scene.IsValid() || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                    string ln = LayerMask.LayerToName(r.gameObject.layer); if (string.IsNullOrEmpty(ln)) ln = r.gameObject.layer.ToString();
                    string kind = r is SpriteRenderer ? "Sprite" : r is MeshRenderer ? "Mesh" : r is ParticleSystemRenderer ? "Particle" : r.GetType().Name;
                    string k = ln + "/" + kind;
                    int[] v; if (!by.TryGetValue(k, out v)) by[k] = v = new int[5];
                    v[0]++;
                    if (r.forceRenderingOff) v[2]++;
                    if (r.isVisible) v[3]++;
                    var sr = r as SpriteRenderer;
                    if (sr != null && sr.color.a == 0f) v[1]++;
                    if (sr != null && !sr.sprite) v[4]++;
                    var m = r.sharedMaterial; string mn = m ? (m.shader ? m.shader.name : "?") : "-";
                    Dictionary<string, int> md; if (!mats.TryGetValue(k, out md)) mats[k] = md = new Dictionary<string, int>();
                    int c; md.TryGetValue(mn, out c); md[mn] = c + 1;
                }
                var l = new List<KeyValuePair<string, int[]>>(by); l.Sort((a, b) => b.Value[0].CompareTo(a.Value[0]));
                var sb = new StringBuilder("레이어/종류=켜짐(알파0, 강제끔, 보임, 스프라이트없음) {셰이더}:");
                for (int i = 0; i < l.Count && i < 12; i++)
                {
                    var md = mats[l[i].Key]; var ml = new List<KeyValuePair<string, int>>(md); ml.Sort((a, b) => b.Value.CompareTo(a.Value));
                    sb.Append(" || ").Append(l[i].Key).Append('=').Append(l[i].Value[0]).Append('(').Append(l[i].Value[1]).Append(',').Append(l[i].Value[2]).Append(',').Append(l[i].Value[3]).Append(',').Append(l[i].Value[4]).Append(") {");
                    for (int j = 0; j < ml.Count && j < 3; j++) sb.Append(ml[j].Key).Append(':').Append(ml[j].Value).Append(' ');
                    sb.Append('}');
                }
                return sb.ToString();
            }
            if (cmd == "cameras")
            {   // 카메라마다: 켜짐, 컬링 마스크가 포함하는 레이어, 출력 대상 크기, depth, 그 마스크에 걸리는 켜진 렌더러 수
                var layerCount = new int[32]; var layerNames = new string[32];
                foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
                {
                    if (!r || !r.gameObject.scene.IsValid() || !r.enabled || !r.gameObject.activeInHierarchy || r.forceRenderingOff) continue;
                    layerCount[r.gameObject.layer]++;
                }
                for (int i = 0; i < 32; i++) layerNames[i] = LayerMask.LayerToName(i);
                var sb = new StringBuilder("카메라:");
                foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
                {
                    if (!c || !c.gameObject.scene.IsValid()) continue;
                    int mask = c.cullingMask, n = 0; var ls = new StringBuilder();
                    for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0 && layerCount[i] > 0) { n += layerCount[i]; ls.Append(string.IsNullOrEmpty(layerNames[i]) ? i.ToString() : layerNames[i]).Append(':').Append(layerCount[i]).Append(','); }
                    sb.Append(" [").Append(c.name).Append(" 켜짐=").Append(c.enabled && c.gameObject.activeInHierarchy).Append(" 깊이=").Append(c.depth)
                      .Append(" 대상=").Append(c.targetTexture ? c.targetTexture.width + "x" + c.targetTexture.height : "화면")
                      .Append(" 렌더러=").Append(n).Append(" (").Append(ls).Append(")] ");
                }
                return sb.ToString();
            }
            if (cmd == "canvaslist")
            {   // 루트 이름별 캔버스 수(켜진 것)와 그 안의 TMP/Text/Image 수
                var by = new Dictionary<string, int[]>();
                foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>())
                {
                    if (!c || !c.gameObject.scene.IsValid() || !c.isActiveAndEnabled) continue;
                    string k = (c.transform.root ? c.transform.root.name : "-") + "/" + c.renderMode;
                    int[] v; if (!by.TryGetValue(k, out v)) by[k] = v = new int[4];
                    v[0]++; v[1] += c.GetComponentsInChildren<TMPro.TMP_Text>(false).Length;
                    v[2] += c.GetComponentsInChildren<UnityEngine.UI.Graphic>(false).Length;
                }
                var l = new List<KeyValuePair<string, int[]>>(by); l.Sort((a, b) => b.Value[2].CompareTo(a.Value[2]));
                var sb = new StringBuilder("켜진 캔버스 루트:");
                for (int i = 0; i < l.Count && i < 30; i++) sb.Append(' ').Append(l[i].Key).Append('=').Append(l[i].Value[0]).Append("(TMP ").Append(l[i].Value[1]).Append(", 그래픽 ").Append(l[i].Value[2]).Append(')');
                return sb.ToString();
            }
            if (cmd == "canvasoff" && p.Length > 1)
            {   // 루트 이름에 p[1] 이 든 켜진 캔버스를 끈다 (scriptcensus on 으로 되돌림)
                int n = 0;
                foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>())
                {
                    if (!c || !c.gameObject.scene.IsValid() || !c.enabled || !c.isRootCanvas) continue;
                    string rn = c.transform.root ? c.transform.root.name : "";
                    if (rn.IndexOf(p[1], StringComparison.OrdinalIgnoreCase) < 0 && c.gameObject.name.IndexOf(p[1], StringComparison.OrdinalIgnoreCase) < 0) continue;
                    off.Add(c); SetOn(c, false); n++;
                }
                return "캔버스 끔 " + n + "개 (" + p[1] + ")";
            }
            if (cmd == "dummyoff")
            {
                if (holder) { UnityEngine.Object.Destroy(holder); holder = null; }
                return "지움";
            }
            return "scriptcensus count | off <형식> | on | dummy <N> | dummyoff | census | masks off | canvaslist | canvasoff <이름> | cameras | layers | colorstat on|off|show";
        }
    }
}
#endif
