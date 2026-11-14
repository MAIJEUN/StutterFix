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
    // JPG 는 libjpeg-turbo 로 푼다(TurboJpeg.cs, 유니티 결과와 바이트까지 같음을 확인).
    // 미리 못 푼 것(순서가 어긋난 것, 지원하지 않는 형식, 오류)은 전부 원래 방식으로 처리된다.
    public static class ImagePrefetch
    {
        // 장식 이동 이벤트 번호: 게임 버전에 따라 번호가 밀릴 수 있어 이름으로 찾는다 (예전에는 29 로 적어 두었다)
        private static readonly int MoveDecoType = FindEventType("MoveDecorations");
        private static int FindEventType(string name) { try { return (int)Enum.Parse(typeof(ADOFAI.LevelEventType), name); } catch { return -1; } }
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
            public string DiskKey;
            public bool CachedBlocks;
            public float Factor = 1f;   // 큰 이미지 줄이기로 줄인 비율 (1 이면 그대로)
            public bool Extra;          // 추가 형식(흑백, 16비트)으로 푼 것: 개발자용에서 유니티 결과와 비교
            public bool Jpg;            // libjpeg-turbo 로 푼 JPG
            public IntPtr Blocks; public long BlockSize; public bool Dxt5;   // 미리 압축한 DXT (없으면 Zero)
            // 줄 묶음 압축: 전체 블록 줄, 다음에 나눠 줄 줄, 끝난 줄, 지금 압축 중인 묶음 수, 압축 그만둠(게임이 먼저 가져감), 다 됨
            public int Rows, NextRow, DoneRows, InFlight, Layout; public bool Abandoned, BlocksReady, Urgent;   // Urgent: 게임이 기다리는 중
            public bool PixelsOrphan, BlocksOrphan;
            public ulong[] Cells;   // 형태 칸(ShapeCellsCpu): 16x16 칸의 알파>0 표시   // 가져간 쪽이 풀기를 작업 스레드에 맡김 (압축 중인 묶음이 남아 있을 때)
            public ShapeCellsCpu.OpaqueInfo Opaque;
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

        // libdeflate.dll 은 모드 DLL 안에 넣어 두었다(업데이트로 모드 DLL 만 바뀌어도 같이 온다).
        // 모드 폴더에 같은 파일이 없거나 다르면 한 번 써 두고, 그 전체 경로로 불러온다. 이미 올라온 DLL 은 잠겨 있으므로
        // 내용이 같으면 쓰지 않는다. 어떤 이유로든 못 불러오면 원래 zlib(DeflateStream) 길로 푼다.
        private static void LoadNative()
        {
            string p = Extract("libdeflate.dll");
            if (p == null) NativeInflate.Status = "모드 안에 DLL 없음"; else NativeInflate.Init(p);
            Main.Entry.Logger.Log("[이미지] 빠른 압축 풀기(libdeflate): " + NativeInflate.Status);
            p = Extract("sfnative.dll");
            if (p == null) SfNative.Status = "모드 안에 DLL 없음"; else SfNative.Init(p);
            Main.Entry.Logger.Log("[이미지] 네이티브 필터 되돌리기·DXT 압축(sfnative): " + SfNative.Status);
            p = Extract("turbojpeg.dll");
            if (p == null) TurboJpeg.Status = "모드 안에 DLL 없음"; else TurboJpeg.Init(p);
            Main.Entry.Logger.Log("[이미지] JPG 풀기(libjpeg-turbo): " + TurboJpeg.Status);
        }

        // 이미지 미리 풀기를 꺼도 첫 판 FPS 떨어짐 막기(WindowGhost)가 sfnative 를 쓴다
        internal static void LoadSfNativeOnly()
        {
            if (SfNative.Ready) return;
            string p = Extract("sfnative.dll");
            if (p == null) SfNative.Status = "모드 안에 DLL 없음"; else SfNative.Init(p);
        }

        // 모드 DLL 안에 넣어 둔 네이티브 DLL 을 모드 폴더에 꺼내 두고 그 경로를 돌려준다(없으면 null). 이미 같은 파일이면 쓰지 않는다.
        private static string Extract(string name)
        {
            try
            {
                string path = Path.Combine(Main.Entry.Path, name);
                byte[] want;
                using (var s = typeof(ImagePrefetch).Assembly.GetManifestResourceStream("StutterFix." + name))
                {
                    if (s == null) return null;
                    want = new byte[s.Length];
                    int got = 0; while (got < want.Length) { int n = s.Read(want, got, want.Length - got); if (n <= 0) break; got += n; }
                }
                bool same = false;
                try { if (File.Exists(path)) { var have = File.ReadAllBytes(path); same = have.Length == want.Length && System.Linq.Enumerable.SequenceEqual(have, want); } } catch { }
                if (!same) { try { File.WriteAllBytes(path, want); } catch (Exception ex) { Main.Entry.Logger.Log("[이미지] " + name + " 쓰기 실패 (" + ex.Message + ")"); } }
                return path;
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[이미지] " + name + " 꺼내기 실패: " + ex.Message); return null; }
        }

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
                var create = AccessTools.Method(typeof(scrDecorationManager), "CreateDecoration");
                if (create != null) harmony.Patch(create, prefix: new HarmonyMethod(typeof(ImagePrefetch), nameof(CreatePrefix)), finalizer: new HarmonyMethod(typeof(ImagePrefetch), nameof(CreateFinalizer)));
                harmony.Patch(load, prefix: new HarmonyMethod(typeof(ImagePrefetch), nameof(LoadTexturePrefix)),
                    transpiler: new HarmonyMethod(typeof(ImagePrefetch), nameof(Transpiler)), postfix: new HarmonyMethod(typeof(ImagePrefetch), nameof(BrokenImagePostfix)));
                LoadNative();

                // 게임 버그: 없는 이미지를 장식 여러 개가 쓰면, 두 번째 실패에서 오류 목록 Dictionary.Add 가
                // "같은 키" 예외를 내고 장식 불러오기가 통째로 멈춘다(DDONGSSADA3302 의 nev_text_-.png, 322/2770 에서 중단).
                // 이미 적힌 이름이면 다시 적지 않게 한다. 실패한 이미지에서만 불리므로 비용은 없다.
                var result = AccessTools.Method(typeof(scnEditor), "UpdateImageLoadResult");
                errorsField = AccessTools.Field(typeof(scnEditor), "errorImageResult");
                if (result != null && errorsField != null)
                    harmony.Patch(result, prefix: new HarmonyMethod(typeof(ImagePrefetch), nameof(SkipDuplicateError)), postfix: new HarmonyMethod(typeof(ImagePrefetch), nameof(MarkBrokenResult)));

                // 맵 불러온 뒤 이미지 오류 안내창: 게임은 깨진 파일도 "알 수 없는 오류"(editor.dialog.imageError)로 적는다.
                // 이 모드가 깨졌다고 확인한 파일은 결과를 SFBroken 으로 적어 두고, 안내창을 만드는 동안에만 그 문구를 "깨진 파일"로 바꾼다.
                var show = AccessTools.Method(typeof(scnEditor), "ShowImageLoadResult", Type.EmptyTypes);
                if (show != null && errorsField != null)
                {
                    harmony.Patch(show, prefix: new HarmonyMethod(typeof(ImagePrefetch), nameof(ShowPrefix)), finalizer: new HarmonyMethod(typeof(ImagePrefetch), nameof(ShowFinalizer)));
                    var rds = AccessTools.TypeByName("RDString");
                    if (rds != null)
                        foreach (var m in rds.GetMethods(BindingFlags.Public | BindingFlags.Static))
                            if (m.Name == "Get" && m.ReturnType == typeof(string) && m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == typeof(string))
                                harmony.Patch(m, prefix: new HarmonyMethod(typeof(ImagePrefetch), nameof(RdStringPrefix)));
                }

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
        internal static float AfterLoadLogAt = -1f;
        internal static void LogAfterLoad()
        {
            if (AfterLoadLogAt < 0 || Time.realtimeSinceStartup < AfterLoadLogAt) return;
            AfterLoadLogAt = -1f;
            try
            {
                var holder = scnGame.instance != null ? scnGame.instance.imgHolder : null;
                var c2 = holder != null && spritesField != null ? spritesField.GetValue(holder) as System.Collections.IDictionary : null;
                Main.Entry.Logger.Log(string.Format("[이미지] 맵 연 뒤: 올라와 있는 이미지 {0}장, VRAM 전체 {1:F0}MB / 게임 {2:F0}MB", c2 != null ? c2.Count : -1, SystemMonitor.VramUsedMB, SystemMonitor.VramGameMB));
                VramGuard.AfterOpen(SystemMonitor.VramUsedMB, SystemMonitor.VramGameMB);
            }
            catch { }
        }
        internal static float LastSavedMB;
        private static readonly Dictionary<Texture2D, float> shrunk = new Dictionary<Texture2D, float>();

        // 자동: VramGuard 가 "VRAM 이 모자라 끊긴 맵" 에 기억해 둔 한도를 쓴다. 기억이 없으면 원본 그대로.
        internal static string AutoNote = "";


        // 다른 맵을 열 때 이전 맵의 이미지를 치운다.
        // 게임은 ReloadAssets 에서 "안 쓰는 것" 만 치우는데(MarkAllUnused -> ... -> Unload(onlyIfUnused)), 그 사이에
        // 옛 장식들이 한 번 더 초기화되며 옛 이미지를 "쓰는 중" 으로 다시 표시해서 하나도 안 치워졌다.
        // CICADA(2,740장) 다음에 Arche 를 열면 3,011장, VRAM 7.5GB 가 남았다(Arche 만 열면 4.2GB).
        // 새 맵이 쓸 수 있는 것은 "새 맵 폴더에 같은 이름, 같은 수정 시각의 파일이 있는 것" 뿐이다(게임도 그렇게 비교한다).
        // 그 밖의 것은 게임이 어차피 다시 쓰지 않으므로 내린다. 게임에 들어 있는 이미지(isInternal)와 번들은 건드리지 않는다.
        private static FieldInfo csInternal, csBundle, csModified;

        private static void FreePreviousLevel(TextureManager holder, System.Collections.IDictionary cached, string dir)
        {
            try
            {
                if (csModified == null)
                {
                    var cs = AccessTools.Inner(typeof(TextureManager), "CustomSprite");
                    if (cs == null) return;
                    csInternal = AccessTools.Field(cs, "isInternal");
                    csBundle = AccessTools.Field(cs, "isFromBundle");
                    csModified = AccessTools.Field(cs, "fileLastModified");
                    if (csInternal == null || csBundle == null || csModified == null) { csModified = null; return; }
                }
                var unload = AccessTools.Method(typeof(TextureManager), "UnloadSprite");
                if (unload == null) return;
                var drop = new List<object>();
                foreach (System.Collections.DictionaryEntry e in cached)
                {
                    var key = e.Key as string;
                    var sp = e.Value;
                    if (key == null || sp == null) continue;
                    if ((bool)csInternal.GetValue(sp) || (bool)csBundle.GetValue(sp)) continue;
                    bool usable = false;
                    try
                    {
                        var fi = new FileInfo(Path.Combine(dir, key));
                        usable = fi.Exists && fi.LastWriteTimeUtc == (DateTime)csModified.GetValue(sp);
                    }
                    catch { }
                    if (!usable) drop.Add(e.Key);
                }
                foreach (var k in drop) { try { unload.Invoke(holder, new[] { k }); } catch { } }
                if (drop.Count > 0) Main.Entry.Logger.Log("[이미지] 이전 맵 이미지 " + drop.Count + "장을 내림 (남은 것 " + cached.Count + "장)");
            }
            catch (Exception ex) { Main.Entry.Logger.Log("[이미지] 이전 맵 이미지 내리기 실패: " + ex.Message); }
        }

        public static void SpritePrefix(Texture2D texture, ref float pixelsPerUnit) { AdjustPixelsPerUnit(texture, ref pixelsPerUnit); }

        public static void AdjustPixelsPerUnit(Texture2D texture, ref float pixelsPerUnit)
        {
            float f;
            if (texture == null || !shrunk.TryGetValue(texture, out f)) return;
            shrunk.Remove(texture);
            pixelsPerUnit *= f;
        }

        private static FieldInfo errorsField;

        // 게임 버그: 파일은 있는데 이미지를 풀지 못하면(깨진 PNG) LoadTexture 가 null 을 돌려주면서 상태는 "성공" 으로 둔다.
        // 그러면 GetOrAddSprite 가 null 텍스처로 스프라이트를 만들다 null 예외를 내고, 맵 열기(장식 불러오기)가 그 자리에서 멈춘다.
        // 2026-10-04 ALPHA WYSI EX: nevCTF/nes_leeeeeeeeeeeetterbox_2-3-5.428571pp.png (4164x4164, 마지막 IDAT 체크섬 틀림·IEND 없음, 유니티 LoadImage 도 실패)
        // -> 장식 이동 45867번에서 멈춰 맵이 안 열림. 상태를 "오류" 로 바꾸면 게임이 없는 파일처럼 건너뛰고 오류 목록에 적는다.
        internal static int BrokenImages;
        // 게임은 실패한 이미지를 기억하지 않아, 그 이미지를 쓰는 장식마다 파일을 다시 읽고 유니티로 다시 풀다 실패한다
        // (ALPHA Arche: 깨진 3장을 70번, 메인 스레드 7.2초). 이번 불러오기에서 깨졌다고 확인된 파일은 다시 읽지 않고 바로 실패시킨다.
        // 게임이 보는 결과(null 텍스처 -> 위에서 "오류" -> 없는 파일처럼 건너뜀)는 같다.
        private static readonly HashSet<string> brokenThisLoad = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly byte[] BrokenMarker = new byte[1];
        private static int brokenSkipped;
        private static readonly HashSet<string> brokenSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public static void BrokenImagePostfix(Texture2D __result, ref ADOFAI.LoadResult status, string filePath)
        {
            if (running && loadT0 != 0) { loadTicks += Stopwatch.GetTimestamp() - loadT0; loadT0 = 0; }
            if ((object)__result != null || status != ADOFAI.LoadResult.Successful) return;
            status = ADOFAI.LoadResult.Error;
            if (running && filePath != null) brokenThisLoad.Add(filePath);
            if (filePath != null) brokenFiles.Add(FullPath(filePath));
            if (filePath != null && brokenSeen.Add(filePath) && BrokenImages++ < 20) Main.Entry.Logger.Log("[이미지] 풀 수 없는 이미지(파일이 깨짐), 게임이 건너뛰게 함: " + filePath);
        }

        // 깨졌다고 확인한 파일 (전체 경로, 이번 실행 동안)
        private static readonly HashSet<string> brokenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private const string BrokenKey = "SFBroken";
        private static bool showingImageResult;

        private static string FullPath(string p) { try { return Path.GetFullPath(p); } catch { return p; } }
        internal static bool IsBroken(string path) { return brokenFiles.Contains(FullPath(path)); }

        public static void MarkBrokenResult(scnEditor __instance, string name, ADOFAI.LoadResult loadResult)
        {
            if (loadResult != ADOFAI.LoadResult.Error || name == null || brokenFiles.Count == 0) return;
            try
            {
                string dir = Path.GetDirectoryName(ADOBase.levelPath ?? "");
                if (!brokenFiles.Contains(FullPath(Path.Combine(dir ?? "", name)))) return;
                var errors = errorsField.GetValue(__instance) as Dictionary<string, string>;
                string v;
                if (errors != null && errors.TryGetValue(name, out v) && v == "Error") errors[name] = BrokenKey;
            }
            catch { }
        }

        public static void ShowPrefix() { showingImageResult = true; }
        public static Exception ShowFinalizer(Exception __exception) { showingImageResult = false; return __exception; }

        public static bool RdStringPrefix(string __0, ref string __result)
        {
            if (!showingImageResult || __0 == null || !__0.EndsWith(BrokenKey, StringComparison.Ordinal)) return true;
            __result = SettingsWindow.T("깨진 파일", "Corrupted file");
            return false;
        }

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

        // ── 첫 판부터 VRAM 부족 막기 ───────────────────────────────────
        // 예전에 "이미지 전체 크기로 어림해 그 안에 맞을 때까지 줄이기" 를 했다가 뺐다(VramGuard 설명: CICADA3302 는 13GB 인데도
        // 원본으로 끊김 없이 돌았고, 어림은 1024 까지 줄여 화질만 버렸다). 그래픽카드는 그 순간 쓰는 이미지만 올려 두기 때문이다.
        // 그래서 여기서는 끝까지 맞추지 않는다. 이미지 전체(RGBA 기준)가 지금 비어 있는 VRAM 의 1.25배를 넘으면 첫 단계(긴 변 3072)만
        // 쓴다 - 3072 보다 큰 이미지만 줄어든다. 더 내리는 것은 지금처럼 실제로 끊겼을 때만(VramGuard) 한다.
        // Hello (BPM) 2026: 원본 약 10.9GB, 첫 판에 VRAM 이 가득 차 130ms 씩 멈췄고, 3072 에서는 그 끊김이 없었다(이미지 84장 줄어듦).
        // 크기는 파일 머리(PNG IHDR, JPG SOF)만 읽는다.
        internal const int PredictSide = 3072;
        internal static float PredictRatio = 1.25f;
        // 3072 로 줄여도 이미지 전체가 비어 있는 VRAM 의 몇 배를 넘으면 한 단계 더 (2048, 1536, 1024 까지).
        // 2026-10-04 ALPHA WYSI EX(이미지 1520장, 원본 약 100GB): 3072 에서 전체가 비어 있는 VRAM 의 7.7배 -> 190~193초에 그리기·GPU 대기로
        // 31~42ms 프레임이 이어졌고, 2048(3.6배)에서는 같은 구간이 17ms 로 끊김 0 (sf_ab 2쌍: FPS 214 -> 281, 곡 중 최악 42 -> 17ms).
        // 원본으로 잘 돌던 CICADA3302 는 2.2배였다. 그래서 5배를 넘을 때만 더 내린다.
        internal static float PredictDeepRatio = 5f;
        private static readonly int[] DeepSides = { 2048, 1536, 1024 };
        private static int Predict(scnGame g, string dir, out string why)
        {
            why = "";
            try
            {
                long totalMB = SystemInfo.graphicsMemorySize;
                if (totalMB <= 0) return 0;
                float used = SystemMonitor.VramUsedMB;
                // 내장 그래픽은 그림을 시스템 RAM 에 둔다. 보고되는 크기는 RAM 의 절반 넘게 잡힌 공유 한도(학교 PC: RAM 15.7GB 에 8,978MB)이고
                // 전용 사용량은 0 으로 읽혀, 이미지 원본 4.9GB 맵을 "비어 있는 VRAM 6.6GB" 로 보고 원본 그대로 올렸다(게임 공유 3.7GB).
                // 게임 힙·윈도우와 같은 RAM 을 나누므로 RAM 의 35% 를 그림 몫으로 보고, 사용량은 게임의 공유 메모리로 읽는다.
                if (PcTune.Integrated)
                {
                    long ramShare = SystemInfo.systemMemorySize * 35L / 100;
                    if (ramShare > 0 && ramShare < totalMB) totalMB = ramShare;
                    used = SystemMonitor.SharedGameMB;
                }
                if (used <= 0) used = 1500f;   // 아직 못 읽었으면 게임 화면·메뉴 몫으로 어림
                double freeMB = totalMB * 0.9 - used;
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                long bytes = 0; int big = 0, count = 0;
                var dims = new List<long>();   // 이미지마다 (가로 << 32 | 세로)
                Action<ADOFAI.LevelEvent> look = ev =>
                {
                    if (ev == null || !ev.ContainsKey("decorationImage")) return;
                    var img = ev["decorationImage"] as string;
                    if (string.IsNullOrEmpty(img) || img.StartsWith("prefab:", StringComparison.OrdinalIgnoreCase)) return;
                    string p = Path.Combine(dir, img);
                    if (!seenPaths.Add(p)) return;
                    int w, h;
                    if (!ReadDims(p, out w, out h)) return;
                    bytes += (long)w * h * 4; count++; dims.Add(((long)w << 32) | (uint)h);
                    if (Math.Max(w, h) > PredictSide) big++;
                };
                foreach (var ev in g.decorations) if (!PlayTweaks.SkipDecoAtLoad(ev)) look(ev);   // 노이펙 모드로 안 만들 장식은 빼고
                if (!PlayTweaks.SkipMoveImagesAtLoad) foreach (var ev in g.events) if ((int)ev.eventType == MoveDecoType) look(ev);
                double needMB = bytes / 1048576.0;
                string info = string.Format("이미지 {0}장 원본 약 {1:F0}MB, 비어 있는 VRAM 약 {2:F0}MB{4}, 3072 보다 큰 이미지 {3}장", count, needMB, freeMB, big, PcTune.Integrated ? " (내장 그래픽: RAM 의 35% 기준)" : "");
                if (needMB > Math.Max(0, freeMB) * PredictRatio && big > 0)
                {
                    int side = PredictSide;
                    double atMB = SizeAt(dims, side);
                    foreach (int next in DeepSides)
                    {
                        if (atMB <= Math.Max(0, freeMB) * PredictDeepRatio) break;
                        side = next; atMB = SizeAt(dims, side);
                    }
                    if (side != PredictSide) info += string.Format(", 3072 로도 약 {0:F0}MB 라 더 줄임 -> 긴 변 {1} 에서 약 {2:F0}MB", SizeAt(dims, PredictSide), side, atMB);
                    why = info; return side;
                }
                why = info;
                return 0;
            }
            catch (Exception ex) { why = "어림 실패: " + ex.Message; return 0; }
        }

        // 긴 변을 side 로 줄였을 때 이미지 전체 크기(RGBA, MB)
        private static double SizeAt(List<long> dims, int side)
        {
            double b = 0;
            foreach (long d in dims)
            {
                int w = (int)(d >> 32), h = (int)(d & 0xffffffff);
                double f = Math.Max(w, h) > side ? (double)side / Math.Max(w, h) : 1.0;
                b += Math.Max(1, (int)(w * f)) * (double)Math.Max(1, (int)(h * f)) * 4;
            }
            return b / 1048576.0;
        }

        // 이미지 가로·세로를 파일 머리에서 읽는다 (PNG: IHDR, JPG: SOFn 표시). 못 읽으면 false.
        private static bool ReadDims(string path, out int w, out int h)
        {
            w = h = 0;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096))
                {
                    var b = new byte[24];
                    if (fs.Read(b, 0, 24) < 24) return false;
                    if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
                    {
                        w = (b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19];
                        h = (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23];
                        return w > 0 && h > 0;
                    }
                    if (b[0] != 0xFF || b[1] != 0xD8) return false;
                    // JPG: 표시(FF xx)를 따라가며 SOF0~SOF15(DHT C4, JPG C8, DAC CC 제외)를 찾는다
                    fs.Position = 2;
                    var m = new byte[9];
                    for (int guard = 0; guard < 512; guard++)
                    {
                        int x = fs.ReadByte();
                        while (x == 0xFF) x = fs.ReadByte();   // 채움 바이트
                        if (x < 0) return false;
                        int marker = x;
                        if (marker == 0xD8 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01) continue;   // 길이 없는 표시
                        if (fs.Read(m, 0, 2) < 2) return false;
                        int len = (m[0] << 8) | m[1];
                        if (len < 2) return false;
                        if (marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
                        {
                            if (fs.Read(m, 0, 5) < 5) return false;
                            h = (m[1] << 8) | m[2]; w = (m[3] << 8) | m[4];
                            return w > 0 && h > 0;
                        }
                        fs.Position += len - 2;
                        // 표시 사이에 FF 가 오도록: 다음 바이트가 FF 가 아니면 잘못된 파일
                        int nx = fs.ReadByte();
                        if (nx != 0xFF) return false;
                    }
                    return false;
                }
            }
            catch { return false; }
        }

        // ── 1) 불러올 순서 뽑기 ─────────────────────────────────────────
        // 게임의 장식 불러오기(UpdateDecorationObjects) 안에서 null 예외가 나면 맵 열기가 그 자리에서 멈춘다. 무엇이 비었는지 적는다.
        private static void WhyNull(scnGame g)
        {
            var sb = new System.Text.StringBuilder("[장식 불러오기] null 예외: ");
            sb.Append("controller ").Append(ADOBase.controller == null ? "없음" : "있음");
            sb.Append(", decManager ").Append(g.decManager == null ? "없음" : "있음");
            sb.Append(", imgHolder ").Append(g.imgHolder == null ? "없음" : "있음");
            sb.Append(", levelData ").Append(g.levelData == null ? "없음" : "있음");
            sb.Append(", levelPath ").Append(g.levelPath == null ? "없음" : "있음");
            if (g.levelData != null)
            {
                var d = g.levelData.decorations; var e = g.levelData.levelEvents;
                int dn = 0, en = 0, img = 0; string firstBad = null;
                if (d != null) for (int i = 0; i < d.Count; i++)
                    {
                        if (d[i] == null) { dn++; if (firstBad == null) firstBad = "장식 " + i; continue; }
                        try { bool a = d[i].active; string o; d[i].TryGet<string>("decorationImage", out o); }
                        catch (Exception ex) { dn++; if (firstBad == null) firstBad = "장식 " + i + ": " + ex.GetType().Name; }
                    }
                if (e != null) for (int i = 0; i < e.Count; i++)
                    {
                        if (e[i] == null) { en++; if (firstBad == null) firstBad = "이벤트 " + i; continue; }
                        if (e[i].eventType == ADOFAI.LevelEventType.MoveDecorations)
                        {
                            try { string o = null; if (e[i].TryGetAndSet("decorationImage", ref o, true) && !string.IsNullOrEmpty(o)) Path.Combine(Path.GetDirectoryName(g.levelPath), o); }
                            catch (Exception ex) { img++; if (firstBad == null) firstBad = "장식 이동 " + i + " (타일 " + e[i].floor + "): " + ex.GetType().Name; }
                        }
                    }
                sb.Append(", 장식 ").Append(d == null ? "목록 없음" : d.Count + "개 중 null " + dn);
                sb.Append(", 이벤트 ").Append(e == null ? "목록 없음" : e.Count + "개 중 null " + en);
                sb.Append(", 읽다 예외 난 장식 이동 ").Append(img);
                if (firstBad != null) sb.Append(", 처음: ").Append(firstBad);
            }
            Main.Entry.Logger.Log(sb.ToString());
        }

        public static void Begin(scnGame __instance)
        {
            if (!Enabled || swapped != 2) return;
            try
            {
                Stop();
                string dir = Path.GetDirectoryName(__instance.levelPath);
                UploadBudget.Begin();
                sideNow = MaxSide > 0 ? MaxSide : MaxSide == Auto ? VramGuard.CapFor(__instance.levelPath) : 0;
                if (MaxSide == Auto)
                {
                    AutoNote = sideNow > 0 ? "자동: 전에 VRAM 이 모자라 끊긴 맵이라 긴 변 " + sideNow + " 으로 줄임" : "자동: 원본 그대로 (이 맵에서 VRAM 부족 끊김 기록 없음)";
                    // 같은 맵(에디터 편집·되돌리기 때도 여기로 온다)은 이번 실행에서 이미 정한 한도를 그대로 쓴다.
                    // 다시 어림하지 않아야 파일 수백 개를 매번 열지 않고, 한도가 바뀌어 이미지를 다시 불러오는 일도 없다.
                    bool sameLevel = string.Equals(__instance.levelPath, VramGuard.Level, StringComparison.OrdinalIgnoreCase);
                    if (sideNow == 0 && sameLevel && VramGuard.CurrentCap > 0) { sideNow = VramGuard.CurrentCap; AutoNote = "자동: 이번 실행에서 정한 긴 변 " + sideNow + " 그대로"; }
                    else if (sideNow == 0 && !sameLevel)
                    {
                        string why;
                        int pre = Predict(__instance, dir, out why);
                        if (pre > 0) { sideNow = pre; AutoNote = "자동: 첫 판부터 긴 변 " + pre + " 으로 줄임 (" + why + ")"; }
                        else if (why.Length > 0) AutoNote += " (" + why + ")";
                    }
                    Main.Entry.Logger.Log("[이미지] " + AutoNote);
                }
                if (Resilience.SafeMode && sideNow <= 0) { Main.Entry.Logger.Log("[이미지] 안전 모드: 줄일 필요가 없는 맵이라 원래 방식으로 불러옴"); return; }
                var list = new List<Item>();
                var seen = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
                var cached = spritesField != null ? spritesField.GetValue(__instance.imgHolder) as System.Collections.IDictionary : null;
                if (cached != null && !string.Equals(__instance.levelPath, VramGuard.Level, StringComparison.OrdinalIgnoreCase))
                    FreePreviousLevel(__instance.imgHolder, cached, dir);

                // 같은 맵을 다시 여는데 이미 올라온 이미지가 지금 한도와 다른 크기면(자동이 이번부터 줄이기로 했거나 한도를 바꿈)
                // 그 이미지를 내려서 새 한도로 다시 불러오게 한다. 예전에는 이미 올라온 이미지를 그대로 써서, 게임을 다시 켜기
                // 전까지는 "줄임" 이라고 적고도 실제로는 원본이었다. 게임도 파일이 바뀌면 같은 방법(UnloadSprite)으로 다시 부른다.
                bool reload = cached != null && string.Equals(__instance.levelPath, VramGuard.Level, StringComparison.OrdinalIgnoreCase) && sideNow != VramGuard.CurrentCap;
                var unload = reload ? AccessTools.Method(typeof(TextureManager), "UnloadSprite") : null;
                int unloaded = 0;

                Action<ADOFAI.LevelEvent> add = ev =>
                {
                    if (ev == null || !ev.ContainsKey("decorationImage")) return;
                    var img = ev["decorationImage"] as string;
                    if (string.IsNullOrEmpty(img) || img.StartsWith("prefab:", StringComparison.OrdinalIgnoreCase)) return;
                    if (cached != null && cached.Contains(img))   // 이미 불러온 이미지는 게임이 다시 풀지 않는다
                    {
                        if (unload == null) return;
                        try { unload.Invoke(__instance.imgHolder, new object[] { img }); unloaded++; } catch { return; }
                    }
                    string path = Path.Combine(dir, img);
                    if (seen.ContainsKey(path)) return;
                    // PNG 와 JPG 만 (어느 쪽으로 풀지는 파일 머리로 정한다. 이름만 .jpg 인 PNG 도 있다)
                    bool jpgName = path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase);
                    if (!path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && !(jpgName && TurboJpeg.Available)) return;
                    var it = new Item { Path = path, Index = list.Count };
                    seen[path] = it;
                    list.Add(it);
                };
                foreach (var ev in __instance.decorations) if (!PlayTweaks.SkipDecoAtLoad(ev)) add(ev);
                if (!PlayTweaks.SkipMoveImagesAtLoad) foreach (var ev in __instance.events) if ((int)ev.eventType == MoveDecoType) add(ev);

                Main.Entry.Logger.Log(string.Format("[이미지] 맵 열기 전: 이미 올라와 있는 이미지 {0}장, VRAM 전체 {1:F0}MB / 게임 {2:F0}MB", cached != null ? cached.Count : -1, SystemMonitor.VramUsedMB, SystemMonitor.VramGameMB));
                if (unloaded > 0) Main.Entry.Logger.Log("[이미지] 한도가 바뀌어 이미 올라온 이미지 " + unloaded + "장을 다시 불러옴 (긴 변 " + (sideNow > 0 ? sideNow.ToString() : "원본") + ")");
                // 이미 올라온 이미지를 다시 쓰는 경우(같은 맵 다시 열기)에는 그때의 한도가 그대로 남는다
                VramGuard.OnLevelLoaded(__instance.levelPath, sideNow, list.Count >= 8);
                if (list.Count < 8) return;   // 몇 장 안 되면 그냥 원래대로

                lock (gate)
                {
                    items = list; byPath = seen; next = 0; pendingBytes = 0; running = true; consumed = -1;
                    used = fallback = notReady = 0; waitMs = 0; shrunkCount = 0; savedBytes = 0;
                    brokenThisLoad.Clear(); brokenSkipped = 0;
                    loadT0 = loadTicks = createTicks = 0; createDepth = createCount = 0;
                }
                startTicks = Stopwatch.GetTimestamp();
                putMs = fallbackMs = 0; fallbackNotes = 0; lateCompress = 0; compressWaitMs = 0;
                gcAtStart = GC.CollectionCount(0);
                // 로딩 동안 GC를 꺼 둔다. 지난번 로딩 중 GC가 13번 돌았다. 곡 중 GC 멈춤이 이미 끈 상태면 건드리지 않는다.
                if (!GcControl.Paused && UnityEngine.Scripting.GarbageCollector.GCMode == UnityEngine.Scripting.GarbageCollector.Mode.Enabled)
                {
                    try { UnityEngine.Scripting.GarbageCollector.GCMode = UnityEngine.Scripting.GarbageCollector.Mode.Disabled; gcWasOn = true; }
                    catch { }
                }

                Compat.Refresh();   // PACL2 손실 압축이 켜져 있는지 (작업 스레드가 미리 압축할지 정한다)
                Resilience.Phase("맵 이미지 불러오는 중");
                // 코어 - 1 (최대 8). 메인 스레드가 대부분 기다리는 맵(Hello (BPM) 2026: 전체 11초 중 기다림 5초)은 작업 스레드가 많을수록
                // 좋지만, 장식이 많거나 미리 압축하는 맵은 메인 스레드도 바쁘다(Arche: 기다림 0.3~0.4초). 6코어 6스레드(i5-9400F)에서
                // 코어 수(6개)로 늘리자 메인 스레드의 넣기가 1.9초 -> 3.6~3.9초, 전체 17.5 -> 18.7~19.6초로 느려졌다(낮은 우선순위여도
                // 유니티의 렌더·잡 스레드와 코어를 나눠 쓴다). 그래서 코어 하나는 비워 두고, 스레드가 많은 CPU 는 최대 8개까지 쓴다.
                int n = Math.Max(1, Math.Min(8, Environment.ProcessorCount - 1));
                PngDecoder.ResetStats(); SfNative.ResetStats(); TurboJpeg.ResetStats(); jpgSeen = 0;
                PngDecoder.DownscaleVerifyAll = Edition.Dev && File.Exists(Path.Combine(Main.Entry.Path, "downscale-verify.txt"));   // 개발자용: 줄인 이미지 전부를 C# 과 비교
                maxTexture = SystemInfo.maxTextureSize;   // 유니티는 이보다 큰 이미지를 못 올린다(JPG 는 원래 방식으로 넘겨 같은 오류가 나게 한다)
                workers = new Thread[n];
                for (int i = 0; i < n; i++)
                {
                    workers[i] = new Thread(Work) { IsBackground = true, Name = "StutterFix.Image" + i, Priority = System.Threading.ThreadPriority.BelowNormal };
                    workers[i].Start();
                }
                Main.Entry.Logger.Log("[이미지] " + list.Count + "장 미리 풀기 시작 (작업 스레드 " + n + "개" + (TexCompress.Planned ? ", DXT 로 미리 압축: " + (Compat.Pacl2Lossy ? "PACL2 손실 압축" : "저사양 옵션") : "") + ")");
            }
            catch (Exception ex) { Main.Entry.Logger.Error("[이미지] 시작 실패: " + ex.Message); Stop(); }
        }

        // ── 2) 작업 스레드 ─────────────────────────────────────────────
        // 압축은 이미지 하나를 블록 줄 묶음(ChunkRows)으로 나눠 여러 작업 스레드가 함께 한다. 큰 이미지(7680x4320)를 스레드 하나가 하면
        // 2.5초가 걸려 그 사이 게임이 가져가 버렸다(2026-09-26: 29장이 늦음). 게임이 곧 가져갈 이미지(앞쪽)부터 압축한다.
        // 풀어 둔 것이 2장 이상 기다리고 있으면 압축을, 아니면 다음 이미지 풀기를 먼저 한다(풀기가 밀리면 게임이 풀기를 기다린다).
        private const int ChunkRows = 16;

        // gate 안에서: 가장 앞의 압축할 거리
        private static Item NextCompressJob(out int ahead)
        {
            ahead = 0; Item job = null;
            for (int i = Math.Max(0, consumed); i < next && i < items.Count; i++)
            {
                var c = items[i];
                if (c.State != 2) continue;
                ahead++;
                if (job == null && c.Blocks != IntPtr.Zero && !c.Abandoned && c.NextRow < c.Rows) job = c;
            }
            return job;
        }

        private static void Work()
        {
            try { WorkLoop(); }
            finally { NativeInflate.FreeThread(); TurboJpeg.FreeThread(); }   // 스레드마다 둔 libdeflate·libjpeg-turbo 해독기와 풀 자리
        }

        private static void WorkLoop()
        {
            while (true)
            {
                Item it = null, job = null; int r0 = 0, r1 = 0; IntPtr jpx = IntPtr.Zero, jbl = IntPtr.Zero;   // 픽셀·결과 주소는 gate 안에서 받아 둔다
                lock (gate)
                {
                    while (true)
                    {
                        if (!running) return;
                        int ahead;
                        var cand = NextCompressJob(out ahead);
                        // 풀어 둔 양이 많으면 풀기는 멈추고(메인 스레드가 가져갈 때까지) 압축만 한다
                        bool canDecode = next < items.Count && pendingBytes <= (long)MaxPendingMB * 1048576;
                        if (cand != null && (cand.Urgent || ahead >= 2 || !canDecode))
                        {
                            job = cand; jpx = cand.Pixels; jbl = cand.Blocks; r0 = cand.NextRow; r1 = Math.Min(cand.Rows, r0 + ChunkRows); cand.NextRow = r1; cand.InFlight++;
                            break;
                        }
                        if (canDecode)
                        {
                            it = items[next++];
                            if (it.State != 0) { it = null; continue; }
                            it.State = 1;
                            break;
                        }
                        if (next >= items.Count && cand == null)
                        {
                            // 남은 풀기가 없고 압축 거리도 없다: 다른 스레드가 아직 푸는 중이면 그 압축을 도우러 기다리고, 아니면 끝
                            bool decoding = false;
                            for (int i = Math.Max(0, consumed); i < items.Count; i++) if (items[i].State == 1) { decoding = true; break; }
                            if (!decoding) return;
                        }
                        Monitor.Wait(gate, 100);
                    }
                }

                if (job != null)
                {
                    long t0 = Stopwatch.GetTimestamp();
                    try { unsafe { SfNative.EncodeRows((byte*)jpx, job.Width, job.Height, job.Layout, job.Dxt5, (byte*)jbl, r0, r1); } }
                    catch { lock (gate) job.Abandoned = true; }
                    Interlocked.Add(ref TexCompress.EncodeTicks, Stopwatch.GetTimestamp() - t0);
                    byte[] cachedCopy=null;
                    var cache=AssetDiskCache.Store;
                    bool cacheComplete=false;
                    if(cache!=null&&job.DiskKey!=null&&!job.Extra&&!job.Jpg)
                    {
                        lock(gate)cacheComplete=!job.Abandoned&&job.DoneRows+r1-r0>=job.Rows;
                        if(cacheComplete)try
                        {
                            if(job.BlockSize<=64L*1024*1024)
                            {cachedCopy=new byte[(int)job.BlockSize];Marshal.Copy(jbl,cachedCopy,0,cachedCopy.Length);}
                        }catch{cachedCopy=null;}
                    }
                    lock (gate)
                    {
                        job.InFlight--;
                        job.DoneRows += r1 - r0;
                        if (job.DoneRows >= job.Rows && !job.Abandoned) job.BlocksReady = true;
                        ReleaseIfDone(job);
                        Monitor.PulseAll(gate);
                    }
                    // No gate or native pointer is held during disk I/O.
                    if(cachedCopy!=null)try
                    {
                        if(job.DiskKey==AssetDiskCache.ImageKey(job.Path,sideNow))
                            if(cache.Write(job.DiskKey,AssetDiskCache.PackDxt(job.Width,job.Height,job.Format,job.Factor,job.Dxt5,cachedCopy)))Interlocked.Increment(ref AssetDiskCache.DxtWrites);
                    }catch{}
                    continue;
                }

                var readCache=AssetDiskCache.Store;
                if(readCache!=null&&TexCompress.Planned)
                {
                    try
                    {
                        it.DiskKey=AssetDiskCache.ImageKey(it.Path,sideNow);
                        byte[] blob,cachedData;int cw,ch,cf,cachedOffset;float factor0;bool dxt5;
                        if(readCache.TryRead(it.DiskKey,out blob)&&AssetDiskCache.UnpackDxt(blob,out cw,out ch,out cf,out factor0,out dxt5,out cachedData,out cachedOffset)
                            &&it.DiskKey==AssetDiskCache.ImageKey(it.Path,sideNow))
                        {
                            int cachedLength=cachedData.Length-cachedOffset;IntPtr pointer=Marshal.AllocHGlobal(cachedLength);
                            try{Marshal.Copy(cachedData,cachedOffset,pointer,cachedLength);}catch{Marshal.FreeHGlobal(pointer);throw;}
                            lock(gate)
                            {
                                if(!running||it.State!=1||it.Index<consumed)Marshal.FreeHGlobal(pointer);
                                else
                                {
                                    it.Width=cw;it.Height=ch;it.Format=cf;it.Factor=factor0;it.Dxt5=dxt5;
                                    it.Blocks=pointer;it.BlockSize=cachedLength;it.BlocksReady=true;it.CachedBlocks=true;
                                    it.State=2;pendingBytes+=cachedLength;Interlocked.Increment(ref AssetDiskCache.DxtHits);
                                }
                                Monitor.PulseAll(gate);
                            }
                            continue;
                        }
                    }catch{it.DiskKey=null;}
                }
                int w = 0, h = 0, f = 0, ow = 0, oh = 0; IntPtr px = IntPtr.Zero; long size = 0; bool ok = false, extra = false, jpg = false; float factor = 1f;
                try
                {
                    int len = ReadInto(it.Path, ref fileBuf);
                    if (TurboJpeg.IsJpg(fileBuf, len))
                    {
                        jpg = true; f = PngDecoder.FormatRGB24;   // 유니티도 JPG 는 RGB24 로 만든다
                        ok = TurboJpeg.TryDecode(fileBuf, len, maxTexture, out w, out h, out px, out size);
                    }
                    else ok = len > 0 && PngDecoder.TryDecode(fileBuf, len, ExtraFormats, sideNow, out w, out h, out f, out px, out size, out extra, out factor, out ow, out oh);
                    long before = (jpg ? (long)w * h : (long)ow * oh) * 4;   // GPU 에는 한 픽셀 4바이트로 올라간다 (PNG 는 해독하며 줄였을 수 있어 원래 크기로)
                    if (ok && factor < 1f)   // 해독하며 줄임
                    {
                        Interlocked.Increment(ref shrunkCount);
                        Interlocked.Add(ref savedBytes, before - (long)w * h * 4);
                    }
                    else if (ok && sideNow > 0 && PngDecoder.Downscale(ref px, ref w, ref h, f, ref size, sideNow, out factor))
                    {
                        Interlocked.Increment(ref shrunkCount);
                        Interlocked.Add(ref savedBytes, before - (long)w * h * 4);
                    }
                }
                catch { ok = false; }

                // 압축할 예정이면(PACL2 손실 압축, 저사양 옵션) 압축 결과 자리를 잡고 줄 묶음 압축 거리로 내놓는다(푼 것은 바로 넘긴다).
                // 유니티 압축처럼 알파 있는 형식은 DXT5, RGB24 는 DXT1. 크기가 4의 배수가 아니면 PACL2 도 압축하지 않는다(IL 확인).
                bool compress = ok && TexCompress.Planned && w % 4 == 0 && h % 4 == 0 && (f == PngDecoder.FormatRGBA32 || f == PngDecoder.FormatARGB32 || f == PngDecoder.FormatRGB24);
                bool d5 = f != PngDecoder.FormatRGB24;
                long bsize = compress ? DxtEncoder.BlocksSize(w, h, d5) : 0;
                IntPtr blocks = IntPtr.Zero;
                if (compress) { try { blocks = Marshal.AllocHGlobal((IntPtr)bsize); } catch { blocks = IntPtr.Zero; bsize = 0; } }
                ulong[] cells = null;
                ShapeCellsCpu.OpaqueInfo opaque = null;
                if (ok) { try { cells = ShapeCellsCpu.Compute(px, w, h, f, out opaque); } catch { cells = null; opaque = null; } }
                lock (gate)
                {
                    if (!running || it.State != 1 || it.Index < consumed)   // 게임이 이미 지나간 것
                    {
                        if (px != IntPtr.Zero) Marshal.FreeHGlobal(px);
                        if (blocks != IntPtr.Zero) Marshal.FreeHGlobal(blocks);
                    }
                    else if (ok)
                    {
                        it.Width = w; it.Height = h; it.Format = f; it.Pixels = px; it.Size = size; it.Factor = factor; it.Extra = extra; it.Jpg = jpg; it.Cells = cells;
                        it.Opaque = opaque;
                        if (blocks != IntPtr.Zero)
                        {
                            it.Blocks = blocks; it.BlockSize = bsize; it.Dxt5 = d5; it.Layout = f == PngDecoder.FormatARGB32 ? 1 : f == PngDecoder.FormatRGB24 ? 2 : 0;
                            it.Rows = h / 4; it.NextRow = 0; it.DoneRows = 0;
                        }
                        it.State = 2;
                        pendingBytes += size + bsize;
                    }
                    else { it.State = 3; if (blocks != IntPtr.Zero) Marshal.FreeHGlobal(blocks); }
                    Monitor.PulseAll(gate);
                }
            }
        }
        internal static int lateCompress;      // 압축이 절반도 안 됐을 때 게임이 가져간 이미지 수 (그 이미지는 원래대로 유니티가 압축)
        internal static double compressWaitMs; // 절반 넘게 된 압축을 게임이 기다린 시간

        // gate 안에서: 압축 거리에서 빠진(게임이 가져갔거나 건너뜀) 이미지는 마지막 줄 묶음이 끝났을 때 결과·픽셀을 푼다
        private static void ReleaseIfDone(Item c)
        {
            if (c.InFlight > 0) return;
            if (c.Abandoned && c.Blocks != IntPtr.Zero && !c.BlocksReady) { Marshal.FreeHGlobal(c.Blocks); c.Blocks = IntPtr.Zero; }
            if (c.PixelsOrphan) { Marshal.FreeHGlobal(c.Pixels); c.Pixels = IntPtr.Zero; c.PixelsOrphan = false; }
            if (c.BlocksOrphan) { if (c.Blocks != IntPtr.Zero) Marshal.FreeHGlobal(c.Blocks); c.Blocks = IntPtr.Zero; c.BlocksOrphan = false; }
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
            WindowGhost.Tick();
            if (running && path != null && brokenThisLoad.Contains(path)) { brokenSkipped++; loadResult = (ADOFAI.LoadResult)0; return BrokenMarker; }
            Item it = null;
            string why = null;
            if (running)
            {
                long t0 = Stopwatch.GetTimestamp();
                lock (gate)
                {
                    if (byPath.TryGetValue(path, out it))
                    {
                        // 아직 시작 안 한 것은 기다리지 않는다(순서가 어긋났다는 뜻). 푸는 중이면 끝날 때까지 기다린다.
                        if (it.State == 0) { it.State = 3; notReady++; it = null; why = "순서 어긋남"; }
                        else
                        {
                            while (it.State == 1 && running) { Monitor.Wait(gate, 100); WindowGhost.Tick(); }
                            // 압축이 절반 넘게 됐으면 모든 작업 스레드가 이것부터 마저 하도록 하고 기다린다. 절반도 안 됐으면 그만두고 원래대로(유니티 압축).
                            if (it.State == 2 && it.Blocks != IntPtr.Zero && !it.BlocksReady && !it.Abandoned)
                            {
                                if (it.DoneRows * 2 >= it.Rows)
                                {
                                    long w0 = Stopwatch.GetTimestamp();
                                    it.Urgent = true; Monitor.PulseAll(gate);
                                    while (running && !it.BlocksReady && !it.Abandoned) { Monitor.Wait(gate, 20); WindowGhost.Tick(); }
                                    compressWaitMs += (Stopwatch.GetTimestamp() - w0) * 1000.0 / Stopwatch.Frequency;
                                }
                                if (!it.BlocksReady) { it.Abandoned = true; lateCompress++; }
                            }
                            if (it.State == 2) { it.State = 4; pendingBytes -= it.Size + it.BlockSize; DropSkipped(it.Index); Monitor.PulseAll(gate); }
                            else { it = null; why = "해독 못 함(지원하지 않는 형식이거나 오류)"; }
                        }
                    }
                    else why = "미리 풀기 목록에 없음";
                }
                waitMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            }

            if (it == null)
            {
                if (running && Edition.Dev && fallbackNotes < 20)
                {
                    fallbackNotes++;
                    long len = -1; try { len = new FileInfo(path).Length; } catch { }
                    Main.Entry.Logger.Log(string.Format("[이미지] 원래 방식으로: {0} ({1}, {2}KB)", Path.GetFileName(path), why, len / 1024));
                }
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
                if (s.State == 2)
                {
                    pendingBytes -= s.Size + s.BlockSize; s.Abandoned = true;
                    if (s.InFlight > 0) { s.PixelsOrphan = true; if (s.Blocks != IntPtr.Zero) s.BlocksOrphan = true; }   // 압축 중인 묶음이 끝나면 작업 스레드가 푼다
                    else { Marshal.FreeHGlobal(s.Pixels); s.Pixels = IntPtr.Zero; if (s.Blocks != IntPtr.Zero) { Marshal.FreeHGlobal(s.Blocks); s.Blocks = IntPtr.Zero; } }
                }
                if (s.State == 0 || s.State == 2) s.State = 3;
            }
            if (k > consumed) consumed = k;
        }

        // 이번 LoadTexture 의 maxSideSize (-1 이 아니면 게임이 LoadImage 뒤에 ShrinkImage 로 픽셀을 다시 읽으므로 압축 결과를 바로 넣지 않는다)
        private static int loadMaxSide = -1;
        public static void LoadTexturePrefix(int maxSideSize) { loadMaxSide = maxSideSize; if (running) loadT0 = Stopwatch.GetTimestamp(); }

        // 맵 열기 나눠 보기: 장식 만들기(CreateDecoration) 전체와 그 안의 텍스처 불러오기(LoadTexture) 시간 (불러오기 중에만, 장식마다 두 번 시각 읽기)
        private static long loadT0, loadTicks, createT0, createTicks; private static int createDepth, createCount;
        public static void CreatePrefix() { if (running && createDepth++ == 0) createT0 = Stopwatch.GetTimestamp(); }
        public static Exception CreateFinalizer(Exception __exception)
        {
            if (running && createDepth > 0 && --createDepth == 0)
            {
                createTicks += Stopwatch.GetTimestamp() - createT0;
                createCount++;
            }
            return __exception;
        }

        public static bool LoadImage(Texture2D tex, byte[] data)
        {
            WindowGhost.Tick();
            Item it;
            long t0 = Stopwatch.GetTimestamp();
            if ((object)data == BrokenMarker) return false;   // 이번 불러오기에서 이미 깨졌다고 확인된 파일
            if (data == null || markers == null || !markers.TryGetValue(data, out it))
            {
                bool r = ImageConversion.LoadImage(tex, data);
                if (running) fallbackMs += (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                return r;
            }
            markers.Remove(data);
            try
            {
                if (it.Extra && Edition.Dev && !ExtraMatches(it))
                {
                    // 추가 형식이 유니티 결과와 다르면 원래 방식으로 (개발자용 확인 중)
                    fallback++;
                    return ImageConversion.LoadImage(tex, File.ReadAllBytes(it.Path));
                }
                if (it.Jpg && Edition.Dev && it.Factor >= 1f && (jpgSeen++ < 16 || jpgSeen % 32 == 0) && !JpgMatches(it))
                {
                    fallback++;
                    return ImageConversion.LoadImage(tex, File.ReadAllBytes(it.Path));
                }
                if(it.CachedBlocks&&(loadMaxSide!=-1||!TexCompress.Planned))
                {
                    fallback++;return ImageConversion.LoadImage(tex,File.ReadAllBytes(it.Path));
                }
                if (it.Blocks != IntPtr.Zero && it.BlocksReady && loadMaxSide == -1 && (it.CachedBlocks||TexCompress.DirectAllowed))
                {
                    // 곧 압축될 것이 확인된 맵: 원본을 넣지 않고 압축 결과를 바로 (TexCompress.DirectAllowed)
                    tex.Reinitialize(it.Width, it.Height, it.Dxt5 ? TextureFormat.DXT5 : TextureFormat.DXT1, false);
                    tex.LoadRawTextureData(it.Blocks, (int)it.BlockSize);
                    TexCompress.MarkDirect(tex);
                }
                else
                {
                    tex.Reinitialize(it.Width, it.Height, (TextureFormat)it.Format, false);
                    tex.LoadRawTextureData(it.Pixels, (int)it.Size);
                    // 미리 압축한 것은 압축 순간(PACL2 의 Compress, 또는 저사양 옵션의 Apply)에 넣도록 맡긴다
                    if (it.Blocks != IntPtr.Zero && it.BlocksReady) { TexCompress.Register(tex, it.Blocks, it.BlockSize, it.Dxt5, it.Width, it.Height, (TextureFormat)it.Format); it.Blocks = IntPtr.Zero; }
                }
                if (it.Factor < 1f) shrunk[tex] = it.Factor;   // 스프라이트를 만들 때 크기 기준을 맞춘다
                ShapeCellsCpu.Register(tex, it.Cells);
                ShapeCellsCpu.RegisterOpaque(tex, it.Opaque);
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
                // 작업 스레드가 아직 이 픽셀로 압축 중인 묶음이 있으면 풀기를 그쪽에 맡긴다(마지막 묶음이 끝나면 작업 스레드가 푼다)
                lock (gate)
                {
                    if (it.InFlight > 0) { it.PixelsOrphan = true; if (it.Blocks != IntPtr.Zero) it.BlocksOrphan = true; }
                    else
                    {
                        Marshal.FreeHGlobal(it.Pixels); it.Pixels = IntPtr.Zero;
                        if (it.Blocks != IntPtr.Zero) { Marshal.FreeHGlobal(it.Blocks); it.Blocks = IntPtr.Zero; }
                    }
                }
            }
        }

        // ── 추가 형식(8비트 흑백·흑백+알파, 16비트 RGBA) ──
        // 유니티 LoadImage 가 이 형식들을 어떤 텍스처 형식·바이트로 만드는지와 똑같아야 쓸 수 있다. 개발자용에서만 풀고, 넣기 전에
        // 같은 파일을 유니티로 풀어 형식·크기·바이트 전체를 비교한다. 같으면 우리 것을 쓰고 다르면 유니티 결과를 쓴다(로그에 남김).
        // 모든 형식에서 같음이 확인되면 플레이어용에서도 켠다.
        // 2026-09-26 확인: 16비트 RGBA(2692x2833), 흑백+알파(2000x2000) 모두 유니티 결과와 형식·바이트 전부 같음 -> 모두에게 켠다(개발자용은 계속 비교)
        internal static bool ExtraFormats = true;
        internal static int ExtraSame, ExtraDiff;
        private static bool ExtraMatches(Item it)
        {
            string desc;
            string why = DiffFromUnity(it, out desc);
            if (why == null) ExtraSame++; else ExtraDiff++;
            Main.Entry.Logger.Log("[이미지] 추가 형식 확인 " + Path.GetFileName(it.Path) + " (" + desc + "): " + (why == null ? "유니티와 같음" : "다름 - " + why));
            return why == null;
        }

        // (개발자용) libjpeg-turbo 로 푼 JPG 를 유니티로도 풀어 비교. 맵마다 앞 16장과 그 뒤 32장마다 한 장. 다를 때만 로그.
        private static int jpgSeen;
        private static int maxTexture = 16384;
        private static bool JpgMatches(Item it)
        {
            string desc;
            string why = DiffFromUnity(it, out desc);
            Interlocked.Increment(ref TurboJpeg.DevChecks);
            if (why != null)
            {
                Interlocked.Increment(ref TurboJpeg.DevMismatch);
                Main.Entry.Logger.Log("[이미지] JPG 유니티와 다름 " + Path.GetFileName(it.Path) + " (" + desc + "): " + why + " -> 원래 방식으로");
            }
            return why == null;
        }

        // 같은 파일을 유니티 LoadImage 로 풀어 형식·크기·바이트 전체를 비교한다. 같으면 null, 다르면 까닭.
        private static unsafe string DiffFromUnity(Item it, out string desc)
        {
            Texture2D u = null;
            desc = "";
            try
            {
                u = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                ImageConversion.LoadImage(u, File.ReadAllBytes(it.Path));
                desc = u.format + " " + u.width + "x" + u.height;
                string why = null;
                if (u.format != (TextureFormat)it.Format) why = "형식 " + u.format + " / 우리 " + (TextureFormat)it.Format;
                else if (u.width != it.Width || u.height != it.Height) why = "크기 " + u.width + "x" + u.height + " / 우리 " + it.Width + "x" + it.Height;
                else
                {
                    var raw = u.GetRawTextureData<byte>();
                    if (raw.Length != it.Size) why = "바이트 수 " + raw.Length + " / 우리 " + it.Size;
                    else
                    {
                        byte* p = (byte*)it.Pixels;
                        for (int i = 0; i < raw.Length; i++) if (raw[i] != p[i]) { why = "바이트 " + i + " 번째부터 다름 (유니티 " + raw[i] + ", 우리 " + p[i] + ")"; break; }
                    }
                }
                return why;
            }
            catch (Exception ex) { return "확인 실패: " + ex.Message; }
            finally { if (u != null) UnityEngine.Object.Destroy(u); }
        }

        // ── 끝 ─────────────────────────────────────────────────────────
        public static Exception End(scnGame __instance, Exception __exception)
        {
            UploadBudget.Restore();
            if (__exception is NullReferenceException) { try { WhyNull(__instance); } catch (Exception ex) { Main.Entry.Logger.Log("[장식 불러오기] 원인 찾기 실패: " + ex.Message); } }
            if (running)
            {
                double total = (Stopwatch.GetTimestamp() - startTicks) * 1000.0 / Stopwatch.Frequency;
                LastSide = sideNow; LastShrunk = shrunkCount; LastSavedMB = Interlocked.Read(ref savedBytes) / 1048576f; AnyLoad = true;
                Last = string.Format("미리 푼 것 {0}장(넣기 {5:F0}ms), 원래 방식 {1}장({6:F0}ms, 순서 어긋남 {2}), 기다림 {3:F0}ms, GC {7}번, 전체 {4:F1}초" + (shrunkCount > 0 ? ", 줄인 이미지 " + shrunkCount + "장 (긴 변 " + sideNow + ", VRAM 약 " + LastSavedMB.ToString("F0") + "MB 아낌)" : ""),
                    used, fallback, notReady, waitMs, total / 1000.0, putMs, fallbackMs, GC.CollectionCount(0) - gcAtStart) + TexCompress.EndLoad() + (lateCompress > 0 ? ", 압축이 늦어 원래대로 " + lateCompress + "장" : "") + (brokenSkipped > 0 ? ", 깨진 이미지 다시 읽지 않음 " + brokenSkipped + "번" : "")
                    + string.Format(" | 장식 만들기 {0}개 {1:F0}ms, 그중 텍스처 불러오기 {2:F0}ms", createCount, createTicks * 1000.0 / Stopwatch.Frequency, loadTicks * 1000.0 / Stopwatch.Frequency) + (compressWaitMs > 0 ? string.Format(", 압축 마저 기다림 {0:F0}ms", compressWaitMs) : "");
                Main.Entry.Logger.Log("[이미지] " + Last);
                double tk = Stopwatch.Frequency / 1000.0;
                Main.Entry.Logger.Log(string.Format("[이미지] 해독 시간(작업 스레드 {0}개 합계): 압축 풀기 {1:F0}ms, 필터 되돌리기 {2:F0}ms, 줄이기 {7:F0}ms({8:F0}M 픽셀), 필터·줄이기 한 번에 {9}장 {10:F0}ms({11:F0}M 픽셀), 흘려 풀기·줄이기 {12}장 {13:F0}ms({14:F0}M 픽셀, 못 한 것 {15}) | 새로 맡은 형식(흑백·인터레이스) {3}장 | libdeflate {4}장, 원래 zlib 로 다시 푼 것 {5}장 ({6})",
                    workers.Length, PngDecoder.InflateTicks / tk, PngDecoder.FilterTicks / tk, PngDecoder.NewKinds, PngDecoder.NativeImages, PngDecoder.NativeFallbacks, NativeInflate.Status, PngDecoder.DownscaleTicks / tk, PngDecoder.DownscalePixels / 1e6, PngDecoder.FusedImages, PngDecoder.FusedTicks / tk, PngDecoder.FusedPixels / 1e6, PngDecoder.StreamImages, PngDecoder.StreamTicks / tk, PngDecoder.StreamPixels / 1e6, PngDecoder.StreamFallbacks) + SfNative.Summary() + TurboJpeg.Summary());
                Resilience.Phase("메뉴·편집");
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
                    GcControl.NoteClean();
                    Main.Entry.Logger.Log(string.Format("[이미지] 로딩 뒤 GC 한 번 {0:F0}ms",
                        (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency));
                }
                catch { }
            }
            // 장식을 다 만든 직후 몇 프레임은 장식들이 처음 움직이며(블렌드 재질 만들기 등) 60ms 쯤 걸린다.
            // 곡 시작 전 편집 화면에서 끊김으로 잡혔는데 맵 불러오기의 끝부분이므로 불러오기로 적는다.
            AfterLoadLogAt = Time.realtimeSinceStartup + 3f;   // 게임이 이전 맵 이미지를 치운 뒤(ReloadAssets 끝)에 남은 양을 적는다
            ShaderWarm.LevelChanged = true;   // 새 장식/이벤트가 올라왔다: 다음 곡 시작 때 필터 셰이더를 다시 본다
            ShaderWarm.AfterLoad();           // 필터 셰이더는 곡 시작이 아니라 지금(불러오기 끝) 데운다
            PerfOverlay.MarkLoading(SettingsWindow.T("맵 불러오기", "Level load"));
            return __exception;
        }

        private static double putMs, fallbackMs;
        private static int fallbackNotes;   // (개발자용) 원래 방식으로 간 이미지 이름을 맵마다 20장까지
        private static int gcAtStart;
        private static bool gcWasOn;

        internal static void Stop()
        {
            UploadBudget.Restore();
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
                {
                    if (it.State != 2) continue;
                    it.Abandoned = true;
                    if (it.InFlight > 0) { if (it.Pixels != IntPtr.Zero) it.PixelsOrphan = true; if (it.Blocks != IntPtr.Zero) it.BlocksOrphan = true; continue; }   // (5초 넘게 압축 중인 것) 끝나면 작업 스레드가 푼다
                    if (it.Pixels != IntPtr.Zero) { Marshal.FreeHGlobal(it.Pixels); it.Pixels = IntPtr.Zero; }
                    if (it.Blocks != IntPtr.Zero) { Marshal.FreeHGlobal(it.Blocks); it.Blocks = IntPtr.Zero; }
                }
                items = new List<Item>();
                byPath = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
                pendingBytes = 0;
            }
            if (markers != null) markers.Clear();
        }
    }
}
