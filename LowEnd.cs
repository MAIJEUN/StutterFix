using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 저사양 모드: 다른 기능과 달리 "완전히 똑같이" 가 원칙이 아니다. 약한 컴퓨터에서 한 프레임이라도 더 짜내려고
    // 게임 밖 설정(우선순위, 절전)이나 거의 안 보이는 차이를 감수하는 기능을 모아 둔다. 전부 기본 꺼짐.
    internal static class LowEnd
    {
        internal static bool Priority, NoThrottle, NoFft;
        private static bool priorityOn, throttleOn, timerOn, installed;
        private static ProcessPriorityClass priorityBefore = ProcessPriorityClass.Normal;

        // ── 1. 게임 우선순위 ──
        // 백그라운드 프로그램(브라우저, 방송 프로그램, 업데이트)이 CPU 를 가져갈 때 게임이 먼저 돌게 한다.
        // "높음" 까지만 쓴다(실시간은 소리·입력 드라이버까지 밀어내 위험).
        private static void ApplyPriority()
        {
            if (Priority == priorityOn) return;
            try
            {
                // 끌 때는 "보통" 이 아니라 켜기 전 값으로 되돌린다 (Quartz 의 '프로세스 우선순위 높이기' 같은 다른 모드 설정을 덮지 않게)
                var p = Process.GetCurrentProcess();
                if (Priority) { priorityBefore = p.PriorityClass; p.PriorityClass = ProcessPriorityClass.High; }
                else p.PriorityClass = priorityBefore;
                priorityOn = Priority;
                Main.Entry.Logger.Log("[저사양] 게임 우선순위 " + (Priority ? "높음" : "원래대로 (" + priorityBefore + ")"));
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[저사양] 우선순위 바꾸기 실패: " + ex.Message); }
        }

        // ── 2. 윈도우 절전 제한 끄기 + 타이머 1ms ──
        // 윈도우 11 은 창이 뒤에 있거나 절전 모드일 때 프로세스를 "효율 모드"(EcoQoS)로 느린 코어·낮은 클럭에 몰아넣는다.
        // 노트북에서 프레임 간격이 들쭉날쭉해지는 원인. 이 게임 프로세스만 제한에서 뺀다. 타이머 정밀도 1ms 는 프레임 대기가 덜 튀게 한다.
        [StructLayout(LayoutKind.Sequential)]
        private struct PowerThrottling { public uint Version, ControlMask, StateMask; }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottling info, int size);
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
        private const int ProcessPowerThrottlingClass = 4;   // PROCESS_INFORMATION_CLASS.ProcessPowerThrottling
        private const uint ExecutionSpeed = 1;               // PROCESS_POWER_THROTTLING_EXECUTION_SPEED

        private static void ApplyThrottle()
        {
            if (NoThrottle == throttleOn) return;
            try
            {
                // 켤 때: 제한을 끈다고 명시(ControlMask 에 넣고 StateMask 는 0). 끌 때: 윈도우가 알아서 하게(ControlMask 0).
                var p = new PowerThrottling { Version = 1, ControlMask = NoThrottle ? ExecutionSpeed : 0u, StateMask = 0u };
                bool ok = SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottlingClass, ref p, Marshal.SizeOf(typeof(PowerThrottling)));
                if (NoThrottle && !timerOn) { timeBeginPeriod(1); timerOn = true; }
                else if (!NoThrottle && timerOn) { timeEndPeriod(1); timerOn = false; }
                throttleOn = NoThrottle;
                Main.Entry.Logger.Log("[저사양] 윈도우 절전 제한 " + (NoThrottle ? "끔" : "윈도우에 맡김") + (ok ? "" : " (절전 제한 설정 실패, 윈도우 10 이전일 수 있음)") + ", 타이머 1ms " + (timerOn ? "켬" : "끔"));
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[저사양] 절전 제한 바꾸기 실패: " + ex.Message); }
        }

        // ── 3. 음악 반응 계산(FFT) 끄기 ──
        // 게임은 매 프레임 음악 주파수 분석(scrVolumeTrackerFloat.Update, GetSpectrumData)을 한다. 이 값을 읽는 곳은 게임 전체에서
        // scrFloor.Update 의 "Volume" 타일 색 방식뿐이다(IL 확인). 그 방식을 쓰는 타일이 없으면 통째로 건너뛴다.
        // 곡 도중 타일이 Volume 으로 바뀌면(ColorFloor) 바로 다시 켜고, 그 밖의 경로를 위해 30프레임마다 타일을 훑는다.
        // 차이: 타일이 Volume 으로 바뀌는 첫 프레임 하나만 이전 값으로 칠해질 수 있다.
        private static bool volumeSeen;
        private static int scanFrame = -1000;
        internal static long FftSkipped, FftRun;

        public static bool TrackerPrefix()
        {
            if (!NoFft) return true;
            if (Time.frameCount - scanFrame >= 30) { scanFrame = Time.frameCount; if (!volumeSeen) volumeSeen = AnyVolumeFloor(); }
            if (volumeSeen) { FftRun++; return true; }
            FftSkipped++;
            return false;
        }
        public static void ColorFloorPrefix(TrackColorType __0) { if (__0 == TrackColorType.Volume) volumeSeen = true; }
        internal static void SongStarted() { volumeSeen = false; scanFrame = -1000; EnsureSharpen(); }   // 맵마다 새로 본다

        private static readonly AccessTools.FieldRef<scrFloor, TrackColorType> colorTypeRef = AccessTools.FieldRefAccess<scrFloor, TrackColorType>("specialColorType");
        private static bool AnyVolumeFloor()
        {
            try
            {
                var lm = scrLevelMaker.instance;
                var list = lm == null ? null : lm.listFloors;
                if (list == null) return true;   // 모르면 켜 둔다
                for (int i = 0; i < list.Count; i++) { var f = list[i]; if ((object)f != null && colorTypeRef(f) == TrackColorType.Volume) return true; }
                return false;
            }
            catch { return true; }
        }

        // ── 4. 게임 화면 해상도 낮추기 ──
        // 플레이 중 게임은 카메라 3개(정적 배경, 움직이는 배경, 본 화면)를 화면 크기 텍스처(scrCamera.camRT) 한 장에 그린 뒤
        // 사각형(quad)에 붙여 화면에 낸다(scnGame.Play 가 항상 SetupRTCam(true), IL 확인). 이 텍스처를 배율만큼 작게 만들면
        // 게임 화면만 낮은 해상도로 그리고 늘려서 보여 준다. UI(HUD, 설정 창)는 화면에 직접 그려지므로 선명하게 남는다.
        // 게임 코드: Update 가 camRTNeedsRecreation(크기가 Screen 과 다르면 true)일 때 new RenderTexture(Screen.width, Screen.height, 24).
        // 두 곳에서 Screen 크기 대신 배율을 곱한 크기를 쓰게 한다. 배율 100% 면 원래와 똑같다.
        internal static int RenderScalePct = 100;
        private static readonly AccessTools.FieldRef<scrCamera, RenderTexture> camRTRef = AccessTools.FieldRefAccess<scrCamera, RenderTexture>("camRT");
        public static int RTWidth() { int p = EffectivePct; return p >= 100 ? Screen.width : Mathf.Max(64, Mathf.RoundToInt(Screen.width * p / 100f)); }
        public static int RTHeight() { int p = EffectivePct; return p >= 100 ? Screen.height : Mathf.Max(64, Mathf.RoundToInt(Screen.height * p / 100f)); }
        public static bool NeedsRecreationPrefix(scrCamera __instance, ref bool __result)
        {
            var rt = camRTRef(__instance);
            __result = rt == null || rt.width != RTWidth() || rt.height != RTHeight();
            return false;
        }
        // Update 안 new RenderTexture(Screen.width, Screen.height, 24) 의 두 크기만 바꾼다 (quad 비율 계산에 쓰는 Screen 크기는 그대로)
        public static System.Collections.Generic.IEnumerable<CodeInstruction> CamUpdateTranspiler(System.Collections.Generic.IEnumerable<CodeInstruction> ins)
        {
            var list = new System.Collections.Generic.List<CodeInstruction>(ins);
            var gw = AccessTools.PropertyGetter(typeof(Screen), "width");
            var gh = AccessTools.PropertyGetter(typeof(Screen), "height");
            var ctor = AccessTools.Constructor(typeof(RenderTexture), new[] { typeof(int), typeof(int), typeof(int) });
            var at = new System.Collections.Generic.List<int>();
            for (int i = 0; i + 3 < list.Count; i++)
                if (list[i].Calls(gw) && list[i + 1].Calls(gh) && list[i + 3].opcode == System.Reflection.Emit.OpCodes.Newobj && Equals(list[i + 3].operand, ctor)) at.Add(i);
            int done = at.Count;
            if (done == 1)   // 정확히 한 곳일 때만 바꾼다 (모양이 다르면 아무것도 안 건드림)
            {
                list[at[0]].operand = AccessTools.Method(typeof(LowEnd), nameof(RTWidth));
                list[at[0] + 1].operand = AccessTools.Method(typeof(LowEnd), nameof(RTHeight));
            }
            RenderScaleReady = done == 1;
            if (done != 1) Main.Entry.Logger.Log("[저사양] 게임 화면 해상도: 게임 코드 모양이 예상과 달라 적용하지 않음 (찾은 곳 " + done + ")");
            return list;
        }
        internal static bool RenderScaleReady;

        // ── 동적 해상도 (자동 해상도) ──
        // 목표 FPS 를 못 맞출 만큼 그래픽카드가 바쁠 때만 게임 화면 해상도를 낮추고, 여유가 생기면 다시 올린다.
        // 기준은 GPU 시간뿐이다(CPU 가 한계라 느린 것은 해상도로 안 풀린다). 해상도를 바꿀 때마다 게임이 텍스처를 다시 만들므로
        // 10% 단위로, 내릴 때는 1.5초, 올릴 때는 3초 간격 이상으로만 바꾼다. 텍스처 일부에만 그리는 방식은 유니티 필터가
        // 카메라 영역을 무시해 필터 많은 맵에서 깨질 수 있어 쓰지 않는다. 슬라이더 값이 최대, AutoMinPct 가 최소.
        internal static bool AutoRes;
        internal static int AutoTargetFps = 60, AutoMinPct = 50, AutoPct = 100;
        internal static long AutoDown, AutoUp;
        private static Unity.Profiling.ProfilerRecorder gpuRec;
        private static float gpuEma, nextChange;
        internal static float GpuEma { get { return gpuEma; } }
        internal static int EffectivePct { get { return AutoRes ? Math.Min(AutoPct, RenderScalePct) : RenderScalePct; } }
        internal static void AutoTick()
        {
            if (!AutoRes || !Hitch.Playing || !RenderScaleReady) { AutoPct = RenderScalePct; gpuEma = 0f; return; }
            try
            {
                if (!gpuRec.Valid) gpuRec = Unity.Profiling.ProfilerRecorder.StartNew(Unity.Profiling.ProfilerCategory.Internal, "GPU Frame Time");
                float g = gpuRec.LastValue / 1e6f;
                if (g <= 0f || g > 500f) return;   // 값이 없거나 멈춘 프레임
                gpuEma = gpuEma <= 0f ? g : gpuEma * 0.92f + g * 0.08f;
                float now = Time.realtimeSinceStartup;
                if (now < nextChange) return;
                float budget = 1000f / Mathf.Max(30, AutoTargetFps);
                int max = RenderScalePct, min = Mathf.Min(AutoMinPct, max);
                if (gpuEma > budget * 0.85f && AutoPct > min) { AutoPct = Math.Max(min, AutoPct - 10); nextChange = now + 1.5f; AutoDown++; EnsureSharpen(); }
                else if (gpuEma < budget * 0.55f && AutoPct < max) { AutoPct = Math.Min(max, AutoPct + 10); nextChange = now + 3f; AutoUp++; EnsureSharpen(); }
            }
            catch { }
        }


        // 게임 화면을 작게 그렸을 때 늘리는 방식: 부드럽게(기본, Bilinear) / 선명하게(도트처럼, Point). 배율 100% 면 건드리지 않는다.
        internal static bool SharpUpscale;
        public static void CamUpdatePostfix(scrCamera __instance)
        {
            if (EffectivePct >= 100) return;
            var rt = camRTRef(__instance);
            if (rt == null) return;
            var fm = SharpUpscale ? FilterMode.Point : FilterMode.Bilinear;
            if (rt.filterMode != fm) rt.filterMode = fm;
        }
        // 장식 이미지 최대 크기 (저사양): 켜면 "맵 불러오기" 페이지 설정보다 작은 쪽을 쓴다
        internal static int ImageCap;
        internal static int CombinedMaxSide(int pageSetting)
        {
            if (ImageCap <= 0) return pageSetting;
            return pageSetting > 0 ? Math.Min(pageSetting, ImageCap) : ImageCap;
        }


        // ── 실험: 늘린 화면 선명도 보정 ──
        // FSR 1 은 "가장자리를 살려 늘리기(EASU) + 선명도 보정(RCAS)" 두 단계다. 새 셰이더는 유니티 에디터로 번들을 만들어야 해서,
        // 게임에 이미 들어 있는 Sharpen 필터 셰이더(CameraFilterPack/Sharpen_Sharpen, SetFilter 의 Sharpen)를 빌려
        // 늘린 뒤의 화면(OverlayCam 출력)에 한 번 건다. UI 는 카메라 뒤에 그려지므로 영향이 없다.
        // 셰이더 값: _Value(필터 기본 4), _Value2(기본 1), _ScreenResolution(가로, 세로). 필터 컴포넌트 코드(IL)와 같은 값을 넣는다.
        internal static bool Sharpen;
        internal static float SharpenValue = 1f;   // _Value (0.5~4)
        internal static bool SharpenReady;
        internal static Material SharpenMat;
        private static bool sharpenTried;
        private static readonly AccessTools.FieldRef<scrCamera, Camera> overlayRef = AccessTools.FieldRefAccess<scrCamera, Camera>("Overlaycam");
        internal static void EnsureSharpen()
        {
            try
            {
                if (!sharpenTried)
                {
                    sharpenTried = true;
                    var sh = Shader.Find("CameraFilterPack/Sharpen_Sharpen");
                    if (sh != null) { SharpenMat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave }; SharpenReady = true; }
                    Main.Entry.Logger.Log("[저사양] 선명도 보정 셰이더 " + (SharpenReady ? "찾음" : "없음 (사용 불가)"));
                }
                var cam = scrCamera.instance;
                var oc = cam == null ? null : overlayRef(cam);
                if (oc == null) return;
                var comp = oc.GetComponent<UpscaleSharpen>();
                bool want = Sharpen && SharpenReady && EffectivePct < 100 && !Fsr.Enabled;   // FSR 은 선명도 보정(RCAS)을 이미 한다
                if (comp == null) { if (!want) return; comp = oc.gameObject.AddComponent<UpscaleSharpen>(); }
                if (comp.enabled != want) comp.enabled = want;
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[저사양] 선명도 보정 붙이기 실패: " + ex.Message); }
        }
        internal static long SharpenFrames;

        internal static void Install(Harmony h)
        {
            if (installed) return;
            installed = true;
            try
            {
                var tu = AccessTools.Method(typeof(scrVolumeTrackerFloat), "Update");
                if (tu != null) h.Patch(tu, prefix: new HarmonyMethod(typeof(LowEnd), nameof(TrackerPrefix)));
                foreach (var m in typeof(scrFloor).GetMethods(AccessTools.all))
                    if (m.Name == "ColorFloor" && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(TrackColorType))
                        h.Patch(m, prefix: new HarmonyMethod(typeof(LowEnd), nameof(ColorFloorPrefix)));
                var cu = AccessTools.Method(typeof(scrCamera), "Update");
                if (cu != null) h.Patch(cu, transpiler: new HarmonyMethod(typeof(LowEnd), nameof(CamUpdateTranspiler)), postfix: new HarmonyMethod(typeof(LowEnd), nameof(CamUpdatePostfix)));
                var nr = AccessTools.PropertyGetter(typeof(scrCamera), "camRTNeedsRecreation");
                if (nr != null && RenderScaleReady) h.Patch(nr, prefix: new HarmonyMethod(typeof(LowEnd), nameof(NeedsRecreationPrefix)));
                var gf = AccessTools.PropertyGetter(typeof(RDUtils), "targetFrameRate");
                if (gf != null) h.Patch(gf, postfix: new HarmonyMethod(typeof(LowEnd), nameof(GameFpsPostfix)));
                Main.Entry.Logger.Log("[저사양] 설치 (게임 화면 해상도 " + (RenderScaleReady ? "사용 가능" : "사용 불가") + ")");
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[저사양] 설치 실패: " + ex.Message); }
        }

        internal static void Apply()
        {
            ApplyPriority();
            ApplyThrottle();
            EnsureSharpen();
        }

        // ── 메뉴·에디터 FPS 제한 ──
        // 플레이 중이 아닐 때(메뉴, 에디터 편집, 맵 고르기) Application.targetFrameRate 를 이 값으로 묶는다. 노트북 발열과 전기를 줄여
        // 플레이할 때 열 때문에 느려지는 것(쓰로틀링)을 덜어 준다. 게임은 이 값을 켤 때와 설정 메뉴에서만 바꾼다(RDUtils.targetFrameRate, IL 확인).
        // 게임이 읽는 값(RDUtils.targetFrameRate)은 묶기 전 값을 돌려줘 설정 메뉴가 원래 값을 보이고 저장하게 한다.
        // 맵을 불러오는 중(장면이 바뀐 뒤 3초, 이미지 미리 풀기 중)에는 묶지 않는다. 수직동기가 켜져 있으면 유니티가 이 값을 쓰지 않는다.
        internal static int MenuFps;          // 0 = 끔
        private static bool fpsCapped;
        private static int savedFps;
        private static int lastScene = -1;
        private static float sceneAt;
        internal static void MenuCapTick()
        {
            if (MenuFps <= 0 && !fpsCapped) return;
            try
            {
                int sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle;
                float now = Time.realtimeSinceStartup;
                if (sc != lastScene) { lastScene = sc; sceneAt = now; }
                bool want = MenuFps > 0 && !Hitch.Playing && !ImagePrefetch.Running && now - sceneAt > 3f;
                if (want)
                {
                    int cur = Application.targetFrameRate;
                    if (!fpsCapped) { savedFps = cur; fpsCapped = true; }
                    else if (cur != MenuFps) savedFps = cur;   // 그사이 게임(설정 메뉴)이 바꿨다
                    bool lower = savedFps > 0 && savedFps <= MenuFps;   // 원래 더 낮으면 그대로 둔다
                    int target = lower ? savedFps : MenuFps;
                    if (cur != target) Application.targetFrameRate = target;
                }
                else if (fpsCapped)
                {
                    fpsCapped = false;
                    if (Application.targetFrameRate != savedFps) Application.targetFrameRate = savedFps;
                }
            }
            catch { }
        }
        public static void GameFpsPostfix(ref int __result) { if (fpsCapped) __result = savedFps; }

        // 모드를 끄거나 다시 불러올 때 원래대로
        internal static void Shutdown()
        {
            if (fpsCapped) { fpsCapped = false; try { Application.targetFrameRate = savedFps; } catch { } }
            bool p = Priority, t = NoThrottle, s = Sharpen;
            Sharpen = false; EnsureSharpen(); Sharpen = s;
            Priority = false; NoThrottle = false;
            ApplyPriority(); ApplyThrottle();
            Priority = p; NoThrottle = t;
        }

        // ── 그래픽 설정 기록 (다음 저사양 옵션을 고르는 근거) ──
        private static bool renderLogged;
        internal static void LogRenderOnce()
        {
            if (renderLogged) return;
            renderLogged = true;
            try
            {
                var sb = new System.Text.StringBuilder("[저사양] 그래픽 설정: ");
                sb.AppendFormat("품질 단계 {0}, 안티에일리어싱 {1}x, 수직동기 {2}, 그림자 {3}, 텍스처 {4}, 이방성 {5}, 파티클 레이캐스트 {6}, 소프트 파티클 {7}",
                    QualitySettings.names[QualitySettings.GetQualityLevel()], QualitySettings.antiAliasing, QualitySettings.vSyncCount,
                    QualitySettings.shadows, QualitySettings.globalTextureMipmapLimit, QualitySettings.anisotropicFiltering, QualitySettings.particleRaycastBudget, QualitySettings.softParticles);
                foreach (var c in Camera.allCameras)
                    sb.AppendFormat(" | 카메라 {0}: MSAA {1}, HDR {2}, 경로 {3}, 대상 {4}, 깊이 {5}, 컬링 {6:X}",
                        c.name, c.allowMSAA, c.allowHDR, c.actualRenderingPath, c.targetTexture != null ? c.targetTexture.width + "x" + c.targetTexture.height : "화면", c.depth, c.cullingMask);
                sb.AppendFormat(" | 화면 {0}x{1} {2}, 그래픽카드 {3}", Screen.width, Screen.height, Screen.fullScreenMode, SystemInfo.graphicsDeviceName);
                Main.Entry.Logger.Log(sb.ToString());
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[저사양] 그래픽 설정 기록 실패: " + ex.Message); }
        }

        internal static string Summary()
        {
            if (!NoFft && !Priority && !NoThrottle && RenderScalePct >= 100 && !HalfRender.Enabled && !AutoRes) return "";
            string rt = ""; try { var cam = scrCamera.instance; var t = cam == null ? null : camRTRef(cam); if (t != null) rt = ", 게임 화면 " + t.width + "x" + t.height; } catch { }
            return string.Format(" | 저사양: 우선순위 {0}, 절전 제한 끔 {1}, 음악 반응 계산 건너뜀 {2}번 (돈 것 {3}번), 해상도 배율 {4}%{5}, 선명도 보정 {6}프레임",
                priorityOn ? "높음" : "보통", throttleOn, FftSkipped, FftRun, RenderScalePct, rt, SharpenFrames) + (AutoRes ? string.Format(", 자동 해상도: 목표 {0} FPS, 내림 {1}번, 올림 {2}번, 끝났을 때 {3}%", AutoTargetFps, AutoDown, AutoUp, AutoPct) : "") + Fsr.Summary() + HalfRender.Summary() + RenderVerify.Summary();
        }
    }
}

namespace StutterFix
{
    // OverlayCam(낮은 해상도 게임 화면을 늘려 그리는 카메라)에 붙는 선명도 보정. 켜져 있을 때만 enabled.
    internal class UpscaleSharpen : MonoBehaviour
    {
        private void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            var m = LowEnd.SharpenMat;
            if (m == null) { Graphics.Blit(src, dst); return; }
            m.SetFloat("_TimeX", 1f);
            m.SetVector("_ScreenResolution", new Vector4(src.width, src.height, 0f, 0f));
            m.SetFloat("_Value", LowEnd.SharpenValue);
            m.SetFloat("_Value2", 1f);
            Graphics.Blit(src, dst, m);
            LowEnd.SharpenFrames++;
        }
    }
}
