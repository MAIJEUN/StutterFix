#if DEV || AUTOTEST
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DG.Tweening;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace StutterFix
{
    // Explicit diagnostic only. Never calls Camera.Render or replaces the camera chain.
    internal sealed class FrameCompare : MonoBehaviour
    {
        sealed class Shot
        {
            internal byte[] Pixels;
            internal int Width, Height, Frame;
            internal FrameCompareState State;
            internal string Mode;
        }
        static FrameCompare instance;
        internal static bool Paused { get; private set; }
        static scrController controller;
        static bool oldPaused, oldAudio;
        static float oldTime, oldTween;
        // Only our intact diagnostic pause may temporarily replace the bake's
        // frozen tween scale. An external pause/scale edit still invalidates it.
        internal static bool PreservesBakeTweenScale(float expected) => Paused
            && controller && controller == scrController.instance && controller.paused
            && Time.timeScale == 0 && DOTween.timeScale == 0 && AudioListener.pause
            && oldTween == expected;
        static int resumedFrame = -1;
        static scrController resumedController;
        // GcControl/Hitch is sampled before AutoTest.Resume in Update. The
        // manager and camera must keep ownership through that frame's stale
        // paused sample only; the next Update supplies the real playback state.
        internal static bool PlaybackContinuity
        {
            get
            {
                if (Paused) return true;
                if (resumedFrame != Time.frameCount) { resumedController = null; return false; }
                return resumedController && resumedController == scrController.instance
                    && !resumedController.paused && !AudioListener.pause
                    && Time.timeScale > 0 && DOTween.timeScale > 0;
            }
        }
        static readonly Harmony pausePatch = new Harmony("StutterFix.framecmp.pause");
        static readonly HashSet<ParticleSystem> pausedParticles = new HashSet<ParticleSystem>();
        static bool PlanetUpdate() => !Paused;
        static void FreezePlanetUpdate(Type type)
        {
            var methods = type.GetMethods(AccessTools.all).Where(m => m.DeclaringType == type
                && m.Name == "LateUpdate" && !m.IsStatic && !m.IsGenericMethod
                && m.ReturnType == typeof(void) && m.GetParameters().Length == 0).ToArray();
            if (methods.Length != 1) throw new MissingMethodException(type.Name + ".LateUpdate/0: " + methods.Length);
            pausePatch.Patch(methods[0], prefix: new HarmonyMethod(typeof(FrameCompare), nameof(PlanetUpdate)));
        }
        static void FreezePlanets()
        {
            // Both callbacks read unscaledTime at timeScale=0. PlanetSprite writes
            // material._Frame without changing the renderer matrix/sprite/color.
            // Keep the last displayed frame; Resume unpatches both, without a setter.
            FreezePlanetUpdate(typeof(PlanetRenderer));
            FreezePlanetUpdate(typeof(PlanetSprite));
            foreach (var planet in Resources.FindObjectsOfTypeAll<PlanetRenderer>())
            {
                if (!planet || !planet.gameObject.scene.IsValid()) continue;
                var particles = new HashSet<ParticleSystem>(planet.GetComponentsInChildren<ParticleSystem>(true));
                foreach (string field in new[] { "tailParticles", "tailParticlesCoop", "coreParticles" })
                    if (AccessTools.Field(typeof(PlanetRenderer), field)?.GetValue(planet) is ParticleSystem ps) particles.Add(ps);
                foreach (var ps in particles)
                    if (ps && ps.isPlaying) { pausedParticles.Add(ps); ps.Pause(false); }
            }
            Log("pause planet LateUpdate and PlanetSprite._Frame frozen; owned playing particle systems=" + pausedParticles.Count);
        }
        static void RestorePlanets()
        {
            try { pausePatch.UnpatchOwner(pausePatch.Id); }
            finally
            {
                foreach (var ps in pausedParticles)
                    try { if (ps && ps.isPaused) ps.Play(false); }
                    catch (Exception e) { Log("particle restore failed: " + e.Message); }
                pausedParticles.Clear();
            }
        }
        readonly Dictionary<string, Shot> shots = new Dictionary<string, Shot>(StringComparer.Ordinal);
        readonly Dictionary<Behaviour, bool> filters = new Dictionary<Behaviour, bool>();
        readonly Dictionary<Canvas, bool> canvases = new Dictionary<Canvas, bool>();
        readonly Dictionary<Camera, int> masks = new Dictionary<Camera, int>();
        bool filtersOff, includeUi, busy, ready, retiring;
        string error;
        bool centerMask = true;
        bool resultLogged, lastNoiseStable;
        long lastNoiseOutside;
        bool noiseValid, aa2Valid, orderValid, hideValid;
        void ReportResult()
        {
            if (resultLogged) return;
            Log($"framecmp RESULT noise_out={noiseOut} aa2_out={aa2Out} ab_out={abOut} ctl_order={ctlOrder} ctl_hide={ctlHide}");
            Log($"framecmp VERDICT noise={FrameCompareControlPolicy.Verdict(noiseOut, false, noiseValid)} aa2={FrameCompareControlPolicy.Verdict(aa2Out, false, aa2Valid)} order_ctl={FrameCompareControlPolicy.Verdict(ctlOrder, true, orderValid)} hide_ctl={FrameCompareControlPolicy.Verdict(ctlHide, true, hideValid)}");
            resultLogged = true;
        }
        string noiseOut = "NA", aa2Out = "NA", abOut = "NA", ctlOrder = "NA", ctlHide = "NA";
        FrameCompareMetrics.Mask[] Masks(string[] args, int start)
        {
            var result = new List<FrameCompareMetrics.Mask>();
            if (centerMask) result.Add(FrameCompareMetrics.Mask.Parse("maskpct=0.5,0.5,0.2,0.2"));
            for (int i = start; i < args.Length; i++)
                if (args[i] != "png") result.Add(FrameCompareMetrics.Mask.Parse(args[i]));
            return result.ToArray();
        }
        RenderTexture pendingTexture;
        AsyncGPUReadbackRequest pending;
        bool hasPending;
        Action restoreControl;
        const int MaxShots = 8;
        internal static FrameCompareMetrics.Result GatePixel(out long restored, out bool stable, bool maskCenter = true)
        {
            var p = Get(); p.RequirePaused();
            if (p.busy) throw new InvalidOperationException("capture busy");
            var mask = maskCenter ? new[] { FrameCompareMetrics.Mask.Parse("maskpct=0.5,0.5,0.2,0.2") } : Array.Empty<FrameCompareMetrics.Mask>();
            var result = p.Diff("A", "B", 0, mask);
            restored = p.Diff("A", "A2", 0, mask).Changed;
            var delta = p.shots["A"].State.Compare(p.shots["A2"].State);
            p.LogDelta("gate2-restore", p.shots["A"].State, p.shots["A2"].State, delta);
            stable = p.noiseValid && p.lastNoiseStable && p.lastNoiseOutside == 0 && delta.Changed == 0;
            Log("gate2-baseline noise_valid=" + p.noiseValid + " noise_out=" + p.lastNoiseOutside
                + " noise_state_stable=" + p.lastNoiseStable + " restored_fields=" + delta.Changed + " aa2_out=" + restored);
            return result;
        }
        // Named, unmasked diagnostics for render-merge. Callers cannot replace
        // runtime evidence with numeric command arguments.
        internal static FrameCompareMetrics.Result CompareShots(string a,string b,out bool stateEqual)
        {
            var p=Get();p.RequirePaused();
            if(p.busy)throw new InvalidOperationException("capture busy");
            var result=p.Diff(a,b,0,Array.Empty<FrameCompareMetrics.Mask>());
            stateEqual=p.shots[a].State.Compare(p.shots[b].State).Changed==0;
            return result;
        }
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static void Log(string s) => Main.Entry.Logger.Log("[framecmp] " + s);
        static FrameCompare Get()
        {
            if (instance && instance.retiring) throw new InvalidOperationException("timed-out GPU readback still draining; retry after completion");
            if (!instance) { instance = new GameObject("StutterFix.FrameCompare").AddComponent<FrameCompare>(); DontDestroyOnLoad(instance.gameObject); }
            return instance;
        }
        internal static void Pause()
        {
            if (Paused) return;
            resumedFrame = -1; resumedController = null;
            controller = scrController.instance;
            if (!controller) throw new InvalidOperationException("pause requires a loaded controller");
            oldPaused = controller.paused; oldAudio = AudioListener.pause;
            oldTime = Time.timeScale; oldTween = DOTween.timeScale;
            Paused = true;
            try { controller.paused = true; Time.timeScale = 0; DOTween.timeScale = 0; AudioListener.pause = true; FreezePlanets(); }
            catch { Resume(); throw; }
            Log("pause controller.paused=1 Time.timeScale=0 DOTween.timeScale=0 AudioListener.pause=1; UI pause menu not invoked");
        }
        internal static void Resume()
        {
            TileRender.Stop();
#if AUTOTEST
            GpuDecoSnapshot.Stop();
#endif
            if (!Paused) return;
            var resumed = controller;
            try { if (instance) instance.RestoreCapture(); }
            finally
            {
                try { try { RestorePlanets(); } finally { if (controller) controller.paused = oldPaused; } }
                finally { Time.timeScale = oldTime; DOTween.timeScale = oldTween; AudioListener.pause = oldAudio; Paused = false; controller = null; }
            }
            // This runs only after successful restoration. A failed restore or
            // an originally paused controller cannot keep a live owner alive.
            resumedController = resumed;
            resumedFrame = Time.frameCount;
            Log("resume restored continuity_frame=" + resumedFrame + " tracking=" + PlaybackContinuity);
        }
        internal static bool Step(string arg)
        {
            var p = Get();
            if (p.busy)
            {
                if (!p.ready) return false;
                p.busy = p.ready = false;
                if (p.error != null) throw new InvalidOperationException(p.error);
                return true;
            }
            var a = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (a.Length == 2 && a[0] == "filters" && (a[1] == "off" || a[1] == "on"))
            {
                p.RestoreCapture();
                p.filtersOff = a[1] == "off";
                Log("filters=" + a[1] + " (capture scope retained until options change/resume/stop/error)"); return true;
            }
            if (a.Length == 2 && a[0] == "ui" && (a[1] == "include" || a[1] == "exclude"))
            { p.RestoreCapture(); p.includeUi = a[1] == "include"; Log("ui=" + a[1]); return true; }
            if (a.Length == 2 && a[0] == "mask" && (a[1] == "center" || a[1] == "none"))
            { p.centerMask = a[1] == "center"; Log("mask=" + a[1]); return true; }
            if (a.Length == 1 && a[0] == "result")
            {
                p.ReportResult();
                return true;
            }
            if (a.Length >= 3 && a[0] == "assertzero")
            {
                if (a.Length > 4 || (a.Length == 4 && a[3] != "png")) throw new ArgumentException("framecmp assertzero A B [png]");
                var r = p.Diff(a[1], a[2], 0);
                p.aa2Out = r.Changed.ToString(Inv);
                var delta = p.shots[a[1]].State.Compare(p.shots[a[2]].State);
                p.aa2Valid = delta.Changed == 0;
                p.LogDelta("negative-control", p.shots[a[1]].State, p.shots[a[2]].State, delta);
                if (a.Length == 4) { p.Save(a[1]); p.Save(a[2]); }
                if (r.Changed != 0 || delta.Changed != 0) throw new InvalidOperationException("negative control requires zero pixels and stable capture state");
                return true;
            }
            // Renderer replacement deliberately changes forceRenderingOff. Keep
            // assertzero strict for noise/restoration; candidate admission is
            // checked separately and only the candidate's pixels are asserted here.
            if (a.Length == 3 && a[0] == "assertpixels")
            {
                var r = p.Diff(a[1], a[2], 0);
                if (r.Changed != 0) throw new InvalidOperationException("candidate requires zero differing pixels");
                Log("assertpixels passed; cross-shot renderer state equality not asserted");
                return true;
            }
            if (a.Length >= 3 && a[0] == "diff")
            {
                int start = 3, threshold = 0;
                if (a.Length > 3 && a[3] != "png" && !a[3].StartsWith("mask", StringComparison.Ordinal))
                { threshold = int.Parse(a[3], Inv); start++; }
                var result = p.Diff(a[1], a[2], threshold, p.Masks(a, start));
                p.abOut = result.Changed.ToString(Inv);
                if (Array.IndexOf(a, "png") >= 0) { p.Save(a[1]); p.Save(a[2]); }
                return true;
            }
            if (a.Length == 2 && a[0] == "save") { p.Save(a[1]); return true; }
            if (a.Length == 1 && a[0] == "clear") { p.shots.Clear(); return true; }
            IEnumerator work;
            if (a.Length == 2 && a[0] == "grab") work = p.Grab(a[1]);
            else if (a.Length >= 3 && a[0] == "noise")
            {
                var selected = p.Masks(a, 3);
                p.noiseOut = p.aa2Out = p.abOut = p.ctlOrder = p.ctlHide = "NA";
                p.resultLogged = false;
                p.noiseValid = p.aa2Valid = p.orderValid = p.hideValid = false;
                work = p.Noise(a[1], a[2], selected, true);
            }
            else if (a.Length == 2 && a[0] == "control" && (a[1] == "hide" || a[1] == "order")) work = p.Control(a[1]);
            else throw new ArgumentException("framecmp filters off|on; ui exclude|include; grab NAME; mask center|none; result; diff A B [threshold] [png] [mask=x,y,w,h ...] [maskpct=cx,cy,w,h ...]; assertzero A B [png]; noise A B [mask=... maskpct=...]; control hide|order; save NAME; clear");
            p.error = null; p.ready = false; p.busy = true;
            p.StartCoroutine(p.Guarded(work)); return false;
        }
        IEnumerator Guarded(IEnumerator work)
        {
            // Flatten nested iterators so every exception reaches AutoTest, with restoration.
            var stack = new Stack<IEnumerator>(); stack.Push(work);
            try
            {
                while (stack.Count > 0)
                {
                    object next = null; bool more = false;
                    try { more = stack.Peek().MoveNext(); if (more) next = stack.Peek().Current; }
                    catch (Exception e) { error = e.Message; Log("valid=0 error=" + error); break; }
                    if (!more) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                    if (next is IEnumerator child) stack.Push(child); else yield return next;
                }
            }
            finally
            {
                while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose();
                RestoreControl();
                if (error != null) RestoreCapture(); else ReleasePending(false);
                ready = true;
            }
        }
        void RequirePaused()
        {
            if (!Paused || !controller || !controller.paused || Time.timeScale != 0 || DOTween.timeScale != 0 || !AudioListener.pause)
                throw new InvalidOperationException("capture requires intact pause; use pause first");
        }
        IEnumerator Grab(string name)
        {
            RequirePaused(); ValidateName(name);
            if (hasPending && !pending.done) throw new InvalidOperationException("previous timed-out readback still pending");
            ReleasePending(false);
            if (!shots.ContainsKey(name) && shots.Count >= MaxShots) throw new InvalidOperationException("8-shot limit; framecmp clear first");
            if (!SystemInfo.supportsAsyncGPUReadback) throw new InvalidOperationException("AsyncGPUReadback unsupported on " + SystemInfo.graphicsDeviceType);
            // Permit capture-scope changes to settle for one natural frame.
            ApplyCapture();
            Camera.onPreCull -= EnforceCapture;
            Camera.onPreCull += EnforceCapture;
            yield return null;
            yield return new WaitForEndOfFrame();
            RequirePaused();
            if (filters.Keys.Any(b => b && b.enabled) || canvases.Keys.Any(c => c && c.enabled) ||
                masks.Keys.Any(c => c && c.cullingMask != (masks[c] & ~(1 << LayerMask.NameToLayer("UI")))))
                throw new InvalidOperationException("capture options overwritten during rendering");
            int w = Screen.width, h = Screen.height;
            var state = State();
            Log($"capture-context frame={Time.frameCount} time={Time.time:R} unscaledTime={Time.unscaledTime:R} realtime={Time.realtimeSinceStartupAsDouble:R} shader_Time={Shader.GetGlobalVector("_Time").ToString("R")} shader_DeltaTime={Shader.GetGlobalVector("unity_DeltaTime").ToString("R")} screenMSAA={QualitySettings.antiAliasing} captureMSAA=1");
            foreach (var e in state.Entries.Where(e => e.Key.StartsWith("camera:", StringComparison.Ordinal)))
                Log("camera " + e.Key + " " + e.Value.Label + " " + string.Join(" ", e.Value.Fields.Select(f => f.Key + "=" + f.Value)));
            pendingTexture = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear) { name = "SF.framecmp.RGBA8", antiAliasing = 1 };
            if (!pendingTexture.Create()) throw new InvalidOperationException($"RT create failed {w}x{h} ARGB32 non-MSAA");
            var active = RenderTexture.active;
            try { ScreenCapture.CaptureScreenshotIntoRenderTexture(pendingTexture); }
            finally { RenderTexture.active = active; }
            int frame = Time.frameCount;
            double auditStart = Time.realtimeSinceStartupAsDouble;
            // Validate submission in the SAME end-of-frame phase. The dedicated RT is
            // immutable while readback is pending; subsequent scene updates cannot alter it.
            var submitted = State(); var submissionDelta = state.Compare(submitted);
            LogDelta("submission", state, submitted, submissionDelta);
            if (submissionDelta.Changed != 0) throw new InvalidOperationException("capture state changed during submission: " + submissionDelta.Details);
            // State() walks the entire scene (157k renderers in Arche). Do that
            // before submitting the readback so its CPU time cannot consume the
            // GPU deadline while this coroutine still owns the main thread.
            double auditMs = (Time.realtimeSinceStartupAsDouble - auditStart) * 1000;
            pending = AsyncGPUReadback.Request(pendingTexture, 0, TextureFormat.RGBA32); hasPending = true;
            double start = Time.realtimeSinceStartupAsDouble;
            // Always return to the player loop once, allowing queued GPU work
            // and request completion to progress before testing the deadline.
            yield return null;
            while (!pending.done)
            {
                if (Time.realtimeSinceStartupAsDouble - start > 15) throw new TimeoutException("GPU readback exceeded 15s; pending RT retained until completion");
                yield return null;
            }
            double waitMs = (Time.realtimeSinceStartupAsDouble - start) * 1000;
            if (pending.hasError) throw new InvalidOperationException($"readback failed {w}x{h} ARGB32->RGBA32 device={SystemInfo.graphicsDeviceType}");
            RequirePaused();
            double completionAuditStart = Time.realtimeSinceStartupAsDouble;
            var completed = State(); var waitDelta = submitted.Compare(completed);
            LogDelta("readback-wait-advisory", submitted, completed, waitDelta);
            Log($"readback sourceFrame={frame} completionFrame={Time.frameCount} elapsedFrames={Time.frameCount - frame} elapsedMs={waitMs:F3} submissionAuditMs={auditMs:F3} completionAuditMs={(Time.realtimeSinceStartupAsDouble - completionAuditStart) * 1000:F3} rt={pendingTexture.width}x{pendingTexture.height} screenNow={Screen.width}x{Screen.height}");
            if (!pendingTexture.IsCreated() || pendingTexture.width != w || pendingTexture.height != h ||
                pending.width != w || pending.height != h || pending.GetData<byte>().Length != checked(w * h * 4))
                throw new InvalidOperationException("readback RT/data dimensions changed");
            shots[name] = new Shot { Pixels = pending.GetData<byte>().ToArray(), Width = w, Height = h, Frame = frame, State = state,
                Mode = $"filtersOff={filtersOff},includeUi={includeUi},device={SystemInfo.graphicsDeviceType},color={QualitySettings.activeColorSpace}" };
            Log($"grab {name} valid=1 {w}x{h} frame={frame} {state.Summary} {shots[name].Mode} disabledFilters={filters.Count} disabledCanvases={canvases.Count} maskedCameras={masks.Count}");
            ReleasePending(false);
        }
        IEnumerator Noise(string a, string b, FrameCompareMetrics.Mask[] selected = null, bool record = false)
        {
            if (a == b) throw new ArgumentException("noise needs distinct names");
            yield return Grab(a); yield return Grab(b);
            var r = Diff(a, b, 0, selected);
            var delta = shots[a].State.Compare(shots[b].State);
            LogDelta("noise", shots[a].State, shots[b].State, delta);
            bool stable = delta.Changed == 0;
            lastNoiseOutside = r.Changed; lastNoiseStable = stable;
            if (record) { noiseOut = r.Changed.ToString(Inv); noiseValid = stable; }
            Log($"noise outside={r.Changed} inside={r.InsideChanged} rendererStateEqual={stable} warning={(!stable || r.Changed != 0)}; noise floor advisory, continuing");
        }
        FrameCompareMetrics.Result Diff(string a, string b, int threshold, FrameCompareMetrics.Mask[] selected = null)
        {
            if (!shots.TryGetValue(a, out var x) || !shots.TryGetValue(b, out var y)) throw new ArgumentException("missing shot");
            if (x.Width != y.Width || x.Height != y.Height || x.Mode != y.Mode) throw new InvalidOperationException("capture dimensions/options differ");
            var r = FrameCompareMetrics.Compare(x.Pixels, y.Pixels, x.Width, x.Height, threshold, selected ?? Masks(Array.Empty<string>(), 0));
            if (r.OutsidePixels == 0) throw new InvalidOperationException("mask excludes all pixels; no outside evidence");
            Log($"mask inside_changed={r.InsideChanged}/{r.InsidePixels} outside_changed={r.Changed}/{r.OutsidePixels} origin=bottom-left samples=pixel-centers preset={(centerMask ? "center" : "none")}");
            Log(string.Format(Inv, "diff {0} {1} threshold={2} maxRGBA={3} changed={4}/{5} ratio={6:R} PSNR={7:R} top32(bottom-left,x,y,w,h,L1)={8}",
                a, b, threshold, string.Join(",", r.Max), r.Changed, r.OutsidePixels, r.Ratio, r.Psnr, r.Regions));
            return r;
        }
        void ApplyCapture()
        {
            if (filtersOff)
            {
                var filterTypes = new Dictionary<Type, bool>();
                foreach (var b in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
                {
                    if (!b || !b.gameObject.scene.IsValid()) continue;
                    var type = b.GetType(); string n = type.Name;
                    if (!filterTypes.TryGetValue(type, out bool isFilter))
                    {
                        isFilter = n.StartsWith("CameraFilterPack", StringComparison.Ordinal) || n.IndexOf("Bloom", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            type.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                            .Any(m => m.Name == "OnRenderImage" && m.GetParameters().Length == 2);
                        filterTypes.Add(type, isFilter);
                    }
                    if (isFilter && !filters.ContainsKey(b))
                    { filters.Add(b, b.enabled); b.enabled = false; }
                }
            }
            if (includeUi) return;
            // Disable explicit canvases, then remove only Unity's dedicated UI layer from cameras.
            // OverlayCam itself is NEVER disabled: it may composite the game camRT.
            foreach (var c in Resources.FindObjectsOfTypeAll<Canvas>())
                if (c && c.gameObject.scene.IsValid() && !canvases.ContainsKey(c)) { canvases.Add(c, c.enabled); c.enabled = false; }
            int layer = LayerMask.NameToLayer("UI");
            // A final composition quad may share the UI layer. Keep that layer intact in that case.
            bool sharedWithWorld = layer >= 0 && Resources.FindObjectsOfTypeAll<Renderer>().Any(r => r && r.gameObject.scene.IsValid() &&
                r.gameObject.layer == layer && r.enabled && r.gameObject.activeInHierarchy && !r.GetComponentInParent<Canvas>(true));
            if (layer >= 0 && !sharedWithWorld) foreach (var c in Camera.allCameras)
                if (!masks.ContainsKey(c)) { masks.Add(c, c.cullingMask); c.cullingMask &= ~(1 << layer); }
        }
        void RestoreFilters() { foreach (var x in filters) if (x.Key) x.Key.enabled = x.Value; filters.Clear(); }
        void RestoreCapture()
        {
            Camera.onPreCull -= EnforceCapture;
            ReleasePending(false);
            RestoreFilters();
            foreach (var x in canvases) if (x.Key) x.Key.enabled = x.Value; canvases.Clear();
            foreach (var x in masks) if (x.Key) x.Key.cullingMask = x.Value; masks.Clear();
        }
        void EnforceCapture(Camera camera)
        {
            foreach (var b in filters.Keys) if (b) b.enabled = false;
            foreach (var c in canvases.Keys) if (c) c.enabled = false;
            if (masks.TryGetValue(camera, out int mask)) camera.cullingMask = mask & ~(1 << LayerMask.NameToLayer("UI"));
        }
        void ReleasePending(bool drain)
        {
            if (hasPending)
            {
                if (!pending.done && drain) pending.WaitForCompletion();
                if (!pending.done) return; // Timeout reports failure without freeing an in-flight RT.
                hasPending = false;
            }
            if (pendingTexture) { pendingTexture.Release(); Destroy(pendingTexture); pendingTexture = null; }
        }
        void Update()
        {
            if (!busy && hasPending && pending.done) ReleasePending(false);
            if (retiring && !hasPending) { if (instance == this) instance = null; Destroy(gameObject); }
        }
        void LogDelta(string phase, FrameCompareState before, FrameCompareState after, FrameCompareState.Delta delta)
        {
            Log($"state phase={phase} changedFields={delta.Changed} ignoredHiddenFields={delta.Ignored} before=[{before.Summary}] after=[{after.Summary}] details={delta.Details}");
        }
        static string Matrix(Matrix4x4 m) => string.Join(",", Enumerable.Range(0, 16).Select(i => m[i].ToString("R", Inv)));
        static string Color(Color c) => c.r.ToString("R", Inv) + "," + c.g.ToString("R", Inv) + "," + c.b.ToString("R", Inv) + "," + c.a.ToString("R", Inv);
        static ulong Hash(IEnumerable<string> values)
        {
            ulong h = 14695981039346656037UL;
            unchecked { foreach (string v in values) { foreach (char c in v) h = (h ^ c) * 1099511628211UL; h = (h ^ 0xFFFF) * 1099511628211UL; } }
            return h;
        }
        FrameCompareState State()
        {
            var state = new FrameCompareState();
            var screen = new FrameCompareState.Entry { Label = "screen", Relevant = true };
            screen.Fields["size"] = Screen.width + "x" + Screen.height;
            screen.Fields["fullscreen"] = Screen.fullScreenMode.ToString();
            screen.Fields["colorSpace"] = QualitySettings.activeColorSpace.ToString();
            screen.Fields["MSAA"] = QualitySettings.antiAliasing.ToString(Inv);
            state.Entries.Add("screen", screen);
            // Only these game sprites animate via a material uniform. Read sharedMaterial
            // to avoid cloning it; ordinary renderer fields cannot detect this drift.
            var planetSprites = new HashSet<int>(Resources.FindObjectsOfTypeAll<PlanetSprite>()
                .Where(p => p && p.gameObject.scene.IsValid() && p.meshRenderer)
                .Select(p => p.meshRenderer.GetInstanceID()));
            var renderers = Resources.FindObjectsOfTypeAll<Renderer>().Where(r => r && r.gameObject.scene.IsValid()).OrderBy(r => r.GetInstanceID()).ToArray();
            foreach (var r in renderers)
            {
                var entry = new FrameCompareState.Entry { Label = r.name.Replace('\n', ' ').Replace('\r', ' '),
                    Relevant = r.enabled && !r.forceRenderingOff && r.gameObject.activeInHierarchy };
                var f = entry.Fields;
                f["enabled"] = r.enabled.ToString(); f["forceRenderingOff"] = r.forceRenderingOff.ToString();
                f["activeInHierarchy"] = r.gameObject.activeInHierarchy.ToString();
                f["matrix"] = Matrix(r.localToWorldMatrix); f["layer"] = r.gameObject.layer.ToString(Inv);
                f["sortingLayer"] = r.sortingLayerID.ToString(Inv); f["sortingOrder"] = r.sortingOrder.ToString(Inv);
                f["materials"] = string.Join(",", r.sharedMaterials.Select(m => m ? m.GetInstanceID() : 0));
                if (r is SpriteRenderer s)
                {
                    f["sprite"] = (s.sprite ? s.sprite.GetInstanceID() : 0).ToString(Inv);
                    f["color"] = Color(s.color); f["flip"] = s.flipX + "," + s.flipY;
                    if (planetSprites.Contains(r.GetInstanceID()))
                    {
                        var material = r.sharedMaterial;
                        f["planetFrame"] = material ? material.GetFloat("_Frame").ToString("R", Inv) : "no-material";
                    }
                }
                state.Entries.Add("renderer:" + r.GetInstanceID().ToString(Inv), entry);
            }
            var cameras = Resources.FindObjectsOfTypeAll<Camera>().Where(c => c && c.gameObject.scene.IsValid()).OrderBy(c => c.GetInstanceID()).ToArray();
            foreach (var c in cameras)
            {
                var entry = new FrameCompareState.Entry { Label = c.name.Replace('\n', ' ').Replace('\r', ' '), Relevant = c.enabled && c.gameObject.activeInHierarchy };
                var f = entry.Fields; var rt = c.targetTexture;
                f["enabled"] = c.enabled.ToString(); f["activeInHierarchy"] = c.gameObject.activeInHierarchy.ToString();
                f["view"] = Matrix(c.worldToCameraMatrix); f["projection"] = Matrix(c.projectionMatrix); f["culling"] = Matrix(c.cullingMatrix);
                f["mask"] = c.cullingMask.ToString(Inv); f["depth"] = c.depth.ToString("R", Inv);
                f["rect"] = c.rect.ToString("R"); f["pixelRect"] = c.pixelRect.ToString("R");
                f["clear"] = c.clearFlags.ToString(); f["background"] = Color(c.backgroundColor);
                f["HDR_MSAA"] = c.allowHDR + "," + c.allowMSAA; f["targetDisplay"] = c.targetDisplay.ToString(Inv);
                f["sort"] = c.transparencySortMode + "," + c.transparencySortAxis.ToString("R");
                f["RT"] = rt ? $"id={rt.GetInstanceID()},size={rt.width}x{rt.height},format={rt.format},MSAA={rt.antiAliasing},created={rt.IsCreated()}" : "backbuffer";
                state.Entries.Add("camera:" + c.GetInstanceID().ToString(Inv), entry);
            }
            ulong list = Hash(renderers.Select(r => r.GetInstanceID().ToString(Inv)));
            ulong raw = Hash(state.Entries.SelectMany(e => new[] { e.Key }.Concat(e.Value.Fields.Select(f => f.Key + "=" + f.Value))));
            state.Summary = $"screen={Screen.width}x{Screen.height} renderers={renderers.Length} listHash={list:X16} rawHash={raw:X16} cameras={cameras.Length} frame={Time.frameCount}";
            return state;
        }
        // Only our own objects are changed. No auxiliary camera or Camera.Render.
        sealed class SyntheticControl
        {
            readonly List<UnityEngine.Object> owned = new List<UnityEngine.Object>();
            internal SpriteRenderer First, Second;
            internal Material Back, Front;
            T Own<T>(T value) where T : UnityEngine.Object { owned.Add(value); return value; }
            internal void Dispose()
            {
                // Destroy is deferred; disable immediately so stop/error cannot leave a draw.
                if (First) First.enabled = false;
                if (Second) Second.enabled = false;
                for (int i = owned.Count - 1; i >= 0; i--)
                    if (owned[i]) { if (owned[i] is GameObject go) go.SetActive(false); Destroy(owned[i]); }
                owned.Clear();
            }
            internal void Create()
            {
                var camera = GpuSplit.FindMain();
                if (!camera.isActiveAndEnabled || camera.targetDisplay != 0)
                    throw new InvalidOperationException("synthetic control requires active main camera on display 0");
                var shader = Shader.Find("Sprites/Tile");
                if (!shader || !shader.isSupported) throw new InvalidOperationException("Sprites/Tile unavailable; no fallback");
                int layer = -1, ui = LayerMask.NameToLayer("UI");
                for (int i = 0; i < 32; i++)
                    if (i != ui && (camera.cullingMask & (1 << i)) != 0) { layer = i; break; }
                if (layer < 0) throw new InvalidOperationException("main camera has no non-UI visible layer");
                var texture = Own(new Texture2D(2, 2, TextureFormat.RGBA32, false));
                texture.name = "SF.framecmp.white";
                texture.filterMode = FilterMode.Point;
                texture.SetPixels(new[] { UnityEngine.Color.white, UnityEngine.Color.white, UnityEngine.Color.white, UnityEngine.Color.white });
                texture.Apply(false, false);
                var sprite = Own(Sprite.Create(texture, new Rect(0, 0, 2, 2), new Vector2(.5f, .5f), 2, 0, SpriteMeshType.FullRect));
                var m1 = Own(new Material(shader) { name = "SF.framecmp.material1", mainTexture = texture });
                var m2 = Own(new Material(shader) { name = "SF.framecmp.material2", mainTexture = texture });
                foreach (var material in new[] { m1, m2 })
                {
                    material.renderQueue = 3000;
                    foreach (string property in new[] { "_Color", "_RendererColor" })
                        if (material.HasProperty(property)) material.SetColor(property, UnityEngine.Color.white);
                }
                if (m1.GetInstanceID() == m2.GetInstanceID()) throw new InvalidOperationException("distinct material IDs required");
                // Assignment guarantees signed ascending IDs even when allocation runs backwards.
                bool firstBehind = FrameCompareControlPolicy.FirstBehind(m1.GetInstanceID(), m2.GetInstanceID());
                Back = firstBehind ? m1 : m2; Front = firstBehind ? m2 : m1;
                int sortingLayer = SortingLayer.layers.OrderByDescending(x => x.value).First().id;
                float range = camera.farClipPlane - camera.nearClipPlane;
                if (range <= 0) throw new InvalidOperationException("invalid camera clip range");
                float depth = camera.nearClipPlane + Mathf.Min(1f, range * .1f);
                Vector3 center = camera.ViewportToWorldPoint(new Vector3(.18f, .18f, depth));
                Vector3 right = camera.ViewportToWorldPoint(new Vector3(.24f, .18f, depth));
                Vector3 up = camera.ViewportToWorldPoint(new Vector3(.18f, .24f, depth));
                float width = (right - center).magnitude * 2, height = (up - center).magnitude * 2;
                if (!(width > 0 && height > 0)) throw new InvalidOperationException("invalid synthetic projection");
                First = Make("red", UnityEngine.Color.red, Back);
                Second = Make("blue", UnityEngine.Color.blue, Front);
                SpriteRenderer Make(string name, UnityEngine.Color color, Material material)
                {
                    var go = Own(new GameObject("SF.framecmp." + name));
                    go.layer = layer;
                    go.transform.SetPositionAndRotation(center, camera.transform.rotation);
                    go.transform.localScale = new Vector3(width, height, 1);
                    var renderer = go.AddComponent<SpriteRenderer>();
                    renderer.sprite = sprite; renderer.color = color; renderer.sharedMaterial = material;
                    renderer.sortingLayerID = sortingLayer; renderer.sortingOrder = 32767;
                    return renderer;
                }
                Log($"synthetic camera={camera.GetInstanceID()} target={(camera.targetTexture ? camera.targetTexture.name : "backbuffer")} viewport=(0.12,0.12,0.12,0.12) layer={layer} sortingLayer={sortingLayer} order=32767 depth={depth:R} signedMaterialIDs(back,front)={Back.GetInstanceID()},{Front.GetInstanceID()} renderers={First.GetInstanceID()},{Second.GetInstanceID()} opaque=red,blue sameTransform=1");
            }
            internal void Restore()
            {
                if (First) { First.sharedMaterial = Back; First.forceRenderingOff = false; }
                if (Second) { Second.sharedMaterial = Front; Second.forceRenderingOff = false; }
            }
        }
        IEnumerator Control(string kind)
        {
            RequirePaused(); shots.Clear();
            if (kind == "order") { orderValid = false; ctlOrder = "NA"; }
            else { hideValid = false; ctlHide = "NA"; }
            double started = Time.realtimeSinceStartupAsDouble;
            Log("control " + kind + " synthetic begin maxAttempts=1 maxCaptures=4 readbackTimeoutSeconds=15");
            var synthetic = new SyntheticControl();
            // Register before allocation so partial setup, stop, unload and errors all clean up.
            restoreControl = synthetic.Dispose;
            try
            {
                synthetic.Create();
                // Baseline includes the synthetic pair. Both quads lie outside center exclusion.
                yield return Noise("_controlA", "_controlN");
                if (lastNoiseOutside != 0 || !lastNoiseStable)
                {
                    if (kind == "order") ctlOrder = "INCONCLUSIVE"; else ctlHide = "INCONCLUSIVE";
                    Log("control " + kind + " inconclusive: nonzero outside noise/unstable state; continuing without positive claim");
                    yield break;
                }
                if (kind == "order")
                {
                    synthetic.First.sharedMaterial = synthetic.Front;
                    synthetic.Second.sharedMaterial = synthetic.Back;
                    Log($"control order synthetic swapped signedMaterialIDs={synthetic.First.sharedMaterial.GetInstanceID()},{synthetic.Second.sharedMaterial.GetInstanceID()} sorting/z unchanged");
                }
                else
                {
                    synthetic.Second.forceRenderingOff = true;
                    Log("control hide synthetic front renderer=" + synthetic.Second.GetInstanceID());
                }
                yield return Grab("_controlB");
                synthetic.Restore();
                yield return Grab("_controlA2");
                var restored = Diff("_controlA", "_controlA2", 0);
                var changed = Diff("_controlA", "_controlB", 0);
                var delta = shots["_controlA"].State.Compare(shots["_controlA2"].State);
                LogDelta("control-restore", shots["_controlA"].State, shots["_controlA2"].State, delta);
                bool valid = restored.Changed == 0 && delta.Changed == 0;
                if (kind == "order") { ctlOrder = changed.Changed.ToString(Inv); orderValid = valid; }
                else { ctlHide = changed.Changed.ToString(Inv); hideValid = valid; }
                // Report zero/failed restoration as FAIL, but let the independent hide control run.
                Log($"control {kind} synthetic valid={(valid && changed.Changed > 0 ? 1 : 0)} restored={restored.Changed} positive={changed.Changed} stateStable={delta.Changed == 0} attempts=1 captures=4; continuing");
            }
            finally
            {
                RestoreControl();
                Log(FormattableString.Invariant($"control {kind} synthetic cleanup elapsedSeconds={Time.realtimeSinceStartupAsDouble - started:F3}"));
            }
            // Let Unity complete deferred destruction before the next command.
            yield return null;
        }
        void RestoreControl() { var restore = restoreControl; restoreControl = null; restore?.Invoke(); }
        static void ValidateName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 64 || name.Any(c => !(char.IsLetterOrDigit(c) || c == '_' || c == '-')))
                throw new ArgumentException("shot name: 1..64 letters/digits/_/-");
        }
        internal static void SaveGateShots(string prefix)
        {
            var p=Get(); p.RequirePaused();
            foreach(var name in new[]{"A","B","A2"})p.Save(name,prefix+"-"+name);
        }
        void Save(string name,string outputName=null)
        {
            ValidateName(name);
            outputName=outputName??name; ValidateName(outputName);
            if (!shots.TryGetValue(name, out var s)) throw new ArgumentException("missing shot");
            string dir = Path.Combine(Main.Entry.Path, "framecmp"); Directory.CreateDirectory(dir);
            var t = new Texture2D(s.Width, s.Height, TextureFormat.RGBA32, false, true);
            try { t.LoadRawTextureData(s.Pixels); t.Apply(false, false); File.WriteAllBytes(Path.Combine(dir, outputName + ".png"), t.EncodeToPNG()); }
            finally { Destroy(t); }
            Log("saved=" + Path.Combine(dir, outputName + ".png"));
        }
        internal static void Stop() => StopCore(false);
        internal static void Unload() => StopCore(true);
        static void StopCore(bool drain)
        {
            try
            {
                if (instance)
                {
                    instance.ReportResult();
                    instance.StopAllCoroutines(); instance.RestoreControl(); instance.RestoreCapture();
                    if (drain) instance.ReleasePending(true);
                    instance.shots.Clear(); instance.busy = false;
                    if (instance.hasPending) instance.retiring = true;
                    else { Destroy(instance.gameObject); instance = null; }
                }
            }
            finally
            {
                try { Resume(); }
                finally { resumedFrame = -1; resumedController = null; }
            }
        }
        void OnDestroy()
        {
            RestoreControl(); RestoreCapture(); ReleasePending(true);
            if (instance == this)
            {
                instance = null;
                try { Resume(); }
                finally { resumedFrame = -1; resumedController = null; }
            }
        }
    }
}
#endif
