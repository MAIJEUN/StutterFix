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

            InstallAll();
            if (LaunchWarning.Length > 0) modEntry.Logger.Log(LaunchWarning.Trim());
            return true;
        }

        // UMM에서 모드를 끄고 켤 때 불린다.
        // 예전에는 true 만 돌려주고 아무것도 안 해서, 꺼도 패치와 GC 멈춤이 그대로 살아 있었다.
        // 이제 끄면 다시 불러오기 때와 똑같이 전부 풀고, 켜면 다시 건다.
        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            if (value) InstallAll(); else UninstallAll();
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
                    if (args.Contains("-force-d3d12")) found.Add("-force-d3d12");
                    if (args.Contains("-force-gfx-jobs")) found.Add("-force-gfx-jobs");
                    if (found.Count > 0)
                        launchWarning = "  ⚠ Steam 실행 옵션에 " + string.Join(", ", found.ToArray()) +
                                        " 가 있습니다. VRAM이 빠듯하면 박자마다 60~80ms씩 멈춥니다. 빼는 것을 권합니다.";
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
                PatchUnloadCallers(harmony);
                TextFix.Install(harmony);
                EffectScan.Install(harmony);
                TweenFix.Install(harmony);
                RenderWatch.Install(harmony);
                RenderCallbackScan.Install(harmony);
                FrameRateScreenWatch.Install(harmony);
                ParticleTextWatch.Install(harmony);
                GcControl.Install();
                Entry.Logger.Log("켜짐: 패치 설치 완료");
            }
            catch (Exception ex)
            {
                Entry.Logger.Error("harmony patch failed: " + ex);
            }
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
            Try(EffectBudget.Reset);
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
                reload.Invoke(Entry, null);
            }
            catch (Exception ex)
            {
                Entry.Logger.Error("다시 불러오기 실패: " + ex);
            }
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float dt)
        {
            // Ctrl+F5: 게임을 켠 채로 새 DLL을 불러온다. 옛 코드는 여기서 바로 빠져나가야 한다.
            if ((Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) && Input.GetKeyDown(KeyCode.F5))
            {
                RequestReload();
                return;
            }

            if (!installed) return;   // 꺼져 있으면 아무 일도 하지 않는다

            GcControl.Tick(dt);
            EffectBudget.Tick();
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

            if (capacityApplied) return;

            // DOTween 초기화 이후에 적용해야 해서 잠시 기다린다.
            elapsed += dt;
            if (elapsed < 3f) return;
            ApplyCapacity();
        }

        internal static void ApplyCapacity()
        {
            capacityApplied = true;
            ModWatch.Install();   // 다른 모드들이 다 올라온 뒤에 감싼다
            SamplerWatch.Install();
            PhaseWatch.Install();  // 끊긴 프레임의 범인을 단계 단위로 지목하려면 항상 켜져 있어야 한다
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

        private static void OnGUI(UnityModManager.ModEntry modEntry)
        {
            GUILayout.Label("고사양 맵의 프레임 문제를 줄입니다. 효과가 측정된 기능만 들어 있습니다.");
            if (LaunchWarning.Length > 0) GUILayout.Label(LaunchWarning);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("모드 다시 불러오기 (Ctrl+F5)", GUILayout.Width(220))) RequestReload();
            GUILayout.Label("  게임을 켠 채로 새로 빌드한 DLL을 적용합니다. 설정 슬라이더는 기본값으로 돌아갑니다.");
            GUILayout.EndHorizontal();

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

        public override void Save(UnityModManager.ModEntry modEntry) { Save(this, modEntry); }
    }
}
