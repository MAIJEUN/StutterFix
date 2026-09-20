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
                GcControl.Install();
            }
            catch (Exception ex)
            {
                modEntry.Logger.Error("harmony patch failed: " + ex);
            }
            return true;
        }

        // scnGame.LoadLevel / Awake 안의 Resources.UnloadUnusedAssets 호출을 가로챈다.
        // 세 호출 지점 모두 반환값을 바로 버리므로(IL에서 call 다음이 pop) 건너뛰어도 로직에 영향이 없다.
        private static void PatchUnloadCallers(Harmony harmony)
        {
            var transpiler = new HarmonyMethod(typeof(Main), nameof(UnloadTranspiler));
            var scnGame = AccessTools.TypeByName("scnGame");
            if (scnGame == null) return;

            foreach (var name in new[] { "LoadLevel", "Awake" })
            {
                var m = AccessTools.Method(scnGame, name);
                if (m == null) continue;
                harmony.Patch(m, transpiler: transpiler);
                Entry.Logger.Log("patched scnGame." + name);
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
            AbTest.Tick(dt);
            LoopProfiler.Tick(dt);
            Profiler.Tick(dt);

            if (Input.GetKeyDown(KeyCode.F7)) LoopProfiler.Toggle();
            if (Input.GetKeyDown(KeyCode.F8)) AbTest.Toggle();

            if (capacityApplied) return;

            // DOTween 초기화 이후에 적용해야 해서 잠시 기다린다.
            elapsed += dt;
            if (elapsed < 3f) return;
            ApplyCapacity();
        }

        internal static void ApplyCapacity()
        {
            capacityApplied = true;
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
            GUILayout.Label("    측정: 평균 106 -> 124fps (A-B 125쌍)");
            GUILayout.Label("    " + GcControl.Status);
            GUILayout.BeginHorizontal();
            GUILayout.Label("전체 정리 기준 " + GcControl.HardLimitMB + "MB", GUILayout.Width(180));
            GcControl.HardLimitMB = (int)GUILayout.HorizontalSlider(GcControl.HardLimitMB, 1000f, 8000f, GUILayout.Width(200));
            GUILayout.EndHorizontal();

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
