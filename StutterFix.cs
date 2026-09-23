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
        internal static UnityModManager.ModEntry Entry;
        internal static Settings Config;

        private static bool capacityApplied;
        private static float elapsed;
        private static int skippedUnloads;

        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Entry = modEntry;
            Config = UnityModManager.ModSettings.Load<Settings>(modEntry);

            modEntry.OnGUI = OnGUI;
            modEntry.OnSaveGUI = OnSaveGUI;
            modEntry.OnUpdate = OnUpdate;
            modEntry.OnToggle = OnToggle;
            modEntry.OnUnload = Unload;   // 이것이 있어야 UMM이 게임을 켠 채로 새 DLL을 다시 불러온다

            ApplyConfig();
            if (Edition.Dev) LogLoadedCopies();
            InstallAll();
            BootConfig.Apply(Config.LegacyGfxJobs);
            modEntry.Logger.Log(BootConfig.Describe());
            if (LaunchWarning.Length > 0) modEntry.Logger.Log(LaunchWarning.Trim());
            return true;
        }

        // UMM에서 모드를 끄고 켤 때 불린다.
        // 예전에는 true 만 돌려주고 아무것도 안 해서, 꺼도 패치와 GC 멈춤이 그대로 살아 있었다.
        // 이제 끄면 다시 불러오기 때와 똑같이 전부 풀고, 켜면 다시 건다.
        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            if (value) InstallAll(); else UninstallAll();
            // 모드를 끄면 게임 파일도 원래대로 돌려놓는다(다음 실행부터 원래 방식).
            // 다시 불러오기도 내부에서 끄기 -> 켜기를 거치므로 그때는 건드리지 않는다
            // (중간에 실패하면 legacy 줄이 빠진 채로 남았다).
            if (!reloading) BootConfig.Apply(value && Config.LegacyGfxJobs);
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

        private static void InstallAll()
        {
            if (installed) return;
            installed = true;
            capacityApplied = false;   // 모드별 감시, 단계 표시 같은 늦은 설치도 다시 하게 한다
            elapsed = 0f;
            try
            {
                var harmony = new Harmony(Entry.Info.Id);
                // 실제 수정 (두 버전 공통)
                PatchUnloadCallers(harmony);
                TextFix.Install(harmony);
                EffectScan.Install(harmony);   // 효과 나누기가 이 패치를 통해 돈다
                RecolorSplit.Install(harmony);
                ZeroTween.Install(harmony);
                MoveApply.Install(harmony);
                ImagePrefetch.Install(harmony);
                FastBlend.Install(harmony);
                InvisibleSkip.Install(harmony);
                TweenFix.Install(harmony);
                GcControl.Install();
                SettingsWindow.Create();
                PerfOverlay.Create();
                Try(() => PerfOverlay.Install(harmony));

                // 측정 (개발자용만)
                if (Edition.Dev)
                {
                    StartProbe.Install(harmony);
                    Try(() => FilterTrace.Install(harmony));
                    Try(() => MergeProbe.Install(harmony));

                    RenderWatch.Install(harmony);
                    RenderCallbackScan.Install(harmony);
                    FrameRateScreenWatch.Install(harmony);
                    ParticleTextWatch.Install(harmony);
                }
                Entry.Logger.Log("켜짐: 패치 설치 완료 (" + Edition.Name + ")");
            }
            catch (Exception ex)
            {
                Entry.Logger.Error("harmony patch failed: " + ex);
            }
            if (Edition.Dev) VerifyPatches();
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
                new[] { "scnGame", "LoadLevel" },
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
            Try(GcControl.Shutdown);
            Try(RenderWatch.Shutdown);
            Try(PhaseWatch.Uninstall);
            Try(LoopProfiler.Shutdown);
            Try(AllocScan.Shutdown);
            Try(Profiler.Stop);
            Try(ModWatch.Shutdown);
            Try(SamplerWatch.Shutdown);
            Try(EffectBudget.Reset);       // 색 나누기 대기열도 같이 비운다
            Try(ImagePrefetch.Stop);       // 이미지 작업 스레드와 풀어 둔 메모리
            Try(() => SystemMonitor.Keep = false);
            Try(FastBlend.Uninstall);      // 바꿔 끼운 블렌드 장식 재질을 원래대로
            Try(InvisibleSkip.Uninstall);   // 그리기에서 뺀 투명 장식을 되돌린다
            Try(SettingsWindow.Destroy);
            Try(PerfOverlay.Destroy);
            Try(ParticleTextWatch.Shutdown);
            Try(RenderCallbackScan.Shutdown);
            Try(SlowScan.Shutdown);

            foreach (var id in HarmonyIds)
                Try(() => new Harmony(id).UnpatchAll(id));

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

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float dt)
        {
            // Ctrl+F5: 게임을 켠 채로 새 DLL을 불러온다. 옛 코드는 여기서 바로 빠져나가야 한다.
            if (Edition.Dev && (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(KeyCode.F5))
            {
                RequestReload();
                return;
            }

            if (!installed) return;   // 꺼져 있으면 아무 일도 하지 않는다

            GcControl.Tick(dt);
            EffectBudget.Tick();
            RecolorSplit.Tick();
            FastBlend.Tick();
            MoveApply.Tick();
            VramGuard.Tick();
            ImagePrefetch.LogAfterLoad();

            if (Edition.Dev)
            {
                RenderWatch.Tick(dt);
                ModWatch.Tick(dt);
                AllocScan.Tick(dt);
                AbTest.Tick(dt);
                LoopProfiler.Tick(dt);
                Profiler.Tick(dt);

                if (Input.GetKeyDown(KeyCode.F7)) LoopProfiler.Toggle();
                if (Input.GetKeyDown(KeyCode.F8)) AbTest.Toggle();
                if (Input.GetKeyDown(KeyCode.F9)) AllocScan.Toggle();
                if (Input.GetKeyDown(KeyCode.F6)) TextureCensus.Run();
                BlendProbe.Tick();
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
            if (Edition.Dev)
            {
                ModWatch.Install();   // 다른 모드들이 다 올라온 뒤에 감싼다
                SamplerWatch.Install();
                PhaseWatch.Install();  // 끊긴 프레임의 범인을 단계 단위로 지목하려면 항상 켜져 있어야 한다
            }
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

        private static void ApplyToggles()
        {
            GcControl.Enabled = Config.GcPause;
            EffectBudget.Enabled = Config.EffectSplit;
            RecolorSplit.Enabled = Config.RecolorSplit;
            TweenFix.Enabled = Config.TweenGuard;
            ZeroTween.Enabled = Config.ZeroTween;
            MoveApply.Enabled = Config.ZeroTween;   // 같은 스위치로 실험 (즉시 이동 최적화)
            TextFix.SkipSameText = Config.SkipSameText;
            ImagePrefetch.Enabled = Config.ImagePrefetch;
            ShaderWarm.Enabled = Config.ShaderWarm;
            FastBlend.Enabled = Config.FastBlend;
            InvisibleSkip.Enabled = Config.SkipInvisible;
            if (!InvisibleSkip.Enabled) InvisibleSkip.RestoreAll();
            ImagePrefetch.MaxSide = Config.ImageMaxSide;
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
                : SettingsWindow.T("   끊김이나 오류가 있었다면 눌러서 생긴 zip 파일을 디스코드 <b>narooh</b> 에게 DM 으로 보내 주세요.", "   After a stutter or error, press it and send the zip file to <b>narooh</b> on Discord (DM).");
            GUILayout.Label(lr, GUILayout.Height(30));
            GUILayout.EndHorizontal();
        }

        private static void DevGUI()
        {
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
            if (legacy != Config.LegacyGfxJobs) { Config.LegacyGfxJobs = legacy; BootConfig.Apply(legacy); Config.Save(Entry); }
            GUILayout.Label("    " + (BootConfig.Status.Length > 0 ? BootConfig.Status : BootConfig.Describe()));
            GUILayout.Label("    측정: D3D11 프레임 140 -> 160, 곡 전체 끊김 150번대 -> 93번 (-force-gfx-jobs legacy 와 같은 효과)");

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
                "  끊길 때 어느 게임 함수가 느렸는지도 찍는다 (곡 시작이 4초 느려집니다)");

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
        // 로그에서 확인된 최대치(19530 tweener / 12187 sequence)보다 넉넉하게 잡는다.
        public int TweenerCapacity = 40000;
        public int SequenceCapacity = 25000;
        public bool SkipAssetUnload = true;
        public bool LegacyGfxJobs = true;   // boot.config 로 그래픽 작업 분산(legacy)을 켠다

        // 기능별 켜기/끄기 (플레이어용 설정 화면에서 바꾸고 저장된다)
        public bool GcPause = true;
        public bool EffectSplit = true;
        public bool RecolorSplit = true;
        public bool TweenGuard = true;
        public bool ZeroTween = true;       // 길이 0 인 즉시 이동을 애니메이션 없이 처리
        public bool SkipSameText = true;
        public bool ImagePrefetch = true;
        public bool ShaderWarm = true;
        public bool FastBlend = true;       // 더하기 블렌드 장식을 화면 복사 없이 그리기
        public bool SkipInvisible = true;   // 투명도 0 인 이미지 장식은 그리지 않기
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

        public override void Save(UnityModManager.ModEntry modEntry) { Save(this, modEntry); }
    }
}
