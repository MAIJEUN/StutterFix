#if DEV || AUTOTEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace StutterFix
{
    // No atlas, material clone, texture mutation or DrawMesh submission. Scene
    // MeshRenderers borrow the first member's exact material identity.
    internal static class LiveDecoRenderer
    {
        const string PatchId = "StutterFix.livedeco";
        const float Extent = 1000000f;
        const MeshUpdateFlags Flags = MeshUpdateFlags.DontValidateIndices |
            MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontNotifyMeshUsers;
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static readonly AccessTools.FieldRef<scrParallax, bool> Inited = AccessTools.FieldRefAccess<scrParallax, bool>("inited");
        static readonly AccessTools.FieldRef<scrParallax, bool> IsDecoration = AccessTools.FieldRefAccess<scrParallax, bool>("isDecoration");
        static readonly AccessTools.FieldRef<scrParallax, Transform> Cached = AccessTools.FieldRefAccess<scrParallax, Transform>("cachedTransform");
        static readonly AccessTools.FieldRef<scrParallax, Vector2> Start = AccessTools.FieldRefAccess<scrParallax, Vector2>("startPosition");
        static readonly AccessTools.FieldRef<scrParallax, Vector2> End = AccessTools.FieldRefAccess<scrParallax, Vector2>("endPosition");
        static readonly AccessTools.FieldRef<scrVisualDecoration, bool> MeshOn = AccessTools.FieldRefAccess<scrVisualDecoration, bool>("meshRendererEnabled");
        static readonly AccessTools.FieldRef<scrVisualDecoration, DecorationBlendMode> Blend = AccessTools.FieldRefAccess<scrVisualDecoration, DecorationBlendMode>("blendMode");
        static readonly AccessTools.FieldRef<scrDecoration, Color> Rendered = AccessTools.FieldRefAccess<scrDecoration, Color>("rendererColor");
        [StructLayout(LayoutKind.Sequential)]
        internal struct Vertex { internal Vector3 Position; internal Color Color; internal Vector2 UV; }
        sealed class Image
        {
            internal scrVisualDecoration D;
            internal SpriteRenderer R;
            internal Sprite Sprite;
            internal Material Material;
            internal Shader Shader;
            internal Texture Texture;
            internal int TextureId;
            internal string Signature;
            internal Vector2[] Corners, UV;
            internal ushort[] Indices;
            internal Vector3[] World => Geometry.World;
            internal LiveDecoGeometry Geometry;
            internal Color UploadedColor;
            internal bool VertexDirty = true;
            internal Bounds PredictedBounds;
            internal bool FlipX, FlipY;
            internal bool Owned, Rejected, OriginalOff, Hidden;
            internal bool Cached;
            internal bool LogicalVisible;
            internal Source Source;
            internal string Reason;
            internal Renderer[] Dependents;
            internal MonoBehaviour[] Scripts;
        }
        sealed class Row
        {
            internal Renderer R;
            internal Image Image;
            internal int Slot, Layer;
            internal Material Material;
            internal SceneRenderOrder.Key Key;
            internal bool Listed, Owned;
            internal Run Run;
        }
        sealed class Source
        {
            internal Renderer R;
            internal Image Image;
            internal Row[] Rows = Array.Empty<Row>();
            internal bool Group;
            internal bool Queued, Invalidate;
            internal int LiveImages;
            internal readonly HashSet<UnityEngine.Object> Watched = new HashSet<UnityEngine.Object>();
        }
        sealed class Run
        {
            internal GameObject Object;
            internal Mesh Mesh;
            internal MeshFilter Filter;
            internal MeshRenderer Renderer;
            internal Image[] Members;
            internal int Offset, VertexCount;
            internal float Z;
            internal bool Enabled;
            internal bool NeedsUpload = true;
            internal int Index;
        }
        static Harmony harmony;
        static Camera camera;
        static scrDecorationManager manager;
        static GameObject root;
        static bool restoring, topology, starting;
        static long enableBegin, enableAlloc, setupEnd;
        static double prepareMs, registerMs, patchMs;
        static int lastFrame = -1, ownedCount, frameSamples;
        static long reset;
        static long computeTicks, uploadTicks, reconcileTicks, scanTicks, uploadCalls, heapDelta;
        static long dirtyImages, skippedUploads;
        static long deltaTicks, auditTicks, dirtySources, cacheHits, admissions, missed, auditCount;
        static long writeCallbacks, watcherVisits;
        static int awakeImages;
        static float nextReport, enabledAt;
        static double transitionMs;
        static readonly List<Source> pending = new List<Source>();
        static readonly Dictionary<UnityEngine.Object, HashSet<Source>> watchers =
            new Dictionary<UnityEngine.Object, HashSet<Source>>();
        static readonly HashSet<Row> repair = new HashSet<Row>();
        static readonly HashSet<Run> invalidRuns = new HashSet<Run>();
        static readonly Dictionary<string, int> releaseReasons = new Dictionary<string, int>();
        static readonly int[] errorHistogram = new int[6];
        static readonly List<KeyValuePair<int, int>> freeVertices = new List<KeyValuePair<int, int>>();
        static int vertexEnd;
        static float pixelsPerWorld, checkPixelMax;
        static Vector2 cameraPosition, screenOrigin, screenX, screenY;
        static float cameraAngle, cameraScale;
        static float sortAxisZ, sortCameraZ;
        static int sortMask;
        static TransparencySortMode sortMode;
        static long transitionCount, restoredCount;
        static long allocatedBytes, steadyAllocatedBytes;
        static int steadySamples;
        static float checkMax, checkVertexMax, checkBoundsMax, checkQuadBoundsMax;
        static int checkCount, checkRejected, checkNested, checkVisibility;
        static string error = "none";
        static readonly List<Source> sources = new List<Source>();
        static readonly List<Row> ordered = new List<Row>();
        static readonly List<Material> materials = new List<Material>(4);
        static readonly List<Run> runs = new List<Run>();
        static Vertex[] vertexBuffer = Array.Empty<Vertex>();
        static readonly Dictionary<scrDecoration, Image> images = new Dictionary<scrDecoration, Image>();
        static readonly Dictionary<int, Source> known = new Dictionary<int, Source>();
        static readonly Dictionary<string, int> rejected = new Dictionary<string, int>();
        static readonly VertexAttributeDescriptor[] Layout = {
            new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
            new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.Float32, 4),
            new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.Float32, 2) };
        internal static bool Active => root && !restoring;
        internal static bool Ready => Active && !starting;
        internal static int Owned => ownedCount;
        internal static double TransitionMs => transitionMs;
        internal static float EnabledSeconds => starting ? 0 : Time.realtimeSinceStartup - enabledAt;
        static void Log(string s) => Main.Entry.Logger.Log("[livedeco] " + s);
        static string Ms(long ticks, int n = 1) => (ticks * 1000.0 / Stopwatch.Frequency / Math.Max(1, n)).ToString("F6", Inv);
        internal static string State() => "on=" + Active + " gate=2 variant=A owned=" + ownedCount
            + " runs=" + runs.Count + " registry=" + sources.Count + " transitions=" + transitionCount
            + " restored=" + restoredCount + " error=" + error + " atlas=0 ready=" + (Active && !starting);

        // Exact signature selection, performed only on explicit enable/admission.
        static MethodInfo One(Type type, string name, int count)
        {
            var methods = type.GetMethods(AccessTools.all).Where(m => m.DeclaringType == type && m.Name == name
                && !m.IsStatic && !m.IsAbstract && !m.IsGenericMethod && m.GetParameters().Length == count
                && (m.CallingConvention & CallingConventions.HasThis) != 0
                && (m.CallingConvention & CallingConventions.VarArgs) == 0).ToArray();
            if (methods.Length != 1) throw new MissingMethodException(type.Name + "." + name + " count=" + methods.Length);
            return methods[0];
        }
        static void Hook(Type type, string name, int count, string prefix)
            => harmony.Patch(One(type, name, count), prefix: new HarmonyMethod(typeof(LiveDecoRenderer), prefix) { priority = Priority.First });
        internal static void Begin()
        {
            if (Active) return;
            try
            {
            if (SceneReset.Resetting || (!Hitch.Playing && !FrameCompare.Paused) || !ADOBase.controller || !ADOBase.customLevel
                || !ADOBase.controller.disableV15Features || DecoEngine.Enabled || MatShare.Active
                || GpuSplit.Busy || GpuHide.Busy || GpuCensus.Busy)
                throw new InvalidOperationException("livedeco requires playing legacy chart; competing diagnostics/engine off");
            if (GraphicsSettings.currentRenderPipeline != null) throw new InvalidOperationException("Built-in pipeline required");
            camera = GpuSplit.FindMain(); manager = scrDecorationManager.instance;
            if (!camera || !manager || !SortSupported() || !Identity(manager.transform))
                throw new InvalidOperationException("requires orthographic Z sorting and identity manager parent");
            if (DecoGpu.Active) throw new InvalidOperationException("competing owner");
            error = "none"; reset = SceneReset.Count; lastFrame = -1;
            enableBegin = Stopwatch.GetTimestamp();
            enableAlloc = GC.GetAllocatedBytesForCurrentThread();
                Dormancy.PrepareLiveTracking(manager);
                Log("live_tracking ready=1 playing=" + Hitch.Playing + " capture_paused=" + FrameCompare.Paused
                    + " awake=" + Dormancy.AwakeList.Count);
                // Refuse foreign hooks before any rendering mutation.
                foreach (var type in new[] { typeof(scrDecoration), typeof(scrVisualDecoration), typeof(scrParallax), typeof(ToggleVisible) })
                    foreach (var method in type.GetMethods(AccessTools.all))
                    {
                        if (method.DeclaringType != type || method.IsAbstract) continue;
                        var patches = Harmony.GetPatchInfo(method);
                        if (patches != null && patches.Owners.Any(id => id != Main.Entry.Info.Id
                            && !id.StartsWith("StutterFix.", StringComparison.Ordinal)))
                            throw new InvalidOperationException("foreign decoration hook: " + method.Name);
                    }
                prepareMs = (Stopwatch.GetTimestamp() - enableBegin) * 1000.0 / Stopwatch.Frequency;
                starting = true;
                nextReport = float.PositiveInfinity;
                root = new GameObject("StutterFix.LiveDeco.A") { hideFlags = HideFlags.HideAndDontSave };
                root.AddComponent<LiveDecoLifetime>();
                long stage = Stopwatch.GetTimestamp();
                Register();
                registerMs = (Stopwatch.GetTimestamp() - stage) * 1000.0 / Stopwatch.Frequency;
                stage = Stopwatch.GetTimestamp();
                harmony = new Harmony(PatchId);
                Hook(typeof(scrDecoration), "UpdatePosition", 0, nameof(PositionPrefix));
                Hook(typeof(scrDecoration), "SetPosition", 2, nameof(SetPositionPrefix));
                Hook(typeof(scrDecoration), "SetRotation", 1, nameof(RotationPrefix));
                Hook(typeof(scrDecoration), "SetScale", 1, nameof(ScalePrefix));
                Hook(typeof(scrDecoration), "SetColor", 1, nameof(ColorPrefix));
                Hook(typeof(scrDecoration), "SetOpacity", 1, nameof(OpacityPrefix));
                Hook(typeof(scrVisualDecoration), "ApplyColor", 0, nameof(ApplyColorPrefix));
                Hook(typeof(scrParallax), "SetTrans", 0, nameof(ParallaxPrefix));
                foreach (var spec in new[] { Tuple.Create(typeof(scrDecoration), "Setup", 3),
                    Tuple.Create(typeof(scrVisualDecoration), "SetSprite", 1),
                    Tuple.Create(typeof(scrVisualDecoration), "SetSprite", 2),
                    Tuple.Create(typeof(scrVisualDecoration), "SetTile", 1),
                    Tuple.Create(typeof(scrVisualDecoration), "SetBlendMode", 1),
                    Tuple.Create(typeof(scrVisualDecoration), "SetVisible", 1),
                    Tuple.Create(typeof(scrVisualDecoration), "SetMaskingType", 1),
                    Tuple.Create(typeof(scrVisualDecoration), "SetTextureScaleMultiplier", 1),
                    Tuple.Create(typeof(scrVisualDecoration), "SetDepth", 1) })
                    Hook(spec.Item1, spec.Item2, spec.Item3, nameof(BoundaryPrefix));
                LiveDecoChanges.Install(harmony);
                Hook(typeof(scrController), "OnLandOnPortal", 3, nameof(LifecyclePrefix));
                Hook(typeof(scrController), "FailAction", 4, nameof(LifecyclePrefix));
                Hook(typeof(scrController), "Restart", 1, nameof(LifecyclePrefix));
                Hook(typeof(scrController), "QuitToMainMenu", 0, nameof(LifecyclePrefix));
                Hook(typeof(scnGame), "OnDestroy", 0, nameof(LifecyclePrefix));
                patchMs = (Stopwatch.GetTimestamp() - stage) * 1000.0 / Stopwatch.Frequency;
                setupEnd = Stopwatch.GetTimestamp();
                Camera.onPreCull += PreCull;
                // Update temporarily puts scrCamera at Z=0. LateUpdate restores Z=-10.
                // No ownership, sort keys, or vertex validation until this camera's PreCull.
                Log("activation pending=1 first_render=main-camera-PreCull setup_ms=" + Ms(setupEnd - enableBegin)
                    + " prepare_ms=" + prepareMs.ToString("F6", Inv) + " register_ms=" + registerMs.ToString("F6", Inv)
                    + " patch_ms=" + patchMs.ToString("F6", Inv));
                LiveDecoGate2.RendererStarted();
            }
            catch (Exception ex)
            { Log("ERROR " + ex.ToString()); Stop(); camera = null; manager = null; throw; }
        }
        static void StartRendering()
        {
                long firstCull = Stopwatch.GetTimestamp();
                RenderFrame();
                if (ownedCount == 0) throw new InvalidOperationException("no eligible images; see rejection counts");
                long ready = Stopwatch.GetTimestamp();
                transitionMs = ((setupEnd - enableBegin) + (ready - firstCull)) * 1000.0 / Stopwatch.Frequency;
                // Warmup and OWNED_LOW count actual owned rendering, excluding setup/admission.
                starting = false; enabledAt = Time.realtimeSinceStartup; nextReport = enabledAt + 1;
                Log("begin variant=A renderer_version=11 color_format=Float32x4 vertex_stride=36 live_source=Dormancy-events"
                    + " change_methods=" + LiveDecoChanges.Methods + " change_sites=" + LiveDecoChanges.Sites
                    + " enable_transition_ms=" + transitionMs.ToString("F6", Inv)
                    + " prepare_ms=" + prepareMs.ToString("F6", Inv) + " register_ms=" + registerMs.ToString("F6", Inv)
                    + " patch_ms=" + patchMs.ToString("F6", Inv) + " first_render_ms=" + Ms(ready - firstCull)
                    + " wait_first_cull_ms=" + Ms(firstCull - setupEnd) + " camera_z=" + sortCameraZ.ToString("R", Inv)
                    + " enable_allocated_bytes=" + (GC.GetAllocatedBytesForCurrentThread() - enableAlloc)
                    + " watched_objects=" + watchers.Count
                    + " bounds_policy=screen-pixels threshold_px=0.01 visibility_policy=logical-visible"
                    + " currentRenderPipeline=null atlas=0 material_clone=0 " + State());
                ReportCost("activation");
        }
        static void Register()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<scrVisualDecoration>())
                if (d is scrVisualDecoration v && v && !images.ContainsKey(d) && v.spriteRenderer
                    && v.spriteRenderer.gameObject.scene.IsValid())
                    images.Add(d, new Image { D = v, R = v.spriteRenderer });
            var owners = new Dictionary<int, Image>();
            foreach (var i in images.Values) if (i.R) owners[i.R.GetInstanceID()] = i;
            foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (!r || !r.gameObject.scene.IsValid() || (root && r.transform.IsChildOf(root.transform)) || known.ContainsKey(r.GetInstanceID())) continue;
                owners.TryGetValue(r.GetInstanceID(), out var i);
                var s = new Source { R = r, Image = i, Group = r.GetComponentInParent<SortingGroup>() };
                known.Add(r.GetInstanceID(), s); sources.Add(s);
                if (i != null) i.Source = s;
                IndexSource(s); Queue(s);
            }
        }
        static void Watch(UnityEngine.Object target, Source s)
        {
            if (!target) return;
            if (!watchers.TryGetValue(target, out var set)) watchers.Add(target, set = new HashSet<Source>());
            set.Add(s);
            s.Watched.Add(target);
        }
        static void IndexSource(Source s)
        {
            foreach (var old in s.Watched) if (watchers.TryGetValue(old, out var set))
            { set.Remove(s); if (set.Count == 0) watchers.Remove(old); }
            s.Watched.Clear();
            if (!s.R) return;
            Watch(s.R, s);
            if (s.Image != null) Watch(s.Image.D, s);
            for (var t = s.R.transform; t; t = t.parent) { Watch(t, s); Watch(t.gameObject, s); }
            materials.Clear(); s.R.GetSharedMaterials(materials);
            foreach (var m in materials) Watch(m, s);
            if (s.Image != null) WatchDependents(s.Image);
            if (s.Image?.Scripts != null) foreach (var script in s.Image.Scripts) Watch(script, s);
        }
        static void WatchDependents(Image i)
        {
            if (i.Dependents == null) return;
            foreach (var dependent in i.Dependents)
            {
                if (!dependent) continue;
                Watch(dependent, i.Source);
                for (var t = dependent.transform; t && t != i.D.pivotTrans; t = t.parent)
                { Watch(t, i.Source); Watch(t.gameObject, i.Source); }
            }
        }
        static void Queue(Source s, bool invalidate = false)
        {
            s.Invalidate |= invalidate;
            if (s.Queued) return;
            s.Queued = true; pending.Add(s);
        }
        internal static bool ObservingWrites => Active && !restoring;
        internal static bool HasOwnedWatcher(UnityEngine.Object target)
        {
            if (!watchers.TryGetValue(target, out var set)) return false;
            foreach (var s in set) if (s.Image != null && s.Image.Owned) return true;
            return false;
        }
        internal static void BeforeWrite(UnityEngine.Object target, int kind)
        {
            if (!Active || restoring || !target) return;
            if (!watchers.TryGetValue(target, out var set))
            {
                if (kind != LiveDecoChanges.Script || !(target is Component component)
                    || !watchers.TryGetValue(component.transform, out set)) return;
            }
            writeCallbacks++;
            try
            {
            foreach (var s in set)
            {
                watcherVisits++;
                var i = s.Image;
                if (kind == LiveDecoChanges.Script && i != null && target == i.D) continue; // bound-self ToggleVisible
                if (i != null && i.Owned) Release(i, "write-" + kind);
                bool shape = kind == LiveDecoChanges.Shape || kind == LiveDecoChanges.Hierarchy || kind == LiveDecoChanges.Script
                    || (kind == LiveDecoChanges.Pose && i != null && target != i.D.pivotTrans)
                    || (kind == LiveDecoChanges.State && i != null && target != i.R && target != i.R.gameObject);
                Queue(s, shape);
            }
            }
            catch (Exception ex) { WriteFailed(ex); }
        }
        internal static void AfterWrite(UnityEngine.Object target, int kind)
        {
            if (!Active || restoring || !target) return;
            try
            {
            bool indexed = watchers.TryGetValue(target, out var set);
            if (indexed) foreach (var s in set)
            {
                // Match BeforeWrite: bound-self ToggleVisible changes Behaviour
                // enablement, not logical visibility, geometry, or sorting.
                if (kind == LiveDecoChanges.Script && s.Image != null && target == s.Image.D) continue;
                Queue(s);
            }
            else if (target is Renderer renderer) RegisterRenderer(renderer);
            // New/reparented subtrees are discovered at their change event only.
            if (kind == LiveDecoChanges.Hierarchy || (!indexed && target is GameObject))
            {
                Transform t = target is Component component ? component.transform : (target as GameObject)?.transform;
                if (t) foreach (var r in t.GetComponentsInChildren<Renderer>(true)) RegisterRenderer(r);
            }
            }
            catch (Exception ex) { WriteFailed(ex); }
        }
        static void WriteFailed(Exception ex)
        {
            error = "change-notification:" + ex.Message;
            Log("ERROR " + ex.ToString()); LiveDecoGate2.Fail(error); Stop();
        }
        static void RegisterRenderer(Renderer r)
        {
            if (!r || !r.gameObject.scene.IsValid() || r.transform.IsChildOf(root.transform)) return;
            if (known.TryGetValue(r.GetInstanceID(), out var existing)) { Queue(existing, true); return; }
            var s = new Source { R = r, Group = r.GetComponentInParent<SortingGroup>() };
            known.Add(r.GetInstanceID(), s); sources.Add(s);
            IndexSource(s); Queue(s, true);
        }
        internal static void DormancyChanged(scrDecoration d)
        {
            if (!Active || restoring || (object)d == null) return;
            if (images.TryGetValue(d, out var i))
            {
                // ApplyColor can fire every channel/frame. Its logical visibility
                // edge, rather than forceRenderingOff (owned by us), drives admission.
                if (i.LogicalVisible != (i.D && i.D.GetVisible())) Queue(i.Source);
            }
            else if (d is scrVisualDecoration v && v && v.spriteRenderer && v.spriteRenderer.gameObject.scene.IsValid())
            {
                i = new Image { D = v, R = v.spriteRenderer };
                images.Add(d, i);
                if (!known.TryGetValue(i.R.GetInstanceID(), out var s))
                {
                    s = new Source { R = i.R, Group = i.R.GetComponentInParent<SortingGroup>() };
                    known.Add(i.R.GetInstanceID(), s); sources.Add(s);
                }
                s.Image = i; i.Source = s; IndexSource(s); Queue(s, true);
            }
        }
        internal static void DormancyRebuilt()
        {
            if (!Active || restoring) return;
            // A rebuilt manager list is a lifecycle boundary, not a request to
            // rediscover 157k renderers during PreCull.
            Log("lifecycle Dormancy-rebuilt restore=1"); Stop();
        }
        public static void LifecyclePrefix() { if (Active && !restoring) Stop(); }
        internal static void DormancyReset()
        {
            if (!Active || restoring) return;
            Log("lifecycle Dormancy-reset restore=1"); Stop();
        }
        internal static void DormancyAudit(long dormantAuditTicks = 0)
        {
            if (!Active || restoring) return;
            long begin = Stopwatch.GetTimestamp(); int found = 0;
            foreach (var d in Dormancy.AwakeList)
            {
                if (!(d is scrVisualDecoration v) || !v || !v.spriteRenderer) continue;
                if (!images.TryGetValue(d, out var i)) { DormancyChanged(d); found++; }
                else if (!i.Source.Queued && i.LogicalVisible != v.GetVisible()) { Queue(i.Source); found++; }
            }
            missed += found; auditCount++; auditTicks += Stopwatch.GetTimestamp() - begin + dormantAuditTicks;
            if (found != 0) Log("audit missed=" + found + " total=" + missed + " awake=" + Dormancy.AwakeList.Count);
        }
        static bool Identity(Transform t) => t && t.position.Equals(Vector3.zero)
            && t.rotation.Equals(Quaternion.identity) && t.lossyScale.Equals(Vector3.one);
        static bool SortSupported()
        {
            if (!camera.orthographic) return false;
            var mode = camera.transparencySortMode;
            if (mode == TransparencySortMode.Default) mode = GraphicsSettings.transparencySortMode;
            if (mode == TransparencySortMode.Perspective) return false;
            Vector3 axis = mode == TransparencySortMode.CustomAxis
                ? (camera.transparencySortMode == TransparencySortMode.CustomAxis ? camera.transparencySortAxis : GraphicsSettings.transparencySortAxis)
                : camera.transform.forward;
            if (axis.x != 0 || axis.y != 0 || axis.z == 0 || !Finite(axis.z)) return false;
            float z = camera.transform.position.z;
            if (!Finite(z)) return false;
            if (Active && !starting && (axis.z != sortAxisZ || z != sortCameraZ || mode != sortMode || camera.cullingMask != sortMask))
                throw new InvalidOperationException("sort-context-changed axis=" + sortAxisZ + "->" + axis.z
                    + " camera_z=" + sortCameraZ + "->" + z + " mode=" + sortMode + "->" + mode
                    + " mask=" + sortMask + "->" + camera.cullingMask);
            if (!Active || starting) { sortAxisZ = axis.z; sortCameraZ = z; sortMode = mode; sortMask = camera.cullingMask; }
            return true;
        }
        static bool Live(Source s)
        {
            var r = s.R;
            return r && r.enabled && r.gameObject.activeInHierarchy && r.gameObject.scene.IsValid()
                && ((camera.cullingMask & (1 << r.gameObject.layer)) != 0)
                && (s.Image != null ? s.Image.D && LiveDecoPolicy.OwnershipVisible(s.Image.D.GetVisible(), r.forceRenderingOff, s.Image.Owned)
                    : !r.forceRenderingOff);
        }
        static string NotLiveReason(Image i)
        {
            if (!i.R || !i.D) return "destroyed";
            if (!i.R.enabled) return "renderer-disabled";
            if (!i.R.gameObject.activeInHierarchy) return "inactive-hierarchy";
            if (!i.D.GetVisible()) return "logical-invisible";
            if ((camera.cullingMask & (1 << i.R.gameObject.layer)) == 0) return "camera-layer";
            return "scene-or-foreign-force-off";
        }
        static string GeometryReason(Image i, scrDecorationManager expectedManager = null)
        {
            var d = i.D; var r = i.R; var p = d.parallax;
            if (!d || !r || d.GetType() != typeof(scrVisualDecoration)) return "subclass";
            if (d.hitbox != 0 || d.isMask() || r.maskInteraction != SpriteMaskInteraction.None) return "hitbox-mask";
            if (d.followPlanet || d.stickToFloor || d.syncFloorDepth) return "parent-follow";
            if (((d.repeatX != 1 || d.repeatY != 1) && !DrawCollapsePolicy.Repeat) || r.drawMode != SpriteDrawMode.Simple) return "repeat-tile";
            if (MeshOn(d) || (d.meshRenderer && d.meshRenderer.enabled && d.meshRenderer.gameObject.activeInHierarchy)) return "mesh-mode";
            if (((int)Blend(d) != 0 || (d.blendModeEffect && d.blendModeEffect.enabled)) && !GpuDecoBlend.Takeable(d, r)) return "blend";
            if (DrawCollapsePolicy.SplitNow && RunLod.CandidateRenderer(r)) return "runlod-split";
            if (!p || !Inited(p) || !IsDecoration(p) || !ReferenceEquals(p.decoration, d)
                || !ReferenceEquals(Cached(p), d.pivotTrans) || p.enabled) return "parallax-binding";
            if (!d.pivotTrans || d.pivotTrans.parent != (expectedManager ? expectedManager : manager).transform) return "pivot-parent";
            if (!d.childTransform) return "child-missing";
            if (i.Owned && (!i.Geometry.Matches(d.pivotTrans, d.childTransform, r.transform)
                || r.flipX != i.FlipX || r.flipY != i.FlipY)) return "child-state-changed";
            var cr = d.childTransform.localRotation;
            if (cr.x != 0 || cr.y != 0) return "nonplanar-child";
            if (!ADOBase.controller || p.cameraTransform != ADOBase.controller.camy.transform) return "camera-binding";
            if (d.cfpCache != null && d.cfpCache.Length != 0) return "component-filter";
            if (i.Dependents != null) foreach (var dependent in i.Dependents)
                if (dependent && dependent != r && dependent.enabled && dependent.gameObject.activeInHierarchy
                    && !dependent.forceRenderingOff) return "live-child-renderer";
            if ((d.selectionBordersObject && d.selectionBordersObject.activeInHierarchy)
                || (d.hitboxBordersObject && d.hitboxBordersObject.activeInHierarchy)) return "editor-borders";
            if (!r.sprite || !r.sprite.texture || r.sprite.associatedAlphaSplitTexture) return "sprite-texture";
            if (i.Owned) return LiveDecoScripts.Reason(i.Scripts, d, r);
            return null;
        }
        internal static string LiveGpuReason(scrVisualDecoration d, Camera cam, scrDecorationManager mgr, LiveDeco.BlockCensus blocks)
        {
            var r = d.spriteRenderer;
            // At t=0 (gpudeco live prepare), decorations that fade in during 258..276s have alpha==0 (d.GetVisible()==false).
            // Do not reject by current alpha here; VisibleInWindow already verified window visibility.
            if (!r || !d.pivotTrans || !r.enabled || !r.gameObject.activeInHierarchy
                || (r.forceRenderingOff && !InvisibleSkip.IsHidden(d))) return "inactive";
            var p = d.parallax;
            if (p && (!Inited(p) || p.cameraTransform == null))
            {
                bool prev = restoring; restoring = true;
                try { d.UpdatePosition(); } catch { } finally { restoring = prev; }
            }
            bool simpleHierarchy = d.pivotTrans.childCount == 1 && d.childTransform && d.childTransform.childCount == 1
                && r.transform.parent == d.childTransform && r.transform.childCount == 0;
            var deps = simpleHierarchy ? null : d.pivotTrans.GetComponentsInChildren<Renderer>(true);
            var image = new Image { D = d, R = r, Dependents = deps };
            string why = GeometryReason(image, mgr) ?? LiveDeco.Reason(d, r, r.sharedMaterials, cam);
            if (why != null) return why;
            if (r.HasPropertyBlock())
            {
                why = blocks.Read(r, r.sharedMaterials);
                if (why != null) return why;
            }
            return simpleHierarchy ? null : LiveDecoScripts.Reason(d.pivotTrans.GetComponentsInChildren<MonoBehaviour>(true), d, r);
        }
#if AUTOTEST
        // Explicit paused export only. Reuse admission rules without installing
        // write hooks, changing a Transform, or taking renderer ownership.
        internal static string ExportReason(scrVisualDecoration d, Camera cam, scrDecorationManager mgr)
        {
            var r = d.spriteRenderer;
            if (!r || !d.pivotTrans || !r.enabled || !r.gameObject.activeInHierarchy || r.forceRenderingOff) return "inactive";
            var image = new Image { D = d, R = r, Dependents = d.pivotTrans.GetComponentsInChildren<Renderer>(true) };
            string why = GeometryReason(image, mgr) ?? LiveDeco.Reason(d, r, r.sharedMaterials, cam);
            if (why != null) return why;
            why = new LiveDeco.BlockCensus().Read(r, r.sharedMaterials);
            return why ?? LiveDecoScripts.Reason(d.pivotTrans.GetComponentsInChildren<MonoBehaviour>(true), d, r);
        }
#endif
        static void Reject(Image i, string why)
        {
            i.Rejected = true; i.Reason = why;
            rejected.TryGetValue(why, out int n); rejected[why] = n + 1;
        }
        static void Admit(Image i)
        {
            if (i.Rejected || i.Owned) return;
            if (i.Cached)
            {
                cacheHits++;
                i.OriginalOff = i.R.forceRenderingOff; i.Owned = true; i.VertexDirty = true;
                ownedCount++; topology = true; return;
            }
            admissions++;
            string reason = GeometryReason(i);
            if (reason != null) { Reject(i, reason); return; }
            var m = i.R.sharedMaterials; // Admission/transition only, never steady-state.
            reason = LiveDeco.Reason(i.D, i.R, m, camera);
            if (reason != null) { Reject(i, reason); return; }
            // Known-name checks retain the gate-1 limitations; the pixel gate is mandatory.
            var block = new LiveDeco.BlockCensus();
            reason = block.Read(i.R, m);
            if (reason != null) { Reject(i, reason); return; }
            i.Sprite = i.R.sprite; i.Texture = i.Sprite.texture; i.Material = m[0];
            i.TextureId = i.Texture.GetInstanceID();
            i.Shader = i.Material.shader;
            i.Dependents = i.D.pivotTrans.GetComponentsInChildren<Renderer>(true);
            WatchDependents(i);
            reason = GeometryReason(i);
            if (reason != null) { Reject(i, reason); return; }
            i.Scripts = i.D.pivotTrans.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (var script in i.Scripts) Watch(script, i.Source);
            reason = LiveDecoScripts.Reason(i.Scripts, i.D, i.R);
            if (reason != null) { Reject(i, reason); return; }
            i.Signature = string.Join(";", MatShare.Properties(i.Material).Select(x => x.Key + "=" + x.Value));
            i.Corners = i.Sprite.vertices; i.UV = i.Sprite.uv; i.Indices = i.Sprite.triangles;
            if (i.Corners.Length != 4 || i.UV.Length != 4 || i.Indices.Length != 6) { Reject(i, "nonquad"); return; }
            i.FlipX = i.R.flipX; i.FlipY = i.R.flipY;
            i.Geometry = LiveDecoGeometry.Capture(i.D.pivotTrans, i.D.childTransform,
                i.R.transform, i.Corners, i.FlipX, i.FlipY);
            if (i.Geometry == null) { Reject(i, "child-chain-unsupported"); return; }
            // Do not depend on stale engine state on re-entry.
            restoring = true;
            try { i.D.UpdatePosition(); } finally { restoring = false; }
            Compute(i, true);
            float vertexError = 0;
            for (int v = 0; v < 4; v++)
            {
                Vector3 local = i.Corners[v];
                if (i.R.flipX) local.x = -local.x;
                if (i.R.flipY) local.y = -local.y;
                vertexError = Mathf.Max(vertexError, Vector3.Distance(i.World[v], i.R.transform.TransformPoint(local)));
            }
            var bounds = new Bounds(i.World[0], Vector3.zero);
            for (int v = 1; v < 4; v++) bounds.Encapsulate(i.World[v]);
            var actualBounds = i.R.bounds;
            float quadBoundsError = Mathf.Max(Vector3.Distance(bounds.min, actualBounds.min), Vector3.Distance(bounds.max, actualBounds.max));
            float boundsError = Mathf.Max(Vector3.Distance(i.PredictedBounds.min, actualBounds.min), Vector3.Distance(i.PredictedBounds.max, actualBounds.max));
            float max = Mathf.Max(vertexError, boundsError);
            checkVertexMax = Mathf.Max(checkVertexMax, vertexError);
            checkBoundsMax = Mathf.Max(checkBoundsMax, boundsError);
            checkQuadBoundsMax = Mathf.Max(checkQuadBoundsMax, quadBoundsError);
            checkCount++; checkMax = Mathf.Max(checkMax, max);
            float pixelError = max * pixelsPerWorld;
            checkPixelMax = Mathf.Max(checkPixelMax, pixelError);
            int bin = !Finite(pixelError) ? 5 : pixelError < .001f ? 0 : pixelError < .005f ? 1
                : pixelError < .01f ? 2 : pixelError < .05f ? 3 : 4;
            errorHistogram[bin]++;
            if (!LiveDecoPolicy.WithinPixelError(vertexError, pixelsPerWorld)) { checkRejected++; Reject(i, "vertex-error-px"); return; }
            if (!LiveDecoPolicy.WithinPixelError(boundsError, pixelsPerWorld)) { checkRejected++; Reject(i, "bounds-error-px"); return; }
            if (i.Geometry.Depth > 1) checkNested++;
            foreach (var component in i.Scripts)
                if (component && component.isActiveAndEnabled && component.GetType() == typeof(ToggleVisible)) checkVisibility++;
            i.Cached = true;
            i.OriginalOff = i.R.forceRenderingOff; i.Owned = true; i.VertexDirty = true; ownedCount++; topology = true;
        }
        static bool Finite(float f) => !float.IsNaN(f) && !float.IsInfinity(f);
        static void CaptureCamera()
        {
            var camy = ADOBase.controller.camy;
            var t = camy.transform; var c = camy.camobj;
            cameraPosition = t.position;
            cameraAngle = t.rotation.eulerAngles.z;
            cameraScale = c.orthographicSize * .2f / (ADOBase.customLevel.levelData.camZoom / 100f);
            // Orthographic projection is affine. Cache the screen basis once,
            // rather than calling ScreenToWorldPoint for each owned decoration.
            Rect rect = c.pixelRect;
            pixelsPerWorld = Mathf.Max(camera.pixelRect.height / (2 * camera.orthographicSize),
                camera.pixelRect.width / (2 * camera.orthographicSize * camera.aspect));
            if (!Finite(pixelsPerWorld) || pixelsPerWorld <= 0) throw new InvalidOperationException("invalid screen pixel scale");
            screenOrigin = c.ScreenToWorldPoint(new Vector3(rect.x, rect.y, 0));
            screenX = (Vector2)c.ScreenToWorldPoint(new Vector3(rect.x + rect.width, rect.y, 0)) - screenOrigin;
            screenY = (Vector2)c.ScreenToWorldPoint(new Vector3(rect.x, rect.y + rect.height, 0)) - screenOrigin;
        }
        static void Compute(Image i, bool validateBounds = false)
        {
            var d = i.D; var p = d.parallax;
            Vector2 position = d.pivotPosVec + Vector2.zero;
            Vector3 position3 = position;
            Start(p) = position; p.posCamAtStart = position3;
            Vector2 trueScale = d.scaleMultiplier * d.textureScaleMultiplier;
            Vector2 camScale = (d.lockScale ? cameraScale : 1f) * trueScale;
            Vector2 world;
            if (p.clampToScreen)
            {
                world = screenOrigin + screenX * p.screenRelativePos.x + screenY * p.screenRelativePos.y;
            }
            else
            {
                world = (cameraPosition - (Vector2)p.posCamAtStart) * new Vector2(p.multiplier_x, p.multiplier_y) + Start(p);
                if (p.dontAlterX) world.x = position.x;
                if (p.dontAlterY) world.y = position.y;
            }
            world += d.parallaxOffset * camScale / d.textureScaleMultiplier;
            End(p) = world;
            float angle = d.startRot + d.rotAngle;
            if (d.lockRotation) angle += cameraAngle;
            Vector2 scale = d.scaleVec * camScale * (Vector2)Vector3.one;
            Vector2 offset = (d.pivotOffsetVec + d.croppedPivotOffset) / trueScale;
            bool changed = i.Geometry.UpdatePose(world, scale, offset, angle, validateBounds);
            i.VertexDirty |= changed;
            if (changed) for (int n = 0; n < 4; n++)
            {
                Vector3 v = i.World[n];
                if (!Finite(v.x) || !Finite(v.y) || !Finite(v.z) || Mathf.Abs(v.x) > Extent || Mathf.Abs(v.y) > Extent)
                    throw new InvalidOperationException("nonfinite/out-of-fixed-bounds vertex");
            }
            if (validateBounds)
                i.PredictedBounds = i.Geometry.WorldBounds(i.R.localBounds, i.Geometry.Offset(offset),
                    new Vector3(world.x, world.y, 0), i.Geometry.Rotation, scale);
        }
        static void Release(Image i, string why = "boundary")
        {
            if (!i.Owned) return;
            releaseReasons.TryGetValue(why, out int n); releaseReasons[why] = n + 1;
            if (i.Source != null)
            {
                Queue(i.Source);
                foreach (var row in i.Source.Rows) Touch(row);
            }
            i.Owned = false; ownedCount--; topology = true;
            i.Hidden = false;
            if (i.R) i.R.forceRenderingOff = i.OriginalOff;
            if (i.D && i.D.pivotTrans)
            {
                bool wasRestoring = restoring; restoring = true;
                try
                {
                    // SetPosition may be lazily suppressed by InvisibleSkip. Restore
                    // the child explicitly before returning to normal bookkeeping.
                    i.D.childTransform.localPosition = (i.D.pivotOffsetVec + i.D.croppedPivotOffset) / i.D.trueScaleMultiplier;
                    ScreenClamp(i.D);
                    i.D.UpdatePosition(); i.D.SetColor(i.D.color); restoredCount++;
                }
                finally { restoring = wasRestoring; }
            }
        }
        internal static bool SkipLate(scrDecoration d)
        {
            if (!Active || restoring || (object)d == null || !images.TryGetValue(d, out var i) || !i.Owned) return false;
            if (SceneReset.Resetting) { Release(i, "scene-reset"); return false; }
            return true;
        }
        public static bool PositionPrefix(scrDecoration __instance) => !SkipLate(__instance);
        internal static bool Owns(scrDecoration d) => Active && !restoring && (object)d != null
            && images.TryGetValue(d, out var i) && i.Owned;
        static void ScreenClamp(scrDecoration d)
        {
            var p = d.parallax;
            p.clampToScreen = d.placementType == DecPlacementType.Camera || d.placementType == DecPlacementType.CameraAspect;
            Vector2 v = d.pivotPosVec + d.pivotOffsetVec;
            if (d.placementType == DecPlacementType.CameraAspect) v.x *= (float)Screen.height / (float)Screen.width;
            p.screenRelativePos = v / 20f + new Vector2(.5f, .5f);
        }
        public static bool SetPositionPrefix(scrDecoration __instance, Vector2 __0, Vector2 __1)
        {
            if (!Owns(__instance)) return true;
            if (!__instance.pivotPosVec.Equals(__0) || !__instance.pivotOffsetVec.Equals(__1)) images[__instance].VertexDirty = true;
            __instance.pivotPosVec = __0; __instance.pivotOffsetVec = __1; ScreenClamp(__instance); return false;
        }
        internal static bool TryLogicalColor(scrDecoration d, Color color, float opacity, bool onlyOpacity)
        {
            if (!Owns(d)) return false;
            if (onlyOpacity) d.opacity = opacity; else d.color = color;
            RefreshColor(d);
            if (Rendered(d).a <= 0) Release(images[d], "alpha-zero"); // Return hidden bookkeeping to InvisibleSkip/Dormancy.
            DormancyChanged(d);
            return true;
        }
        static void RefreshColor(scrDecoration d)
        {
            var color = d.color.WithAlpha(d.color.a * d.opacity * 1f);
            if (!Rendered(d).Equals(color)) images[d].VertexDirty = true;
            Rendered(d) = color;
        }
        public static bool ColorPrefix(scrDecoration __instance, Color __0) => !TryLogicalColor(__instance, __0, 0, false);
        public static bool OpacityPrefix(scrDecoration __instance, float __0) => !TryLogicalColor(__instance, default(Color), __0, true);
        public static bool ApplyColorPrefix(scrVisualDecoration __instance)
        { if (!Owns(__instance)) return true; RefreshColor(__instance); return false; }
        public static bool RotationPrefix(scrDecoration __instance, float __0)
        {
            if (!SkipLate(__instance)) return true;
            if (!__instance.rotAngle.Equals(__0)) images[__instance].VertexDirty = true;
            __instance.rotAngle = __0; return false;
        }
        public static bool ScalePrefix(scrDecoration __instance, Vector2 __0)
        {
            if (!SkipLate(__instance)) return true;
            if (!__instance.scaleVec.Equals(__0)) images[__instance].VertexDirty = true;
            __instance.scaleVec = __0; return false;
        }
        public static bool ParallaxPrefix(scrParallax __instance) => !SkipLate(__instance.decoration);
        public static void BoundaryPrefix(scrDecoration __instance, MethodBase __originalMethod)
        {
            if (!Active || restoring) return;
            if (images.TryGetValue(__instance, out var i))
            {
                Release(i, "boundary:" + __originalMethod.Name);
                Queue(i.Source, __originalMethod.Name != "SetVisible" && __originalMethod.Name != "SetDepth");
            }
            else DormancyChanged(__instance);
        }
        static void PreCull(Camera c)
        {
            if (!Active || c != camera || lastFrame == Time.frameCount) return;
            try { if (starting) StartRendering(); else RenderFrame(); }
            catch (Exception e)
            {
                error = e.GetType().Name + ":" + e.Message;
                LiveDecoGate2.Fail("renderer:" + error); Log("ERROR " + e.ToString()); Stop();
            }
        }
        static int Locate(Row row)
        {
            int lo = 0, hi = ordered.Count;
            while (lo < hi) { int mid = (lo + hi) / 2; if (ordered[mid].Key.CompareTo(row.Key) < 0) lo = mid + 1; else hi = mid; }
            return lo;
        }
        static void Touch(Row row)
        {
            if (row == null) return;
            repair.Add(row);
            if (row.Run != null) invalidRuns.Add(row.Run);
            topology = true;
        }
        static void TouchAt(int n) { if (n >= 0 && n < ordered.Count) Touch(ordered[n]); }
        static void Remove(Row row)
        {
            if (!row.Listed) return;
            int n = Locate(row);
            if (n >= ordered.Count || !ReferenceEquals(ordered[n], row)) throw new InvalidOperationException("row index lost");
            Touch(row); TouchAt(n - 1); TouchAt(n + 1);
            ordered.RemoveAt(n); row.Listed = false;
            RetryTieAt(n - 1); RetryTieAt(n);
        }
        static void RetryTieAt(int n)
        {
            if (n < 0 || n >= ordered.Count) return;
            var i = ordered[n].Image;
            if (i != null && i.Rejected && i.Reason == "material-tie") Queue(i.Source);
        }
        static bool HasOtherTie(Row row)
        {
            if (!row.Listed) return false;
            int n = Locate(row);
            return (n > 0 && row.Key.SameMaterialTie(ordered[n - 1].Key))
                || (n + 1 < ordered.Count && row.Key.SameMaterialTie(ordered[n + 1].Key));
        }
        static void Insert(Row row)
        {
            int lo = 0, hi = ordered.Count;
            while (lo < hi) { int mid = (lo + hi) / 2; if (ordered[mid].Key.CompareTo(row.Key) < 0) lo = mid + 1; else hi = mid; }
            ordered.Insert(lo, row); row.Listed = true; Touch(row); TouchAt(lo - 1); TouchAt(lo + 1);
        }
        static void DrainChanges()
        {
            if (pending.Count == 0) return;
            long begin = Stopwatch.GetTimestamp();
            for (int n = 0; n < pending.Count; n++)
            {
                var s = pending[n];
                Refresh(s); s.Queued = false; dirtySources++;
            }
            pending.Clear();
            deltaTicks += Stopwatch.GetTimestamp() - begin;
        }
        static void Refresh(Source s)
        {
                bool live = Live(s); var image = s.Image;
                if (image != null)
                {
                    int count = live ? 1 : 0;
                    awakeImages += count - s.LiveImages; s.LiveImages = count;
                    image.LogicalVisible = image.D && image.D.GetVisible();
                    if (s.Invalidate)
                    {
                        Release(image, "static-invalidated");
                        image.Cached = image.Rejected = false;
                        image.Dependents = null; image.Scripts = null;
                    }
                    if (!live) { if (image.Owned) Release(image, NotLiveReason(image)); }
                    else
                    {
                        if (image.Rejected && image.Reason == "material-tie" && s.Rows.Length == 1 && !HasOtherTie(s.Rows[0]))
                            image.Rejected = false;
                        Admit(image);
                    }
                }
                if (s.Invalidate && s.R) { IndexSource(s); s.Group = s.R.GetComponentInParent<SortingGroup>(); }
                s.Invalidate = false;
                if (!live) { foreach (var old in s.Rows) Remove(old); return; }
                if (s.Group) throw new InvalidOperationException("live SortingGroup not supported");
                materials.Clear(); s.R.GetSharedMaterials(materials);
                if (s.Rows.Length != Math.Max(1, materials.Count))
                {
                    foreach (var old in s.Rows) Remove(old);
                    s.Rows = new Row[Math.Max(1, materials.Count)];
                    for (int n = 0; n < s.Rows.Length; n++) s.Rows[n] = new Row { R = s.R, Image = image, Slot = n };
                }
                // Material reads and key construction happen only for notified sources.
                foreach (var row in s.Rows)
                {
                    var material = row.Slot < materials.Count ? materials[row.Slot] : null;
                    if (!material || material.renderQueue <= 2500) throw new InvalidOperationException("unsupported/missing live material");
                    var key = image != null && image.Owned ? new SceneRenderOrder.Key(
                        SortingLayer.GetLayerValueFromID(s.R.sortingLayerID), s.R.sortingOrder, material.renderQueue,
                        0, material.GetInstanceID(), s.R.GetInstanceID(), row.Slot, SceneRenderOrder.ShaderRank(material)) : MatShare.SortKey(s.R, material, camera, row.Slot);
                    if (image != null && image.Owned)
                    {
                        // The supported sorting axis is Z only; sprite/mesh centers have identical Z.
                        var z = image.World[0].z;
                        var mode = camera.transparencySortMode;
                        if (mode == TransparencySortMode.Default) mode = GraphicsSettings.transparencySortMode;
                        var axis = mode == TransparencySortMode.CustomAxis
                            ? (camera.transparencySortMode == TransparencySortMode.CustomAxis ? camera.transparencySortAxis : GraphicsSettings.transparencySortAxis)
                            : camera.transform.forward;
                        float distance = mode == TransparencySortMode.CustomAxis ? z * axis.z : (z - camera.transform.position.z) * axis.z;
                        key = new SceneRenderOrder.Key(key.Layer, key.Order, key.Queue, distance, key.Material, key.Renderer, key.Slot, key.Shader);
                    }
                    bool owned = image != null && image.Owned;
                    int layer = s.R.gameObject.layer;
                    if (row.Listed && row.Key.CompareTo(key) == 0 && row.Owned == owned && row.Layer == layer && row.Material == material) continue;
                    Remove(row); row.Key = key; row.Material = material; row.Layer = layer; row.Owned = owned; Insert(row);
                    CheckTies(row);
                }
        }
        static void CheckTies(Row row)
        {
            int a = Locate(row), b = a + 1;
            while (a > 0 && row.Key.SameMaterialTie(ordered[a - 1].Key)) a--;
            while (b < ordered.Count && row.Key.SameMaterialTie(ordered[b].Key)) b++;
            if (b - a < 2) return;
            for (int n = a; n < b; n++) if (ordered[n].Owned)
            {
                var tied = ordered[n]; Touch(tied);
                Release(tied.Image, "material-tie"); Reject(tied.Image, "material-tie"); tied.Owned = false;
            }
        }
        static bool Together(Row a, Row b) => a.Owned && b.Owned && LiveDecoPolicy.CanJoin(
            new LiveDecoPolicy.Item(a.Key, true, a.Image.Signature, a.Layer, a.Image.TextureId),
            new LiveDecoPolicy.Item(b.Key, true, b.Image.Signature, b.Layer, b.Image.TextureId), true);
        static void Reconcile()
        {
            if (!topology) return;
            long begin = Stopwatch.GetTimestamp();
            foreach (var run in invalidRuns) Retire(run);
            invalidRuns.Clear();
            while (repair.Count > 0)
            {
                var iterator = repair.GetEnumerator(); iterator.MoveNext(); var seed = iterator.Current; iterator.Dispose();
                repair.Remove(seed);
                if (!seed.Listed || !seed.Owned) continue;
                int a = Locate(seed);
                while (a > 0 && Together(ordered[a - 1], ordered[a])) a--;
                int b = a + 1;
                while (b < ordered.Count && Together(ordered[b - 1], ordered[b])) b++;
                // A newly joined neighbor may still have a valid old run. Only
                // retire runs intersecting this changed contiguous component.
                for (int n = a; n < b; n++) if (ordered[n].Run != null)
                { Retire(ordered[n].Run); }
                var run = CreateRun(a, b);
                run.Index = runs.Count; runs.Add(run);
                for (int n = a; n < b; n++) { ordered[n].Run = run; repair.Remove(ordered[n]); }
            }
            topology = false; transitionCount++;
            long ticks = Stopwatch.GetTimestamp() - begin; reconcileTicks += ticks;
            transitionMs += ticks * 1000.0 / Stopwatch.Frequency;
            // Transition totals are reported once per second, never once per frame.
        }
        static void Retire(Run run)
        {
            foreach (var i in run.Members) foreach (var row in i.Source.Rows)
                if (ReferenceEquals(row.Run, run)) { row.Run = null; repair.Add(row); }
            int last = runs.Count - 1;
            if (run.Index < 0 || run.Index > last || !ReferenceEquals(runs[run.Index], run))
                throw new InvalidOperationException("run index lost");
            runs[run.Index] = runs[last]; runs[run.Index].Index = run.Index; runs.RemoveAt(last);
            run.Index = -1; Dispose(run);
        }
        static int AllocateVertices(int count)
        {
            for (int n = 0; n < freeVertices.Count; n++)
            {
                var block = freeVertices[n]; if (block.Value < count) continue;
                freeVertices.RemoveAt(n);
                if (block.Value > count) freeVertices.Insert(n, new KeyValuePair<int, int>(block.Key + count, block.Value - count));
                return block.Key;
            }
            int offset = vertexEnd; vertexEnd += count;
            if (vertexBuffer.Length < vertexEnd) Array.Resize(ref vertexBuffer, Math.Max(vertexEnd, Math.Max(256, vertexBuffer.Length * 2)));
            return offset;
        }
        static void FreeVertices(Run run)
        {
            if (run.VertexCount == 0) return;
            int start = run.Offset, count = run.VertexCount;
            int n = 0;
            while (n < freeVertices.Count && freeVertices[n].Key < start) n++;
            if (n > 0 && freeVertices[n - 1].Key + freeVertices[n - 1].Value == start)
            { n--; start = freeVertices[n].Key; count += freeVertices[n].Value; freeVertices.RemoveAt(n); }
            if (n < freeVertices.Count && start + count == freeVertices[n].Key)
            { count += freeVertices[n].Value; freeVertices.RemoveAt(n); }
            if (start + count == vertexEnd) vertexEnd = start;
            else freeVertices.Insert(n, new KeyValuePair<int, int>(start, count));
            run.VertexCount = 0;
        }
        static Run CreateRun(int a, int b)
        {
            var first = ordered[a]; var run = new Run();
            try
            {
            // Put every partially-created resource under the root immediately.
            run.Object = new GameObject("LiveDeco.Run") { hideFlags = HideFlags.HideAndDontSave, layer = first.Layer };
            run.Object.transform.SetParent(root.transform, false);
            run.Renderer = run.Object.AddComponent<MeshRenderer>(); run.Renderer.enabled = false;
            run.Mesh = new Mesh { name = "LiveDeco.A", hideFlags = HideFlags.HideAndDontSave };
            run.Filter = run.Object.AddComponent<MeshFilter>(); run.Filter.sharedMesh = run.Mesh;
            run.Members = new Image[b - a]; run.VertexCount = (b - a) * 4;
            run.Offset = AllocateVertices(run.VertexCount);
            var indices = new int[(b - a) * 6];
            for (int n = a; n < b; n++)
            {
                var i = ordered[n].Image; run.Members[n - a] = i;
                for (int k = 0; k < 6; k++) indices[(n - a) * 6 + k] = (n - a) * 4 + i.Indices[k];
            }
            run.Mesh.indexFormat = run.VertexCount > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            run.Mesh.MarkDynamic(); run.Mesh.SetVertexBufferParams(run.VertexCount, Layout);
            if (Marshal.SizeOf(typeof(Vertex)) != 36 || run.Mesh.GetVertexBufferStride(0) != 36
                || run.Mesh.GetVertexAttributeFormat(VertexAttribute.Color) != VertexAttributeFormat.Float32)
                throw new InvalidOperationException("Float32 color vertex layout mismatch");
            run.Mesh.SetIndices(indices, MeshTopology.Triangles, 0, false);
            run.Mesh.bounds = new Bounds(Vector3.zero, new Vector3(Extent * 2, Extent * 2, 0));
            run.Renderer.sharedMaterial = first.Material;
            run.Renderer.sortingLayerID = first.R.sortingLayerID; run.Renderer.sortingOrder = first.R.sortingOrder;
            run.Renderer.shadowCastingMode = ShadowCastingMode.Off; run.Renderer.receiveShadows = false;
            run.Renderer.lightProbeUsage = LightProbeUsage.Off; run.Renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
            var props = new MaterialPropertyBlock();
            props.SetTexture("_MainTex", first.Image.Texture);
            props.SetColor("_RendererColor", Color.white); props.SetVector("_Flip", new Vector4(1, 1, 1, 1));
            run.Renderer.SetPropertyBlock(props);
            return run;
            }
            catch { Dispose(run); throw; }
        }
        static void Dispose(Run run)
        {
            FreeVertices(run);
            if (run.Renderer) run.Renderer.enabled = false;
            if (run.Filter) run.Filter.sharedMesh = null;
            if (run.Mesh) UnityEngine.Object.Destroy(run.Mesh);
            if (run.Object) UnityEngine.Object.Destroy(run.Object);
        }
        static void RenderFrame()
        {
            if (!manager || reset != SceneReset.Count || SceneReset.Resetting || (!Hitch.Playing && !FrameCompare.PlaybackContinuity))
                throw new InvalidOperationException("playback/lifecycle changed");
            if (!SortSupported() || !Identity(manager.transform)) throw new InvalidOperationException("sort/parent changed");
            if (DecoGpu.Active || MatShare.Active || DecoEngine.Enabled) throw new InvalidOperationException("competing owner enabled");
            lastFrame = Time.frameCount;
            long heap = GC.GetTotalMemory(false), alloc = GC.GetAllocatedBytesForCurrentThread(), transitions = transitionCount;
            int checkedBefore = checkCount;
            long compute = Stopwatch.GetTimestamp();
            CaptureCamera();
            computeTicks += Stopwatch.GetTimestamp() - compute;
            long scan = Stopwatch.GetTimestamp();
            bool changed = pending.Count != 0;
            scanTicks += Stopwatch.GetTimestamp() - scan; // Queue probe only; no renderer scan.
            if (changed) DrainChanges();
            Reconcile();
            compute = Stopwatch.GetTimestamp();
            foreach (var run in runs)
            {
                float z = run.Members[0].World[0].z;
                bool depthChanged = run.Z != z;
                if (depthChanged) { run.Z = z; run.Object.transform.position = new Vector3(0, 0, run.Z); }
                var uploadState = new LiveDecoUploadState(run.NeedsUpload || depthChanged);
                for (int n = 0; n < run.Members.Length; n++)
                {
                    var i = run.Members[n]; Compute(i); Color color = Rendered(i.D);
                    if (!uploadState.Fill(i.VertexDirty, !color.Equals(i.UploadedColor))) continue;
                    dirtyImages++;
                    for (int k = 0; k < 4; k++)
                    {
                        var v = i.World[k]; v.z -= run.Z;
                        vertexBuffer[run.Offset + n * 4 + k] = new Vertex { Position = v, Color = color, UV = i.UV[k] };
                    }
                    i.UploadedColor = color; i.VertexDirty = false;
                    // Do not use NeedsUpload here as the per-image fill predicate:
                    // one dirty image need not cause later unchanged images to fill.
                }
                run.NeedsUpload = uploadState.Dirty;
            }
            computeTicks += Stopwatch.GetTimestamp() - compute;
            long upload = Stopwatch.GetTimestamp();
            foreach (var run in runs)
            {
                if (!run.NeedsUpload) { skippedUploads++; continue; }
                run.Mesh.SetVertexBufferData(vertexBuffer, run.Offset, 0, run.VertexCount, 0, Flags);
                run.NeedsUpload = false;
                if (!run.Enabled) { run.Renderer.enabled = true; run.Enabled = true; }
                uploadCalls++;
            }
            foreach (var run in runs) foreach (var i in run.Members)
                if (!i.Hidden) { i.R.forceRenderingOff = true; i.Hidden = true; }
            uploadTicks += Stopwatch.GetTimestamp() - upload;
            heapDelta += GC.GetTotalMemory(false) - heap; frameSamples++;
            long bytes = GC.GetAllocatedBytesForCurrentThread() - alloc; allocatedBytes += bytes;
            if (transitionCount == transitions && checkCount == checkedBefore) { steadyAllocatedBytes += bytes; steadySamples++; }
            if (Time.realtimeSinceStartup >= nextReport) { nextReport = Time.realtimeSinceStartup + 1f; ReportCost(); }
            if (!starting && !FrameCompare.Paused && EnabledSeconds >= 3 && EnabledSeconds <= 5 && ownedCount < 800)
                LiveDecoGate2.OwnedLow(ownedCount, awakeImages);
        }
        static void ReportCost(string phase = "steady")
        {
            if (frameSamples == 0) return;
            if (checkCount != 0) Log("vertexcheck max_err=" + checkMax.ToString("R", Inv) + " count=" + checkCount
                + " excluded=" + checkRejected + " nested_admitted=" + checkNested
                + " visibility_admitted=" + checkVisibility + " threshold_px=0.01 pixel_max_err=" + checkPixelMax.ToString("R", Inv)
                + " vertex_max_err=" + checkVertexMax.ToString("R", Inv) + " bounds_max_err=" + checkBoundsMax.ToString("R", Inv)
                + " quad_bounds_max_err=" + checkQuadBoundsMax.ToString("R", Inv) + " bounds_source=renderer-local-aabb"
                + " histogram_px_lt_001_005_01_05_ge05_nonfinite=" + string.Join(",", errorHistogram));
            foreach (var p in rejected) Log("excluded reason=" + p.Key + " count=" + p.Value);
            foreach (var p in releaseReasons) Log("released reason=" + p.Key + " count=" + p.Value);
            rejected.Clear(); releaseReasons.Clear();
            Log("cost per_frame=1 samples=" + frameSamples + " phase=" + phase + " owned=" + ownedCount + " runs=" + runs.Count
                + " vertex_ms=" + Ms(computeTicks, frameSamples) + " upload_ms=" + Ms(uploadTicks, frameSamples)
                + " reconcile_ms=" + Ms(reconcileTicks + deltaTicks, frameSamples) + " delta_ms=" + Ms(deltaTicks, frameSamples)
                + " scan_ms=" + Ms(scanTicks, frameSamples) + " audit_ms=" + Ms(auditTicks, frameSamples)
                + " awake=" + awakeImages + " dormancy_awake=" + Dormancy.AwakeList.Count + " dirty=" + dirtySources
                + " cache_hits=" + cacheHits + " admissions=" + admissions
                + " write_callbacks=" + writeCallbacks + " watcher_visits=" + watcherVisits
                + " observer_reads=" + LiveDecoChanges.EqualityReads + " unchanged_writes=" + LiveDecoChanges.UnchangedWrites
                + " shadowed_pose_writes=" + LiveDecoChanges.ShadowedPoseWrites + " observer_timing=unmeasured"
                + " upload_calls=" + (uploadCalls / (double)frameSamples).ToString("F3", Inv)
                + " dirty_images=" + (dirtyImages / (double)frameSamples).ToString("F3", Inv)
                + " skipped_uploads=" + (skippedUploads / (double)frameSamples).ToString("F3", Inv)
                + " vertex_target_ms=0.5 upload_target_ms=0.4 mesh_targets_met="
                + (computeTicks * 1000.0 / Stopwatch.Frequency / frameSamples <= .5
                    && uploadTicks * 1000.0 / Stopwatch.Frequency / frameSamples <= .4)
                + " allocated_bytes=" + allocatedBytes + " steady_allocated_bytes=" + steadyAllocatedBytes + " steady_samples=" + steadySamples
                + " heap_delta=" + heapDelta + " heap_scope=frame-work-excludes-cost-and-vertex-logs");
            double song = scrConductor.instance ? scrConductor.instance.songposition_minusi : double.NaN;
            Log("owned_ts t=" + song.ToString("F6", Inv) + " awake=" + awakeImages + " owned=" + ownedCount
                + " runs=" + runs.Count + " dirty=" + dirtySources + " phase=" + phase + " scan_ms=" + Ms(scanTicks, frameSamples)
                + " vertex_ms=" + Ms(computeTicks, frameSamples) + " upload_ms=" + Ms(uploadTicks, frameSamples)
                + " transition_ms=" + transitionMs.ToString("F6", Inv) + " audits=" + auditCount + " missed=" + missed);
            computeTicks = uploadTicks = reconcileTicks = scanTicks = uploadCalls = heapDelta = 0; frameSamples = 0;
            allocatedBytes = steadyAllocatedBytes = 0; steadySamples = 0;
            dirtyImages = skippedUploads = 0;
            deltaTicks = auditTicks = dirtySources = cacheHits = admissions = 0;
            writeCallbacks = watcherVisits = 0;
            LiveDecoChanges.ResetCounts();
            checkMax = checkVertexMax = checkBoundsMax = checkQuadBoundsMax = checkPixelMax = 0;
            checkCount = checkRejected = checkNested = checkVisibility = 0;
            Array.Clear(errorHistogram, 0, errorHistogram.Length);
        }
        internal static void Stop()
        {
            Camera.onPreCull -= PreCull;
            if (!root && harmony == null) { starting = false; return; }
            LiveDecoGate2.RendererStopped();
            ReportCost(starting ? "activation" : "steady"); restoring = true;
            if (root) root.SetActive(false);
            try
            {
                foreach (var run in runs) Dispose(run);
                // Covers a failed CreateRun before it entered runs, too.
                if (root) foreach (var f in root.GetComponentsInChildren<MeshFilter>(true))
                    if (f.sharedMesh) UnityEngine.Object.Destroy(f.sharedMesh);
                foreach (var i in images.Values)
                    try { Release(i, "stop"); } catch (Exception e)
                    { error = "restore:" + e.Message; Log("ERROR " + e.ToString()); LiveDecoGate2.Fail(error); }
            }
            finally
            {
                try { harmony?.UnpatchOwner(PatchId); }
                finally
                {
                    if (root) { root.SetActive(false); UnityEngine.Object.Destroy(root); }
                    root = null; harmony = null; camera = null; manager = null; starting = false;
                    enableBegin = enableAlloc = setupEnd = 0; prepareMs = registerMs = patchMs = 0;
                    sources.Clear(); known.Clear(); images.Clear(); ordered.Clear(); runs.Clear();
                    pending.Clear(); watchers.Clear(); repair.Clear(); invalidRuns.Clear();
                    freeVertices.Clear(); vertexEnd = 0; awakeImages = 0;
                    releaseReasons.Clear(); transitionMs = 0; auditCount = missed = 0;
                    computeTicks = uploadTicks = reconcileTicks = scanTicks = deltaTicks = auditTicks = 0;
                    uploadCalls = dirtyImages = dirtySources = cacheHits = admissions = heapDelta = 0;
                    allocatedBytes = steadyAllocatedBytes = skippedUploads = 0; steadySamples = frameSamples = 0;
                    writeCallbacks = watcherVisits = 0;
                    checkCount = checkRejected = checkNested = checkVisibility = 0;
                    checkMax = checkVertexMax = checkBoundsMax = checkQuadBoundsMax = checkPixelMax = 0;
                    Array.Clear(errorHistogram, 0, errorHistogram.Length);
                    materials.Clear(); rejected.Clear(); ownedCount = 0; restoring = false; topology = false;
                    vertexBuffer = Array.Empty<Vertex>();
                }
            }
        }
        internal static void Destroyed(GameObject target) { if (ReferenceEquals(root, target)) Stop(); }
    }
    internal sealed class LiveDecoLifetime : MonoBehaviour
    {
        void OnDestroy() { LiveDecoRenderer.Destroyed(gameObject); }
    }
}
#endif
