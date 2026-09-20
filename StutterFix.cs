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
        private static bool tweensPaused;

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
            // 맵이 프레임 제한 이벤트로 targetFrameRate를 낮추는 경우가 있다.
            // 0보다 큰 값이 설정되어 있으면 매 프레임 덮어써서 맵의 제한을 무시한다.
            if (Config != null && Config.ForceFrameRate > 0 && Application.targetFrameRate != Config.ForceFrameRate)
            {
                Application.targetFrameRate = Config.ForceFrameRate;
            }

            // F9: 애니메이션 멈춤/재생 토글, F10: 자동 A/B 테스트 시작/종료
            if (Input.GetKeyDown(KeyCode.F9))
            {
                tweensPaused = !tweensPaused;
                try { if (tweensPaused) DOTween.PauseAll(); else DOTween.PlayAll(); } catch { }
                Entry.Logger.Log(tweensPaused ? "tweens paused (F9)" : "tweens resumed (F9)");
            }
            if (Input.GetKeyDown(KeyCode.F7)) LoopProfiler.Toggle();
            if (Input.GetKeyDown(KeyCode.F9) == false && Input.GetKeyDown(KeyCode.F8))
            {
                AbTest.Toggle();
            }

            GcControl.Tick(dt);
            EventSpread.Tick(dt);
            ColorDefer.Tick(dt);
            LoopProfiler.Tick(dt);
            AbTest.Tick(dt);
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
            GUILayout.Label("── 곡 중 GC 멈춤 (핵심 기능) ──");
            GcControl.Enabled = GUILayout.Toggle(GcControl.Enabled, "  곡을 플레이하는 동안 GC를 멈춘다");
            GUILayout.Label("    " + GcControl.Status);
            GUILayout.Label("    측정: 끊김(33ms 초과) 118구간 -> 12구간, 평균 106 -> 124fps");

            GUILayout.Space(10);
            GUILayout.Label("── GC 상태 ──");
            GUILayout.Label("    " + GcTest.Status);

            GUILayout.Space(10);
            GUILayout.Label("── A/B 실험 대상 선택 (F8로 실행) ──");
            GUILayout.BeginHorizontal();
            foreach (Experiments.Target t in Enum.GetValues(typeof(Experiments.Target)))
            {
                if (t == Experiments.Target.None) continue;
                bool sel = Experiments.Selected == t;
                if (GUILayout.Button((sel ? "> " : "  ") + t, GUILayout.Width(110)))
                {
                    Experiments.Selected = t;
                    Experiments.Install(t);
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("    " + Experiments.Status);

            GUILayout.Space(10);
            GUILayout.Label("── 짧은 색 애니메이션 생략 (권장) ──");
            if (GUILayout.Button(TweenSkip.Enabled ? "생략 끄기" : "생략 켜기", GUILayout.Width(180)))
            {
                TweenSkip.Install();
                TweenSkip.Enabled = !TweenSkip.Enabled;
                TweenSkip.ResetStats();
            }
            GUILayout.BeginHorizontal();
            GUILayout.Label("기준 길이 " + TweenSkip.Threshold.ToString("F2") + "초 이하", GUILayout.Width(170));
            TweenSkip.Threshold = GUILayout.HorizontalSlider(TweenSkip.Threshold, 0f, 2f, GUILayout.Width(200));
            GUILayout.EndHorizontal();
            GUILayout.Label("    " + TweenSkip.Status);

            GUILayout.Space(10);
            GUILayout.Label("── 화면 밖 타일 색칠 미루기 (실험) ──");
            if (GUILayout.Button(ColorDefer.Enabled ? "미루기 끄기" : "미루기 켜기", GUILayout.Width(180)))
            {
                ColorDefer.Install();
                ColorDefer.Enabled = !ColorDefer.Enabled;
                ColorDefer.ResetStats();
                if (!ColorDefer.Enabled) ColorDefer.FlushAll();
            }
            ColorDefer.NoTweenMode = GUILayout.Toggle(ColorDefer.NoTweenMode, "  미루지 않고 화면 밖 애니메이션만 생략 (권장)");
            GUILayout.Label("    " + ColorDefer.Status);

            GUILayout.Space(10);
            GUILayout.Label("── 타일 색칠 중복 제거 (실험) ──");
            if (GUILayout.Button(ColorSkip.Enabled ? "중복 검사 끄기" : "중복 검사 켜기", GUILayout.Width(180)))
            {
                ColorSkip.Install();
                ColorSkip.Reset();
                ColorSkip.Enabled = !ColorSkip.Enabled;
            }
            ColorSkip.CountOnly = GUILayout.Toggle(ColorSkip.CountOnly, "  세기만 하고 건너뛰지 않음");
            GUILayout.Label("    " + ColorSkip.Status);

            GUILayout.Space(10);
            GUILayout.Label("── 엔진 단계별 측정 (F7) ──");
            if (GUILayout.Button(LoopProfiler.Running ? "엔진 측정 끄기" : "엔진 측정 켜기", GUILayout.Width(180)))
            {
                LoopProfiler.Toggle();
            }
            GUILayout.Label("    " + LoopProfiler.LastReport);

            GUILayout.Space(10);
            GUILayout.Label("── 애니메이션 일시정지 (진단) ──");
            int playing = 0;
            try { playing = DOTween.TotalPlayingTweens(); } catch { }
            GUILayout.Label($"    재생 중인 tween: {playing}개");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("전부 멈추기", GUILayout.Width(120)))
            {
                try { DOTween.PauseAll(); Entry.Logger.Log($"paused all tweens ({playing})"); } catch (Exception ex) { Entry.Logger.Error(ex.Message); }
            }
            if (GUILayout.Button("다시 재생", GUILayout.Width(120)))
            {
                try { DOTween.PlayAll(); Entry.Logger.Log("resumed all tweens"); } catch (Exception ex) { Entry.Logger.Error(ex.Message); }
            }
            GUILayout.EndHorizontal();
            GUILayout.Label("    멈춘 동안 FPS가 오르면 애니메이션 처리가 병목입니다. 화면은 멈춰 보입니다.");
            GUILayout.Label("    단축키: F9 = 멈춤/재생 토글,  F8 = 자동 A/B 테스트 시작/종료");
            GUILayout.Label($"    자동 A/B: {(AbTest.Running ? "진행 중" : "정지")} — {AbTest.Summary}");

            GUILayout.Space(10);
            GUILayout.Label("── 맵의 프레임 제한 무시 ──");
            GUILayout.Label($"    현재 targetFrameRate: {Application.targetFrameRate} (-1이면 제한 없음)");
            GUILayout.BeginHorizontal();
            GUILayout.Label("강제할 값 (0 = 끔)", GUILayout.Width(140));
            Config.ForceFrameRate = IntField(Config.ForceFrameRate, 0, 1000);
            GUILayout.EndHorizontal();
            GUILayout.Label("    맵이 연출용으로 프레임을 낮추는 경우 이 값으로 덮어씁니다. 180 정도를 넣어보세요.");

            GUILayout.Space(10);
            GUILayout.Label("── 화면 밖 장식 컬링 (실험) ──");
            bool cull = GUILayout.Toggle(Culling.Enabled, "  화면 밖 스프라이트 렌더러 끄기");
            if (cull != Culling.Enabled)
            {
                Culling.Enabled = cull;
                if (!cull) Culling.RestoreAll();
            }
            bool mesh = GUILayout.Toggle(Culling.IncludeMeshes, "  타일(메시)도 컬링 — 화면이 크게 망가짐, 진단 전용");
            if (mesh != Culling.IncludeMeshes)
            {
                Culling.RestoreAll();
                Culling.IncludeMeshes = mesh;
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
        // 0이면 맵의 프레임 제한을 그대로 둔다. 0보다 크면 그 값으로 강제한다.
        public int ForceFrameRate = 0;

        public override void Save(UnityModManager.ModEntry modEntry)
        {
            Save(this, modEntry);
        }
    }
}
