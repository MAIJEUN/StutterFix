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

        // ── 1. 게임 우선순위 ──
        // 백그라운드 프로그램(브라우저, 방송 프로그램, 업데이트)이 CPU 를 가져갈 때 게임이 먼저 돌게 한다.
        // "높음" 까지만 쓴다(실시간은 소리·입력 드라이버까지 밀어내 위험).
        private static void ApplyPriority()
        {
            if (Priority == priorityOn) return;
            try
            {
                Process.GetCurrentProcess().PriorityClass = Priority ? ProcessPriorityClass.High : ProcessPriorityClass.Normal;
                priorityOn = Priority;
                Main.Entry.Logger.Log("[저사양] 게임 우선순위 " + (Priority ? "높음" : "보통"));
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
        internal static void SongStarted() { volumeSeen = false; scanFrame = -1000; WrapFilters(); }   // 맵마다 새로 본다

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
        public static int RTWidth() { return RenderScalePct >= 100 ? Screen.width : Mathf.Max(64, Mathf.RoundToInt(Screen.width * RenderScalePct / 100f)); }
        public static int RTHeight() { return RenderScalePct >= 100 ? Screen.height : Mathf.Max(64, Mathf.RoundToInt(Screen.height * RenderScalePct / 100f)); }
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

        // ── 5. 화면 필터 끄기 / 블룸 끄기 ──
        // 필터(SetFilter 로 켜지는 CameraFilterPack 계열)와 블룸(VideoBloom 등)은 카메라의 OnRenderImage 에서 화면 전체를
        // 한 번 더 그리는 후처리라, 그래픽카드가 약하면 가장 비싸다. 켜면 그리지 않고 들어온 화면을 그대로 넘긴다(Graphics.Blit).
        // 게임의 VideoBloom 도 조건이 안 맞을 때 이렇게 그대로 넘긴다(IL 확인). 화면 타일·스크롤·극좌표·블렌드 모드는
        // 맵 연출 자체라 건드리지 않는다.
        // 필터 종류가 300개가 넘어 전부 미리 감싸면 시작이 느려진다. 옵션이 켜져 있을 때 곡 시작마다 카메라에 실제로 붙은
        // 종류만 감싼다(처음 한 번만, 곡 시작 연출 중).
        internal static bool NoFilters, NoBloom;
        internal static long FilterSkips, BloomSkips;
        private static Harmony harmony;
        private static readonly System.Collections.Generic.HashSet<Type> wrapped = new System.Collections.Generic.HashSet<Type>();
        public static bool FilterPrefix(RenderTexture __0, RenderTexture __1)
        {
            if (!NoFilters) return true;
            Graphics.Blit(__0, __1); FilterSkips++;
            return false;
        }
        public static bool BloomPrefix(RenderTexture __0, RenderTexture __1)
        {
            if (!NoBloom) return true;
            Graphics.Blit(__0, __1); BloomSkips++;
            return false;
        }
        private static int Kind(Type t)
        {
            string n = t.Name;
            if (n.StartsWith("CameraFilterPack", StringComparison.Ordinal)) return 1;
            if (n.IndexOf("Bloom", StringComparison.Ordinal) >= 0) return 2;
            return 0;
        }
        internal static void WrapFilters()
        {
            if (harmony == null || (!NoFilters && !NoBloom)) return;
            try
            {
                int added = 0; var sw = Stopwatch.StartNew();
                foreach (var cam in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    foreach (var mb in cam.GetComponents<MonoBehaviour>())
                    {
                        if (mb == null) continue;
                        var t = mb.GetType();
                        int k = Kind(t);
                        if (k == 0 || (k == 1 && !NoFilters) || (k == 2 && !NoBloom) || wrapped.Contains(t)) continue;
                        var m = AccessTools.DeclaredMethod(t, "OnRenderImage", new[] { typeof(RenderTexture), typeof(RenderTexture) });
                        wrapped.Add(t);
                        if (m == null) continue;
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(LowEnd), k == 1 ? nameof(FilterPrefix) : nameof(BloomPrefix)));
                        added++;
                    }
                }
                if (added > 0) Main.Entry.Logger.Log("[저사양] 필터·블룸 " + added + "종 감쌈 (" + sw.ElapsedMilliseconds + "ms)");
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[저사양] 필터 감싸기 실패: " + ex.Message); }
        }

        internal static void Install(Harmony h)
        {
            if (installed) return;
            installed = true;
            harmony = h;
            try
            {
                var tu = AccessTools.Method(typeof(scrVolumeTrackerFloat), "Update");
                if (tu != null) h.Patch(tu, prefix: new HarmonyMethod(typeof(LowEnd), nameof(TrackerPrefix)));
                foreach (var m in typeof(scrFloor).GetMethods(AccessTools.all))
                    if (m.Name == "ColorFloor" && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(TrackColorType))
                        h.Patch(m, prefix: new HarmonyMethod(typeof(LowEnd), nameof(ColorFloorPrefix)));
                var cu = AccessTools.Method(typeof(scrCamera), "Update");
                if (cu != null) h.Patch(cu, transpiler: new HarmonyMethod(typeof(LowEnd), nameof(CamUpdateTranspiler)));
                var nr = AccessTools.PropertyGetter(typeof(scrCamera), "camRTNeedsRecreation");
                if (nr != null && RenderScaleReady) h.Patch(nr, prefix: new HarmonyMethod(typeof(LowEnd), nameof(NeedsRecreationPrefix)));
                Main.Entry.Logger.Log("[저사양] 설치 (게임 화면 해상도 " + (RenderScaleReady ? "사용 가능" : "사용 불가") + ")");
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[저사양] 설치 실패: " + ex.Message); }
        }

        internal static void Apply()
        {
            ApplyPriority();
            ApplyThrottle();
            WrapFilters();
        }

        // 모드를 끄거나 다시 불러올 때 원래대로
        internal static void Shutdown()
        {
            bool p = Priority, t = NoThrottle;
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
            if (!NoFft && !Priority && !NoThrottle && !NoFilters && !NoBloom && RenderScalePct >= 100) return "";
            string rt = ""; try { var cam = scrCamera.instance; var t = cam == null ? null : camRTRef(cam); if (t != null) rt = ", 게임 화면 " + t.width + "x" + t.height; } catch { }
            return string.Format(" | 저사양: 우선순위 {0}, 절전 제한 끔 {1}, 음악 반응 계산 건너뜀 {2}번 (돈 것 {3}번), 해상도 배율 {4}%{5}, 필터 건너뜀 {6}번, 블룸 건너뜀 {7}번",
                priorityOn ? "높음" : "보통", throttleOn, FftSkipped, FftRun, RenderScalePct, rt, FilterSkips, BloomSkips);
        }
    }
}
