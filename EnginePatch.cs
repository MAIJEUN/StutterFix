using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace StutterFix
{
    internal static class EnginePatch
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandleW(string lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FlushInstructionCache(IntPtr hProcess, IntPtr lpBaseAddress, UIntPtr dwSize);

        private const uint PAGE_EXECUTE_READWRITE = 0x40;
        private const string ExpectedUnityVersion = "6000.3.25f1";

        // ── 1) SpriteMask 렌더 노드 배열 2배 확장 패치 (Unity 6000.3.25f1 정확한 RVA 0x1148649) ──
        private const int MaskGrowRva = 0x1148649;
        private static readonly byte[] MaskGrowGuard    = { 0x48, 0x3B, 0xD8, 0x76, 0x0B }; // cmp rbx,rax / jbe
        private static readonly byte[] MaskGrowOriginal = { 0x48, 0x8B, 0xD3, 0x49, 0x8B, 0xCF };
        private static readonly byte[] MaskGrowPatched  = { 0x49, 0x8B, 0xCF, 0x8D, 0x14, 0x1B };
        private static bool maskApplied;

        // ── 2) WaitSkip v5: 메인 스레드 TimeUpdate 대기 우회(0x5D0A62) + 렌더 스레드 0ms 비차단 폴링(0x8E815F+0x8E8176) ──
        private const int FwdSkipRva = 0x5D0A62;
        private static readonly byte[] FwdSkipBefore = { 0x74, 0x13 };
        private static readonly byte[] FwdSkipAfter  = { 0xEB, 0x13 };

        private const int WaitBranchRva = 0x8E815F;
        private static readonly byte[] WaitBranchBefore = { 0x0F, 0x85, 0x91, 0x00, 0x00, 0x00 };
        private static readonly byte[] WaitBranchAfter  = { 0x90, 0x90, 0x90, 0x90, 0x90, 0x90 };

        private const int WaitTimeoutRva = 0x8E8176;
        private static readonly byte[] WaitTimeoutBefore = { 0xBA, 0xC8, 0x00, 0x00, 0x00 };
        private static readonly byte[] WaitTimeoutAfter  = { 0xBA, 0x00, 0x00, 0x00, 0x00 };
        private static bool waitApplied;

        internal static void MaskGrow(bool on)
        {
            if (on == maskApplied) return;
            if (Application.unityVersion != ExpectedUnityVersion) return;
            IntPtr module = GetModuleHandleW("UnityPlayer.dll");
            if (module == IntPtr.Zero) return;
            IntPtr target = (IntPtr)(module.ToInt64() + MaskGrowRva);
            for (int i = 0; i < MaskGrowGuard.Length; i++)
                if (Marshal.ReadByte(target, i - MaskGrowGuard.Length) != MaskGrowGuard[i])
                {
                    Main.Entry.Logger.Log("[엔진 패치] 건너뜀: MaskGrow guard mismatch");
                    return;
                }
            byte[] want = on ? MaskGrowOriginal : MaskGrowPatched;
            byte[] put  = on ? MaskGrowPatched  : MaskGrowOriginal;
            if (!MatchBytes(target, want))
            {
                Main.Entry.Logger.Log("[엔진 패치] 건너뜀: MaskGrow byte mismatch");
                return;
            }
            if (WriteBytes(target, put))
            {
                maskApplied = on;
                Main.Entry.Logger.Log("[엔진 패치] 마스크 노드 배열 늘리기(0x1148649) " + (on ? "켬" : "끔"));
            }
        }

        internal static void WaitSkip(bool on)
        {
            if (on == waitApplied) return;
            if (Application.unityVersion != ExpectedUnityVersion) return;
            IntPtr mod = GetModuleHandleW("UnityPlayer.dll");
            if (mod == IntPtr.Zero) return;

            IntPtr addrFwd = (IntPtr)(mod.ToInt64() + FwdSkipRva);
            IntPtr addrBranch = (IntPtr)(mod.ToInt64() + WaitBranchRva);
            IntPtr addrTimeout = (IntPtr)(mod.ToInt64() + WaitTimeoutRva);

            if (on)
            {
                if (!MatchBytes(addrFwd, FwdSkipBefore) || !MatchBytes(addrBranch, WaitBranchBefore) || !MatchBytes(addrTimeout, WaitTimeoutBefore))
                {
                    Main.Entry.Logger.Log("[엔진 패치] 건너뜀: WaitSkip v5 바이트 불일치");
                    return;
                }
                if (WriteBytes(addrFwd, FwdSkipAfter) && WriteBytes(addrBranch, WaitBranchAfter) && WriteBytes(addrTimeout, WaitTimeoutAfter))
                {
                    waitApplied = true;
                    Main.Entry.Logger.Log("[엔진 패치] 화면 대기 건너뛰기(WaitSkip v5 0x5D0A62+0x8E815F+0x8E8176 0ms 폴링) 켬");
                }
            }
            else
            {
                WriteBytes(addrFwd, FwdSkipBefore);
                WriteBytes(addrBranch, WaitBranchBefore);
                WriteBytes(addrTimeout, WaitTimeoutBefore);
                waitApplied = false;
                Main.Entry.Logger.Log("[엔진 패치] 화면 대기 건너뛰기(WaitSkip v5) 끔");
            }
        }

        private static bool MatchBytes(IntPtr addr, byte[] expected)
        {
            for (int i = 0; i < expected.Length; i++)
                if (Marshal.ReadByte(addr, i) != expected[i]) return false;
            return true;
        }

        private static bool WriteBytes(IntPtr addr, byte[] bytes)
        {
            uint oldProtect;
            UIntPtr size = (UIntPtr)bytes.Length;
            if (!VirtualProtect(addr, size, PAGE_EXECUTE_READWRITE, out oldProtect)) return false;
            try
            {
                Marshal.Copy(bytes, 0, addr, bytes.Length);
                FlushInstructionCache(GetCurrentProcess(), addr, size);
                return true;
            }
            finally
            {
                uint dummy;
                VirtualProtect(addr, size, oldProtect, out dummy);
            }
        }

        // ── 3) 무손실 CPU + GPU 가속 (TightLate.SkipSame + UpdateEvalMemo + ClipperSkip + EventSystem/SendMouseEvents 유휴 생략 + Flash 투명 쿼드 컬링) ──
        private static bool turboActive;
        private static Harmony turboHarmony;
        private static readonly List<Camera> disabledIdleCams = new List<Camera>();
        private static readonly List<(Camera cam, DepthTextureMode mode, bool occ)> savedCamModes = new List<(Camera, DepthTextureMode, bool)>();
        private static readonly List<Renderer> flashRenderers = new List<Renderer>();
        private static readonly List<Material> flashMaterials = new List<Material>();
        private static bool prevGpuListCaching, prevTightSkipSame, prevEvalMemo;
        private static long mouseSkippedFrames, flashCulledFrames, clipperSkippedFrames, eventSysSkippedFrames;

        internal static void TurboCommand(string arg)
        {
            string cmd = (arg ?? "").Trim().ToLowerInvariant();
            if (cmd == "off")
            {
                DisableTurbo();
                Main.Entry.Logger.Log("[turbo] 꺼짐");
                return;
            }
            if (cmd == "state")
            {
                Main.Entry.Logger.Log($"[turbo] active={turboActive} gpuLateCache={TightLate.GpuListCaching} tightSkipSame={TightLate.SkipSame} evalMemo={UpdateEvalMemo.Enabled} mouseSkips={mouseSkippedFrames} eventSysSkips={eventSysSkippedFrames} flashCulled={flashCulledFrames} clipperSkips={clipperSkippedFrames}");
                return;
            }
            DisableTurbo();
            bool exactOnly = cmd == "safe" || cmd == "safe2" || cmd == "lite"; bool keepDepth = cmd == "safe2" || cmd == "lite"; bool scanRenderers = cmd != "lite";   // lite: 곡 시작 끊김을 피하려고 모든 렌더러 훑기를 하지 않는다   // UI 마스크 잘라내기 건너뛰기와 거의 투명한 플래시 판 숨기기를 뺀다(결과가 정확히 같다고 확인되지 않음)
            turboActive = true;
            mouseSkippedFrames = 0;
            flashCulledFrames = 0;
            clipperSkippedFrames = 0;
            eventSysSkippedFrames = 0;

            prevGpuListCaching = TightLate.GpuListCaching;
            prevTightSkipSame = TightLate.SkipSame;
            prevEvalMemo = UpdateEvalMemo.Enabled;

            TightLate.GpuListCaching = true;
            TightLate.SkipSame = true;
            UpdateEvalMemo.Enabled = true;

            int depthCleared = 0;
            foreach (var c in Resources.FindObjectsOfTypeAll<Camera>())
            {
                if (!c || !c.gameObject.scene.IsValid()) continue;
                if (c.name == "PausePlanets" && c.enabled)
                {
                    c.enabled = false;
                    disabledIdleCams.Add(c);
                }
                if (!keepDepth && (c.depthTextureMode != DepthTextureMode.None || c.useOcclusionCulling))
                {
                    savedCamModes.Add((c, c.depthTextureMode, c.useOcclusionCulling));
                    c.depthTextureMode = DepthTextureMode.None;
                    c.useOcclusionCulling = false;
                    depthCleared++;
                }
            }

            int rflagsCount = 0;
            if (scanRenderers) foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if (!r || !r.gameObject.scene.IsValid()) continue;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.lightProbeUsage = LightProbeUsage.Off;
                r.reflectionProbeUsage = ReflectionProbeUsage.Off;
                r.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                r.allowOcclusionWhenDynamic = false;
                rflagsCount++;

                string rn = r.name ?? "";
                if (!exactOnly && (rn == "Flash" || rn == "FlashPlusBackground" || rn == "FlashPlusForeground" || rn == "FlashEndscreen"))
                {
                    var mat = r.sharedMaterial;
                    if (mat != null && mat.HasProperty("_Color"))
                    {
                        flashRenderers.Add(r);
                        flashMaterials.Add(mat);
                    }
                }
            }

            Camera.onPreCull -= OnPreCullFlashSkip;
            Camera.onPreCull += OnPreCullFlashSkip;

            turboHarmony = new Harmony("StutterFix.CleanTurbo");
            var smeType = AccessTools.TypeByName("UnityEngine.SendMouseEvents");
            var sme = smeType != null ? AccessTools.Method(smeType, "DoSendMouseEvents") : null;
            if (sme != null)
                turboHarmony.Patch(sme, prefix: new HarmonyMethod(typeof(EnginePatch), nameof(SendMouseEventsPrefix)));

            var esUpdate = AccessTools.Method(typeof(EventSystem), "Update");
            if (esUpdate != null)
                turboHarmony.Patch(esUpdate, prefix: new HarmonyMethod(typeof(EnginePatch), nameof(EventSystemUpdatePrefix)));

            var cullMethod = AccessTools.Method(typeof(ClipperRegistry), "Cull");
            if (cullMethod != null && !exactOnly)
                turboHarmony.Patch(cullMethod, prefix: new HarmonyMethod(typeof(EnginePatch), nameof(ClipperCullPrefix)));

            Main.Entry.Logger.Log($"[turbo] 켜짐 mode=clean_lossless_v14 idleCamsDisabled={disabledIdleCams.Count} depthCleared={depthCleared} rflags={rflagsCount} flashQuads={flashRenderers.Count}");
        }

        private static void OnPreCullFlashSkip(Camera cam)
        {
            if (!turboActive || flashRenderers.Count == 0) return;
            for (int i = 0; i < flashRenderers.Count; i++)
            {
                var r = flashRenderers[i];
                var m = flashMaterials[i];
                if (!r || !m) continue;
                bool transparent = m.color.a <= 1e-4f;
                if (r.forceRenderingOff != transparent)
                    r.forceRenderingOff = transparent;
                if (transparent) flashCulledFrames++;
            }
        }

        public static bool ClipperCullPrefix()
        {
            if (!turboActive || !Hitch.Playing) return true;
            var ctrl = ADOBase.controller;
            if (ctrl == null || ctrl.paused) return true;
            if (scnEditor.instance != null && !scnEditor.instance.playMode) return true;
            if ((Time.frameCount & 31) == 0) return true;
            clipperSkippedFrames++;
            return false;
        }

        public static bool EventSystemUpdatePrefix()
        {
            if (!turboActive || !Hitch.Playing) return true;
            var ctrl = ADOBase.controller;
            if (ctrl == null || ctrl.paused) return true;
            if (scnEditor.instance != null && !scnEditor.instance.playMode) return true;
            if (Input.anyKey || Input.anyKeyDown || Input.GetMouseButton(0) || Input.GetMouseButton(1)) return true;
            eventSysSkippedFrames++;
            return false;
        }

        internal static void DisableTurbo()
        {
            Camera.onPreCull -= OnPreCullFlashSkip;
            for (int i = 0; i < flashRenderers.Count; i++)
                if (flashRenderers[i]) flashRenderers[i].forceRenderingOff = false;
            flashRenderers.Clear();
            flashMaterials.Clear();

            for (int i = 0; i < savedCamModes.Count; i++)
            {
                var entry = savedCamModes[i];
                if (entry.cam)
                {
                    entry.cam.depthTextureMode = entry.mode;
                    entry.cam.useOcclusionCulling = entry.occ;
                }
            }
            savedCamModes.Clear();

            if (turboHarmony != null)
            {
                try { turboHarmony.UnpatchOwner("StutterFix.CleanTurbo"); } catch { }
                turboHarmony = null;
            }
            foreach (var c in disabledIdleCams) if (c) c.enabled = true;
            disabledIdleCams.Clear();
            if (turboActive)
            {
                TightLate.GpuListCaching = prevGpuListCaching;
                TightLate.SkipSame = prevTightSkipSame;
                UpdateEvalMemo.Enabled = prevEvalMemo;
            }
            turboActive = false;
        }

        public static bool SendMouseEventsPrefix()
        {
            if (!turboActive || !Hitch.Playing) return true;
            var ctrl = ADOBase.controller;
            if (ctrl == null || ctrl.paused) return true;
            if (scnEditor.instance != null && !scnEditor.instance.playMode) return true;
            if (Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButtonDown(0) || Input.GetMouseButtonUp(0)) return true;
            mouseSkippedFrames++;
            return false;
        }

        // ── 4) 비관리자 DwmGetCompositionTimingInfo 지연 측정기 (292B 구조체) ──
        [DllImport("dwmapi.dll")] private static extern int DwmGetCompositionTimingInfo(IntPtr hwnd, IntPtr pTimingInfo);
        private static readonly uint[] DwmSizes = { 292u, 296u, 288u, 284u, 304u };
        private static uint workingDwmSize;
        private static IntPtr dwmBuf;
        private static bool latActive;
        private static long latSamples, latSumOut, latMaxOut, latSumPending, latMaxPending, latSumLate;
        private static double latSumMs, latMaxMs;

        internal static void LatencyCommand(string arg)
        {
            string cmd = (arg ?? "").Trim().ToLowerInvariant();
            if (cmd == "on" || cmd == "reset")
            {
                if (dwmBuf == IntPtr.Zero) dwmBuf = Marshal.AllocHGlobal(512);
                latSamples = latSumOut = latMaxOut = latSumPending = latMaxPending = latSumLate = 0;
                latSumMs = latMaxMs = 0;
                latActive = true;
            }
            else if (cmd == "off") { latActive = false; }
            else if (cmd == "state")
            {
                if (latSamples <= 0) { Main.Entry.Logger.Log("[latprobe] 표본 없음"); return; }
                Main.Entry.Logger.Log(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "[latprobe] samples={0} maxQueued={1} waitSkip={2} outstanding_avg={3:F2} outstanding_max={4} pending_avg={5:F2} pending_max={6} late_frames={7} queue_latency_avg_ms={8:F2} queue_latency_max_ms={9:F2}",
                    latSamples, QualitySettings.maxQueuedFrames, waitApplied,
                    (double)latSumOut / latSamples, latMaxOut,
                    (double)latSumPending / latSamples, latMaxPending,
                    latSumLate, latSumMs / latSamples, latMaxMs));
            }
        }

        internal static void LatencySampleFrame()
        {
            if (!latActive || dwmBuf == IntPtr.Zero) return;
            try
            {
                int hr = -1;
                if (workingDwmSize != 0)
                {
                    for (int k = 0; k < 512; k += 8) Marshal.WriteInt64(dwmBuf, k, 0L);
                    Marshal.WriteInt32(dwmBuf, 0, (int)workingDwmSize);
                    hr = DwmGetCompositionTimingInfo(IntPtr.Zero, dwmBuf);
                }
                else
                {
                    foreach (uint sz in DwmSizes)
                    {
                        for (int k = 0; k < 512; k += 8) Marshal.WriteInt64(dwmBuf, k, 0L);
                        Marshal.WriteInt32(dwmBuf, 0, (int)sz);
                        hr = DwmGetCompositionTimingInfo(IntPtr.Zero, dwmBuf);
                        if (hr >= 0) { workingDwmSize = sz; break; }
                    }
                }
                if (hr < 0) return;

                ulong cFrameSubmitted = (ulong)Marshal.ReadInt64(dwmBuf, 148);
                uint cFramesPending = (uint)Marshal.ReadInt32(dwmBuf, 180);
                uint cFramesMissed = (uint)Marshal.ReadInt32(dwmBuf, 192);
                ulong cFramesComplete = (ulong)Marshal.ReadInt64(dwmBuf, 212);

                long outFrames = 0;
                if (cFrameSubmitted >= cFramesComplete)
                {
                    ulong diff = cFrameSubmitted - cFramesComplete;
                    if (diff < 64UL) outFrames = (long)diff;
                }
                if (outFrames == 0 && cFramesPending > 0 && cFramesPending < 64u)
                    outFrames = cFramesPending;

                double refreshHz = Screen.currentResolution.refreshRate > 0 ? Screen.currentResolution.refreshRate : 165.0;
                double framePeriodMs = 1000.0 / refreshHz;
                double estQueueMs = Math.Max(1L, outFrames) * framePeriodMs;

                latSamples++;
                latSumOut += Math.Max(1L, outFrames);
                if (outFrames > latMaxOut) latMaxOut = outFrames;
                latSumPending += Math.Max(1u, cFramesPending);
                if (cFramesPending > latMaxPending) latMaxPending = cFramesPending;
                if (cFramesMissed > 0) latSumLate++;
                latSumMs += estQueueMs;
                if (estQueueMs > latMaxMs) latMaxMs = estQueueMs;
            }
            catch { }
        }
    }
}
