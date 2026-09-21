using System;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 그리는 쪽에서 막히는 끊김을 들여다본다.
    //
    // 28~33초의 끊김은 성격이 다르다. 게임 코드는 2ms뿐이고
    // PostLateUpdate/FinishFrameRendering 이 70ms다. 즉 화면을 그리다가 막힌다.
    // 게다가 32.7초, 33.7초처럼 1초 간격으로 반복된다.
    //
    // 그릴 때 비싼 것은 대개 셋 중 하나다.
    //   1) 카메라를 줌아웃해서 한 번에 그릴 것이 많아짐
    //   2) 화면 전체를 다시 그리는 효과(필터)가 여러 겹 켜짐
    //   3) 블렌드 모드를 쓰는 물체가 많아짐 (각각 화면을 한 번씩 더 읽는다)
    //
    // 셋 다 숫자로 볼 수 있으므로 끊긴 순간의 값을 같이 남긴다.
    public static class RenderWatch
    {
        private static Camera cam;
        internal static int BlendModeCount;
        private static int blendThisFrame;

        // 필터를 전부 꺼도 박자마다 75ms가 그대로였다. 필터는 범인이 아니다.
        // 그리기 안에서 어디가 막히는지 쪼갠다.
        //   걸러내기(컬링) : 화면 안에 무엇이 들어오는지 고르는 시간. 물체 수에 비례한다.
        //   그리기 준비     : 고른 것을 그래픽 카드에 넘기는 시간. 그리는 개수에 비례한다.
        // 둘 다 짧은데 프레임이 길면 그래픽 카드가 실제로 바쁜 것이고, 그때는 그릴 양을 줄이는 수밖에 없다.
        internal static float CullMs, SubmitMs;
        private static long preCullStamp, preRenderStamp;

        private static void OnPreCull(Camera c)
        {
            if (c != cam) return;
            preCullStamp = System.Diagnostics.Stopwatch.GetTimestamp();
        }

        private static void OnPreRender(Camera c)
        {
            if (c != cam) return;
            preRenderStamp = System.Diagnostics.Stopwatch.GetTimestamp();
            if (preCullStamp != 0)
                CullMs = (preRenderStamp - preCullStamp) * 1000f / System.Diagnostics.Stopwatch.Frequency;
        }

        private static void OnPostRender(Camera c)
        {
            if (c != cam || preRenderStamp == 0) return;
            SubmitMs = (System.Diagnostics.Stopwatch.GetTimestamp() - preRenderStamp) * 1000f / System.Diagnostics.Stopwatch.Frequency;
        }

        // 카메라 이벤트는 정적이라, 해제하지 않으면 다시 불러온 뒤에도 옛 코드가 계속 불린다.
        internal static void Shutdown()
        {
            Camera.onPreCull -= OnPreCull;
            Camera.onPreRender -= OnPreRender;
            Camera.onPostRender -= OnPostRender;
        }

        internal static void Install(Harmony harmony)
        {
            Camera.onPreCull += OnPreCull;
            Camera.onPreRender += OnPreRender;
            Camera.onPostRender += OnPostRender;

            try
            {
                var type = AccessTools.TypeByName("BlendModeEffect");
                if (type == null) return;
                var update = AccessTools.Method(type, "Update");
                if (update == null) return;

                // 켜져 있는 것만 Update 가 불리므로, 프레임당 호출 수가 곧 화면에 걸린 개수다.
                harmony.Patch(update, prefix: new HarmonyMethod(typeof(RenderWatch), nameof(CountBlend)));

                // 화면 크기 버퍼를 새로 잡는 순간도 센다.
                foreach (var m in typeof(RenderTexture).GetMethods(AccessTools.all))
                {
                    if (m.Name != "GetTemporary" || m.ContainsGenericParameters) continue;
                    try { harmony.Patch(m, prefix: new HarmonyMethod(typeof(RenderWatch), nameof(CountRt))); }
                    catch { }
                }

                Main.Entry.Logger.Log("render watch installed");
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("render watch 실패: " + ex.Message);
            }
        }

        // 예전에는 여기에 블렌드 장식을 강제로 끄는 실험 스위치가 있었다.
        // 컴포넌트를 enabled=false 로 끄기만 하고 스위치를 꺼도 되살리지 않아서,
        // 한 번 써 본 뒤로는 그 판 내내 블렌드 효과가 꺼진 채 남는 버그가 있었다. 원인도 아니었으므로 지웠다.
        public static void CountBlend()
        {
            blendThisFrame++;
        }

        internal static void EndFrame()
        {
            BlendModeCount = blendThisFrame;
            blendThisFrame = 0;
            TempRtThisFrame = rtCounter;
            rtCounter = 0;
        }

        // 같은 지점에서 두 판 연속 끊긴다. 일회성 비용이 아니라 그 순간의 그리기가 무거운 것이다.
        // 프레임마다 카메라 효과의 켜짐/꺼짐을 지켜보다가, 끊긴 프레임 근처에서 바뀐 것을 알려준다.
        private static Behaviour[] camFx = new Behaviour[0];
        private static bool[] wasOn = new bool[0];
        private static float refreshTimer;
        private static string lastChange = "없음";
        private static float sinceChange = 999f;

        // 필터 전환은 끊김과 상관이지 인과가 아니었다(다 꺼도 끊김 그대로). 로그만 불리므로 기본으로 끈다.
        internal static bool LogFilterChanges;

        internal static void Tick(float dt)
        {
            try
            {
                refreshTimer += dt;
                if (cam == null || refreshTimer >= 1f)
                {
                    refreshTimer = 0f;
                    if (cam == null) cam = Camera.main;
                    if (cam == null) return;
                    var found = cam.GetComponents<Behaviour>();
                    if (found.Length != camFx.Length)
                    {
                        camFx = found;
                        wasOn = new bool[found.Length];
                        for (int i = 0; i < found.Length; i++)
                            wasOn[i] = found[i] != null && found[i].enabled;
                    }
                }

                sinceChange += dt;
                for (int i = 0; i < camFx.Length; i++)
                {
                    var b = camFx[i];
                    if (b == null) continue;
                    bool on = b.enabled;
                    if (on == wasOn[i]) continue;
                    wasOn[i] = on;
                    lastChange = b.GetType().Name + (on ? " 켜짐" : " 꺼짐");
                    sinceChange = 0f;
                    // 필터가 바뀌는 순간이 비싼 것인지, 그 박자에 우연히 겹친 것인지 가리려면
                    // 끊기지 않은 전환도 전부 봐야 한다.
                    if (LogFilterChanges && GcControl.Paused)
                        Main.Entry.Logger.Log(string.Format("[필터] {0} | 이 프레임 {1:F0}ms | 버퍼 {2}개 | 켜진 효과 {3}개",
                            lastChange, Hitch.LastFrameMs, TempRtThisFrame, CountOn()));
                }
            }
            catch { }
        }

        private static int CountOn()
        {
            int n = 0;
            foreach (var b in camFx)
            {
                if (b == null || !b.enabled || b is Camera) continue;
                n++;
            }
            return n;
        }

        // 걸러내기 0.2ms인데 Camera.Render 안에서 69ms를 멈춘다.
        // CPU가 그릴 목록을 만드느라 바쁜 것이 아니라 그래픽 카드가 밀려서 기다리는 모양이다.
        // 유니티가 GPU 시간을 직접 알려주는 창구가 있으므로 그 값으로 확인한다.
        private static UnityEngine.FrameTiming[] timings = new UnityEngine.FrameTiming[1];

        internal static string GpuInfo()
        {
            try
            {
                UnityEngine.FrameTimingManager.CaptureFrameTimings();
                uint n = UnityEngine.FrameTimingManager.GetLatestTimings(1, timings);
                if (n == 0) return "GPU 시간 못 읽음";
                var t = timings[0];
                return string.Format("CPU {0:F1}ms, GPU {1:F1}ms", t.cpuFrameTime, t.gpuFrameTime);
            }
            catch { return "GPU 시간 못 읽음"; }
        }

        internal static string Info()
        {
            try
            {
                if (cam == null) cam = Camera.main;
                if (cam == null) return "카메라 없음";

                int fx = 0;
                foreach (var b in camFx)
                {
                    if (b == null || !b.enabled) continue;
                    if (b is Camera) continue;
                    fx++;
                }

                return string.Format("카메라 크기 {0:F1}, 효과 {1}개, 블렌드 물체 {2}개, 버퍼 {3}개 | 걸러내기 {4:F1}ms, 그리기 준비 {5:F1}ms",
                    cam.orthographicSize, fx, BlendModeCount, TempRtThisFrame, CullMs, SubmitMs) + " | " + GpuInfo();
            }
            catch { return "?"; }
        }

        // 화면 크기 버퍼를 새로 잡으면 그래픽 카드 쪽에서 한 프레임이 통째로 밀릴 수 있다.
        internal static int TempRtThisFrame;
        private static int rtCounter;

        public static void CountRt()
        {
            rtCounter++;
        }
    }
}
