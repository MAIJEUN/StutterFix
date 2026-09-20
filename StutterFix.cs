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
    // 얼불춤 고사양 맵의 프레임 멈춤 두 가지를 줄이는 모드.
    //
    // 1) DOTween 용량 재할당
    //    로그에 "Max Tweens reached: capacity has automatically been increased ..."가 반복된다.
    //    그때마다 메인 스레드에서 배열을 새로 만들고 복사하느라 프레임이 밀린다.
    //    처음부터 큰 용량을 잡아두면 재할당이 일어나지 않는다.
    //
    // 2) 맵 로딩 시 에셋 정리 (Resources.UnloadUnusedAssets)
    //    오브젝트가 56만 개인 상태에서 한 번에 200ms 넘게 메인 스레드를 멈춘다.
    //    scnGame.Awake / scnGame.LoadLevel / scnEditor.SwitchToEditMode 세 곳에서 호출하는데,
    //    반환값을 바로 버리므로(pop) 호출을 건너뛰어도 게임 로직에는 영향이 없다.
    //    대신 정리를 안 하므로 메모리 사용량은 늘어난다.
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
            }
            catch (Exception ex)
            {
                modEntry.Logger.Error("harmony patch failed: " + ex);
            }
            return true;
        }

        private static void PatchUnloadCallers(Harmony harmony)
        {
            var transpiler = new HarmonyMethod(typeof(Main), nameof(UnloadTranspiler));
            var targets = new List<MethodBase>();

            var scnGame = AccessTools.TypeByName("scnGame");
            if (scnGame != null)
            {
                targets.Add(AccessTools.Method(scnGame, "LoadLevel"));
                targets.Add(AccessTools.Method(scnGame, "Awake"));
            }

            foreach (var m in targets)
            {
                if (m == null) continue;
                harmony.Patch(m, transpiler: transpiler);
                Entry.Logger.Log("patched " + m.DeclaringType.Name + "." + m.Name);
            }
        }

        // Resources.UnloadUnusedAssets() 호출을 우리 함수 호출로 바꾼다.
        public static IEnumerable<CodeInstruction> UnloadTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            var original = AccessTools.Method(typeof(Resources), nameof(Resources.UnloadUnusedAssets), new Type[0]);
            var replacement = AccessTools.Method(typeof(Main), nameof(MaybeUnloadUnusedAssets));

            foreach (var ins in instructions)
            {
                // 붙어 있는 라벨과 예외 블록을 유지하려고 명령어를 새로 만들지 않고 피연산자만 바꾼다.
                if (ins.opcode == OpCodes.Call && ReferenceEquals(ins.operand, original))
                {
                    ins.operand = replacement;
                }
                yield return ins;
            }
        }

        public static AsyncOperation MaybeUnloadUnusedAssets()
        {
            if (Config != null && Config.SkipAssetUnload)
            {
                skippedUnloads++;
                Entry.Logger.Log($"skipped Resources.UnloadUnusedAssets (총 {skippedUnloads}회)");
                return null; // 호출부에서 바로 버리는 값이라 null이어도 안전하다.
            }
            Entry.Logger.Log("passing through Resources.UnloadUnusedAssets (기능 꺼짐)");
            return Resources.UnloadUnusedAssets();
        }

        private static void OnUpdate(UnityModManager.ModEntry modEntry, float dt)
        {
            Profiler.Tick(dt);
            Culling.Tick(dt);
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
            GUILayout.Label("고사양 맵의 프레임 멈춤을 줄입니다.");
            GUILayout.Space(8);

            Config.SkipAssetUnload = GUILayout.Toggle(
                Config.SkipAssetUnload,
                $"  맵 로딩 시 에셋 정리 건너뛰기 (지금까지 {skippedUnloads}회 건너뜀)");
            GUILayout.Label("    로딩 멈춤이 줄어드는 대신 메모리 사용량이 늘어납니다.");

            GUILayout.Space(10);
            GUILayout.Label("── 진단용 프로파일러 ──");
            if (GUILayout.Button(Profiler.Running ? "프로파일러 끄기" : "프로파일러 켜기", GUILayout.Width(180)))
            {
                if (Profiler.Running) Profiler.Stop(); else Profiler.Start();
            }
            GUILayout.Label("    켠 뒤 무거운 구간을 지나가면 함수별 소요 시간이 로그에 기록됩니다.");
            GUILayout.Label("    최근: " + Profiler.LastReport);

            GUILayout.Space(10);
            GUILayout.Label("── 화면 밖 장식 컬링 (실험) ──");
            bool cull = GUILayout.Toggle(Culling.Enabled, "  화면 밖 스프라이트 렌더러 끄기");
            if (cull != Culling.Enabled)
            {
                Culling.Enabled = cull;
                if (!cull) Culling.RestoreAll();
            }
            bool deact = GUILayout.Toggle(Culling.DeactivateObjects, "  오브젝트를 통째로 비활성화 (더 강력, 실험용)");
            if (deact != Culling.DeactivateObjects)
            {
                Culling.RestoreAll();
                Culling.DeactivateObjects = deact;
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("여유 범위 (화면 배수)", GUILayout.Width(160));
            GUILayout.Label(Culling.Margin.ToString("F1"), GUILayout.Width(40));
            Culling.Margin = GUILayout.HorizontalSlider(Culling.Margin, 1.0f, 4.0f, GUILayout.Width(200));
            GUILayout.EndHorizontal();
            GUILayout.Label("    " + Culling.Status);
            GUILayout.Label("    화면 밖 장식이 사라져 보이면 여유 범위를 키우세요.");

            GUILayout.Space(6);
            Profiler.AutoScan = GUILayout.Toggle(Profiler.AutoScan, "  씬 스캔 (5초마다 스프라이트/텍스처 수 기록)");
            GUILayout.Label("    스캔할 때마다 순간 멈춤이 생깁니다. 확인 후 꺼주세요.");
            GUILayout.Label("    최근: " + SceneScan.LastResult);

            GUILayout.Space(10);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Tweener 용량", GUILayout.Width(140));
            Config.TweenerCapacity = IntField(Config.TweenerCapacity, 500, 500000);
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Sequence 용량", GUILayout.Width(140));
            Config.SequenceCapacity = IntField(Config.SequenceCapacity, 50, 200000);
            GUILayout.EndHorizontal();

            if (GUILayout.Button("용량 지금 적용", GUILayout.Width(140)))
            {
                ApplyCapacity();
            }
            GUILayout.Label(capacityApplied ? "상태: 적용됨" : "상태: 대기 중 (시작 3초 후 자동 적용)");
        }

        private static int IntField(int value, int min, int max)
        {
            string text = GUILayout.TextField(value.ToString(), GUILayout.Width(120));
            int parsed;
            if (int.TryParse(text, out parsed))
            {
                value = Mathf.Clamp(parsed, min, max);
            }
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

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }
    }
}
