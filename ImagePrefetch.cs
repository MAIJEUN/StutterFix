using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 맵을 열 때 장식 이미지를 여러 코어에서 미리 풀어 둔다.
    //
    // 측정 (HELLO (BPM) 2026, 장식 2531개):
    //   scnGame.UpdateDecorationObjects 66.9초 중 TextureManager.LoadTexture 719번이 65.5초
    //   이미지는 평균 430만 화소, 800만 화소 넘는 것 99장, 최대 10000x10000. 원본 합계 약 13GB 픽셀.
    //   게임은 이것을 메인 스레드 한 곳에서 한 장씩 ReadAllBytes -> LoadImage 한다. 나머지 5개 코어는 논다.
    //
    // 방법:
    //   1) UpdateDecorationObjects 가 시작될 때, 게임과 같은 규칙으로 불러올 이미지 경로를 순서대로 뽑는다
    //   2) 작업 스레드들이 그 순서대로 파일을 읽고 PNG 를 풀어 둔다 (풀어 둔 양은 MaxPendingMB 까지만)
    //   3) LoadTexture 안의 두 호출만 바꾼다
    //        RDFile.ReadAllBytes(path)    -> 미리 풀어 둔 것이 있으면 그 표식을, 없으면 원래대로 읽기
    //        ImageConversion.LoadImage()  -> 표식이면 풀어 둔 픽셀을 그대로 넣기, 아니면 원래대로
    //      상태값, 이름, Apply, wrapMode 같은 나머지는 게임 코드 그대로 돈다.
    // 미리 못 푼 것(16비트/흑백 PNG, JPG, 순서가 어긋난 것, 오류)은 전부 원래 방식으로 처리된다.
    public static class ImagePrefetch
    {
        internal static bool Enabled = true;
        internal static int MaxPendingMB = 1500;
        internal static string Last = "아직 안 함";

        private class Item
        {
            public string Path;
            public int Index;
            public int State;          // 0 대기, 1 푸는 중, 2 끝, 3 못 함(원래 방식), 4 가져감
            public int Width, Height, Format;
            public IntPtr Pixels;
            public long Size;
            public float Factor = 1f;   // 큰 이미지 줄이기로 줄인 비율 (1 이면 그대로)
        }

        private static readonly object gate = new object();
        private static List<Item> items = new List<Item>();
        private static Dictionary<string, Item> byPath = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
        private static int next;
        private static long pendingBytes;
        private static int consumed;   // 메인 스레드가 마지막으로 가져간 순번
        private static bool running;
        internal static bool Running { get { return running; } }   // 실시간 모니터가 "불러오는 중" 을 가릴 때 쓴다
        private static Thread[] workers = new Thread[0];

        private static int used, fallback, notReady;
        private static double waitMs;
        private static long startTicks;

        // ReadAllBytes 가 돌려준 "미리 풀어 둔 것" 표식. 이 배열 자체가 LoadImage 로 넘어온다.
        [ThreadStatic] private static Dictionary<byte[], Item> markers;

        internal static void Install(Harmony harmony)
        {
            try
            {
                var update = AccessTools.Method(typeof(scnGame), "UpdateDecorationObjects");
                var load = AccessTools.Method(typeof(TextureManager), "LoadTexture");
                if (update == null || load == null) { Main.Entry.Logger.Error("[이미지] 대상 없음"); return; }
                harmony.Patch(update,
                    prefix: new HarmonyMethod(typeof(ImagePrefetch), nameof(Begin)),
                    finalizer: new HarmonyMethod(typeof(ImagePrefetch), nameof(End)));
                harmony.Patch(load, transpiler: new HarmonyMethod(typeof(ImagePrefetch), nameof(Transpiler)));

                // 게임 버그: 없는 이미지를 장식 여러 개가 쓰면, 두 번째 실패에서 오류 목록 Dictionary.Add 가
                // "같은 키" 예외를 내고 장식 불러오기가 통째로 멈춘다(DDONGSSADA3302 의 nev_text_-.png, 322/2770 에서 중단).
                // 이미 적힌 이름이면 다시 적지 않게 한다. 실패한 이미지에서만 불리므로 비용은 없다.
                var result = AccessTools.Method(typeof(scnEditor), "UpdateImageLoadResult");
                errorsField = AccessTools.Field(typeof(scnEditor), "errorImageResult");
                if (result != null && errorsField != null)
                    harmony.Patch(result, prefix: new HarmonyMethod(typeof(ImagePrefetch), nameof(SkipDuplicateError)));

                // 줄인 이미지의 스프라이트 크기 기준을 맞춘다 (texture, fileLastModified, isInternal, isFromBundle, pixelsPerUnit, spriteType)
                var spriteType = AccessTools.TypeByName("CustomSprite") ?? AccessTools.TypeByName("ADOFAI.CustomSprite");
                if (spriteType != null) foreach (var ctor in spriteType.GetConstructors(AccessTools.all))
                {
                    var ps = ctor.GetParameters();
                    if (ps.Length < 5 || ps[0].ParameterType != typeof(Texture2D) || ps[4].Name != "pixelsPerUnit") continue;
                    harmony.Patch(ctor, prefix: new HarmonyMethod(typeof(ImagePrefetch), nameof(SpritePrefix)));
                    Main.Entry.Logger.Log("[이미지] 스프라이트 크기 보정 설치 (큰 이미지 줄이기용)");
                }
                Main.Entry.Logger.Log("[이미지] 미리 풀기 설치" + (swapped == 2 ? "" : " (LoadTexture 모양이 달라 적용 안 됨)"));
            }
            catch (Exception ex) { Main.Entry.Logger.Error("[이미지] 설치 실패: " + ex.Message); }
        }

        // ── 큰 이미지 줄이기 (선택) ──────────────────────────────────────
        // 이미지가 수천 장인 맵은 텍스처가 VRAM 을 넘쳐 GPU 가 프레임당 90ms 넘게 걸렸다(DDONGSSADA3302).
        // 긴 변이 MaxSide 를 넘는 이미지를 작업 스레드에서 풀자마자 줄인다. VRAM 과 로딩 시간이 같이 준다.
        // 스프라이트의 화면 크기는 "픽셀 수 / pixelsPerUnit(100)" 이라, 이미지만 줄이면 장식이 작아진다.
        // 그래서 줄인 비율만큼 pixelsPerUnit 도 줄여서(CustomSprite 생성자) 화면 크기는 그대로 둔다. 화질만 낮아진다.
        internal static int MaxSide;   // 0 = 끔, Auto(-1) = 자동, 그 외 = 긴 변 한도
        internal const int Auto = -1;
        private static volatile int sideNow;   // 이번 맵에 실제로 쓰는 한도 (자동이면 맵마다 정한다)
        private static int shrunkCount;
        private static long savedBytes;
        // 마지막으로 불러온 맵에서 실제로 줄인 결과 (실시간 모니터 VRAM 줄에 보여 준다)
        internal static int LastSide, LastShrunk;
        internal static bool AnyLoad;   // 미리 풀기로 맵을 한 번이라도 불러왔는가
        internal static float LastSavedMB;
        private static readonly Dictionary<Texture2D, float> shrunk = new Dictionary<Texture2D, float>();

        // 자동: VramGuard 가 "VRAM 이 모자라 끊긴 맵" 에 기억해 둔 한도를 쓴다. 기억이 없으면 원본 그대로.
        internal static string AutoNote = "";


        public static void SpritePrefix(Texture2D texture, ref float pixelsPerUnit) { AdjustPixelsPerUnit(texture, ref pixelsPerUnit); }

        public static void AdjustPixelsPerUnit(Texture2D texture, ref float pixelsPerUnit)
        {
            float f;
            if (texture == null || !shrunk.TryGetValue(texture, out f)) return;
            shrunk.Remove(texture);
            pixelsPerUnit *= f;
        }

        private static FieldInfo errorsField;

        public static bool SkipDuplicateError(scnEditor __instance, string name)
        {
            try
            {
                var errors = errorsField.GetValue(__instance) as System.Collections.IDictionary;
                if (errors != null && name != null && errors.Contains(name)) return false;   // 이미 적혀 있다
            }
            catch { }
            return true;
        }

        private static int swapped;
        private static readonly FieldInfo spritesField = AccessTools.Field(typeof(TextureManager), "customSprites");

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var readAll = AccessTools.Method(typeof(RDFile), "ReadAllBytes");
            var loadImage = AccessTools.Method(typeof(ImageConversion), nameof(ImageConversion.LoadImage), new[] { typeof(Texture2D), typeof(byte[]) });
            var code = new List<CodeInstruction>(instructions);
            int a = -1, b = -1;
            for (int i = 0; i < code.Count; i++)
            {
                if (readAll != null && code[i].Calls(readAll)) a = i;
                else if (loadImage != null && code[i].Calls(loadImage)) b = i;
            }
            swapped = 0;
            if (a < 0 || b < 0 || a > b) return code;
            code[a].operand = AccessTools.Method(typeof(ImagePrefetch), nameof(ReadAllBytes));
            code[b].operand = AccessTools.Method(typeof(ImagePrefetch), nameof(LoadImage));
            swapped = 2;
            return code;
        }

        // ── 1) 불러올 순서 뽑기 ─────────────────────────────────────────
        public static void Begin(scnGame __instance)
        {
            if (!Enabled || swapped != 2) return;
            try
            {
                Stop();
                string dir = Path.GetDirectoryName(__instance.levelPath);
                sideNow = MaxSide > 0 ? MaxSide : MaxSide == Auto ? VramGuard.CapFor(__instance.levelPath) : 0;
                if (MaxSide == Auto)
                {
                    AutoNote = sideNow > 0 ? "자동: 전에 VRAM 이 모자라 끊긴 맵이라 긴 변 " + sideNow + " 으로 줄임" : "자동: 원본 그대로 (이 맵에서 VRAM 부족 끊김 기록 없음)";
                    Main.Entry.Logger.Log("[이미지] " + AutoNote);
                }
                var list = new List<Item>();
                var seen = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
                var cached = spritesField != null ? spritesField.GetValue(__instance.imgHolder) as System.Collections.IDictionary : null;

                Action<ADOFAI.LevelEvent> add = ev =>
                {
                    if (ev == null || !ev.ContainsKey("decorationImage")) return;
                    var img = ev["decorationImage"] as string;
                    if (string.IsNullOrEmpty(img) || img.StartsWith("prefab:", StringComparison.OrdinalIgnoreCase)) return;
                    if (cached != null && cached.Contains(img)) return;   // 이미 불러온 이미지는 게임이 다시 풀지 않는다
                    string path = Path.Combine(dir, img);
                    if (seen.ContainsKey(path)) return;
                    if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return;   // JPG 등은 원래 방식
                    var it = new Item { Path = path, Index = list.Count };
                    seen[path] = it;
                    list.Add(it);
                };
                foreach (var ev in __instance.decorations) add(ev);
                foreach (var ev in __instance.events) if ((int)ev.eventType == 29) add(ev);

                // 이미 올라온 이미지를 다시 쓰는 경우(같은 맵 다시 열기)에는 그때의 한도가 그대로 남는다
                VramGuard.OnLevelLoaded(__instance.levelPath, sideNow, list.Count >= 8);
                if (list.Count < 8) return;   // 몇 장 안 되면 그냥 원래대로

                lock (gate)
                {
                    items = list; byPath = seen; next = 0; pendingBytes = 0; running = true; consumed = -1;
                    used = fallback = notReady = 0; waitMs = 0; shrunkCount = 0; savedBytes = 0;
                }
                startTicks = Stopwatch.GetTimestamp();
                putMs = fallbackMs = 0;
                gcAtStart = GC.CollectionCount(0);
                // 로딩 동안 GC를 꺼 둔다. 지난번 로딩 중 GC가 13번 돌았다. 곡 중 GC 멈춤이 이미 끈 상태면 건드리지 않는다.
                if (!GcControl.Paused && UnityEngine.Scripting.GarbageCollector.GCMode == UnityEngine.Scripting.GarbageCollector.Mode.Enabled)
                {
                    try { UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Disabled; gcWasOn = true; }
                    catch { }
                }

                int n = Math.Max(1, Math.Min(6, Environment.ProcessorCount - 1));
                workers = new Thread[n];
                for (int i = 0; i < n; i++)
                {
                    workers[i] = new Thread(Work) { IsBackground = true, Name = "StutterFix.Image" + i, Priority = System.Threading.ThreadPriority.BelowNormal };
                    workers[i].Start();
                }
                Main.Entry.Logger.Log("[이미지] " + list.Count + "장 미리 풀기 시작 (작업 스레드 " + n + "개)");
            }
            catch (Exception ex) { Main.Entry.Logger.Error("[이미지] 시작 실패: " + ex.Message); Stop(); }
        }

        // ── 2) 작업 스레드 ─────────────────────────────────────────────
        private static void Work()
        {
            while (true)
            {
                Item it;
                lock (gate)
                {
                    // 풀어 둔 양이 많으면 메인 스레드가 가져갈 때까지 기다린다
                    while (running && next < items.Count && pendingBytes > (long)MaxPendingMB * 1048576)
                        Monitor.Wait(gate, 200);
                    if (!running || next >= items.Count) return;
                    it = items[next++];
                    if (it.State != 0) continue;
                    it.State = 1;
                }

                int w = 0, h = 0, f = 0; IntPtr px = IntPtr.Zero; long size = 0; bool ok = false; float factor = 1f;
                try
                {
                    int len = ReadInto(it.Path, ref fileBuf);
                    ok = len > 0 && PngDecoder.TryDecode(fileBuf, len, out w, out h, out f, out px, out size);
                    long before = (long)w * h * 4;   // GPU 에는 한 픽셀 4바이트로 올라간다
                    if (ok && sideNow > 0 && PngDecoder.Downscale(ref px, ref w, ref h, f, ref size, sideNow, out factor))
                    {
                        Interlocked.Increment(ref shrunkCount);
                        Interlocked.Add(ref savedBytes, before - (long)w * h * 4);
                    }
                }
                catch { ok = false; }

                lock (gate)
                {
                    if (!running || it.State != 1 || it.Index < consumed)   // 게임이 이미 지나간 것
                    {
                        if (px != IntPtr.Zero) Marshal.FreeHGlobal(px);
                    }
                    else if (ok)
                    {
                        it.Width = w; it.Height = h; it.Format = f; it.Pixels = px; it.Size = size; it.Factor = factor;
                        it.State = 2;
                        pendingBytes += size;
                    }
                    else it.State = 3;
                    Monitor.PulseAll(gate);
                }
            }
        }

        // 파일을 스레드마다 하나씩 둔 버퍼에 읽는다(이미지마다 새 배열을 만들지 않는다).
        [ThreadStatic] private static byte[] fileBuf;

        private static int ReadInto(string path, ref byte[] buf)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16))
            {
                long n = fs.Length;
                if (n <= 0 || n > int.MaxValue) return 0;
                if (buf == null || buf.Length < n) buf = new byte[Math.Max(n, buf == null ? 0 : buf.Length * 3 / 2)];
                int got = 0;
                while (got < n)
                {
                    int r = fs.Read(buf, got, (int)n - got);
                    if (r <= 0) return 0;
                    got += r;
                }
                return got;
            }
        }

        // ── 3) LoadTexture 안에서 바꿔 부르는 두 함수 ─────────────────────
        // 원래 RDFile.ReadAllBytes(path, out 상태)와 같은 모양이어야 한다. 성공이면 상태 0(게임이 캐시에 넣는 조건).
        public static byte[] ReadAllBytes(string path, out ADOFAI.LoadResult loadResult)
        {
            Item it = null;
            if (running)
            {
                long t0 = Stopwatch.GetTimestamp();
                lock (gate)
                {
                    if (byPath.TryGetValue(path, out it))
                    {
                        // 아직 시작 안 한 것은 기다리지 않는다(순서가 어긋났다는 뜻). 푸는 중이면 끝날 때까지 기다린다.
                        if (it.State == 0) { it.State = 3; notReady++; it = null; }
                        else
                        {
                            while (it.State == 1 && running) Monitor.Wait(gate, 100);
                            if (it.State == 2) { it.State = 4; pendingBytes -= it.Size; DropSkipped(it.Index); Monitor.PulseAll(gate); }
                            else it = null;
                        }
                    }
                }
                waitMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            }

            if (it == null)
            {
                if (running) fallback++;
                return RDFile.ReadAllBytes(path, out loadResult);
            }

            loadResult = (ADOFAI.LoadResult)0;
            var marker = new byte[1];
            if (markers == null) markers = new Dictionary<byte[], Item>();
            markers[marker] = it;
            return marker;
        }

        // 게임이 k번째를 가져갔는데 그 앞의 것을 안 가져갔다면 앞으로도 안 쓴다(게임이 걸러낸 것).
        // 풀어 둔 채로 두면 메모리 한도를 차지해 작업 스레드가 멈추므로 바로 버린다. gate 안에서 부른다.
        private static void DropSkipped(int k)
        {
            for (int i = consumed + 1; i < k && i < items.Count; i++)
            {
                var s = items[i];
                if (s.State == 2) { Marshal.FreeHGlobal(s.Pixels); s.Pixels = IntPtr.Zero; pendingBytes -= s.Size; }
                if (s.State == 0 || s.State == 2) s.State = 3;
            }
            if (k > consumed) consumed = k;
        }

        public static bool LoadImage(Texture2D tex, byte[] data)
        {
            Item it;
            long t0 = Stopwatch.GetTimestamp();
            if (data == null || markers == null || !markers.TryGetValue(data, out it))
            {
                bool r = ImageConversion.LoadImage(tex, data);
                if (running) fallbackMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                return r;
            }
            markers.Remove(data);
            try
            {
                tex.Reinitialize(it.Width, it.Height, (TextureFormat)it.Format, false);
                tex.LoadRawTextureData(it.Pixels, (int)it.Size);
                if (it.Factor < 1f) shrunk[tex] = it.Factor;   // 스프라이트를 만들 때 크기 기준을 맞춘다
                used++;
                putMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                return true;
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("[이미지] 넣기 실패, 원래 방식으로: " + ex.Message);
                fallback++;
                return ImageConversion.LoadImage(tex, File.ReadAllBytes(it.Path));
            }
            finally
            {
                Marshal.FreeHGlobal(it.Pixels);
                it.Pixels = IntPtr.Zero;
            }
        }

        // ── 끝 ─────────────────────────────────────────────────────────
        public static Exception End(Exception __exception)
        {
            if (running)
            {
                double total = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
                LastSide = sideNow; LastShrunk = shrunkCount; LastSavedMB = Interlocked.Read(ref savedBytes) / 1048576f; AnyLoad = true;
                Last = string.Format("미리 푼 것 {0}장(넣기 {5:F0}ms), 원래 방식 {1}장({6:F0}ms, 순서 어긋남 {2}), 기다림 {3:F0}ms, GC {7}번, 전체 {4:F1}초" + (shrunkCount > 0 ? ", 줄인 이미지 " + shrunkCount + "장 (긴 변 " + sideNow + ", VRAM 약 " + LastSavedMB.ToString("F0") + "MB 아낌)" : ""),
                    used, fallback, notReady, waitMs, total / 1000.0, putMs, fallbackMs, GC.CollectionCount(0) - gcAtStart);
                Main.Entry.Logger.Log("[이미지] " + Last);
            }
            Stop();
            // 로딩 동안 꺼 둔 GC를 되돌리고 한 번에 치운다(곡 중이 아니라 멈춰도 괜찮은 순간).
            if (gcWasOn)
            {
                gcWasOn = false;
                try
                {
                    UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Enabled;
                    long t0 = Stopwatch.GetTimestamp();
                    GC.Collect();
                    Main.Entry.Logger.Log(string.Format("[이미지] 로딩 뒤 GC 한 번 {0:F0}ms",
                        (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency));
                }
                catch { }
            }
            // 장식을 다 만든 직후 몇 프레임은 장식들이 처음 움직이며(블렌드 재질 만들기 등) 60ms 쯤 걸린다.
            // 곡 시작 전 편집 화면에서 끊김으로 잡혔는데 맵 불러오기의 끝부분이므로 불러오기로 적는다.
            ShaderWarm.LevelChanged = true;   // 새 장식/이벤트가 올라왔다: 다음 곡 시작 때 필터 셰이더를 다시 본다
            PerfOverlay.MarkLoading(SettingsWindow.T("맵 불러오기", "Level load"));
            return __exception;
        }

        private static double putMs, fallbackMs;
        private static int gcAtStart;
        private static bool gcWasOn;

        internal static void Stop()
        {
            Thread[] ws;
            lock (gate)
            {
                running = false;
                Monitor.PulseAll(gate);
                ws = workers;
                workers = new Thread[0];
            }
            foreach (var t in ws) { try { t.Join(5000); } catch { } }
            lock (gate)
            {
                foreach (var it in items)
                    if (it.Pixels != IntPtr.Zero && it.State == 2) { Marshal.FreeHGlobal(it.Pixels); it.Pixels = IntPtr.Zero; }
                items = new List<Item>();
                byPath = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
                pendingBytes = 0;
            }
            if (markers != null) markers.Clear();
        }
    }
}
