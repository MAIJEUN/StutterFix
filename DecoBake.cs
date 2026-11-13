#if DEV || AUTOTEST || GPUDECO
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DG.Tweening;
using ADOFAI;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // Shadow remains read-only. Explicit development-only adopt owns supported targets.
    internal static partial class DecoBake
    {
        private const string Owner = "StutterFix.DecoBake.Shadow";
        private const int MaxDecorations = 100000, MaxEvents = 300000, MaxTargetRefs = 4000000;
        private static Harmony patches;
        private static bool enabled;
        private static int generation;
        private static readonly DecoBakeCore.SongClock clock = new DecoBakeCore.SongClock();
        private static int clockFrame = -1;
        private static List<scrFloor> prepFloors;
        internal static double BakeTime => clock.Time;
        // Runs at the existing DecoAnim hook, before its empty-record early return.
        internal static void AdvanceClock()
        {
            if (!enabled || clockFrame == Time.frameCount) return;
            clockFrame = Time.frameCount;
            var c = scrConductor.instance;
            float dt = (DOTween.useSmoothDeltaTime ? Time.smoothDeltaTime : Time.deltaTime) * DOTween.timeScale;
            clock.Step(c == null ? double.NaN : c.songposition_minusi, dt,
                c?.song == null ? 1 : c.song.pitch, DOTween.timeScale,
                c != null && c.hasSongStarted && ADOBase.controller != null && !ADOBase.controller.paused);
        }
        private static readonly Dictionary<scrDecoration, DecoBakeCore.Decoration> initial = new Dictionary<scrDecoration, DecoBakeCore.Decoration>();
        private static scrDecoration[] baselineObjects;
        private static int[] baselineIds;
        private static DecoBakeCore.Decoration[] baselineDecorations;
        private static DecoBakeCore.Event[] baselineEvents;
#if AUTOTEST || GPUDECO
        // Snapshot metadata for explicit boundary census only; never admission.
        private static int[] baselineVisualFlags;
        private static string[] baselineImageSelectors;
#endif
        private static int baselineSortedCount, baselineSupported = -1;
        private static void ClearBaseline()
        {
            ForgetPlayerPrepIdentity();
            baselineObjects = null; baselineIds = null; baselineDecorations = null; baselineEvents = null;
#if AUTOTEST || GPUDECO
            baselineVisualFlags = null;
            baselineImageSelectors = null; ClearGpuImagePlan();
#endif
            baselineSupported = -1; baselineSortedCount = 0;
        }
        private static void ValidateBaseline()
        {
            if (!DecoBakeCore.SameInstances(manager.allDecorations, baselineObjects, baselineIds, d => d.GetInstanceID()))
                throw new InvalidOperationException("기준 스냅샷 재사용 거부: 장식 개수/순서/인스턴스 ID/객체 변경; 맵 다시 open 필요");
        }
#if DEV || AUTOTEST
        internal struct StartStamp { internal int Source, Frame; internal double Song; }
#endif
        private static IEnumerator collector;
        private static Task<DecoBakeCore.Table> job;
        private static CancellationTokenSource cancellation;
        private static DecoBakeCore.Table table;
        private static bool ready;
        private static scrDecorationManager manager;
        private static List<ffxPlusBase> effectList;
        private static ffxPlusBase[] prepEffectSnapshot;
        private static string status = "꺼짐";
        private static double snapshotMs, snapshotMaxMs, workerWallMs;
        private static long heapBefore, heapDelta;
        private static Stopwatch wall;
        private static float frozenPitch, frozenScale;
        private static float frozenOver, frozenPeriod;
        private static double publishedSnapshotMs, publishedMaxMs;
        private static double setupMs, setupFrameMs, setupFrameMaxMs;
        private static int setupFrame = -1;
        private static int publishedSortedCount;
        private static readonly string[] StageNames = { "장식복사", "효과읽기", "태그열거", "조건효과", "worker전달" };
        private static readonly double[] stageMs = new double[5];
        private static readonly double[] stageSlice = new double[5], stageMaxMs = new double[5];
        private static double tagMoveNextMaxMs;
        private static double publishedSetupMs, publishedSetupMaxMs, publishedTagMaxMs;
        private static readonly double[] publishedStages = new double[5], publishedStageMax = new double[5];
        private static int stage;
        private static long stageBegin;
        private static void Stage(int value) { long now = Stopwatch.GetTimestamp();
            if (stageBegin != 0) { double ms = (now - stageBegin) * 1000.0 / Stopwatch.Frequency; stageMs[stage] += ms; stageSlice[stage] += ms; }
            stage = value; stageBegin = now; }
        private static IEnumerable<scrDecoration> TimedTargets(ffxMoveDecorationsPlus f)
        {
            using (var iterator = f.decManager.GetTaggedDecorations((IEnumerable<string>)f.targetTags).GetEnumerator())
                while (true) { long begin = Stopwatch.GetTimestamp(); bool more = iterator.MoveNext();
                    tagMoveNextMaxMs = Math.Max(tagMoveNextMaxMs, (Stopwatch.GetTimestamp() - begin) * 1000.0 / Stopwatch.Frequency);
                    if (!more) yield break; yield return iterator.Current; }
        }
        internal static int Generation => ready ? generation : -1;
        internal static DecoBakeCore.Table CaptureTable => ready ? table : null;
        internal static bool Enabled => enabled;
        private static void Log(string s) { if (GpuDecoPlayer.Product) return; Main.Entry.Logger.Log("[장식굽기/그림자] " + s); }
        private static string N(double x) => x.ToString("G9", CultureInfo.InvariantCulture);
        private static MethodInfo Exact(Type type, string name, bool isStatic, params Type[] parameters)
        {
            var found = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == name && !m.IsGenericMethod && m.IsStatic == isStatic && m.ReturnType == typeof(void)
                    && m.CallingConvention == (isStatic ? CallingConventions.Standard : CallingConventions.Standard | CallingConventions.HasThis)
                    && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameters)).ToArray();
            if (found.Length != 1) throw new InvalidOperationException(type.Name + "." + name + " 후보=" + found.Length);
            return found[0];
        }
#if DEV || AUTOTEST
        internal static void Command(string arg)
        {
            try
            {
                string command = (arg ?? "").Trim();
                if (command.StartsWith("adopt ", StringComparison.OrdinalIgnoreCase)) { AdoptCommand(command.Substring(6).ToLowerInvariant()); return; }
                if (command.StartsWith("adoptsig ", StringComparison.OrdinalIgnoreCase)) { AdoptSignature(command.Substring(9)); return; }
                switch ((arg ?? "").Trim().ToLowerInvariant())
                {
                    case "on":
                        if (enabled) { Log(status); return; }
#if DEV || AUTOTEST
                        if (Hitch.Playing || DecoSim.Capturing) { Log("거부: 곡/캡처 종료 후 on, 그 다음 맵 다시 open"); return; }
#endif
                        if (!ZeroTween.CanEase || !FastMove.Installed) { Log("거부: 기존 이징/FastMove 시작 관찰 후크 필요"); return; }
                        // Resolve ALL hooks before installing any. No metadata-token resolution on Mono.
                        var setup = Exact(typeof(scrDecoration), "Setup", false, typeof(LevelEvent), typeof(bool).MakeByRefType(), typeof(bool));
                        var prep = Exact(typeof(scnGame), "PrepVfx", true, typeof(List<scrFloor>), typeof(int), typeof(List<LevelEvent>), typeof(bool));
                        patches = new Harmony(Owner);
                        try
                        {
                            patches.Patch(setup, postfix: new HarmonyMethod(typeof(DecoBake), nameof(SetupPostfix)));
                            patches.Patch(prep, postfix: new HarmonyMethod(typeof(DecoBake), nameof(PrepPostfix)));
                        }
                        catch { patches.UnpatchOwner(Owner); patches = null; throw; }
                        enabled = true; status = "켜짐: 새 맵 open 대기"; Log(status); return;
                    case "off": Shutdown(); Log(status); return;
                    case "state": Log("ready=" + ready + " generation=" + generation + " collector=" + (collector != null) + " worker=" + (job != null) + " status=" + status); return;
                    case "verify": Verify(); return;
                    case "bench": Bench(); return;
                    default: Log("사용: decobake on (맵 open 전) | off | verify | bench | adopt on|off|state | adoptsig 이름 [초] [표시Hz]; " + status); return;
                }
            }
            catch (Exception e) { Log("실패: " + e.GetType().Name + " " + e.Message); }
        }
        internal static bool Step(string arg)
        {
            if ((arg ?? "").Trim().ToLowerInvariant() != "ready") { Command(arg); return true; }
            CheckGpuPrepareBudget();
            if (!enabled || status.StartsWith("중단:")) throw new InvalidOperationException("decobake ready: " + status);
            return ready;
        }
#endif
        private static void CancelBuild(bool preserveAdopt = false)
        {
            InvalidateGpuSnapshot();
            if(GpuLiveActive)RecoverGpuLive("snapshot_generation_change",false);
#if DEV || AUTOTEST
            bool resume = preserveAdopt && (Adopting || resumeAdopt);
            StopAdopt("스냅샷 세대 변경");
            resumeAdopt = resume;
            StopSignature();
#if DEV || AUTOTEST
            DecoSim.InvalidateBake("스냅샷 세대 변경: 재준비");
#endif
#endif
            generation++; ready = false; collector = null; clock.Reset(); clockFrame = -1;
            ResetGpuFrames();
            cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
            // Observe late faults even when an old generation is discarded. Worker snapshots contain no Unity references.
            if (job != null) job.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            job = null;
        }
        internal static void Shutdown()
        {
#if DEV || AUTOTEST
            gpuPacketMode=0;
#endif
            gpuPrepareWatch = null;
            DisposeGpuLive();
#if AUTOTEST
            ResetSingleArrayExperiment();
#endif
            if(nativeUpdateHooked)SetNativeAdoption(false);
#if AUTOTEST || GPUDECO
            gpuImageCandidate=false;gpuImageUploadFault=false;gpuImageMatrix=false;
#endif
#if DEV || AUTOTEST
            gpuRenderMergePrepare=false;
#endif
            enabled = false; CancelBuild(); initial.Clear(); g1Initial.Clear(); ClearBaseline(); manager = null; effectList = null; prepEffectSnapshot = null; prepFloors = null;
#if DEV || AUTOTEST
            DecoSim.ForgetBake();
#endif
            table = null;
            patches?.UnpatchOwner(Owner); patches = null; status = "꺼짐";
        }
        // 제품 경로: 다시 하기는 장식을 새로 Setup 하지 않을 수 있으므로(가벼운 초기화) 처음 Setup 기록(initial)과 관리자는 남긴다.
        // 맵을 새로 불러오면 새 관리자의 Setup 이 기록을 비우고 다시 채운다(SetupPostfix).
        internal static void ForgetLevelKeepInitial()
        {
            if (!enabled) return;
            DisposeGpuLive();
            CancelBuild(); ClearBaseline(); effectList = null; prepEffectSnapshot = null; prepFloors = null; status = "새 라운드 대기";
        }
        internal static void ForgetLevel()
        {
            if (!enabled) return;
            DisposeGpuLive();
            CancelBuild(); initial.Clear(); g1Initial.Clear(); ClearBaseline(); manager = null; effectList = null; prepEffectSnapshot = null; prepFloors = null; status = "새 맵 스냅샷 대기";
            setupMs = setupFrameMs = setupFrameMaxMs = 0; setupFrame = -1;
        }
        private static void SetupPostfix(scrDecoration __instance)
        {
            if (!enabled) return;
            long begin = Stopwatch.GetTimestamp();
            try
            {
                if (ready || collector != null || job != null) CancelBuild(manager == __instance.manager);
                var d = __instance;
                if (manager != d.manager) { initial.Clear(); g1Initial.Clear(); ClearBaseline(); manager = d.manager; setupMs = setupFrameMs = setupFrameMaxMs = 0; setupFrame = -1; }
                // Setup can run again during seek. Never replace pre-scrub values with animated values.
                if (initial.ContainsKey(d)) return;
                if (playerPrepKept) ClearPlayerPrepSnapshot(); // New objects must be captured normally before the next PreparePlayer.
                if (baselineObjects != null) throw new InvalidOperationException("기준 스냅샷 재사용 거부: 새 장식 객체 Setup; 맵 다시 open 필요");
                if (initial.Count >= MaxDecorations && !initial.ContainsKey(d)) throw new InvalidOperationException("장식 상한");
                var row = new DecoBakeCore.Decoration { Id = d.GetInstanceID(), OriginalX = d.startPos.x, OriginalY = d.startPos.y };
                if (d.sourceLevelEvent != null && d.sourceLevelEvent.TryGet<Vector2>("position", out var sourcePosition))
                {
                    // Same multiply/order as SetPlacementType(Global), without calling a setter.
                    var global = sourcePosition * ADOBase.controller.tileSize;
                    row.GlobalX = global.x; row.GlobalY = global.y;
                }
                else row.Reasons.Add("Global 기준 원본 위치 없음");
                var c = d.color; var p = d.parallax == null ? Vector2.zero : d.parallax.multiplier;
                row.Initial = new[] { d.pivotPosVec.x, d.pivotPosVec.y, d.pivotOffsetVec.x, d.pivotOffsetVec.y, d.rotAngle,
                    d.scaleVec.x, d.scaleVec.y, c.r, c.g, c.b, c.a, d.opacity, p.x, p.y, d.parallaxOffset.x, d.parallaxOffset.y };
                if (d is scrVisualDecoration visual && visual.spriteRenderer != null && visual.spriteRenderer.sprite != null)
                {
                    var size = visual.spriteRenderer.sprite.bounds.size;
                    var multiplier = d.trueScaleMultiplier;
                    row.ScaleUnitsX = Math.Abs(size.x * multiplier.x); row.ScaleUnitsY = Math.Abs(size.y * multiplier.y);
                }
                if (!(d is scrVisualDecoration)) row.Reasons.Add("이미지 이외 장식");
                if (d.useHitbox || d.hitboxEvents.Count != 0) row.Reasons.Add("히트박스/충돌");
                if (d.sourceLevelEvent != null && d.sourceLevelEvent.TryGet<MaskingType>("maskingType", out var mask) && mask != MaskingType.None) row.Reasons.Add("초기 마스크");
                if (d.parallax == null) row.Reasons.Add("시차 부품 없음");
                var placementReason = DecoBakeCore.PlacementReason(d.placementType.ToString(), d.stickToFloor);
                if (placementReason != null) row.Reasons.Add(placementReason);
                if (d.stickToFloor) row.Reasons.Add("바닥 부착");
                if (d.followPlanet != null) row.Reasons.Add("행성 추종");
                if (row.Initial.Any(v => !DecoBakeCore.Finite(v))) row.Reasons.Add("초기값 비유한");
                if (!DecoBakeCore.Finite(row.OriginalX) || !DecoBakeCore.Finite(row.OriginalY)
                    || !DecoBakeCore.Finite(row.GlobalX) || !DecoBakeCore.Finite(row.GlobalY)) row.Reasons.Add("초기값 비유한");
                initial[d] = row;
                CaptureG1Initial(d);
            }
            catch (Exception e) { Fail("초기값 수집 " + e.Message); }
            finally
            {
                double ms = (Stopwatch.GetTimestamp() - begin) * 1000.0 / Stopwatch.Frequency;
                if (setupFrame != Time.frameCount) { setupFrame = Time.frameCount; setupFrameMs = 0; }
                setupFrameMs += ms; setupMs += ms; setupFrameMaxMs = Math.Max(setupFrameMaxMs, setupFrameMs);
            }
        }
        // Existing FastMove prefix calls this before deciding which implementation runs.
        internal static void ObserveStart(ffxMoveDecorationsPlus fx)
        {
            if (!enabled) return;
            try
            {
#if DEV || AUTOTEST
                AdoptStart(fx);
#if DEV || AUTOTEST
                if (DecoSim.Capturing) DecoSim.RecordBakeStart(new StartStamp { Source = fx.GetInstanceID(), Frame = Time.frameCount, Song = scrConductor.instance.songposition_minusi });
#endif
#endif
            }
            catch (Exception e) { Fail("시작 관찰 " + e.Message); }
        }
        private static void PrepPostfix(List<scrFloor> __0)
            => BeginPrepCollection(__0, true);
        private static void BeginPrepCollection(List<scrFloor> floors, bool newSchedule)
        {
            if (!enabled) return;
            ForgetPlayerPrepIdentity();
            try
            {
                CancelBuild(true);
                if (manager == null || manager != scrDecorationManager.instance || initial.Count == 0) throw new InvalidOperationException("open 전 on 필요: Setup 초기값 없음");
                if (ADOBase.isOfficialLevel || ADOBase.levelIsMikoSkip) throw new InvalidOperationException("커스텀 일반 맵만 지원");
                if (ADOBase.controller.visualQuality != VisualQuality.High) throw new InvalidOperationException("High 시각 설정만 지원");
                if (newSchedule)
                {
                    effectList = scrVfxPlus.instance.effects;
                    prepEffectSnapshot = DecoBakeCore.CaptureSchedule(effectList, MaxEvents);
                    Log("PrepVfx 예약 순서 보존=" + prepEffectSnapshot.Length + " generation=" + generation);
                }
                else if (prepEffectSnapshot == null || effectList != scrVfxPlus.instance.effects)
                    throw new InvalidOperationException("전체 PrepVfx 예약 스냅샷 없음/목록 교체; 맵 다시 open 필요");
                wall = Stopwatch.StartNew(); snapshotMs = setupMs; snapshotMaxMs = setupFrameMaxMs; heapBefore = GC.GetTotalMemory(false);
                Array.Clear(stageMs, 0, stageMs.Length); stage = 0; stageBegin = 0;
                Array.Clear(stageMaxMs, 0, stageMaxMs.Length); tagMoveNextMaxMs = 0;
                frozenPitch = scrConductor.instance.song.pitch; frozenScale = DOTween.timeScale;
                frozenOver = DOTween.defaultEaseOvershootOrAmplitude; frozenPeriod = DOTween.defaultEasePeriod;
                status = "메인 스냅샷 수집 중";
                prepFloors = floors;
                collector = Collect(floors, generation, prepEffectSnapshot);
                if (baselineObjects != null) { ValidateBaseline(); Log("기준 스냅샷 재사용 장식=" + baselineObjects.Length
                    + " 전체예약효과=" + baselineSortedCount + " 현재예약효과=" + effectList.Count + " 최초지원=" + baselineSupported); }
            }
            catch (Exception e) { Fail("PrepVfx " + e.Message); }
        }
        private static bool Conditional(ffxPlusBase fx) => fx.runManually || fx.runOnHit || (fx.conditionalInfo != null && fx.conditionalInfo.Any(x => x));
        private static string[] Reasons(ffxMoveDecorationsPlus f)
        {
            var r = new List<string>();
            if (Conditional(f)) r.Add("조건/수동/타격");
            if (f.disableIfMinFx || f.disableIfMaxFx) r.Add("시각 효과 설정 조건");
            if (f.imageFilenameUsed)
#if AUTOTEST || GPUDECO
                if(!gpuImageCandidate)
#endif
                r.Add("이미지 변경");
            var movementReason = DecoBakeCore.MovementReason(f.movementType.ToString(), f.movementTypeUsed);
            if (movementReason != null) r.Add(movementReason);
            if (!Enum.IsDefined(typeof(Ease), f.ease) || f.ease == Ease.INTERNAL_Custom || f.ease == Ease.Unset) r.Add("미확인/사용자 이징");
            if (!DecoBakeCore.Finite(f.duration) || f.duration < 0) r.Add("길이 비유한/음수");
            return r.ToArray();
        }
        private static DecoBakeCore.Event CopyEvent(ffxMoveDecorationsPlus f)
        {
            var e = new DecoBakeCore.Event { T0 = DecoBakeCore.ScheduledTime(f.startTime, f.startEffectOffset), Duration = f.duration,
                StartTime = f.startTime, StartEffectOffset = f.startEffectOffset, AngleOffset = f.degreeOffset,
                Source = f.GetInstanceID(), Ease = (int)f.ease, RelativePosition = f.movementType == DecPlacementType.LastPosition,
                ResetGlobalOrigin = f.movementTypeUsed && f.movementType == DecPlacementType.Global, Reasons = Reasons(f) };
            // Same parameters as DecoAnim.EaseParams -> Tween.SetEase, without touching ZeroTween's shared proxy.
            e.Over = frozenOver;
            if (f.ease == Ease.Flash || f.ease == Ease.InFlash || f.ease == Ease.OutFlash || f.ease == Ease.InOutFlash) e.Over = (int)e.Over;
            e.Period = frozenPeriod;
            if (!f.forceDontTweenMovement)
            {
                if (f.positionUsed) { e.Values[0] = f.targetPos.x; e.Values[1] = f.targetPos.y; }
                if (f.pivotUsed) { e.Values[2] = f.targetPivot.x; e.Values[3] = f.targetPivot.y; }
                if (f.rotationUsed) e.Values[4] = f.targetRot;
                DecoBakeCore.CopyScale(e, f.scaleUsed, f.targetScale, f.targetScaleV2.x, f.targetScaleV2.y);
                if (f.parallaxOffsetUsed) { e.Values[14] = f.targetParallaxOffset.x; e.Values[15] = f.targetParallaxOffset.y; }
            }
            if (f.colorUsed) { e.Values[7] = f.targetColor.r; e.Values[8] = f.targetColor.g; e.Values[9] = f.targetColor.b; e.Values[10] = f.targetColor.a; }
            if (f.opacityUsed) e.Values[11] = f.targetOpacity;
            if (f.parallaxUsed) { e.Values[12] = f.targetParallax.x / 100f; e.Values[13] = f.targetParallax.y / 100f; }
            if (e.Values.Any(x => float.IsInfinity(x))) e.Reasons = e.Reasons.Concat(new[] { "목표값 비유한" }).ToArray();
            return e;
        }
        private static bool UnknownWriter(ffxPlusBase fx)
        {
            if (fx == null || fx is ffxMoveDecorationsPlus) return false;
            string name = fx.GetType().Name;
            return name.IndexOf("Decora", StringComparison.OrdinalIgnoreCase) >= 0 || name == "ffxCallMethod" || name == "ffxAddComponent";
        }
        private static IEnumerator Collect(List<scrFloor> floors, int epoch, ffxPlusBase[] schedule)
        {
            if (baselineObjects != null)
            {
                ValidateBaseline();
                var snapshot = baselineDecorations;
                var savedEvents = baselineEvents; float savedPitch = frozenPitch, savedScale = frozenScale;
                cancellation = new CancellationTokenSource(); var savedToken = cancellation.Token;
                job = Task.Run(() => DecoBakeCore.Compile(DecoBakeCore.CloneDecorations(snapshot), savedEvents, savedPitch, savedScale, EvaluateEase, savedToken));
                status = "기준 스냅샷 재굽기 중";
                yield break;
            }
            var all = manager.allDecorations;
            int count = all.Count, effects = schedule.Length, refs = 0;
            if (count > MaxDecorations) throw new InvalidOperationException("장식 상한");
            var decs = new DecoBakeCore.Decoration[count];
            var ids = new Dictionary<scrDecoration, int>();
            var ordered = new HashSet<ffxPlusBase>();
            var events = new List<DecoBakeCore.Event>();
            g1EventGuards.Clear(); g1DepthSets.Clear();
            var curVisible = new Dictionary<scrDecoration, bool>();
            foreach(var kv in g1Initial) curVisible[kv.Key] = kv.Value.Visible;
            var imageWrites = new HashSet<scrDecoration>();
            foreach(var f in schedule.OfType<ffxMoveDecorationsPlus>())
                if(f.imageFilenameUsed)
                    foreach(var d in TimedTargets(f)) imageWrites.Add(d);
#if AUTOTEST || GPUDECO
            var visualFlags = new List<int>();
            var imageSelectors = new List<string>();
#endif
            float pitch = frozenPitch, tweenScale = frozenScale;
            bool foreignWriter = typeof(scrDecoration).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.StartsWith("Set", StringComparison.Ordinal) || m.Name == "Setup")
                .Select(Harmony.GetPatchInfo).Where(info => info != null).Any(info => info.Owners.Any(o => !o.StartsWith("StutterFix", StringComparison.Ordinal)));
            var slice = Stopwatch.StartNew();
            for (int i = 0; i < count; i++)
            {
                var d = DecoBakeCore.SnapshotItem(all, i, count, epoch, generation);
                if (d == null || ids.ContainsKey(d)) throw new InvalidOperationException("null/중복 장식");
                ids.Add(d, i);
                if (!initial.TryGetValue(d, out var source)) throw new InvalidOperationException("초기 Setup 누락");
                var row = new DecoBakeCore.Decoration { Id = source.Id, Initial = (float[])source.Initial.Clone(), OriginalX = source.OriginalX, OriginalY = source.OriginalY,
                    GlobalX = source.GlobalX, GlobalY = source.GlobalY,
                    ScaleUnitsX = source.ScaleUnitsX, ScaleUnitsY = source.ScaleUnitsY };
                foreach (var reason in source.Reasons) row.Reasons.Add(reason);
                if (foreignWriter) row.Reasons.Add("외부 모드 장식 setter 패치");
                if (d.parallax != null && d.parallax.clampToScreen && !DrawCollapsePolicy.ScreenFixed) row.Reasons.Add("시차 화면 고정");
                if (d.hitboxEvents.Count != 0 || d.hasConditionalChange) row.Reasons.Add("히트박스/조건 연결");
                if (d is scrVisualDecoration v && v.spriteRenderer != null && v.spriteRenderer.maskInteraction != SpriteMaskInteraction.None) row.Reasons.Add("마스크 대상");
                decs[i] = row;
                if (slice.Elapsed.TotalMilliseconds >= 4) { yield return null; slice.Restart(); }
            }
            // Copy the game's final sorted sequence. Repeat-expanded objects remain distinct Source IDs.
            Stage(1);
            for (int i = 0; i < effects; i++)
            {
                var fx = DecoBakeCore.SnapshotItem(schedule, i, effects, epoch, generation); ordered.Add(fx);
                if (fx is ffxMoveDecorationsPlus f)
                {
                    CaptureG1EventGuard(f);
                    var e = CopyEvent(f); var targets = new List<int>();
#if AUTOTEST || GPUDECO
                    int visualSnapshot=(f.imageFilenameUsed?1:0)|(f.originalSizeUsed?2:0)|(f.smoothingUsed?4:0)|(f.cropPivotOffsetUsed?8:0);
                    string imageSnapshot=FastMove.GpuImageSelector(f);
#endif
                    Stage(2);
                    foreach (var d in TimedTargets(f))
                    {
                        if (!ids.TryGetValue(d, out int id)) throw new InvalidOperationException("대상/초기값 불일치");
                        targets.Add(id); if (++refs > MaxTargetRefs) throw new InvalidOperationException("대상 참조 상한");
                        bool visNow = curVisible.TryGetValue(d, out bool cv) && cv;
                        if (f.visibleUsed) { visNow = f.visible; curVisible[d] = f.visible; }
                        AddG1ValueReasons(f,d,decs[id],visNow,imageWrites.Contains(d));
                        if (slice.Elapsed.TotalMilliseconds >= 4) { yield return null; slice.Restart(); }
                    }
                    e.Targets = targets.ToArray(); events.Add(e);
#if AUTOTEST || GPUDECO
                    visualFlags.Add(visualSnapshot);
                    imageSelectors.Add(imageSnapshot);
#endif
                    Stage(1);
                }
                else if (UnknownWriter(fx))
                    foreach (var d in decs) { d.Reasons.Add("다른 장식 효과/태그 변경 가능"); if (slice.Elapsed.TotalMilliseconds >= 4) { yield return null; slice.Restart(); } }
                if (slice.Elapsed.TotalMilliseconds >= 4) { yield return null; slice.Restart(); }
            }
            // Conditional/manual effects are omitted by PrepVfx. Include their possible targets in exclusion counts.
            Stage(3);
            foreach (var floor in floors.ToArray())
            foreach (var fx in floor.plusEffects.ToArray())
            {
                if (fx is ffxMoveDecorationsPlus f && !ordered.Contains(fx))
                {
                    foreach (var d in TimedTargets(f))
                    {
                        if (!ids.TryGetValue(d, out int id)) throw new InvalidOperationException("조건 대상 불일치");
                        decs[id].Reasons.Add("예약 목록 밖/조건/수동/타격");
                        if (slice.Elapsed.TotalMilliseconds >= 4) { yield return null; slice.Restart(); }
                    }
                }
                else if (UnknownWriter(fx))
                    foreach (var d in decs) { d.Reasons.Add("다른 장식 효과/태그 변경 가능"); if (slice.Elapsed.TotalMilliseconds >= 4) { yield return null; slice.Restart(); } }
                if (slice.Elapsed.TotalMilliseconds >= 4) { yield return null; slice.Restart(); }
            }
            if (epoch != generation || manager.allDecorations.Count != count || effectList != scrVfxPlus.instance.effects) throw new DecoBakeCore.SnapshotChangedException();
            Stage(4);
            cancellation = new CancellationTokenSource(); var token = cancellation.Token;
            var copied = events.ToArray();
            // Freeze the COMPLETE first timeline, before Compile can add computed reasons.
            baselineObjects = all.ToArray(); baselineIds = decs.Select(d => d.Id).ToArray();
            baselineDecorations = decs; baselineEvents = copied; baselineSortedCount = effects;
#if AUTOTEST || GPUDECO
            baselineVisualFlags = visualFlags.ToArray();
            baselineImageSelectors = imageSelectors.ToArray();
#endif
            job = Task.Run(() => DecoBakeCore.Compile(DecoBakeCore.CloneDecorations(decs), copied, pitch, tweenScale, EvaluateEase, token));
            status = "작업 스레드 굽기 중";
        }
        private static float EvaluateEase(int id, float t, float dur, float over, float period) => ZeroTween.Eval((Ease)id, t, dur, over, period);
        internal static void Tick()
        {
            if (!enabled) return;
            try
            {
                CheckGpuPrepareBudget();
                if (ready && manager != scrDecorationManager.instance) { Fail("기준 스냅샷 재사용 거부: 장식 manager 변경; 맵 다시 open 필요"); return; }
                if (ready && effectList != scrVfxPlus.instance?.effects) { Reprepare(); return; }
                if ((ready || collector != null || job != null) && (scrConductor.instance?.song == null
                    || scrConductor.instance.song.pitch != frozenPitch
                    || (DOTween.timeScale != frozenScale && !GpuPlaybackState.PreservesBakeTweenScale(frozenScale))
                    || DOTween.defaultEaseOvershootOrAmplitude != frozenOver || DOTween.defaultEasePeriod != frozenPeriod))
                {
                    Fail("수집 후 pitch/DOTween 배율/이징 기본값 변경: 고정 계약에서 제외"
                        + " pitch=" + (scrConductor.instance?.song == null ? "missing" : scrConductor.instance.song.pitch.ToString("R",CultureInfo.InvariantCulture)) + "/" + frozenPitch.ToString("R",CultureInfo.InvariantCulture)
                        + " tween=" + DOTween.timeScale.ToString("R",CultureInfo.InvariantCulture) + "/" + frozenScale.ToString("R",CultureInfo.InvariantCulture)
                        + " overshoot=" + DOTween.defaultEaseOvershootOrAmplitude.ToString("R",CultureInfo.InvariantCulture) + "/" + frozenOver.ToString("R",CultureInfo.InvariantCulture)
                        + " period=" + DOTween.defaultEasePeriod.ToString("R",CultureInfo.InvariantCulture) + "/" + frozenPeriod.ToString("R",CultureInfo.InvariantCulture)
                        + " pause_intact=" + GpuPlaybackState.PreservesBakeTweenScale(frozenScale)
                        + " audio_paused=" + AudioListener.pause + " time_scale=" + Time.timeScale.ToString("R",CultureInfo.InvariantCulture)
                        + " song_started=" + (scrConductor.instance && scrConductor.instance.hasSongStarted)); return;
                }
                if (collector != null)
                {
                    Array.Clear(stageSlice, 0, stageSlice.Length);
                    var sw = Stopwatch.StartNew(); stageBegin = Stopwatch.GetTimestamp(); bool more = collector.MoveNext();
                    Stage(stage); stageBegin = 0;
                    for (int i = 0; i < stageSlice.Length; i++) stageMaxMs[i] = Math.Max(stageMaxMs[i], stageSlice[i]);
                    double ms = sw.Elapsed.TotalMilliseconds; snapshotMs += ms;
                    snapshotMaxMs = Math.Max(snapshotMaxMs, ms + (setupFrame == Time.frameCount ? setupFrameMs : 0));
                    if (!more) collector = null;
                }
                if (job != null && job.IsCompleted)
                {
                    var completed = job; job = null; table = completed.GetAwaiter().GetResult();
                    ValidateBaseline();
                    if (baselineSupported >= 0 && table.Supported != baselineSupported)
                        throw new InvalidOperationException("기준 재굽기 지원 수 불일치: 최초=" + baselineSupported + " 현재=" + table.Supported);
                    baselineSupported = table.Supported;
                    ready = true;
                    heapDelta = GC.GetTotalMemory(false) - heapBefore; workerWallMs = wall.Elapsed.TotalMilliseconds;
                    publishedSnapshotMs = snapshotMs; publishedMaxMs = snapshotMaxMs;
                    publishedSetupMs = setupMs; publishedSetupMaxMs = setupFrameMaxMs; publishedTagMaxMs = tagMoveNextMaxMs;
                    Array.Copy(stageMs, publishedStages, stageMs.Length); Array.Copy(stageMaxMs, publishedStageMax, stageMaxMs.Length);
                    publishedSortedCount = baselineSortedCount;
                    status = "준비됨 (그림자 기본; adopt 별도)";
                    Log(status + " 장식=" + table.Decorations.Length + " 지원=" + table.Supported + " 이벤트=" + table.Events + " 구간=" + table.Segments);
                }
#if DEV || AUTOTEST
                if (ready && resumeAdopt && Hitch.Playing && !EffectBudget.InCatchUp)
                {
                    resumeAdopt = false;
                    try { AdoptCommand("on"); }
                    catch (Exception e) { Log("adopt 재채택 거부(그림자 유지): " + e.Message); }
                }
#endif
            }
            catch (DecoBakeCore.SnapshotChangedException) { Reprepare(); }
            catch (ArgumentOutOfRangeException) { Reprepare(); }
            catch (Exception e) { Fail(e.GetType().Name + " " + e.Message); }
        }
        private static void Reprepare()
        {
            if (GpuDecoPlayer.Product) { GpuDecoPlayer.Skip("재생 준비 뒤 효과 목록이 바뀜"); ReleasePlayerLevel(); return; }
#if DEV || AUTOTEST
            DecoSim.InvalidateBake("seek 중 스냅샷 변경: 재준비");
#endif
            CancelBuild(true); status = "재준비: 목록/세대 변경"; Log(status);
            if (prepFloors != null) BeginPrepCollection(prepFloors, false);
        }
        private static void Fail(string why) {
#if DEV || AUTOTEST
            DecoSim.InvalidateBake(why);
#endif
            CancelBuild(); status = "중단: " + why; Log(status); if (GpuDecoPlayer.Product) GpuDecoPlayer.Skip(why); }
#if DEV || AUTOTEST
        private static void Bench()
        {
#if DEV || AUTOTEST
            if (Hitch.Playing || DecoSim.Capturing) { Log("거부: 곡/캡처 종료 후 bench"); return; }
#endif
            if (table == null) { Log(status); return; }
            double checksum = 0; var timings = new List<double>();
            // All supported decorations and all 16 channels, at varying song times; no Unity writes.
            for (int pass = 0; pass < 33; pass++)
            {
                var sw = Stopwatch.StartNew();
                for (int d = 0; d < table.Decorations.Length; d++) if (table.Decorations[d].Reasons.Count == 0)
                    for (int c = 0; c < DecoBakeCore.Channels; c++) checksum += table.Eval(d, c, pass * 2.125);
                if (pass != 0) timings.Add(sw.Elapsed.TotalMilliseconds);
            }
            timings.Sort();
            Log("bench 실수집 장식=" + table.Decorations.Length + "/맵참조28828 이동효과인스턴스=" + table.Events
                + " 전체예약효과=" + publishedSortedCount + " 원본맵이벤트참조95211(집계단계다름) 구간=" + table.Segments
                + " worker굽기ms=" + N(table.CompileMs) + " 수집CPUms=" + N(publishedSnapshotMs) + " 최대메인sliceMs=" + N(publishedMaxMs)
                + " Prep후완료wallMs=" + N(workerWallMs) + " 표payloadMiB=" + N(table.PayloadBytes / 1048576.0)
                + " 프로세스heap변화MiB=" + N(heapDelta / 1048576.0) + " eval중앙ms=" + N(timings[16]) + " evalp99ms=" + N(timings[31]) + " checksum=" + N(checksum));
            Log("메모리 payload는 객체 header/임시 list/스냅샷 제외; heap변화는 다른 게임 할당 포함. Arche 실수집 수와 지원율을 함께 판단.");
            Log("수집단계CPUms Setup=" + N(publishedSetupMs) + " Setup동일프레임최대ms=" + N(publishedSetupMaxMs)
                + " " + string.Join(" ", StageNames.Select((name, i) => name + "=" + N(publishedStages[i])))
                + " 단계최대sliceMs=" + string.Join(" ", StageNames.Select((name, i) => name + "=" + N(publishedStageMax[i])))
                + " 태그단일MoveNext최대ms=" + N(publishedTagMaxMs)
                + "; Collect 협력예산4ms, Setup postfix는 게임의 동기 호출이므로 yield 불가; GC/단일 API 호출은 예산을 넘길 수 있음");
            Log("evalAll 정의=지원 장식 전체*16채널 이진탐색, t=pass*2.125초; 프레임 활성 비용 아님. payload: 구간="
                + table.Segments + "*48=" + table.Segments * 48L + "B, 트랙참조=" + table.Tracks.Length * (long)IntPtr.Size
                + "B, 장식추정=" + table.Decorations.Length * (DecoBakeCore.Channels * 4L + 32) + "B; header/진단 InputEvents 별도");
            long targetRefs = 0; foreach (var e in table.InputEvents) targetRefs += e.Targets.Length;
            Log("추가보유 진단 InputEvents=" + table.InputEvents.Length + " targetRefs=" + targetRefs
                + " 목표배열payloadB=" + table.InputEvents.Length * (DecoBakeCore.Channels * 4L) + " 대상배열payloadB=" + targetRefs * 4L
                + " 참조배열payloadB=" + table.InputEvents.Length * (long)IntPtr.Size
                + " 트랙비어있지않음=" + table.Tracks.Count(x => x.Length != 0)
                + "; 진단 snapshot 객체/배열header/Reasons 및 scheduler 구축 메모리는 표payload 밖. evalActive scheduler는 bench 지역 객체");
#if DEV || AUTOTEST
            var tape = DecoSim.BakeTape;
#endif
            if (tape?.BakeTable != table || !tape.Valid || tape.Frames.Count < 2 || tape.BakeInvalidReason.Length != 0) { Log("evalActive 관측 불가: 같은 표의 유효한 완료 테이프 필요"); return; }
            var active = new DecoBakeCore.ActiveEvaluator(table);
            active.Step(tape.Frames[0].BakeTime); // Warm seek excluded.
            var activeTimes = new List<double>(); int evaluated = 0, dispatched = 0, resets = 0;
            double resetMs = 0;
            for (int i = 1; i < tape.Frames.Count; i++)
            {
                double time = tape.Frames[i].BakeTime;
                if (!DecoBakeCore.Finite(time)) continue;
                if (time < tape.Frames[i - 1].BakeTime || Math.Abs(time - tape.Frames[i - 1].BakeTime) > .05)
                { var reset = Stopwatch.StartNew(); active.Step(time); resetMs += reset.Elapsed.TotalMilliseconds; resets++; continue; }
                var sw = Stopwatch.StartNew(); checksum += active.Step(time);
                activeTimes.Add(sw.Elapsed.TotalMilliseconds); evaluated += active.Evaluated; dispatched += active.Dispatched;
            }
            if (activeTimes.Count == 0) { Log("evalActive 관측 불가: 단조 구간 없음"); return; }
            activeTimes.Sort();
            Log("evalActive 프레임n=" + activeTimes.Count + " 중앙ms=" + N(activeTimes[activeTimes.Count / 2])
                + " p99ms=" + N(activeTimes[(int)Math.Ceiling(activeTimes.Count * .99) - 1])
                + " 채널평균=" + N((double)evaluated / activeTimes.Count) + " 예약dispatch=" + dispatched
                + " checksum=" + N(checksum) + " 재동기n=" + resets + " 재동기ms=" + N(resetMs) + "; tb 단조 구간, 커서+시각순 시작 dispatch+활성 채널+완료값1회, 초기구축/seek 제외, Unity setter/렌더 제외; 목표<=0.5ms");
        }
        private sealed class Errors
        {
            internal readonly List<double> Values = new List<double>();
            internal readonly List<double> ReplacementValues = new List<double>();
            internal readonly Dictionary<int, double> Top = new Dictionary<int, double>();
            internal double Max, ReplacementMax;
            internal int ReplacementN;
            internal void Add(double value, bool replacement, int decoration = -1) {
                Values.Add(value); Max = Math.Max(Max, value);
                if (decoration >= 0 && (!Top.TryGetValue(decoration, out double old) || value > old)) Top[decoration] = value;
                if (replacement) { ReplacementN++; ReplacementMax = Math.Max(ReplacementMax, value); ReplacementValues.Add(value); }
            }
            internal double P99() { Values.Sort(); return Values.Count == 0 ? double.NaN : Values[(int)Math.Ceiling(Values.Count * .99) - 1]; }
            internal double Rms() => Values.Count == 0 ? double.NaN : Math.Sqrt(Values.Sum(x => x * x) / Values.Count);
            internal double ReplacementP99() { ReplacementValues.Sort(); return ReplacementValues.Count == 0 ? double.NaN : ReplacementValues[(int)Math.Ceiling(ReplacementValues.Count * .99) - 1]; }
        }
        private static readonly string[] Names = { "위치X", "위치Y", "피벗X", "피벗Y", "회전", "크기X", "크기Y", "색R", "색G", "색B", "색A", "불투명도", "시차X", "시차Y", "시차오프셋X", "시차오프셋Y" };
        private static int Channel(int key) { switch (key) { case 1: return 0; case 2: return 1; case 3: return 2; case 4: return 3; case 5: return 4; case 7: return 5; case 8: return 6; case 9: return 7; case 10: return 11; case 11: return 12; case 12: return 14; case 13: return 15; default: return -1; } }
        private static void Verify()
        {
#if DEV || AUTOTEST
            if (DecoSim.Capturing) { Log("거부: 테이프 캡처 완료 후 verify"); return; }
#endif
#if DEV || AUTOTEST
            var tape = DecoSim.BakeTape;
#endif
            var table = tape?.BakeTable;
            if (table == null || tape == null || !tape.Valid || tape.Frames.Count == 0 || tape.BakeGeneration < 0)
            { Log("거부: 같은 굽기 세대의 유효한 완료 테이프 필요; " + status); return; }
            if (tape.BakeInvalidReason.Length != 0) { Log("거부: 캡처 중 굽기 계약 무효화: " + tape.BakeInvalidReason); return; }
            var byId = new Dictionary<int, int>(); for (int i = 0; i < table.Decorations.Length; i++) byId.Add(table.Decorations[i].Id, i);
            var stats = Enumerable.Range(0, DecoBakeCore.Channels).Select(_ => new Errors()).ToArray();
            var times = Enumerable.Range(0, DecoBakeCore.Channels).Select(_ => new Errors()).ToArray();
            var timeFrames = Enumerable.Range(0, DecoBakeCore.Channels).Select(_ => new Errors()).ToArray();
            var timeNA = new int[DecoBakeCore.Channels];
            var scaleRelative = new[] { new Errors(), new Errors() }; var scalePixels = new[] { new Errors(), new Errors() };
            var scaleZero = new int[2]; var scaleNoPixels = new int[2];
            var top = new Dictionary<int, double>(); var pixels = new Errors(); int skipped = 0, badClock = 0, nonfinite = 0;
            foreach (var frame in tape.Frames)
            {
                if (!DecoBakeCore.Finite(frame.BakeTime) || frame.Pitch != table.Pitch || frame.TweenScale != table.TweenScale)
                { badClock++; continue; }
                for (int i = frame.SampleBegin; i < frame.SampleEnd; i++)
                {
                    var s = tape.Samples[i]; int c = Channel(s.Key);
                    if (c < 0 || !byId.TryGetValue(tape.DecorationIds[s.Decoration], out int d) || table.Decorations[d].Reasons.Count != 0) { skipped++; continue; }
                    int components = s.Key == 9 ? 4 : s.Key == 11 ? 2 : 1;
                    for (int v = 0; v < components; v++)
                    {
                        float actual = v == 0 ? s.X : v == 1 ? s.Y : v == 2 ? s.Z : s.W;
                        if (s.Key == 8) actual = s.Y;
                        double error = Math.Abs((double)table.Eval(d, c + v, frame.BakeTime) - actual);
                        if (!DecoBakeCore.Finite(error)) { nonfinite++; continue; }
                        stats[c + v].Add(error, table.Replaced[d], d);
                        double speed = DecoBakeCore.Speed(table, d, c + v, frame.BakeTime);
                        if (DecoBakeCore.Finite(speed))
                        {
                            double seconds = error / speed;
                            times[c + v].Add(seconds * 1000, false);
                            double frameSeconds = frame.TweenScale > 0 ? frame.Dt * frame.Pitch / frame.TweenScale : 0;
                            if (frameSeconds > 0) timeFrames[c + v].Add(seconds / frameSeconds, false);
                        }
                        else timeNA[c + v]++;
                        if (c == DecoBakeCore.ScaleX || c == DecoBakeCore.ScaleY)
                        {
                            int axis = c - DecoBakeCore.ScaleX;
                            if (actual == 0) scaleZero[axis]++;
                            else scaleRelative[axis].Add(error / Math.Abs((double)actual), table.Replaced[d], d);
                            float units = axis == 0 ? table.Decorations[d].ScaleUnitsX : table.Decorations[d].ScaleUnitsY;
                            if (DecoBakeCore.Finite(units) && units > 0 && DecoBakeCore.Finite(frame.PixelsPerWorld) && frame.PixelsPerWorld > 0)
                                scalePixels[axis].Add(error * units * frame.PixelsPerWorld, table.Replaced[d], d);
                            else scaleNoPixels[axis]++;
                        }
                        if (!top.TryGetValue(d, out double old) || error > old) top[d] = error;
                        if (c < 2 && DecoBakeCore.Finite(frame.PixelsPerWorld) && frame.PixelsPerWorld > 0) pixels.Add(error * frame.PixelsPerWorld, table.Replaced[d]);
                    }
                }
            }
            Log("verify 표본=" + (tape.Moving ? "지원 활성 Rec, Key별 최초32슬롯" : "인덱스%4==0") + " Rec.Step 출력; 완료 이벤트의 중간 Kill 값은 최종 프레임과 혼합하지 않음. 제외표본=" + skipped + " 시계변경제외프레임=" + badClock + " 비유한=" + nonfinite);
            for (int c = 0; c < stats.Length; c++)
            {
                Log("시간환산 tb " + Names[c] + " n=" + times[c].Values.Count + " 최대ms=" + (times[c].Values.Count == 0 ? "NA" : N(times[c].Max))
                    + " p99ms=" + N(times[c].P99()) + " RMSms=" + N(times[c].Rms()) + " NA=" + timeNA[c]
                    + " 프레임n=" + timeFrames[c].Values.Count + " 최대프레임=" + (timeFrames[c].Values.Count == 0 ? "NA" : N(timeFrames[c].Max))
                    + " p99프레임=" + N(timeFrames[c].P99()) + " RMS프레임=" + N(timeFrames[c].Rms()) + "; 제안 p99<=2프레임, 곡초 속도 기반 국소 환산");
                Log(Names[c] + " n=" + stats[c].Values.Count + (stats[c].Values.Count == 0 ? " 관측 불가(활성 지원 Rec 없음)" : "") + " 최대절대=" + (stats[c].Values.Count == 0 ? "NA" : N(stats[c].Max)) + " p99=" + N(stats[c].P99())
                    + " 교체장식n=" + stats[c].ReplacementN + " 교체최대=" + (stats[c].ReplacementN == 0 ? "NA" : N(stats[c].ReplacementMax)) + " 교체p99=" + N(stats[c].ReplacementP99())
                    + " 상위장식8=" + string.Join(";", stats[c].Top.OrderByDescending(x => x.Value).Take(8).Select(x => table.Decorations[x.Key].Id + ":" + N(x.Value))));
            }
            for (int axis = 0; axis < 2; axis++)
                Log("크기" + (axis == 0 ? "X" : "Y") + " 상대오차(|차|/|실제|) n=" + scaleRelative[axis].Values.Count
                    + " 최대=" + (scaleRelative[axis].Values.Count == 0 ? "NA" : N(scaleRelative[axis].Max)) + " p99=" + N(scaleRelative[axis].P99())
                    + " 실제0으로상대NA=" + scaleZero[axis] + " 교체n=" + scaleRelative[axis].ReplacementN + " 교체최대=" + (scaleRelative[axis].ReplacementN == 0 ? "NA" : N(scaleRelative[axis].ReplacementMax))
                    + " 교체p99=" + N(scaleRelative[axis].ReplacementP99())
                    + " 명목축길이px n=" + scalePixels[axis].Values.Count + " 최대=" + (scalePixels[axis].Values.Count == 0 ? "NA" : N(scalePixels[axis].Max))
                    + " p99=" + N(scalePixels[axis].P99()) + " 환산불가=" + scaleNoPixels[axis]
                    + "; 초기 sprite.bounds*trueScaleMultiplier*직교배율, 지연스프라이트/크기0은NA, 회전/시차/피벗/타일링 합성 렌더 오차 아님");
            Log("위치 축별 화면환산(1440 높이/직교 카메라) n=" + pixels.Values.Count + " 최대px=" + (pixels.Values.Count == 0 ? "NA" : N(pixels.Max)) + " p99px=" + N(pixels.P99())
                + " 제안기준<=0.01px; pivotPosVec 진단이며 시차/피벗/회전 합성 후 렌더 위치 검증은 별도");
            Log("상위장식8(속성 원단위 최대; 단위 혼합 순위) " + string.Join("; ", top.OrderByDescending(x => x.Value).Take(8).Select(x => table.Decorations[x.Key].Id + ":" + N(x.Value))));
            var reasons = new Dictionary<string, int> {
                ["Tile-동적 바닥 추종"] = 0, ["Tile-동적 효과 시점 재배치"] = 0,
                ["Camera-동적 화면 투영"] = 0, ["Camera-동적 배치 변경"] = 0,
                ["초기 배치 미지원"] = 0
            };
            foreach (var d in table.Decorations) foreach (string reason in d.Reasons) { reasons.TryGetValue(reason, out int n); reasons[reason] = n + 1; }
            Log("지원=" + table.Supported + "/" + table.Decorations.Length + " 비율=" + N(100.0 * table.Supported / Math.Max(1, table.Decorations.Length)) + "% (사유별 중복 가능)");
            foreach (var p in reasons.OrderByDescending(x => x.Value)) Log("미지원 " + p.Key + " 장식=" + p.Value + " 비율=" + N(100.0 * p.Value / Math.Max(1, table.Decorations.Length)) + "%");
            Phase(tape, table);
            Diagnose(tape, table, top.OrderByDescending(x => x.Value).Take(3).Select(x => x.Key));
            var scaleTop = stats[5].Top.Concat(stats[6].Top).GroupBy(x => x.Key)
                .Select(g => new { Id = g.Key, Error = g.Max(x => x.Value) }).OrderByDescending(x => x.Error).Take(3).Select(x => x.Id);
            Diagnose(tape, table, scaleTop, true);
        }
        private sealed class DiagnosticRow
        {
#if DEV || AUTOTEST
            internal DecoSim.Frame Frame;
#endif
            internal int Channel, Token;
            internal float Actual, Position;
            internal double Error;
        }
#if DEV || AUTOTEST
        private static float StateValue(DecoSim.State s, int channel, bool end)
#endif
        {
            if (s.Key == 7 || s.Key == 8 || s.Key == 11) { var v = end ? s.VE : s.VS;
                return channel == 6 || channel == 13 ? v.y : v.x; }
            if (s.Key == 9) { var c = end ? s.CE : s.CS; return channel == 7 ? c.r : channel == 8 ? c.g : channel == 9 ? c.b : c.a; }
            return end ? s.FE : s.FS;
        }
#if DEV || AUTOTEST
        private static void Diagnose(DecoSim.Tape tape, DecoBakeCore.Table table, IEnumerable<int> targets, bool scaleOnly = false)
#endif
        {
#if DEV || AUTOTEST
            var states = new Dictionary<int, DecoSim.State>();
#endif
            foreach (var state in tape.Initial) states[state.Token] = state;
#if DEV || AUTOTEST
            foreach (var ev in tape.Events) if (ev.Kind == DecoSim.Kind.Startup) states[ev.State.Token] = ev.State;
#endif
            Log("진단시계 calibration_i=" + N(tape.Calibration) + " addoffset=" + N(tape.AddOffset)
                + " pitch=" + N(table.Pitch) + " dspTimeSongPosZero=" + N(tape.DspZero)
                + " separateCountdownTime=" + tape.SeparateCountdown + " countdown곡초=" + N(tape.CountdownSeconds)
                + " editor=" + tape.EditorPath + "; offset 부호: baked(songposition_minusi+offset) 대 actual, 진단만/보정 적용 없음");
            foreach (int d in targets)
            {
                int id = table.Decorations[d].Id; var rows = new List<DiagnosticRow>();
                foreach (var frame in tape.Frames)
                {
                    if (frame.Pitch != table.Pitch || frame.TweenScale != table.TweenScale || !DecoBakeCore.Finite(frame.SongTime)) continue;
                    for (int i = frame.SampleBegin; i < frame.SampleEnd; i++)
                    {
                        var s = tape.Samples[i]; if (tape.DecorationIds[s.Decoration] != id) continue;
                        int c = Channel(s.Key); if (c < 0 || (scaleOnly && c != 5 && c != 6)) continue;
                        int n = s.Key == 9 ? 4 : s.Key == 11 ? 2 : 1;
                        for (int v = 0; v < n; v++)
                        {
                            float value = v == 0 ? s.X : v == 1 ? s.Y : v == 2 ? s.Z : s.W;
                            if (s.Key == 8) value = s.Y;
                            if (!DecoBakeCore.Finite(value) || !DecoBakeCore.Finite(frame.SongTime)) continue;
                            rows.Add(new DiagnosticRow { Frame = frame, Channel = c + v, Actual = value, Token = s.Token, Position = s.Position,
                                Error = Math.Abs((double)table.Eval(d, c + v, frame.BakeTime) - value) });
                        }
                    }
                }
                if (rows.Count == 0) continue;
                var worst = rows.OrderByDescending(x => x.Error).First();
                var channelRows = rows.Where(x => x.Channel == worst.Channel).ToList();
                Log((scaleOnly ? "크기상위3 " : "") + "진단 장식=" + id + " 초기pivot=" + N(table.Decorations[d].Initial[0]) + "," + N(table.Decorations[d].Initial[1])
                    + " startPos=" + N(table.Decorations[d].OriginalX) + "," + N(table.Decorations[d].OriginalY)
                    + " Global원점=" + N(table.Decorations[d].GlobalX) + "," + N(table.Decorations[d].GlobalY));
                foreach (var e in table.InputEvents)
                {
                    if (Array.IndexOf(e.Targets, d) < 0) continue;
                    Log("진단 효과 장식=" + id + " source=" + e.Source + " T0=" + N(e.T0) + " startTime=" + N(e.StartTime)
                        + " startEffectOffset=" + N(e.StartEffectOffset) + " duration=" + N(e.Duration) + " ease=" + e.Ease
                        + " relative=" + e.RelativePosition + " resetGlobal=" + e.ResetGlobalOrigin);
                    foreach (var stamp in tape.BakeStarts) if (stamp.Source == e.Source)
                        Log("진단 실제StartEffect source=" + e.Source + " frame=" + stamp.Frame + " song=" + N(stamp.Song) + " song-T0=" + N(stamp.Song - e.T0));
                    for (int c = 0; c < DecoBakeCore.Channels; c++)
                        foreach (var s in table.Tracks[d * DecoBakeCore.Channels + c]) if (s.Source == e.Source)
                            Log("진단 구간 " + Names[c] + " T0=" + N(s.T0) + " start=" + N(s.Start) + " end=" + N(s.End) + " target=" + N(s.Target) + " replacement=" + s.Replacement);
                }
                foreach (var token in rows.Select(x => x.Token).Distinct())
                    if (states.TryGetValue(token, out var rec))
                        foreach (int c in rows.Where(x => x.Token == token).Select(x => x.Channel).Distinct())
                            Log("진단 Startup 장식=" + id + " token=" + token + " 채널=" + Names[c] + " ease=" + rec.Ease
                                + " duration=" + N(rec.Duration) + " start=" + N(StateValue(rec, c, false)) + " target=" + N(StateValue(rec, c, true)));
                var first = channelRows[0];
                var moved = channelRows.FirstOrDefault(x => x.Token == first.Token && x.Actual != first.Actual);
                var firstMoving = rows.FirstOrDefault(x => x.Position > 0 && states.TryGetValue(x.Token, out var state)
                    && x.Actual != StateValue(state, x.Channel, false));
                Log("진단 Rec 처음 움직임 관측 장식=" + id + " " + (firstMoving == null ? "관측 불가" : "frame=" + firstMoving.Frame.UnityFrame
                    + " song=" + N(firstMoving.Frame.SongTime) + " 채널=" + Names[firstMoving.Channel] + " RecPos=" + N(firstMoving.Position))
                    + "; Rec 시작값과 다른 첫 캡처 표본, 캡처 이전 시작은 정확한 최초 프레임을 알 수 없음");
                Log("진단 Rec 최초관측 장식=" + id + " 채널=" + Names[worst.Channel] + " frame=" + first.Frame.UnityFrame
                    + " song=" + N(first.Frame.SongTime) + " RecPos=" + N(first.Position)
                    + " 첫값변화=" + (moved == null ? "관측 불가" : "frame=" + moved.Frame.UnityFrame + " song=" + N(moved.Frame.SongTime))
                    + "; 캡처 전 시작은 좌측절단, 첫값변화는 동일 token 표본 간 비교");
                int center = channelRows.IndexOf(worst), begin = Math.Max(0, Math.Min(center - 6, channelRows.Count - 12));
                foreach (var r in channelRows.Skip(begin).Take(12))
                {
                    double speed = DecoBakeCore.Speed(table, d, r.Channel, r.Frame.BakeTime);
                    double seconds = r.Error / speed;
                    Log("진단 행 장식=" + id + " 채널=" + Names[r.Channel] + " frame=" + r.Frame.UnityFrame
                        + " songposition_minusi=" + N(r.Frame.SongTime) + " actual=" + N(r.Actual)
                        + " tb=" + N(r.Frame.BakeTime) + " tbBaked=" + N(table.Eval(d, r.Channel, r.Frame.BakeTime))
                        + " baked=" + N(table.Eval(d, r.Channel, r.Frame.SongTime)) + " dt=" + N(r.Frame.Dt) + " RecPos=" + N(r.Position)
                        + " rawDt=" + N(r.Frame.RawDt) + " smoothDt=" + N(r.Frame.SmoothDt) + " useSmooth=" + r.Frame.UseSmooth
                        + " 시간오차ms=" + (DecoBakeCore.Finite(seconds) ? N(seconds * 1000) : "NA")
                        + " 시간오차프레임=" + (r.Frame.Dt > 0 && DecoBakeCore.Finite(seconds) ? N(seconds / (r.Frame.Dt * r.Frame.Pitch / r.Frame.TweenScale)) : "NA"));
                }
                // Bound work independently of capture FPS. Use one channel to avoid mixing physical units.
                var fit = channelRows.Where((r, i) => i % Math.Max(1, (channelRows.Count + 511) / 512) == 0).ToArray();
                Func<double, double> loss = shift => { double sum = 0; foreach (var r in fit) {
                    double error = table.Eval(d, r.Channel, r.Frame.SongTime + shift) - (double)r.Actual; sum += error * error; } return sum / fit.Length; };
                double offset = DecoBakeCore.FitOffset(loss);
                double max = 0, sumSq = 0; foreach (var r in channelRows) { double error = Math.Abs((double)table.Eval(d, r.Channel, r.Frame.SongTime + offset) - r.Actual); max = Math.Max(max, error); sumSq += error * error; }
                double timeSq = 0, timeMax = 0; int timeN = 0;
                foreach (var r in channelRows)
                {
                    if (!states.TryGetValue(r.Token, out var state) || r.Position <= 0 || r.Position >= state.Duration) continue;
                    var track = table.Tracks[d * DecoBakeCore.Channels + r.Channel];
                    int index = DecoBakeCore.Find(track, r.Frame.SongTime + offset); if (index < 0) continue;
                    var segment = track[index];
                    if (segment.Duration != state.Duration || segment.Ease != (int)state.Ease
                        || segment.Start != StateValue(state, r.Channel, false) || segment.Target != StateValue(state, r.Channel, true)) continue;
                    double residual = r.Frame.SongTime + offset - (segment.T0 + r.Position * (double)table.Pitch / table.TweenScale);
                    timeSq += residual * residual; timeMax = Math.Max(timeMax, Math.Abs(residual)); timeN++;
                }
                Log("진단 상수시간오프셋초=" + N(offset) + " 탐색범위=[-32,+32] 최종격자초=" + N(.000125) + " fitN=" + fit.Length
                    + " 전체잔차n=" + channelRows.Count + " RMS원단위=" + N(Math.Sqrt(sumSq / channelRows.Count)) + " 최대원단위=" + N(max)
                    + " offset0RMS=" + N(Math.Sqrt(loss(0))) + " actual범위=" + N(channelRows.Max(x => x.Actual) - channelRows.Min(x => x.Actual))
                    + " calibration*pitch차초=" + N(offset - tape.Calibration * table.Pitch)
                    + " addoffset차초=" + N(offset - tape.AddOffset) + " countdown차초=" + N(offset - tape.CountdownSeconds)
                    + "; 유계격자최소/유일해 보장없음, 평탄/반복/교체 구간은 식별불가. 위 잔차는 값 단위");
                Log("진단 시간잔차 장식=" + id + " n=" + timeN + " RMS곡초=" + (timeN == 0 ? "관측 불가" : N(Math.Sqrt(timeSq / timeN)))
                    + " 최대곡초=" + (timeN == 0 ? "관측 불가" : N(timeMax))
                    + "; 시작/목표/길이/ease 일치하는 진행중 Rec만, (song+fitOffset)-(T0+RecPos*pitch/tweenScale); 반복 Source 모호성은 남음");
            }
        }
#if DEV || AUTOTEST
        private static void Phase(DecoSim.Tape tape, DecoBakeCore.Table table)
#endif
        {
            var scheduled = new Dictionary<int, double>();
            foreach (var track in table.Tracks) foreach (var s in track) scheduled[s.Source] = s.T0;
            var frames = tape.Frames; var gaps = new List<int>(); int missing = 0;
            foreach (var stamp in tape.BakeStarts)
            {
                if (stamp.Frame < frames[0].UnityFrame || stamp.Frame > frames[frames.Count - 1].UnityFrame || !scheduled.TryGetValue(stamp.Source, out double t0)) continue;
                // First captured frame crossing T0; starts before the tape are explicitly censored.
                int crossing = -1;
                for (int i = 1; i < frames.Count; i++)
                    if (frames[i - 1].BakeTime < t0 && frames[i].BakeTime >= t0 && frames[i].BakeTime - frames[i - 1].BakeTime <= .05
                        && (crossing < 0 || Math.Abs(frames[i].UnityFrame - stamp.Frame) < Math.Abs(crossing - stamp.Frame)))
                        crossing = frames[i].UnityFrame;
                if (crossing < 0) { missing++; continue; }
                gaps.Add(stamp.Frame - crossing);
            }
            gaps.Sort();
            Log("효과시작위상 tb(실제 StartEffect UnityFrame - 테이프 tb T0 통과 UnityFrame) n=" + gaps.Count
                + " 최소=" + (gaps.Count == 0 ? "NA" : gaps[0].ToString()) + " 최대=" + (gaps.Count == 0 ? "NA" : gaps[gaps.Count - 1].ToString())
                + " 경계/되감기등미대응=" + missing + " 프레임차별빈도=" + string.Join(",", gaps.GroupBy(x => x).Select(g => g.Key + ":" + g.Count()))
                + "; 단조 재생 구간만 해석, FastMove 기존 후크 관찰; 제안 절대차<=1프레임, 반복 교차는 stamp에 가장 가까운 프레임");
        }
#endif
    }
}
#endif
