using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using ADOFAI;
using DG.Tweening;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;

namespace StutterFix
{
    // The small entry point must stay independent of DecoBake's static storage.
    internal static class GpuDecoPlayer
    {
        internal static bool RuntimeInitialized { get; private set; }
        internal static bool Product { get; private set; }
        internal static bool Available => Product && RuntimeInitialized || Edition.Dev || Edition.AutoTest;
        internal static bool Active => Available && DecoBake.GpuLiveActive;
        internal static bool Preparing { get; private set; }
        static GpuDecoPlayerLifetime lifetime;
        static bool reported, orderReported, cleanupPending;
        static Harmony hooks;
        const string Owner = "StutterFix.GpuDeco.Player";

        internal static void Configure()
        {
            if (Main.Config.GpuDeco != 1 || Resilience.Off("GpuDeco")) { if (Product) Shutdown(); return; }
            if (Product) return;
            Product = true; lifetime.NewRound(); reported=false; GpuDecoGpu.AlphaEarlyOut = 1;
            try
            {
                if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11 || !SystemInfo.supportsComputeShaders)
                    throw new NotSupportedException("D3D11 컴퓨트 셰이더를 사용할 수 없음");
                // Resolve before applying patches. With the option off no method lookup,
                // event subscription, DecoBake initializer or GPU allocation occurs.
                var setup = AccessTools.Method(typeof(scrDecoration), "Setup", new[] { typeof(LevelEvent), typeof(bool).MakeByRefType(), typeof(bool) });
                var prep = AccessTools.Method(typeof(scnGame), "PrepVfx", new[] { typeof(List<scrFloor>), typeof(int), typeof(List<LevelEvent>), typeof(bool) });
                var update = AccessTools.Method(typeof(scrController), "Update");
                if (setup == null || prep == null || update == null) throw new MissingMethodException("재생 준비 후크 없음");
                RuntimeInitialized = true;
                DecoBake.EnablePlayer();
                hooks = new Harmony(Owner);
                hooks.Patch(setup, postfix: new HarmonyMethod(typeof(DecoBake), "SetupPostfix"));
                hooks.Patch(prep, postfix: new HarmonyMethod(typeof(GpuDecoPlayer), nameof(Prepared)) { priority = Priority.Last });
                hooks.Patch(update, prefix: new HarmonyMethod(typeof(GpuDecoPlayer), nameof(BeforeControllerUpdate)) { priority = Priority.First });
                var depth = AccessTools.Method(typeof(scrVisualDecoration), "SetDepth");
                if (depth != null) hooks.Patch(depth, postfix: new HarmonyMethod(typeof(GpuDecoPlayer), nameof(DepthChanged)));
                // These hooks own the product lifetime independently of other modules.
                foreach (var pair in new[] {
                    (typeof(scrController), "FailAction"), (typeof(scrController), "Fail2Action"),
                    (typeof(scrController), "OnLandOnPortal"), (typeof(scrController), "Restart"),
                    (typeof(scrController), "ResetCustomLevel"), (typeof(scrController), "QuitToMainMenu"),
                    (typeof(scnEditor), "SwitchToEditMode"), (typeof(scnEditor), "QuitToMenu"),
                    (typeof(scnEditor), "Play"), (typeof(scnGame), "ResetScene") })
                {
                    var method = AccessTools.Method(pair.Item1, pair.Item2);
                    if (method == null) throw new MissingMethodException(pair.Item1.Name + "." + pair.Item2);
                    hooks.Patch(method, prefix: new HarmonyMethod(typeof(GpuDecoPlayer), nameof(Ending)) { priority = Priority.First });
                }
                UnityEngine.SceneManagement.SceneManager.activeSceneChanged += SceneChanged;
            }
            catch (Exception e) { Skip(e.Message); Shutdown(); }
        }

        static void DepthChanged(scrVisualDecoration __instance)
        { if (Product) { DecoBake.GpuDepthChanged(__instance); DecoBake.GpuOwnedDepthChanged(__instance); } }
        static void SceneChanged(UnityEngine.SceneManagement.Scene oldScene, UnityEngine.SceneManagement.Scene newScene) => End("화면 전환");
        static void Ending(MethodBase __originalMethod) => End(__originalMethod.Name);

        static void Prepared(List<scrFloor> __0)
        {
            if (!Product || Main.Config.GpuDeco != 1) return;
            // A new PrepVfx is the sole retry boundary. Nothing may prepare during play.
            if (!lifetime.TryPrepare()) return;
            reported = orderReported = cleanupPending = false;
            Preparing = true;
            try
            {
                if (!FastMove.Installed || !FastMove.Enabled || !DecoAnim.Active || !ZeroTween.CanEase)
                    throw new InvalidOperationException("장식 이동 최적화가 꺼져 있음");
                // Quartz 최적화 모듈은 파티클·히트음·텍스처·카메라만 고친다(장식 setter 0곳, 2026-11-01 Arche 로그). 실제 겹침은 setter 패치 검사로 본다.
                if (DecoBake.ForeignSetter()) throw new InvalidOperationException("다른 모드의 장식 처리와 겹침");
                if (Screen.width <= 0 || Screen.height <= 0) throw new InvalidOperationException("화면 크기를 확인할 수 없음");
                var camera = GpuDecoScene.FindMain();
                if (!camera.orthographic || camera.stereoEnabled || camera.rect != new Rect(0, 0, 1, 1))
                    throw new InvalidOperationException("지원하지 않는 카메라 설정 (직교 "+camera.orthographic+", 렌더 텍스처 "+(camera.targetTexture?camera.targetTexture.name:"없음")+", VR "+camera.stereoEnabled+", 영역 "+camera.rect+")");
                if (UnityEngine.Object.FindObjectsByType<SortingGroup>(FindObjectsInactive.Include, FindObjectsSortMode.None).Any(g => g.isActiveAndEnabled))
                    throw new InvalidOperationException("정렬 그룹 사용 중");
                if (!DrawCollapsePolicy.PartialMaskAdmit && UnityEngine.Object.FindObjectsByType<SpriteMask>(FindObjectsInactive.Include, FindObjectsSortMode.None).Any(m => m.enabled && m.gameObject.activeInHierarchy))
                    throw new InvalidOperationException("스프라이트 마스크 사용 중");
#if AUTOTEST
                long heap0 = GC.GetTotalMemory(false) / 1048576;
#endif
                long heapBeforePrep = GC.GetTotalMemory(false) >> 20;
                DecoBake.PreparePlayer(__0);
#if AUTOTEST
                {
                    long heap1 = GC.GetTotalMemory(false) / 1048576;
                    Main.Entry.Logger.Log("[GPU 장식] 준비로 늘어난 관리 힙: " + heap0 + "MB -> " + heap1 + "MB (+" + (heap1 - heap0) + "MB) | 단계별: " + DecoBake.hpSb);
                }
#endif
                {
                    long grown = (GC.GetTotalMemory(false) >> 20) - heapBeforePrep;
                    lifetime.Ready();
                    GcControl.CleanAfterPrepare(grown);
                    DecoBake.EnsureNativeAdoptionHook();
                }
#if AUTOTEST
                Main.Entry.Logger.Log("[GPU 장식] 준비 완료 (막힘 " + lifetime.Blocked + ", 준비됨 " + lifetime.Prepared + ")");
#endif
            }
            catch (Exception e)
            {
#if AUTOTEST
                Main.Entry.Logger.Log("[GPU 장식] 준비 예외: " + e);
#endif
                Skip(e.Message); DecoBake.ReleasePlayerLevel(); cleanupPending=false;
            }
            finally { Preparing = false; }
        }

        static AccessTools.FieldRef<scrController, States> stateRef;
        static States CurrentState(scrController c)
        {
            if (stateRef == null) stateRef = AccessTools.FieldRefAccess<scrController, States>("currentState");
            return stateRef(c);
        }
        static void BeforeControllerUpdate() => TryStart();
        // 에디터 재생에서는 scrController.Update 앞 후크가 불리지 않아 Tick 에서도 같은 시도를 한다(한 번만 켜진다).
        static void TryStart()
        {
            if (!Product || lifetime.Blocked || !lifetime.Prepared) return;
            var c = ADOBase.controller; var song = scrConductor.instance;
            bool preStart = DrawCollapsePolicy.PreStartActivate && c && c.gameworld && !c.paused && !(scnEditor.instance) && CurrentState(c) == States.Start; // 시작 키 전에는 따라잡을 효과가 없다(InCatchUp 은 다시 하기 뒤 몇 초 켜져 있어 활성화가 키를 누른 뒤로 밀렸다)
#if AUTOTEST
            if (c && song && !song.hasSongStarted && lifetime.Prepared && !lifetime.Attempted && (Time.frameCount % 240) == 0)
                Main.Entry.Logger.Log("[GPU 장식] 시작 전 대기 점검: preStart " + preStart + " gameworld " + c.gameworld + " paused " + c.paused + " 상태 " + CurrentState(c) + " 따라잡기 " + EffectBudget.InCatchUp + " 에디터 " + (bool)scnEditor.instance + " 오디오정지 " + AudioListener.pause + " timeScale " + Time.timeScale);
#endif
            if (!c || !song || (!song.hasSongStarted && !preStart) || !c.gameworld || c.paused || AudioListener.pause || Time.timeScale <= 0) return;
            if (scnEditor.instance && !scnEditor.instance.playMode) return;
            // 시작 직후 60프레임(따라잡기)에는 효과가 원본 경로로 도는 순간이 있어, 그때 켜면 곧바로 원본으로 복귀한다(편집기 중간 시작).
            // 이어받기가 켜져 있으면 따라잡는 중에 만들어진 네이티브 트윈도 GPU 가 받으므로 기다리지 않는다(로딩으로 멈춘 첫 프레임 바로 뒤에 켜서, 곡 0.6초의 190ms 끊김을 로딩에 붙인다).
            if (song.songposition_minusi > 0.5 && (DrawCollapsePolicy.AdoptNativeTweens && DrawCollapsePolicy.ActivateInCatchUp ? EffectBudget.InReplay : EffectBudget.InCatchUp)) return;
            if (!lifetime.TryActivate()) return;
            // 첫 타일을 치기 전이면(현재 타일 0) 이 프레임의 켜기 비용은 곡 시작 처리로 본다.
            try { var fl = c.currFloor; if (fl != null && fl.seqID == 0) PerfOverlay.GraceForGpuDecoStart(); } catch { }
            try { DecoBake.StartPlayer(); }
            catch (DecoBake.GpuActivationDeferredException) { lifetime.Retry(); }
            catch (Exception e)
            {
#if AUTOTEST
                Main.Entry.Logger.Log("[GPU 장식] 켜기 예외: " + e);
#endif
                Skip(e.Message); DecoBake.ReleasePlayerLevel(); cleanupPending=false;
            }
        }

        internal static void Tick()
        {
            if (!Product || !RuntimeInitialized) return;
            if (cleanupPending) { cleanupPending=false; DecoBake.ReleasePlayerLevel(); }
            if (lifetime.Blocked) return;
            TryStart();
            if (lifetime.Blocked) return;
            if (lifetime.Attempted && !DecoBake.GpuLiveActive) { Skip("원본 장식 처리로 복귀함"); DecoBake.ReleasePlayerLevel(); return; }
            if (lifetime.Prepared) DecoBake.Tick();
        }
        internal static void Skip(string reason)
        {
            lifetime.Fail();
            cleanupPending = RuntimeInitialized;
            if (reported) return;
            reported = true;
            Main.Entry.Logger.Log("[GPU 장식] 건너뜀: " + reason.Replace('\r', ' ').Replace('\n', ' '));
        }
        internal static void OrderViolation(string reason)
        {
            if (orderReported) return;
            orderReported = true;
            Main.Entry.Logger.Log("[GPU 장식] 순서 점검: " + reason);
        }
        internal static void End(string reason)
        {
            if (!Product || !RuntimeInitialized) return;
#if AUTOTEST
            Main.Entry.Logger.Log("[GPU 장식] 종료 후크: " + reason + " | 준비됨 " + lifetime.Prepared + " 시도함 " + lifetime.Attempted + " 막힘 " + lifetime.Blocked + " 준비중 " + Preparing);
#endif
            // 죽음·완주 순간에는 되돌리지 않는다: 켜 둔 채 결과 화면을 그리고, 다시 하기·나가기·편집기 복귀 같은 전환(어차피 멈추는 순간)에서 되돌린다.
            // 마지막 타일·죽는 프레임의 약 40~55ms 복원 멈춤이 없어진다.
            if (DrawCollapsePolicy.DeferEndOnResult && DecoBake.GpuLiveActive && lifetime.Attempted
                && (reason == "FailAction" || reason == "Fail2Action" || reason == "OnLandOnPortal"))
            {
#if AUTOTEST
                Main.Entry.Logger.Log("[GPU 장식] 종료를 전환 때로 미룸: " + reason);
#endif
                return;
            }
            lifetime.NewRound();
            cleanupPending=false;
            // 맵을 벗어나거나 새로 불러올 때만 준비 결과를 버린다. 다시 하기·죽음·편집기 정지 등은 원본으로 되돌리기만 하고 GPU 준비를 남긴다.
            bool full = reason == "화면 전환" || reason == "ForgetLevel" || reason == "QuitToMainMenu" || reason == "QuitToMenu";
            try { if (full) DecoBake.ReleasePlayerLevel(); else DecoBake.EndRoundPlayerKeep(); }
            catch (Exception e) { Skip("종료 복원: " + e.Message); }
        }
        internal static void Shutdown()
        {
#if AUTOTEST
            DecoBake.ResetMapUploadExperiment();
#endif
            DrawCollapsePolicy.ImageArrayReuse=false;
            DrawCollapsePolicy.ImageArrayGpuOnly=false;
            if (!Product)
            {
#if AUTOTEST
                DecoBake.ResetSingleArrayExperiment();
#endif
                return;
            }
            UnityEngine.SceneManagement.SceneManager.activeSceneChanged -= SceneChanged;
            hooks?.UnpatchOwner(Owner); hooks = null;
            try { if (RuntimeInitialized) { DecoBake.ReleasePlayerLevel(); DecoBake.Shutdown(); } }
            finally
            {
#if AUTOTEST
                DecoBake.ResetSingleArrayExperiment();
#endif
                Product = false; GpuDecoGpu.AlphaEarlyOut = 0; lifetime.Fail(); cleanupPending = false;
            }
        }
    }

    internal static partial class DecoBake
    {
        // Prevent beforefieldinit from allocating tables at a guarded call site.
        static DecoBake() { }
        private static string TableFingerprint()
        {
            using (var hash = SHA256.Create())
            using (var sink = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write))
            using (var writer = new BinaryWriter(sink, Encoding.UTF8, true))
            {
                writer.Write(table.Pitch); writer.Write(table.TweenScale);
                for (int d = 0; d < table.Decorations.Length; d++)
                {
                    writer.Write(table.Decorations[d].Reasons.Count == 0);
                    foreach (float v in table.Decorations[d].Initial) writer.Write(v);
                    for (int c = 0; c < 16; c++)
                    {
                        var track = table.Tracks[d*16+c]; writer.Write(track.Length);
                        foreach (var s in track)
                        { writer.Write(s.T0); writer.Write(s.Duration); writer.Write(s.Start); writer.Write(s.End);
                          writer.Write(s.Target); writer.Write(s.Ease); writer.Write(s.Over); writer.Write(s.Period); }
                    }
                }
                writer.Flush(); sink.FlushFinalBlock();
                return BitConverter.ToString(hash.Hash).Replace("-", "");
            }
        }
        internal static bool ForeignSetter()
        {
            return new[] { typeof(scrDecoration), typeof(scrVisualDecoration), typeof(scrParallax), typeof(ffxMoveDecorationsPlus) }
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .Where(m => m.Name.StartsWith("Set", StringComparison.Ordinal) || m.Name == "Setup" || m.Name == "UpdatePosition" || m.Name == "StartEffect")
                .Select(Harmony.GetPatchInfo).Where(p => p != null)
                .Any(p => p.Owners.Any(o => !o.StartsWith("StutterFix", StringComparison.Ordinal)));
        }
        internal static bool CpuAdopting
        {
            get
            {
#if DEV || AUTOTEST
                return Adopting;
#else
                return false;
#endif
            }
        }
        internal static bool SkipGpuOrAdopt(ffxMoveDecorationsPlus fx,scrDecoration d,int keys)
        {
#if DEV || AUTOTEST
            return SkipAdopt(fx,d,keys);
#else
            return GpuLiveSkip(fx,d,keys);
#endif
        }
        internal static void EnablePlayer()
        {
            enabled = true;
            gpuImageCandidate = Main.Config.GpuDecoImages == 1;
            gpuImageOwn = true;
        }
        // 라운드 사이에 남겨 두는 GPU 준비 결과의 식별값과, 통합 구간 계획의 준비 시점 상태.
        static ulong gpuPreparedHash;
        static bool[] gpuCollapseIncludedPrepared;
        static bool gpuReusedRound;
        static readonly List<int> gpuFallbackExcludedItems=new List<int>();
        static readonly List<GpuLiveRow> gpuFallbackBarrierRows=new List<GpuLiveRow>();

        // 표·이벤트·이미지 예약의 내용 해시(검증용 SHA256 은 5초라 가벼운 FNV-1a). 같으면 GPU 준비 결과를 재사용할 수 있다.
        static ulong PlayerSetupHash()
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                void M(ulong v) { h ^= v; h *= 1099511628211UL; }
                void F(float v) => M((uint)BitConverter.SingleToInt32Bits(v));
                M((ulong)table.Decorations.Length); M((ulong)table.Supported); M((ulong)table.Events); M((ulong)table.Segments); F(table.Pitch); F(table.TweenScale);
                for (int d = 0; d < table.Decorations.Length; d++)
                {
                    var dec = table.Decorations[d];
                    M((ulong)dec.Id); M((ulong)dec.Reasons.Count);
                    foreach (float v in dec.Initial) F(v);
                    F(dec.OriginalX); F(dec.OriginalY); F(dec.GlobalX); F(dec.GlobalY); F(dec.ScaleUnitsX); F(dec.ScaleUnitsY);
                    for (int c = 0; c < 16; c++)
                    {
                        var track = table.Tracks[d * 16 + c]; M((ulong)track.Length);
                        for (int k = 0; k < track.Length; k++)
                        {
                            var sg = track[k];
                            M((ulong)BitConverter.DoubleToInt64Bits(sg.T0)); F(sg.Duration); F(sg.Start); F(sg.End); F(sg.Target); F(sg.Over); F(sg.Period);
                            M((ulong)sg.Ease); M((ulong)sg.Source); M(sg.Replacement ? 1UL : 0UL);
                        }
                    }
                }
                if (baselineEvents != null)
                    for (int i = 0; i < baselineEvents.Length; i++)
                    {
                        var e = baselineEvents[i];
                        M((ulong)BitConverter.DoubleToInt64Bits(e.T0)); F(e.Duration); M((ulong)e.Ease); M((ulong)e.Source);
                        M(e.RelativePosition ? 1UL : 0UL); M(e.ResetGlobalOrigin ? 1UL : 0UL);
                        if (e.Targets != null) { M((ulong)e.Targets.Length); foreach (int t in e.Targets) M((ulong)t); }
                        if (e.Values != null) foreach (float v in e.Values) F(v);
                    }
                if (baselineVisualFlags != null) for (int i = 0; i < baselineVisualFlags.Length; i++) M((ulong)baselineVisualFlags[i]);
                if (baselineImageSelectors != null)
                    for (int i = 0; i < baselineImageSelectors.Length; i++)
                    {
                        var sel = baselineImageSelectors[i];
                        if (sel == null) { M(0); continue; }
                        M((ulong)sel.Length); for (int k = 0; k < sel.Length; k++) M(sel[k]);
                    }
                return h == 0 ? 1UL : h;
            }
        }
        static bool SameBaselineObjects(IList<scrDecoration> a, IList<scrDecoration> b)
        {
            if (a == null || b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!a[i] || !b[i] || !ReferenceEquals(a[i], b[i])) return false;
            return true;
        }
        static bool playerPrepReuseEnabled = true, playerPrepKept;
        static scnGame playerPrepGame;
        static LevelData playerPrepLevel;
        static ulong playerPrepFingerprint;
#if AUTOTEST
        static bool playerPrepVerify;
#endif
        // Only a game-screen retry may retain a CPU snapshot. This is a cheap
        // change detector, not a deep hash of every mutable LevelEvent field.
        static ulong PlayerPrepFingerprint(List<scrFloor> floors)
        {
            var game = ADOBase.customLevel;
            var data = game ? game.levelData : null;
            if (scnEditor.instance || !game || data == null || floors == null
                || data.levelEvents == null || data.decorations == null || !ADOBase.controller
                || scrConductor.instance?.song == null || !scrVfxPlus.instance) return 0;
            unchecked
            {
                ulong h = 14695981039346656037UL;
                void M(ulong v) { h ^= v; h *= 1099511628211UL; }
                void F(float v) => M((uint)BitConverter.SingleToInt32Bits(v));
                void R(object v) => M(v == null ? 0UL : (uint)System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(v));
                void Events(List<LevelEvent> events)
                {
                    R(events); M((ulong)events.Count);
                    // At most 16 evenly spaced events, including both ends.
                    int n = Math.Min(16, events.Count);
                    for (int s = 0; s < n; s++)
                    {
                        var e = events[n == 1 ? 0 : (int)((long)s * (events.Count - 1) / (n - 1))];
                        R(e); if (e == null) continue;
                        M((uint)e.floor); M((uint)e.eventType); R(e.disabled);
                        M(e.disabled == null ? 0UL : (ulong)e.disabled.Count);
                        F(e.Get<float>("angleOffset")); F(e.Get<float>("duration"));
                    }
                }
                R(game); R(data); Events(data.levelEvents); Events(data.decorations);
                F(data.camZoom); F(ADOBase.controller.tileSize);
                M((uint)ADOBase.controller.visualQuality); M(ADOBase.isOfficialLevel ? 1UL : 0UL); M(ADOBase.levelIsMikoSkip ? 1UL : 0UL);
                F(scrConductor.instance.song.pitch); F(DOTween.timeScale);
                F(DOTween.defaultEaseOvershootOrAmplitude); F(DOTween.defaultEasePeriod);
                M((uint)Main.Config.GpuDecoImages); M((ulong)floors.Count);
                for (int i = 0; i < floors.Count; i++)
                {
                    var floor = floors[i];
                    if (!floor || floor.plusEffects == null) return 0;
                    R(floor); M((uint)floor.seqID); M((ulong)BitConverter.DoubleToInt64Bits(floor.entryTime));
                    M((ulong)floor.plusEffects.Count);
                }
                M((ulong)scrVfxPlus.instance.effects.Count);
                return h == 0 ? 1UL : h;
            }
        }
        static void ForgetPlayerPrepIdentity()
        {
            playerPrepKept = false; playerPrepFingerprint = 0;
            playerPrepGame = null; playerPrepLevel = null;
        }
        // Do not clear the retained GPU image plan here: the existing setup hash
        // still decides whether the freshly collected data can use GPU resources.
        static void ClearPlayerPrepSnapshot()
        {
            ForgetPlayerPrepIdentity();
            baselineObjects = null; baselineIds = null; baselineDecorations = null; baselineEvents = null;
            baselineVisualFlags = null; baselineImageSelectors = null;
            baselineSupported = -1; baselineSortedCount = 0;
            table = null; effectList = null; prepEffectSnapshot = null; prepFloors = null;
        }
        static bool TryReusePlayerPreparation(List<scrFloor> floors)
        {
            if (!playerPrepReuseEnabled || !playerPrepKept || scnEditor.instance || gpuRecoveryBlocked || !gpuLastRestoreComplete
                || status.StartsWith("중단:", StringComparison.Ordinal) || collector != null || job != null
                || !ReferenceEquals(playerPrepGame, ADOBase.customLevel) || !playerPrepGame
                || !ReferenceEquals(playerPrepLevel, playerPrepGame.levelData) || playerPrepFingerprint == 0
                || table == null || baselineIds == null || baselineDecorations == null || baselineEvents == null
                || baselineVisualFlags == null || baselineImageSelectors == null || prepEffectSnapshot == null
                || !manager || !ReferenceEquals(manager, scrDecorationManager.instance) || initial.Count == 0
                || !SameBaselineObjects(baselineObjects, manager.allDecorations)
                || !SameBaselineObjects(gpuLiveBaseline, baselineObjects)) return false;
            try
            {
                if (PlayerPrepFingerprint(floors) != playerPrepFingerprint) return false;
                ValidateBaseline();
                var effects = scrVfxPlus.instance.effects;
                if (effects.Count != prepEffectSnapshot.Length) return false;
                for (int i = 0; i < effects.Count; i++)
                    if (!effects[i] || !ReferenceEquals(effects[i], prepEffectSnapshot[i])) return false;
                // New generation/clock, same immutable table. Bind the current
                // live list; PrepVfx may have replaced its List wrapper.
                CancelBuild(true);
                effectList = effects; prepFloors = floors; ready = true;
                status = "준비됨 (수집·표 재사용)";
                playerPrepKept = false;
                return true;
            }
            catch { return false; } // An uncertain identity always takes full collection.
        }
#if AUTOTEST
        static ulong PlayerPrepInitialHash()
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                foreach (var d in table.Decorations) foreach (float v in d.Initial)
                    h = (h ^ (uint)BitConverter.SingleToInt32Bits(v)) * 1099511628211UL;
                return h;
            }
        }
        static bool VerifyPlayerPreparation(List<scrFloor> floors)
        {
            // Clear baseline too: Collect's baseline branch only recompiles the
            // old snapshot and would not independently verify collection.
            ulong expected = PlayerSetupHash(), expectedInitial = PlayerPrepInitialHash();
            var expectedObjects = baselineObjects;
            var saved = (Ids: baselineIds, Decorations: baselineDecorations, Events: baselineEvents,
                Flags: baselineVisualFlags, Selectors: baselineImageSelectors, Table: table,
                Schedule: prepEffectSnapshot, Sorted: baselineSortedCount, Supported: baselineSupported);
            int expectedCount = table.Decorations.Length;
            ClearPlayerPrepSnapshot();
            BeginPrepCollection(floors, true);
            while (collector != null) { if (!collector.MoveNext()) collector = null; }
            if (job != null) job.GetAwaiter().GetResult();
            Tick();
            RequireLive(ready && table != null, status);
            int differences = 0;
            if (PlayerSetupHash() != expected) differences++;
            if (!SameBaselineObjects(expectedObjects, baselineObjects)) differences++;
            if (table.Decorations.Length != expectedCount) differences++;
            if (PlayerPrepInitialHash() != expectedInitial) differences++;
            Main.Entry.Logger.Log("[GPU 장식] 준비 재사용 검증: " + (differences == 0 ? "같음" : "다름") + " " + differences);
            if (differences != 0)
            {
                playerPrepReuseEnabled = playerPrepVerify = false;
                // The current table is already the independently collected
                // fallback. Force rebuilding GPU resources from it as well.
                return false;
            }
            // Agreement verifies the retained path itself: continue with the
            // original table/arrays, not the throwaway verification result.
            baselineObjects = expectedObjects; baselineIds = saved.Ids;
            baselineDecorations = saved.Decorations; baselineEvents = saved.Events;
            baselineVisualFlags = saved.Flags; baselineImageSelectors = saved.Selectors;
            table = saved.Table; prepEffectSnapshot = saved.Schedule;
            baselineSortedCount = saved.Sorted; baselineSupported = saved.Supported;
            return true;
        }
#endif
#if AUTOTEST
        static long hpMark; internal static readonly System.Text.StringBuilder hpSb = new System.Text.StringBuilder();
        static void HpMark(string n){ long h = GC.GetTotalMemory(false) >> 20; hpSb.Append(n).Append(' ').Append(h - hpMark).Append("MB | "); hpMark = h; }
#else
        static void HpMark(string n){}
#endif
        internal static void PreparePlayer(List<scrFloor> floors)
        {
            long p0 = Stopwatch.GetTimestamp();
#if AUTOTEST
            hpSb.Clear(); hpMark = GC.GetTotalMemory(false) >> 20;
#endif
            // 파괴된 장식(맵을 다시 불러온 경우) 항목은 정리한다. 살아 있는 것은 처음 Setup 기록을 그대로 쓴다.
            int deadCount = 0;
            if (initial.Count > 0) { var dead = initial.Keys.Where(k => !k).ToArray(); deadCount = dead.Length; foreach (var k in dead) initial.Remove(k); }
            bool cacheCandidate = gpuLivePrepared && !gpuRecoveryBlocked && gpuLastRestoreComplete && gpuLiveSlices.Count > 0 && gpuScope != null && gpuScope.WholeSong
                && gpuPreparedHash != 0 && gpuLiveManager == manager && manager == scrDecorationManager.instance;
            if (!cacheCandidate) { if (gpuLivePrepared || gpuLiveSlices.Count > 0) DisposeGpuLive(); gpuScope = GpuDecoScopePlan.Song(); gpuPreparedHash = 0; }
#if AUTOTEST
            Main.Entry.Logger.Log("[GPU 장식] 메모리: 관리 힙 " + (GC.GetTotalMemory(false) >> 20) + "MB, 프로세스 개인 " + (System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 >> 20)
                + "MB, 유니티 할당 " + (UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() >> 20) + "MB, 그래픽 드라이버 " + (UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver() >> 20) + "MB");
            Main.Entry.Logger.Log("[GPU 장식] 준비 시작: 처음 Setup 기록 " + initial.Count + "개 (파괴된 것 " + deadCount + "개 정리), 장식 " + (scrDecorationManager.instance ? scrDecorationManager.instance.allDecorations.Count : -1) + "개, enabled " + enabled + ", 관리자 같음 " + (manager == scrDecorationManager.instance) + ", 준비 결과 재사용 후보 " + cacheCandidate);
#endif
            gpuImageCandidate = Main.Config.GpuDecoImages == 1;
            gpuImageOwn = true;
            GpuDecoNativeBatch.Enabled = true;
            GpuDecoCopy.Enabled = true; GpuDecoCopy.EightUavEnabled = true; GpuDecoCopy.MaskedEnabled = true; // 연구 계획의 nativecopy on8masked 와 같다
            gpuCollapsePrepare = true;
            bool prepReuse = cacheCandidate && TryReusePlayerPreparation(floors);
            if (!prepReuse)
            {
                ClearPlayerPrepSnapshot();
                BeginPrepCollection(floors, true);
                while (collector != null) { if (!collector.MoveNext()) collector = null; }
            }
            long p1 = Stopwatch.GetTimestamp();HpMark("수집");
            if (!prepReuse)
            {
                if (job != null) job.GetAwaiter().GetResult();
                Tick();
            }
#if AUTOTEST
            if (prepReuse && playerPrepVerify && !VerifyPlayerPreparation(floors))
            {
                prepReuse = cacheCandidate = false;
                DisposeGpuLive(); ClearGpuImagePlan(); gpuScope = GpuDecoScopePlan.Song(); gpuPreparedHash = 0;
            }
#endif
            HpMark("표 만들기");
            long p2 = Stopwatch.GetTimestamp();
            RequireLive(ready && table != null, status);
#if AUTOTEST
            {
                var total = new Dictionary<string, int>(); var only = new Dictionary<string, int>();
                foreach (var row in table.Decorations)
                {
                    foreach (var why in row.Reasons) { total.TryGetValue(why, out int n); total[why] = n + 1; }
                    if (row.Reasons.Count == 1) foreach (var why in row.Reasons) { only.TryGetValue(why, out int n); only[why] = n + 1; }
                }
                Main.Entry.Logger.Log("[GPU 장식] 표 거부 사유: 장식 " + table.Decorations.Length + "개, 지원 " + table.Supported + "개 | "
                    + string.Join(", ", total.OrderByDescending(kv => kv.Value).Select(kv => kv.Key + " " + kv.Value + "/단독 " + (only.TryGetValue(kv.Key, out int o) ? o : 0))));
            }
#endif
            RequireLive(table.Supported >= GpuDecoPlayerPolicy.MinimumOwned, "지원되는 장식이 적음 (64개 미만)");
            bool reuse = false;
            if (cacheCandidate)
            {
                reuse = prepReuse || (SameBaselineObjects(gpuLiveBaseline, baselineObjects) && PlayerSetupHash() == gpuPreparedHash);
                if (reuse) gpuLiveBaseline = baselineObjects; // 같은 객체·같은 순서라 새 배열로 바꿔 끼운다
                else { DisposeGpuLive(); ClearGpuImagePlan(); gpuScope = GpuDecoScopePlan.Song(); gpuPreparedHash = 0; }
            }
            if (!reuse && gpuImageCandidate) CompilePlayerImages(); // 재사용이면 남겨 둔 이미지 계획(같은 내용)을 그대로 쓴다
            long p3 = Stopwatch.GetTimestamp();HpMark("이미지계획");
            PrepareGpuLive();HpMark("GPU준비");   // 재사용이면 준비 결과가 그대로라 바로 돌아온다
            if(gpuWorkloadGate || Edition.AutoTest)
            {
                bool admit=CheckGpuWorkload(floors.Max(f=>f.entryTime));
                RequireLive(!gpuWorkloadGate || admit,"workload below measured beneficial calibration");
            }
            if (!reuse)
            {
                gpuPreparedHash = PlayerSetupHash();
                gpuCollapseIncludedPrepared = gpuCollapseIncludedSaved != null ? (bool[])gpuCollapseIncludedSaved.Clone() : (gpuCollapseIncluded != null ? (bool[])gpuCollapseIncluded.Clone() : null);
                if (gpuCollapseIncludedSaved == null) SaveCollapseBaseState();
                gpuFallbackExcludedItems.Clear(); gpuFallbackBarrierRows.Clear();
            }
            gpuReusedRound = reuse;
            // 구간 계획 재구성(약 200ms)과 전수 검증(약 35~150ms)은 곡 시작 순간의 장식 상태와 상관없는 장면 구조 계산이라 로딩 때 미리 해 둔다.
            // 곡 중간 시작·CPU 처리 장식·동적 제외 흔적이 있으면 StartPlayer 가 다시 한다. 미리 하다 실패해도 시작 때 하는 길로 간다.
            gpuPlanPreBuilt = false;
            if (gpuCollapseTexture != null && !gpuRecoveryBlocked)
            {
                gpuPrePlan = true;
                try
                {
                    if (reuse)
                    {
                        UndoCpuFallbackExclusion(); RestoreCollapseBaseState(); gpuCollapsePlannedFallback = 0;
                        foreach (var sl in gpuLiveSlices) foreach (var rr in sl.Renderers) if (rr && rr.enabled) rr.enabled = false;
                        RebuildDrawCollapse();
                    }
                    HpMark("재구성");ValidateDrawCollapse();HpMark("검증");
                    gpuPlanPreBuilt = true;
                }
                catch (Exception e)
                {
                    gpuPlanPreBuilt = false;
#if AUTOTEST
                    string st = e.StackTrace ?? "";
                    Main.Entry.Logger.Log("[GPU 장식] 계획 사전 구성 실패(시작 때 한다): " + e.GetType().Name + " " + e.Message + " @ " + st.Substring(0, Math.Min(200, st.Length)).Replace((char)10, ' ').Replace((char)13, ' '));
#endif
                }
                finally { gpuPrePlan = false; }
            }
            RequireLive(gpuLiveOwnedIndices.Length >= GpuDecoPlayerPolicy.MinimumOwned, "GPU 소유가 가능한 장식이 적음 (64개 미만)");
            // Remember only a successfully completed preparation. Release and
            // every full collection invalidate this identity before doing work.
            playerPrepGame = ADOBase.customLevel;
            playerPrepLevel = playerPrepGame ? playerPrepGame.levelData : null;
            try { playerPrepFingerprint = PlayerPrepFingerprint(floors); }
            catch { ForgetPlayerPrepIdentity(); }
            long p4 = Stopwatch.GetTimestamp();
            double f = 1000.0 / Stopwatch.Frequency;
            Main.Entry.Logger.Log("[GPU 장식] 준비 " + ((p4 - p0) * f).ToString("F0") + "ms" + (prepReuse ? " (수집·표 재사용)" : reuse ? " (이전 준비 결과 재사용)" : "") + ": 수집 " + ((p1 - p0) * f).ToString("F0") + ", 표 만들기 대기 " + ((p2 - p1) * f).ToString("F0")
                + ", 이미지 계획 " + ((p3 - p2) * f).ToString("F0") + ", GPU 준비 " + ((p4 - p3) * f).ToString("F0"));
        }
        // 정상 종료한 게임 화면만 CPU 준비 결과도 보존한다. 편집기는 매번 전체 수집한다.
        internal static void EndRoundPlayerKeep()
        {
            gpuPlanPreBuilt = false;
            if (!gpuLivePrepared || gpuRecoveryBlocked || !gpuLastRestoreComplete || gpuPreparedHash == 0
                || status.StartsWith("중단:", StringComparison.Ordinal)) { ReleasePlayerLevel(); return; }
            try { StopGpuLive("round_end"); }
            catch { ReleasePlayerLevel(); return; }
            if (!gpuLastRestoreComplete || gpuRecoveryBlocked) { ReleasePlayerLevel(); return; }
            CancelBuild();
            playerPrepKept = playerPrepReuseEnabled && !scnEditor.instance && playerPrepFingerprint != 0 && table != null;
            if (!playerPrepKept) ClearPlayerPrepSnapshot();
#if !DEV && !AUTOTEST
            DecoAnim.ClearGpuRecordMetadata();
#endif
        }
        // 통합 구간 계획을 준비 시점 상태로 되돌린다(이전 라운드의 CPU 처리 장식 제외를 취소).
        static bool gpuPlanPreBuilt;
        static void UndoCpuFallbackExclusion()
        {
            if (gpuCollapseIncluded != null) foreach (int i in gpuFallbackExcludedItems) if ((uint)i < (uint)gpuCollapseIncluded.Length) gpuCollapseIncluded[i] = true;
            if (gpuFallbackBarrierRows.Count > 0) gpuCollapseBarriers.RemoveAll(b => gpuFallbackBarrierRows.Contains(b));
            gpuFallbackExcludedItems.Clear(); gpuFallbackBarrierRows.Clear();
        }
        static void CompilePlayerImages()
        {
            ClearGpuImagePlan();
            RequireLive(baselineEvents != null && baselineVisualFlags != null && baselineImageSelectors != null, "이미지 예약 정보 없음");
            var input = new GpuDecoImageTimeline.Input[baselineEvents.Length];
            for (int i = 0; i < input.Length; i++)
            {
                var e = baselineEvents[i];
                input[i] = new GpuDecoImageTimeline.Input(i, e.Source, e.T0, baselineVisualFlags[i], baselineImageSelectors[i], e.Targets);
            }
            gpuImageTimeline = GpuDecoImageTimeline.Compile(baselineObjects.Length, input);
            gpuImageVariants = new GpuDecoImageVariant[gpuImageTimeline.SelectorCount];
            for (int i = 0; i < gpuImageVariants.Length; i++) gpuImageVariants[i] = GpuDecoImageVariant.Capture(gpuImageTimeline.Selector(i));
        }
        internal static void StartPlayer()
        {
            RequireLive(gpuLivePrepared && !gpuRecoveryBlocked, "곡 준비가 취소됨");
            long t0 = Stopwatch.GetTimestamp();
            StartGpuLive();
            long t1 = Stopwatch.GetTimestamp();ActMark("(StartGpuLive 끝)");
            // 그리기 통합 없이 소유만 쓰면 독립 렌더러 수만 개를 그려 오히려 훨씬 느리다(Arche 에디터 중간 시작: 101 -> 17 FPS). 통합을 못 켜면 원본을 유지한다.
            if (gpuCollapseTexture == null)
                throw new InvalidOperationException("그리기 통합을 쓸 수 없어 원본 유지");
            bool prebuilt = gpuPlanPreBuilt; gpuPlanPreBuilt = false;
            UndoCpuFallbackExclusion();ActMark("폴백해제");
            bool hadDynamic = gpuDynExcludedItems.Count > 0;
            // 준비 때 미리 만든 계획(다시 하기 판은 기준 상태 복원 + 재계획으로 만든다)을 그대로 쓸 수 있으면 포함 배열을 기준으로 되돌리지 않는다:
            // 그 계획이 동점으로 뺀 장식을 다시 넣으면 어느 구간에도 없고 독립 렌더러도 꺼진 채라 보이지 않는다.
            if (prebuilt && !hadDynamic && gpuLiveCpuFallbackCount == 0) ClearCollapseDynamicState();
            else RestoreCollapseBaseState();
            gpuCollapsePlannedFallback = 0;
            if (gpuLiveCpuFallbackCount != 0) ExcludeCpuFallbackFromCollapse();
            var songNow = scrConductor.instance;
            bool midSong = songNow != null && songNow.hasSongStarted && songNow.songposition_minusi > 0.5; // 시작 키 전(Start 상태)의 곡 위치는 지난 판의 값이라 믿지 않는다
            // 재사용 라운드·CPU 처리 장식·동적 제외 흔적·곡 중간 시작(장벽 상태가 달라짐)이면 통합을 다시 계획한다
            // 형태 칸이 준비 뒤에 도착했으면 기록을 올리고 다시 계획한다(켜는 시점은 멈춘 시작 화면)
            ActMark("기준복원");
            bool shapeRebuild = ApplyShapesIfReady();
            ActMark("형태칸");
            bool otherRebuild = (gpuReusedRound && !prebuilt) || gpuLiveCpuFallbackCount != 0 || hadDynamic || shapeRebuild;
            bool reuseTry = DrawCollapsePolicy.MidReusePlan && midSong && prebuilt && !otherRebuild;
            bool needRebuild = otherRebuild || (midSong && !reuseTry);
            if (needRebuild)
            {
                // StartGpuLive 가 독립 렌더러(GpuDeco.Live run)를 켜 둔 채로 계획하면 그것들이 "켜진 장벽"으로 잡혀 동점 장식이 수천 개로 불어난다
                // (에디터에서 같은 맵을 여러 번 재생하면 독립 렌더러 1,500개, FPS 143). 첫 라운드는 계획이 준비 때 렌더러가 꺼진 채 이뤄진다.
                foreach (var sl in gpuLiveSlices) foreach (var rr in sl.Renderers) if (rr && rr.enabled) rr.enabled = false;
                ActMark("독립렌더러끄기");
                RebuildDrawCollapse();
            }
            ActMark("계획(재구성)");
            {
                if (reuseTry)
                {
                    try { ValidateDrawCollapse(); ActMark("전수검증(계획 재사용)"); }
                    catch (Exception e)
                    {
                        // 중간 시작 상태가 준비 때 계획과 어긋남: 순수 기준 상태를 복원하고 다시 계획한다.
                        Main.Entry.Logger.Log("[GPU 장식] 중간 시작: 준비 때 계획이 맞지 않아 다시 계획함 (" + e.GetType().Name + ")");
                        RestoreCollapseBaseState();
                        foreach (var sl in gpuLiveSlices) foreach (var rr in sl.Renderers) if (rr && rr.enabled) rr.enabled = false;
                        RebuildDrawCollapse();ActMark("계획(재구성)");
                        ValidateDrawCollapse();ActMark("전수검증");
                    }
                }
                else
                {
                    if (needRebuild || !prebuilt) ValidateDrawCollapse();
                    ActMark(needRebuild || !prebuilt ? "전수검증" : "전수검증(사전 완료)");
                }
                gpuCollapseOn = true;
                foreach (var slice in gpuLiveSlices) slice.RefreshAlpha();ActMark("알파갱신");
                DispatchGpuLiveSlice(BakeTime);ActMark("통합디스패치");
            }
            ActEnd();
            gpuActivationDeferred = 0;
            long t2 = Stopwatch.GetTimestamp();
            Main.Entry.Logger.Log("[GPU 장식] 켜짐: 소유 " + GpuLiveOwned + "개, 그리기 통합 " + (gpuCollapseOn ? "켬" : "끔") + ", 이미지 " + (gpuImageCandidate ? "켬" : "끔") + ", CPU 처리 장식 " + gpuLiveCpuFallbackCount + "개"
                + " | 시작 " + ((t1 - t0) * 1000.0 / Stopwatch.Frequency).ToString("F1") + "ms, 통합 켜기 " + ((t2 - t1) * 1000.0 / Stopwatch.Frequency).ToString("F1") + "ms");
        }
        internal static void ReleasePlayerLevel()
        {
#if AUTOTEST
            if (gpuLiveActive && gpuCostFrames > 0) LogGpuLiveCost("product_release");
#endif
            ForgetPlayerPrepIdentity();
            gpuPlanPreBuilt = false; DisposeGpuLive(); gpuPreparedHash = 0; gpuCollapseIncludedPrepared = null; gpuFallbackExcludedItems.Clear(); gpuFallbackBarrierRows.Clear();
            CancelBuild(); ClearBaseline(); table = null; // initial(처음 Setup 기록)은 남긴다: 다시 하기는 장식을 새로 Setup 하지 않을 수 있다
            effectList = null; prepEffectSnapshot = null; prepFloors = null;
            gpuTrimBounds.Clear(); gpuImageBlock.Clear(); ForgetShapes();
#if !DEV && !AUTOTEST
            DecoAnim.ClearGpuRecordMetadata();
#endif
        }

        static int playerOwnedCursor;
        static void CheckPlayerFrame()
        {
            if (!GpuDecoPlayer.Product || (Time.frameCount & 3) != 0) return;
            RequireLive(manager && manager==scrDecorationManager.instance && gpuLiveCamera && gpuLiveCamera.orthographic
                && gpuLiveCamera.rect==new Rect(0,0,1,1)
                && Mathf.Abs(gpuLiveCamera.transform.forward.z)>.999999f
                && ADOBase.controller && ADOBase.controller.camy, "카메라 또는 장식 관리자 변경");
            RequireLive(FastMove.Enabled && DecoAnim.Active, "장식 이동 처리 중단");
            RequireLive(GpuDecoCopy.ReadTotals().Errors==0, "GPU 버퍼 복사 오류");
            var slice=gpuLiveSlices[0];
            for(int n=0;n<Math.Min(4,gpuLiveOwnedIndices.Length);n++)
            {
                int master=playerOwnedCursor++%gpuLiveOwnedIndices.Length;
                if(GpuCpuFallback(master))continue;
                var original=gpuLiveOwnedRenderers[master];
                int item=gpuLiveMasterToSliceItem[master][0];
                var sprite=original?original.sprite:null;
                var texture=sprite?sprite.texture:null;
                RequireLive(original && (texture || G1ExpectedEmpty(master)) && item>=0 && slice.Renderers[item], "소유 장식 또는 GPU 렌더러 소실");
                RequireLive(original.enabled==(DrawCollapsePolicy.DisableOwnedRenderers?false:!G1DrawHidden(master))
                    && original.gameObject.activeInHierarchy && original.forceRenderingOff, "다른 처리에서 소유 렌더러 상태 변경");
                RequireLive(original.maskInteraction==SpriteMaskInteraction.None && !original.GetComponentInParent<SortingGroup>(), "소유 장식 마스크 또는 정렬 그룹 변경");
                var material=original.sharedMaterial;
                var shader=material?material.shader:null;
                RequireLive(material && shader && ((shader.name=="Sprites/Tile" && material.passCount==1) || GpuDecoBlend.ShaderFits(shader,slice.Data.Items[item].BlendMode))
                    // 혼합 장식은 에셋이 재질을 새로 만들거나 FastBlend 가 더하기 재질로 바꿔 끼울 수 있다: 모드가 그대로면 같은 그림이다
                    && (material.GetInstanceID()==slice.Data.Items[item].MaterialId || (slice.Data.Items[item].BlendMode>0 && GpuDecoBlend.Id(original.GetComponentInParent<scrVisualDecoration>())==slice.Data.Items[item].BlendMode)), "소유 장식 재질 변경: " + original.name + " shader=" + (shader?shader.name:"null") + " item_blend=" + slice.Data.Items[item].BlendMode + " now_blend=" + GpuDecoBlend.Id(original.GetComponentInParent<scrVisualDecoration>()) + " mat_same=" + (material && material.GetInstanceID()==slice.Data.Items[item].MaterialId));
                if(!GpuImageActor(gpuLiveOwnedIndices[master]))
                    RequireLive(texture.GetInstanceID()==slice.Data.Items[item].TextureId, "준비하지 않은 텍스처 변경");
            }
            if(gpuLiveOwnedIndices.Length>0)playerOwnedCursor%=gpuLiveOwnedIndices.Length;
        }
    }

    internal static class GpuPlaybackState
    {
        internal static bool Paused
        {
            get
            {
#if DEV || AUTOTEST
                if (!GpuDecoPlayer.Product) return FrameCompare.Paused;
#endif
                return ADOBase.controller && ADOBase.controller.paused;
            }
        }
        internal static bool PlaybackContinuity
        {
            get
            {
#if DEV || AUTOTEST
                if (!GpuDecoPlayer.Product) return FrameCompare.PlaybackContinuity;
#endif
                return scrConductor.instance && scrConductor.instance.hasSongStarted;
            }
        }
        internal static bool PreservesBakeTweenScale(float scale)
        {
#if DEV || AUTOTEST
            if (!GpuDecoPlayer.Product) return FrameCompare.PreservesBakeTweenScale(scale);
#endif
            return Paused && DOTween.timeScale == 0;
        }
    }
}
