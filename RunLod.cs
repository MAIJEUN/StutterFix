using System;
using ADOFAI;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using UnityEngine;
using UnityEngine.Rendering;

namespace StutterFix
{
    // 묶음 LOD(원래 렌더러): 그리기 순서에서 연속으로 붙은 "크게 확대된 이미지 장식"들을 저해상도 그림 하나에 순서대로 모아 그리고
    // (본 카메라의 투명 그리기 직전 CommandBuffer), 묶음 첫 장식의 정렬 자리에서 화면 사각형 하나로 합친다.
    // 확대된 그림은 원래 저주파라 눈으로는 같다. 화면 전체 층 수가 묶음 수로 준다.
    internal static class RunLod
    {
        internal static bool On;
        internal static float MinMag = 4f;
        // 그리기 방식 Tiled(반복) 장식도 맡는다. 확대율은 반복 한 칸 기준(텍셀 수 = 그림 폭 x 반복 횟수), 재질 RepeatX/Y 도 곱한다. 기본 끔: runlod tiled on
        internal static bool Tiled;
        internal static bool Particles, CpuFast, Verify;
        internal static int ParticleCands, ParticleMembersDrawn, VerifyFrames, VerifyDiffs;
        static readonly List<Material> particleMaterials = new List<Material>(4);
        static ParticleSystem.Particle[] liveParticles = new ParticleSystem.Particle[0];
        static int particleCapacity;
        internal static int TiledCands;
        static bool onlyTiled;
        // GPU 장식이 반복(Repeat 기본 끔) 때문에 못 맡는 장식: 장식 repeatX/Y 가 1 이 아님
        static bool RepeatDeco(SpriteRenderer sr) { var d = sr.GetComponentInParent<scrVisualDecoration>(); return d && (d.repeatX != 1 || d.repeatY != 1); }
        static bool ModeOk(SpriteRenderer sr) => sr.drawMode == SpriteDrawMode.Simple || (Tiled && sr.drawMode == SpriteDrawMode.Tiled);
        // 확대율 계산용 가로/세로 텍셀 수(반복 포함). Tiled 끔이면 원래 식(그림 rect 크기)
        static void TexSize(SpriteRenderer r, Cand cd)
        {
            var tr = r.sprite.textureRect; float w = Mathf.Max(1f, tr.width), h = Mathf.Max(1f, tr.height);
            if (Tiled)
            {
                if (r.drawMode == SpriteDrawMode.Tiled) { var sb = r.sprite.bounds.size; if (sb.x > 1e-6f) w *= Mathf.Max(1f, Mathf.Abs(r.size.x) / sb.x); if (sb.y > 1e-6f) h *= Mathf.Max(1f, Mathf.Abs(r.size.y) / sb.y); }
                var m = r.sharedMaterial;
                if (m) { if (m.HasProperty("RepeatX")) w *= Mathf.Max(1f, Mathf.Abs(m.GetFloat("RepeatX"))); if (m.HasProperty("RepeatY")) h *= Mathf.Max(1f, Mathf.Abs(m.GetFloat("RepeatY"))); }
            }
            cd.TexW = w; cd.TexH = h;
        }
        internal static int MinRun = 2;
        internal static int Frames, RunsDrawn, MembersDrawn;
        const int HideLayer = 31;

        // GPU 장식과 나눠 맡기: RunLod 가 다루는 장식(모음의 후보 조건과 같음: Sprites/Tile·Default, 미리 곱한 알파, 단순 그리기, 마스크 없음)
        internal static bool CandidateRenderer(Renderer r)
        {
            var m = r ? r.sharedMaterial : null;
            if (Particles && r is ParticleSystemRenderer pr) return ParticleOk(pr, m);
            if (!m || !(r is SpriteRenderer sr) || !ModeOk(sr) || sr.maskInteraction != SpriteMaskInteraction.None) return false;
            var sh = m.shader; if (!sh || (sh.name != "Sprites/Tile" && sh.name != "Sprites/Default")) return false;
            return !m.HasProperty("_SrcBlend") || (m.GetFloat("_SrcBlend") == 1f && m.GetFloat("_DstBlend") == 10f);
        }

        static bool ParticleOk(ParticleSystemRenderer r, Material m)
        {
            if (!ParticleCurves.Ready) return false;
            if (!m || r.renderMode != ParticleSystemRenderMode.Billboard || r.maskInteraction != SpriteMaskInteraction.None
                || r.trailMaterial || r.GetComponentInParent<SortingGroup>() || !r.GetComponentInParent<scrParticleDecoration>()) return false;
            var ps = r.GetComponent<ParticleSystem>();
            if (!ps || ps.trails.enabled) return false;
            var sh = m.shader;
            if (!sh || (sh.name != "Sprites/Default" && sh.name != "Sprites/Tile")) return false;
            if ((m.HasProperty("_SrcBlend") && m.GetFloat("_SrcBlend") != 1f)
                || (m.HasProperty("_DstBlend") && m.GetFloat("_DstBlend") != 10f)) return false;
            particleMaterials.Clear(); r.GetSharedMaterials(particleMaterials);
            return particleMaterials.Count == 1;
        }

        static bool Finite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);
        // Exact extrema of unweighted cubic Hermite segments. Never use sampled minima:
        // an unsampled dip would overestimate magnification. Unsupported curves fail closed.
        static float CurveMin(IntPtr curve)
        {
            if (curve == IntPtr.Zero) return float.NaN;
            int length = ParticleCurves.Length(curve);
            if (length == 0) return float.NaN;
            float min = float.PositiveInfinity;
            for (int i = 0; i < length; i++)
            {
                ParticleCurves.Key(curve, i, out var k);
                if (!Finite(k.time) || !Finite(k.value) || k.weightedMode != WeightedMode.None) return float.NaN;
                min = Mathf.Min(min, k.value);
                if (i == 0) continue;
                ParticleCurves.Key(curve, i - 1, out var p); double dt = k.time - p.time;
                if (dt <= 0 || double.IsInfinity(dt) || double.IsNaN(dt)) return float.NaN;
                // Infinite tangents are Unity's step interpolation; endpoint minimum suffices.
                if (float.IsInfinity(p.outTangent) || float.IsInfinity(k.inTangent)) continue;
                if (!Finite(p.outTangent) || !Finite(k.inTangent)) return float.NaN;
                double a = 2.0 * p.value - 2.0 * k.value + dt * (p.outTangent + k.inTangent);
                double b = -3.0 * p.value + 3.0 * k.value - dt * (2.0 * p.outTangent + k.inTangent);
                double c = dt * p.outTangent;
                if (Math.Abs(a) < 1e-20)
                {
                    if (Math.Abs(b) > 1e-20) CurveExtremum(-c / (2 * b), a, b, c, p.value, ref min);
                }
                else
                {
                    double disc = 4 * b * b - 12 * a * c;
                    if (disc >= 0)
                    {
                        double root = Math.Sqrt(disc);
                        CurveExtremum((-2 * b - root) / (6 * a), a, b, c, p.value, ref min);
                        CurveExtremum((-2 * b + root) / (6 * a), a, b, c, p.value, ref min);
                    }
                }
            }
            // A tiny downward margin covers float rounding in Unity's interpolation.
            return min > 0 ? Mathf.Max(0, min - Mathf.Abs(min) * 1e-6f) : min;
        }
        static void CurveExtremum(double t, double a, double b, double c, double d, ref float min)
        {
            if (t > 0 && t < 1) min = Mathf.Min(min, (float)(((a * t + b) * t + c) * t + d));
        }
        #pragma warning disable CS0649 // Fields are populated by the checked IL copier below.
        struct RawCurve
        {
            public ParticleSystemCurveMode m_Mode;
            public float m_CurveMultiplier;
            public IntPtr m_CurveMin, m_CurveMax;
            public float m_ConstantMin, m_ConstantMax;
        }
        #pragma warning restore CS0649
        // Unity 6's public MinMaxCurve getters allocate AnimationCurve wrappers. Bind
        // its existing blittable getters once, copying fields rather than relying on ABI layout.
        // These private APIs are version checked; unsupported engines exclude particles.
        static class ParticleCurves
        {
            internal delegate void ReadKey(IntPtr ptr, int i, out Keyframe key);
            internal static readonly Func<IntPtr, int> Length;
            internal static readonly ReadKey Key;
            internal static readonly Func<ParticleSystem.MainModule, RawCurve>[] Main;
            internal static readonly Func<ParticleSystem.SizeOverLifetimeModule, RawCurve>[] Life;
            internal static readonly Func<ParticleSystem.SizeBySpeedModule, RawCurve>[] Speed;
            internal static readonly bool Ready;
            static ParticleCurves()
            {
                try
                {
                    Length = (Func<IntPtr, int>)AccessTools.Method(typeof(AnimationCurve), "get_length_Injected").CreateDelegate(typeof(Func<IntPtr, int>));
                    Key = (ReadKey)AccessTools.Method(typeof(AnimationCurve), "GetKey_Injected").CreateDelegate(typeof(ReadKey));
                    Main = Readers<ParticleSystem.MainModule>("startSize", "startSizeX", "startSizeY", "startSizeZ");
                    Life = Readers<ParticleSystem.SizeOverLifetimeModule>("size", "x", "y", "z");
                    Speed = Readers<ParticleSystem.SizeBySpeedModule>("size", "x", "y", "z");
                    Ready = true;
                }
                catch { Ready = false; }
            }
            static Func<T, RawCurve>[] Readers<T>(params string[] names)
            {
                var result = new Func<T, RawCurve>[names.Length];
                for (int i = 0; i < names.Length; i++)
                {
                    var getter = AccessTools.PropertyGetter(typeof(T), names[i] + "Blittable");
                    if (getter == null) throw new MissingMethodException();
                    var dm = new DynamicMethod("RunLod_" + typeof(T).Name + names[i], typeof(RawCurve), new[] { typeof(T) }, typeof(RunLod).Module, true);
                    var il = dm.GetILGenerator(); var raw = il.DeclareLocal(getter.ReturnType); var dest = il.DeclareLocal(typeof(RawCurve));
                    il.Emit(OpCodes.Ldarga_S, (byte)0); il.Emit(OpCodes.Call, getter); il.Emit(OpCodes.Stloc, raw);
                    foreach (var field in typeof(RawCurve).GetFields(BindingFlags.Public | BindingFlags.Instance))
                    {
                        var source = AccessTools.Field(getter.ReturnType, field.Name);
                        if (source == null || source.FieldType != field.FieldType) throw new MissingFieldException();
                        il.Emit(OpCodes.Ldloca, dest); il.Emit(OpCodes.Ldloca, raw); il.Emit(OpCodes.Ldfld, source); il.Emit(OpCodes.Stfld, field);
                    }
                    il.Emit(OpCodes.Ldloc, dest); il.Emit(OpCodes.Ret);
                    result[i] = (Func<T, RawCurve>)dm.CreateDelegate(typeof(Func<T, RawCurve>));
                }
                return result;
            }
        }
        static float SizeMin(RawCurve c)
        {
            float result;
            switch (c.m_Mode)
            {
                case ParticleSystemCurveMode.Constant: result = c.m_ConstantMax; break;
                case ParticleSystemCurveMode.TwoConstants:
                    result = Finite(c.m_ConstantMin) && Finite(c.m_ConstantMax) ? Mathf.Min(c.m_ConstantMin, c.m_ConstantMax) : float.NaN; break;
                case ParticleSystemCurveMode.Curve:
                    result = c.m_CurveMultiplier >= 0 ? CurveMin(c.m_CurveMax) * c.m_CurveMultiplier : float.NaN; break;
                case ParticleSystemCurveMode.TwoCurves:
                    float lo = CurveMin(c.m_CurveMin), hi = CurveMin(c.m_CurveMax);
                    result = c.m_CurveMultiplier >= 0 && Finite(lo) && Finite(hi) ? Mathf.Min(lo, hi) * c.m_CurveMultiplier : float.NaN; break;
                default: return float.NaN;
            }
            return Finite(result) && result > 0 ? result : float.NaN;
        }
        static float ParticleMag(Cand cd, float ppw, int w, int h)
        {
            var ps = cd.System; var main = ps.main;
            if (cd.Particle.renderMode != ParticleSystemRenderMode.Billboard || cd.Particle.maskInteraction != SpriteMaskInteraction.None
                || ps.trails.enabled || cd.Particle.trailMaterial) return 0;
            float size = main.startSize3D ? AxesMin(SizeMin(ParticleCurves.Main[1](main)), SizeMin(ParticleCurves.Main[2](main)), SizeMin(ParticleCurves.Main[3](main))) : SizeMin(ParticleCurves.Main[0](main));
            // A size tween changes future emissions, not the start size of particles already
            // alive. Bound those too; the reusable buffer is reserved during gathering.
            int alive = ps.particleCount;
            if (!Finite(size) || size <= 0 || alive == 0 || alive > liveParticles.Length) return 0;
            int count = ps.GetParticles(liveParticles);
            if (count != alive) return 0;
            for (int i = 0; i < count; i++)
            {
                var start = liveParticles[i].startSize3D;
                float v = AxesMin(start.x, start.y, start.z);
                if (!Finite(v) || v <= 0) return 0;
                size = Mathf.Min(size, v);
            }
            var life = ps.sizeOverLifetime;
            if (life.enabled) size *= life.separateAxes ? AxesMin(SizeMin(ParticleCurves.Life[1](life)), SizeMin(ParticleCurves.Life[2](life)), SizeMin(ParticleCurves.Life[3](life))) : SizeMin(ParticleCurves.Life[0](life));
            var speed = ps.sizeBySpeed;
            if (speed.enabled) size *= speed.separateAxes ? AxesMin(SizeMin(ParticleCurves.Speed[1](speed)), SizeMin(ParticleCurves.Speed[2](speed)), SizeMin(ParticleCurves.Speed[3](speed))) : SizeMin(ParticleCurves.Speed[0](speed));
            if (!Finite(size) || size <= 0) return 0;
            var tex = cd.M.mainTexture; if (!tex) return 0;
            float tw = tex.width, th = tex.height;
            var sheet = ps.textureSheetAnimation;
            if (sheet.enabled)
            {
                if (sheet.mode != ParticleSystemAnimationMode.Grid || sheet.numTilesX < 1 || sheet.numTilesY < 1) return 0;
                tw /= sheet.numTilesX; th /= sheet.numTilesY;
            }
            if (cd.M.HasProperty("RepeatX")) tw *= Mathf.Max(1f, Mathf.Abs(cd.M.GetFloat("RepeatX")));
            if (cd.M.HasProperty("RepeatY")) th *= Mathf.Max(1f, Mathf.Abs(cd.M.GetFloat("RepeatY")));
            var scale = ps.transform.lossyScale;
            float s = Mathf.Min(Mathf.Abs(scale.x), Mathf.Min(Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
            // Shape scaling does not scale the particles themselves. Local scaling may be
            // smaller than hierarchy scaling; using their minimum is conservative.
            if (main.scalingMode == ParticleSystemScalingMode.Shape) s = Mathf.Min(s, 1f);
            if (main.scalingMode == ParticleSystemScalingMode.Local)
            {
                var local = ps.transform.localScale;
                s = Mathf.Min(s, Mathf.Min(Mathf.Abs(local.x), Mathf.Min(Mathf.Abs(local.y), Mathf.Abs(local.z))));
            }
            if (!Finite(s) || s <= 0 || !Finite(tw) || !Finite(th)) return 0;
            return Mathf.Min(size * s * ppw, cd.Particle.maxParticleSize * Mathf.Min(w, h)) / Mathf.Max(1, Mathf.Max(tw, th));
        }
        static float AxesMin(float x, float y, float z) => Finite(x) && Finite(y) && Finite(z) ? Mathf.Min(x, Mathf.Min(y, z)) : float.NaN;
        // GPU 장식 맡기 검사용: RunLod 가 붙이는 보임 알림 컴포넌트(그림에 영향 없음)
        internal static bool IsOwnTag(Component c) => c is VisTag || c is BarrierTag;
        sealed class VisTag : MonoBehaviour
        {
            internal Cand C;
            void OnBecameVisible() { if (C != null) { C.Vis = true; if (!C.Listed) { C.Listed = true; visible.Add(C); } } }
            void OnBecameInvisible() { if (C != null) C.Vis = false; }
        }
        static readonly List<Cand> visible = new List<Cand>();
        sealed class BarrierTag : MonoBehaviour
        {
            internal bool Vis;
            void OnBecameVisible() { Vis = true; }
            void OnBecameInvisible() { Vis = false; }
        }
        static BarrierTag[] barrierTag = new BarrierTag[0];
        static int tagsThisFrame; internal static int TagsPerFrame = 300;
        sealed class Cand { public bool Vis, Listed, ParticleAllowed; public VisTag Tag; public Renderer R; public SpriteRenderer Sprite; public ParticleSystemRenderer Particle; public ParticleSystem System; public int Layer; public bool Moved; public Item It = new Item(); public float TexW, TexH; public Material M; public int Shader, Mat, Q, SlId, SlVal = int.MinValue; }
        static readonly Comparison<Item> order = (x, y) =>
        {
            int k = x.Q.CompareTo(y.Q); if (k != 0) return k;
            k = x.Lv.CompareTo(y.Lv); if (k != 0) return k;
            k = x.Ord.CompareTo(y.Ord); if (k != 0) return k;
            k = y.Dist.CompareTo(x.Dist); if (k != 0) return k;
            k = x.Shader.CompareTo(y.Shader); if (k != 0) return k;
            return x.Mat.CompareTo(y.Mat);
        };
        sealed class Item { public Renderer R; public Cand C; public int Q, Lv, Ord, SlId; public float Dist; public int Shader, Mat; public Bounds Bounds; public float X0, Y0, X1, Y1, Mag; }
        internal static float MinSave = 1.0f;   // 묶음으로 아끼는 화면 층 수가 이보다 커야 묶는다
        internal static int MaxCands = 8000;     // 후보가 이보다 많은 맵은 끈다(매 프레임 비용)
        static bool mapOff, active, anyMoved;
        internal static bool Force;   // 측정: 쉬는 모드 없이 늘 켬(화질 비교용)
        internal static float ActiveSave = 3f, MinGpuWait = 0.3f;
        static readonly FrameTiming[] ft = new FrameTiming[1]; static float waitEma = 1f; internal static int ProbeEvery = 30;
        static float savedNow, savedEma; internal static int ModeSwitches, ActiveOffFrames;

        static Camera cam;
        static CommandBuffer cb;
        static Shader shader;
        static readonly List<Cand> cands = new List<Cand>();
        static readonly List<Renderer> barriers = new List<Renderer>();
        static long[] barrierKey = new long[0]; static Renderer[] barrierByKey = new Renderer[0];
        internal static int BarrierChecks;
        static int[] barrierStamp = new int[0];
        static bool[] barrierValue = new bool[0];
        static int queryStamp;
        // A fully examined empty block can be skipped for the rest of this camera callback.
        const int BarrierBlock = 32;
        static int[] emptyBlockStamp = new int[0];
        static int gatheredFrame = -9999;
        static readonly List<Item> items = new List<Item>();
        static readonly List<GameObject> comps = new List<GameObject>();
        static readonly List<Material> compMats = new List<Material>();
        static readonly List<RenderTexture> rts = new List<RenderTexture>();
        static Mesh quad;

        // 합치기 셰이더는 모드에 넣은 작은 번들(StutterFix.runlod)에서 한 번만 읽는다.
        static AssetBundle bundle;
        static Shader LoadShader()
        {
            if (shader) return shader;
            if (!bundle)
            {
                using (var s = typeof(RunLod).Assembly.GetManifestResourceStream("StutterFix.runlod"))
                {
                    if (s == null) return null;
                    var bytes = new byte[s.Length]; int o = 0; while (o < bytes.Length) { int n = s.Read(bytes, o, bytes.Length - o); if (n <= 0) break; o += n; }
                    bundle = AssetBundle.LoadFromMemory(bytes);
                }
            }
            shader = bundle ? bundle.LoadAsset<Shader>("Assets/Shaders/RunLodComposite.shader") : null;
            return shader;
        }

        // 곡 시작/끝(Hitch)에서 부른다. 설정 RunLod 가 켜져 있을 때만.
        internal static void SongStarted()
        {
            if (Main.Config == null || !Main.Config.RunLod) return;
            // 같은 맵을 다시 하면 모은 결과를 그대로 쓴다(재생 준비에서 이미 모았거나 지난 판에 모은 것)
            try { if (!On) Command("on"); active = false; savedEma = 0; anyMoved = false; }
            catch (Exception e) { Main.Entry.Logger.Log("[묶음 LOD] 켜기 실패: " + e.Message); }
        }
        // 설정으로 켠 경우만 곡 끝(일시정지 포함)에 멈춘다. 측정 명령으로 켠 경우는 그대로 둔다(같은 프레임 비교용).
        internal static void SongEnded() { if (On && Main.Config != null && Main.Config.RunLod) { try { Pause(); } catch { } } }

        // 재생 시작 때 장식 다시 설정(ResetDecorations) 뒤에 한 번에 모은다. 게임 화면의 같은 맵 재시도는 비용 0.
        internal static void Install(Harmony h)
        {
            // 장식이 모두 만들어진 뒤인 장식 다시 설정(재생 시작·다시 하기) 뒤에 모은다
            var rd = AccessTools.Method(typeof(scrDecorationManager), "ResetDecorations");
            if (rd == null) { Main.Entry.Logger.Log("[묶음 LOD] 장식 다시 설정 함수를 못 찾음: 곡 시작 때 나눠 모음"); return; }
            h.Patch(rd, postfix: new HarmonyMethod(typeof(RunLod), nameof(PrepPostfix)) { priority = Priority.Last });
        }
        public static void PrepPostfix()
        {
            if (Main.Config == null || !Main.Config.RunLod) return;
            // 게임 화면(목록에서 연 맵)은 같은 맵 재시도에 모은 결과를 그대로 쓴다. 편집기는 장식이 바뀌었을 수 있어 매번 다시 모은다.
            bool editor = false; try { editor = scnEditor.instance != null; } catch { }
            if (gstate == 2 && !editor) return;
            try
            {
                if (gstate != 0) ForgetLevel();
                GatherBegin();
                int guard = 0;
                while (gstate == 1 && guard++ < 100000) GatherStep();
            }
            catch (Exception e) { Main.Entry.Logger.Log("[묶음 LOD] 준비 중 모으기 실패: " + e.Message); ForgetLevel(); }
        }
        internal static void Unload() { try { Off(); } catch { } if (bundle) { bundle.Unload(true); bundle = null; } shader = null; }

        internal static string Command(string arg)
        {
            var a = arg.Trim().Split(' ');
            if (a[0] == "off") { Off(); return "꺼짐"; }
            if (a[0] == "state") return State();
            if (a[0] == "pause") { Pause(); return "멈춤(모은 결과 유지)"; }
            if (a[0] == "particles")
            {
                bool enabled = a.Length < 2 || a[1] != "off";
                if (Particles != enabled) { ForgetLevel(); Particles = enabled; }
                return "particles=" + Particles + " api=" + (!Particles || ParticleCurves.Ready) + " (다음 카메라 프레임에 다시 모음)";
            }
            if (a[0] == "cpu") { CpuFast = a.Length < 2 || a[1] != "off"; return "cpu=" + CpuFast; }
            if (a[0] == "verify") { Verify = a.Length < 2 || a[1] != "off"; return "verify=" + Verify; }
            if (a[0] == "resume") { if (!On && shader) { On = true; Camera.onPreCull += PreCull; } return State(); }
            if (a[0] == "why")
            {
                // 진단: 단순 그리기가 아닌 장식 SpriteRenderer 가 왜 후보가 아닌지 묶어 센다(곡 중 한 번만 부를 것)
                var cnt = new Dictionary<string, int>();
                foreach (var sr in UnityEngine.Object.FindObjectsByType<SpriteRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    if (!sr || sr.drawMode == SpriteDrawMode.Simple || !sr.GetComponentInParent<scrVisualDecoration>()) continue;
                    var wm = sr.sharedMaterial; string sh = wm && wm.shader ? wm.shader.name : "-";
                    string bl = wm && wm.HasProperty("_SrcBlend") ? wm.GetFloat("_SrcBlend") + "/" + wm.GetFloat("_DstBlend") : "none";
                    var tex = sr.sprite ? sr.sprite.texture : null;
                    string k = sr.drawMode + " mask=" + sr.maskInteraction + " sh=" + sh + " blend=" + bl + " col=" + (sr.GetComponent<Collider2D>() != null)
                        + " sg=" + (sr.GetComponentInParent<UnityEngine.Rendering.SortingGroup>() != null) + " vis=" + sr.isVisible
                        + " wrap=" + (tex ? tex.wrapMode.ToString() : "-") + " tiling=" + sr.tileMode;
                    cnt.TryGetValue(k, out int c); cnt[k] = c + 1;
                }
                var sb = new System.Text.StringBuilder("why:");
                foreach (var kv in cnt) sb.Append(" | ").Append(kv.Value).Append("x ").Append(kv.Key);
                return sb.ToString();
            }
            if (a[0] == "tiled") { Tiled = a.Length < 2 || a[1] != "off"; return "tiled=" + Tiled + " (맵을 새로 열어야 모음에 반영)"; }
            if (a[0] == "force") { Force = a.Length < 2 || a[1] != "off"; return "force=" + Force; }
            if (a.Length > 0 && float.TryParse(a[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) && m > 0) MinMag = m;
            if (!On)
            {
                shader = LoadShader();
                if (!shader || !shader.isSupported) return "합치기 셰이더 없음";
                On = true; Camera.onPreCull += PreCull;
            }
            return State();
        }

        static string State() => "on=" + On + " minMag=" + MinMag + " tiled=" + Tiled + "/" + TiledCands + (onlyTiled ? "(tiled-only)" : "") + " frames=" + Frames + " runs=" + RunsDrawn + " members=" + MembersDrawn
            + " particles=" + Particles + "/" + ParticleCands + " 파티클구성원/프레임 " + (Frames > 0 ? (ParticleMembersDrawn / (float)Frames).ToString("F2") : "-")
            + " cpu=" + CpuFast + " verify=" + Verify + "/" + VerifyFrames + " 검증다름=" + VerifyDiffs
            + " 후보 " + cands.Count + " 장벽 " + barriers.Count + " 장벽검사/프레임 " + (Frames > 0 ? (BarrierChecks / (float)Frames).ToString("F0") : "-") + string.Format(" ms/프레임 전체 {0:F3} 후보 {1:F3} 정렬 {2:F3} 묶음 {3:F3}", Ms(tTotal), Ms(tCand), Ms(tSort), Ms(tRuns)) + " 이득평균 " + savedEma.ToString("F2") + " GPU대기 " + waitEma.ToString("F2") + " 켜짐 " + active + " 전환 " + ModeSwitches + " 쉰프레임 " + ActiveOffFrames + " 묶음당 평균 " + (RunsDrawn > 0 ? (MembersDrawn / (float)RunsDrawn).ToString("F1") : "-");

        static double Ms(long ticks) => Frames > 0 ? ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / Frames : 0;

        // 멈춤: 그리기만 원래대로 돌리고 모은 결과(후보·장벽·알림)는 같은 맵 재시도를 위해 둔다.
        internal static void Pause()
        {
            if (On) Camera.onPreCull -= PreCull;
            On = false;
            if (cam && cb != null) cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, cb);
            cb?.Release(); cb = null; cam = null;
            foreach (var c in cands) Restore(c);
            anyMoved = false;
            foreach (var g in comps) if (g && g.activeSelf) g.SetActive(false);
        }

        // 맵이 바뀌면 모은 결과를 버린다.
        // 알림 컴포넌트는 지우지 않고 떼어만 둔다: Destroy 는 프레임 끝에 일어나서, 바로 다시 모을 때 GetComponent 가
        // 지워질 예정인 것을 돌려주면 알림이 끊긴다. 다시 모을 때 그대로 다시 쓴다. 모드를 내릴 때(Off)만 지운다.
        internal static void ForgetLevel()
        {
            if (cb != null) cb.Clear();
            foreach (var g in comps) if (g && g.activeSelf) g.SetActive(false);
            foreach (var c in cands) { Restore(c); if (c.Tag) c.Tag.C = null; }
            barrierTag = new BarrierTag[0]; barrierKey = new long[0]; barrierByKey = new Renderer[0];
            barrierStamp = new int[0]; barrierValue = new bool[0]; emptyBlockStamp = new int[0];
            ParticleCands = 0; anyMoved = false;
            liveParticles = new ParticleSystem.Particle[0]; particleCapacity = 0;
            cands.Clear(); barriers.Clear(); visible.Clear(); gatheredFrame = -9999; gstate = 0; gAll = null; mapOff = false; onlyTiled = false;
            items.Clear(); legacyRuns.Clear(); fastRuns.Clear();
        }

        internal static void Off()
        {
            Pause();
            // Include detached tags retained across ForgetLevel/retries, too.
            foreach (var tag in UnityEngine.Object.FindObjectsByType<VisTag>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (tag) UnityEngine.Object.Destroy(tag);
            foreach (var tag in UnityEngine.Object.FindObjectsByType<BarrierTag>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (tag) UnityEngine.Object.Destroy(tag);
            ForgetLevel();
            foreach (var g in comps) if (g)
            {
                var mf = g.GetComponent<MeshFilter>(); if (mf && mf.sharedMesh) UnityEngine.Object.Destroy(mf.sharedMesh);
                UnityEngine.Object.Destroy(g);
            }
            foreach (var m in compMats) if (m) UnityEngine.Object.Destroy(m);
            foreach (var r in rts) if (r) { r.Release(); UnityEngine.Object.Destroy(r); }
            comps.Clear(); compMats.Clear(); rts.Clear();
            if (quad) UnityEngine.Object.Destroy(quad); quad = null;
            Particles = CpuFast = Verify = false;
        }

        static void Restore(Cand c) { if (c.Moved && c.R) c.R.gameObject.layer = c.Layer; c.Moved = false; }

        // 모으기는 여러 프레임에 나눈다(곡 시작 멈춤 방지): 0 시작 전, 1 분류 중, 2 끝.
        static int gstate; static Renderer[] gAll; static int gPos; static long gTicks, gFind;
        static readonly List<long> gKeys = new List<long>(); static readonly List<Renderer> gRens = new List<Renderer>();
        static readonly Dictionary<int, bool> shaderOk = new Dictionary<int, bool>();
        internal static int GatherChunk = 1500;
        static readonly AccessTools.FieldRef<scrDecorationManager, List<scrDecoration>> allDecos =
            AccessTools.FieldRefAccess<scrDecorationManager, List<scrDecoration>>("allDecorations");

        static void GatherBegin()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (var c in cands) { Restore(c); if (c.Tag) c.Tag.C = null; }
            cands.Clear(); barriers.Clear(); visible.Clear(); gKeys.Clear(); gRens.Clear(); gTicks = 0;
            barrierKey = new long[0]; barrierByKey = new Renderer[0]; barrierTag = new BarrierTag[0];
            // 장식이 아주 많은 맵은 렌더러를 훑지도 않고 끈다
            int decoCount = 0;
            try { var dm = scrDecorationManager.instance; if (dm != null) decoCount = allDecos(dm)?.Count ?? 0; } catch { }
            mapOff = decoCount > MaxCands;
            // 장식이 아주 많은 맵(GPU 장식이 대부분 맡음)에서도 Tiled 를 켜면 Tiled 장식만 모은다(GPU 장식이 못 맡는 반복 배경)
            onlyTiled = mapOff && Tiled; if (onlyTiled) mapOff = false;
            if (mapOff) { gstate = 2; gatheredFrame = Time.frameCount; Main.Entry.Logger.Log("[묶음 LOD] 이 맵은 끔: 장식 " + decoCount + " > " + MaxCands); return; }
            gAll = UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            gPos = 0; gstate = 1; TiledCands = ParticleCands = particleCapacity = 0;
            gFind = System.Diagnostics.Stopwatch.GetTimestamp() - t0; gTicks += gFind;
        }

        static void GatherStep()
        {
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            int end = Math.Min(gAll.Length, gPos + GatherChunk);
            for (; gPos < end; gPos++)
            {
                var r = gAll[gPos];
                if (!r) continue;
                var m = r.sharedMaterial;
                bool cand = false;
                if (m && r is SpriteRenderer sr && ModeOk(sr) && (!onlyTiled || sr.drawMode == SpriteDrawMode.Tiled || RepeatDeco(sr)) && sr.maskInteraction == SpriteMaskInteraction.None)
                {
                    var sh = m.shader; int sid = sh ? sh.GetInstanceID() : 0;
                    if (!shaderOk.TryGetValue(sid, out bool ok)) { ok = sh && (sh.name == "Sprites/Tile" || sh.name == "Sprites/Default"); shaderOk[sid] = ok; }
                    // 미리 곱한 알파 덮기(One, OneMinusSrcAlpha)만
                    if (ok && (!m.HasProperty("_SrcBlend") || (m.GetFloat("_SrcBlend") == 1f && m.GetFloat("_DstBlend") == 10f))
                        && r.GetComponentInParent<scrVisualDecoration>() && !r.GetComponent<Collider2D>() && !r.GetComponentInParent<SortingGroup>())
                    {
                        cand = true; if (sr.drawMode == SpriteDrawMode.Tiled || (Tiled && RepeatDeco(sr))) TiledCands++;
                        var cd = new Cand { R = sr, Sprite = sr, Layer = r.gameObject.layer };
                        var tag = r.GetComponent<VisTag>() ?? r.gameObject.AddComponent<VisTag>(); tag.C = cd; cd.Tag = tag;
                        if (r.isVisible) { cd.Vis = true; cd.Listed = true; visible.Add(cd); }
                        cands.Add(cd);
                    }
                }
                if (!cand && Particles && !onlyTiled && r is ParticleSystemRenderer pr && ParticleOk(pr, m))
                {
                    var cd = new Cand { R = pr, Particle = pr, System = pr.GetComponent<ParticleSystem>(), Layer = r.gameObject.layer };
                    var tag = r.GetComponent<VisTag>() ?? r.gameObject.AddComponent<VisTag>(); tag.C = cd; cd.Tag = tag;
                    if (r.isVisible) { cd.Vis = true; cd.Listed = true; visible.Add(cd); }
                    cands.Add(cd); ParticleCands++; cand = true;
                    particleCapacity = Math.Max(particleCapacity, Math.Min(65536, cd.System.main.maxParticles));
                }
                if (!cand && m && m.renderQueue > 2500)
                {
                    barriers.Add(r);
                    gKeys.Add(Key(m.renderQueue, SortingLayer.GetLayerValueFromID(r.sortingLayerID), r.sortingOrder)); gRens.Add(r);
                }
            }
            if (gPos >= gAll.Length)
            {
                // 장벽은 정렬 키 순으로(후보 키 범위 안만). 보임 알림은 처음 훑을 때 조금씩 붙인다.
                long lo = long.MaxValue, hi = long.MinValue;
                foreach (var cd in cands) { var mm = cd.R.sharedMaterial; if (!mm) continue; long k = Key(mm.renderQueue, SortingLayer.GetLayerValueFromID(cd.R.sortingLayerID), cd.R.sortingOrder); if (k < lo) lo = k; if (k > hi) hi = k; }
                var ks = new List<long>(); var rs = new List<Renderer>();
                for (int i = 0; i < gKeys.Count; i++) if (gKeys[i] >= lo && gKeys[i] <= hi) { ks.Add(gKeys[i]); rs.Add(gRens[i]); }
                barrierKey = ks.ToArray(); barrierByKey = rs.ToArray(); Array.Sort(barrierKey, barrierByKey);
                // Reserve managed planning buffers during gathering, never in the frame loop.
                items.Capacity = Math.Max(items.Capacity, cands.Count);
                visible.Capacity = Math.Max(visible.Capacity, cands.Count);
                legacyRuns.Capacity = Math.Max(legacyRuns.Capacity, cands.Count);
                fastRuns.Capacity = Math.Max(fastRuns.Capacity, cands.Count);
                if (Particles) liveParticles = new ParticleSystem.Particle[particleCapacity];
                barrierTag = new BarrierTag[barrierByKey.Length];
                barrierStamp = new int[barrierByKey.Length]; barrierValue = new bool[barrierByKey.Length];
                emptyBlockStamp = new int[(barrierByKey.Length + BarrierBlock - 1) / BarrierBlock]; queryStamp = 0;
                gKeys.Clear(); gRens.Clear(); gAll = null; gstate = 2; gatheredFrame = Time.frameCount;
                gTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
                Main.Entry.Logger.Log("[묶음 LOD] 모음 끝: 합계 " + GMs(0, gTicks) + "ms (찾기 " + GMs(0, gFind) + ", 나머지는 프레임당 " + GatherChunk + "개씩), 후보 " + cands.Count + ", 후보 키 범위 안 장벽 " + barrierByKey.Length + " (전체 " + barriers.Count + ")");
                return;
            }
            gTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }

        static string GMs(long a, long b) => ((b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F1");

        static long Key(int q, int lv, int ord) => ((long)(q & 0xFFFF) << 48) | ((long)((lv + 32768) & 0xFFFF) << 32) | (uint)(ord + int.MinValue) ;

        static int LowerBound(long k) { int lo = 0, hi = barrierKey.Length; while (lo < hi) { int mid = (lo + hi) >> 1; if (barrierKey[mid] < k) lo = mid + 1; else hi = mid; } return lo; }

        // 키 [a, b] 안에 지금 보이는 장벽이 있나
        static bool BarrierBetween(long a, long b, int camMask)
        {
            for (int i = LowerBound(a); i < barrierKey.Length && barrierKey[i] <= b; i++)
            {
                var r = barrierByKey[i]; if (r is null) continue; BarrierChecks++;
                var tg = barrierTag[i];
                bool vis;
                if (tg is object) vis = tg.Vis;   // 보임 알림(관리 메모리 읽기)
                else
                {
                    try { vis = r.isVisible; } catch { barrierByKey[i] = null; continue; }
                    // 처음 훑은 장벽에는 보임 알림을 붙인다(프레임당 개수 제한)
                    if (tagsThisFrame < TagsPerFrame) { tagsThisFrame++; try { tg = r.GetComponent<BarrierTag>() ?? r.gameObject.AddComponent<BarrierTag>(); tg.Vis = vis; barrierTag[i] = tg; } catch { } }
                }
                if (vis && r.enabled && !r.forceRenderingOff && (camMask & (1 << r.gameObject.layer)) != 0) return true;
            }
            return false;
        }

        static bool BarrierAt(int i, int camMask)
        {
            if (barrierStamp[i] == queryStamp) return barrierValue[i];
            var r = barrierByKey[i]; bool hit = false;
            if (!(r is null))
            {
                BarrierChecks++;
                var tg = barrierTag[i]; bool vis = false;
                if (tg is object) vis = tg.Vis;
                else
                {
                    try { vis = r.isVisible; }
                    catch { barrierByKey[i] = null; r = null; }
                    if (!(r is null) && tagsThisFrame < TagsPerFrame)
                    {
                        tagsThisFrame++;
                        try { tg = r.GetComponent<BarrierTag>() ?? r.gameObject.AddComponent<BarrierTag>(); tg.Vis = vis; barrierTag[i] = tg; } catch { }
                    }
                }
                if (!(r is null)) hit = vis && r.enabled && !r.forceRenderingOff && (camMask & (1 << r.gameObject.layer)) != 0;
            }
            barrierStamp[i] = queryStamp; barrierValue[i] = hit; return hit;
        }
        static bool BarrierBetweenFast(long a, long b, int camMask)
        {
            int i = LowerBound(a);
            while (i < barrierKey.Length && barrierKey[i] <= b)
            {
                int block = i / BarrierBlock, end = Math.Min(barrierKey.Length, (block + 1) * BarrierBlock);
                if (emptyBlockStamp[block] == queryStamp) { i = end; continue; }
                bool full = i == block * BarrierBlock && barrierKey[end - 1] <= b;
                for (; i < end && barrierKey[i] <= b; i++) if (BarrierAt(i, camMask)) return true;
                if (full) emptyBlockStamp[block] = queryStamp;
            }
            return false;
        }

        static Matrix4x4 vp;
        static Vector3 VPt(Vector3 w) { var n = vp.MultiplyPoint(w); return new Vector3(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, 0); }
        // Renderer.bounds is a world-space AABB. A tilted camera needs all eight
        // corners, including both depths of the two mixed XY corners.
        static void ProjectBounds(Bounds b, out float x0, out float y0, out float x1, out float y1)
        {
            var lo = b.min; var hi = b.max;
            x0 = y0 = float.PositiveInfinity; x1 = y1 = float.NegativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                var p = VPt(new Vector3((i & 1) == 0 ? lo.x : hi.x,
                    (i & 2) == 0 ? lo.y : hi.y, (i & 4) == 0 ? lo.z : hi.z));
                x0 = Mathf.Min(x0, p.x); x1 = Mathf.Max(x1, p.x);
                y0 = Mathf.Min(y0, p.y); y1 = Mathf.Max(y1, p.y);
            }
        }
        static void ViewRect(Bounds b, Item it)
        {
            ProjectBounds(b, out var x0, out var y0, out var x1, out var y1);
            it.X0 = Mathf.Clamp01(x0); it.X1 = Mathf.Clamp01(x1);
            it.Y0 = Mathf.Clamp01(y0); it.Y1 = Mathf.Clamp01(y1);
        }

        static bool OnScreen(Bounds b, out float magDummy)
        {
            magDummy = 0;
            ProjectBounds(b, out var x0, out var y0, out var x1, out var y1);
            return x1 >= 0 && y1 >= 0 && x0 <= 1 && y0 <= 1;
        }

        static void PreCull(Camera c)
        {
            if (!On || !c || c.name != "Camera") return;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            try { PreCullBody(c); }
            catch (Exception e) { ForgetLevel(); if (++errors <= 3) Main.Entry.Logger.Log("[runlod] 오류, 다시 모음: " + e.GetType().Name + " " + e.Message); }
            finally { tTotal += System.Diagnostics.Stopwatch.GetTimestamp() - t0; }
        }
        static int errors;
        static long tTotal, tCand, tSort, tRuns;

        static void PreCullBody(Camera c)
        {
            // The configured feature belongs to playback. Hitch notices exits
            // on its next update; never carry its hidden originals into editing.
            // Explicit diagnostic commands can still compare a paused frame.
            if (Main.Config != null && Main.Config.RunLod
                && (SceneReset.Resetting || (scnEditor.instance && !scnEditor.instance.playMode)))
            { Pause(); return; }
            if (cam != c)
            {
                if (cam && cb != null) cam.RemoveCommandBuffer(CameraEvent.BeforeForwardAlpha, cb);
                cam = c; cb = cb ?? new CommandBuffer { name = "StutterFix RunLod" };
                cam.AddCommandBuffer(CameraEvent.BeforeForwardAlpha, cb);
                gatheredFrame = -9999;
            }
            if (gstate == 0) GatherBegin();
            if (gstate == 1) { GatherStep(); cb.Clear(); return; }
            if (mapOff) { cb.Clear(); for (int k = 0; k < comps.Count; k++) if (comps[k] && comps[k].activeSelf) comps[k].SetActive(false); return; }
            // GPU 를 기다리는 시간(메인 스레드의 present 대기). 쉬는 동안의 값만 켜기 판단에 쓴다(켜져 있으면 LOD 가 대기를 없앤다).
            FrameTimingManager.CaptureFrameTimings();
            if (!active && FrameTimingManager.GetLatestTimings(1, ft) == 1) waitEma += ((float)ft[0].cpuMainThreadPresentWaitTime - waitEma) * 0.05f;
            // 쉬는 모드: 최근 이득(아낀 화면 층 수의 이동 평균)이 작으면 30프레임에 한 번만 살펴본다.
            if (Force) active = true;
            if (!active && Time.frameCount % ProbeEvery != 0)
            {
                if (anyMoved) { foreach (var cd in visible) Restore(cd); anyMoved = false; cb.Clear(); for (int k = 0; k < comps.Count; k++) if (comps[k] && comps[k].activeSelf) comps[k].SetActive(false); }
                return;
            }
            savedNow = 0;
            Frames++; tagsThisFrame = 0;
            cb.Clear();
            var target = cam.targetTexture;
            int w = target ? target.width : Screen.width, h = target ? target.height : Screen.height;
            Vector3 cp = cam.transform.position, fwd = cam.transform.forward;
            items.Clear();
            int camMask = cam.cullingMask;
            // 후보: 지난 프레임에 옮겼더라도 원래 층 기준으로 본다
            // 화면 1 월드 단위의 픽셀 수(직교 카메라)
            float ppw = h / (2f * cam.orthographicSize);
            vp = cam.projectionMatrix * cam.worldToCameraMatrix;
            long s0 = System.Diagnostics.Stopwatch.GetTimestamp();
            // 보이는 후보(보임 알림으로 유지)와 옮겨 둔 묶음 장식만 본다
            for (int vi = visible.Count - 1; vi >= 0; vi--)
            {
                var cd = visible[vi];
                if (!cd.Vis && !cd.Moved) { cd.Listed = false; visible[vi] = visible[visible.Count - 1]; visible.RemoveAt(visible.Count - 1); continue; }
                var r = cd.R;
                if (r is null) { cd.Listed = false; visible[vi] = visible[visible.Count - 1]; visible.RemoveAt(visible.Count - 1); continue; }
                if (cd.Particle != null)
                {
                    int layer = r.gameObject.layer;
                    if (!cd.Moved || layer != HideLayer) { cd.Layer = layer; cd.Moved = false; }
                } // SetDepth changes both sorting and layer immediately.
                // 지난 프레임에 그려진(보임 알림) 후보는 켜짐·활성이 확실하다. 옮겨 둔 묶음 장식만 직접 확인한다.
                if (cd.Moved && (!r.enabled || r.forceRenderingOff || !r.gameObject.activeInHierarchy)) { Restore(cd); continue; }
                if ((camMask & (1 << cd.Layer)) == 0) { Restore(cd); continue; }
                Bounds b;
                try { b = r.bounds; } catch { cd.R = null; cd.Moved = false; cd.Listed = false; visible[vi] = visible[visible.Count - 1]; visible.RemoveAt(visible.Count - 1); continue; }   // 지워진 장식
                if (cd.Moved && !OnScreen(b, out _)) { Restore(cd); continue; }
                // 재질·텍스처 크기는 캐시하고 32프레임마다(후보마다 엇갈려) 다시 본다
                if (cd.M is null || ((Frames + vi) & 31) == 0)
                {
                    var m0 = r.sharedMaterial;
                    if (cd.Particle != null)
                    {
                        cd.ParticleAllowed = ParticleOk(cd.Particle, m0);
                        if (!m0 || !m0.shader) { cd.M = null; Restore(cd); continue; }
                        cd.M = m0; cd.Shader = m0.shader.GetInstanceID(); cd.Mat = m0.GetInstanceID(); cd.Q = m0.renderQueue;
                    }
                    else if (!ReferenceEquals(m0, cd.M)) { if (!m0 || !cd.Sprite.sprite) { Restore(cd); continue; } cd.M = m0; cd.Shader = m0.shader.GetInstanceID(); cd.Mat = m0.GetInstanceID(); cd.Q = m0.renderQueue; TexSize(cd.Sprite, cd); }
                }
                if (Tiled && cd.Sprite != null && ((Frames + vi) & 31) == 0 && cd.Sprite.sprite) TexSize(cd.Sprite, cd);
                var sz = b.size;
                // A particle that becomes ineligible must still split runs as a full-resolution
                // item; omitting it could merge sprites across its sorting position.
                float mag = cd.Particle != null ? (cd.ParticleAllowed ? ParticleMag(cd, ppw, w, h) : 0) : Mathf.Min(sz.x * ppw / cd.TexW, sz.y * ppw / cd.TexH);
                int sl = r.sortingLayerID; if (sl != cd.SlId || cd.SlVal == int.MinValue) { cd.SlId = sl; cd.SlVal = SortingLayer.GetLayerValueFromID(sl); }
                var it = cd.It; it.R = r; it.C = mag >= MinMag ? cd : null; it.Mag = mag; it.Q = cd.Q; it.Lv = cd.SlVal; it.Ord = r.sortingOrder; it.SlId = sl; it.Bounds = b;
                it.Dist = Vector3.Dot(b.center - cp, fwd); it.Shader = cd.Shader; it.Mat = cd.Mat;
                if (it.C != null) ViewRect(b, it);
                else Restore(cd);
                items.Add(it);
            }
            // 유니티 투명 정렬: 큐, 정렬층, 순서, 먼 것부터, 셰이더 ID, 재질 ID
            long s1 = System.Diagnostics.Stopwatch.GetTimestamp(); tCand += s1 - s0;
            items.Sort(order);
            long s2 = System.Diagnostics.Stopwatch.GetTimestamp(); tSort += s2 - s1;
            // 후보끼리 이웃한 두 장식 사이(키 포함 범위)에 보이는 장벽이 있으면 묶음을 끊는다. 키가 같은 장벽도 끊는다(동점 자리 보장 불가).
            int compIdx = 0;
            if (!CpuFast && !Verify) DrawLegacyRuns(camMask, w, h, ref compIdx);
            else
            {
                if (++queryStamp == int.MaxValue)
                {
                    Array.Clear(barrierStamp, 0, barrierStamp.Length); Array.Clear(emptyBlockStamp, 0, emptyBlockStamp.Length); queryStamp = 1;
                }
                if (Verify)
                {
                    BuildRuns(false, camMask, legacyRuns);
                    BuildRuns(true, camMask, fastRuns);
                    VerifyPlans();
                }
                else BuildRuns(CpuFast, camMask, CpuFast ? fastRuns : legacyRuns);
                var plans = CpuFast ? fastRuns : legacyRuns;
                // No renderer state changes until both plans have been constructed.
                int pi = 0;
                for (int i = 0; i < items.Count; i++)
                {
                    while (pi < plans.Count && i >= plans[pi].Start + plans[pi].Count) pi++;
                    if (items[i].C != null && (pi >= plans.Count || i < plans[pi].Start)) Restore(items[i].C);
                }
                for (int i = 0; i < plans.Count; i++)
                {
                    var plan = plans[i]; savedNow += plan.Save;
                    rx0 = plan.X0; ry0 = plan.Y0; rx1 = plan.X1; ry1 = plan.Y1; runDiv = plan.Div;
                    Emit(plan.Start, plan.Count, compIdx++, w, h);
                }
            }
            tRuns += System.Diagnostics.Stopwatch.GetTimestamp() - s2;
            // 켜진 동안은 매 프레임, 쉬는 동안은 살펴본 프레임만으로 평균을 갱신한다
            savedEma += (savedNow - savedEma) * (active ? 1f / 60f : 0.25f);
            bool was = active; active = Force || (active ? savedEma >= ActiveSave * 0.6f : savedEma >= ActiveSave && waitEma >= MinGpuWait);
            if (was != active) { ModeSwitches++; }
            if (!active) ActiveOffFrames++;
            anyMoved = compIdx > 0;
            for (int k = compIdx; k < comps.Count; k++) if (comps[k] && comps[k].activeSelf) comps[k].SetActive(false);
            if (compIdx > 0) cb.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);
        }

        static void DrawLegacyRuns(int camMask, int w, int h, ref int compIdx)
        {
            int run = 0;
            for (int i = 0; i <= items.Count; i++)
            {
                bool ok = i < items.Count && items[i].C != null;
                if (ok)
                {
                    long ki = Key(items[i].Q, items[i].Lv, items[i].Ord);
                    if (run > 0 && BarrierBetween(Key(items[i - 1].Q, items[i - 1].Lv, items[i - 1].Ord), ki, camMask))
                    {
                        Flush(i, run, ref compIdx, w, h); run = 0;
                    }
                    if (run == 0 && BarrierBetween(ki, ki, camMask)) { Restore(items[i].C); items[i].C = null; ok = false; }
                }
                if (ok) { run++; continue; }
                Flush(i, run, ref compIdx, w, h); run = 0;
            }
        }

        static void Flush(int end, int run, ref int compIdx, int w, int h)
        {
            bool go = false;
            if (run >= MinRun)
            {
                // 아끼는 층 수 = 장식들 화면 넓이 합 - 저해상도 몫(1/16) - 합치기 사각형 넓이
                float sum = 0, x0 = 1, y0 = 1, x1 = 0, y1 = 0, minMag = float.MaxValue;
                for (int k = end - run; k < end; k++) { var it = items[k]; sum += (it.X1 - it.X0) * (it.Y1 - it.Y0); x0 = Mathf.Min(x0, it.X0); y0 = Mathf.Min(y0, it.Y0); x1 = Mathf.Max(x1, it.X1); y1 = Mathf.Max(y1, it.Y1); minMag = Mathf.Min(minMag, it.Mag); }
                float union = Mathf.Max(0, x1 - x0) * Mathf.Max(0, y1 - y0);
                // 해상도: 묶음에서 가장 덜 확대된 장식 기준(4배 이상 1/4, 아니면 1/2)
                runDiv = minMag >= 4f ? 4 : 2;
                float save = sum * (1f - 1f / (runDiv * runDiv)) - union;
                go = save > MinSave;
                if (go) savedNow += save;
                if (go) { rx0 = x0; ry0 = y0; rx1 = x1; ry1 = y1; }
            }
            if (go) Emit(end - run, run, compIdx++, w, h);
            else for (int k = end - run; k < end; k++) Restore(items[k].C);
        }

        struct RunPlan
        {
            internal int Start, Count, Div;
            internal float X0, Y0, X1, Y1, Save;
        }
        static readonly List<RunPlan> legacyRuns = new List<RunPlan>();
        static readonly List<RunPlan> fastRuns = new List<RunPlan>();
        static void BuildRuns(bool fast, int camMask, List<RunPlan> output)
        {
            output.Clear(); int run = 0;
            for (int i = 0; i <= items.Count; i++)
            {
                bool ok = i < items.Count && items[i].C != null;
                if (ok)
                {
                    long ki = Key(items[i].Q, items[i].Lv, items[i].Ord);
                    if (run > 0)
                    {
                        long prev = Key(items[i - 1].Q, items[i - 1].Lv, items[i - 1].Ord);
                        if (fast ? BarrierBetweenFast(prev, ki, camMask) : BarrierBetween(prev, ki, camMask))
                        { PlanRun(i, run, output); run = 0; }
                    }
                    if (run == 0 && (fast ? BarrierBetweenFast(ki, ki, camMask) : BarrierBetween(ki, ki, camMask))) ok = false;
                }
                if (ok) { run++; continue; }
                PlanRun(i, run, output); run = 0;
            }
        }
        static void PlanRun(int end, int run, List<RunPlan> output)
        {
            if (run < MinRun) return;
            float sum = 0, x0 = 1, y0 = 1, x1 = 0, y1 = 0, minMag = float.MaxValue;
            for (int k = end - run; k < end; k++) { var it = items[k]; sum += (it.X1 - it.X0) * (it.Y1 - it.Y0); x0 = Mathf.Min(x0, it.X0); y0 = Mathf.Min(y0, it.Y0); x1 = Mathf.Max(x1, it.X1); y1 = Mathf.Max(y1, it.Y1); minMag = Mathf.Min(minMag, it.Mag); }
            float union = Mathf.Max(0, x1 - x0) * Mathf.Max(0, y1 - y0);
            int div = minMag >= 4f ? 4 : 2;
            float save = sum * (1f - 1f / (div * div)) - union;
            if (save > MinSave) output.Add(new RunPlan { Start = end - run, Count = run, Div = div, X0 = x0, Y0 = y0, X1 = x1, Y1 = y1, Save = save });
        }
        static void VerifyPlans()
        {
            VerifyFrames++;
            bool same = legacyRuns.Count == fastRuns.Count;
            for (int i = 0; same && i < legacyRuns.Count; i++)
            {
                var a = legacyRuns[i]; var b = fastRuns[i];
                // Both plans reference the same immutable sorted Item array: equal start/count
                // means the complete renderer ID sequence is identical (including ties).
                same = a.Start == b.Start && a.Count == b.Count && a.Div == b.Div
                    && a.X0 == b.X0 && a.Y0 == b.Y0 && a.X1 == b.X1 && a.Y1 == b.Y1 && a.Save == b.Save;
            }
            if (!same)
            {
                VerifyDiffs++;
                if (VerifyDiffs <= 10) Main.Entry.Logger.Log("[묶음 LOD] 검증 다름 frame=" + Time.frameCount + " old=" + legacyRuns.Count + " new=" + fastRuns.Count);
            }
        }

        static float rx0, ry0, rx1, ry1; static int runDiv = 4;
        static bool SameKey(Item a, Item b) => a.Q == b.Q && a.Lv == b.Lv && a.Ord == b.Ord && a.Dist == b.Dist;

        static void Emit(int start, int count, int idx, int w, int h)
        {
            int div = runDiv;
            int lw = Math.Max(1, w / div), lh = Math.Max(1, h / div);
            while (rts.Count <= idx) rts.Add(null);
            var rt = rts[idx];
            if (!rt || rt.width != lw || rt.height != lh)
            {
                if (rt) { rt.Release(); UnityEngine.Object.Destroy(rt); }
                var desc = cam.targetTexture ? cam.targetTexture.descriptor : new RenderTextureDescriptor(w, h, RenderTextureFormat.ARGB32, 0);
                desc.width = lw; desc.height = lh; desc.depthBufferBits = 0; desc.msaaSamples = 1; desc.useMipMap = false;
                rt = new RenderTexture(desc) { name = "StutterFix.RunLod", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp }; rt.Create(); rts[idx] = rt;
            }
            cb.SetRenderTarget(rt); cb.ClearRenderTarget(false, true, Color.clear);
            for (int k = start; k < start + count; k++)
            {
                var it = items[k];
                if (!it.C.Moved) { it.C.Layer = it.R.gameObject.layer; it.R.gameObject.layer = HideLayer; it.C.Moved = true; }
                cb.DrawRenderer(it.R, it.R.sharedMaterial, 0, 0);
                if (it.C.Particle != null) ParticleMembersDrawn++;
            }
            RunsDrawn++; MembersDrawn += count;
            // 합치기 사각형: 묶음 첫 장식의 정렬 자리
            while (comps.Count <= idx)
            {
                if (!quad)
                {
                    quad = new Mesh { name = "StutterFix.RunLod quad" };
                    quad.SetVertices(new[] { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(2, 0, 0), new Vector3(3, 0, 0) });
                    quad.SetIndices(new[] { 0, 1, 2, 0, 2, 3 }, MeshTopology.Triangles, 0, false);
                }
                var g = new GameObject("StutterFix.RunLod composite") { hideFlags = HideFlags.HideAndDontSave };
                var mf = g.AddComponent<MeshFilter>(); mf.sharedMesh = UnityEngine.Object.Instantiate(quad);
                var mat = new Material(shader); compMats.Add(mat);
                var mr = g.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat; mr.shadowCastingMode = ShadowCastingMode.Off; mr.receiveShadows = false;
                comps.Add(g);
            }
            var go = comps[idx]; if (!go.activeSelf) go.SetActive(true);
            var first = items[start];
            go.layer = first.C.Layer;
            var rend = go.GetComponent<MeshRenderer>();
            rend.sortingLayerID = first.SlId; rend.sortingOrder = first.Ord;
            var cmat = compMats[idx]; cmat.renderQueue = first.Q; cmat.mainTexture = rt; cmat.SetVector("_LodTarget", new Vector4(w, h, 1f / w, 1f / h));
            // 합치기 사각형은 묶음이 덮는 화면 범위만(저해상도 4픽셀 여유). 그림(RT)에 그릴 때 유니티는 투영의 위아래를 뒤집는다.
            float mx = 4f * div / w, my = 4f * div / h;
            float cx0 = Mathf.Clamp01(rx0 - mx) * 2 - 1, cx1 = Mathf.Clamp01(rx1 + mx) * 2 - 1;
            float vy0 = Mathf.Clamp01(ry0 - my) * 2 - 1, vy1 = Mathf.Clamp01(ry1 + my) * 2 - 1;
            bool flip = cam.targetTexture != null;
            float cy0 = flip ? -vy1 : vy0, cy1 = flip ? -vy0 : vy1;
            cmat.SetVector("_Rect", new Vector4(cx0, cy0, cx1, cy1));
            var mesh = go.GetComponent<MeshFilter>().sharedMesh;
            mesh.bounds = new Bounds(go.transform.InverseTransformPoint(first.Bounds.center), new Vector3(1e6f, 1e6f, 0));
        }
    }
}
