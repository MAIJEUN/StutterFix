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
            modEntry.OnToggle = (e, value) => true;

            try
            {
                var harmony = new Harmony(modEntry.Info.Id);
                PatchUnloadCallers(harmony);
                TextFix.Install(harmony);
                EffectScan.Install(harmony);
                GcControl.Install();
            }
            catch (Exception ex)
            {
                modEntry.Logger.Error("harmony patch failed: " + ex);
            }
            return true;
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

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float dt)
        {
            GcControl.Tick(dt);
            EffectBudget.Tick();
            ModWatch.Tick(dt);
            AllocScan.Tick(dt);
            AbTest.Tick(dt);
            LoopProfiler.Tick(dt);
            Profiler.Tick(dt);

            if (Input.GetKeyDown(KeyCode.F7)) LoopProfiler.Toggle();
            if (Input.GetKeyDown(KeyCode.F8)) AbTest.Toggle();
            if (Input.GetKeyDown(KeyCode.F9)) AllocScan.Toggle();

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

            GUILayout.Space(10);
            GUILayout.Label("── 끊김 기록 ──");
            Hitch.Enabled = GUILayout.Toggle(Hitch.Enabled, "  끊긴 프레임을 기록한다 (곡이 끝나면 정리해서 보여줌)");
            GUILayout.BeginHorizontal();
            GUILayout.Label("기준 " + (int)Hitch.ThresholdMs + "ms", GUILayout.Width(200));
            Hitch.ThresholdMs = (int)GUILayout.HorizontalSlider(Hitch.ThresholdMs, 16f, 100f, GUILayout.Width(200));
            GUILayout.EndHorizontal();
            GUILayout.Label("    " + Hitch.Summary);
            GUILayout.Label("    모드별 사용량: " + ModWatch.Summary);
            SlowScan.Enabled = GUILayout.Toggle(SlowScan.Enabled, "  끊길 때 어느 게임 함수가 느렸는지도 같이 찍는다");

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
