using System;

using System.Collections.Generic;

using System.Reflection;

using System.Reflection.Emit;

using DG.Tweening;

using HarmonyLib;

using UnityEngine;

using UnityModManagerNet;



namespace StutterFix

{

    // 얼불춤 고사양 맵의 프레임 문제를 줄이는 모드.

    //

    // 실제로 효과가 확인된 기능만 남겼다. 측정 근거는 README 참고.

    //   1) 곡 중 GC 멈춤        : 평균 106 -> 124fps (A/B 125쌍)

    //   2) DOTween 용량 확보    : 하위 1% 67 -> 96fps

    //   3) 맵 로딩 시 에셋 정리 건너뛰기 : 게임 코드가 부르는 200ms 정리 2건 제거

    //

    // 효과가 없어 제거한 것: 화면 밖 타일 색칠 미루기, 짧은 색 애니메이션 생략,

    // 이벤트 분산, 스프라이트/메시 컬링. 전부 A/B에서 오차 범위였다.

    public static class Main

    {

        // 측정용 플레이어 빌드 표시: 켜면 플레이어용에서도 엔진 단계별 시간을 잰다(곡 요약의 무거운 프레임 5개에 붙음). 배포 전에 false.

        internal const bool MeasureBuild = false;

#if AUTOTEST

        internal const bool AutoTestBuild = true;    // 측정용 플레이어 빌드(-p:AutoTestBuild=1): 진단 도구를 파일로 켤 수 있다

#else

        internal const bool AutoTestBuild = false;

#endif

        internal static UnityModManager.ModEntry Entry;

        internal static Settings Config;



        private static bool capacityApplied;

        private static float elapsed;

        private static int skippedUnloads;



        public static bool Load(UnityModManager.ModEntry modEntry)

        {

            Entry = modEntry;

            // 이 모드는 윈도우 전용이다(user32/kernel32/D3D11 직접 호출 수백 곳). 리눅스·맥의 원래(네이티브) 유니티에서는 EntryPointNotFoundException 으로 불러오기가 죽으므로

            // 아무것도 설치하지 않고 이유만 알려 준다. (Wine/Proton 에서는 Application.platform 이 WindowsPlayer 라 그대로 동작)

            if (Application.platform != RuntimePlatform.WindowsPlayer && Application.platform != RuntimePlatform.WindowsEditor)

            {

                modEntry.Logger.Warning("StutterFix 는 Windows 전용입니다. 이 운영체제(" + Application.platform + ")에서는 아무것도 설치하지 않고 꺼 둡니다. / StutterFix is Windows-only; disabled on this OS (" + Application.platform + ").");

                modEntry.OnGUI = e => GUILayout.Label("StutterFix 는 Windows 전용이라 이 운영체제(" + Application.platform + ")에서는 꺼져 있습니다. / StutterFix is Windows-only and is disabled on this OS.");

                return true;

            }

            // 7th Beat Games 모딩 가이드라인: 로그에 개조된 게임임을 남긴다.

            modEntry.Logger.Log("This game is modded (StutterFix " + modEntry.Info.Version + ")");

            Config = UnityModManager.ModSettings.Load<Settings>(modEntry);

            if (Config.DefaultsGen < 1)

            {

                // 2026-10-20: 히트음 예약 묶기와 화면 밖 파티클 멈추기를 기본 켬으로 (예전 설정 파일에는 false 가 적혀 있어 한 번만 덮어쓴다. 설정창에서 끄면 그대로 유지)

                Config.HitSoundBatch = true; Config.LowPauseParticles = true; Config.DefaultsGen = 1;

                try { Config.Save(modEntry); } catch { }

            }
            if (Config.DefaultsGen < 2)
            {
                // 2026-11-08: 화면 출력 대기 없애기를 기본 켬으로 (3440 효과 켬 ABBA: 앞서 준비 프레임 4 단독 +12~16%, 화면 대기 건너뛰기 단독 +14%, 서로 겹침)
                Config.MaxQueuedFrames = 4; Config.EngineWaitSkip = true; Config.DefaultsGen = 2;
                try { Config.Save(modEntry); } catch { }
            }
            // 3세대: 확대 장식 묶어 그리기(묶음 LOD)를 켰다가, 4세대(11-13 사용자 결정)에서 저사양 기능(기본 꺼짐)으로 되돌림. 손실 있음(PSNR 약 50dB).
            if (Config.DefaultsGen < 4) { Config.RunLod = false; Config.DefaultsGen = 4; try { Config.Save(modEntry); } catch { } }
            // 5세대(11-13 사용자 결정): GPU 장식 처리를 기본 켬으로. 장식이 아주 많은 맵(Arche +100% 이상, DD +29%)에서만 켜지고, 지원 안 되는 맵·환경은 스스로 꺼진다.
            if (Config.DefaultsGen < 5) { Config.GpuDeco = 1; Config.DefaultsGen = 5; try { Config.Save(modEntry); } catch { } }

            Resilience.Init();   // 비정상 종료 감지 (안전 모드면 이번 실행 동안 일부 기능을 끔)



            modEntry.OnGUI = OnGUI;

            modEntry.OnSaveGUI = OnSaveGUI;

            modEntry.OnUpdate = OnUpdate;

            modEntry.OnToggle = OnToggle;

            modEntry.OnUnload = Unload;   // 이것이 있어야 UMM이 게임을 켠 채로 새 DLL을 다시 불러온다



            ApplyConfig();

            if (Edition.Dev) LogLoadedCopies();

            InstallAll();

            if (Config.FlipModel < 0) { Config.FlipModel = BootConfig.FlipNow() ? 1 : 0; try { Config.Save(modEntry); } catch { } }   // 처음: 지금 boot.config 상태를 따른다

            BootConfig.Apply(Config.LegacyGfxJobs, Config.FlipModel == 1, false);

            modEntry.Logger.Log(BootConfig.Describe());

            WindowGhost.MainThread = Environment.CurrentManagedThreadId;

            WindowGhost.MainNativeThread = WindowGhost.GetCurrentThreadId();

            WindowGhost.KeepResponsive = Config.NoGhosting;

            if (Config.NoGhosting) WindowGhost.Disable();   // 첫 판 FPS 떨어짐 막기 (WindowGhost.cs)

            if (Edition.Dev) WindowGhost.StartWatch();   // 윈도우의 멈춘 창 판정 기록

            // (개발자용 시험) 모드 폴더의 파일로 멈춘 동안 화면 다시 내보내기를 바꾼다: present-keepalive 켬(효과 없어 기본은 끔), present-flip 보통 Present, present-screen-check 화면 비교

            if (Edition.Dev || Edition.AutoTest) DevPresentFlags(modEntry.Path);

            TightLate.Enabled = !System.IO.File.Exists(System.IO.Path.Combine(modEntry.Path, "tightlate-off"));

            TightLate.EulerMemo = !System.IO.File.Exists(System.IO.Path.Combine(modEntry.Path, "eulermemo-off"));   // 기본 켬(2026-10-19: LateUpdate 1.25→1.18ms, 비트 동일)

#if DEV || AUTOTEST

            TightLate.SkipSame = System.IO.File.Exists(System.IO.Path.Combine(modEntry.Path, "tightskip-on"));   // 2026-10-19: 건너뛴 비율 위치 1%·회전 9%·크기 34% 로 이득이 작아 기본 끔

#else

            TightLate.SkipSame = false;

#endif

            PosFast.Configure(modEntry.Path);

            ColorFast.Configure(modEntry.Path);

            if (Edition.AutoTest) {

#if DEV || AUTOTEST

bool atFail = false; try { AutoTest.Init(modEntry.Path); } catch (Exception ex) { atFail = true; modEntry.Logger.Error("자동 시험 준비 실패: " + ex.Message); }
            // 자동 시험 판에서 설치가 깨졌으면(다른 모드가 Harmony 를 깨뜨린 실행) 재지 않고 바로 끈다. 측정 스크립트가 다시 켠다.
            if ((installFailed || atFail) && (System.IO.File.Exists(System.IO.Path.Combine(modEntry.Path, "autotest-steps.json")) || System.IO.File.Exists(System.IO.Path.Combine(modEntry.Path, "autotest.txt")))) { modEntry.Logger.Error("[자동 시험] 설치 실패한 실행이라 바로 끕니다"); Application.Quit(); }

#endif

}   // 모드 폴더에 autotest.txt 가 있으면 자동 시험 (AutoTest.cs)

            PerfOverlay.FrameStatsOff = !Config.FrameStats;

            // (개발자용 시험) 모드 폴더에 frame-stats-off 파일이 있으면 설정과 상관없이 끈다 - 설정을 건드리지 않고 켜고 끄며 비교하려고

            if (Edition.Dev && System.IO.File.Exists(System.IO.Path.Combine(modEntry.Path, "frame-stats-off"))) { PerfOverlay.FrameStatsOff = true; modEntry.Logger.Log("[프레임 통계] frame-stats-off 파일이 있어 이번 실행은 끔"); }

            try { modEntry.Logger.Log("[프레임 통계] 게임 기본: " + (FrameTimingManager.IsFeatureEnabled() ? "켜짐" : "꺼짐") + ", 이 모드: " + (Config.FrameStats ? "켬(GPU 시간 측정)" : "안 켬") + (PerfOverlay.FrameStatsOff ? " -> 이번 실행 꺼짐" : "")); } catch { }

            if (LaunchWarning.Length > 0) modEntry.Logger.Log(LaunchWarning.Trim());

            return true;

        }



        private static void DevPresentFlags(string dir)

        {

            Func<string, bool> has = n => System.IO.File.Exists(System.IO.Path.Combine(dir, n));

            if (has("present-keepalive")) { WindowGhost.KeepPresent = true; Entry.Logger.Log("[첫 판 FPS] present-keepalive 파일이 있어 멈춘 동안 화면 다시 내보내기 켬"); }

            if (has("present-flip")) { WindowGhost.PresentMode = 0; Entry.Logger.Log("[첫 판 FPS] present-flip 파일이 있어 보통 Present(0,0) 로 내보냄"); }

            if (has("present-screen-check")) { WindowGhost.ScreenCheck = true; Entry.Logger.Log("[첫 판 FPS] present-screen-check 파일이 있어 멈춘 동안 화면을 비교함"); }

            if (has("beat-verify")) { BeatFix.Verify = true; Entry.Logger.Log("[박자 알림 검증] beat-verify 파일이 있어 건너뛴 타일을 가끔 원래대로 불러 봄"); }

            if (has("time-probe")) { Hitch.TimeProbeOn = true;

#if DEV || AUTOTEST

TimeProbe.Install(new HarmonyLib.Harmony("StutterFix.timeprobe"));

#endif

 }

            if (has("alloc-probe")) { Hitch.AllocProbeOn = true;

#if DEV || AUTOTEST

AllocProbe.Install(new HarmonyLib.Harmony("StutterFix.allocprobe"));

#endif

 }

#if DEV || AUTOTEST

            if (has("matprobe")) { MatProbe.Install(new HarmonyLib.Harmony("StutterFix.matprobe")); Entry.Logger.Log("[재질 만들기] matprobe 파일이 있어 곡 중 재질 만들기를 잼"); }

#endif

            if (has("late-verify")) { MoveApply.VerifyLate = true; Entry.Logger.Log("[장식 이동] late-verify 파일이 있어 LateUpdate 에 맡긴 장식을 32개 중 1개 대조"); }

            if (has("pairxy-off")) { DecoAnim.PairXY = false; Entry.Logger.Log("[장식 애니메이션] pairxy-off 파일이 있어 위치 X·Y 합치기 끔"); }

            if (has("rotscale-off")) { MoveApply.DeferRotScale = false; Entry.Logger.Log("[장식 이동] rotscale-off 파일이 있어 회전·크기 맡기기 끔"); }

            if (has("lazysprite-off")) { LazySprite.Enabled = false; Entry.Logger.Log("[투명 장식] lazysprite-off 파일이 있어 이미지 미루기 끔"); }

            if (has("floorlate-off")) { FloorLateFix.Enabled = false; Entry.Logger.Log("[타일 LateUpdate] floorlate-off 파일이 있어 위치·크기 읽기를 원래대로 함"); }

            if (has("tc-no-glow")) { TileCull.CullGlow = false; Entry.Logger.Log("[화면 밖 타일] tc-no-glow 파일이 있어 타일 발광은 끄지 않음"); }

            if (has("tc-old-pin")) { TileCull.PinOnScript = true; Entry.Logger.Log("[화면 밖 타일] tc-old-pin 파일이 있어 타일 스크립트가 돌면 예전처럼 관리에서 뺌"); }

            if (has("fastopa-off")) { global::StutterFix.FloorAnim.FastOpa = false; Entry.Logger.Log("[타일 이동] fastopa-off 파일이 있어 불투명도 전용 빠른 경로를 끔"); }

            if (has("deco-split-probe")) { FastMove.Probe = true; Entry.Logger.Log("[장식 이동 쪼개 보기] deco-split-probe 파일이 있어 큰 효과를 단계별로 잼"); }

            if (has("tilecull-verify.txt")) { TileCull.Verify = true; TileCull.VerifyInstall(); Entry.Logger.Log("[화면 밖 타일 검증] tilecull-verify.txt 파일이 있어 그리기마다 검사함"); }

            if (has("tilecull-min.txt")) { try { TileCull.MinFloors = int.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(dir, "tilecull-min.txt")).Trim()); Entry.Logger.Log("[화면 밖 타일] 최소 타일 수 " + TileCull.MinFloors); } catch { } }

            if (has("ffx-verify.txt")) { FfxReuse.Verify = true; Entry.Logger.Log("[효과 재사용 검증] ffx-verify.txt 파일이 있어 효과 붙이기마다 원래 방식과 비교함"); }

#if DEV || AUTOTEST

            if (has("alloc-phase")) { PhaseWatch.AllocPhases = true; Entry.Logger.Log("[할당 단계] alloc-phase 파일이 있어 곡 중 단계별 힙 증가를 잼"); }

#endif

            if (has("freeze-test")) { PresentWatch.FreezeTest = true; Entry.Logger.Log("[첫 판 FPS 시험] freeze-test 파일이 있어 곡 중 6초 멈춤 시험을 함"); }

            try { var gm = System.IO.Path.Combine(dir, "gc-mode.txt"); if (System.IO.File.Exists(gm)) { GcControl.PauseMode = System.IO.File.ReadAllText(gm).Trim().ToLowerInvariant(); Entry.Logger.Log("[GC] (시험) 곡 중 멈추는 방식: " + GcControl.PauseMode); } } catch { }

            // (시험) 곡 중 힙 한계를 낮춰 긴 곡의 "한계 뒤 조금씩 치우기" 를 빨리 재현 / 조각 치우기 방식

            try { var gl = System.IO.Path.Combine(dir, "gc-limit.txt"); if (System.IO.File.Exists(gl)) { GcControl.HardLimitMB = int.Parse(System.IO.File.ReadAllText(gl).Trim()); Entry.Logger.Log("[GC] (시험) 곡 중 힙 한계 " + GcControl.HardLimitMB + "MB"); } } catch { }

            try { var gg = System.IO.Path.Combine(dir, "gc-garbage.txt"); if (System.IO.File.Exists(gg)) { GcControl.GarbageMBps = float.Parse(System.IO.File.ReadAllText(gg).Trim(), System.Globalization.CultureInfo.InvariantCulture); Entry.Logger.Log("[GC] (시험) 곡 중 쓰레기 초당 " + GcControl.GarbageMBps + "MB 만듦"); } } catch { }

            try { var gs = System.IO.Path.Combine(dir, "gc-slice.txt"); if (System.IO.File.Exists(gs)) { GcControl.SliceStyle = System.IO.File.ReadAllText(gs).Trim().ToLowerInvariant(); Entry.Logger.Log("[GC] (시험) 조각 치우기 방식: " + GcControl.SliceStyle); } } catch { }

        }



        // UMM에서 모드를 끄고 켤 때 불린다.

        // 예전에는 true 만 돌려주고 아무것도 안 해서, 꺼도 패치와 GC 멈춤이 그대로 살아 있었다.

        // 이제 끄면 다시 불러오기 때와 똑같이 전부 풀고, 켜면 다시 건다.

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)

        {
#if AUTOTEST
            if (!value) ScriptPath.Stop();
            if (!value) EngineKnob.Stop();
            if (!value) PresentPath.Stop();
#endif
#if DEV || AUTOTEST
            if (!value) TileRender.Shutdown();
#endif

            if (value) { Resilience.Init(); ApplyExperimentalOptions(); InstallAll(); } else UninstallAll();

            // 모드를 끄면 게임 파일도 원래대로 돌려놓는다(다음 실행부터 원래 방식).

            // 다시 불러오기도 내부에서 끄기 -> 켜기를 거치므로 그때는 건드리지 않는다

            // (중간에 실패하면 legacy 줄이 빠진 채로 남았다).

            if (!reloading) BootConfig.Apply(value && Config.LegacyGfxJobs, value && Config.FlipModel == 1);

            if (!reloading && !value) { try { RestoreFullscreen(); } catch { } }   // 모드를 끄면 독점 전체 화면도 원래대로

            return true;

        }



        private static bool installed;



        // Steam 실행 옵션 -force-d3d12 / -force-gfx-jobs 가 28~40초 박자 끊김(60~80ms)의 원인이었다.

        // PerfView로 엔진 안쪽을 보니 끊긴 60ms 동안 게임 전체가 거의 쉬고 있었고(GPU도 대기),

        // VRAM이 8GB 한도에 걸려 600MB가 시스템 램으로 밀려난 상태였다. D3D12가 그것을 옮기는 동안 다 같이 멈춘다.

        // 옵션을 빼고 D3D11로 돌리자 그 구간 끊김이 사라졌다. 모드로는 고칠 수 없는 자리라 켜져 있으면 알린다.

        private static string launchWarning;



        internal static string LaunchWarning

        {

            get

            {

                if (launchWarning != null) return launchWarning;

                launchWarning = "";

                try

                {

                    string args = string.Join(" ", Environment.GetCommandLineArgs()).ToLowerInvariant();

                    var found = new List<string>();

                    // 문제였던 것은 D3D12 위의 native 그래픽 작업이다. legacy/split 은 따로 시험 중이라 경고하지 않는다.

                    if (args.Contains("-force-gfx-jobs native")) found.Add("-force-gfx-jobs native");

                    if (found.Count > 0)

                        launchWarning = "  ⚠ Steam 실행 옵션에 " + string.Join(", ", found.ToArray()) +

                                        " 가 있습니다. 곡 중 그래픽 메모리를 새로 잡다가 60~80ms씩 멈춥니다. 빼는 것을 권합니다.";

                }

                catch { }

                return launchWarning;

            }

        }



        // 2026-11-11~12: 다른 모드 라이브러리 JALib 은 켜질 때 스레드 풀에서 Harmony 내부(UpdateWrapper 등)를 패치하는데, 그 초기화(JAPatcher 정적 생성자)가
        // 가끔 예외로 끝난다(어제 코드로도 10번 중 4번: 이 모드의 변경 탓이 아님). 그러면 그 실행 내내 모든 모드의 Harmony.Patch 가 실패하고,
        // 이 모드는 그 순간 이후의 패치를 못 깐다. 시도해 본 것: 다시 시도(120번 모두 실패), JALib 기다리기(효과 없음),
        // Harmony 잠금을 잡고 먼저 다 깔기(이 모드는 항상 다 깔리지만 JALib 이 14번 중 13번 실패해서 버림). 지금은 알리기만 한다.
        private static void InstallAll()
        {
            if (InstallAllOnce()) return;
            Entry.Logger.Error("패치 설치가 중간에 실패해 일부 기능만 켜졌습니다. 다른 모드가 Harmony 를 깨뜨린 실행일 수 있습니다(로그의 harmony patch failed 참고). 게임을 껐다 켜 주세요.");
        }
        private static bool installFailed;
        private static bool InstallAllOnce()

        {

            if (installed) return true;
            installFailed = false;

            installed = true;

            capacityApplied = false;   // 모드별 감시, 단계 표시 같은 늦은 설치도 다시 하게 한다

            elapsed = 0f;

            try

            {

                var harmony = new Harmony(Entry.Info.Id);

                SettingsWindow.InstallInputGuard(harmony);

                // 실제 수정 (두 버전 공통)

                PatchUnloadCallers(harmony);

                TextFix.Install(harmony);

                EffectScan.Install(harmony);   // 효과 나누기가 이 패치를 통해 돈다

                RecolorSplit.Install(harmony);

                RecolorDefer.Install(harmony);

                ZeroTween.Install(harmony);
                DecoDetach.Install(harmony);

                InstantMove.Install(harmony);

                MoveProf.Install(harmony);

                FastMove.Install(harmony);

                if(TagIndex.Enabled)OptimizationModules.Install(TagIndexModule.Instance);

                if(MapProfiles.Enabled)OptimizationModules.Install(MapProfilesModule.Instance);

                if(HitchHeatmap.Enabled)OptimizationModules.Install(HitchHeatmapModule.Instance);

                if(FrameQueueDelay.Enabled)OptimizationModules.Install(FrameQueueDelay.Instance);

                Try(MaterialPropertyIds.Warm);

                Precheck.Install(harmony);

                DecoAnim.Install(harmony);

                FloorAnim.Install(harmony);   // 길이 있는 타일 이동 애니메이션을 모드가 직접 (FloorAnim.cs)

                LowEnd.Install(harmony);

                ParticleFix.Install(harmony);

                LeakGuard.Install(harmony);

                LoadFix.Install(harmony);

                LoadFix.InstallDoubleReset(harmony);

                LoadFix.InstallRetryColliders(harmony);

                LoadFix.InstallTextureDict(harmony);

                FastJson.Install(harmony);

                DecodeFix.Install(harmony);

                ParallelDecode.Install(harmony);

                DecoBatch.Install(harmony);

                HitTextFade.Install(harmony);

                HitMeterFade.Install(harmony);

                PlayTweaks.Install(harmony);

                TexCompress.Install(harmony);

                HalfRender.Install(harmony);



#if DEV || AUTOTEST

RenderVerify.Install(harmony);

#endif



                FrameParts.Install(harmony);



#if DEV || AUTOTEST

UiProf.Install(harmony);

#endif



                MoveApply.Install(harmony);

                TightLate.Install(harmony);

                Dormancy.Install(harmony);

                ImagePrefetch.Install(harmony);

#if DEV || AUTOTEST

                DecoDiag.Install(harmony);

#endif

                FastBlend.Install(harmony);

                HoldFix.Install(harmony);

                HitboxQuery.Install(harmony);
#if AUTOTEST

                DisplayPace.Install(harmony);

#else

                if (Config.DisplayPace) DisplayPace.Install(harmony);

#endif

                InvisibleSkip.Install(harmony);

                TweenFix.Install(harmony);

                SceneReset.Install(harmony);

                TransitionFix.Install(harmony);

                Try(() => ExitFix.Install(harmony));   // 편집으로 나가기 멈춤 (ExitFix.cs)

                Try(() => { FfxReuse.Install(harmony); FfxReuse.InstallMakeLevel(harmony); });   // 효과 컴포넌트 다시 쓰기 (FfxReuse.cs)

                GcControl.Install();

                SettingsWindow.Create();

                RestartAdvisor.Init();

                RestartAdvisor.StartReopen();

                PerfOverlay.Create();

                Try(() => PerfOverlay.Install(harmony));

                Try(() => WindowGhost.Install(harmony));   // 긴 전환 중 윈도우 멈춘 창 판정 막기 자리 더하기

                Try(() => BigLevel.Install(harmony));   // 타일이 아주 많은 맵 불러오기 (BigLevel.cs)

                Try(() => AllocFix.Install(harmony));   // 곡 중 쓰레기 줄이기 (AllocFix.cs)

                Try(() => ThumbCam.Install(harmony));   // 아무도 안 보는 썸네일 카메라 끄기 (ThumbCam.cs)

                Try(() => TileCull.Install(harmony, Entry.Path));

                Try(() => FloorLateFix.Install(harmony));   // 화면 밖 타일 끄기 (TileCull.cs)

                Try(() => BeatFix.Install(harmony));   // 박자 알림에서 아무것도 안 하는 타일 건너뛰기 (BeatFix.cs)

                Try(() => MatReuse.Install(harmony));   // 타일 머티리얼 누수 막기 (MatReuse.cs)

                Try(() => EditorPick.Install(harmony));   // 큰 맵 편집 화면 클릭 끊김 (EditorPick.cs)

                Try(() => FilterIdentity.Install(harmony));

                Try(() => TypeCache.Install(harmony));   // 고급 필터 형식 찾기 기억 (TypeCache.cs)

                Try(() => MeshWarm.Install(harmony));   // 색 바꾸기가 곡 중에 만들 타일 메시 미리 만들기 (MeshWarm.cs)

                Try(() => SoundWarm.Install(harmony));   // 곡 중에 처음 쓰는 효과음 미리 불러오기 (SoundWarm.cs)

                Try(() => RunLod.Install(harmony));   // 묶음 LOD: 재생 준비 때 한 번 모으기 (RunLod.cs)
                Try(HitSoundBatch.Install);

                if (Edition.Dev || MeasureBuild || AutoTestBuild) {

#if DEV || AUTOTEST

Try(() => ScriptProbe.Install(harmony));

#endif

}   // (진단) scriptprobe.txt 가 있으면 스크립트별 시간

                if (Edition.Dev && System.IO.File.Exists(System.IO.Path.Combine(Entry.Path, "floorprof.txt"))) {

#if DEV || AUTOTEST

Try(() => FloorProf.Install(harmony));

#endif

}   // (측정) floorprof.txt 가 있으면 타일 이동 안 DOTween 몫



#if AUTOTEST

                // 측정용 플레이어 빌드: startprobe-cold.txt 가 있으면 맵 열기·재생 시작 단계 시간 (자주 불리는 함수는 감싸지 않음)

                if (!Edition.Dev && System.IO.File.Exists(System.IO.Path.Combine(Entry.Path, "startprobe-cold.txt"))) Try(() => StartProbe.Install(harmony));

#endif

                // 측정 (개발자용만)

                if (Edition.Dev)

                {



#if DEV || AUTOTEST

StartProbe.Install(harmony);

#endif





#if DEV || AUTOTEST

Try(() => FilterTrace.Install(harmony));

#endif





#if DEV || AUTOTEST

Try(() => MergeProbe.Install(harmony));

#endif





#if DEV || AUTOTEST

                    RenderWatch.Install(harmony);

#endif



#if DEV || AUTOTEST

RenderCallbackScan.Install(harmony);

#endif





#if DEV || AUTOTEST

FrameRateScreenWatch.Install(harmony);

#endif





#if DEV || AUTOTEST

ParticleTextWatch.Install(harmony);

#endif



                }

                GpuDecoPlayer.Configure();

                if (Config.JitWarm && !Resilience.Off("JitWarm")) Try(JitWarm.Run);   // 모드 함수 미리 컴파일 (곡 초반 처음 쓰는 효과의 JIT, JitWarm.cs)

                Entry.Logger.Log("켜짐: 패치 설치 완료 (" + Edition.Name + ")");

            }

            catch (Exception ex)

            {

                Entry.Logger.Error("harmony patch failed: " + ex);
                installFailed = true;

            }

            if (Edition.Dev && !installFailed) VerifyPatches();
            return !installFailed;

        }



        // ── 다시 불러오기가 엉뚱한 함수를 건 경우 ─────────────────────────

        // Harmony 는 걸어 둔 패치 함수를 "모듈 ID + 번호"로 기억했다가, 같은 원본을 다시 패치할 때 그 둘로 찾는다.

        // Ctrl+F5 로 코드가 바뀐 DLL을 불러오자, 새 TextFix.SetTextPrefix 자리가 RenderWatch.OnPreCull(Camera c)로

        // 풀렸다. 이전에 불러온 어셈블리의 같은 번호를 잡은 것이다. 그 결과 글자/애니메이션/색 패치가 전부 실패하고,

        // 엉뚱하게 이어진 패치가 매 프레임 끊김 기록을 새로 시작시켜 파티클 검색이 10만 번 돌았다.

        // 걸린 패치가 전부 지금 어셈블리의 함수인지 확인하고, 하나라도 아니면 전부 풀고 재시작을 요청한다.

        internal static string ReloadProblem = "";



        // 다시 불러오기가 왜 이전 DLL을 잡는지 보려고, 메모리에 올라온 이 모드의 사본과 모듈 ID를 남긴다.

        private static void LogLoadedCopies()

        {

            try

            {

                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())

                {

                    string n = a.GetName().Name;

                    if (!n.StartsWith("StutterFix", StringComparison.Ordinal)) continue;

                    Entry.Logger.Log("[사본] " + n + " 모듈 " + a.ManifestModule.Name + " ID " + a.ManifestModule.ModuleVersionId +

                        (a == typeof(Main).Assembly ? " (지금 것)" : ""));

                }

            }

            catch (Exception ex) { Entry.Logger.Error("[사본] " + ex.Message); }

        }



        private static void VerifyPatches()

        {

            try

            {

                var mine = typeof(Main).Assembly;

                var ids = new HashSet<string>(HarmonyIds);

                int ok = 0;

                var bad = new List<string>();

                foreach (var original in Harmony.GetAllPatchedMethods())

                {

                    var info = Harmony.GetPatchInfo(original);

                    if (info == null) continue;

                    var all = new List<Patch>();

                    all.AddRange(info.Prefixes); all.AddRange(info.Postfixes);

                    all.AddRange(info.Transpilers); all.AddRange(info.Finalizers);

                    foreach (var p in all)

                    {

                        if (!ids.Contains(p.owner)) continue;

                        MethodInfo pm = null;

                        try { pm = p.PatchMethod; } catch { }

                        if (pm != null && pm.DeclaringType != null && pm.DeclaringType.Assembly == mine) ok++;

                        else if (bad.Count < 5) bad.Add(original.DeclaringType?.Name + "." + original.Name + " -> " +

                            (pm == null ? "?" : pm.DeclaringType?.Assembly.GetName().Name + ":" + pm.DeclaringType?.Name + "." + pm.Name));

                        else bad.Add("");

                    }

                }



                if (bad.Count == 0) { ReloadProblem = ""; return; }



                ReloadProblem = "다시 불러오기가 깨졌습니다 (패치 " + bad.Count + "개가 이전 DLL을 가리킴). 모드를 전부 껐습니다. 게임을 껐다 켜 주세요.";

                Entry.Logger.Error(ReloadProblem + " 정상 " + ok + "개");

                foreach (var b in bad) if (b.Length > 0) Entry.Logger.Error("  " + b);

                UninstallAll();

            }

            catch (Exception ex) { Entry.Logger.Error("패치 확인 실패: " + ex.Message); }

        }



        // Resources.UnloadUnusedAssets 를 부르는 곳은 게임 전체에서 딱 세 군데다.

        // (IL 스캔으로 확인: scnGame.Awake, scnGame.LoadLevel, scnEditor.SwitchToEditMode)

        // 세 곳 모두 반환값을 바로 버리므로(call 다음 바이트가 pop) 건너뛰어도 로직에 영향이 없다.

        //

        // 특히 scnEditor.SwitchToEditMode 는 플레이를 멈추고 편집으로 돌아올 때마다 불린다.

        // 로그에서 이 호출 하나가 매번 120ms씩 화면을 세웠다(정리된 에셋은 1~2개뿐이었다).

        private static void PatchUnloadCallers(Harmony harmony)

        {

            var transpiler = new HarmonyMethod(typeof(Main), nameof(UnloadTranspiler));



            var targets = new[]

            {

                // scnGame.LoadLevel 은 뺐다(2.2.0): 에디터에서 맵을 새로 열 때 이전 맵의 머티리얼 인스턴스가 이 정리로만 풀리는데, 건너뛰면

                // 맵을 열 때마다 쌓였다(측정: 맵 A 뒤 4,781개 → 맵 B 뒤 11,601개 = 4,781 + 맵 B 의 6,820). 맵 열기는 어차피 몇 초 걸리는 순간이라 원래대로 둔다.

                new[] { "scnGame", "Awake" },

                new[] { "scnEditor", "SwitchToEditMode" },

            };



            foreach (var t in targets)

            {

                var type = AccessTools.TypeByName(t[0]);

                if (type == null) continue;

                var m = AccessTools.Method(type, t[1]);

                if (m == null) continue;

                try

                {

                    harmony.Patch(m, transpiler: transpiler);

                    Entry.Logger.Log("patched " + t[0] + "." + t[1]);

                }

                catch (Exception ex)

                {

                    Entry.Logger.Error("patch failed " + t[0] + "." + t[1] + ": " + ex.Message);

                }

            }

        }



        public static IEnumerable<CodeInstruction> UnloadTranspiler(IEnumerable<CodeInstruction> instructions)

        {

            var original = AccessTools.Method(typeof(Resources), nameof(Resources.UnloadUnusedAssets), new Type[0]);

            var replacement = AccessTools.Method(typeof(Main), nameof(MaybeUnloadUnusedAssets));



            foreach (var ins in instructions)

            {

                // 라벨과 예외 블록을 유지하려고 명령어를 새로 만들지 않고 피연산자만 바꾼다.

                if (ins.opcode == OpCodes.Call && ReferenceEquals(ins.operand, original))

                    ins.operand = replacement;

                yield return ins;

            }

        }



        public static AsyncOperation MaybeUnloadUnusedAssets()

        {

            if (Config != null && Config.SkipAssetUnload)

            {

                skippedUnloads++;

                return null;   // 호출부에서 바로 버리는 값이라 null이어도 안전하다

            }

            return Resources.UnloadUnusedAssets();

        }



        // ── 게임을 켠 채로 다시 불러오기 ──────────────────────────────

        // 고칠 때마다 게임을 껐다 켜고 맵을 다시 여는 것이 너무 느렸다.

        // UMM은 모드가 OnUnload 를 주면 새 DLL을 다시 불러올 수 있다.

        // 단, 내려가면서 게임에 걸어 둔 것을 전부 되돌려야 한다. 안 그러면 옛 코드가 계속 돈다.

        //   - Harmony 패치 (이 모드가 쓰는 ID 전부. ID 없이 UnpatchAll 하면 남의 모드까지 지운다)

        //   - GC 멈춤 상태, 씬/카메라 이벤트, PlayerLoop 표시, 다른 모드 갱신 함수 감싸기

        private static readonly string[] HarmonyIds =

        {

            "StutterFix", "StutterFix.GcControl", "StutterFix.AllocScan", "StutterFix.SlowScan", "StutterFix.Profiler",

            "StutterFix.matprobe", "StutterFix.fuseprobe", "StutterFix.cpubreak",

        };



        private static bool Unload(UnityModManager.ModEntry modEntry)

        {

            modEntry.Logger.Log("내리는 중 (다시 불러오기)");

            UninstallAll();

            return true;

        }



        private static void UninstallAll()

        {

            if (!installed) return;

            installed = false;

            Try(BeatFix.Shutdown);

            EndDisplayTransition();

            Try(GpuDecoPlayer.Shutdown);
            Try(ReplayCompat.Reset);
            Try(RunLod.Unload);

#if AUTOTEST

            Try(LightProbe.Uninstall);
            Try(ScriptPath.Stop);
            Try(GpuExactCensus.Stop);
            Try(EngineKnob.Stop);
            Try(PresentPath.Stop);

#endif

#if DEV || AUTOTEST

            Try(FreeFpsProbe.Restore);

            Try(TileSim.Shutdown);

            Try(DecoBake.Shutdown);

            Try(DecoSim.Shutdown);

            Try(TileCost.Shutdown);
            Try(TileRender.Shutdown);

            Try(DecoGpu.Stop);

            Try(DecoRender.Stop);

            Try(FilterGpuProbe.Stop);

            Try(GpuHide.Stop);
            Try(DecoDetach.FinishReattach);
            Try(FlashSkip.Shutdown);
            Try(MouseSkip.Shutdown);
            Try(GpuPriority.Restore);

            Try(Occlude.Stop);
            Try(ZCull.Stop);
            Try(GpuSplit.Stop);

#if DEV || AUTOTEST

            Try(FrameCompare.Unload);

            Try(MatShare.Stop);

            Try(LiveDecoGate2.RestoreProfile);

            Try(LiveDeco.Stop);

#endif

            Try(GpuCensus.Stop);

            Try(OffscreenProbe.Stop);

            Try(DecoJobShadow.Stop);

            Try(UpdateCostProbe.Stop);

            Try(DecoBatchShadow.Stop);

            Try(GpuProbe.Stop);

            Try(ArcheGpuResearch.Stop);

            Try(FilterHalfProbe.Stop);

            Try(FilterHalfPerf.Stop);

#endif

            Try(FrameGen.Shutdown);

            Try(HitSoundBatch.Shutdown);

            Try(HitMeterFade.Shutdown);

            Try(MemoryBudget.Reset);

            Try(NativeMotion.Reset);

            Try(OptimizationModules.Shutdown);

            Try(ThreadScheduling.Shutdown);

            Try(RenderThreadScheduling.Shutdown);

            Try(TagIndex.Shutdown);

            Try(UploadBudget.Restore);

            Try(Updater.Shutdown);

            Try(() => AssetDiskCache.Store=null);

            Try(ScreenEffects.Shutdown);

            Try(ShaderWarm.Shutdown);

            Try(FilterIdentity.Shutdown);

            Try(GcControl.Shutdown);

            Try(GameScreenButton.Remove);

            Try(LowEnd.Shutdown);

            Try(PhysicsPause.Shutdown);

            Try(TileCull.Shutdown);

            Try(RecolorDefer.Shutdown);

            Try(HalfRender.Shutdown);

            Try(ParticleFix.Shutdown);

            Try(Resilience.Shutdown);

            Try(Fsr.Shutdown);

#if DEV || AUTOTEST

            Try(RenderWatch.Shutdown);

            Try(PhaseWatch.Uninstall);

            Try(LoopProfiler.Shutdown);

            Try(AllocScan.Shutdown);

#endif

            Try(Profiler.Stop);

            Try(ModWatch.Shutdown);

#if DEV || AUTOTEST

            Try(SamplerWatch.Shutdown);

#endif

            // 곡 중에 내리면 원래 게임이 이미 끝냈을 일을 마저 한다: 밀린 효과·타일 색 조각 실행, 모드 애니메이션은 끝값으로(Kill(true) 와 같음)

            if (Hitch.Playing) Try(EffectBudget.FlushAll);

            Try(global::StutterFix.DecoAnim.FinishAll);

            Try(global::StutterFix.DecoEngine.Uninstall);

            Try(global::StutterFix.DecoAnim.BatchUninstall);

            Try(global::StutterFix.FloorAnim.FinishAll);

            Try(RestoreQueued);

            Try(EffectBudget.Reset);       // 색 나누기 대기열도 같이 비운다

            Try(ImagePrefetch.Stop);       // 이미지 작업 스레드와 풀어 둔 메모리

#if DEV || AUTOTEST

            Try(TightSpriteProbe.Stop);
            Try(SpriteDiscardResearch.Stop);
            Try(SpriteChannelResearch.Stop);
            Try(CpuWindowProbe.Stop);
            Try(LeafProbe.Stop);
            Try(DecoBake.StopClockMemo);
            Try(DecoBake.StopClockCohorts);

#endif

            Try(() => SystemMonitor.Keep = false);

            Try(DisplayPace.Uninstall);

            Try(() => EnginePatch.MaskGrow(false));

            Try(() => EnginePatch.WaitSkip(false));

            Try(EnginePatch.DisableTurbo);

            Try(FastBlend.Uninstall);      // 바꿔 끼운 블렌드 장식 재질을 원래대로

#if DEV || AUTOTEST

            Try(HiddenDecoSynergy.Reset);

#endif

            Try(InvisibleSkip.Uninstall);   // 그리기에서 뺀 투명 장식을 되돌린다

            Try(SettingsWindow.Destroy);

            Try(global::StutterFix.MeshWarm.Release);   // 미리 만들기용 숨긴 물체

            Try(PerfOverlay.Destroy);



#if DEV || AUTOTEST

Try(ParticleTextWatch.Shutdown);

#endif





#if DEV || AUTOTEST

Try(RenderCallbackScan.Shutdown);

#endif



#if DEV || AUTOTEST

            Try(SlowScan.Shutdown);

#endif

#if DEV || AUTOTEST

            Try(WasteProbe.Off);

            Try(PatchHot.Off);

#endif

            Try(WindowGhost.StopWatch);



            foreach (var id in HarmonyIds)

                Try(() => new Harmony(id).UnpatchOwner(id));



            Try(TightLate.Uninstall);

            Try(PosFast.Uninstall);

            Try(ColorFast.Uninstall);



            Entry.Logger.Log("꺼짐: 패치와 감시를 모두 풀었음");

        }



        private static void Try(Action a)

        {

            try { a(); }

            catch (Exception ex) { Entry.Logger.Error("내리는 중 오류: " + ex.Message); }

        }



        internal static void RequestReload()

        {

            try

            {

                var type = typeof(UnityModManager.ModEntry);

                var canReload = AccessTools.Property(type, "CanReload");

                if (canReload != null && !(bool)canReload.GetValue(Entry, null))

                    canReload.SetValue(Entry, true, null);



                var reload = AccessTools.Method(type, "Reload");

                if (reload == null) { Entry.Logger.Error("UMM에 Reload가 없음"); return; }

                reloading = true;

                reload.Invoke(Entry, null);

            }

            catch (Exception ex)

            {

                Entry.Logger.Error("다시 불러오기 실패: " + ex);

            }

            finally { reloading = false; }

        }



        private static bool reloading;



        internal static long UpdateTicks;   // (개발자용 측정) 이 모드 OnUpdate 에 쓴 시간

        internal static readonly long[] TickCost = new long[17];

        internal static readonly string[] TickName = { "GcControl", "RestartAdvisor", "EffectBudget", "RecolorSplit", "FastBlend", "MoveApply", "VramGuard", "ImagePrefetch", "RenderWatch(개발)", "ModWatch(개발)", "AllocScan(개발)", "AbTest(개발)", "LoopProfiler(개발)", "Profiler(개발)", "InvisibleSkip.DevTick(개발)", "BlendProbe(개발)", "그중 Hitch.Tick(GcControl 안, 개발자용 측정 대부분)" };

        private static void Tk(int i, ref long q) { long n = System.Diagnostics.Stopwatch.GetTimestamp(); TickCost[i] += n - q; q = n; }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float dt)

        {

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

            OnUpdateBody(modEntry, dt);

            UpdateTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;

        }



        private static void OnUpdateBody(UnityModManager.ModEntry modEntry, float dt)

        {
            DecoDetach.Pump();


            // Ctrl+F5: 게임을 켠 채로 새 DLL을 불러온다. 옛 코드는 여기서 바로 빠져나가야 한다.

            if (Edition.Dev && !SettingsWindow.EditingText && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(KeyCode.F5))

            {

                RequestReload();

                return;

            }



#if DEV || AUTOTEST

if (Edition.AutoTest && AutoTest.ReloadNow) { AutoTest.ReloadNow = false; RequestReload(); return; }

#endif





            if (!installed) return;   // 꺼져 있으면 아무 일도 하지 않는다



            long q = System.Diagnostics.Stopwatch.GetTimestamp();

            ReplayCompat.Tick();
            GcControl.Tick(dt); Tk(0, ref q);

            RestartAdvisor.Tick(); if (Edition.AutoTest) {

#if DEV || AUTOTEST

AutoTest.Tick();

#endif

} FrameGen.Tick(); PcTune.Tick(); ThreadScheduling.Tick(); RenderThreadScheduling.Tick(); if(FrameQueueDelay.Enabled)FrameQueueDelay.Tick(); LowEnd.AutoTick(); LowEnd.MenuCapTick(); Updater.Tick(); TileCull.Tick(); LeakGuard.Tick(); Resilience.Tick(); if (!Hitch.Playing && Time.realtimeSinceStartup > 30f) Compat.LogSharedPatches(); Tk(1, ref q);

#if DEV || AUTOTEST

            if (!GpuDecoPlayer.Product && DecoBake.Enabled) DecoBake.Tick();

#endif

            GpuDecoPlayer.Tick();

            EffectBudget.Tick(); Tk(2, ref q);

            RecolorSplit.Tick(); Tk(3, ref q);

            FastBlend.Tick(); Tk(4, ref q);

            MoveApply.Tick(); Tk(5, ref q);

            VramGuard.Tick(); Tk(6, ref q);

            ImagePrefetch.LogAfterLoad(); GameScreenButton.Tick(); Tk(7, ref q);



            if (Edition.Dev)

            {

#if DEV || AUTOTEST

                RenderWatch.Tick(dt); SlowScan.Tick(); Tk(8, ref q);

                ModWatch.Tick(dt); Tk(9, ref q);

                AllocScan.Tick(dt); Tk(10, ref q);

                AbTest.Tick(dt); Tk(11, ref q);

                LoopProfiler.Tick(dt); Tk(12, ref q);

                Profiler.Tick(dt); Tk(13, ref q);



                if (Input.GetKeyDown(KeyCode.F7)) LoopProfiler.Toggle();

                if (Input.GetKeyDown(KeyCode.F8)) AbTest.Toggle();

                if (Input.GetKeyDown(KeyCode.F9)) AllocScan.Toggle();

                if (Input.GetKeyDown(KeyCode.F6)) TextureCensus.Run();



#if DEV || AUTOTEST

BlendProbe.Tick();

#endif

 Tk(15, ref q);

                InvisibleSkip.DevTick(); Tk(14, ref q);

#endif

            }



            if (capacityApplied) return;



            // DOTween 초기화 이후에 적용해야 해서 잠시 기다린다.

            elapsed += dt;

            if (elapsed < 3f) return;

            ApplyCapacity();

        }



        internal static void ApplyCapacity()

        {

            capacityApplied = true;

#if DEV || AUTOTEST

            if (Edition.Dev)

            {

                ModWatch.Install();   // 다른 모드들이 다 올라온 뒤에 감싼다

                SamplerWatch.Install();

                PhaseWatch.Install();  // 끊긴 프레임의 범인을 단계 단위로 지목하려면 항상 켜져 있어야 한다

            }

            else if (MeasureBuild || System.IO.File.Exists(System.IO.Path.Combine(Entry.Path, "phasewatch")))

            {

                PhaseWatch.Install();  // 측정용 플레이어 빌드: 무거운 프레임의 엔진 단계를 곡 요약에 남긴다

            }

#endif

            try

            {

                DOTween.Init();

                DOTween.SetTweensCapacity(Config.TweenerCapacity, Config.SequenceCapacity);

                Entry.Logger.Log($"tween capacity set to {Config.TweenerCapacity}/{Config.SequenceCapacity}");

            }

            catch (Exception ex)

            {

                Entry.Logger.Error("failed to set tween capacity: " + ex.Message);

            }

        }



        // 설정 파일에 저장된 켜기/끄기를 각 기능에 반영한다.

        internal static void ApplyConfig()

        {

            if (Config.ShowOverlay) { Config.ShowOverlay = false; Config.OverlayMode = 1; }   // 예전 "모니터 켜짐" 은 아이콘으로

            if (Config.ImageAutoVer < 1) { if (Config.ImageMaxSide == 0) Config.ImageMaxSide = ImagePrefetch.Auto; Config.ImageAutoVer = 1; }   // 1.2.2: 기본을 자동으로

            ApplyToggles();

        }



        // (실험, 설정 창에 없음) 앞서 준비하는 프레임 수. 0 이면 게임 값 그대로. 바꿨으면 모드를 내릴 때 되돌린다.

        private static int origQueued = -1;

        private static void ApplyQueued()

        {

            try

            {

                int want = Config.MaxQueuedFrames;

                if (want > 0)

                {

                    if (origQueued < 0) origQueued = QualitySettings.maxQueuedFrames;

                    if (QualitySettings.maxQueuedFrames != want) { QualitySettings.maxQueuedFrames = want; Entry.Logger.Log("[화면 출력 실험] 앞서 준비하는 프레임 " + origQueued + " -> " + want); }

                }

                else if (origQueued >= 0) { QualitySettings.maxQueuedFrames = origQueued; origQueued = -1; }

            }

            catch (Exception ex) { Entry.Logger.Log("[화면 출력 실험] 실패: " + ex.Message); }

        }

        private static void RestoreQueued() { if (origQueued >= 0) { QualitySettings.maxQueuedFrames = origQueued; origQueued = -1; } }

        // (실험 옵션, 설정 창 그래픽) 전체 화면일 때 독점 전체 화면(ExclusiveFullScreen). 창 모드면 건드리지 않는다.

        // 유니티는 끌 때 전체 화면 방식을 기억하므로, 모드가 바꿨다는 표시(ExpFsChanged)를 저장해 두고 옵션을 끄면 다음 실행에서도 전체 화면 창으로 되돌린다.

        private static bool displayPending,displayApplied;

        private static DisplayTransitionRunner displayRunner;

        private static FullScreenMode displayTarget,displayIssuedTarget,displayObserved;

        private static int displayFrame;

        private static float displaySince,displayStableSince;

        internal static string DescribeDisplay() => "mode="+Screen.fullScreenMode+" pending="+displayPending+" applied="+displayApplied+" window="+Screen.width+"x"+Screen.height;

        internal static void ApplyFullscreen()

        {

            bool want=Config.ExpFullscreen==1;

            if(want && Screen.fullScreen && (displayPending || Screen.fullScreenMode!=FullScreenMode.ExclusiveFullScreen))

                RequestDisplay(FullScreenMode.ExclusiveFullScreen);

            else if(!want) RestoreFullscreen();

            else if(Screen.fullScreenMode==FullScreenMode.ExclusiveFullScreen && !Config.ExpFsChanged) {

                Config.ExpFsChanged=true; try { Config.Save(Entry); } catch { }

            }

        }

        private static void RequestDisplay(FullScreenMode target)

        {

            if(displayPending) { displayTarget=target; return; }

            // SetResolution is asynchronous. Quiesce the worker and release all

            // backbuffer references BEFORE Unity starts its DXGI mode transition.

            FrameGen.BeginDisplayChange();

            displayPending=true;displayApplied=false;displayTarget=target;

            displaySince=Time.realtimeSinceStartup;

            var go=new GameObject("StutterFix.DisplayTransition") {hideFlags=HideFlags.HideAndDontSave};

            UnityEngine.Object.DontDestroyOnLoad(go);displayRunner=go.AddComponent<DisplayTransitionRunner>();

        }

        private static void EndDisplayTransition()

        {

            displayPending=displayApplied=false;FrameGen.EndDisplayChange();

            if(displayRunner!=null) {UnityEngine.Object.Destroy(displayRunner.gameObject);displayRunner=null;}

        }

        private sealed class DisplayTransitionRunner:MonoBehaviour

        {

            // A restore requested by OnToggle(false) must finish even when UMM

            // stops calling this disabled mod's OnUpdate. Unload removes us.

            private void Update() {TickFullscreen();}

        }

        private static void TickFullscreen()

        {

            if(!displayPending) return;

            try {

                float now=Time.realtimeSinceStartup;

                if(!displayApplied) {

                    if(!FrameGen.DisplayChangeReady) {

                        if(now-displaySince>5) {

                            Entry.Logger.Log("[화면 출력] 생성 출력 해제 확인 시간 초과; 화면 전환 취소");

                            EndDisplayTransition();

                        }

                        return;

                    }

                    var r=Screen.currentResolution;

                    Entry.Logger.Log("[화면 출력] 생성 출력 해제 확인; "+Screen.fullScreenMode+" -> "+displayTarget+" "+r.width+"x"+r.height);

                    displayIssuedTarget=displayTarget;

                    Screen.SetResolution(r.width,r.height,displayIssuedTarget,r.refreshRateRatio);

                    Config.ExpFsChanged=displayTarget==FullScreenMode.ExclusiveFullScreen;

                    try { Config.Save(Entry); } catch { }

                    displayApplied=true;displayFrame=Time.frameCount;displaySince=now;

                    displayObserved=Screen.fullScreenMode;displayStableSince=now;

                    return;

                }

                if(displayObserved!=Screen.fullScreenMode) {displayObserved=Screen.fullScreenMode;displayStableSince=now;}

                // Allow Unity's render thread/window mode hand-off to settle;

                // do not install a new Present connection in that same frame.

                if(Time.frameCount-displayFrame<8 || now-displayStableSince<.25f) return;

                if(displayObserved!=displayIssuedTarget && now-displaySince<1f) return;

                Entry.Logger.Log("[화면 출력] 전환 완료: 요청="+displayIssuedTarget+" 실제="+displayObserved);

                if(displayTarget!=displayIssuedTarget) {displayApplied=false;displaySince=now;return;}

                EndDisplayTransition();

            } catch(Exception ex) {

                EndDisplayTransition();

                Entry.Logger.Log("[화면 출력] 전환 실패: "+ex.Message);

            }

        }

        private static void RestoreFullscreen()

        {

            if(!Config.ExpFsChanged && !displayPending) return;

            if(Screen.fullScreenMode==FullScreenMode.ExclusiveFullScreen || displayPending)

                RequestDisplay(FullScreenMode.FullScreenWindow);

            else {Config.ExpFsChanged=false;try {Config.Save(Entry);}catch {}}

        }

        private static void ApplyExperimentalOptions()

        {

            if(Config==null)return;

            if(installed)GpuDecoPlayer.Configure();

            Func<string,bool,bool> effective=Resilience.Eff;

            MemoryBudget.Enabled=Config.GcDynamicBudget;

            UploadBudget.Configure(Config.AsyncUploadBudget);

            FastJsonParser.PoolStrings=Config.JsonStringPool;

            DecoAnim.FastCompact=Config.DecoFastCompact;

            AssetDiskCache.Configure(Config.AssetDiskCache,Config.AssetDiskCacheMB,Config.AssetDiskCachePath);

            MoveApply.EpsilonSkip=effective("MoveEpsilon",Config.MoveEpsilon);

            NativeMotion.Configure(effective("NativeMotion",Config.NativeMotion),Config.NativeMotionArena,Config.NativeMotionPinArena);

            TagIndex.Configure(effective("TagIndex",Config.TagIndex),Config.TagIndexNativePlans);

            MapProfiles.Configure(effective("MapProfiles",Config.MapProfilesEnabled));

            ThreadScheduling.Configure(Config.ThreadSchedulingMode,Config.ThreadPerformanceCores,Config.ThreadAvoidLogicalZero);

            RenderThreadScheduling.Configure(Config.ThreadRenderScheduling,Config.ThreadSchedulingMode,Config.ThreadPerformanceCores,Config.ThreadAvoidLogicalZero);

            FrameQueueDelay.Enabled=effective("FrameQueueDelay",Config.FrameQueueDelay);

            HitchHeatmap.Enabled=effective("HitchHeatmap",Config.HitchHeatmap);

            PhysicsPause.Enabled=Config.PhysicsPause;if(!PhysicsPause.Enabled)PhysicsPause.SongEnded();

            ParticleFix.CatchupOffscreen=effective("ParticleCatchup",Config.ParticleCatchup);

            if(installed)

            {

                if(TagIndex.Enabled)OptimizationModules.Install(TagIndexModule.Instance);

                else OptimizationModules.Uninstall("tags");

                if(MapProfiles.Enabled)OptimizationModules.Install(MapProfilesModule.Instance);

                else OptimizationModules.Uninstall("profiles");

                if(HitchHeatmap.Enabled)OptimizationModules.Install(HitchHeatmapModule.Instance);

                else OptimizationModules.Uninstall("heatmap");

                if(FrameQueueDelay.Enabled)OptimizationModules.Install(FrameQueueDelay.Instance);

                else OptimizationModules.Uninstall("input-frame-delay");

            }

        }

#if DEV || AUTOTEST

        internal static string LifecycleFixture()

        {

            if(Hitch.Playing)throw new InvalidOperationException("Lifecycle fixture is menu-only");

            bool tagBefore=Config.TagIndex,nativeBefore=Config.NativeMotion;

            var method=AccessTools.Method(typeof(scnGame),"FinishCustomLevelLoading",new[]{typeof(int),typeof(bool)});

            int Owners(){int n=0;var info=Harmony.GetPatchInfo(method);if(info!=null)foreach(var owner in info.Owners)if(owner=="StutterFix.optimization.tags")n++;return n;}

            int expected=OptimizationModules.Count+(Owners()==0?1:0);

            try

            {

                Config.TagIndex=Config.NativeMotion=true;ApplyExperimentalOptions();

                if(OptimizationModules.Count!=expected||Owners()!=1)throw new Exception("Module install mismatch");

                OnToggle(Entry,false);

                if(OptimizationModules.Count!=0||Owners()!=0||NativeMotion.Enabled)throw new Exception("Module uninstall mismatch");

                OnToggle(Entry,true);

                if(OptimizationModules.Count!=expected||Owners()!=1||!NativeMotion.Enabled||!TagIndex.Enabled)throw new Exception("Module re-enable mismatch");

                return "checks=3 mismatch=0 owners=1/0/1 native_enabled=True/False/True";

            }

            finally

            {

                Config.TagIndex=tagBefore;Config.NativeMotion=nativeBefore;

                if(!installed)OnToggle(Entry,true);

                ApplyConfig();

            }

        }

#endif

        private static void ApplyToggles()

        {

            ApplyQueued();

            ApplyFullscreen();

            // 설정 값에 "이번 실행 동안 끔"(오류 자동 차단, 안전 모드)을 겹친다 (Resilience)

            Func<string, bool, bool> E = Resilience.Eff;

            bool low = !Resilience.Off("LowEnd");   // 저사양 그래픽 기능 묶음

            GcControl.Enabled = E("GcPause", Config.GcPause);

            ApplyExperimentalOptions();

            EffectBudget.Enabled = E("EffectSplit", Config.EffectSplit);

            RecolorSplit.Enabled = E("RecolorSplit", Config.RecolorSplit);

            RecolorDefer.Enabled = E("RecolorDefer", Config.RecolorDefer);

            if (!RecolorDefer.Enabled) RecolorDefer.FlushAll();

            TweenFix.Enabled = E("TweenGuard", Config.TweenGuard);

            HitTextFade.Enabled = E("HitTextFade", Config.HitTextFade);

            HitMeterFade.Enabled = E("HitMeterFade", Config.HitMeterFade);

            HitMeterFade.HitMeterDirectKill = E("HitMeterDirectKill", Config.HitMeterDirectKill);

            {

                var off = new HashSet<string>((Config.EffectsOff ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));

                foreach (var k in PlayTweaks.Kinds) k.Off = off.Contains(k.Event);

                PlayTweaks.FiltersOff.Clear();

                foreach (var f in (Config.FiltersOff ?? "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries)) PlayTweaks.FiltersOff.Add(f);

                PlayTweaks.NoFx = Config.NoFx;

                PlayTweaks.AheadSec = Mathf.Clamp(Config.NoFxAheadSec, 0.3f, 10f);

                GameScreenButton.Enabled = Config.GameScreenButton;

                PlayTweaks.HideJudgeAll = Config.HideJudgeAll; PlayTweaks.HideJudgePerfect = Config.HideJudgePerfect; PlayTweaks.NoPlayZoom = Config.NoPlayZoom;

            }

            ZeroTween.Enabled = E("ZeroTween", Config.ZeroTween);

            ZeroTween.SkipUpdate = ZeroTween.Enabled;

            InstantMove.Enabled = E("InstantDirect", Config.InstantDirect);

            InstantMove.SkipSame = E("InstantDirect", Config.SkipSame);

            FastMove.Enabled = E("FastLoop", Config.FastLoop);

            Precheck.Enabled = E("Precheck", Config.Precheck); Precheck.ResetAll();   // 설정이 바뀌면 확인해 둔 것을 모두 버린다

            bool deco = E("DecoAnim", Config.DecoAnim);

            if (!deco && global::StutterFix.DecoAnim.Enabled) global::StutterFix.DecoAnim.FinishAll();   // 끄면 진행 중인 것은 끝값으로 (Kill(true) 와 같음)

            global::StutterFix.DecoAnim.Enabled = deco;

            bool floorAnim = E("FloorAnim", Config.FloorAnim);

            if (!floorAnim && global::StutterFix.FloorAnim.Enabled) global::StutterFix.FloorAnim.FinishAll();

            global::StutterFix.FloorAnim.Enabled = floorAnim;

            LowEnd.Priority = Config.LowPriority; LowEnd.NoThrottle = Config.LowNoThrottle; LowEnd.HalfMsTimer = Config.TimerHalfMs; LowEnd.NoFft = Config.LowNoFft; LowEnd.RenderScalePct = low ? Mathf.Clamp(Config.LowRenderScale, 10, 100) : 100; LowEnd.SharpUpscale = Config.LowSharpUpscale; LowEnd.ImageCap = Config.LowImageCap; LowEnd.Sharpen = low && Config.LowSharpen; LowEnd.SharpenValue = Mathf.Clamp(Config.LowSharpenValue, 0.25f, 4f); HalfRender.Enabled = low && E("LowHalfRender", Config.LowHalfRender); LowEnd.AutoRes = low && Config.LowAutoRes; LowEnd.AutoTargetFps = Config.LowAutoFps; LowEnd.AutoMinPct = Mathf.Clamp(Config.LowAutoMin, 10, 100); LowEnd.MenuFps = Config.LowMenuFps; Fsr.Enabled = low && E("LowFsr", Config.LowFsr) && !Config.LowSharpUpscale;

            EffectBudget.BudgetMs = Config.LowSplit >= 2 ? 3f : Config.LowSplit == 1 ? 5f : 10f;

            RecolorSplit.ChunkTiles = Config.LowSplit >= 2 ? 120 : Config.LowSplit == 1 ? 200 : 400;

            bool fsplit = low && E("LowFloorSplit", Config.LowFloorSplit);

            if (!fsplit && global::StutterFix.FloorAnim.Split) global::StutterFix.FloorAnim.Flush();

            global::StutterFix.FloorAnim.Split = fsplit;

            global::StutterFix.FloorAnim.SplitBudgetMs = Config.LowSplit >= 2 ? 1.5f : Config.LowSplit == 1 ? 2.5f : 4f;

            ScreenEffects.Apply();

            Fsr.Sharpness=Mathf.Clamp(Config.FxRcasSharpness,0,2);

            Fsr.Apply();

            LowEnd.Apply();

            MoveApply.Enabled = E("MoveFinish", Config.MoveFinish);

            ParticleFix.SkipIdle = E("SkipIdleParticles", Config.SkipIdleParticles); ParticleFix.PauseOffscreen = E("LowPauseParticles", Config.LowPauseParticles); LeakGuard.Enabled = E("LeakFix", Config.LeakFix); MatReuse.Enabled = LeakGuard.Enabled;

            bool lc = E("LoadCache", Config.LoadCache); LoadFix.CacheFileTimes = lc; LoadFix.SkipDoubleReset = lc; LoadFix.ReverseToggle = lc; LoadFix.RetryColliders = lc; LoadFix.FastTextureDict = lc; FastJson.Enabled = lc; DecodeFix.Enabled = lc; TransitionFix.KeepImages = TransitionFix.SkipRestartReset = lc; BigLevel.Enabled = lc; ExitFix.Enabled = lc; FfxReuse.Enabled = lc; AllocFix.Enabled = E("GcPause", Config.GcPause);

            TexCompress.OwnOption = E("ImagePrefetch", Config.LowCompressImages) && !Resilience.SafeMode;   // 안전 모드: 미리 풀기는 줄이기에만 쓰고 저사양 압축은 끈다

            MoveApply.LogicSkip = Dormancy.Enabled = E("DormantSkip", Config.DormantSkip);

            BeatFix.Enabled = Dormancy.Enabled; EditorPick.Enabled = Dormancy.Enabled;

            BeatFix.SetSkipNoChange(Config.BeatSkipNoChange);

            TextFix.SkipSameText = E("SkipSameText", Config.SkipSameText);

            TypeCache.Enabled = E("FilterTypeCache", Config.FilterTypeCache);

            FilterIdentity.Enabled = E("FilterIdentity", Config.FilterIdentity);

            global::StutterFix.MeshWarm.Enabled = E("MeshWarm", Config.MeshWarm);

            global::StutterFix.SoundWarm.Enabled = E("SoundWarm", Config.SoundWarm);

            global::StutterFix.HitSoundBatch.Enabled = E("HitSoundBatch", Config.HitSoundBatch);

            ImagePrefetch.Enabled = E("ImagePrefetch", Config.ImagePrefetch);

            ShaderWarm.Enabled = E("ShaderWarm", Config.ShaderWarm);

            FastBlend.Enabled = E("FastBlend", Config.FastBlend);

            JitOpt.Apply(Config.JitInline);

            HoldFix.Enabled = Config.HoldSkip;

            HitboxQuery.Enabled = Config.HitboxQuerySkip;
            DisplayPace.Enabled = Config.DisplayPace;

            EnginePatch.MaskGrow(Config.EngineMaskGrow);

            EnginePatch.WaitSkip(Config.EngineWaitSkip);
            InvisibleSkip.Enabled = E("SkipInvisible", Config.SkipInvisible);
            DecoDetach.Enabled = E("GpuDecoDetach", Config.GpuDecoDetach);
            DecoDetach.CpuEnabled = E("DecoDetachCpu", Config.DecoDetachCpu);
            FlashSkip.SetEnabled(E("SkipClearFlash", Config.SkipClearFlash));
            MouseSkip.Enabled = E("MouseEventsSkip", Config.MouseEventsSkip);

            ThumbCam.Enabled = InvisibleSkip.Enabled || ReplayCompat.Active; ThumbCam.Apply();   // 리플레이 중에도 썸네일 카메라는 꺼 둔다 (중간에 다시 켜면 화면을 덮었다)

            TileCull.Enabled = InvisibleSkip.Enabled;

            InvisibleSkip.LazyMove = E("SkipInvisible", Config.LazyHidden);

            if (!InvisibleSkip.Enabled) InvisibleSkip.RestoreAll();

            else if (!InvisibleSkip.LazyMove) InvisibleSkip.ApplyAllLazy();

            ImagePrefetch.MaxSide = LowEnd.CombinedMaxSide(Config.ImageMaxSide);

        }



        private static void OnGUI(UnityModManager.ModEntry modEntry)

        {

            if (Edition.Dev) DevGUI(); else PlayerGUI();

        }



        // 플레이어용 UMM 화면: 소개와 설정 창 열기 버튼만. 설정은 따로 뜨는 창(SettingsWindow)에서 한다.

        private static void PlayerGUI()

        {

            GUILayout.Label("<b>Stutter Fix</b>  v" + Entry.Info.Version + "  ·  made by <b>naro</b> & <b>Claude</b>");

            GUILayout.Label(SettingsWindow.T("고사양 커스텀 맵에서 플레이 중 순간적으로 멈추는 현상과 맵 로딩 시간을 줄입니다. 연출과 판정은 바꾸지 않습니다.",

                "Reduces hitches during play and loading times on heavy custom levels. Visuals and judgement are unchanged."));

            if (LaunchWarning.Length > 0) GUILayout.Label(LaunchWarning);

            GUILayout.Space(8);

            WindowButton();

        }



        private static void WindowButton()

        {

            GUILayout.BeginHorizontal();

            if (GUILayout.Button(SettingsWindow.T("설정 창 열기", "Open settings"), GUILayout.Width(160), GUILayout.Height(30))) SettingsWindow.Toggle();

            GUILayout.Label(SettingsWindow.T("   게임 중 언제든 <b>" + Hotkey.Name(Config.WindowKey, Config.WindowMods) + "</b> 키로 열고 닫을 수 있습니다.",

                "   Press <b>" + Hotkey.Name(Config.WindowKey, Config.WindowMods) + "</b> at any time in game to open or close it."), GUILayout.Height(30));

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();

            if (GUILayout.Button(SettingsWindow.T("문제 보고용 로그 만들기", "Create bug-report log"), GUILayout.Width(160), GUILayout.Height(30)))

            {

                if (LogExport.Export() != null) LogExport.Reveal();

            }

            string lr = LogExport.LastError.Length > 0 ? SettingsWindow.T("   만들지 못했습니다: ", "   Failed: ") + LogExport.LastError

                : LogExport.LastPath.Length > 0 ? SettingsWindow.T("   바탕화면에 만들었습니다: ", "   Saved to desktop: ") + System.IO.Path.GetFileName(LogExport.LastPath)

                : SettingsWindow.T("   끊김이나 오류가 있었다면 눌러서 생긴 zip 파일을 모드 디스코드 서버에 올리거나 <b>narooh</b> 에게 DM 으로 보내 주세요.", "   After a stutter or error, press it and post the zip file on the mod's Discord server or send it to <b>narooh</b> (DM).");

            GUILayout.Label(lr, GUILayout.Height(30));

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();

            if (GUILayout.Button(SettingsWindow.T("디스코드 서버 열기", "Open Discord server"), GUILayout.Width(160), GUILayout.Height(30))) Application.OpenURL(SettingsWindow.DiscordUrl);

            GUILayout.Label(SettingsWindow.T("   버그 제보, 기능 아이디어, 질문: ", "   Bug reports, feature ideas, questions: ") + "discord.gg/csys9ZAeD6", GUILayout.Height(30));

            GUILayout.EndHorizontal();

        }



        private static void DevGUI()

        {

#if DEV || AUTOTEST

            GUILayout.Label("고사양 맵의 프레임 문제를 줄입니다. 효과가 측정된 기능만 들어 있습니다. (" + Edition.Name + ")");

            if (LaunchWarning.Length > 0) GUILayout.Label(LaunchWarning);

            if (ReloadProblem.Length > 0) GUILayout.Label("  ⚠ " + ReloadProblem);

            WindowButton();

            GUILayout.BeginHorizontal();

            if (GUILayout.Button("모드 다시 불러오기 (Ctrl+F5)", GUILayout.Width(220))) RequestReload();

            GUILayout.Label("  게임을 켠 채로 새로 빌드한 DLL을 적용합니다. 설정 슬라이더는 기본값으로 돌아갑니다.");

            GUILayout.EndHorizontal();



            GUILayout.Space(10);

            GUILayout.Label("── 그래픽 작업 분산 ──");

            bool legacy = GUILayout.Toggle(Config.LegacyGfxJobs, "  그리기 명령을 여러 스레드로 만든다 (legacy 그래픽 작업, 게임 재시작 후 적용)");

            if (legacy != Config.LegacyGfxJobs) { Config.LegacyGfxJobs = legacy; BootConfig.Apply(legacy, Config.FlipModel == 1); Config.Save(Entry); }

            GUILayout.Label("    " + (BootConfig.Status.Length > 0 ? BootConfig.Status : BootConfig.Describe()));

            GUILayout.Label("    측정: D3D11 프레임 140 -> 160, 곡 전체 끊김 150번대 -> 93번 (-force-gfx-jobs legacy 와 같은 효과)");

            bool fs = GUILayout.Toggle(Config.FrameStats, "  프레임 시간 통계를 켠다 (GPU 시간 측정, 게임 재시작 후 적용. 끄면 모니터의 CPU/GPU 시간·화면 대기 줄이 빈다)");

            if (fs != Config.FrameStats) { Config.FrameStats = fs; Config.Save(Entry); }

            GUILayout.Label("    조사 중: 판마다 곡 내내 화면 대기 1.7ms 가 붙는 상태(약 200 FPS)와 없는 상태(약 320 FPS)로 갈린다. 원래 게임은 이 통계가 꺼져 있다.");



            GUILayout.Space(10);

            GUILayout.Label("── 곡 중 GC 멈춤 (핵심) ──");

            GcControl.Enabled = GUILayout.Toggle(GcControl.Enabled, "  곡을 플레이하는 동안 GC를 멈춘다");

            GcControl.NoCollectDuringSong = GUILayout.Toggle(GcControl.NoCollectDuringSong,

                "  곡 중에는 아예 치우지 않고 쌓아두기만 한다 (권장)");

            GUILayout.Label("    측정: 평균 106 -> 124fps (A-B 125쌍)");

            GUILayout.Label("    " + GcControl.Status);

            GUILayout.BeginHorizontal();

            GUILayout.Label("한계 " + GcControl.HardLimitMB + "MB에서 한 번 정리", GUILayout.Width(200));

            GcControl.HardLimitMB = (int)GUILayout.HorizontalSlider(GcControl.HardLimitMB, 1000f, 12000f, GUILayout.Width(200));

            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();

            GUILayout.Label("곡 끝나고 " + GcControl.EndDelaySeconds.ToString("F0") + "초 뒤 정리", GUILayout.Width(200));

            GcControl.EndDelaySeconds = (int)GUILayout.HorizontalSlider(GcControl.EndDelaySeconds, 0f, 15f, GUILayout.Width(200));

            GUILayout.EndHorizontal();

            GUILayout.Label("    (완주 연출이 도는 중에 정리하면 연출이 끊깁니다)");



            GUILayout.Space(10);

            GUILayout.Label("── 끊김 기록 ──");

            Hitch.Enabled = GUILayout.Toggle(Hitch.Enabled, "  끊긴 프레임을 기록한다 (곡이 끝나면 정리해서 보여줌)");

            GUILayout.BeginHorizontal();

            GUILayout.Label("기준 " + (int)Hitch.ThresholdMs + "ms", GUILayout.Width(200));

            Hitch.ThresholdMs = (int)GUILayout.HorizontalSlider(Hitch.ThresholdMs, 8f, 100f, GUILayout.Width(200));

            GUILayout.EndHorizontal();

            GUILayout.Label("    " + Hitch.Summary);

            GUILayout.Label("    모드별 사용량: " + ModWatch.Summary);

            SlowScan.Enabled = GUILayout.Toggle(SlowScan.Enabled,

                "  끊길 때 어느 게임 함수가 느렸는지도 찍는다 (곡 시작이 4초 느려지고, 곡 중 5초마다 멈춥니다)");

            InvisibleSkip.PixelCompare = GUILayout.Toggle(InvisibleSkip.PixelCompare,

                "  20초마다 투명 장식을 빼고/넣고 화면을 두 번 그려 픽셀을 비교한다 (그때마다 200ms 멈추고, 시간으로 움직이는 필터가 한 번 튄다)");



            GUILayout.Space(10);

            GUILayout.Label("── 애니메이션 목록 재정렬 막기 (핵심) ──");

            TweenFix.Enabled = GUILayout.Toggle(TweenFix.Enabled,

                "  효과가 도는 동안 DOTween 목록을 건드리지 않는다  (지금까지 " + TweenFix.Guarded + "회)");

            GUILayout.Label("    측정: 한 프레임 435ms 중 382ms가 목록 재정렬 4981회였다");



            GUILayout.Space(10);

            GUILayout.Label("── 효과 몰림 나누기 ──");

            EffectBudget.Enabled = GUILayout.Toggle(EffectBudget.Enabled,

                "  한 프레임에 몰린 효과를 나눠서 시작한다");

            GUILayout.BeginHorizontal();

            GUILayout.Label("한 프레임에 " + (int)EffectBudget.BudgetMs + "ms까지", GUILayout.Width(200));

            EffectBudget.BudgetMs = (int)GUILayout.HorizontalSlider(EffectBudget.BudgetMs, 3f, 60f, GUILayout.Width(200));

            GUILayout.EndHorizontal();

            GUILayout.Label("    지금까지 " + EffectBudget.DeferredTotal + "개 미룸, 대기 " + EffectBudget.QueueLength + "개");

            RecolorSplit.Enabled = GUILayout.Toggle(RecolorSplit.Enabled,

                "  타일 색 바꾸기를 " + RecolorSplit.ChunkTiles + "칸씩 나눠 칠한다" + (RecolorSplit.Patched ? "" : " (적용 안 됨)"));

            GUILayout.Label("    지금까지 " + RecolorSplit.SplitEffects + "번 나눔, 타일 " + RecolorSplit.DeferredTiles + "칸 미룸, 순서 맞추려 먼저 칠함 " + RecolorSplit.FlushedForOrder + "번, 대기 " + RecolorSplit.Pending + "조각");

            ShaderWarm.Enabled = GUILayout.Toggle(ShaderWarm.Enabled, "  곡 시작 때 셰이더를 미리 준비한다 (" + ShaderWarm.Last + ")");

            Main.Config.FastBlend = FastBlend.Enabled = GUILayout.Toggle(FastBlend.Enabled, "  블렌드 장식을 화면 복사 없이 그린다 (지금 " + FastBlend.Count + "개)");



            GUILayout.Space(10);

            GUILayout.Label("── 글자 장식 ──");

            GUILayout.Label("    TextGenerator 재사용: 바꾼 자리 " + TextFix.Replaced + "곳, 지금까지 " + TextFix.Reused + "회 재사용");

            TextFix.SkipSameText = GUILayout.Toggle(TextFix.SkipSameText,

                "  같은 글자를 다시 넣으면 건너뛴다 (지금까지 " + TextFix.SkippedSameText + "회)");

            GUILayout.Label("    (PACL2 모드가 매 프레임 글자 장식 34개를 같은 내용으로 다시 넣습니다)");

            GUILayout.Label("    (원래는 초당 3891번 새로 만들어 96MB/s를 잡아먹던 자리)");



            GUILayout.Space(10);

            GUILayout.Label("── 맵 로딩 ──");

            Config.SkipAssetUnload = GUILayout.Toggle(Config.SkipAssetUnload,

                $"  로딩 시 에셋 정리 건너뛰기 (지금까지 {skippedUnloads}회)");

            ImagePrefetch.Enabled = GUILayout.Toggle(ImagePrefetch.Enabled, "  장식 이미지를 여러 코어에서 미리 푼다");

            GUILayout.Label("    " + ImagePrefetch.Last);



            GUILayout.Space(10);

            GUILayout.Label("── DOTween 용량 ──");

            GUILayout.BeginHorizontal();

            GUILayout.Label("Tweener", GUILayout.Width(80));

            Config.TweenerCapacity = IntField(Config.TweenerCapacity, 500, 500000);

            GUILayout.Label("Sequence", GUILayout.Width(80));

            Config.SequenceCapacity = IntField(Config.SequenceCapacity, 50, 200000);

            GUILayout.EndHorizontal();

            if (GUILayout.Button("지금 적용", GUILayout.Width(120))) ApplyCapacity();



            GUILayout.Space(10);

            GUILayout.Label("── 진단 도구 ──");

            GUILayout.Label("    F6: VRAM을 무엇이 쓰는지 센다 (편집 화면에서 맵을 연 상태로 누를 것)");

            GUILayout.Label("    " + TextureCensus.LastReport);

            GUILayout.Label("    F9: 누가 메모리를 잡는지 15초 추적 (곡 재생 중에 누를 것)");

            GUILayout.Label("    " + AllocScan.LastReport);

            GUILayout.Label("    F7: 엔진 단계 + 함수별 측정 20초,  F8: GC 기능 A-B 테스트");

            GUILayout.Label("    " + LoopProfiler.LastReport);

            GUILayout.Label("    A-B: " + AbTest.Summary);

#endif

        }



        private static int IntField(int value, int min, int max)

        {

            string text = GUILayout.TextField(value.ToString(), GUILayout.Width(90));

            int parsed;

            if (int.TryParse(text, out parsed)) value = Mathf.Clamp(parsed, min, max);

            return value;

        }



        private static void OnSaveGUI(UnityModManager.ModEntry modEntry)

        {

            Config.Save(modEntry);

        }

    }



    public class Settings : UnityModManager.ModSettings

    {

#if DEV || AUTOTEST

        // 연구 세션 명령으로만 켬. 저장 값만으로 패치를 설치하지 않는다.

        public bool ArcheGpuResearch = false;

#endif

        // 로그에서 확인된 최대치(19530 tweener / 12187 sequence)보다 넉넉하게 잡는다.

        public int TweenerCapacity = 40000;

        public int SequenceCapacity = 25000;

        public bool SkipAssetUnload = true;

        public bool BeatSkipNoChange = true;  // dontChangeMySprite인 빛난 타일의 빈 OnBeat 호출 제외 (결과 동일 검증됨; 고BPM 맵 +33~42%)

        public bool SkipIdleParticles = true;  // 파티클 장식이 매 프레임 같은 크기·속도를 다시 넣는 것 건너뛰기

        public bool LeakFix = true;            // 게임 메모리 누수 막기 (사용자 지정 FPS 화면 버퍼)

        public bool LoadCache = true;          // 맵 열기·재생 시작 때 이미지 파일 수정 시각을 한 프레임에 한 번만 읽기

        public bool LegacyGfxJobs = true;   // boot.config 로 그래픽 작업 분산(legacy)을 켠다

        public bool NoGhosting = true;      // 이 게임에서만 "응답 없음" 고스트 창을 끈다 (첫 판 FPS 떨어짐 막기, WindowGhost.cs). 끄면 다음 실행부터

        public bool FrameStats = true;      // 프레임 시간 통계(FrameTimingManager, GPU 시간) 켜기. 원래 게임은 꺼져 있다. 시작할 때만 읽는다

        public int FlipModel = -1;         // 화면 출력 최신 방식(Flip, 실험): 1 켬, 0 끔, -1 아직 안 정함(처음에 지금 boot.config 상태를 따름)



        // 기능별 켜기/끄기 (플레이어용 설정 화면에서 바꾸고 저장된다)

        public bool CheckUpdates = true;    // 게임을 켜면 GitHub 에서 새 버전이 있는지 한 번 확인

        public bool GcPause = true;

        public bool GcDynamicBudget = false;

        public bool MoveEpsilon = false;

        public bool NativeMotion = false;

        public bool NativeMotionArena = false;

        public bool NativeMotionPinArena = false;

        public bool DecoFastCompact = false;

        public bool TagIndex = false;

        public bool TagIndexNativePlans = false;

        public bool ParticleCatchup = false;

        public bool MapProfilesEnabled = false;

        public bool HitchHeatmap = false;

        public int ThreadSchedulingMode = 1;   // 기본 켬(우선순위): HELLO 전곡 30ms 초과 프레임 12·20개 -> 렌더 스레드와 함께 0·1개, Arche 11 -> 3개

        public bool ThreadRenderScheduling = true;

        public bool FrameQueueDelay = false;

        public bool ThreadPerformanceCores = false;

        public bool ThreadAvoidLogicalZero = false;

        public string MapProfilesPath = "";

        public bool AsyncUploadBudget = false;

        public bool JsonStringPool = false;

        public bool AssetDiskCache = true;  // 기본 켬(내장 드라이브 C: 의 LocalAppData). 두 번째 열기부터 Windflower 11.8 -> 9.7초, HELLO 12.9 -> 11.6초. 첫 열기는 +0.1~1.2초(쓰기). 빈 공간이 모자라면 저절로 꺼진다

        public int AssetDiskCacheMB = 3072;   // 무거운 맵 하나(Windflower)가 1.6GB. 512MB 는 계속 밀어내서 적중 40%였다

        public string AssetDiskCachePath = "";

        public bool EffectSplit = true;

        public bool RecolorSplit = true;

        public bool RecolorDefer = true;    // 화면 밖 타일의 색 바꾸기 엔진 호출을 타일이 다시 켜질 때로 미룸. HELLO 12ms+ 프레임 102 -> 66~77, Arche 358 -> 288, 곡 끝 타일 상태 대조 차이 0

        public bool TweenGuard = true;

        public bool ZeroTween = true;       // 길이 0 인 즉시 이동을 애니메이션 없이 처리

        public bool SkipSameText = true;

        public bool ImagePrefetch = true;

        public bool ShaderWarm = true;

        public bool JitInline = true;       // JIT 공격적 인라인: 곡 전체 +2~5.6%

        public bool HoldSkip = true;        // 입력이 같으면 홀드 갱신 건너뛰기: Exit +4.7%, 값 대조 235만 번 다름 0

        public bool HitboxQuerySkip = false; // 히트박스 장식이 없을 때 공의 히트박스 물리 질의 건너뛰기(결과 같음)
        public bool PlayTurbo = false;        // 곡 중에만: 일시정지 공 카메라 끄기, 입력 없을 때 UI 이벤트·마우스 처리 건너뛰기, 장식 LateUpdate 같은 값 건너뛰기 등(turbo lite)
        public bool MouseEventsSkip = true;   // 곡 중 SendMouseEvents 단계를 뺀다(프레임당 0.04~0.13ms). MouseSkip.cs
        public bool SkipClearFlash = true;    // 번쩍임 사각형(전체 화면 두 장)을 재질 알파 0 이하인 프레임에는 안 그린다. FlashSkip.cs
        public bool GpuDecoDetach = true;     // GPU 장식이 켜져 있는 동안 장식 루트를 관리자에서 뗀다(Transform 계층 훑기 줄임, Arche 메인 -0.2~0.3ms). DecoDetach.cs
        public bool DecoDetachCpu = true;     // GPU 장식이 꺼진 맵에서 장식이 3,000개 넘으면 장식 루트를 관리자에서 뗀다(Windflower +4.5%, Exit +3.0%, 비트 동일 검증). DecoDetach.cs
        public bool RunLod = false;           // 곡 중에만: 그리기 순서에서 연속된 크게 확대된 이미지 장식을 1/4 해상도로 모아 그려 한 번에 합치기(GPU 병목일 때만 켜짐, 저사양 기능·손실 있음)
        public bool DisplayPace = false;    // 화면에 나갈 프레임만 그리기: 곡 전체 2~2.5배 (재시작 후 적용)

        public bool EngineMaskGrow = false; // 엔진 코드 6바이트 패치: 마스크 많은 맵 Exit +11.8%

        public bool EngineWaitSkip = true; // 엔진 코드 패치: 지난 화면 출력을 기다리지 않음. 2026-11-08 기본 켬(DefaultsGen 2), 화면 지연 미측정
        public bool FastBlend = true;       // 더하기 블렌드 장식을 화면 복사 없이 그리기

        public bool SkipInvisible = true;   // 투명도 0 인 이미지 장식은 그리지 않기

        public bool LazyHidden = true;     // 투명한 장식은 위치·회전·크기를 보일 때 반영 (SkipInvisible 필요)

        public bool InstantDirect = true;  // 길이 0 장식 이동을 게임 코드의 애니메이션 만들기 없이 처리

        public bool SkipSame = true;       // 즉시 이동 값이 이미 그대로면(투명 장식) 설정 함수를 부르지 않음

        public bool FastLoop = true;       // 길이 0 장식 이동 효과를 게임 코드 대신 모드 루프로

        public bool Precheck = true;       // 곧 발동할 무거운 장식 이동이 아무것도 안 바꾸는지 미리 확인해 두고 건너뛰기

        public bool FloorAnim = true;      // 길이 있는 타일 이동(MoveTrack)의 애니메이션을 DOTween 대신 모드가 돌림

        public bool HitTextFade = true;    // 판정 글자가 투명해질 때 메시 전체 대신 정점 색만 바꾸기

        public bool HitMeterFade = true;   // 판정 오차 막대 눈금이 사라질 때 그림을 다시 만들지 않고 렌더러 투명도만 바꾸기

        public bool HitMeterDirectKill = true;   // 판정 오차 막대 눈금·손의 트윈을 문자열 검색 대신 핸들로 종료 (원래 DOTween 과 비트 차이 0; 고BPM 맵 +11~13%)

        // 연출 끄기 (기본 꺼짐: 켜면 화면이 달라진다)

        public string EffectsOff = "";     // 끌 효과 이벤트 이름들 (쉼표로)

        public bool GameScreenButton = true;   // 에디터 재생 단추 옆 "게임 화면으로 플레이" 단추

        public float NoFxAheadSec = 1.5f;  // 노이펙: 앞 타일이 보이기 시작하는 시간(초)

        public bool NoFx = false;          // 노이펙 모드: 맵 효과 전부 + 장식 끄기 (히트박스 장식은 그대로)

        public string FiltersOff = "";     // 하나씩 끈 필터 ("SetFilter:이름" / "SetFilterAdvanced:클래스", | 로)

        public bool HideJudgeAll = false;

        public bool HideJudgePerfect = false;

        public bool NoPlayZoom = false;    // 에디터 재생 중 마우스 휠로 화면 크기 바꾸기 막기

        public bool DecoAnim = true;       // 길이 있는 장식 이동의 애니메이션을 DOTween 대신 모드가 돌림

        public bool FilterIdentity = false; // (실험) 픽셀 대조를 통과한 효과 없는 필터만 건너뛰기. HELLO 2026 에서 이득이 프레임당 0.03ms 이하라 기본 끔

        public bool FilterTypeCache = true; // 고급 필터의 형식 찾기(Type.GetType, 한 번 약 0.7ms) 결과를 기억

        public bool MeshWarm = true;        // 색 바꾸기가 곡 중에 만들 타일 메시를 재생 준비 때 미리 만들기

        public bool SoundWarm = true;

        public bool HitSoundBatch = true;   // 2026-10-20: 기본 켬 (Arche 전곡 +6.6%, 막히면 자동으로 원래 방식). 저사양 페이지에 있다

        public int DefaultsGen = 0;         // 기본값을 바꿔 한 번 덮어쓴 세대 (1 = 히트음 묶기·화면 밖 파티클 멈추기 켬, 2 = 앞서 준비 프레임 4·화면 대기 건너뛰기 켬, 3 = 확대 장식 묶어 그리기 켬, 4 = 그것을 저사양 기능으로 되돌려 끔, 5 = GPU 장식 처리 켬)

        public bool JitWarm = true;         // 모드·게임 효과 함수를 켤 때 미리 컴파일 (곡 초반 첫 효과의 JIT)       // 곡 중에 처음 쓰는 효과음(박자 소리 등)을 재생 준비 때 미리 불러오기

        // 저사양 (화면·동작이 아주 조금 달라질 수 있어 기본 꺼짐)

        public bool LowPriority = false;    // 게임 우선순위 높음

        public bool PhysicsPause = false;   // 곡 중 물리 시뮬레이션 중지(Script 모드), 기본 끔

        public bool TimerHalfMs = false;    // 타이머 0.5ms 요청(NtSetTimerResolution), 기본 끔

        public bool LowNoThrottle = false;  // 윈도우 절전 제한 끄기 + 타이머 1ms

        public int LowMenuFps = 0;          // 플레이 중이 아닐 때(메뉴·에디터) FPS 제한 (0 끔, 30, 60)

        public int CrashStreak = 0;         // 연속 비정상 종료 횟수 (2 이상이면 안전 모드)

        public bool LowNoFft = true;        // Volume 타일이 없으면 음악 주파수 분석 건너뛰기 (프레임당 0.073ms, HELLO 2026 곡 11,322프레임 중 4번만 돎. 새 설치부터 기본 켬)

        public bool LowFloorSplit = false;    // (저사양) 타일이 많은 타일 이동 효과를 여러 프레임에 나눠 처리

        public bool LowPauseParticles = true; // 화면 밖 파티클 장식 시뮬레이션 멈추기 (2026-10-20 기본 켬)

        public bool LowCompressImages = false; // (저사양) 장식 이미지를 DXT 로 압축해서 올리기 (그래픽 메모리 1/4, 여러 코어로 미리 압축)

        public int LowRenderScale = 100;    // 게임 화면(카메라) 해상도 배율 % (10~100), 100 = 원래대로

        public bool LowSharpUpscale = false; // 작게 그린 게임 화면을 선명하게(도트처럼) 늘리기

        public bool LowFsr = false;         // 작게 그린 게임 화면을 AMD FSR 1 로 늘리기 (가장자리 살리기 + 선명도 보정)

        public int LowImageCap = 0;         // 장식 이미지 최대 크기 (0 = 맵 불러오기 설정 그대로, 1024, 512)

        public bool LowSharpen = false;     // (실험) 늘린 게임 화면에 선명도 보정

        public float LowSharpenValue = 1f;  // (실험) 선명도 보정 세기 (셰이더 _Value)

        public bool LowHalfRender = false;  // (실험) 두 프레임에 한 번만 그리고 사이 프레임은 카메라만 옮기기

        public int FrameGenOutside = 0;     // (실험) 0=끔, 2..8=출력 배율. 입력·판정은 원래 속도.

        public int FrameGenMultiplier = 2;  // 마지막으로 선택한 배율

        public bool FrameGenRefresh = true; // 새 설정의 첫 추천. 기능 전체는 FrameGenOutside=0으로 기본 꺼짐.

        public bool FrameGenRefreshRest = true; // 옛 XML 호환용. 주사율 맞춤은 이 값과 무관하게 마감+저장 휴식 사용.

        public bool FrameGenConstantRing = false;

        public int FrameGenPace = 700;       // 마감 생성 간격: 한 칸의 천분율. 1000 이면 정확히 한 칸 뒤라 조금만 늦어도 칸이 비므로 700 (500~1000)

        public int FrameGenLead = 600;      // 화면 시각 맞추기: 출력이 모니터 갱신 시각에서 이만큼(마이크로초) 앞서 들어가야 그 칸에 뜬다고 보고 그림 시각을 그 칸에 맞춘다. 0 이면 끔(옛 방식)

        public bool FrameGenDense = true;       // 조밀한 다단계 광학 흐름(1/4 해상도, 4px마다 벡터 하나). 끄면 옛 8x8 블록 매칭

        public bool FrameGenQueue = false;      // (실험) 무거운 구간에서 출력을 수직동기 대기열로. 꺼 둠: 꽉 찬 대기열에서 진짜 Present 가 35~40ms 막혀 HELLO 2026 54~61초 "게임 바깥" 끊김 44번(끔 4번)

        public bool FrameGenDecoHints = false;  // (연구) 모드가 돌리는 장식의 화면 변환을 움직임 후보로 넘김 (DLSS 의 게임 모션 벡터 역할)

        public bool FrameGenDebugMarks = false; // (진단) 공 위치 표시 고리를 그린다

        public bool FrameGenBallMove = true;   // 공을 두 진짜 프레임 사이 위치(피벗 둘레 호)로 옮겨 그린다. 끄면 공은 진짜 프레임 속도로만 움직인다

        public int FrameGenBlur = 0;         // 움직임 흐림(모션 블러): 0 끔, 1 약(진짜 프레임 간격의 30%), 2 중(60%), 3 강(100%). 카메라·흐름으로 움직이는 내용만, 화면 고정 UI·공은 선명

        public int FrameGenBlend = 1;        // 못 믿는 곳의 겹침: 0 끔(가까운 진짜 그림), 1 공 둘레만 섞기, 2 전체 섞기 (움직임이 고름)

        public int FxPreset = 0;

        public List<FxSlot> FxSlots = new List<FxSlot>();

        public string FxLastOn = "";

        public bool FxColor=false, FxSharp=false, FxAA=false, FxGlow=false, FxVignette=false, FxLut=false, FxLight=false;

        public bool FxGlowStack=false, FxToneMap=false, FxRays=false, FxStreak=false, FxFlare=false;

        public bool FxChromatic=false, FxGrain=false, FxCrt=false, FxPixel=false, FxPosterize=false, FxBlur=false;

        public bool FxHdr=false, FxFilmic=false;   // HDR 느낌(국소 대비+그림자 살리기+밝은 곳 누르기), 필름 톤(ACES)

        public float FxHdrAmount=.6f, FxFilmicExposure=1.2f;

        // 더 많은 효과 (FxExtras.cs 표). 색 조정 / 디테일 / 렌즈·왜곡. 전부 기본 꺼짐.

        public bool FxSepia=false, FxDuotone=false, FxTealOrange=false, FxInvert=false, FxMono=false, FxNight=false, FxThermal=false, FxHue=false;

        public float FxSepiaAmount=.8f, FxDuotoneAmount=.8f, FxDuotoneHue=.6f, FxTealOrangeAmount=.8f, FxInvertAmount=1f, FxMonoAmount=1f, FxNightAmount=.85f, FxThermalAmount=.9f, FxHueShift=30f;

        public bool FxOutline=false, FxEmboss=false, FxHalftone=false, FxTiltShift=false, FxSoftFocus=false;

        public float FxOutlineAmount=.8f, FxEmbossAmount=.7f, FxHalftoneSize=8f, FxHalftoneAmount=.9f, FxTiltShiftAmount=.8f, FxSoftFocusAmount=.5f;

        public bool FxBarrel=false, FxRipple=false, FxGlitch=false, FxZoomBlur=false, FxMirror=false, FxKaleido=false, FxLetterbox=false;

        public float FxBarrelAmount=.3f, FxRippleAmount=.6f, FxGlitchAmount=.5f, FxZoomBlurAmount=.5f, FxKaleidoSegments=6f, FxLetterboxAspect=2.39f;

        public float FxCeiling=.97f, FxRaysAmount=.8f, FxRaysX=.5f, FxRaysY=.25f, FxRaysLength=.8f;

        public float FxStreakAmount=.5f, FxFlareAmount=.35f, FxChromaticAmount=2, FxGrainAmount=.045f;

        public float FxCrtAmount=.5f, FxPixelSize=6, FxPosterizeLevels=8, FxBlurRadius=2;

        public float FxVibrance=.2f, FxContrast=1.08f, FxBrightness=0, FxTemperature=0;

        public float FxSharpAmount=.4f, FxGlowThreshold=.75f, FxGlowAmount=.25f, FxVignetteAmount=.15f, FxLightAmount=.2f;

        public int FxLightQuality=3;

        public string FxLutPath="";

        public float FxRcasSharpness=.2f;

        public bool LowAutoRes = false;     // 자동 해상도: 목표 FPS 를 못 맞출 만큼 GPU 가 바쁠 때만 게임 화면 해상도를 낮춤

        public int LowAutoFps = 60;         // 자동 해상도 목표 FPS

        public int LowAutoMin = 50;         // 자동 해상도 최소 배율 %

        public bool ExpFsChanged = false;   // 모드가 독점 전체 화면으로 바꿔 둠 (옵션을 끄면 되돌린다)

        public int GpuDeco = 1;             // GPU 장식 처리: 기본 켬(3.0.0, DefaultsGen 5). 장식이 아주 많은 맵에서만 실제로 켜진다

        public int GpuDecoImages = 1;       // GPU 장식 처리 사용 시 이미지 소유: 기본 켬

        public int ExpFullscreen = 0;       // (실험, 설정 창 그래픽) 1 = 전체 화면일 때 독점 전체 화면 (화면 지연 절반, FPS 약 -10%, 찢어짐)

        public int MaxQueuedFrames = 4;     // (실험) 앞서 준비하는 프레임 수, 0 = 게임 값 그대로

        public int LowSplit = 0;            // 효과 몰림 나누기 세기: 0 기본(10ms, 400칸), 1 잘게(5ms, 200칸), 2 아주 잘게(3ms, 120칸)

        public string ReopenLevel = "";     // 재시작 버튼으로 껐을 때 다시 켠 뒤 에디터로 열 맵 (한 번 쓰고 비움)

        public string LastEditorLevel = ""; // 에디터에서 마지막으로 연 맵 (메인 메뉴에서도 "마지막 맵으로 재시작" 하려고)

        public int TuneVer = 0;              // PC 맞춤 자동 설정을 한 버전 (PcTune.Version 보다 작으면 메뉴에서 한 번 재고 적용)

        public string TuneBackup = "";       // PC 맞춤으로 바꾸기 전 값 ("키=값;..."), 되돌리기용

        public string TuneNotice = "";       // 처음 자동으로 바꾼 것 이름들 (홈 화면 알림, 닫으면 비움)

        public string TuneLast = "";         // 마지막 PC 맞춤 측정 요약 (설정 창 표시용)

        public bool MoveFinish = true;     // 장식 위치 계산 줄이기 (마무리 묶기, 같은 값 건너뛰기, 편집기 작업 건너뛰기, LateUpdate 에 맡기기)

        public bool DormantSkip = true;    // 매 프레임 장식 순회에서 바뀔 일 없는 장식과 히트박스 없는 장식 빼기

        public int ImageMaxSide = -1;       // 큰 이미지 줄이기: 0 끔, -1 자동(VRAM 이 모자랄 때만), 4096, 2048 (긴 변 기준)

        public int ImageAutoVer = 0;        // 1.2.2 에서 "끔" 이던 설정을 한 번 "자동" 으로 옮겼는지

        public string VramCaps = "";        // 자동: VRAM 부족으로 끊긴 맵과 다음부터 쓸 한도 ("경로 탭 한도" 줄들)

        public string Language = "";   // "" = 윈도우 언어를 따름, "ko", "en"

        public KeyCode WindowKey = KeyCode.Insert;     // 설정 창 열기/닫기

        public int WindowMods = 0;                     // Hotkey.Shift/Ctrl/Alt 조합

        public KeyCode OverlayKey = KeyCode.Insert;    // 실시간 모니터 표시 방식 바꾸기

        public int OverlayMods = Hotkey.Shift;

        public bool ShowOverlay = false;   // 예전 설정 (켜져 있었으면 아이콘 모드로 옮긴다)



        // 실시간 모니터: 0 끔, 1 아이콘(화면 끝의 작은 탭), 2 미니(한 줄), 3 상세(패널). Shift+키로 차례로 바꾼다.

        public int OverlayMode = 1;

        public int OverlayFpsSource = -1;   // -1 자동(생성 켬: 출력, 끔: 원본), 0 원본, 1 출력, 2 둘 다. 통계·끊김은 항상 원본.

        public bool OverlayRight = false;   // 왼쪽 끝 / 오른쪽 끝

        public float OverlayY = 0.5f;       // 세로 위치 (0 위 ~ 1 아래)

        public float OverlayOpacity = 0.75f;

        public float OverlayScale = 1f;

        public bool OvCpu = true, OvGpu = true, OvVram = true, OvRam = true, OvGc = true, OvGraph = true, OvHitchList = true;

        public bool OvSession = true;       // 상세: 이번 곡 통계

        // 아이콘/미니에 FPS 말고 더 보여 줄 것

        public bool CmMs = true, CmLow = false, CmCpu = false, CmGpu = true, CmVram = true, CmRam = false;

        public float AlertMs = 33f;         // 이보다 긴 프레임만 알린다 (2프레임 이상 밀린 것)

        public bool AlertDetailed = false;  // 알림: 간단(한 줄) / 자세히(설명 포함 카드)

        public int AlertPos = 0;            // 알림 위치: 0 모니터 옆, 1 화면 위 가운데, 2 화면 아래 가운데

        public bool HitchAlerts = true;    // 끊기면 원인 알림   // 따로 뜨는 설정 창 (F10 은 윈도우 창 메뉴 키라 피한다)



        public override void Save(UnityModManager.ModEntry modEntry) { MapProfiles.Save(this,()=>Save(this, modEntry)); }

    }

}

