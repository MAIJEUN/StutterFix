using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // (개발자용) 자동 시험: 모드 폴더의 autotest.txt 를 한 줄씩 해서, 사람이 직접 누르지 않아도 맵 열기·재생·재기·끄기를 한다.
    // Claude 가 게임을 직접 켜서(steam://rungameid/977950) 여러 번 되풀이해 재려고 만들었다(첫 판 FPS, Play 시간 등).
    // 결과는 평소 로그([곡], [재시작 시간], [화면 대기], [로딩 검증] ...)에 그대로 남고, 단계마다 "[자동 시험]" 줄을 적는다.
    //
    // 명령 (한 줄에 하나, # 은 주석):
    //   open <맵 파일 경로>   에디터로 가서 맵을 연다 (다 열릴 때까지 기다림)
    //   auto on|off          자동 플레이
    //   play                 에디터 재생 (scnEditor.Play)
    //   stop                 편집으로 돌아가기 (scnEditor.SwitchToEditMode)
    //   wait <초>            기다리기
    //   log <글>             로그에 표시 남기기
    //   quit                 저장하지 않고 게임 끄기
    //   keep                 (켜 둔 게임 다시 쓰기) 이 묶음이 끝나도, 오류·시간 초과여도 게임을 끄지 않는다. 끝나면 autotest.end 에 이유를 쓴다
    //   set <설정> <값>       Settings 필드를 바꾸고 바로 반영(ApplyConfig). 묶음이 끝나면 원래 값으로 되돌린다 (재시작이 필요한 설정은 안 됨)
    //   reload               묶음을 끝내고 UMM 다시 불러오기(Ctrl+F5 와 같음: 새 DLL 적용). 마지막 단계로 쓴다
    // 파일은 켤 때 읽고 autotest.done 으로 이름을 바꾼다(다음 실행에서 되풀이하지 않게). 어디서든 3분 넘게 멈추면 그만두고 끈다.
    // 묶음을 하고 있지 않을 때는 1초마다 autotest.txt 를 다시 찾는다(켜 둔 게임에 새 묶음 넣기, sfmeasure sf_live).
    internal static class AutoTest
    {
        private static List<string> steps;
        private static int idx;
        private static float stepStart, waitSec; private static double waitTarget;
        private static bool started, prevAuto, autoChanged, desiredAuto;
#if AUTOTEST
        private static long missionStepTicks;
#endif
        private static readonly System.Reflection.FieldInfo loadingField = AccessTools.Field(typeof(scnEditor), "isLoading");

        internal static bool Active { get { return steps != null; } }

        internal static bool ReloadNow;

        // 맵을 여는 동안 3초마다 힙·프로세스 메모리를 적는다 (긴 한 프레임 안에서도 다른 스레드라 적힌다). 메모리가 터지는 단계 찾기용.
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct PMC { public uint cb, PageFaultCount; public UIntPtr PeakWorkingSetSize, WorkingSetSize, QuotaPeakPagedPoolUsage, QuotaPagedPoolUsage, QuotaPeakNonPagedPoolUsage, QuotaNonPagedPoolUsage, PagefileUsage, PeakPagefileUsage, PrivateUsage; }
        [System.Runtime.InteropServices.DllImport("psapi.dll")] private static extern bool GetProcessMemoryInfo(IntPtr h, out PMC c, uint cb);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        private static volatile int memWatchGen;
        internal static volatile int MainFrame;
        internal static volatile string Phase = "";
        private static void StartMemWatch()
        {
            int gen = ++memWatchGen;
            var t = new System.Threading.Thread(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (gen == memWatchGen && sw.Elapsed.TotalSeconds < 600)
                {
                    System.Threading.Thread.Sleep(3000);
                    try
                    {
                        PMC c; GetProcessMemoryInfo(GetCurrentProcess(), out c, (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(PMC)));
                        Main.Entry.Logger.Log(string.Format("[자동 시험] 메모리 {0:F0}초: 힙 {1}MB, 프로세스 {2}MB(실제 램 {3}MB), 프레임 {4} {5}",
                            sw.Elapsed.TotalSeconds, GC.GetTotalMemory(false) >> 20, (long)c.PrivateUsage.ToUInt64() >> 20, (long)c.WorkingSetSize.ToUInt64() >> 20, MainFrame, Phase));
                    }
                    catch { }
                }
            }) { IsBackground = true, Name = "SF memwatch" };
            t.Start();
        }
        private static string loadedHash;   // 불러온 DLL 내용. 같은 DLL 을 다시 불러오면 Mono 가 같은 어셈블리로 여겨 패치가 깨진다
        private static string DllHash()
        {
            try { using (var md5 = System.Security.Cryptography.MD5.Create()) return BitConverter.ToString(md5.ComputeHash(File.ReadAllBytes(Path.Combine(modDir, "StutterFix.dll")))); }
            catch { return null; }
        }
        private static string modDir;
        private static float pollAt;
        private static bool keep, reloadPending;
        private static readonly Dictionary<System.Reflection.FieldInfo, object> setOrig = new Dictionary<System.Reflection.FieldInfo, object>();
#if AUTOTEST
        internal static bool GpuReloadOriginalAuto=>autoChanged?prevAuto:RDC.auto;
        internal static void InheritGpuReloadAuto(bool original)
        {prevAuto=original;autoChanged=true;desiredAuto=RDC.auto;}
        internal static bool ReloadGpuTrace()
        {
            if(setOrig.Count!=0 || !FrameCompare.Paused || !DecoBake.GpuLiveActive || !RDC.auto || DllHash()==loadedHash)
                throw new InvalidOperationException("GPU reload fixture requires paused owned GPU, autoplay, no temporary settings, and a different DLL");
            autoChanged=false;reloadPending=true;EndBatch("GPU trace active reload");return false;
        }
#endif

        internal static void Init(string modPath)
        {
            if (!Edition.AutoTest) return;
            modDir = modPath;
            loadedHash = DllHash();
            ReadFile();
#if AUTOTEST
            var endMethod = AccessTools.Method(typeof(scrController), "OnLandOnPortal");
            if (endMethod != null)
                new Harmony(Main.Entry.Info.Id).Patch(endMethod,
                    prefix: new HarmonyMethod(typeof(AutoTest), nameof(MissionEndPrefix)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(AutoTest), nameof(MissionEndFinalizer)) { priority = Priority.Last });
#endif
        }

#if AUTOTEST
        // 완주 전환 때 한 번만 측정. Main.Unload의 기존 Harmony ID 해제에 포함된다.
        public static void MissionEndPrefix(out long __state) { __state = Active ? System.Diagnostics.Stopwatch.GetTimestamp() : 0; }
        public static Exception MissionEndFinalizer(long __state, Exception __exception)
        {
            if (__state != 0) Log(string.Format("[임무완주] OnLandOnPortal {0:F3}ms", (System.Diagnostics.Stopwatch.GetTimestamp() - __state) * 1000.0 / System.Diagnostics.Stopwatch.Frequency));
            return __exception;
        }
#endif

        private static void ReadFile()
        {
            try
            {
                string f = Path.Combine(modDir, "autotest.txt");
                if (!File.Exists(f)) return;
                idx = 0; started = false; finished = false; keep = false; reloadPending = false; stepTimeout = 180f; waitSec = 0f; ummClosed = false;
                string endf = Path.Combine(modDir, "autotest.end");
                if (File.Exists(endf)) File.Delete(endf);
                steps = new List<string>();
                foreach (var raw in File.ReadAllLines(f)) { var l = raw.Trim(); if (l.Length > 0 && !l.StartsWith("#")) steps.Add(l); }
                string done = Path.Combine(modDir, "autotest.done");
                if (File.Exists(done)) File.Delete(done);
                File.Move(f, done);
                Main.Entry.Logger.Log("[자동 시험] 시작: " + steps.Count + "단계 - " + string.Join(" / ", steps.ToArray()));
            }
            catch (Exception ex) { steps = null; Main.Entry.Logger.Log("[자동 시험] 읽기 실패: " + ex.Message); }
        }

        static int fpsMarkFrames;static long fpsMarkTicks;
        private static void Log(string s) { Main.Entry.Logger.Log("[자동 시험] " + s); }

        private static float watchStart = -1, watchNext; private static int watchBad, watchChecks; private static readonly HashSet<int> watchSeen = new HashSet<int>();
        private static object spamMgr; private static scrPlanet spamPlanet; private static System.Reflection.MethodInfo spamShow;
        private static float spamStart, spamLast, spamMax; private static int spamOver, spamFrames, spamShown, spamSlowCalls;
        private static double spamCallMs, spamCallMax; private static string spamCallMaxWhat = ""; private static float spamLastShow, spamAcc;
        private static readonly System.Random spamRng = new System.Random(7);
        // scrHitTextManager 는 MonoBehaviour 가 아니라 컨트롤러·플레이어 관리자 필드에 들어 있다
        private static object FindHitTextManager()
        {
            var t = AccessTools.TypeByName("scrHitTextManager");
            if (t == null) return null;
            foreach (var owner in new object[] { scrController.instance, UnityEngine.Object.FindObjectOfType<scrPlayerManager>() })
            {
                if (owner == null) continue;
                foreach (var f in owner.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static))
                    if (t.IsAssignableFrom(f.FieldType)) { var v = f.GetValue(f.IsStatic ? null : owner); if (v != null) return v; }
                foreach (var pr in owner.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static))
                    if (t.IsAssignableFrom(pr.PropertyType) && pr.GetIndexParameters().Length == 0) { try { var v = pr.GetValue(owner, null); if (v != null) return v; } catch { } }
            }
            return null;
        }

        // 매 프레임 (OnUpdate)
        internal static void Tick()
        {
            if (steps == null)
            {
                if (modDir == null) return;
                float t = Time.realtimeSinceStartup;
                if (t < pollAt) return;
                pollAt = t + 1f;
                ReadFile();
                if (steps == null) return;
            }
            float now = Time.realtimeSinceStartup;
            MainFrame = Time.frameCount;
            EnginePatch.LatencySampleFrame();
            if (!started) { started = true; stepStart = now;
#if AUTOTEST
                missionStepTicks = System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            }
            if (!ummClosed || (idx < steps.Count && steps[idx].StartsWith("play", StringComparison.OrdinalIgnoreCase))) CloseUmm();
            if (idx >= steps.Count) { Finish("끝"); return; }
            if (now - stepStart > stepTimeout) { Finish("시간 초과 (" + steps[idx] + ")"); return; }
            string line = steps[idx];
            int sp = line.IndexOf(' ');
            string cmd = (sp < 0 ? line : line.Substring(0, sp)).ToLowerInvariant();
            string arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();
            try
            {
                if (Step(cmd, arg, now)) {
#if AUTOTEST
                    long ticks = System.Diagnostics.Stopwatch.GetTimestamp();
                    Log(string.Format("[임무단계] {0} {1:F3}ms", cmd, (ticks - missionStepTicks) * 1000.0 / System.Diagnostics.Stopwatch.Frequency));
                    missionStepTicks = ticks;
#endif
                    idx++; stepStart = now; waitSec = 0f;
                }
            }
            catch (Exception ex)
            {
                string failure="단계 실패 '" + line + "': " + (ex.InnerException ?? ex).Message;
                Log(failure); Finish(failure);
            }
        }

        // UMM 창이 열려 있으면 그리는 비용 때문에 FPS 가 조금 낮게 나온다(사용자 관찰, 2026-09-27). 시작할 때와 재생마다 닫는다.
        private static bool ummClosed;
        private static void CloseUmm()
        {
            try
            {
                var ui = UnityModManagerNet.UnityModManager.UI.Instance;
                if (ui == null) return;
                if (ui.Opened) { ui.ToggleWindow(false); Log("UMM 창 닫음"); }
                ummClosed = true;
            }
            catch { ummClosed = true; }
        }

        // 판마다 재기: play/retry 뒤 2초부터 다음 명령 전까지 프레임 시간과 화면 대기(PresentWatch.Frame 에서 받음)
        private static float runStart = -1f; private static string runKind = ""; private static int runNo;
        private static double accMs, accWait; private static int accN;
        internal static void Sample(bool playing, float ms, float wait)
        {
            if (runStart < 0f || ms > 500f || Time.realtimeSinceStartup - runStart < 2f) return;
            accMs += ms; accWait += wait; accN++;
        }
        private static void EndRun()
        {
            if (runStart >= 0f && accN > 30)
                Log(string.Format("판 #{0} ({1}): 평균 {2:F0} FPS, 화면 대기 {3:F2}ms, 프레임 {4}개 -> {5}", runNo, runKind, 1000.0 / (accMs / accN), accWait / accN, accN, accWait / accN >= 0.5 ? "느림" : "빠름"));
            runStart = -1f; accMs = accWait = 0; accN = 0;
        }
        private static void BeginRun(string kind) { EndRun(); runNo++; runKind = kind; runStart = Time.realtimeSinceStartup; }

        private static float stepTimeout = 180f;   // 단계마다 최대 (timeout 명령으로 바꿈, 큰 맵 열기용)
        private static float openStartedAt;
        private static int thumbPhase; private static float thumbAt; private static byte[] thumbA; private static bool thumbSame;
        private static int panPhase, panVis, panInvis, panVisFrame, panInvisFrame; private static float panAt, panLast, panK; private static Harmony panHarmony;
        private static readonly List<float> panMs = new List<float>(); private static readonly List<KeyValuePair<float, string>> panWorst = new List<KeyValuePair<float, string>>();
        private static readonly long[] panTick0 = new long[17]; private static long panUpd0;
        private static readonly Dictionary<string, long> panTimes = new Dictionary<string, long>();
        public static void PanPre(out long __state) { __state = System.Diagnostics.Stopwatch.GetTimestamp(); }
        public static void PanPost(System.Reflection.MethodBase __originalMethod, long __state) { if (panPhase == 0) return; string k = __originalMethod.DeclaringType.Name + "." + __originalMethod.Name; long v; panTimes.TryGetValue(k, out v); panTimes[k] = v + System.Diagnostics.Stopwatch.GetTimestamp() - __state; }
        public static void PanVis() { panVis++; panVisFrame++; }
        public static void PanInvis() { panInvis++; panInvisFrame++; }
        private static int cullPhase, cullFrames; private static float cullAt; private static readonly List<Renderer> cullList = new List<Renderer>();
        private static bool Same(byte[] a, byte[] b) { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
        private static int openPhase, gamePhase; private static scnGame gameOld;
        private static bool endSeen; private static float endAt;
        private static Harmony pressHarmony; private static bool pressPending;
        public static bool PressPrefix(ref bool __result) { if (!pressPending) return true; pressPending = false; __result = true; return false; }
        private static System.Reflection.MethodInfo pickMethod; private static int pickDone, pickHits; private static readonly List<float> pickMs = new List<float>();

        // 끝났으면 true
        private static int modWatchFrame, onUpdFrame; private static long onUpdTotal0; private static readonly long[] onUpd0 = new long[64];
        private static bool Step(string cmd, string arg, float now)
        {
            var ed = ADOBase.isLevelEditor ? scnEditor.instance : null;
            switch (cmd)
            {
#if DEV || AUTOTEST
                case "pause": FrameCompare.Pause(); return true;
                case "resume": FrameCompare.Resume(); return true;
                case "framecmp": return FrameCompare.Step(arg);
                case "coldopen": ColdOpenProbe.Step(arg); return true;
                case "tightsprite": TightSpriteProbe.Step(arg); return true;
#endif
                case "pick":
                    {
                        // (개발자용) 편집 화면 클릭 판정(scnEditor.ObjectsAtMouse)을 프레임마다 한 번씩 N번: 카메라를 타일 길을 따라 옮기고
                        // 마우스를 화면 가운데에 두어 마우스 아래에 타일이 있게 한다. 한 번에 걸린 시간 평균·최대, EditorPick 검증 결과.
                        if (ed == null) { Log("pick: 에디터 아님"); return true; }
                        var ps = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries); int total = 30; if (ps.Length > 0) int.TryParse(ps[0], out total);
                        if (pickDone == 0) EditorPick.Enabled = !(ps.Length > 1 && ps[1] == "off");
                        var lfp = ADOBase.lm != null ? ADOBase.lm.listFloors : null;
                        if (pickMethod == null) pickMethod = HarmonyLib.AccessTools.Method(typeof(scnEditor), "ObjectsAtMouse");
                        var camF = HarmonyLib.AccessTools.Field(typeof(scnEditor), "camera");
                        var cam = camF != null ? camF.GetValue(ed) as Camera : null;
                        if (lfp == null || lfp.Count == 0 || cam == null || pickMethod == null) { Log("pick: 준비 안 됨"); return true; }
                        if (pickDone == 0) { pickMs.Clear(); pickHits = 0; }
                        var fp = lfp[(int)((long)pickDone * 7919 % lfp.Count)];
                        var q = fp.transform.position; cam.transform.position = new Vector3(q.x, q.y, -10f);
                        SetCursorPos(Screen.width / 2, Screen.height / 2);
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        var r = pickMethod.Invoke(ed, null) as GameObject[];
                        pickMs.Add((float)((System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency));
                        if (r != null && r.Length > 0) pickHits++;
                        if (++pickDone < total) return false;
                        pickMs.Sort(); float s = 0; foreach (var v in pickMs) s += v;
                        Log(string.Format("클릭 판정 {0}번: 평균 {1:F2}ms, 최대 {2:F2}ms, 물체 찾음 {3}번 | EditorPick 부름 {4}, 빠른 길 {5}, 검증 {6}번 중 빠뜨림 {7}", pickDone, s / pickMs.Count, pickMs[pickMs.Count - 1], pickHits, EditorPick.Calls, EditorPick.Fast, EditorPick.Checks, EditorPick.Missed));
                        pickDone = 0; EditorPick.Enabled = true;
                        return true;
                    }
                case "waitend":
                    {
                        // 곡이 끝날 때까지 (Hitch.Playing 이 켜졌다가 꺼지면), 최대 arg 초. 곡 끝 요약([곡])이 남은 뒤 2초 더
                        if (waitSec == 0f) { float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out waitSec); if (waitSec <= 0f) waitSec = 600f; endSeen = false; endAt = 0f; stepTimeout = Math.Max(stepTimeout, waitSec + 30f); }
                        if (Hitch.Playing) endSeen = true;
                        else if (endSeen && endAt == 0f) { endAt = now; Log(string.Format("곡 끝남 ({0:F0}초)", now - stepStart)); }
                        if (endAt > 0f && now - endAt >= 2f) return true;
                        if (now - stepStart >= waitSec) { Log("곡이 끝나지 않음 (" + waitSec + "초)"); return true; }
                        return false;
                    }
                case "waitsongpos":
                    {
                        // waitsongpos <곡 초> [최대 대기 초=400]: 곡 시계가 그 값에 닿을 때까지 (창을 판마다 같은 곡 시각에 맞춘다)
                        if (waitSec == 0f) { var sp2 = arg.Split(' '); waitTarget = double.Parse(sp2[0], System.Globalization.CultureInfo.InvariantCulture); waitSec = sp2.Length > 1 ? float.Parse(sp2[1], System.Globalization.CultureInfo.InvariantCulture) : 400f; }
                        var c2 = scrConductor.instance;
                        if (c2 != null && Hitch.Playing && c2.songposition_minusi >= waitTarget) return true;
                        if (now - stepStart >= waitSec) throw new Exception("곡 시계가 " + waitTarget + "초에 닿지 않음"
                            + " (현재=" + (c2 == null ? "없음" : c2.songposition_minusi.ToString("F3", System.Globalization.CultureInfo.InvariantCulture))
                            + ", playing=" + Hitch.Playing + ", GC=" + GcControl.Enabled + ", 상태=" + GcControl.LastScene + ")");
                        return false;
                    }
                case "wait":
                    if (waitSec == 0f) { float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out waitSec); if (waitSec <= 0f) waitSec = 0.01f; }
                    return now - stepStart >= waitSec;
                case "thumbtest":
                    {
                        // 썸네일 카메라를 켠 채(원래 게임) 만든 썸네일과 끈 채(ThumbCam) 만든 썸네일이 바이트까지 같은지.
                        // 포털 그림이 시간에 따라 움직일 수 있어 같은 프레임 안에서 켬/끔을 비교하고, 0.5초 뒤 한 번 더(시간 차이 대조군) 만든다.
                        if (ed == null || ed.thumbnailMaker == null) { Log("썸네일 시험: 에디터 아님"); return true; }
                        var tcam = ed.thumbnailMaker.GetComponent<Camera>();
                        if (thumbPhase == 0)
                        {
                            tcam.enabled = true;
                            thumbA = File.ReadAllBytes(ed.MakeThumbnail(new DLCManager[0]));
                            tcam.enabled = false;
                            var b = File.ReadAllBytes(ed.MakeThumbnail(new DLCManager[0]));
                            thumbSame = Same(thumbA, b);
                            thumbPhase = 1; thumbAt = now; return false;
                        }
                        if (now - thumbAt < 0.5f) return false;
                        var later = File.ReadAllBytes(ed.MakeThumbnail(new DLCManager[0]));
                        ThumbCam.Apply();
                        Log("썸네일 시험: 같은 프레임 켬/끔 " + (thumbSame ? "같음" : "다름") + " (" + thumbA.Length + "바이트) | 0.5초 뒤(대조군) " + (Same(thumbA, later) ? "같음" : "다름") + " | 지금 카메라 켜짐 " + tcam.enabled);
                        thumbPhase = 0; return true;
                    }
                case "cullexp":
                    {
                        // (실험) 타일 렌더러를 끄는 방식별 FPS: none / force(forceRenderingOff) / enabled(enabled=false). 카메라 근처 반경 안 타일은 그대로 둔다.
                        var parts = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        string mode = parts[0]; float secs = parts.Length > 1 ? float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 4f;
                        if (cullPhase == 0)
                        {
                            cullList.Clear();
                            var cpos = (Vector2)Camera.main.transform.position;
                            foreach (var fl in ADOBase.lm.listFloors)
                            {
                                if (fl == null || ((Vector2)fl.transform.position - cpos).sqrMagnitude < 60f * 60f) continue;
                                foreach (var r in fl.GetComponentsInChildren<Renderer>(false)) if (r.enabled && !r.forceRenderingOff) cullList.Add(r);
                            }
                            foreach (var r in cullList) { if (mode == "force") r.forceRenderingOff = true; else if (mode == "enabled") r.enabled = false; }
                            cullPhase = 1; cullAt = now; cullFrames = Time.frameCount; return false;
                        }
                        if (now - cullAt < secs) return false;
                        float fps = (Time.frameCount - cullFrames) / (now - cullAt);
                        foreach (var r in cullList) { if (r == null) continue; if (mode == "force") r.forceRenderingOff = false; else if (mode == "enabled") r.enabled = true; }
                        Log("컬링 실험 " + mode + ": 렌더러 " + cullList.Count + "개 뺌, " + fps.ToString("F0") + " FPS");
                        cullPhase = 0; return true;
                    }
                case "ffxreuse":
                    FfxReuse.ForceOff = arg == "off"; Log("효과 재사용 " + (FfxReuse.ForceOff ? "끔" : "켬")); return true;
                case "floordump":
                    {
                        // (개발자용 조사) 타일 구조, 렌더러 종류, 카메라 마스크, 렌더러 켜기/끄기 호출 훑기 시간
                        var fl = ADOBase.lm.listFloors;
                        var f0 = fl[Math.Min(100, fl.Count - 1)];
                        var sb = new System.Text.StringBuilder("타일 " + fl.Count + "개, 예시 #" + f0.seqID + ":");
                        foreach (var t in f0.GetComponentsInChildren<Transform>(true))
                        {
                            sb.Append("\n  ").Append(t == f0.transform ? "(루트)" : t.name).Append(" 층 ").Append(t.gameObject.layer).Append(t.gameObject.activeSelf ? "" : " 꺼짐").Append(":");
                            foreach (var c in t.GetComponents<Component>())
                            {
                                if (c == null) continue;
                                sb.Append(' ').Append(c.GetType().Name);
                                if (c is Renderer rr) sb.Append(rr.enabled ? "(켬)" : "(끔)");
                                else if (c is Behaviour bb) sb.Append(bb.enabled ? "(켬)" : "(끔)");
                            }
                        }
                        var tally = new Dictionary<string, int>();
                        foreach (var f in fl) foreach (var r in f.GetComponentsInChildren<Renderer>(false)) if (r.enabled) { string k = r.GetType().Name + "@층" + r.gameObject.layer + (r.transform == f.transform ? "(루트)" : "(" + r.name + ")"); int n; tally.TryGetValue(k, out n); tally[k] = n + 1; }
                        sb.Append("\n켜진 렌더러:");
                        foreach (var kv in tally) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
                        sb.Append("\n카메라:");
                        foreach (var cam in Camera.allCameras) sb.Append(' ').Append(cam.name).Append("=0x").Append(cam.cullingMask.ToString("X"));
                        Log(sb.ToString());
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var setE = typeof(Renderer).GetProperty("enabled").GetSetMethod(); var getE = typeof(Renderer).GetProperty("enabled").GetGetMethod();
                        int methods = 0, hits = 0; var who = new List<string>();
                        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                        {
                            var an = asm.GetName().Name;
                            if (an.StartsWith("Unity") || an.StartsWith("System") || an == "mscorlib" || an == "netstandard" || an.StartsWith("Mono.") || an.StartsWith("0Harmony") || an.StartsWith("Harmony") || asm.IsDynamic) continue;
                            Type[] types; try { types = asm.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException ex) { types = ex.Types; }
                            var mod = asm.ManifestModule;
                            var cache = new Dictionary<int, bool>();
                            foreach (var t in types)
                            {
                                if (t == null) continue;
                                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                                {
                                    System.Reflection.MethodBody body; try { body = m.GetMethodBody(); } catch { continue; }
                                    if (body == null) continue;
                                    methods++;
                                    var il = body.GetILAsByteArray();
                                    for (int i = 0; i + 4 < il.Length; i++)
                                    {
                                        if (il[i] != 0x28 && il[i] != 0x6F) continue;
                                        int tok = BitConverter.ToInt32(il, i + 1);
                                        if ((tok >> 24) != 0x0A && (tok >> 24) != 0x06) continue;
                                        bool hit;
                                        if (!cache.TryGetValue(tok, out hit)) { try { var mb = mod.ResolveMethod(tok); hit = mb == setE || mb == getE; } catch { hit = false; } cache[tok] = hit; }
                                        if (hit) { hits++; if (who.Count < 400) who.Add(an + ":" + t.Name + "." + m.Name); break; }
                                    }
                                }
                            }
                        }
                        Log("렌더러 enabled 호출 함수 " + hits + "개 / 훑은 함수 " + methods + "개, " + sw.ElapsedMilliseconds + "ms: " + string.Join(", ", who.ToArray()));
                        return true;
                    }
                case "matrefs":
                    {
                        // 지금 타일들이 가리키는 머티리얼: 스크립트 필드(FloorRenderer.material) / 렌더러가 쓰는 것(sharedMaterials)
                        var byField = new HashSet<Material>(); var byRend = new HashSet<Material>(); int same = 0, n = 0, slots = 0;
                        foreach (var f in ADOBase.lm.listFloors)
                        {
                            if (f == null || f.floorRenderer == null) continue; n++;
                            var m = f.floorRenderer.material; if (m != null) byField.Add(m);
                            var sm = f.floorRenderer.renderer.sharedMaterials; slots += sm.Length; foreach (var x in sm) if (x != null) byRend.Add(x);
                            if (sm.Length > 0 && ReferenceEquals(sm[0], m)) same++;
                        }
                        var both = new HashSet<Material>(byField); both.UnionWith(byRend);
                        Log("타일 " + n + "개: 스크립트 필드 머티리얼 " + byField.Count + "개, 렌더러 머티리얼 " + byRend.Count + "개(슬롯 " + slots + "), 합 " + both.Count + "개, 둘이 같은 타일 " + same + "개, 첫 타일 id " + ADOBase.lm.listFloors[0].GetInstanceID());
                        return true;
                    }
                case "listtail":
                    {
                        // 지금 맵 이벤트 목록의 내부 배열에서 Count 뒤에 남은 것 (지난 맵 이벤트가 남아 있는지)
                        var ld2 = ADOBase.customLevel != null ? ADOBase.customLevel.levelData : null;
                        if (ld2 == null) { Log("listtail: 맵 없음"); return true; }
                        foreach (var g in Resources.FindObjectsOfTypeAll<scnGame>())
                        {
                            var gl = g.levelData;
                            Log("listtail scnGame#" + g.GetInstanceID() + " 장면 " + g.gameObject.scene.IsValid() + ", ADOBase.customLevel 와 같음 " + ReferenceEquals(g, ADOBase.customLevel) + ", levelData 이벤트 " + (gl != null ? gl.levelEvents.Count : -1) + ", 지금 levelData 와 같음 " + ReferenceEquals(gl, ld2));
                        }
                        var edc = ed != null ? HarmonyLib.Traverse.Create(ed).Field("customLevel").GetValue() as scnGame : null;
                        Log("listtail scnEditor.customLevel = ADOBase.customLevel ? " + ReferenceEquals(edc, ADOBase.customLevel) + (edc != null && edc.levelData != null ? ", 그 levelData 이벤트 " + edc.levelData.levelEvents.Count : ""));
                        var itemsF = typeof(List<ADOFAI.LevelEvent>).GetField("_items", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        foreach (var pr in new[] { new KeyValuePair<string, List<ADOFAI.LevelEvent>>("levelEvents", ld2.levelEvents), new KeyValuePair<string, List<ADOFAI.LevelEvent>>("decorations", ld2.decorations) })
                        {
                            var arr = itemsF.GetValue(pr.Value) as ADOFAI.LevelEvent[];
                            int beyond = 0; if (arr != null) for (int i = pr.Value.Count; i < arr.Length; i++) if (arr[i] != null) beyond++;
                            Log("listtail " + pr.Key + ": Count " + pr.Value.Count + ", 배열 길이 " + (arr == null ? -1 : arr.Length) + ", Count 뒤에 남은 것 " + beyond);
                        }
                        return true;
                    }
                case "whoholdsev":
                    {
                        // 지금 맵에 없는 레벨 이벤트(지난 맵 것)를 붙잡는 곳: 정적 필드 + 살아 있는 모든 MonoBehaviour 에서 출발
                        var cur = new HashSet<object>();
                        var ld = ADOBase.customLevel != null ? ADOBase.customLevel.levelData : null;
                        if (ld != null)
                        {
                            foreach (var e in ld.levelEvents) cur.Add(e); foreach (var e in ld.decorations) cur.Add(e);
                            foreach (var fn in new[] { "levelSettings", "trackSettings", "backgroundSettings", "cameraSettings", "miscSettings", "eventSettings", "decorationSettings" })
                            { var v = HarmonyLib.Traverse.Create(ld).Field(fn).GetValue(); if (v != null) cur.Add(v); }
                        }
                        var roots = new List<KeyValuePair<object, string>>();
                        var byType = new Dictionary<string, int>(); int inScene = 0, noScene = 0, destroyedGo = 0;
                        foreach (var mb in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
                        {
                            if (mb == null) continue;
                            roots.Add(new KeyValuePair<object, string>(mb, "장면:" + mb.GetType().Name));
                            string k = mb.GetType().Name; int c; byType.TryGetValue(k, out c); byType[k] = c + 1;
                            if (mb.gameObject.scene.IsValid()) inScene++; else noScene++;
                        }
                        var top = new List<KeyValuePair<string, int>>(byType); top.Sort((x, y) => y.Value.CompareTo(x.Value));
                        var tsb = new System.Text.StringBuilder();
                        for (int i = 0; i < top.Count && i < 15; i++) tsb.Append(' ').Append(top[i].Key).Append('=').Append(top[i].Value);
                        int floorsAlive = 0; foreach (var f in Resources.FindObjectsOfTypeAll<scrFloor>()) if (f != null && f.gameObject.scene.IsValid()) floorsAlive++;
                        Log("살아 있는 MonoBehaviour " + roots.Count + "개 (장면 안 " + inScene + ", 장면 밖(에셋·프리팹) " + noScene + "), 장면 안 타일 " + floorsAlive + "개, 지금 맵 타일 " + (ADOBase.lm != null && ADOBase.lm.listFloors != null ? ADOBase.lm.listFloors.Count : -1) + "개 | 종류별:" + tsb);
                        GC.Collect();
                        var hsw = System.Diagnostics.Stopwatch.StartNew();
                        Log("지금 맵(이벤트 " + cur.Count + "개)에 없는 LevelEvent 를 붙잡는 곳 (출발 " + roots.Count + "개 + 정적): " + HeapPath.Find(o => o is ADOFAI.LevelEvent && !cur.Contains(o), "옛 LevelEvent", 20000000, 40, roots, null, true) + " (" + hsw.ElapsedMilliseconds + "ms)");
                        return true;
                    }
                case "whoholds":
                    { var wt = string.IsNullOrEmpty(arg) ? typeof(scrFloor) : HarmonyLib.AccessTools.TypeByName(arg.Trim()); if (wt == null) { Log("whoholds: 형식 없음 " + arg); return true; } GC.Collect(); var hsw = System.Diagnostics.Stopwatch.StartNew(); Log("지워진 " + wt.Name + " 을 붙잡는 곳: " + HeapPath.Find(wt, 6000000, 12) + " (" + hsw.ElapsedMilliseconds + "ms)"); return true; }
                case "whoholdsmat":
                    {
                        var cur = new HashSet<Material>();
                        foreach (var f in ADOBase.lm.listFloors) if (f != null && f.floorRenderer != null) { if (f.floorRenderer.material != null) cur.Add(f.floorRenderer.material); foreach (var x in f.floorRenderer.renderer.sharedMaterials) if (x != null) cur.Add(x); }
                        var orphan = new HashSet<object>(); int total = 0;
                        foreach (var m in Resources.FindObjectsOfTypeAll<Material>()) if (m.name == "FloorMeshDefault (Instance)" && !cur.Contains(m)) { total++; if (orphan.Count < 2000) orphan.Add(m); }
                        var hsw = System.Diagnostics.Stopwatch.StartNew();
                        Log("버려진 타일 머티리얼 " + total + "개 중 " + orphan.Count + "개를 찾음: " + HeapPath.Find(o => orphan.Contains(o), "버려진 머티리얼", 20000000, 12) + " (" + hsw.ElapsedMilliseconds + "ms)");
                        return true;
                    }
                case "dotclear":
                    DG.Tweening.DOTween.ClearCachedTweens(); Log("DOTween 재활용 풀 비움"); return true;
                case "killorphans":
                    {
                        var cur = new HashSet<Material>();
                        foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>()) foreach (var x in r.sharedMaterials) if (x != null) cur.Add(x);
                        foreach (var f in ADOBase.lm.listFloors) if (f != null && f.floorRenderer != null && f.floorRenderer.material != null) cur.Add(f.floorRenderer.material);
                        int k = 0;
                        foreach (var m in Resources.FindObjectsOfTypeAll<Material>()) if (m.name == "FloorMeshDefault (Instance)" && !cur.Contains(m)) { UnityEngine.Object.Destroy(m); k++; }
                        Log("버려진 타일 머티리얼 " + k + "개 지움 (시험)");
                        return true;
                    }
                case "orphanrend":
                    {
                        // 버려진 타일 머티리얼을 아직 쓰는 렌더러가 있는지 (지금 맵 목록 밖의 렌더러)
                        var cur = new HashSet<Material>();
                        foreach (var f in ADOBase.lm.listFloors) if (f != null && f.floorRenderer != null) foreach (var x in f.floorRenderer.renderer.sharedMaterials) if (x != null) cur.Add(x);
                        var desc = new Dictionary<string, int>(); int n = 0;
                        foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
                        {
                            foreach (var m in r.sharedMaterials)
                            {
                                if (m == null || cur.Contains(m) || !m.name.EndsWith("(Instance)")) continue;
                                if (!m.name.StartsWith("FloorMeshDefault")) continue;
                                n++;
                                var t = r.transform; string path = t.name; for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
                                var comps = new System.Text.StringBuilder(); foreach (var c in r.GetComponents<Component>()) comps.Append(c == null ? "null" : c.GetType().Name).Append(',');
                                string k = path.Length > 80 ? path.Substring(0, 80) : path; k = System.Text.RegularExpressions.Regex.Replace(k, @"\d+", "#") + " [" + comps + "] 켜짐=" + r.gameObject.activeInHierarchy + " 장면=" + r.gameObject.scene.name;
                                int c0; desc.TryGetValue(k, out c0); desc[k] = c0 + 1;
                                break;
                            }
                        }
                        var sbr = new System.Text.StringBuilder("버려진 타일 머티리얼을 쓰는 렌더러 " + n + "개:");
                        int shown = 0; foreach (var kv in desc) { if (shown++ >= 8) break; sbr.Append("\n  ").Append(kv.Value).Append("개: ").Append(kv.Key); }
                        Log(sbr.ToString());
                        return true;
                    }
                case "newmats":
                    {
                        // 참조 없는 머티리얼을 만들어 둔다 (에셋 정리가 이런 것을 치우는지 시험)
                        var sh = Shader.Find("Sprites/Default");
                        for (int i = 0; i < 1000; i++) { var m = new Material(sh); m.name = "SFTestMat"; }
                        var src = ADOBase.lm.listFloors[0].floorRenderer.renderer;
                        var go = new GameObject("SFTestRend"); var mr = go.AddComponent<MeshRenderer>(); mr.sharedMaterial = src.sharedMaterial;
                        for (int i = 0; i < 1000; i++) { var inst = mr.material; mr.sharedMaterial = src.sharedMaterial; }   // renderer.material 로 복제된 것 1000개
                        UnityEngine.Object.Destroy(go);
                        Log("시험 머티리얼 만듦");
                        return true;
                    }
                case "countmats":
                    {
                        int a = 0, b = 0;
                        foreach (var m in Resources.FindObjectsOfTypeAll<Material>()) { if (m.name == "SFTestMat") a++; else if (m.name.EndsWith("(Instance)") && m.name.StartsWith(ADOBase.lm.listFloors[0].floorRenderer.renderer.sharedMaterial.name.Replace(" (Instance)", ""))) b++; }
                        Log("시험 머티리얼 남음: new Material " + a + "개, 타일 머티리얼 이름의 (Instance) " + b + "개");
                        return true;
                    }
                case "whoholds2":
                    {
                        // 버려진 타일 머티리얼이나 지워진 타일/타일 렌더러에 닿는 경로를 출발점 묶음별로 (mods / game / scene)
                        var cur = new HashSet<Material>();
                        foreach (var f in ADOBase.lm.listFloors) if (f != null && f.floorRenderer != null) { if (f.floorRenderer.material != null) cur.Add(f.floorRenderer.material); foreach (var x in f.floorRenderer.renderer.sharedMaterials) if (x != null) cur.Add(x); }
                        var orphan = new HashSet<object>();
                        foreach (var m in Resources.FindObjectsOfTypeAll<Material>()) if (m.name == "FloorMeshDefault (Instance)" && !cur.Contains(m)) orphan.Add(m);
                        Func<object, bool> isT = o => orphan.Contains(o) || ((o is scrFloor || o is FloorRenderer) && (UnityEngine.Object)o == null);
                        HeapPath.SkipInto = o => (o is scrFloor || o is ffxPlusBase || o is FloorRenderer) && (UnityEngine.Object)o != null;
                        string gameAsm = typeof(scrFloor).Assembly.GetName().Name, self = typeof(AutoTest).Assembly.GetName().Name;
                        List<KeyValuePair<object, string>> roots = null;
                        Func<System.Reflection.Assembly, bool> af = null;
                        if (arg == "mods") af = a => { var n = a.GetName().Name; return n != gameAsm && n != self && !n.StartsWith("Unity") && !n.StartsWith("System") && n != "mscorlib" && !n.StartsWith("Mono.") && !n.Contains("Harmony") && !n.StartsWith("DOTween") && n != "netstandard"; };
                        else if (arg == "floors") { HeapPath.SkipInto = null; roots = new List<KeyValuePair<object, string>>(); foreach (var f in ADOBase.lm.listFloors) if (f != null) roots.Add(new KeyValuePair<object, string>(f, "새 타일")); }
                        else if (arg == "self") af = a => a.GetName().Name == self;
                        else if (arg == "game") af = a => a.GetName().Name == gameAsm || a.GetName().Name.StartsWith("DOTween") || a.GetName().Name.StartsWith("Assembly-CSharp");
                        else
                        {
                            roots = new List<KeyValuePair<object, string>>();
                            foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>()) if (!(mb is scrFloor) && !(mb is ffxPlusBase) && !(mb is FloorRenderer)) roots.Add(new KeyValuePair<object, string>(mb, "장면:" + mb.GetType().Name));
                        }
                        var hsw = System.Diagnostics.Stopwatch.StartNew();
                        Log("[" + arg + "] 버려진 머티리얼 " + orphan.Count + "개: " + HeapPath.Find(isT, "버려진 것", (arg == "game" || arg == "scene") ? 9000000 : 3000000, 16, roots, af) + " (" + hsw.ElapsedMilliseconds + "ms)");
                        HeapPath.SkipInto = null;
                        return true;
                    }
                case "unpatch":
                    new Harmony(Main.Entry.Info.Id).UnpatchOwner(Main.Entry.Info.Id); Log("이 모드의 게임 패치를 모두 뗌 (비교용)"); return true;
                case "orphaninfo":
                    {
                        var cur = new HashSet<Material>();
                        foreach (var f in ADOBase.lm.listFloors) if (f != null && f.floorRenderer != null) { if (f.floorRenderer.material != null) cur.Add(f.floorRenderer.material); foreach (var x in f.floorRenderer.renderer.sharedMaterials) if (x != null) cur.Add(x); }
                        var flags = new Dictionary<string, int>(); int total = 0;
                        foreach (var m in Resources.FindObjectsOfTypeAll<Material>()) if (m.name == "FloorMeshDefault (Instance)" && !cur.Contains(m)) { total++; string k = m.hideFlags.ToString(); int c; flags.TryGetValue(k, out c); flags[k] = c + 1; }
                        var sbf = new System.Text.StringBuilder(); foreach (var kv in flags) sbf.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
                        int curFlags = 0; foreach (var m in cur) if (m.hideFlags != HideFlags.None) curFlags++;
                        Log("버려진 타일 머티리얼 " + total + "개, hideFlags:" + sbf + " / 지금 타일 것 중 hideFlags 있는 것 " + curFlags + "/" + cur.Count);
                        return true;
                    }
                case "unload":
                    { var usw = System.Diagnostics.Stopwatch.StartNew(); var op = Resources.UnloadUnusedAssets(); Log("에셋 정리 요청 (" + usw.ElapsedMilliseconds + "ms)"); return true; }
                case "census":
                    LeakGuard.CensusNow(arg.Length > 0 ? arg : "자동 시험");
                    { var all = Resources.FindObjectsOfTypeAll<scrFloor>(); int act = 0; foreach (var f in all) if (f.gameObject.activeInHierarchy) act++; Log("타일 오브젝트 전체 " + all.Length + "개(켜진 것 " + act + "), 지금 맵 목록 " + (ADOBase.lm != null ? ADOBase.lm.listFloors.Count : -1) + "개, 박자 알림 목록 " + (scrConductor.instance != null ? scrConductor.instance.onBeats.Count : -1) + "개"); }
                    return true;
                case "beatfix":
                    BeatFix.Enabled = arg != "off"; Log("박자 알림 건너뛰기 " + (BeatFix.Enabled ? "켬" : "끔")); return true;
                case "tilecullmin": TileCull.MinFloors = int.Parse(arg.Trim()); Log("화면 밖 타일 최소 타일 수 " + TileCull.MinFloors); return true;
                case "tilecull":
                    TileCull.ForceOff = arg == "off"; Log("화면 밖 타일 " + (TileCull.ForceOff ? "끔" : "켬")); return true;
                case "gcfull":
                    { var gsw = System.Diagnostics.Stopwatch.StartNew(); int c0 = GC.CollectionCount(0); GC.Collect(); Log("GC 한 번에 끝냄 " + gsw.ElapsedMilliseconds + "ms (그 전 수집 횟수 " + c0 + ", 힙 " + (GC.GetTotalMemory(false) >> 20) + "MB, 모드 " + UnityEngine.Scripting.GarbageCollector.GCMode + ", 점진 " + UnityEngine.Scripting.GarbageCollector.isIncremental + ")"); return true; }
                case "campan":
                    {
                        // (개발자용 조사) 편집 화면에서 카메라를 매 프레임 옮기며(끌기와 같은 방식) 프레임 시간·엔진 단계·타일 보임 이벤트를 잰다
                        if (ed == null) { Log("campan: 에디터 아님"); return true; }
                        var parts = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        float secs = parts.Length > 0 ? float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) : 5f;
                        float speed = parts.Length > 1 ? float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 20f;
                        var camF = HarmonyLib.AccessTools.Field(typeof(scnEditor), "camera");
                        var cam = camF != null ? camF.GetValue(ed) as Camera : null;
                        if (cam == null) { Log("campan: 카메라 없음"); return true; }
                        if (panPhase == 0)
                        {
                            if (panHarmony == null)
                            {
                                panHarmony = new Harmony("StutterFix.campan");
                                panHarmony.Patch(HarmonyLib.AccessTools.Method(typeof(scrFloor), "OnBecameVisible"), prefix: new HarmonyMethod(typeof(AutoTest), nameof(PanVis)));
                                panHarmony.Patch(HarmonyLib.AccessTools.Method(typeof(scrFloor), "OnBecameInvisible"), prefix: new HarmonyMethod(typeof(AutoTest), nameof(PanInvis)));
                                foreach (var pm in new[] { HarmonyLib.AccessTools.Method(HarmonyLib.AccessTools.TypeByName("DG.Tweening.Core.DOTweenComponent"), "Update"), HarmonyLib.AccessTools.Method(HarmonyLib.AccessTools.TypeByName("UnityModManagerNet.UnityModManager+UI"), "Update"), HarmonyLib.AccessTools.Method(typeof(scnEditor), "Update"), HarmonyLib.AccessTools.Method(typeof(scnEditor), "LateUpdate"), HarmonyLib.AccessTools.Method(typeof(scrController), "UpdateInput"), HarmonyLib.AccessTools.Method(HarmonyLib.AccessTools.TypeByName("PlatformHelper"), "Update"), HarmonyLib.AccessTools.Method(HarmonyLib.AccessTools.TypeByName("AsyncInputUtils"), "UpdateOffsetTime"), HarmonyLib.AccessTools.Method(typeof(scrPlayer), "Simulated_PlayerControl_Update"), HarmonyLib.AccessTools.Method(typeof(scrConductor), "Update"), HarmonyLib.AccessTools.Method(typeof(scrConductor), "PropagateOnBeat"), HarmonyLib.AccessTools.Method(typeof(scrController), "CheckForAudioOutputChange"), HarmonyLib.AccessTools.Method(typeof(AudioManager), "Play", new[] { typeof(string), typeof(double), typeof(UnityEngine.Audio.AudioMixerGroup), typeof(float), typeof(int) }), HarmonyLib.AccessTools.Method(HarmonyLib.AccessTools.TypeByName("FloorMesh"), "UpdateAllRequired") })
                                    if (pm != null) panHarmony.Patch(pm, prefix: new HarmonyMethod(typeof(AutoTest), nameof(PanPre)), postfix: new HarmonyMethod(typeof(AutoTest), nameof(PanPost)));
                            }
                            {
                                // 켜진 스크립트 중 Update/LateUpdate 가 있는 것, 종류별 수
                                var cnt = new Dictionary<Type, int>();
                                foreach (var mb in UnityEngine.Object.FindObjectsOfType<MonoBehaviour>())
                                {
                                    if (mb == null || !mb.isActiveAndEnabled) continue;
                                    var tt = mb.GetType(); int c0; cnt.TryGetValue(tt, out c0); cnt[tt] = c0 + 1;
                                }
                                var tl = new List<KeyValuePair<Type, int>>();
                                foreach (var kv in cnt) { var um = kv.Key.GetMethod("Update", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) ?? kv.Key.GetMethod("LateUpdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic); if (um != null) tl.Add(kv); }
                                tl.Sort((x, y) => y.Value.CompareTo(x.Value));
                                var sbc = new System.Text.StringBuilder("켜진 Update 스크립트:");
                                sbc.Append(" | 도는 트윈 ").Append(DG.Tweening.DOTween.TotalPlayingTweens()).Append("개, 전체 ").Append(DG.Tweening.DOTween.TotalActiveTweens()).Append("개 |");
                                for (int k = 0; k < tl.Count && k < 12; k++) sbc.Append(' ').Append(tl[k].Key.Name).Append('=').Append(tl[k].Value);
                                Log(sbc.ToString());
                            }
                            SlowScan.StartPan();
                            if (Hitch.TimeProbeOn) { TimeProbe.ResetSong(); TimeProbe.Force = true; }
                            Array.Copy(Main.TickCost, panTick0, panTick0.Length); panUpd0 = Main.UpdateTicks; panTimes.Clear();
                            panPhase = 1; panAt = now; panK = 0; panVis = panInvis = 0; panMs.Clear(); panWorst.Clear(); panLast = now;
                            return false;
                        }
                        float dtMs = (now - panLast) * 1000f; panLast = now;
                        if (panMs.Count > 0 || dtMs > 0) { panMs.Add(dtMs); panWorst.Add(new KeyValuePair<float, string>(dtMs, PhaseWatch.TopOfLastFrame(3) + " | 보임 " + panVisFrame + " 안보임 " + panInvisFrame)); }
                        panVisFrame = panInvisFrame = 0;
                        var p = cam.transform.position;
                        var lfp = ADOBase.lm != null ? ADOBase.lm.listFloors : null;
                        if (parts.Length > 2 && parts[2] == "tiles" && lfp != null && lfp.Count > 0)
                        {
                            // 타일 길을 따라 (초당 speed 타일) - 타일이 없는 빈 곳으로 가지 않게
                            panK += speed * Time.unscaledDeltaTime;
                            var fp = lfp[Mathf.Clamp((int)panK, 0, lfp.Count - 1)];
                            if (fp != null) { var q = fp.transform.position; cam.transform.position = new Vector3(q.x, q.y, -10f); }
                        }
                        else cam.transform.position = new Vector3(p.x + speed * Time.unscaledDeltaTime, p.y, -10f);
                        if (now - panAt < secs) return false;
                        panMs.Sort();
                        panWorst.Sort((a, b) => b.Key.CompareTo(a.Key));
                        float sum = 0; foreach (var v in panMs) sum += v;
                        var sb = new System.Text.StringBuilder();
                        sb.Append("카메라 옮기기 ").Append(secs).Append("초, 초당 ").Append(speed).Append(" 칸: 프레임 ").Append(panMs.Count).Append("개, 평균 ").Append((sum / Math.Max(1, panMs.Count)).ToString("F1"))
                          .Append("ms, 95% ").Append(panMs.Count > 0 ? panMs[(int)(panMs.Count * 0.95f)].ToString("F1") : "-").Append("ms, 최대 ").Append(panMs.Count > 0 ? panMs[panMs.Count - 1].ToString("F1") : "-")
                          .Append("ms | 타일 보임 ").Append(panVis).Append("번, 안보임 ").Append(panInvis).Append("번");
                        double tms = 1000.0 / System.Diagnostics.Stopwatch.Frequency; int pf = Math.Max(1, panMs.Count);
                        if (Hitch.TimeProbeOn) { TimeProbe.Force = false; TimeProbe.Report(); }
                        sb.Append("\n  함수별: ").Append(SlowScan.EndPan(pf));
                        sb.Append("\n  박자 알림: 건너뜀 ").Append(BeatFix.Skipped).Append(", 부름 ").Append(BeatFix.Called).Append(", 빠른 길 ").Append(BeatFix.Fast).Append(", 원래 반복 ").Append(BeatFix.Slow).Append(", 목록 틀림 ").Append(BeatFix.Mismatch).Append(", 다시 만들기 ").Append(BeatFix.Rebuilds).Append("번 ").Append(BeatFix.RebuildMs.ToString("F0")).Append("ms, 끝에 더하기 ").Append(BeatFix.Appends).Append("번");
                        sb.Append("\n  beatsettled: ").Append(BeatFix.NoChangeStatus);
                        sb.Append("\n  이 모드 OnUpdate 프레임당 ").Append(((Main.UpdateTicks - panUpd0) * tms / pf).ToString("F2")).Append("ms:");
                        for (int k = 0; k < panTick0.Length; k++) { double v = (Main.TickCost[k] - panTick0[k]) * tms / pf; if (v >= 0.05) sb.Append(' ').Append(Main.TickName[k]).Append(' ').Append(v.ToString("F2")); }
                        foreach (var kv in panTimes) sb.Append(" | ").Append(kv.Key).Append(' ').Append((kv.Value * tms / pf).ToString("F2")).Append("ms/프레임");
                        for (int i = 0; i < panWorst.Count && i < 5; i++) sb.Append("\n  ").Append(panWorst[i].Key.ToString("F1")).Append("ms: ").Append(panWorst[i].Value);
                        Log(sb.ToString());
                        panPhase = 0;
                        return true;
                    }
                case "culltest":
                    Log("화면 밖 타일 비교: " + TileCull.RenderCompare()); return true;
                case "framescan":
                    return FrameScan.Step(arg, now);
                case "slowscan":
                    SlowScan.Enabled = arg != "off"; return true;
                case "log":
                    Log(arg); return true;
                case "allocscan":
                    // 곡 중 누가 메모리를 잡는지 15초 동안 모은다 (F9 와 같음, 개발자용)
                    if (!Edition.Dev) { Log("할당 추적은 개발자용만"); return true; }
                    AllocScan.Toggle(); Log("할당 추적 시작");
                    return true;
                case "timeout":
                    float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out stepTimeout);
                    if (stepTimeout < 10f) stepTimeout = 180f;
                    Log("단계 최대 시간 " + stepTimeout + "초");
                    return true;
                case "alttab":
                    {
                        // 옆 스레드가 <지연>초 뒤 Alt+Tab 으로 다른 창으로 갔다가 <머묾>초 뒤 Alt+Tab 으로 돌아온다(사람이 창을 오가는 것 흉내).
                        // 바로 다음 play 의 멈춤 동안 하려고. 글자 입력은 없다.
                        var parts = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        float delay = 1f, hold = 2f;
                        if (parts.Length > 0) float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out delay);
                        if (parts.Length > 1) float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out hold);
                        new System.Threading.Thread(() =>
                        {
                            System.Threading.Thread.Sleep((int)(delay * 1000));
                            AltTab(); Main.Entry.Logger.Log("[자동 시험] Alt+Tab 나감 (옆 스레드)");
                            System.Threading.Thread.Sleep((int)(hold * 1000));
                            AltTab(); Main.Entry.Logger.Log("[자동 시험] Alt+Tab 돌아옴 (옆 스레드)");
                        }) { IsBackground = true, Name = "StutterFix.AutoTestAltTab" }.Start();
                        Log(string.Format("Alt+Tab 예약: {0}초 뒤 나갔다가 {1}초 뒤 돌아옴", delay, hold));
                        return true;
                    }
                case "game":
                    {
                        // 커스텀 맵 목록에서 고른 것처럼 게임 화면(scnGame)으로 연다 (scrController.LoadCustomLevel). 에디터를 거치지 않는다.
                        // 열린 뒤 곡은 "아무 키나 누르기"를 기다린다 -> press
                        if (gamePhase == 0)
                        {
                            if (!File.Exists(arg)) throw new Exception("맵 파일 없음: " + arg);
                            var ctrl = ADOBase.controller;
                            string sc = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                            if (ctrl == null || sc == "" || sc == "scnSplash" || sc == "scnLoading" || sc == "scnIntro" || now - stepStart < 3f) return false;   // 첫 메뉴가 뜨고 조금 뒤 (RestartAdvisor 와 같은 조건)
                            gameOld = ADOBase.customLevel;   // 이미 게임 화면이면 새 장면이 뜰 때까지 기다린다
                            ctrl.LoadCustomLevel(arg);
                            gamePhase = 1; openStartedAt = now; Log("게임 화면으로 맵 열기: " + arg);
                            return false;
                        }
                        if (gamePhase == 1)
                        {
                            if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "scnGame" || ADOBase.customLevel == null || ReferenceEquals(ADOBase.customLevel, gameOld) || ADOBase.customLevel.isLoading) return false;
                            gamePhase = 2; waitSec = now;
                            return false;
                        }
                        if (now - waitSec < 2f) return false;
                        gamePhase = 0; Log(string.Format("게임 화면 맵 열림 ({0:F1}초, 2초 기다림 포함)", now - openStartedAt));
                        return true;
                    }
                case "press":
                    {
                        // 키를 한 번 누른 것처럼: 다음 입력 확인(scrPlayerManager.AnyValidInputWasTriggered) 한 번만 참
                        if (pressHarmony == null)
                        {
                            pressHarmony = new Harmony(Main.Entry.Info.Id);   // 모드를 내릴 때 같이 풀린다
                            pressHarmony.Patch(AccessTools.Method(typeof(scrPlayerManager), "AnyValidInputWasTriggered"), prefix: new HarmonyMethod(typeof(AutoTest), nameof(PressPrefix)));
                        }
                        if (autoChanged) RDC.auto = desiredAuto;
                        pressPending = true; Log("키 누름");
                        BeginRun("게임 화면");
                        return true;
                    }
                case "retry":
                    {
                        // 에디터에서 죽은 뒤 키를 눌렀을 때와 같은 다시 하기 (scrController.ResetCustomLevel 코루틴)
                        var ctrl = ADOBase.controller;
                        if (ctrl == null) throw new Exception("scrController 없음");
                        if (ed != null && !ed.playMode) throw new Exception("재생 중이 아님");
                        if (autoChanged) RDC.auto = desiredAuto;
                        ctrl.StartCoroutine(ctrl.ResetCustomLevel());
#if AUTOTEST
                        DecoBake.ObserveGpuMergeRetry();
#endif
                        Log("다시 하기");
                        BeginRun("다시 하기");
                        return true;
                    }
                case "mark":
                    {
                        // 같은 판 안에서 재기 구간을 끊는다 (킥 전/뒤 비교). 새 구간은 0.5초 뒤부터
                        EndRun(); runNo++; runKind = arg; runStart = now - 1.5f;
                        return true;
                    }
                case "kick":
                    {
                        // 느린 판(화면 대기 1.7ms)을 빠른 판으로 바꿀 수 있는지 시험하는 한 번짜리 동작
                        var parts = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        string kind = parts.Length > 0 ? parts[0] : "";
                        int n = parts.Length > 1 ? int.Parse(parts[1]) : 1;
                        if (kind == "present") { for (int i = 0; i < n; i++) WindowGhost.PresentOnce(0); }          // 보통 Present(0,0) n번 (그래픽 스레드)
                        else if (kind == "presentdns") { for (int i = 0; i < n; i++) WindowGhost.PresentOnce(0x21); }
                        else if (kind == "freeze") System.Threading.Thread.Sleep(n);                                      // 메인 스레드 n ms 멈춤
                        else throw new Exception("모르는 킥: " + kind);
                        Log("킥: " + arg);
                        return true;
                    }
                case "tune":
                    PcTune.Measure();
                    Log("PC 맞춤 추천: " + string.Join(", ", PcTune.RecommendLabels().ToArray()));
                    return true;
                case "tunesim":
                    {
                        // 약한 PC(내장 그래픽 + 느린 CPU)인 척 적용했다가 되돌려, 설정이 전과 같은지 본다 (저장 파일도)
                        Func<string> snap = () => { var sb = new System.Text.StringBuilder(); foreach (var f in typeof(Settings).GetFields()) sb.Append(f.Name).Append("=").Append(Convert.ToString(f.GetValue(Main.Config), System.Globalization.CultureInfo.InvariantCulture)).Append(";"); return sb.ToString(); };
                        string before = snap();
                        PcTune.Measure(); PcTune.Integrated = PcTune.GpuWeak = PcTune.CpuWeak = true;
                        var done = PcTune.Apply();
                        string mid = snap();
                        PcTune.Undo();
                        string after = snap();
                        Log("PC 맞춤 시험 적용: " + string.Join(", ", done.ToArray()) + " | 바뀐 값 " + (before == mid ? "없음(이상)" : "있음") + " | 되돌린 뒤 " + (before == after ? "전과 같음" : "다름: " + Diff(before, after)));
                        PcTune.HasResult = false;
                        return true;
                    }
                case "setstate":
                    {
                        // scrController.currentState 를 직접 바꾼다. 에디터 밖에서 죽은 뒤 에디터로 오면 Fail 이 남아 있는 상황을 흉내 내려고.
                        var ctrl = ADOBase.controller;
                        if (ctrl == null) throw new Exception("scrController 없음");
                        var st = (States)Enum.Parse(typeof(States), arg, true);
                        AccessTools.FieldRefAccess<scrController, States>("currentState")(ctrl) = st;
                        Log("게임 상태를 " + st + " 로 바꿈");
                        return true;
                    }
                case "jiggle":
                    {
                        // 옆 스레드가 N초 동안 50ms 마다 마우스 커서를 몇 픽셀씩 움직인다(클릭 없음). 바로 다음 play 의 멈춤 동안
                        // 사람이 마우스를 건드린 것처럼 입력이 쌓이게 하려고. 끝나면 커서를 제자리로.
                        float sec; float.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out sec);
                        if (sec <= 0f) sec = 5f;
                        int ms = (int)(sec * 1000);
                        new System.Threading.Thread(() =>
                        {
                            POINT p0; GetCursorPos(out p0);
                            var sw = System.Diagnostics.Stopwatch.StartNew(); int n = 0;
                            while (sw.ElapsedMilliseconds < ms) { n++; SetCursorPos(p0.x + (n % 2 == 0 ? 6 : -6), p0.y + (n % 4 < 2 ? 4 : -4)); System.Threading.Thread.Sleep(50); }
                            SetCursorPos(p0.x, p0.y);
                        }) { IsBackground = true, Name = "StutterFix.AutoTestJiggle" }.Start();
                        Log("마우스 움직이기 " + sec + "초 (클릭 없음)");
                        return true;
                    }
                case "auto":
                    {
                        bool on = arg.Equals("on", StringComparison.OrdinalIgnoreCase);
                        if (!autoChanged) { prevAuto = RDC.auto; autoChanged = true; }
                        desiredAuto = on; RDC.auto = on; Log("자동 플레이 " + (on ? "켬" : "끔") + " (재생 때마다 다시 맞춤)");
                        return true;
                    }
                case "open":
                    if (openPhase == 0)
                    {
                        if (!File.Exists(arg)) throw new Exception("맵 파일 없음: " + arg);
                        RestartAdvisor.BeginOpen(arg); openPhase = 1; openStartedAt = now; Log("맵 열기: " + arg);
                        StartMemWatch();
                        return false;
                    }
                    if (openPhase == 1)
                    {
                        if (RestartAdvisor.Opening || ed == null) return false;
                        if (loadingField != null && (bool)loadingField.GetValue(ed)) return false;
                        if (!SamePath(ADOBase.levelPath, arg)) return false;
                        openPhase = 2; waitSec = now;
                        return false;
                    }
                    if (now - waitSec < 2f) return false;   // 열린 뒤 2초 (이미지 결과 창 등 정리)
                    openPhase = 0; memWatchGen++; Log(string.Format("맵 열림 ({0:F1}초, 열기 요청부터 2초 기다림 포함)", now - openStartedAt));
                    return true;
                case "select":
                    { if (ed == null) throw new Exception("에디터가 아님"); int si = int.Parse(arg); ed.SelectFloor(ADOBase.lm.listFloors[Math.Min(si, ADOBase.lm.listFloors.Count - 1)], true); Log("타일 " + si + " 선택"); return true; }
                case "seek":
                    {
                        // seek <곡 초> [앞 여유 초=8]: 그 시점 조금 앞 타일을 골라 재생한다 (곡 전체를 돌리지 않고 볼 구간만)
                        if (ed == null) throw new Exception("에디터가 아님");
                        var sp = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        double at = double.Parse(sp[0], System.Globalization.CultureInfo.InvariantCulture);
                        double lead = sp.Length > 1 ? double.Parse(sp[1], System.Globalization.CultureInfo.InvariantCulture) : 8;
                        if (double.IsNaN(at) || double.IsInfinity(at) || at < 0 || double.IsNaN(lead) || double.IsInfinity(lead) || lead < 0)
                            throw new Exception("seek: 곡 시각과 앞 여유는 유한한 0 이상 값이어야 함");
                        if (ed.playMode) throw new Exception("seek: stop 후 편집 상태에서 실행해야 함");
                        var fl = ADOBase.lm.listFloors; int pick = -1;
                        for (int i = 0; i < fl.Count; i++) if (fl[i] != null && fl[i].entryTime >= at - lead) { pick = i; break; }
                        // 범위 밖 요청/아직 준비되지 않은 entryTime을 맵 끝 타일로 바꾸면 즉시 완주해 원인을 숨긴다.
                        if (pick < 0 || (pick == fl.Count - 1 && at - lead > 0))
                            throw new Exception("seek: 요청 앞 여유 시각 " + (at - lead).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
                                + "초에 재생 가능한 타일이 없음 (타일 수=" + fl.Count + ", 맵/entryTime 확인)");
                        ed.SelectFloor(fl[pick], true);
                        Log(string.Format("{0:F1}초 {1:F0}초 앞: 타일 {2} (그 타일 {3:F1}초) 선택", at, lead, pick, fl[pick].entryTime));
                        if (autoChanged) RDC.auto = desiredAuto;
                        Log("재생 (자동 플레이 " + (RDC.auto ? "켬" : "끔") + ")");
                        ed.Play();
                        BeginRun("Play");
                        return true;
                    }
                case "play":
                    if (ed == null) throw new Exception("에디터가 아님");
                    if (ed.playMode) { Log("이미 재생 중"); return true; }
                    if (autoChanged) RDC.auto = desiredAuto;   // 메뉴에서 켠 값이 에디터에 들어가며 풀렸다(2026-09-27)
                    Log("재생 (자동 플레이 " + (RDC.auto ? "켬" : "끔") + ")");
                    ed.Play();
                    BeginRun("Play");
                    return true;
                case "stop":
                    if (ed == null) throw new Exception("에디터가 아님");
                    EndRun();
                    var stopSw = System.Diagnostics.Stopwatch.StartNew();
                    if (ed.playMode) ed.SwitchToEditMode();
                    Log("편집으로 돌아감 (" + stopSw.ElapsedMilliseconds + "ms)");
                    return true;
                case "quit":
                    FrameGen.Quit();
                    keep = false;
                    Finish("끝");
                    return true;
#if DEV || AUTOTEST
                case "animbench": return MicroBench.Step("animbench", arg);
                case "matbench": return MicroBench.Step("matbench", arg);
                case "instbench": return InstBench.Step(arg);
                case "meshbench": return MeshBench.Step(arg);
                case "decosim": DecoSim.Command(arg); return true;
                case "decobake": return DecoBake.Step(arg);
                case "gpudeco": return DecoBake.GpuStep(arg);
                case "beatprobe": Log("[beatprobe] " + BeatProbe.Run()); return true;
                case "beatskip": BeatFix.DiagSkipFloors = arg == "on"; Log("[beatskip] " + BeatFix.DiagSkipFloors); return true;
                case "beatsettled":
                    if (arg != "on" && arg != "off") throw new Exception("beatsettled on|off");
                    Main.Config.BeatSkipNoChange = arg == "on";
                    BeatFix.SetSkipNoChange(Main.Config.BeatSkipNoChange);
                    Main.Config.Save(Main.Entry);
                    Log("[beatsettled] " + BeatFix.NoChangeStatus); return true;
                case "tilesim": TileSim.Command(arg); return true;
                case "tilecost": TileCost.Command(arg); return true;
                case "tilerender": return TileRender.Step(arg.Trim());
#if AUTOTEST
                case "scriptskip": return ScriptPath.Step(arg.Trim());
                case "gpuexact": return GpuExactCensus.Step(arg.Trim());
                case "engknob": return EngineKnob.Step(arg.Trim());
                case "presentpath": return PresentPath.Step(arg.Trim());
#endif
                case "fxslot": FxSlots.Command(arg); return true;
                case "filterhalfperf": FilterHalfPerf.Command(arg); return true;
                case "filterhalf": FilterHalfProbe.Command(arg); return true;
                case "waitsong": return now - stepStart > 5f && !Hitch.Playing;
                case "filtergpu": FilterGpuProbe.Command(arg); return true;
                case "gpuprobe": GpuProbe.Command(arg); return true;
                case "archegpu": ArcheGpuResearch.Command(arg); return true;
                case "fxoff": ArcheGpuResearch.Select(arg); return true;
                case "gpucensus": GpuCensus.Run(); return true;
                case "repeataudit": Log(RepeatRendererAudit.Run(arg.Trim())); return true;
                case "spritediscard": Log(SpriteDiscardResearch.Command(arg.Trim())); return true;
                case "spritechannels": Log(SpriteChannelResearch.Command(arg.Trim())); return true;
                case "cpuwindow": Log(CpuWindowProbe.Command(arg.Trim())); return true;
                case "leafprobe": Log(LeafProbe.Command(arg.Trim())); return true;
                case "gpuprio": Log("[gpuprio] " + GpuPriority.Command(arg)); return true;
                case "guiprobe":
                    {
                        // 진단: OnGUI 가 있는 켜진 컴포넌트 목록 / UMM 창 컴포넌트 끄기(창이 닫혀 있어도 매 프레임 IMGUI 이벤트 2번을 받는다)
                        var a0 = arg.Trim();
                        if (a0 == "list")
                        {
                            var sb = new System.Text.StringBuilder(); int n = 0;
                            foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                            {
                                if (!mb || !mb.enabled) continue;
                                var t = mb.GetType();
                                if (t.GetMethod("OnGUI", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic) == null) continue;
                                n++; sb.Append(t.FullName).Append("(layout=").Append(mb.useGUILayout).Append(") ");
                            }
                            Log("[guiprobe] OnGUI 컴포넌트 " + n + "개: " + sb); return true;
                        }
                        var ui = UnityModManagerNet.UnityModManager.UI.Instance;
                        if (ui) { ui.enabled = a0 != "ummoff"; Log("[guiprobe] UMM UI enabled=" + ui.enabled); } else Log("[guiprobe] UMM UI 없음");
                        return true;
                    }
#if AUTOTEST
                case "srcdebug": if (arg.Trim() == "phases") { Log("[srcdebug] phases " + DecoBake.PhReport()); return true; } if (arg.Trim() == "check") { Log("[srcdebug] check " + DecoBake.SrcCheck()); return true; } DecoBake.SrcDebug = int.Parse(arg.Trim()); Log("[srcdebug] " + DecoBake.SrcDebug + " avg_active=" + (DecoBake.SrcFrames > 0 ? (DecoBake.SrcActiveSum / (double)DecoBake.SrcFrames).ToString("F1") : "0")); DecoBake.SrcActiveSum = 0; DecoBake.SrcFrames = 0; return true;
#endif
                case "layercensus":
                    {
                        var counts = new System.Collections.Generic.Dictionary<string, int>();
                        foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
                        {
                            if (!r || !r.gameObject.scene.IsValid()) continue;
                            int ly = r.gameObject.layer; if (ly != 10 && ly != 11) continue;
                            bool act = r.enabled && r.gameObject.activeInHierarchy && !r.forceRenderingOff;
                            string extra = "";
                            if (act)
                            {
                                var m = r.sharedMaterial; var sr = r as SpriteRenderer;
                                extra = " sh=" + (m && m.shader ? m.shader.name : "-") + " q=" + (m ? m.renderQueue : 0)
                                    + (m && m.HasProperty("_Color") ? " matA=" + m.GetColor("_Color").a.ToString("F2") : "")
                                    + (sr ? " sprite=" + (sr.sprite ? sr.sprite.name : "null") + " colA=" + sr.color.a.ToString("F2") : "")
                                    + " size=" + r.bounds.size.x.ToString("F0") + "x" + r.bounds.size.y.ToString("F0");
                            }
                            string key = "L" + ly + " " + r.GetType().Name + (act ? " ACTIVE" : " off") + " " + r.gameObject.name + extra;
                            counts.TryGetValue(key, out int c); counts[key] = c + 1;
                        }
                        var sb = new System.Text.StringBuilder(); foreach (var kv in counts) sb.Append(kv.Value).Append("x ").Append(kv.Key).Append(" | ");
                        foreach (var c in Camera.allCameras) sb.Append("CAM ").Append(c.name).Append(" mask=").Append(c.cullingMask).Append(" depth=").Append(c.depth).Append(" clear=").Append(c.clearFlags).Append(" | ");
                        foreach (var cv in Resources.FindObjectsOfTypeAll<Canvas>()) if (cv && cv.gameObject.scene.IsValid() && (cv.gameObject.layer == 10 || cv.gameObject.layer == 11)) sb.Append("CANVAS ").Append(cv.name).Append(" layer=").Append(cv.gameObject.layer).Append(" | ");
                        Log("[layercensus] " + sb); return true;
                    }
                case "flashskip": { var a0 = arg.Trim(); if (a0 == "on" || a0 == "off") FlashSkip.SetEnabled(a0 == "on"); Log("[flashskip] " + FlashSkip.State()); return true; }
                case "flashcolor":
                    {
                        // 시험 전용: 번쩍임 사각형(Bg·Fg, 옛 scrFlash)의 재질 색을 직접 정한다. 인자: r g b a
                        var q = arg.Trim().Split(' '); var col = new Color(float.Parse(q[0], System.Globalization.CultureInfo.InvariantCulture), float.Parse(q[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(q[2], System.Globalization.CultureInfo.InvariantCulture), float.Parse(q[3], System.Globalization.CultureInfo.InvariantCulture));
                        int cn = 0; var cam0 = scrCamera.instance;
                        if (cam0 && cam0.flashPlusRendererBg) { cam0.flashPlusRendererBg.material.color = col; cn++; }
                        if (cam0 && cam0.flashPlusRendererFg) { cam0.flashPlusRendererFg.material.color = col; cn++; }
                        foreach (var f in UnityEngine.Object.FindObjectsByType<scrFlash>(FindObjectsInactive.Include, FindObjectsSortMode.None)) { var rr = f.GetComponent<Renderer>(); if (rr) { rr.material.color = col; cn++; } }
                        Log("[flashcolor] " + col + " renderers=" + cn); return true;
                    }
                case "decodetach": Log("[decodetach] " + DecoDetach.Command(arg)); return true;
                case "zcull": return ZCull.Step(arg);
                case "occlude": if (arg.Trim() == "collect") { ShapeCellsCpu.CollectOpaque = true; Log("[occlude] 다음 맵 열기부터 불투명 정보 수집"); return true; } Occlude.Command(arg); return true;
                case "gpuhide": GpuHide.Command(arg); return true;
                case "runlod": Log("[runlod] " + RunLod.Command(arg)); return true;
                case "imagearrayreuse": Log("[imagearrayreuse] " + DecoBake.ImageArrayReuseCommand(arg)); return true;
                case "runprobe": Log("[runprobe] " + RunProbe.Run(float.TryParse(arg.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rpm) ? rpm : 4f)); return true;
                case "hidetex":
                    {
                        // 측정 전용: 이름에 arg 가 들어간 텍스처를 쓰는 SpriteRenderer 를 그리지 않게 한다(새로 생기는 것 포함, 매 호출 다시 적용).
                        var key = arg.Trim(); int n = 0;
                        foreach (var sr in UnityEngine.Object.FindObjectsByType<SpriteRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                            if (sr.sprite && sr.sprite.texture && sr.sprite.texture.name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) { sr.forceRenderingOff = true; n++; }
                        Log("[hidetex] " + key + " " + n); return true;
                    }
                case "decotexmin": GpuHide.DecoTexMin = int.Parse(arg.Trim()); Log("[decotexmin] " + GpuHide.DecoTexMin); return true;
                case "hidegroup": { var q = arg.Split(' '); Log("[hidegroup] " + q[0] + " " + GpuHide.Toggle(q[0], q.Length > 1 && q[1] == "on")); return true; }
                case "holdskip": HoldFix.Command(arg); return true;
                case "waitskip": EnginePatch.WaitSkip(arg != "off"); return true;
                case "screenres":
                    {
                        // 해상도 변경 재현: screenres <w> <h> [fsw|win|excl]
                        var q = arg.Split(' ');
                        var m = q.Length > 2 && q[2] == "win" ? FullScreenMode.Windowed : q.Length > 2 && q[2] == "excl" ? FullScreenMode.ExclusiveFullScreen : FullScreenMode.FullScreenWindow;
                        Log("[screenres] 요청 " + q[0] + "x" + q[1] + " " + m + " (지금 " + Screen.width + "x" + Screen.height + " " + Screen.fullScreenMode + ")");
                        Screen.SetResolution(int.Parse(q[0]), int.Parse(q[1]), m);
                        return true;
                    }
                case "maskgrow": EnginePatch.MaskGrow(arg != "off"); return true;
                case "latprobe": EnginePatch.LatencyCommand(arg); return true;
                case "turbo": EnginePatch.TurboCommand(arg); return true;
#if AUTOTEST
                case "texdensity": TexDensity.Run(arg); return true;
                case "stencilprobe": Log("[stencilprobe] " + StencilProbe.Run(arg.Trim())); return true;
                case "lodprobe": Log("[lodprobe] " + LodProbe.Run(arg.Trim())); return true;
                case "nodirect": Log("[nodirect] " + LodProbe.NoDirect(arg.Trim() != "off")); return true;
                case "screenfixed": DrawCollapsePolicy.ScreenFixed = arg.Trim() != "off"; Log("[screenfixed] " + DrawCollapsePolicy.ScreenFixed); return true;
                case "repeat": DrawCollapsePolicy.Repeat = arg.Trim() != "off"; Log("[repeat] " + DrawCollapsePolicy.Repeat); return true;
                case "sfflat": Shader.SetGlobalFloat("_SfDebugFlat", arg.Trim() == "off" ? 0f : 1f); Log("[sfflat] " + arg.Trim()); return true;
                case "sfsimple": Log("[sfsimple] " + LodProbe.Simple(arg.Trim() != "off")); return true;
                case "coverage": Log("[coverage] " + DecoBake.CoverageProbe()); return true;
                case "depthinst": { var a0 = arg.Trim(); DrawCollapsePolicy.DepthInstances = a0 != "off"; if (int.TryParse(a0, out int mx) && mx > 1) DrawCollapsePolicy.MaxDepthInstances = mx; Log("[depthinst] " + DrawCollapsePolicy.DepthInstances + " max=" + DrawCollapsePolicy.MaxDepthInstances); return true; }
                case "blendgrab": DrawCollapsePolicy.BlendGrab = arg.Trim() != "off"; Log("[blendgrab] " + DrawCollapsePolicy.BlendGrab); return true;
                case "ownprobe": Log("[ownprobe] " + DecoBake.OwnProbe(arg)); return true;
                case "sfnodiscard": Shader.SetGlobalFloat("_SfNoDiscard", arg.Trim() == "off" ? 0f : 1f); Log("[sfnodiscard] " + arg.Trim()); return true;
                case "sfdebug": Shader.SetGlobalFloat("_SfDebugMode", float.Parse(arg.Trim(), System.Globalization.CultureInfo.InvariantCulture)); Log("[sfdebug] " + arg.Trim()); return true;
                case "blendmodes": DrawCollapsePolicy.BlendModes = arg.Trim() != "off"; Log("[blendmodes] " + DrawCollapsePolicy.BlendModes); return true;
                case "oversized": DrawCollapsePolicy.AllowOversized = arg.Trim() != "off"; Log("[oversized] " + DrawCollapsePolicy.AllowOversized); return true;
                case "globaldirect": DrawCollapsePolicy.GlobalDirect = arg.Trim() != "off"; Log("[globaldirect] " + DrawCollapsePolicy.GlobalDirect); return true;
                case "fewdirect": DrawCollapsePolicy.FewDirect = arg.Trim() != "off"; Log("[fewdirect] " + DrawCollapsePolicy.FewDirect); return true;
                case "renderlod":
                    {
                        var ra = arg.Trim();
                        if (ra == "state") { Log("[renderlod] " + DecoBake.LodState()); return true; }
                        if (ra.StartsWith("max ")) { DecoBake.RenderLod.MaxSpans = int.Parse(ra.Substring(4)); Log("[renderlod] max=" + DecoBake.RenderLod.MaxSpans); return true; }
                        if (ra == "off") { DecoBake.RenderLod.Enabled = false; DecoBake.LodRelease(); }
                        else { DecoBake.RenderLod.Enabled = true; if (float.TryParse(ra, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rq) && rq > 0) DecoBake.RenderLod.Quality = rq; }
                        Log("[renderlod] " + DecoBake.LodState()); return true;
                    }
                case "tightshape":
                    { var ta = arg.Trim(); TightSprite.UnityTight = ta.StartsWith("unity"); if (TightSprite.UnityTight) ta = ta.Substring(5).Trim();
                      if (int.TryParse(ta, out var tg) && tg >= 4) { TightSprite.Grid = tg; TightSprite.ForgetLevel(); } }
                    Log("[tightshape] grid=" + TightSprite.Grid + " " + (arg.Trim() == "off" ? TightSprite.Restore() : TightSprite.Apply())); return true;
                case "clipprobe": Log("[clipprobe] " + StencilProbe.Clip(arg.Trim())); return true;
#endif
                case "pace": Main.Entry.Logger.Log("[pace] 프레임 " + DisplayPace.Frames + " 그림 " + DisplayPace.Draws); return true;
#if AUTOTEST
                case "pacecheck": DisplayPace.Check(arg == "start"); return true;
                case "waitutc": return DateTime.UtcNow.Ticks >= long.Parse(arg);
#endif
                case "rtcensus": RuntimeCensus.Run(arg); return true;
                case "rdcapture": RuntimeCensus.Capture(); return true;
                case "dormfilter": Dormancy.FilterEveryFrame = arg == "every"; return true;
                case "gpusplit": GpuSplit.Command(arg); return true;
#if DEV || AUTOTEST
                case "matshare": MatShare.Command(arg); return true;
                case "livedeco": return LiveDeco.Step(arg);
                case "livedeco2": return LiveDecoGate2.Step(arg);
#endif
                case "filteridentity":
                    FilterIdentityProbe.Command(arg); return true;
                case "patchhot":
                    switch (arg.Trim().ToLowerInvariant())
                    {
                        case "on": PatchHot.On(); break;
                        case "off": PatchHot.Off(); break;
                        case "state": PatchHot.State(); break;
                        default: throw new Exception("patchhot on|state|off");
                    }
                    return true;
                case "wasteprobe":
                    switch (arg.Trim().ToLowerInvariant())
                    {
                        case "on": WasteProbe.On(); break;
                        case "off": WasteProbe.Off(); break;
                        case "state": WasteProbe.State(); break;
                        case "reset": WasteProbe.Reset(); break;
                        default: throw new Exception("wasteprobe on|off|state|reset");
                    }
                    return true;
#endif
 #if DEV || AUTOTEST
                case "freefps":
                    FreeFpsProbe.Command(arg); return true;
#endif
                case "filterstate":
                {
                    // filterstate: 지금 켜진 카메라 필터(CameraFilterPack)와 공개 숫자·참거짓 값 (세기 0 인데 켜진 필터 찾기)
                    var sbf = new System.Text.StringBuilder();
                    int on = 0;
                    foreach (var cam0 in Camera.allCameras)
                        foreach (var mb in cam0.GetComponents<MonoBehaviour>())
                        {
                            if (mb == null || !mb.enabled) continue;
                            var t = mb.GetType();
                            if (!t.Name.StartsWith("CameraFilterPack_")) continue;
                            on++;
                            sbf.Append(" | ").Append(cam0.name).Append(':').Append(t.Name.Substring(17)).Append('(');
                            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                            {
                                object v = f.GetValue(mb);
                                if (v is float fv) sbf.Append(f.Name).Append('=').Append(fv.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
                                else if (v is int iv) sbf.Append(f.Name).Append('=').Append(iv).Append(' ');
                                else if (v is bool bv) sbf.Append(f.Name).Append('=').Append(bv ? 1 : 0).Append(' ');
                            }
                            sbf.Append(')');
                        }
                    Log("[필터 상태] 곡 " + (scrConductor.instance != null ? scrConductor.instance.songposition_minusi.ToString("F1") : "-") + "초, 켜진 필터 " + on + "개" + sbf);
                    return true;
                }
                case "fgstate":
                    Log("[프레임상태] " + FrameGen.Describe());
                    Log("[화면상태] " + Main.DescribeDisplay());
                    Log("[프레임저장상태] " + FrameGen.DescribeStorage());
                    Log("[프레임시각] frame=" + Time.frameCount + " ticks=" + System.Diagnostics.Stopwatch.GetTimestamp() + " hz=" + System.Diagnostics.Stopwatch.Frequency);
                    return true;
                case "shaders":
                    ScreenEffects.Inventory();return true;
                case "fxstate":
                    Log("[화면효과상태] "+ScreenEffects.Describe());
#if DEV || AUTOTEST
                    // Explicit diagnostic only; no new per-frame work. The effect
                    // belongs to its recorded frame, not automatically to this one.
                    Log(FormattableString.Invariant($"[화면효과시각] song_s={(scrConductor.instance!=null?scrConductor.instance.songposition_minusi:double.NaN):R} unity_frame={Time.frameCount} effect_frame={ScreenEffects.LastFrame} glow={ScreenEffects.LastGlow}"));
#endif
                    return true;
#if DEV || AUTOTEST
                case "fxinput":
                    {var input=arg.Split(new[]{' '},2);if(input.Length!=2)throw new Exception("fxinput <slider key> <text>");SettingsWindow.InputFxForTest(input[0],input[1]);return true;}
                case "fxpersist":
                    {
                        var stored=UnityModManagerNet.UnityModManager.ModSettings.Load<Settings>(Main.Entry);int fields=0,bad=0;
                        foreach(var field in typeof(Settings).GetFields())if(field.Name.StartsWith("Fx") && field.FieldType==typeof(float)){fields++;if(!field.GetValue(stored).Equals(field.GetValue(Main.Config)))bad++;}
                        Log("[화면효과저장] float_fields="+fields+" mismatches="+bad+" preset="+stored.FxPreset);
                        if(bad!=0 || stored.FxPreset!=4)throw new Exception("Custom effect values not persisted");return true;
                    }
                case "fxshare":
                    {
                        // 공유 코드 왕복: 내보내기 → 값 바꿈 → 가져오기로 원래 값 복구, 깨진 코드는 거부하고 아무것도 안 바꿈
                        var c=Main.Config;var fields=new System.Collections.Generic.List<System.Reflection.FieldInfo>();
                        foreach(var field in typeof(Settings).GetFields(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Instance))
                            if(field.Name.StartsWith("Fx",StringComparison.Ordinal) && field.Name!="FxLutPath" && (field.FieldType==typeof(bool)||field.FieldType==typeof(int)||field.FieldType==typeof(float)))fields.Add(field);
                        var before=new object[fields.Count];for(int i=0;i<fields.Count;i++)before[i]=fields[i].GetValue(c);
                        string code=FxShare.Export(c);string lut=c.FxLutPath;
                        bool hdr=c.FxHdr;float amount=c.FxHdrAmount;
                        c.FxHdr=!hdr;c.FxHdrAmount=amount>.5f?.1f:1.2f;
                        string message;bool ok=FxShare.Import("받은 코드: "+code+" 끝",c,out message);
                        int mismatch=0;for(int i=0;i<fields.Count;i++)if(!before[i].Equals(fields[i].GetValue(c)))mismatch++;
                        c.FxHdr=false;
                        bool badA=FxShare.Import("SFX1:!!!!",c,out message),badB=FxShare.Import("nothing",c,out message),badC=FxShare.Import("SFX1:"+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("HdrAmount=NaN;Hdr=1")),c,out message);
                        bool unchanged=!c.FxHdr;
                        bool unknown=FxShare.Import("SFX1:"+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("FutureField=3;Hdr=1;LutPath=x")),c,out message);
                        bool noPath=c.FxLutPath==lut && !System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(code.Substring(5))).Contains("LutPath");
                        Log("[공유 시험] fields="+fields.Count+" roundtrip_ok="+ok+" mismatches="+mismatch+" bad_rejected="+(!badA&&!badB&&!badC)+" bad_unchanged="+unchanged+" unknown_field_ok="+unknown+" hdr_after_unknown="+c.FxHdr+" lut_path_kept="+noPath);
                        bool pass=ok&&mismatch==0&&!badA&&!badB&&!badC&&unchanged&&unknown&&c.FxHdr&&noPath;
                        for(int i=0;i<fields.Count;i++)fields[i].SetValue(c,before[i]);   // 원래 값으로
                        if(!pass)throw new Exception("공유 코드 시험 실패");
                        return true;
                    }
                case "fxpreset":
                    {
                        int preset=int.Parse(arg); if(preset<0 || preset>6 || preset==4) throw new Exception("미리 설정 0~3 또는 후보5·6 필요");
                        foreach(var field in typeof(Settings).GetFields(System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.Instance))
                            if(field.Name.StartsWith("Fx",StringComparison.Ordinal) && !setOrig.ContainsKey(field)) setOrig[field]=field.GetValue(Main.Config);
                        ScreenEffects.Preset(preset); Main.ApplyConfig();
                        Log("화면 효과 미리 설정: "+preset); return true;
                    }
                case "shotat":
                case "fxshotat":
                    {
                        // Explicit visual command only. Poll until the requested song
                        // time; the ordinary shot path captures the end of that frame.
                        var pair=arg.Split(new[]{' '},2,StringSplitOptions.RemoveEmptyEntries);
                        if(pair.Length!=2) throw new Exception("shotat <곡 초> <이름>");
                        double target=double.Parse(pair[0],System.Globalization.CultureInfo.InvariantCulture);
                        if(double.IsNaN(target) || double.IsInfinity(target) || target<0) throw new Exception("유효한 곡 시간 필요");
                        if(!Hitch.Playing || scrConductor.instance==null || scrConductor.instance.songposition_minusi<target) return false;
                        Log(FormattableString.Invariant($"[예약 캡처] target_song_s={target:R} request_song_s={scrConductor.instance.songposition_minusi:R} unity_frame={Time.frameCount}"));
                        Log(FormattableString.Invariant($"[예약 화면] window={Screen.width}x{Screen.height} display={Screen.currentResolution.width}x{Screen.currentResolution.height} hz={Screen.currentResolution.refreshRateRatio.value:R} sync={QualitySettings.vSyncCount}"));
                        if(cmd=="fxshotat") {ScreenEffects.TimedShot(pair[1],target);return true;}
                        arg=pair[1]; goto case "shot";
                    }
                case "fxcapture":
                    ScreenEffects.Capture();return true;
                case "fxfixture":
                    ScreenEffects.Fixture();return true;
                case "fxgallery":
                    ScreenEffects.Gallery(arg);return true;
                case "fxbench":
                    EffectsGpuProbe.Start(arg);return true;
                case "fxbenchreport":
                    EffectsGpuProbe.Report();return true;
#endif
#if DEV || AUTOTEST
                case "fgmonitorhold":
                    PerfOverlay.TestFreezeOutputSample(arg=="on");
                    Log("[출력 모니터 합성 시험] hold="+(arg=="on"));return true;
#endif
                case "fgmonitor":
                    Log("[출력 모니터] " + PerfOverlay.DescribeFps());
                    return true;
#if DEV || AUTOTEST
                case "profilestate":
                    Log("[맵 프로필 경로] load="+MapProfiles.LoadCalls+" loaded="+MapProfiles.LoadedCalls+" finish="+MapProfiles.FinishCalls+" miss="+MapProfiles.KeyMisses+" arg="+MapProfiles.LastArgument);
                    Log("[맵 프로필 시험] key="+MapProfiles.CurrentKey+" enabled="+MapProfiles.Enabled+" vibrance="+Main.Config.FxVibrance.ToString("R",System.Globalization.CultureInfo.InvariantCulture)+" status="+MapProfiles.Status);return true;
                case "threadfixture":Log("[스레드 예약 시험] "+ThreadScheduling.Fixture());return true;
                case "animationaudit":AnimationAudit.Run();return true;
                case "tagprimitivestate":Log("[태그 번호 영역] enabled="+TagIndex.Enabled+" requested="+TagIndex.NativePlans+" used_ints="+TagIndex.PrimitiveUsed+" capacity_ints="+TagIndex.PrimitiveCapacity+" hits="+TagIndex.Hits+" compiled="+TagIndex.Compiled+" invalidations="+TagIndex.Invalidations);return true;
                case "tagprimitiveverify":Log("[태그 번호 원본 대조] "+TagIndex.VerifyPrimitivePlans());return true;
                case "tagprimitivelifetime":Log("[태그 번호 수명 대조] "+TagIndex.PrimitiveLifetimeFixture());return true;
                case "heatmapstate":Log("[상위 타일 완주 상태] enabled="+HitchHeatmap.Enabled+" portal_completed="+HitchHeatmap.CompletedForFixture+" summary="+HitchHeatmap.Describe().Replace("\n"," | "));return true;
                case "timerperiodstate":Log("[물리 상태] "+PhysicsPause.State);Log("[타이머 요청 소유] requested="+LowEnd.NoThrottle+" owned="+LowEnd.TimerOwnedForFixture+" nt_requested="+LowEnd.HalfMsTimer+" nt_owned="+LowEnd.NtTimerOwned+" "+TimerResolutionInfo.Read());return true;
                case "reportclipboardfixture":
                {
                    string previous=GUIUtility.systemCopyBuffer,error=LogExport.LastError;
                    bool equal=false,restored=false;int length=0;
                    try
                    {
                        string expected=LogExport.Markdown();length=expected.Length;LogExport.CopyMarkdown();
                        equal=LogExport.LastError.Length==0&&GUIUtility.systemCopyBuffer==expected;
                    }
                    finally
                    {
                        GUIUtility.systemCopyBuffer=previous;restored=GUIUtility.systemCopyBuffer==previous;
                        LogExport.LastError=error;
                    }
                    Log("[보고서 복사 API 시험] equal="+equal+" original_clipboard_restored="+restored+" report_characters="+length+" external_messages=0");return true;
                }
                case "threadstate":Log("[스레드 예약 상태] "+ThreadScheduling.Status);return true;
                case "renderthreadstate":Log("[렌더 스레드 예약 상태] "+RenderThreadScheduling.FixtureState);return true;
                case "renderthreadstopfixture":RenderThreadScheduling.Shutdown();Log("[렌더 스레드 내리기 시험] "+RenderThreadScheduling.FixtureState);return true;
                case "constantringstate":Log("[보간 상수 버퍼 링] "+FrameGen.DescribeConstantRing());return true;
                case "tilesig": Log("[타일 상태 기록] "+TileSig.Run(modDir,arg)); Log("[색 바꾸기 미루기] "+RecolorDefer.State); return true;
                case "matstate": Log("[재질 만들기] "+MatProbe.State); return true;
                case "psdump": Log("[파티클 목록] "+ParticleDump.Run(arg.Length>0?int.Parse(arg):8)); return true;
                case "floordiag":
                {
                    var fls = ADOBase.lm == null ? null : ADOBase.lm.listFloors;
                    if (fls == null) { Log("[타일 진단] 목록 없음"); return true; }
                    int n = 0, scriptOn = 0, rootOn = 0, glowNull = 0, glowOn = 0, glowActive = 0, glowAlpha0 = 0, glowVisible = 0;
                    foreach (var f in fls)
                    {
                        if (f == null) continue; n++;
                        if (f.enabled) scriptOn++;
                        var rr = f.floorRenderer != null ? f.floorRenderer.renderer : null;
                        if (rr != null && rr.enabled) rootOn++;
                        var g = f.topGlow;
                        if (g == null) { glowNull++; continue; }
                        if (g.enabled) glowOn++;
                        if (g.gameObject.activeInHierarchy) glowActive++;
                        if (g.enabled && g.gameObject.activeInHierarchy && g.color.a <= 0f) glowAlpha0++;
                        if (g.isVisible) glowVisible++;
                    }
                    Log("[타일 진단] 타일 " + n + " 스크립트 켜짐 " + scriptOn + " 본체 렌더러 켜짐 " + rootOn + " | 발광(topGlow) 없음 " + glowNull + " 켜짐 " + glowOn + " 활성 " + glowActive + " 켜짐인데 알파0 " + glowAlpha0 + " 보임 " + glowVisible);
                    return true;
                }
                case "decosig": Log("[장식 상태 기록] "+DecoSig.Run(modDir,arg)+" | "+LazySprite.State); return true;
                case "hiddensynergy": Log("[hiddensynergy] " + HiddenDecoSynergy.Command(arg)); return true;
                case "onupdate":
                    if (arg == "begin") { Array.Copy(Main.TickCost, onUpd0, Main.TickCost.Length); onUpdTotal0 = Main.UpdateTicks; onUpdFrame = Time.frameCount; return true; }
                    {
                        double tms = 1000.0 / System.Diagnostics.Stopwatch.Frequency; int pf = Math.Max(1, Time.frameCount - onUpdFrame);
                        var sbo = new System.Text.StringBuilder("[onupdate] frames=" + pf + " total_ms=" + ((Main.UpdateTicks - onUpdTotal0) * tms / pf).ToString("F3") + " |");
                        for (int k = 0; k < Main.TickCost.Length; k++) { double v = (Main.TickCost[k] - onUpd0[k]) * tms / pf; if (v >= 0.005) sbo.Append(' ').Append(k < Main.TickName.Length ? Main.TickName[k] : k.ToString()).Append(' ').Append(v.ToString("F3")); }
                        Log(sbo.ToString()); return true;
                    }
                case "monosyms":
                    {
                        var paths=System.Text.RegularExpressions.Regex.Matches(arg,@"(?:""(?<path>[^""]+)""|(?<path>\S+))");
                        if(paths.Count!=2)throw new Exception("monosyms <주소파일> <출력파일> (공백 경로는 큰따옴표)");
                        var ap=new[]{paths[0].Groups["path"].Value,paths[1].Groups["path"].Value};
                        if (gamePhase == 0) { gamePhase = 1; openStartedAt = now; }
                        string msg;
                        if (!MonoDump.Syms(ap[0], ap[1], now - openStartedAt, out msg)) return false;
                        gamePhase = 0; Log("[monosyms] " + msg); return true;
                    }
                case "monodump": Log("[monodump] " + MonoDump.Run(arg.Length > 0 ? arg : Path.Combine(modDir, "monodump.txt"))); return true;
                case "modwatch":
                    if (arg == "on") { ModWatch.Install(); modWatchFrame = Time.frameCount; Log("[modwatch] on"); }
                    else { Log("[modwatch] " + ModWatch.Snapshot(Time.frameCount - modWatchFrame)); modWatchFrame = Time.frameCount; }
                    return true;
                case "fuse": Log("[합치기 연구] " + (arg == "begin" ? FuseProbe.Begin() : FuseProbe.End())); return true;
                case "decobatch": Log("[장식 일괄] " + (arg.StartsWith("shadow") ? DecoBatchShadow.Command(arg) : DecoAnim.BatchCommand(arg))); return true;
                case "phasereport": PhaseWatch.ReportSong(); return true;
#if AUTOTEST
                case "slowfx": Log(EffectScan.SlowFxCommand(arg)); return true;
                case "cameraframeprobe": Log(ManagedCameraTrace.Command(arg)); return true;
#endif
#if AUTOTEST
                case "phasewindow": Log(PhaseWatch.ResearchWindow(arg)); return true;
                case "decosamp": if (arg == "begin") { DecoAnim.SampReset(); DecoAnim.SampOn = true; Log("[decosamp] 시작"); } else { DecoAnim.SampOn = false; Log("[decosamp] " + DecoAnim.SampReport()); } return true;
                case "profsamp": Log("[profsamp] " + (arg.StartsWith("begin") ? ProfSamp.Begin(arg.Substring(5)) : ProfSamp.End(arg.Contains("frames") ? modDir : null))); return true;
                case "floatsem": Log("[floatsem] " + FloatSem.Run()); return true;
                case "hitsound": Log("[히트음 예약 묶기] " + HitSoundBatch.Command(arg)); return true;
                case "audioplaying":
                    {
                        // 재생 중인 AudioSource 목록(곡 제외) - 죽은 뒤에도 히트음이 남는지 보는 진단 (AUTOTEST 전용)
                        var sb = new System.Text.StringBuilder(); int n = 0, total = 0;
                        var song = scrConductor.instance != null ? scrConductor.instance.song : null;
                        foreach (var a in UnityEngine.Object.FindObjectsOfType<AudioSource>())
                        {
                            if (a == null || !a.isPlaying) continue; total++;
                            if (a == song) continue;
                            n++; if (n > 30) continue;
                            string path = a.gameObject.name; var t = a.transform.parent; int d = 0; while (t != null && d++ < 3) { path = t.name + "/" + path; t = t.parent; }
                            sb.Append((char)10).Append("   ").Append(path).Append(" clip=").Append(a.clip != null ? a.clip.name : "null").Append(" t=").Append(a.time.ToString("F2")).Append("/").Append(a.clip != null ? a.clip.length.ToString("F2") : "?")
                              .Append(" vol=").Append(a.volume.ToString("F2")).Append(" mixer=").Append(a.outputAudioMixerGroup != null ? a.outputAudioMixerGroup.name : "none");
                        }
                        Log("[audioplaying] 재생 중 " + total + "개(곡 제외 " + n + "개) state=" + (ADOBase.controller != null ? ADOBase.controller.state.ToString() : "?") + " songpos=" + (scrConductor.instance != null ? scrConductor.instance.songposition_minusi.ToString("F2") : "?") + sb);
                        return true;
                    }
                case "decoablate":
                    { int m = 0; foreach (var k in arg.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)) m |= 1 << int.Parse(k); DecoAnim.AblateMask = m; Log("[ablate] 건너뛸 키 비트마스크 " + m + " (" + arg + ") — 측정 전용, 화면이 달라진다"); }
                    return true;
#endif
                case "cpubreak": Log("[CPU 분해] " + (arg == "on" ? CpuBreak.On() : arg == "begin" ? CpuBreak.Begin() : CpuBreak.End())); return true;
                case "rtsize": { var q = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries); if (q.Length >= 2) { LowEnd.DiagRtW = int.Parse(q[0]); LowEnd.DiagRtH = int.Parse(q[1]); } else { LowEnd.DiagRtW = LowEnd.DiagRtH = 0; } Log("[rtsize] " + LowEnd.DiagRtW + "x" + LowEnd.DiagRtH + " (창 " + Screen.width + "x" + Screen.height + ")"); return true; }
                case "scriptcensus": Log("[scriptcensus] " + ScriptCensus.Command(arg)); return true;
                case "updatecost": Log("[updatecost] " + UpdateCostProbe.Command(arg)); return true;
                case "decojob": Log("[decojob] " + DecoJobShadow.Command(arg)); return true;
                case "tightlate": Log("[tightlate] " + TightLate.Command(arg)); return true;
                case "decoengine": Log("[decoengine] " + DecoEngine.Command(arg)); return true;
                case "posfast": Log("[posfast] " + PosFast.Command(arg)); return true;
                case "colorfast": Log("[colorfast] " + ColorFast.Command(arg)); return true;
                case "groupstep": Log("[groupstep] " + DecoAnim.GroupCommand(arg)); return true;
                case "fastlane": Log("[fastlane] " + DecoAnim.FastCommand(arg)); return true;
                case "decocensus": Log("[decocensus] " + (arg.StartsWith("begin") ? DecoCensus.Begin(arg.Substring(5)) : arg.StartsWith("props") ? DecoCensus.ShaderProps() : arg.StartsWith("hide") ? DecoCensus.Hide(arg.Substring(4)) : DecoCensus.End())); return true;
                case "offscreenstat": Log("[offscreenstat] " + (arg == "begin" ? OffscreenProbe.Begin() : arg == "end" ? OffscreenProbe.End() : "offscreenstat begin|end")); return true;
                case "objcensus":
                    {
                        // 누적 추적: 유니티 객체 종류별 개수·힙·프로세스 메모리 (플레이를 반복할 때 무엇이 쌓이는지)
                        var sb = new System.Text.StringBuilder("[census]");
                        void C<T>(string n) where T : UnityEngine.Object { try { sb.Append(' ').Append(n).Append('=').Append(Resources.FindObjectsOfTypeAll<T>().Length); } catch { } }
                        C<GameObject>("GameObject"); C<Transform>("Transform"); C<scrDecoration>("deco"); C<scrFloor>("floor"); C<SpriteRenderer>("SpriteRenderer");
                        C<Renderer>("Renderer"); C<Material>("Material"); C<Texture2D>("Texture2D"); C<RenderTexture>("RT"); C<Sprite>("Sprite"); C<Mesh>("Mesh");
                        C<MonoBehaviour>("MonoBehaviour"); C<Component>("Component");
                        sb.Append(" heapMB=").Append((GC.GetTotalMemory(false) >> 20)).Append(" workingMB=").Append((System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 >> 20));
                        sb.Append(" | ").Append(TightLate.State()); Log(sb.ToString()); return true;
                    }
                case "decostruct": Log("[합치기 연구] " + FuseProbe.Struct()); return true;
                case "decogpu": Log(DecoGpu.Command(arg)); return true;
                case "decorender": return DecoRender.Step(arg, modDir);
                case "sortprobe": return SortProbe.Step(arg, modDir);
                case "animstat": Log("[장식 애니메이션 상태] "+DecoAnim.Stat()+" | "+Dormancy.Stat()+" | 위치 X·Y 합침 "+DecoAnim.PairSteps+" | 회전·크기 LateUpdate 에 맡김 "+MoveApply.RotScaleDeferred+" 위치 맡김 "+MoveApply.LateSkips+" 대조 "+MoveApply.LateChecked+" 다름 "+MoveApply.LateMismatch+" 목록에 없음 "+MoveApply.LateNotInList); return true;
                case "deferstate": Log("[색 바꾸기 미루기] "+RecolorDefer.State+" | "+LazySprite.State+" | 화면 밖 타일: "+TileCull.Counters+" | 타일 이동 효과 시작 합계 "+global::StutterFix.FloorAnim.StartMs.ToString("F1")+"ms"); return true;
                case "moduleidentity":Log("[실행 모듈 식별] mvid="+typeof(Main).Module.ModuleVersionId);return true;
                case "arenastate":Log("[계산 영역 시험] requested="+NativeMotion.ArenaRequested+" active="+NativeMotion.ArenaActive+" native_allocator="+NativeMotion.NativeArenaActive+" column_alignment_or="+NativeMotion.ArenaAlignment);return true;
                case "pinarena":Log("[페이지 고정 시험] requested="+NativeMotion.PinRequested+" pinned="+NativeMotion.ArenaPinned+" working_set="+PinnedArenaOwner.WorkingSetSnapshot()+" original="+PinnedArenaOwner.OriginalSnapshot+" status="+PinnedArenaOwner.Status);return true;
                case "timercapability":
                    Log("[타이머 구성 감사] "+TimerResolutionInfo.Read());return true;
                case "audiocapability":
                {
                    int samples,buffers;AudioSettings.GetDSPBufferSize(out samples,out buffers);
                    var config=AudioSettings.GetConfiguration();
                    Log("[오디오 구성 감사] dsp_samples="+samples+" buffers="+buffers+" sample_rate="+config.sampleRate+" real_voices="+config.numRealVoices+" virtual_voices="+config.numVirtualVoices+" loaded_clips="+Resources.FindObjectsOfTypeAll<AudioClip>().Length+" audio_reset=0 phase_changed=0");return true;
                }
                case "physicsaudit":
                {
                    string Read(string name,string property)
                    {try{var type=AccessTools.TypeByName(name);return Convert.ToString(type?.GetProperty(property,System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.Public)?.GetValue(null))??"unknown";}catch{return "unknown";}}
                    int Count(string name)
                    {
                        var type=AccessTools.TypeByName(name);if(type==null)return -1;int n=0;
                        foreach(var obj in Resources.FindObjectsOfTypeAll(type))if(obj is Component component&&component.gameObject.activeInHierarchy)n++;
                        return n;
                    }
                    int callbacks=0;var types=new SortedDictionary<string,int>();
                    foreach(var behaviour in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
                    {
                        if(behaviour==null||!behaviour.isActiveAndEnabled)continue;
                        var type=behaviour.GetType();
                        var method=type.GetMethod("FixedUpdate",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.Public|System.Reflection.BindingFlags.NonPublic,null,Type.EmptyTypes,null);
                        if(method==null)continue;callbacks++;int n;types.TryGetValue(type.FullName,out n);types[type.FullName]=n+1;
                    }
                    Log("[물리 사용 감사] rigidbody2d="+Count("UnityEngine.Rigidbody2D")+" rigidbody3d="+Count("UnityEngine.Rigidbody")+" colliders2d="+Count("UnityEngine.Collider2D")+" fixed_update_behaviours="+callbacks+" fixed_delta="+Time.fixedDeltaTime.ToString("R")+" simulation2d="+Read("UnityEngine.Physics2D","simulationMode")+" simulation3d="+Read("UnityEngine.Physics","simulationMode")+" sync2d="+Read("UnityEngine.Physics2D","autoSyncTransforms")+" values_changed=0");
                    foreach(var row in types)Log("[FixedUpdate 사용] "+row.Key+"="+row.Value);return true;
                }
                case "indirectaudit":
                {
                    int decorations=0,renderers=0,masked=0,missing=0;
                    var materials=new HashSet<int>();var textures=new HashSet<int>();var orders=new HashSet<string>();
                    var scene=UnityEngine.SceneManagement.SceneManager.GetActiveScene();
                    foreach(var deco in Resources.FindObjectsOfTypeAll<scrDecoration>())
                    {
                        if(deco==null||deco.gameObject.scene!=scene)continue;decorations++;
                        foreach(var renderer in deco.GetComponentsInChildren<SpriteRenderer>(true))
                        {
                            renderers++;var material=renderer.sharedMaterial;
                            if(material==null){missing++;continue;}materials.Add(material.GetInstanceID());
                            var texture=renderer.sprite!=null?renderer.sprite.texture:null;if(texture!=null)textures.Add(texture.GetInstanceID());
                            orders.Add(renderer.sortingLayerID+"/"+renderer.sortingOrder);
                            if(renderer.maskInteraction!=SpriteMaskInteraction.None)masked++;
                        }
                    }
                    Log("[간접 그리기 감사] decorations="+decorations+" sprite_renderers="+renderers+" shared_materials="+materials.Count+" textures="+textures.Count+" sorting_keys="+orders.Count+" masked="+masked+" missing_material="+missing+" instancing="+SystemInfo.supportsInstancing+" compute="+SystemInfo.supportsComputeShaders+" renderer_changed=0");return true;
                }
                case "decostructure":DecorationAudit.Run();return true;
                case "inputclockaudit":DecorationAudit.Input();return true;
                case "framewindow":DecorationAudit.Window();return true;
                case "rawclockstate":Log("[프레임 입력 대기] enabled="+FrameQueueDelay.Enabled+" status="+FrameQueueDelay.Status+" corrected="+FrameQueueDelay.Corrected+" skipped="+FrameQueueDelay.Skipped+" already_async="+FrameQueueDelay.AlreadyAsync+" last_delay_ms="+FrameQueueDelay.LastDelayMs);return true;
                case "rawanglefixture":Log("[입력 각도 대조] "+FrameQueueDelay.AngleFixture());return true;
                case "profilesave":
                    Main.Config.Save(Main.Entry);return true;
                case "optimizationstate":
                    Log("[최적화 후보] memory_enabled="+MemoryBudget.Enabled+" memory_limit="+
                        MemoryBudget.Limit(GcControl.HardLimitMB,UnityEngine.SystemInfo.systemMemorySize,GC.GetTotalMemory(false)>>20)+
                        " memory_active="+MemoryBudget.Active+" pool="+FastJsonParser.PoolStrings+
                        " cache="+(AssetDiskCache.Store!=null)+" dxt_hits="+System.Threading.Interlocked.Read(ref AssetDiskCache.DxtHits)+
                        " dxt_writes="+System.Threading.Interlocked.Read(ref AssetDiskCache.DxtWrites)+
                        " json_hits="+System.Threading.Interlocked.Read(ref AssetDiskCache.JsonHits)+
                        " json_writes="+System.Threading.Interlocked.Read(ref AssetDiskCache.JsonWrites)+
                        " upload_time="+UnityEngine.QualitySettings.asyncUploadTimeSlice+
                        " upload_buffer="+UnityEngine.QualitySettings.asyncUploadBufferSize+
                        " epsilon_skips="+MoveApply.EpsilonSkips+" native_batches="+NativeMotion.Batches+
                        " native_values="+NativeMotion.Values+" native_checks="+NativeMotion.Checks+
                        " native_mismatch="+NativeMotion.Mismatch+" native_failed="+NativeMotion.Failed+" native_rounding="+NativeMotion.Rounding+
                        " tag_hits="+TagIndex.Hits+" tag_compiled="+TagIndex.Compiled+" tag_invalidations="+TagIndex.Invalidations);
                    return true;
                case "uploadbudgettest":
                    UploadBudget.Begin();
                    Log("[업로드 엔진 시험] time="+UnityEngine.QualitySettings.asyncUploadTimeSlice+" buffer="+UnityEngine.QualitySettings.asyncUploadBufferSize);
                    UploadBudget.Restore();return true;
                case "particlesnapshot":
                    Log("[입자 컬링 상태] "+ParticleFix.CullSnapshot());return true;
                case "autoresstate":
                    Log("[자동 해상도 상태] "+LowEnd.AutoGpuState);return true;
                case "updatefixture":
                    Log("[업데이트 복구 시험] "+UpdateFixture.Run(arg));return true;
                case "modulefixture":
                    Log("[모듈 수명 시험] "+Main.LifecycleFixture());return true;
                case "propertyidfixture":
                {
                    int checkedIds=0,badIds=0;
                    foreach(var f in typeof(MaterialPropertyIds).GetFields(System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic))
                    {
                        if(f.FieldType!=typeof(int)||!f.Name.StartsWith("P_",StringComparison.Ordinal))continue;
                        checkedIds++;if((int)f.GetValue(null)!=UnityEngine.Shader.PropertyToID(f.Name.Substring(1)))badIds++;
                    }
                    Log("[재질 ID 시험] checks="+checkedIds+" mismatch="+badIds);return true;
                }
                case "motionfixture":
                {
                    if(!NativeMotion.Enabled)throw new InvalidOperationException("Native motion candidate must be enabled");
                    if(!NativeMotion.EnsureCompatible())throw new InvalidOperationException("Runtime rounding mismatch; original calculation retained");
                    int checkedValues=0,badValues=0,doubleBad=0,strictBad=0,printed=0;
                    var scratch=new float[2];
                    for(int pass=0;pass<16;pass++)
                    {
                        if(pass==8&&NativeMotion.ArenaActive)NativeMotion.ResetArena();
                        for(int i=0;i<NativeMotion.Capacity;i++)
                        {
                            NativeMotion.Start[i]=(i%997-500)*.37f;NativeMotion.Change[i]=(i%499-250)*1.11f;
                            NativeMotion.Duration[i]=(i%199+1)*.019f;NativeMotion.Position[i]=NativeMotion.Duration[i]*((i+pass)%101)/100f;
                        }
                        if(!NativeMotion.Run(NativeMotion.Capacity))throw new InvalidOperationException("Native batch unavailable; managed fallback retained");
                        for(int i=0;i<NativeMotion.Capacity;i++)
                        {
                            float e=ZeroTween.Eval(DG.Tweening.Ease.Linear,NativeMotion.Position[i],NativeMotion.Duration[i],0,0);
                            float value=NativeMotion.Start[i]+NativeMotion.Change[i]*e;
                            float dv=(float)((double)NativeMotion.Start[i]+(double)NativeMotion.Change[i]*(double)e);
                            scratch[0]=e;scratch[1]=NativeMotion.Change[i]*scratch[0];scratch[0]=NativeMotion.Start[i]+scratch[1];
                            int actual=InstantMove.Bits(NativeMotion.Output[i]);
                            checkedValues++;if(InstantMove.Bits(value)!=actual)
                            {
                                badValues++;
                                if(printed++<3)Log("[계산 반올림 표본] s="+NativeMotion.Start[i].ToString("R")+" c="+NativeMotion.Change[i].ToString("R")+" e="+e.ToString("R")+" mono="+value.ToString("R")+" r64="+dv.ToString("R")+" r32="+scratch[0].ToString("R")+" native="+NativeMotion.Output[i].ToString("R"));
                            }
                            if(InstantMove.Bits(dv)!=actual)doubleBad++;
                            if(InstantMove.Bits(scratch[0])!=actual)strictBad++;
                        }
                    }
                    if(badValues>0)NativeMotion.Failed=true;
                    Log("[네이티브 계산 시험] checks="+checkedValues+" mismatch="+badValues+" native_vs_r64="+doubleBad+" native_vs_forced_r32="+strictBad);
                    return true;
                }
                case "fpsmark":
                {
                    // fpsmark start | fpsmark end <이름>: 두 호출 사이의 실제 시간 FPS(프레임 수 / Stopwatch). 일시정지한 같은 장면에서 ABAB 로 쓴다.
                    if(arg=="start"){fpsMarkFrames=Time.frameCount;fpsMarkTicks=System.Diagnostics.Stopwatch.GetTimestamp();PerfOverlay.FpsWinStart();return true;}
                    double sec=(System.Diagnostics.Stopwatch.GetTimestamp()-fpsMarkTicks)/(double)System.Diagnostics.Stopwatch.Frequency;int fr=Time.frameCount-fpsMarkFrames;
                    Log("[FPS 구간] "+arg.Substring(4).Trim()+" : "+(fr/Math.Max(sec,1e-6)).ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+" FPS ("+fr+"프레임 "+sec.ToString("F2",System.Globalization.CultureInfo.InvariantCulture)+"초) | "+PerfOverlay.FpsWinEnd());
                    return true;
                }
                case "fpscap":
                {
                    // fpscap <n | 0>: 진짜 FPS 를 n 으로 묶는다(수직동기 끔). 0 이면 풀기. 게임은 Application.targetFrameRate 만 쓴다.
                    int n = int.Parse(arg);
                    QualitySettings.vSyncCount = 0;
                    Application.targetFrameRate = n > 0 ? n : 10000;
                    Log("[FPS 제한] 목표 FPS " + Application.targetFrameRate + ", vSync " + QualitySettings.vSyncCount + " frame " + Time.frameCount);
                    return true;
                }
                case "planetinfo":
                {
                    // planetinfo: 행성 위치·크기와 렌더러별 경계 (프레임 생성이 공을 따로 옮길 때 쓸 반지름 보정용)
                    var ctl = scrController.instance;
                    var cam = scrCamera.instance != null ? scrCamera.instance.GetComponent<Camera>() : Camera.main;
                    if (ctl == null) { Log("[행성 정보] 컨트롤러 없음"); return true; }
                    Log(FormattableString.Invariant($"[행성 정보] 카메라 size={(cam != null ? cam.orthographicSize : -1):R} aspect={(cam != null ? cam.aspect : -1):R} screen={Screen.width}x{Screen.height} pos={(cam != null ? cam.transform.position.x : 0):R},{(cam != null ? cam.transform.position.y : 0):R} rotz={(cam != null ? cam.transform.eulerAngles.z : 0):R}"));
                    foreach (var p in new[] { ctl.planetRed, ctl.planetBlue })
                    {
                        if (p == null) continue;
                        Log(FormattableString.Invariant($"[행성 정보] {p.name} pos={p.transform.position.x:R},{p.transform.position.y:R} lossy={p.transform.lossyScale.x:R} angle={p.angle:R}"));
                        foreach (var r in p.GetComponentsInChildren<Renderer>(true))
                        {
                            var b = r.bounds;
                            Log(FormattableString.Invariant($"[행성 정보]   {r.GetType().Name} '{r.name}' on={r.enabled && r.gameObject.activeInHierarchy} center={b.center.x:R},{b.center.y:R} extents={b.extents.x:R},{b.extents.y:R}"));
                        }
                    }
                    return true;
                }
                case "hideplanets":
                {
                    // hideplanets: 두 행성(꼬리 포함)의 렌더러를 끈다 (움직임 진단용: 공 말고 무엇이 진짜 프레임에서만 움직이는지)
                    var ctl = scrController.instance;
                    int n = 0;
                    if (ctl != null)
                        foreach (var p in new[] { ctl.planetRed, ctl.planetBlue })
                            if (p != null) foreach (var r in p.GetComponentsInChildren<Renderer>(true)) { r.enabled = false; n++; }
                    Log("[행성 숨김] 렌더러 " + n + "개 끔");
                    return true;
                }
                case "spawn":
                {
                    // spawn <exe> <인자...>: 바깥 도구를 기다리지 않고 켠다 (프레임 생성 연구의 화면 기록기용). 저장소 tools\framegenlab\out 안의 exe 만.
                    int sp = arg.IndexOf(' ');
                    string exe = sp > 0 ? arg.Substring(0, sp) : arg, rest = sp > 0 ? arg.Substring(sp + 1) : "";
                    string full = Path.GetFullPath(exe);
                    if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || full.IndexOf(@"\tools\framegenlab\out\", StringComparison.OrdinalIgnoreCase) < 0 || !File.Exists(full))
                        throw new Exception("spawn 은 tools\\framegenlab\\out 안의 exe 만: " + full);
                    var psi = new System.Diagnostics.ProcessStartInfo(full, rest) { UseShellExecute = false, CreateNoWindow = true };
                    System.Diagnostics.Process.Start(psi);
                    Log("[spawn] " + Path.GetFileName(full) + " " + rest + " 곡 " + (scrConductor.instance != null ? scrConductor.instance.songposition_minusi.ToString("F2") : "-") + "초");
                    return true;
                }
                case "pm":
                {
                    // pm <이름> <초>: 관리자 에이전트(StutterFixTrace\agent.ps1)에 PresentMon 기록을 시킨다. 바로 돌아온다(기록은 따로 돈다).
                    var q = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (q.Length < 2) throw new Exception("pm <이름> <초>");
                    string st = Path.Combine(@"C:\Users\Public\StutterFixTrace", "agent-status.txt");
                    if (!File.Exists(st) || File.ReadAllText(st).Contains("ended") || File.ReadAllText(st).Contains("quit")) throw new Exception("관리자 에이전트가 꺼져 있음 (agent.ps1)");
                    File.WriteAllText(Path.Combine(@"C:\Users\Public\StutterFixTrace", "cmd.txt"), "pm " + q[0] + " " + q[1]);
                    Log("[PresentMon] 기록 요청 " + q[0] + " " + q[1] + "초, 곡 " + (scrConductor.instance != null ? scrConductor.instance.songposition_minusi.ToString("F2") : "-") + "초, 시각 " + DateTime.Now.ToString("HH:mm:ss.fff"));
                    return true;
                }
#endif
                case "ui":
                    // ui <페이지 0~9 | gpudeco | GPU 장식 | GPU decorations | dock | close>: 설정 창 열기 (모양 확인용)
                    // ui 1.2 = 플레이 페이지의 셋째 갈래, ui gpudeco.0 = GPU 장식
                    {
                        var pp = arg.Split('.');
                        string name = pp[0].Trim().ToLowerInvariant();
                        int p = name == "close" ? -2 : name == "dock" ? -1
                            : name == "gpudeco" || name == "gpu 장식" || name == "gpu decorations" ? 9 : int.Parse(pp[0]);
                        SettingsWindow.ShowForTest(p, pp.Length > 1 ? int.Parse(pp[1]) : 0);
                    }
                    Log("설정 창: " + arg);
                    return true;
                case "uiscroll":
                    SettingsWindow.ScrollForTest(float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture));
                    return true;
                case "bootnotice":
                    Log(SettingsWindow.BootNoticeCommand(arg));
                    return true;
                case "shot":
                {
                    // shot <이름>: 화면 캡처를 모드 폴더 shots/<이름>.png 로 (그 프레임 끝에 저장된다)
                    string dir = Path.Combine(modDir, "shots");
                    Directory.CreateDirectory(dir);
                    string f = Path.Combine(dir, (arg.Length > 0 ? arg : "shot") + ".png");
                    ScreenCapture.CaptureScreenshot(f);
                    double shotSong=scrConductor.instance!=null?scrConductor.instance.songposition_minusi:double.NaN;
                    Log("화면 캡처: " + f + " | 요청 곡 " + shotSong.ToString("F3",System.Globalization.CultureInfo.InvariantCulture)
                        + "초, 배율 " + Main.Config.FrameGenOutside + ", frame " + Time.frameCount);
                    return true;
                }
                case "decowatch":
                {
                    // decowatch <초>: 곡 중 0.5초마다, 그리기가 꺼져 있는데(forceRenderingOff) 실제 색은 보이는(알파 > 0) 이미지 장식을 찾는다
                    float dur = arg.Length > 0 ? float.Parse(arg, System.Globalization.CultureInfo.InvariantCulture) : 60f;
                    if (watchStart < 0) { watchStart = now; watchNext = now; watchBad = 0; watchChecks = 0; watchSeen.Clear(); }
                    if (now >= watchNext)
                    {
                        watchNext = now + 0.5f; watchChecks++;
                        var mgr = scrDecorationManager.instance;
                        if (mgr != null)
                            foreach (var d in mgr.allDecorations)
                            {
                                var v = d as scrVisualDecoration;
                                if (v == null || v.spriteRenderer == null) continue;
                                var r = v.spriteRenderer;
                                if (!r.forceRenderingOff || r.color.a <= 0f || !r.enabled || !v.gameObject.activeInHierarchy) continue;
                                watchBad++;
                                int id = v.GetInstanceID();
                                if (watchSeen.Add(id) && watchSeen.Count <= 15)
                                    Log(string.Format("decowatch: 곡 {0:F1}초 그리기 꺼짐인데 보여야 함: '{1}' 알파 {2:F3} (태그 {3})", (scrConductor.instance != null ? scrConductor.instance.songposition_minusi : -1), v.sourceLevelEvent != null ? v.sourceLevelEvent["decorationImage"] : "?", r.color.a, v.sourceLevelEvent != null ? v.sourceLevelEvent["tag"] : ""));
                            }
                    }
                    if (now - watchStart < dur && Hitch.Playing) return false;
                    Log(string.Format("decowatch 끝: 검사 {0}번, 보여야 하는데 꺼진 경우 {1}번 (장식 {2}개)", watchChecks, watchBad, watchSeen.Count));
                    watchStart = -1;
                    return true;
                }
                case "gamebtn":
                {
                    // gamebtn: 에디터의 "게임 화면으로" 단추와 같은 동작 (저장 확인 없이). 열린 뒤는 game 과 같이 기다린다
                    if (gamePhase == 0)
                    {
                        if (scnEditor.instance == null) throw new Exception("에디터가 아님");
                        gameOld = ADOBase.customLevel;
                        GameScreenButton.Go();
                        gamePhase = 1; openStartedAt = now; Log("에디터에서 게임 화면으로 열기");
                        return false;
                    }
                    if (gamePhase == 1)
                    {
                        if (UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "scnGame" || scnEditor.instance != null || ADOBase.customLevel == null || ReferenceEquals(ADOBase.customLevel, gameOld) || ADOBase.customLevel.isLoading) return false;
                        gamePhase = 2; waitSec = now;
                        return false;
                    }
                    if (now - waitSec < 2f) return false;
                    gamePhase = 0; Log(string.Format("게임 화면 맵 열림 ({0:F1}초, 2초 기다림 포함)", now - openStartedAt));
                    return true;
                }
                case "trace":
                {
                    // trace <이름> <초>: 관리자 에이전트(C:\Users\Public\StutterFixTrace\agent.ps1)에 PerfView ThreadTime 기록 시작을 알린다.
                    // 에이전트가 받았다고 적을 때까지(최대 15초) 기다린다. 끊김 줄의 "시각" 으로 기록 안의 위치를 맞춘다.
                    const string dir = @"C:\Users\Public\StutterFixTrace";
                    string st = Path.Combine(dir, "agent-status.txt");
                    var pa = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (pa.Length < 2) throw new Exception("trace <이름> <초>");
                    if (gamePhase == 0)
                    {
                        if (!Directory.Exists(dir)) throw new Exception("에이전트 폴더 없음");
                        File.WriteAllText(Path.Combine(dir, "cmd.txt"), "start " + pa[0] + " " + pa[1]);
                        gamePhase = 1; openStartedAt = now;
                        Log("기록 시작 요청: " + pa[0] + " " + pa[1] + "초, 시각 " + DateTime.Now.ToString("HH:mm:ss.fff"));
                        return false;
                    }
                    string s = "";
                    try { s = File.ReadAllText(st); } catch { }
                    if (s.Contains("started " + pa[0] + " "))
                    {
                        if (now - openStartedAt < 4f) return false;   // PerfView 가 커널 세션을 여는 시간
                        gamePhase = 0; Log("기록 시작됨 (" + s.Trim() + "), 시각 " + DateTime.Now.ToString("HH:mm:ss.fff"));
                        return true;
                    }
                    if (now - openStartedAt > 15f) { gamePhase = 0; throw new Exception("에이전트 응답 없음 (관리자 agent.ps1 이 꺼져 있음?)"); }
                    return false;
                }
                case "toeditor":
                {
                    // toeditor: 게임 화면 일시정지 메뉴의 "에디터에서 열기" 와 같게 (같은 scnGame 위에 에디터를 더한다)
                    if (gamePhase == 0)
                    {
                        if (scnEditor.instance != null) throw new Exception("이미 에디터");
                        UnityEngine.SceneManagement.SceneManager.LoadScene("scnEditor", UnityEngine.SceneManagement.LoadSceneMode.Additive);
                        gamePhase = 1; openStartedAt = now; Log("게임 화면에서 에디터 열기");
                        return false;
                    }
                    if (gamePhase == 1)
                    {
                        if (scnEditor.instance == null || scnEditor.instance.playMode || now - openStartedAt < 2f) return false;
                        gamePhase = 2; waitSec = now;
                        return false;
                    }
                    if (now - waitSec < 2f) return false;
                    gamePhase = 0; Log(string.Format("에디터 열림 ({0:F1}초)", now - openStartedAt));
                    return true;
                }
                case "bigrend":
                {
                    // bigrend: 켜져 있는 렌더러 중 큰 것(경계 상자 긴 변 > 15) - 화면을 덮는 물체 찾기
                    int n = 0;
                    var cnt = new Dictionary<string, int>();
                    foreach (var r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                    {
                        if (!r.enabled || !r.gameObject.activeInHierarchy || r.forceRenderingOff) continue;
                        var sz = r.bounds.size;
                        if (Mathf.Max(sz.x, sz.y) < 15f) continue;
                        var t = r.transform; string path = t.name; if (t.parent != null) path = t.parent.name + "/" + path; if (t.parent != null && t.parent.parent != null) path = t.parent.parent.name + "/" + path;
                        string col = ""; var sr = r as SpriteRenderer; if (sr != null) col = " 색 " + sr.color;
                        string key = r.GetType().Name + " " + path + " 크기 " + sz.x.ToString("F0") + "x" + sz.y.ToString("F0") + col + " 층 " + r.sortingLayerName + "/" + r.sortingOrder + " 재질 " + (r.sharedMaterial != null ? r.sharedMaterial.name : "-");
                        cnt[key] = cnt.TryGetValue(key, out var c0) ? c0 + 1 : 1; n++;
                    }
                    Log("bigrend: 큰 렌더러 " + n + "개");
                    foreach (var kv in cnt.OrderByDescending(x => x.Value).Take(15)) Log("bigrend:  " + kv.Value + "개 " + kv.Key);
                    return true;
                }
                case "uidump":
                {
                    // uidump: 에디터 재생 단추와 그 부모의 구조 (자리·크기·컴포넌트)
                    var ued = scnEditor.instance;
                    if (ued == null || ued.playPause == null) { Log("uidump: 에디터 재생 단추 없음"); return true; }
                    Action<Transform, int> udump = null;
                    udump = (t, depth) =>
                    {
                        var rt = t as RectTransform;
                        var comps = new List<string>();
                        foreach (var c in t.GetComponents<Component>()) if (c != null && !(c is Transform)) comps.Add(c.GetType().Name);
                        Log(string.Format("uidump: {0}{1} [{2}] 활성 {3} {4}", new string(' ', depth * 2), t.name, string.Join(",", comps.ToArray()), t.gameObject.activeSelf,
                            (rt != null ? string.Format("anchor {0}-{1} pivot {2} pos {3} size {4}", rt.anchorMin, rt.anchorMax, rt.pivot, rt.anchoredPosition, rt.sizeDelta) : "")
                            + (t.GetComponent<UnityEngine.UI.Image>() is UnityEngine.UI.Image im ? string.Format(" 그림 {0} 색 {1} 재질 {2}", im.sprite != null ? im.sprite.name + "/" + im.sprite.texture.width : "-", im.color, im.material != null ? im.material.name : "-") : "")));
                        if (depth < 3) foreach (Transform ch in t) udump(ch, depth + 1);
                    };
                    var p = ued.playPause.transform.parent;
                    Log("uidump: 부모 " + (p != null ? p.name : "-") + ", 형제 " + (p != null ? p.childCount : 0) + "개");
                    if (p != null) foreach (Transform ch in p) udump(ch, 0);
                    return true;
                }
                case "decostate":
                {
                    // decostate: 장식 보이기 상태 세기 (꺼진 오브젝트 / 꺼진 렌더러 / 그리기 꺼짐 / 투명)
                    var mgr = scrDecorationManager.instance;
                    if (mgr == null) { Log("decostate: 장식 관리자 없음"); return true; }
                    int total = 0, inactive = 0, rOff = 0, force = 0, clear = 0, vis = 0;
                    foreach (var d in mgr.allDecorations)
                    {
                        if (d == null) continue;
                        total++;
                        if (!d.gameObject.activeInHierarchy) { inactive++; continue; }
                        var v = d as scrVisualDecoration;
                        if (v == null || v.spriteRenderer == null) continue;
                        var r = v.spriteRenderer;
                        if (!r.enabled) rOff++; else if (r.forceRenderingOff) force++; else if (r.color.a <= 0f) clear++; else vis++;
                    }
                    Log(string.Format("decostate: 장식 {0}개, 꺼진 오브젝트 {1}, 꺼진 렌더러 {2}, 그리기 꺼짐 {3}, 투명 {4}, 보임 {5}", total, inactive, rOff, force, clear, vis));
                    // 켜져 있는 장식 중 히트박스 장식 (노이펙에서 남는 것)
                    int hb = 0; var names = new List<string>();
                    foreach (var d in mgr.allDecorations)
                    {
                        if (d == null || !d.gameObject.activeInHierarchy || !d.useHitbox) continue;
                        hb++;
                        if (names.Count < 12 && d.sourceLevelEvent != null)
                        {
                            object img = null; try { img = d.sourceLevelEvent["decorationImage"]; } catch { }
                            object tag = null; try { tag = d.sourceLevelEvent["tag"]; } catch { }
                            names.Add(string.Format("{0}({1}, {2}, 태그 {3})", img, d.hitbox, d.hitboxTriggerType, tag));
                        }
                    }
                    Log("decostate: 켜진 히트박스 장식 " + hb + "개: " + string.Join(" | ", names.ToArray()));
                    return true;
                }
                case "decoaudit":
                {
                    // decoaudit: 이미지가 지정된 장식 중 그림(스프라이트)이 비어 있는 것을 센다 (파일 없음 / 깨진 파일 / 파일은 정상인데 빠짐)
                    var mgr = scrDecorationManager.instance;
                    if (mgr == null) { Log("decoaudit: 장식 관리자 없음"); return true; }
                    string dir = System.IO.Path.GetDirectoryName(ADOBase.levelPath ?? "") ?? "";
                    int total = 0, ok = 0, noFile = 0, broken = 0, suspect = 0, noTex = 0;
                    var names = new List<string>();
                    foreach (var d in mgr.allDecorations)
                    {
                        var v = d as scrVisualDecoration;
                        if (v == null || v.sourceLevelEvent == null) continue;
                        string img = v.sourceLevelEvent["decorationImage"] as string;
                        if (string.IsNullOrEmpty(img) || img.StartsWith("prefab:", StringComparison.OrdinalIgnoreCase)) continue;
                        total++;
                        var sp = v.spriteRenderer != null ? v.spriteRenderer.sprite : null;
                        if (sp != null && sp.texture != null) { ok++; continue; }
                        if (sp != null) { noTex++; if (names.Count < 12) names.Add("텍스처 없음:" + img); continue; }
                        string full = System.IO.Path.Combine(dir, img);
                        if (!System.IO.File.Exists(full)) { noFile++; continue; }
                        if (ImagePrefetch.IsBroken(full)) { broken++; continue; }
                        suspect++; if (names.Count < 12) names.Add(img);
                    }
                    Log(string.Format("decoaudit: 이미지 장식 {0}개 - 그림 있음 {1}, 파일 없음 {2}, 깨진 파일 {3}, 스프라이트는 있는데 텍스처 없음 {4}, 파일은 정상인데 빠짐 {5}{6}",
                        total, ok, noFile, broken, noTex, suspect, names.Count > 0 ? " | " + string.Join(", ", names.ToArray()) : ""));
                    return true;
                }
                case "hitspam":
                {
                    // hitspam <초> <개수>: 곡 중 판정 글자를 여러 종류로 띄운다(자동 플레이는 판정 글자를 띄우지 않는다). 개수가 정수면 프레임당, "20/s" 면 초당. 끝나면 최악 프레임을 적는다
                    var ps = arg.Split(' ');
                    float dur = ps.Length > 0 && ps[0].Length > 0 ? float.Parse(ps[0], System.Globalization.CultureInfo.InvariantCulture) : 20f;
                    string perS = ps.Length > 1 ? ps[1] : "3";
                    bool perSec = perS.EndsWith("/s");
                    float rate = float.Parse(perSec ? perS.Substring(0, perS.Length - 2) : perS, System.Globalization.CultureInfo.InvariantCulture);
                    if (spamMgr == null)
                    {
                        spamMgr = FindHitTextManager();
                        spamPlanet = UnityEngine.Object.FindObjectOfType<scrPlanet>();
                        spamStart = now; spamMax = 0; spamOver = 0; spamFrames = 0; spamLast = now; spamShown = 0; spamLastShow = now; spamAcc = 0;
                        if (spamMgr == null || spamPlanet == null) { Log("hitspam: 판정 글자 관리자/행성 없음"); spamMgr = null; return true; }
                        spamShow = AccessTools.Method(spamMgr.GetType(), "ShowHitText");
                    }
                    float dt = (now - spamLast) * 1000f; spamLast = now;
                    if (spamFrames > 0) { if (dt > spamMax) spamMax = dt; if (dt > 15f) spamOver++; }
                    spamFrames++;
                    var margins = new[] { HitMargin.TooEarly, HitMargin.VeryEarly, HitMargin.EarlyPerfect, (HitMargin)Enum.Parse(typeof(HitMargin), Enum.IsDefined(typeof(HitMargin), "Perfect") ? "Perfect" : "XPerfect"), HitMargin.LatePerfect, HitMargin.VeryLate, HitMargin.TooLate, HitMargin.Multipress, HitMargin.FailMiss, HitMargin.FailOverload, HitMargin.OverPress };
                    int per;
                    if (perSec) { spamAcc += rate * (now - spamLastShow); per = (int)spamAcc; spamAcc -= per; } else per = (int)rate;
                    spamLastShow = now;
                    for (int k = 0; k < per; k++)
                    {
                        var hm = margins[spamRng.Next(margins.Length)];
                        long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        spamShow.Invoke(spamMgr, new object[] { hm, spamPlanet, (float)(spamRng.NextDouble() * 2 - 1) }); spamShown++;
                        double cms = (System.Diagnostics.Stopwatch.GetTimestamp() - c0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                        spamCallMs += cms; if (cms > spamCallMax) { spamCallMax = cms; spamCallMaxWhat = hm + " #" + spamShown; }
                        if (cms > 5) spamSlowCalls++;
                    }
                    if (now - spamStart < dur) return false;
                    Log(string.Format("hitspam {0:F0}초: 판정 글자 {1}개, 프레임 {2}개, 최악 {3:F1}ms, 15ms 넘은 프레임 {4}개 | 띄우기 호출 합계 {5:F0}ms, 가장 오래 {6:F1}ms ({7}), 5ms 넘은 호출 {8}번", dur, spamShown, spamFrames, spamMax, spamOver, spamCallMs, spamCallMax, spamCallMaxWhat, spamSlowCalls));
                    spamCallMs = 0; spamCallMax = 0; spamSlowCalls = 0;
                    spamMgr = null;
                    return true;
                }
                case "glassdump":
                    // glassdump: 유리 배경 복사본(창 자리)을 shots/glass-panel.png 로 (위아래 방향 확인용)
                    Log("유리 배경 저장: " + SettingsWindow.DumpGlass(Path.Combine(modDir, "shots")));
                    return true;
                case "glassflip":
                    SettingsWindow.GlassFlip = arg == "1";
                    Log("유리 뒤집기: " + SettingsWindow.GlassFlip);
                    return true;
                case "keep":
                    keep = true;
                    Log("묶음이 끝나도 게임을 끄지 않음");
                    return true;
                case "set":
                {
                    var sp = arg.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                    if (sp.Length < 2) throw new Exception("set <설정> <값>");
                    var fi = typeof(Settings).GetField(sp[0], System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                    if (fi == null) throw new Exception("설정 이름 없음: " + sp[0]);
                    object v = ParseValue(fi.FieldType, sp[1].Trim());
                    if (!setOrig.ContainsKey(fi)) setOrig[fi] = fi.GetValue(Main.Config);
                    fi.SetValue(Main.Config, v);
                    Main.ApplyConfig();
                    Log("설정 " + fi.Name + " = " + v + " (묶음 끝에 " + setOrig[fi] + " 로 되돌림)");
                    return true;
                }
                case "reload":
                    if (DllHash() == loadedHash) { Log("DLL 이 그대로라 다시 불러오지 않음"); return true; }
                    reloadPending = true;
                    EndBatch("다시 불러오기");
                    return false;   // 묶음이 이미 끝났다 (idx 를 건드리지 않음)
#if AUTOTEST
                case "tilecullreloadcheck":
                    Log(TileCull.ReloadPatchAudit());
                    return true;
#endif
                case "reloadactivefixture":
                    if(setOrig.Count!=0||!Hitch.Playing||!RDC.auto||!RenderThreadScheduling.FixtureState.Contains("owned=True pending=0"))throw new InvalidOperationException("Active reload fixture requires an owned render lease, autoplay and no temporary settings");
                    if(DllHash()==loadedHash)throw new InvalidOperationException("Active reload fixture requires a new DLL");
                    Log("[활성 내리기 시험] "+RenderThreadScheduling.FixtureState);
                    // This explicit research command keeps playback alive across
                    // unload. Its run-owned process is normally quit afterwards.
                    // Ordinary reload still restores temporary autoplay normally.
                    autoChanged=false;reloadPending=true;EndBatch("활성 다시 불러오기");return false;
                default:
                    throw new Exception("모르는 명령: " + cmd);
            }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        private static void AltTab()
        {
            const byte VK_MENU = 0x12, VK_TAB = 0x09; const uint KEYUP = 2;
            keybd_event(VK_MENU, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(30);
            keybd_event(VK_TAB, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(30);
            keybd_event(VK_TAB, 0, KEYUP, UIntPtr.Zero); System.Threading.Thread.Sleep(30);
            keybd_event(VK_MENU, 0, KEYUP, UIntPtr.Zero);
        }

        private static string Diff(string a, string b)
        {
            var x = a.Split(';'); var y = b.Split(';'); var d = new List<string>();
            for (int i = 0; i < Math.Min(x.Length, y.Length); i++) if (x[i] != y[i]) d.Add(x[i] + " -> " + y[i]);
            return string.Join(", ", d.ToArray());
        }

        private static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); } catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        }

        private static bool finished;
        private static object ParseValue(Type t, string v)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (t == typeof(bool)) { string l = v.ToLowerInvariant(); return l == "true" || l == "1" || l == "on"; }
            if (t.IsEnum) return Enum.Parse(t, v, true);
            if (t == typeof(string)) return v;
            return Convert.ChangeType(v, t, inv);
        }

        private static void RestoreSets()
        {
#if DEV || AUTOTEST
            PerfOverlay.TestFreezeOutputSample(false);
#endif
            if (setOrig.Count == 0) return;
            foreach (var kv in setOrig) kv.Key.SetValue(Main.Config, kv.Value);
            setOrig.Clear();
            try { Main.ApplyConfig(); } catch (Exception ex) { Log("설정 되돌린 뒤 반영 실패: " + ex.Message); }
            Log("바꾼 설정 되돌림");
        }

        // keep 묶음의 끝: 게임은 켜 둔 채 결과 표시만 남기고 다음 autotest.txt 를 기다린다
#if DEV || AUTOTEST
        internal static void StopLiveDecoTest(string why) { Finish(why); }
#endif
        private static void EndBatch(string why)
        {
#if AUTOTEST
            GpuExactCensus.Stop();
            EngineKnob.Stop();
            PresentPath.Stop();
            ScriptPath.Stop();
#endif
            Occlude.Stop();
#if AUTOTEST
            DecoBake.FinishGpuMergeTest(why);
#endif
#if DEV || AUTOTEST
            HiddenDecoSynergy.Reset();
            LiveDecoGate2.Abort(why);
            LiveDeco.Stop();
            TightSpriteProbe.Stop();
            TileRender.Shutdown();
            FrameCompare.Stop();
#endif
            if (finished) return;
            finished = true;
            EndRun();
            try { if (autoChanged) RDC.auto = prevAuto; } catch { }
            autoChanged = false;
            try { RestoreSets(); } catch (Exception ex) { Log("설정 되돌리기 실패: " + ex.Message); }
            Log("묶음 끝: " + why);
            steps = null;
            pollAt = Time.realtimeSinceStartup + 1f;
            try { File.WriteAllText(Path.Combine(modDir, "autotest.end"), why); } catch { }
            if (reloadPending)
            {
                reloadPending = false;
                ReloadNow = true;   // 다음 OnUpdate 맨 앞에서 Ctrl+F5 와 같이 (새 DLL 의 Init 이 다음 묶음을 받는다)
            }
        }

        private static void Finish(string why)
        {
#if AUTOTEST
            EngineKnob.Stop();
            PresentPath.Stop();
            ScriptPath.Stop();
#endif
            Occlude.Stop();
#if AUTOTEST
            DecoBake.FinishGpuMergeTest(why);
#endif
#if DEV || AUTOTEST
            HiddenDecoSynergy.Reset();
            LiveDecoGate2.Abort(why);
            TightSpriteProbe.Stop();
            TileRender.Shutdown();
            FrameCompare.Stop();
            MatShare.Stop();
            LiveDeco.Stop();
#endif
#if DEV || AUTOTEST
            MicroBench.Cancel();
            InstBench.Cancel();
            MeshBench.Cancel();
            SortProbe.Cancel();
#endif
            if (keep) { EndBatch(why); return; }
            if (finished) return;
            finished = true;
            try { RestoreSets(); } catch { }
            EndRun();
            Log(why + " - 게임을 끕니다");
            steps = null;
            try { if (autoChanged) RDC.auto = prevAuto; } catch { }
            RestartAdvisor.ForceQuitNext();
            Application.Quit();
        }
    }
}
