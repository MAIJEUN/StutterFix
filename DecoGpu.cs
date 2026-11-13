#if DEV || AUTOTEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace StutterFix
{
    // Explicit render-side experiment. Never enabled by configuration or load.
    // The command buffer is a separate transparent band: NOT a transparent-sort replacement.
    internal sealed class DecoGpu : MonoBehaviour
    {
        const string PatchId = "StutterFix.decogpu";
        static DecoGpu session;
        internal static bool Active => session != null && !session.stopping;
        static readonly AccessTools.FieldRef<scrDecoration, Color> ColorField = AccessTools.FieldRefAccess<scrDecoration, Color>("color");
        static readonly AccessTools.FieldRef<scrDecoration, float> Opacity = AccessTools.FieldRefAccess<scrDecoration, float>("opacity");
        static readonly AccessTools.FieldRef<scrDecoration, Color> Rendered = AccessTools.FieldRefAccess<scrDecoration, Color>("rendererColor");
        static readonly AccessTools.FieldRef<scrCamera, Camera> GameCamera = AccessTools.FieldRefAccess<scrCamera, Camera>("camobj");
        static readonly AccessTools.FieldRef<scrParallax, Vector2> StartPos = AccessTools.FieldRefAccess<scrParallax, Vector2>("startPosition");
        static readonly AccessTools.FieldRef<scrParallax, Vector2> EndPos = AccessTools.FieldRefAccess<scrParallax, Vector2>("endPosition");
        internal static long TotalTicks;
        static long restores, restoreMismatch;
        static string last = "not run";
        static string firstStop = "none";
        static string stoppedState = "";
        const CameraEvent DrawEvent = CameraEvent.BeforeForwardAlpha;
        sealed class Item
        {
            internal scrVisualDecoration D;
            internal SpriteRenderer R;
            internal Sprite Sprite;
            internal Material Source;
            internal TightLate.Entry Entry;
            internal bool Off;
            internal int Layer, Order, UnityLayer;
            internal Matrix4x4 Child;
            internal Transform ChildTransform;
            internal Vector2 Offset, Cropped, TextureScale;
            internal float ScaleMultiplier;
            internal Color Tint;
            internal string Signature;
        }
        sealed class Batch
        {
            internal Mesh Mesh;
            internal Material Material;
            internal readonly List<Item> Items = new List<Item>();
            internal readonly Matrix4x4[] Matrices = new Matrix4x4[1023];
            internal readonly Vector4[] Colors = new Vector4[1023];
            internal readonly MaterialPropertyBlock Props = new MaterialPropertyBlock();
        }
        readonly Dictionary<scrDecoration, Item> owned = new Dictionary<scrDecoration, Item>();
        readonly List<Batch> batches = new List<Batch>();
        Camera camera;
        scrDecorationManager manager;
        CommandBuffer commands;
        AssetBundle bundle;
        Shader shader;
        Harmony harmony;
        bool stopping, comparing;
        bool originalsHidden;
        int beginFrame;
        long preCullCalls, selectedPreCull, postRenderCalls;
        readonly Dictionary<int, int> observedCameras = new Dictionary<int, int>();
        readonly MaterialPropertyBlock sourceBlock = new MaterialPropertyBlock();
        long computeTicks, submitTicks, calls, draws;
        int currentDraws;
        static readonly int TintId = Shader.PropertyToID("_InstanceTint");

        internal static bool Owns(scrDecoration d) => session != null && !session.stopping
            && (object)d != null && session.owned.ContainsKey(d);
        internal static bool SkipLate(scrDecoration d)
        {
            if (!Owns(d)) return false;
            var s = session;
            if (!s.originalsHidden || s.comparing) return false;
            var i = s.owned[d];
            if (i.Entry != null) i.Entry.LastFrame = Time.frameCount;   // Late 를 건너뛰는 동안에도 엔트리를 최신으로 (아니면 다음 프레임 EngineReason=7)
            var reason = s.InvalidReason(i, true);
            if (reason != null) { Stop("SkipLate " + Describe(i) + " " + reason); return false; }
            return true;
        }
        internal static string Command(string arg)
        {
            var words = arg.Trim().Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return "decogpu begin [limit=500]|end|state|compare|probe";
            if (words[0] == "end") { Stop("explicit end"); return State(); }
            if (words[0] == "state") return State();
            if (words[0] == "probe") return Probe();
            if (words[0] == "compare") return session == null ? "decogpu inactive" : session.Compare();
            if (words[0] != "begin") return "decogpu begin [limit=500]|end|state|compare|probe";
            if (session != null) return "already active; " + State();
            int cap = words.Length > 1 ? int.Parse(words[1], CultureInfo.InvariantCulture) : 500;
            if (cap < 1 || cap > 10000) throw new ArgumentOutOfRangeException("limit", "1..10000");
            if (!Hitch.Playing || SceneReset.Resetting || !TightLate.Enabled || !ColorFast.Enabled || DecoEngine.Enabled
                || ADOBase.controller == null || !ADOBase.controller.disableV15Features)
                return "requires playing legacy map, TightLate+ColorFast on, DecoEngine off";
            if (GraphicsSettings.currentRenderPipeline != null || !SystemInfo.supportsInstancing)
                return "requires Built-in RP + instancing";
            // Check hooks before installing ours. No per-frame reflection/patch enumeration.
            foreach (var t in new[] {typeof(scrDecoration), typeof(scrVisualDecoration)})
                foreach (var m in t.GetMethods(AccessTools.all))
                {
                    if (m.Name != "UpdatePosition" && m.Name != "LogicUpdate" && m.Name != "ApplyColor"
                        && m.Name != "SetColor" && m.Name != "SetOpacity") continue;
                    var p = Harmony.GetPatchInfo(m);
                    if (p != null && p.Owners.Any(x => x != Main.Entry.Info.Id && x != "StutterFix.fuseprobe"))
                        return "foreign hook: " + t.Name + "." + m.Name;
                }
            GameObject go = null;
            firstStop = "none"; stoppedState = "";
            try
            {
                go = new GameObject("StutterFix.DecoGpu") {hideFlags = HideFlags.HideAndDontSave};
                var s = go.AddComponent<DecoGpu>(); session = s;
                s.Begin(cap); last = "active; awaiting first camera submission; visual equivalence UNVERIFIED";
            }
            catch (Exception e)
            {
                string reason = "begin failure " + e.GetType().Name + ": " + e.Message;
                Stop(reason);
                if (firstStop == "none") { firstStop = reason; last = "stopped: " + reason; }
                if (go && session == null) Destroy(go);
                throw;
            }
            return State();
        }
        static void Log(string s) => Main.Entry.Logger.Log("[decogpu] " + s);
        static readonly string[] BlockColors = { "_Color", "_RendererColor" };
        static readonly string[] BlendFields = { "OverlayColorPropertyId", "OverlayTexturePropertyId", "OverlayTextureSTPropertyId",
            "BlendStencilCompPropertyId", "NormalStencilCompPropertyId", "StencilIdPropertyId" };
        static readonly Dictionary<string, int> BlendProperties = new Dictionary<string, int>();
        static void PrepareBlockProperties()
        {
            if (BlendProperties.Count == BlendFields.Length) return;
            BlendProperties.Clear();
            // Explicit commands only; property IDs come from the installed game, not guessed shader names.
            var type = typeof(scrVisualDecoration).Assembly.GetType("BlendModes.ShaderUtilities");
            if (type == null) throw new InvalidOperationException("BlendModes.ShaderUtilities missing");
            foreach (string name in BlendFields)
            {
                var field = AccessTools.Field(type, name);
                if (field == null || field.FieldType != typeof(int)) throw new InvalidOperationException("missing blend property " + name);
                BlendProperties.Add(name, (int)field.GetValue(null));
            }
        }
        static string BlockReason(SpriteRenderer r, MaterialPropertyBlock block)
        {
            block.Clear(); r.GetPropertyBlock(block);
            foreach (var property in BlendProperties)
                if (block.HasProperty(property.Value)) return "MPB unsupported blend/mask property " + property.Key;
            if (block.HasTexture("_MainTex") && block.GetTexture("_MainTex") != r.sprite.texture) return "MPB _MainTex override";
            if (block.HasTexture("_AlphaTex") && block.GetTexture("_AlphaTex")) return "MPB _AlphaTex override";
            if (block.HasFloat("_EnableExternalAlpha") && block.GetFloat("_EnableExternalAlpha") != 0) return "MPB external alpha";
            foreach (string name in BlockColors)
                if (block.HasColor(name) && block.GetColor(name) != Color.white && block.GetColor(name) != r.color)
                    return "MPB " + name + "=" + block.GetColor(name);
            if (block.HasVector("_Flip"))
            {
                var f = block.GetVector("_Flip");
                if (f.x != (r.flipX ? -1 : 1) || f.y != (r.flipY ? -1 : 1)) return "MPB _Flip=" + f;
            }
            return null; // Unknown/custom properties cannot be enumerated through MPB; pixel compare remains required.
        }
        static string Probe()
        {
            PrepareBlockProperties();
            var output = new StringBuilder("[decogpu] probe\n");
            var sc = ADOBase.controller ? ADOBase.controller.camy : null;
            Camera selected = sc ? GameCamera(sc) : null;
            var candidates = TightLate.AllDecorations();
            var block = new MaterialPropertyBlock(); int samples = 0;
            foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (!c || !c.gameObject.scene.IsValid()) continue;
                int seen = 0; if (session != null) session.observedCameras.TryGetValue(c.GetInstanceID(), out seen);
                var layers = new SortedDictionary<int, int>();
                foreach (var d in candidates) if (d is scrVisualDecoration v && v && v.spriteRenderer)
                {
                    int layer = v.spriteRenderer.gameObject.layer;
                    if ((c.cullingMask & (1 << layer)) != 0) { layers.TryGetValue(layer, out int n); layers[layer] = n + 1; }
                }
                var rt = c.targetTexture;
                output.AppendLine("camera=" + c.name + " id=" + c.GetInstanceID() + " selected=" + (c == selected)
                    + " path=" + c.actualRenderingPath + " enabled=" + c.enabled + " active=" + c.gameObject.activeInHierarchy
                    + " depth=" + c.depth + " mask=" + c.cullingMask + " target=" + (rt ? rt.name + "/" + rt.GetInstanceID() + "/" + rt.width + "x" + rt.height : "screen")
                    + " preCull-seen=" + seen + " candidate-layers=" + string.Join(",", layers.Select(x => x.Key + ":" + x.Value)));
                foreach (CameraEvent ev in Enum.GetValues(typeof(CameraEvent)))
                    foreach (var cb in c.GetCommandBuffers(ev)) output.AppendLine("  buffer " + ev + " " + cb.name + " bytes=" + cb.sizeInBytes);
            }
            foreach (var d in candidates)
            {
                if (!(d is scrVisualDecoration v) || !v || !v.spriteRenderer || !v.spriteRenderer.sprite || !d.GetVisible()) continue;
                var r = v.spriteRenderer; string reason = BlockReason(r, block);
                output.Append("MPB id=" + d.GetInstanceID() + " layer=" + r.gameObject.layer + " shader=" + (r.sharedMaterial ? r.sharedMaterial.shader.name : "none")
                    + " has=" + r.HasPropertyBlock() + " known-reject=" + (reason ?? "none"));
                foreach (var name in new[] { "_MainTex", "_AlphaTex", "_Color", "_RendererColor", "_Flip", "_EnableExternalAlpha" })
                {
                    if (block.HasTexture(name)) { var tex = block.GetTexture(name); output.Append(" " + name + "=tex:" + (tex ? tex.GetInstanceID() : 0)); }
                    else if (block.HasColor(name)) output.Append(" " + name + "=" + block.GetColor(name));
                    else if (block.HasVector(name)) output.Append(" " + name + "=" + block.GetVector(name));
                    else if (block.HasFloat(name)) output.Append(" " + name + "=" + block.GetFloat(name));
                }
                foreach (var property in BlendProperties)
                {
                    int id = property.Value;
                    if (block.HasColor(id)) output.Append(" " + property.Key + "=" + block.GetColor(id));
                    else if (block.HasTexture(id)) { var tex = block.GetTexture(id); output.Append(" " + property.Key + "=tex:" + (tex ? tex.GetInstanceID() : 0)); }
                    else if (block.HasVector(id)) output.Append(" " + property.Key + "=" + block.GetVector(id));
                    else if (block.HasFloat(id)) output.Append(" " + property.Key + "=" + block.GetFloat(id));
                }
                output.AppendLine(); if (++samples == 16) break;
            }
            return output + State();
        }
        static string Key(Material m)
        {
            if (!m || !m.shader) return "none";
            var parts = new List<string> {m.shader.name, "queue=" + m.renderQueue, "keywords=" + string.Join(",", m.shaderKeywords)};
            for (int n = 0; n < m.shader.GetPropertyCount(); n++)
            {
                string k = m.shader.GetPropertyName(n);
                switch (m.shader.GetPropertyType(n))
                {
                    case ShaderPropertyType.Color: parts.Add(k + "=" + m.GetColor(k).ToString("R")); break;
                    case ShaderPropertyType.Vector: parts.Add(k + "=" + m.GetVector(k).ToString("R")); break;
                    case ShaderPropertyType.Float: case ShaderPropertyType.Range: parts.Add(k + "=" + m.GetFloat(k).ToString("R", CultureInfo.InvariantCulture)); break;
                    case ShaderPropertyType.Texture:
                        var tex = m.GetTexture(k); parts.Add(k + "=" + (tex ? tex.GetInstanceID() : 0) + "/" + m.GetTextureScale(k) + "/" + m.GetTextureOffset(k)); break;
                }
            }
            return string.Join(";", parts);
        }
        void Survey(List<scrDecoration> running)
        {
            var sprites = new HashSet<int>(); var textures = new HashSet<int>(); var materials = new HashSet<int>();
            var groups = new SortedDictionary<string, int>(); var sizes = new int[5];
            int image = 0, mask = 0, hitbox = 0, mesh = 0, visible = 0;
            foreach (var d in running)
            {
                if (!d) continue;
                if (d.hitbox != 0) hitbox++;
                var v = d as scrVisualDecoration; if (!v) continue; image++;
                if (v.isMask()) mask++;
                if ((bool)AccessTools.Field(typeof(scrVisualDecoration), "meshRendererEnabled").GetValue(v)) mesh++;
                var r = v.spriteRenderer; if (!r || !r.sprite) continue;
                if (d.GetVisible()) visible++;
                sprites.Add(r.sprite.GetInstanceID()); textures.Add(r.sprite.texture.GetInstanceID());
                if (r.sharedMaterial) materials.Add(r.sharedMaterial.GetInstanceID());
                string k = "tex=" + r.sprite.texture.GetInstanceID() + ";layer=" + r.sortingLayerName + "/" + r.sortingLayerID
                    + ";order=" + r.sortingOrder + ";" + Key(r.sharedMaterial);
                groups.TryGetValue(k, out int c); groups[k] = c + 1;
                var b = r.bounds; float minX = float.PositiveInfinity, maxX = float.NegativeInfinity, minY = minX, maxY = maxX;
                for (int a = 0; a < 8; a++)
                {
                    var p = camera.WorldToScreenPoint(b.center + Vector3.Scale(b.extents, new Vector3((a&1)==0?-1:1,(a&2)==0?-1:1,(a&4)==0?-1:1)));
                    minX = Mathf.Min(minX,p.x); maxX = Mathf.Max(maxX,p.x); minY = Mathf.Min(minY,p.y); maxY = Mathf.Max(maxY,p.y);
                }
                float side = Mathf.Max(maxX-minX,maxY-minY);
                sizes[side < 128 ? 0 : side < 512 ? 1 : side < 1024 ? 2 : side < 2048 ? 3 : 4]++;
            }
            Log("survey running=" + running.Count + " image=" + image + " visible=" + visible + " mask=" + mask + " hitbox=" + hitbox
                + " mesh=" + mesh + " sprites/textures/materials=" + sprites.Count + "/" + textures.Count + "/" + materials.Count
                + " bounds-longest-pixels(<128/512/1024/2048/rest)=" + string.Join("/", sizes)
                + " pipeline=" + (GraphicsSettings.currentRenderPipeline == null ? "Built-in" : "SRP") + " unity=" + Application.unityVersion);
            foreach (var g in groups) Log("group count=" + g.Value + " " + g.Key);
        }
        void Begin(int cap)
        {
            PrepareBlockProperties();
            camera = GameCamera(ADOBase.controller.camy);
            if (!camera || camera.actualRenderingPath != RenderingPath.Forward) throw new InvalidOperationException("forward game camera required");
            var running = new List<scrDecoration>(DecoAnim.RunningDecorations());
            Survey(running); // setup cost intentionally outside measurement
            using (var stream = typeof(DecoGpu).Assembly.GetManifestResourceStream("StutterFix.decogpu"))
            {
                if (stream == null) throw new InvalidOperationException("build effects/stutterfix_decogpu first (Unity Editor required)");
                var bytes = new byte[stream.Length]; int n = 0;
                while (n < bytes.Length) { int r = stream.Read(bytes,n,bytes.Length-n); if (r == 0) throw new System.IO.EndOfStreamException(); n += r; }
                bundle = AssetBundle.LoadFromMemory(bytes);
            }
            if (!bundle) throw new InvalidOperationException("bundle load failed");
            shader = bundle.LoadAsset<Shader>("Assets/DecoGpu/DecoGpu.shader");
            if (!shader || !shader.isSupported) throw new InvalidOperationException("instancing shader unavailable");
            var all = TightLate.AllDecorations(); var why = new int[16];
            foreach (var d in all)
            {
                if (owned.Count >= cap) break;
                if (!d || d.GetType() != typeof(scrVisualDecoration)) { why[0]++; continue; }
                var v = (scrVisualDecoration)d; var r = v.spriteRenderer;
                var e = TightLate.EngineEntry(d, out int missing);
                if (e == null) { why[1 + Mathf.Min(missing, 5)]++; continue; }
                if (e.Engine != null) { why[7]++; continue; }
                if (!r || !r.sprite) { why[8]++; continue; }
                if (!r.enabled || r.forceRenderingOff || !r.gameObject.activeInHierarchy) { why[9]++; continue; }
                // Internal sprite texture blocks are normal; reject unsupported known overrides.
                if (BlockReason(r, sourceBlock) != null) { why[10]++; continue; }
                if (r.maskInteraction != SpriteMaskInteraction.None) { why[14]++; continue; }
                if (r.drawMode != SpriteDrawMode.Simple) { why[15]++; continue; }
                if (r.GetComponentInParent<SortingGroup>() != null) { why[6]++; continue; }
                if (v.repeatX != 1 || v.repeatY != 1) { why[11]++; continue; }
                if (!r.sharedMaterial || !r.sharedMaterial.shader || r.sharedMaterial.shaderKeywords.Length != 0
                    || (r.sharedMaterial.shader.name != "Sprites/Default" && r.sharedMaterial.shader.name != "Sprites/Tile")
                    || r.sharedMaterial.passCount != 1 || r.sprite.associatedAlphaSplitTexture != null) { why[12]++; continue; }
                if ((camera.cullingMask & (1 << r.gameObject.layer)) == 0) { why[13]++; continue; }
                if (manager != null && manager != d.manager) continue;
                manager = d.manager;
                var m = r.sharedMaterial;
                owned.Add(d, new Item {D=v, R=r, Sprite=r.sprite, Source=m, Entry=e, Off=r.forceRenderingOff,
                    Layer=r.sortingLayerID, Order=r.sortingOrder, UnityLayer=r.gameObject.layer,
                    Child=d.pivotTrans.worldToLocalMatrix * r.transform.localToWorldMatrix,
                    ChildTransform=d.childTransform, Offset=d.pivotOffsetVec, Cropped=CropField.Get(d),
                    TextureScale=d.textureScaleMultiplier, ScaleMultiplier=d.scaleMultiplier,
                    Tint=m.HasProperty("_Color")?m.GetColor("_Color"):Color.white, Signature=Key(m)});
            }
            if (owned.Count == 0) throw new InvalidOperationException("no clean supported images; candidates=" + all.Count + " why[notVisual,noTL,tlMissing1..5(1..5),engineOwned,noSprite,renderOff,propertyBlock,repeat,material,cullmask,mask,drawMode; [6]=sortingGroup]=" + string.Join("/", why));
            // Stable deterministic approximation only. Native tie-sort and foreign renderers are not reproduced.
            var ordered = owned.Values.OrderBy(x => SortingLayer.GetLayerValueFromID(x.Layer)).ThenBy(x => x.Order).ThenBy(x => x.D.GetInstanceID());
            Batch current = null; Item prev = null;
            foreach (var i in ordered)
            {
                if (current == null || current.Items.Count == 1023 || prev.Sprite != i.Sprite || prev.Signature != i.Signature
                    || prev.Layer != i.Layer || prev.Order != i.Order)
                {
                    current = new Batch(); batches.Add(current);
                    var v = i.Sprite.vertices; var uv = i.Sprite.uv; var tri = i.Sprite.triangles;
                    current.Mesh = new Mesh {name="DecoGpu.sprite", hideFlags=HideFlags.HideAndDontSave};
                    current.Mesh.vertices = Array.ConvertAll(v, p => new Vector3(p.x,p.y,0));
                    current.Mesh.uv = uv; current.Mesh.triangles = Array.ConvertAll(tri, x => (int)x);
                    current.Mesh.RecalculateBounds();
                    current.Material = new Material(shader) {enableInstancing=true, hideFlags=HideFlags.HideAndDontSave};
                    current.Material.mainTexture = i.Sprite.texture;
                }
                current.Items.Add(i); prev = i;
            }
            commands = new CommandBuffer {name="StutterFix.DecoGpu (experimental transparent band)"};
            camera.AddCommandBuffer(DrawEvent, commands);
            harmony = new Harmony(PatchId);
            harmony.Patch(AccessTools.Method(typeof(scrVisualDecoration), "ApplyColor"), prefix: new HarmonyMethod(typeof(DecoGpu), nameof(ApplyColorPrefix)));
            foreach (var type in new[] { typeof(scrDecoration), typeof(scrVisualDecoration) })
                foreach (var method in type.GetMethods(AccessTools.all))
                    if (method.DeclaringType == type && !method.IsAbstract && method.GetMethodBody() != null && (method.Name == "Setup" || method.Name == "SetSprite"
                        || method.Name == "SetBlendMode" || method.Name == "SetMaskingType" || method.Name == "SetTile"
                        || method.Name == "SetVisible" || method.Name == "EnableMeshRenderer"))
                        harmony.Patch(method, prefix: new HarmonyMethod(typeof(DecoGpu), nameof(BoundaryPrefix)));
            Camera.onPreCull += PreCull;
            Camera.onPostRender += PostRender;
            beginFrame = Time.frameCount;
            // Keep originals visible until the first successful command submission.
        }
        public static bool ApplyColorPrefix(scrVisualDecoration __instance)
        {
            if (!Owns(__instance) || !session.originalsHidden || session.comparing) return true;
            if (__instance.stickToFloor) { Stop("ApplyColor stickToFloor id=" + __instance.GetInstanceID()); return true; }
            Color c = ColorField(__instance); c.a = c.a * Opacity(__instance) * 1f; Rendered(__instance) = c;
            return false;
        }
        public static void BoundaryPrefix(scrDecoration __instance, MethodBase __originalMethod)
        {
            if (!Owns(__instance)) return;
            if (session.comparing) { Stop("boundary " + __originalMethod.DeclaringType.Name + "." + __originalMethod.Name + " id=" + __instance.GetInstanceID() + " comparing=True"); return; }
            session.Evict(__instance, __originalMethod.Name);
        }
        static int evictedTotal; static string lastEvict = "";
        // 게임이 그 장식의 스프라이트·재질·보임 등을 바꾸는 순간: 세션 전체를 끝내지 않고 그 장식만 원래 경로로 돌려보낸다.
        void Evict(scrDecoration d, string why)
        {
            if (!owned.TryGetValue(d, out var i)) return;
            owned.Remove(d);
            foreach (var b in batches) if (b.Items.Remove(i)) break;
            try { if (i.R) { i.R.forceRenderingOff = i.Off; i.R.color = Rendered(i.D); } if (i.Entry != null) i.Entry.Has = false; } catch { }
            evictedTotal++; lastEvict = why;
            if (owned.Count == 0) Stop("all evicted (last " + why + ")");
        }
        static string Describe(Item i) => "id=" + (i.D ? i.D.GetInstanceID() : 0);
        string InvalidReason(Item i, bool inLoop = false)
        {
            if (!i.D || !i.R || i.Entry == null) return "destroyed/entry missing";
            int reason = inLoop ? TightLate.EngineReasonInLoop(i.Entry) : TightLate.EngineReason(i.Entry);
            if (reason != 0 && reason != 3) return "TightLate.EngineReason=" + reason + " [" + TightLate.EngineReasonDetail(i.Entry) + "]";
            if (i.R.sprite != i.Sprite) return "sprite changed";
            if (i.R.sharedMaterial != i.Source) return "material changed";
            if (i.R.sortingLayerID != i.Layer || i.R.sortingOrder != i.Order || i.R.gameObject.layer != i.UnityLayer)
                return "sorting/layer changed current=" + i.R.sortingLayerID + "/" + i.R.sortingOrder + "/" + i.R.gameObject.layer;
            if (!i.R.enabled || !i.R.gameObject.activeInHierarchy) return "renderer disabled/inactive";
            if (i.R.maskInteraction != SpriteMaskInteraction.None || i.R.drawMode != SpriteDrawMode.Simple
                || i.D.isMask() || i.D.repeatX != 1 || i.D.repeatY != 1) return "mask/drawMode/repeat changed";
            if (!ReferenceEquals(i.D.childTransform,i.ChildTransform) || i.D.pivotOffsetVec != i.Offset
                || CropField.Get(i.D) != i.Cropped || i.D.textureScaleMultiplier != i.TextureScale
                || i.D.scaleMultiplier != i.ScaleMultiplier) return "child/pivot/scale changed";
            return null;
        }
        void Update()
        {
            if (stopping) return;
            string reason = !Hitch.Playing ? "not playing" : SceneReset.Resetting ? "SceneReset.Resetting"
                : Time.timeScale == 0 ? "timeScale=0" : !manager ? "manager missing" : !camera ? "camera missing"
                : !ADOBase.controller || !ADOBase.controller.camy ? "controller/camy missing"
                : GameCamera(ADOBase.controller.camy) != camera ? "camobj changed"
                : ADOBase.controller.paused ? "paused" : !TightLate.Enabled ? "TightLate disabled"
                : !ColorFast.Enabled ? "ColorFast disabled" : DecoEngine.Enabled ? "DecoEngine enabled" : null;
            if (reason != null) { Stop("Update " + reason); return; }
            if (calls == 0 && Time.frameCount - beginFrame > 120) { Stop("Update no selected PreCull for 120 frames; run probe"); return; }
            // No full property enumeration, string creation or extra Transform reads here.
            // Game mutation setters end the session; foreign direct material/child writes are unsupported.
        }
        void PreCull(Camera c)
        {
            if (!c || stopping) return;
            preCullCalls++;
            // Bounded camera names/counts, emitted only on explicit probe, never per-frame logging.
            int id = c.GetInstanceID();
            if (observedCameras.TryGetValue(id, out int count)) observedCameras[id] = count + 1;
            else if (observedCameras.Count < 32) observedCameras.Add(id, 1);
            if (c != camera || stopping || comparing) return;
            selectedPreCull++;
            try { Build(); }
            catch (Exception e) { Stop("PreCull " + e.GetType().Name + ": " + e.Message); Log(last); }
        }
        void PostRender(Camera c) { if (!stopping && !comparing && c == camera) postRenderCalls++; }
        void Build(bool measure = true)
        {
            long aggregateStart = Stopwatch.GetTimestamp();
            long t = Stopwatch.GetTimestamp(); commands.Clear(); currentDraws = 0;
            Vector3 cam = camera.transform.position;
            foreach (var b in batches)
            {
                int n = 0;
                foreach (var i in b.Items)
                {
                    string reason = InvalidReason(i) ?? BlockReason(i.R, sourceBlock);
                    if (reason != null) throw new InvalidOperationException(Describe(i) + " " + reason);
                    var d=i.D; var p=d.parallax; Vector2 pos=d.pivotPosVec + Vector2.zero;
                    StartPos(p)=pos; p.posCamAtStart=pos;
                    Vector2 u=(Vector2)(cam-p.posCamAtStart)*new Vector2(p.multiplier_x,p.multiplier_y)+StartPos(p);
                    Vector2 cs=1f*(d.scaleMultiplier*d.textureScaleMultiplier);
                    Vector2 result=u+d.parallaxOffset*cs/d.textureScaleMultiplier; EndPos(p)=result;
                    Vector2 scale=d.scaleVec*cs*(Vector2)Vector3.one;
                    var matrix=Matrix4x4.TRS(new Vector3(result.x,result.y,0),Quaternion.Euler(0,0,d.startRot+d.rotAngle),new Vector3(scale.x,scale.y,1))*i.Child;
                    // Sprite flips are renderer metadata, not Transform values.
                    matrix *= Matrix4x4.Scale(new Vector3(i.R.flipX?-1:1,i.R.flipY?-1:1,1));
                    b.Matrices[n]=matrix; b.Colors[n]=Rendered(d)*i.Tint; n++;
                }
                long beforeSubmit=Stopwatch.GetTimestamp(); if (measure) computeTicks += beforeSubmit-t;
                b.Props.SetVectorArray(TintId,b.Colors);
                commands.DrawMeshInstanced(b.Mesh,0,b.Material,0,b.Matrices,n,b.Props); currentDraws++;
                long after=Stopwatch.GetTimestamp(); if (measure) submitTicks+=after-beforeSubmit; t=after;
            }
            foreach (var i in owned.Values) i.R.forceRenderingOff = true;
            originalsHidden = true;
            if (measure) { calls++; draws+=currentDraws; }
            // All batches' aggregate times, with two timestamps per batch; no per-item clocks.
            if (measure) TotalTicks += Stopwatch.GetTimestamp() - aggregateStart;
        }
        string Compare()
        {
            if (calls == 0 || postRenderCalls == 0) return "[decogpu] compare refused: wait for selected PreCull and PostRender; " + State();
            // Two synchronous renders, isolated from performance evidence. No game Update between them.
            // Pixel comparison includes command-band ordering and shader differences by design.
            RenderTexture oldTarget=camera.targetTexture, oldActive=RenderTexture.active;
            var rt=RenderTexture.GetTemporary(512,288,24,RenderTextureFormat.ARGB32);
            Texture2D a=null,b=null;
            var snapshots=new List<Tuple<Item,Vector3,Quaternion,Vector3,Color,bool>>();
            try
            {
                comparing=true;
                foreach(var i in owned.Values)
                {
                    var p=i.D.pivotTrans;
                    snapshots.Add(Tuple.Create(i,p.position,p.rotation,p.localScale,i.R.color,i.R.forceRenderingOff));
                    i.D.UpdatePosition(); i.R.color=Rendered(i.D); i.R.forceRenderingOff=i.Off;
                }
                commands.Clear(); camera.targetTexture=rt;
                camera.Render();
                if (stopping || session != this) throw new InvalidOperationException("compare interrupted: " + firstStop);
                a=Read(rt);
                foreach(var i in owned.Values) i.R.forceRenderingOff=true;
                Build(false); camera.Render();
                if (stopping || session != this) throw new InvalidOperationException("compare interrupted: " + firstStop);
                b=Read(rt);
                var aa=a.GetPixels32();var bb=b.GetPixels32();int max=0,changed=0;
                for(int n=0;n<aa.Length;n++)
                {
                    var x=aa[n];var y=bb[n];int delta=Math.Max(Math.Max(Math.Abs(x.r-y.r),Math.Abs(x.g-y.g)),Math.Max(Math.Abs(x.b-y.b),Math.Abs(x.a-y.a)));
                    max=Math.Max(max,delta);if(delta!=0)changed++;
                }
                return "[decogpu] compare 512x288 max-channel="+max+" changed="+changed+"/"+aa.Length+" ratio="+((double)changed/aa.Length).ToString("F6",CultureInfo.InvariantCulture)
                    +"; synchronous Camera.Render x2; temporal callbacks/filters may differ; NOT performance evidence";
            }
            catch (Exception e) { Stop("compare " + e.GetType().Name + ": " + e.Message); throw; }
            finally
            {
                if (camera) camera.targetTexture=oldTarget;RenderTexture.active=oldActive;
                foreach(var s in snapshots) if(!stopping && s.Item1.D && s.Item1.R)
                {var p=s.Item1.D.pivotTrans;p.position=s.Item2;p.rotation=s.Item3;p.localScale=s.Item4;s.Item1.R.color=s.Item5;s.Item1.R.forceRenderingOff=s.Item6;}
                if(a)Destroy(a);if(b)Destroy(b);RenderTexture.ReleaseTemporary(rt);comparing=false;
                if (!stopping) try { Build(false); } catch (Exception e) { Stop("compare rebuild " + e.Message); }
            }
        }
        static Texture2D Read(RenderTexture rt)
        {
            RenderTexture.active=rt;var t=new Texture2D(rt.width,rt.height,TextureFormat.RGBA32,false);
            try { t.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);t.Apply();return t; }
            catch { Destroy(t);throw; }
        }
        internal static string State()
        {
            var s=session; double k=1000.0/Stopwatch.Frequency;
            return "[decogpu] active="+(s!=null)+" managed="+(s==null?0:s.owned.Count)+" batches="+(s==null?0:s.batches.Count)
                +" draws-last="+(s==null?0:s.currentDraws)+" renders="+(s==null?0:s.calls)+" restore="+restores+" restore-mismatch="+restoreMismatch
                +(s==null?stoppedState:" preCull-all="+s.preCullCalls+" preCull-selected="+s.selectedPreCull+" postRender-selected="+s.postRenderCalls+" originals-hidden="+s.originalsHidden)
                +(s==null?"":" compute-ms/render="+(s.computeTicks*k/Math.Max(1,s.calls)).ToString("F4")+" submit-ms/render="+(s.submitTicks*k/Math.Max(1,s.calls)).ToString("F4")+" draws-total="+s.draws)
                +" evicted="+evictedTotal+"("+lastEvict+") status="+last+" first-stop="+firstStop;
        }
        internal static void Stop() => Stop("Unload/external Stop");
        static void Stop(string reason)
        {
            var s=session;if(s==null || s.stopping)return;s.stopping=true;
            if (firstStop == "none") firstStop = reason + " frame=" + Time.frameCount;
            last="stopped: " + firstStop;
            stoppedState=" stopped-renders="+s.calls+" stopped-draws="+s.draws+" preCull-all="+s.preCullCalls+" preCull-selected="+s.selectedPreCull+" postRender-selected="+s.postRenderCalls;
            Camera.onPreCull-=s.PreCull;
            Camera.onPostRender-=s.PostRender;
            try
            {
                try { if(s.camera && s.commands!=null)s.camera.RemoveCommandBuffer(DrawEvent,s.commands); }
                catch(Exception e) { restoreMismatch++;Log("remove command buffer failed: "+e.Message); }
                try { s.harmony?.UnpatchOwner(PatchId); }
                catch(Exception e) { restoreMismatch++;Log("unpatch failed: "+e.Message); }
                foreach(var i in s.owned.Values)
                {
                    if(!i.R)continue;
                    try
                    {
                        // Animation fields have advanced: restore CURRENT fields, never rewind begin values.
                        if(i.D)
                        {
                            Vector2 pos=i.D.pivotPosVec,scale=i.D.scaleVec; float angle=i.D.rotAngle;
                            Color rendered=Rendered(i.D); bool enabled=i.R.enabled; var material=i.R.sharedMaterial;
                            i.D.UpdatePosition();i.R.color=rendered;i.Entry.Has=false;
                            var p=i.D.pivotTrans; var wp=p.position;var wr=p.rotation;var ws=p.localScale;
                            // Original evaluation twice must be idempotent, and source fields must survive.
                            i.D.UpdatePosition();
                            if(p.position!=wp || p.rotation!=wr || p.localScale!=ws || i.D.pivotPosVec!=pos
                                || i.D.scaleVec!=scale || i.D.rotAngle!=angle || Rendered(i.D)!=rendered
                                || i.R.color!=rendered || i.R.enabled!=enabled || i.R.sharedMaterial!=material)restoreMismatch++;
                        }
                    }
                    catch(Exception e){restoreMismatch++;Log("restore position failed: "+e.Message);}
                    finally {i.R.forceRenderingOff=i.Off;restores++;if(i.R.forceRenderingOff!=i.Off)restoreMismatch++;}
                }
            }
            finally
            {
                try { s.commands?.Release(); }
                catch(Exception e) { restoreMismatch++;Log("release command buffer failed: "+e.Message); }
                s.commands=null;
                foreach(var b in s.batches){if(b.Mesh)Destroy(b.Mesh);if(b.Material)Destroy(b.Material);}
                s.batches.Clear();s.owned.Clear();if(s.bundle)s.bundle.Unload(true);
                session=null;Destroy(s.gameObject);
            }
        }
        void OnDestroy(){if(session==this)Stop("OnDestroy");}
    }
}
#endif
