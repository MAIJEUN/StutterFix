using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.LowLevel;

namespace StutterFix
{
    // 화면 밖 타일 끄기 (타일이 많은 맵).
    //
    // 2026-09-28 측정(9만 타일 시험 맵, 플레이어용): 5천 타일 맵 402 FPS 대 9만 타일 295 FPS. 늘어난 CPU 0.9ms 는 거의 다 유니티가
    // 카메라 4개마다 모든 타일 렌더러를 "보이나?" 검사하는 값이었다(카메라별 메인 스레드 시간 +0.84ms). 먼 타일 렌더러를 enabled=false 로
    // 하면 유니티 검사 목록에서 빠져 380 FPS (forceRenderingOff 는 목록에 남아 333 FPS 뿐).
    //
    // 방법: 타일 루트의 MeshRenderer(타일 모양, 메인 카메라만 그림)를 칸(8x8) 격자에 넣고, 매 프레임 카메라 이동이 다 끝난 뒤
    // (PostLateUpdate 맨 앞, 그리기 전) 카메라가 볼 수 있는 원(회전해도 되게) + 여유를 덮는 칸만 켠다. 칸이 바뀐 곳만 켜고 끈다.
    //   화면: 꺼 둔 타일은 그 순간 카메라 검사 범위 밖이라 원래도 안 그려진다. 켜는 것은 그리기 전이다.
    //   게임이 보는 상태: isVisible·OnBecameVisible/Invisible 은 원래도 화면 밖에서 false 라 같다(켤 때는 아직 화면 밖).
    //     게임·다른 모드가 렌더러 enabled 를 읽고 쓰는 곳(게임 시작 때 모든 어셈블리를 훑어 찾음, 약 200곳)은 꺼 둔 타일에 대해
    //     게임이 원하는 값을 따로 기억해 돌려주고, 켤 때 그 값으로 되돌린다.
    //   타일이 움직이거나 모양이 바뀌면(트랙 이동·등장·사라짐 효과, 타일 회전·리셋, 메시 다시 만들기, 타일 스크립트가 도는 것) 바로 켜고
    //     한동안 관리에서 뺀다. 1초 넘게 안 움직이면 새 크기로 다시 넣는다.
    //   곡 중에만, 타일이 MinFloors 개 이상인 맵에서만. 곡이 끝나거나 장면·타일이 다시 만들어지면 전부 원래대로 켠다.
    //   안전장치: 이 단계가 한 프레임이라도 안 돌면(다른 모드가 PlayerLoop 를 바꾸는 등) 곧바로 전부 켜고 멈춘다.
    // 개발자용 검증(tilecull-verify.txt): 메인 카메라 그리기 직전마다, 꺼 둔 타일 중 실제 카메라 화면과 겹치는 것, 꺼 둔 뒤 움직인 것을 센다.
    //   자동 시험 culltest: 지금 상태로 한 번, 전부 켠 상태로 한 번 메인 카메라를 그려 픽셀을 비교한다.
    internal static class TileCull
    {
        internal static bool Enabled = true;
        internal static int MinFloors = 3000;
        internal static bool Verify;
        internal static bool Ready;           // 렌더러 enabled 호출을 모두 바꿔 끼웠음
        internal static bool ForceOff;        // (개발자용 자동 시험 tilecull off) 비교 측정용
        private static bool active, blocked;   // blocked: 곡 도중 멈췄으면 다음 곡까지 다시 시작하지 않는다
        private const float Cell = 8f;

        // ── 타일 목록 (곡마다 새로 만든다) ──
        private static scrFloor[] fl = new scrFloor[0];
        private static Renderer[] rend = new Renderer[0];
        private static Bounds[] bnd = new Bounds[0];
        private static byte[] st = new byte[0];      // 0 관리(켜짐), 1 꺼 둠, 2 관리 밖(움직임·모양 바뀜·너무 큼), 3 대상 아님
        private static bool[] want = new bool[0];    // 꺼 둔 동안 게임이 원하는 enabled
        // 타일 발광(topGlow, 자식 SpriteRenderer)도 같이 끈다. 평소 알파 0 이어도 켜져 있으면 카메라마다 컬링 목록에 들어간다
        // (HELLO 130초: 타일 14,721개 중 발광 활성 9,712개, 그중 보이는 것 64개). culled 사전에는 -(i+1) 로 넣는다.
        internal static bool CullGlow = true;
        private static Renderer[] glowR = new Renderer[0];
        private static bool[] wantGlow = new bool[0];
        private static int[] cover = new int[0];     // 이 타일을 덮는 켜진 칸 수
        private static int[] quiet = new int[0];     // 관리 밖 타일: 안 움직인 검사 횟수
        private static readonly Dictionary<long, List<int>> cells = new Dictionary<long, List<int>>();
        private static readonly Dictionary<Renderer, int> culled = new Dictionary<Renderer, int>(RefEq<Renderer>.I);
        private static readonly Dictionary<GameObject, int> byGo = new Dictionary<GameObject, int>(RefEq<GameObject>.I);
        private static readonly List<int> outList = new List<int>();
        private static readonly Dictionary<scrFloor, int> floorIndex = new Dictionary<scrFloor, int>(RefEq<scrFloor>.I);
        // 지금 렌더러를 꺼 둔(화면 밖) 타일인가 (RecolorDefer 가 엔진 호출을 미룰 수 있는지 묻는다)
        internal static bool IsCulled(scrFloor f) { int i; return active && floorIndex.TryGetValue(f, out i) && st[i] == 1; }
        private static int rx0, ry0, rx1, ry1;   // 지금 켠 칸 범위 (포함)
        private static bool haveRect;
        private static int lastStep = -10, frames;
        internal static long Culls, Unculls, Pins, Violations, Moved, ReadHits;
        internal static string Counters { get { return "끔 " + Culls + " 켬 " + Unculls + " 관리에서 뺌 " + Pins + " 미룬 메시 갱신에 그대로 둠 " + DeferMeshKept; } }

        private sealed class RefEq<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly RefEq<T> I = new RefEq<T>();
            public bool Equals(T a, T b) { return ReferenceEquals(a, b); }
            public int GetHashCode(T o) { return RuntimeHelpers.GetHashCode(o); }
        }

        // ── 렌더러 enabled 바꿔 끼우기 ──
        public static void SetEnabled(Renderer r, bool v)
        {
            int i;
            if (culled.Count > 0 && culled.TryGetValue(r, out i)) { if (i >= 0) want[i] = v; else wantGlow[-i - 1] = v; ReadHits++; return; }
            r.enabled = v;
        }
        public static bool GetEnabled(Renderer r)
        {
            int i;
            if (culled.Count > 0 && culled.TryGetValue(r, out i)) { ReadHits++; return i >= 0 ? want[i] : wantGlow[-i - 1]; }
            return r.enabled;
        }

        private static readonly MethodInfo setE = typeof(Renderer).GetProperty("enabled").GetSetMethod();
        private static readonly MethodInfo getE = typeof(Renderer).GetProperty("enabled").GetGetMethod();
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var ins in instructions)
            {
                if ((ins.opcode == OpCodes.Call || ins.opcode == OpCodes.Callvirt) && ins.operand is MethodInfo mi)
                {
                    if (mi == setE) { ins.opcode = OpCodes.Call; ins.operand = typeof(TileCull).GetMethod(nameof(SetEnabled)); }
                    else if (mi == getE) { ins.opcode = OpCodes.Call; ins.operand = typeof(TileCull).GetMethod(nameof(GetEnabled)); }
                }
                yield return ins;
            }
        }

        // 모든 어셈블리(유니티·시스템·하모니·UMM·이 모드 제외)에서 Renderer.enabled 를 부르는 함수 찾기: 백그라운드, 결과는 파일에 캐시
        private static Harmony harmony;
        private static volatile List<MethodBase> found;
        private static int patchAt, patchFail, patchFrames;
        private static long patchMs, patchWorst;
        private static string cachePath;
        internal static void Install(Harmony h, string modDir)
        {
            harmony = h;
            gen++; shut = false; Ready = false; found = null; patchAt = 0; patchFail = 0; loopLost = false; blocked = false; pauseDepth = 0;
            cachePath = Path.Combine(modDir, "tilecull-scan.txt");
            var patchMover = new HarmonyMethod(typeof(TileCull), nameof(PinSelf));
            // 타일을 움직이거나 모양을 바꾸는 곳
            foreach (var n in new[] { "TweenRotation", "SetRotation", "ResetToLevelStart" })
            {
                var m = AccessTools.Method(typeof(scrFloor), n);
                if (m != null) h.Patch(m, prefix: patchMover);
            }
            // 타일 스크립트(Update/LateUpdate)는 위치·회전·크기·메시를 바꾸지 않는다(IL 확인: 색·투명도·아이콘·발광만).
            // 예전에는 스크립트가 돌면 관리에서 뺐는데(켜진 타일마다 매 프레임 2번 gameObject 읽기), 지금은 미뤄 둔 색 바꾸기만 먼저 적용한다.
            var script = new HarmonyMethod(typeof(TileCull), nameof(ScriptPrefix));
            foreach (var n in new[] { "LateUpdate", "Update" })
            {
                var m = AccessTools.Method(typeof(scrFloor), n);
                if (m != null) h.Patch(m, prefix: script);
            }
            var fm = AccessTools.TypeByName("FloorMesh");
            if (fm != null) foreach (var n in new[] { "GenerateMesh", "UpdateMesh" }) { var m = AccessTools.Method(fm, n); if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(TileCull), nameof(PinComp))); }
            foreach (var tn in new[] { "ffxFloorAppearPlus", "ffxFloorDisappearPlus" })
            {
                var t = AccessTools.TypeByName(tn);
                if (t == null) continue;
                foreach (var n in new[] { "StartEffect", "ScrubToTime", "FloorSetup" }) { var m = AccessTools.DeclaredMethod(t, n); if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(TileCull), nameof(PinFx))); }
            }
            foreach (var m in typeof(ffxMoveFloorPlus).GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                if (m.Name.Contains("TweenFloor")) h.Patch(m, prefix: new HarmonyMethod(typeof(TileCull), nameof(PinArg)));
            // 타일이 다시 만들어지거나 장면이 초기화되면 전부 켜고 멈춘다
            var off = new HarmonyMethod(typeof(TileCull), nameof(StopHook));
            // 타일 다시 만들기·장면 초기화: 하는 동안만 전부 켜 두고, 끝나면 새 상태로 다시 시작
            var pause = new HarmonyMethod(typeof(TileCull), nameof(PauseHook));
            var resume = new HarmonyMethod(typeof(TileCull), nameof(ResumeHook));
            h.Patch(AccessTools.Method(typeof(scrLevelMaker), "MakeLevel"), prefix: pause, finalizer: resume);
            h.Patch(AccessTools.Method(typeof(scnGame), "ResetScene"), prefix: pause, finalizer: resume);
            var sw = AccessTools.Method(typeof(scnEditor), "SwitchToEditMode");
            if (sw != null) h.Patch(sw, prefix: off);
            new System.Threading.Thread(Scan) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal }.Start();
        }

        private static bool Skip(string an)
        {
            return an.StartsWith("Unity") || an.StartsWith("System") || an == "mscorlib" || an == "netstandard" || an.StartsWith("Mono.") || an.Contains("Harmony")
                // UMM reload renames the next assembly StutterFix1, StutterFix2,
                // ... . Rewriting our own SetEnabled would call itself forever.
                || an == "UnityModManager" || OwnAssembly(an) || an.StartsWith("Microsoft.") || an.StartsWith("DOTween") || an.StartsWith("Newtonsoft")
                // MelonLoader 와 그 도구들
                || an.StartsWith("MelonLoader") || an.StartsWith("MonoMod") || an.StartsWith("AsmResolver") || an.StartsWith("AssetsTools") || an.StartsWith("AssetRipper")
                || an == "Tomlet" || an == "bHapticsLib" || an == "WebSocketDotNet" || an == "IndexRange" || an == "ValueTupleBridge";
        }
        private static bool OwnAssembly(string name) => name != null && name.StartsWith("StutterFix", StringComparison.Ordinal);

#if AUTOTEST
        // Explicit reload check only; never scans Harmony patches per frame.
        internal static string ReloadPatchAudit()
        {
            int external=0,own=0;
            foreach(var original in Harmony.GetAllPatchedMethods())
            {
                var info=Harmony.GetPatchInfo(original);
                if(info==null)continue;
                foreach(var patch in info.Transpilers)
                    if(patch.PatchMethod?.DeclaringType?.FullName==typeof(TileCull).FullName)
                    {
                        if(OwnAssembly(original.DeclaringType?.Assembly.GetName().Name))own++;
                        else external++;
                    }
            }
            if(own!=0 || external==0)throw new InvalidOperationException("TileCull self-patch/nonempty external patch contract: "+own+"/"+external);
            return "[TileCull reload] own_transpilers="+own+" external_transpilers="+external
                +" current_module="+typeof(TileCull).Assembly.GetName().Name+" mvid="+typeof(TileCull).Assembly.ManifestModule.ModuleVersionId;
        }
#endif

        private static int gen;   // 설치 세대: 해제 뒤 끝난 옛 훑기 결과를 쓰지 않게
        private static void Scan()
        {
            int g = gen;
            try
            {
                System.Threading.Thread.Sleep(8000);   // 모드들이 다 올라온 뒤
                var asms = new List<Assembly>();
                var key = new System.Text.StringBuilder();
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.IsDynamic || Skip(a.GetName().Name)) continue;
                    asms.Add(a);
                    key.Append(a.GetName().Name).Append(':').Append(a.ManifestModule.ModuleVersionId).Append(';');
                }
                var list = new List<MethodBase>();
                // 캐시: 같은 어셈블리 묶음이면 지난번 결과
                try
                {
                    if (File.Exists(cachePath))
                    {
                        var lines = File.ReadAllLines(cachePath);
                        if (lines.Length > 0 && lines[0] == key.ToString())
                        {
                            var byName = new Dictionary<string, Assembly>();
                            foreach (var a in asms) byName[a.GetName().Name] = a;
                            for (int i = 1; i < lines.Length; i++)
                            {
                                var p = lines[i].Split('|');
                                Assembly a;
                                if (p.Length == 2 && byName.TryGetValue(p[0], out a)) list.Add(a.ManifestModule.ResolveMethod(int.Parse(p[1])));
                            }
                            if (g == gen) found = list;
                            return;
                        }
                    }
                }
                catch { list.Clear(); }
                var sw = Stopwatch.StartNew();
                var sb = new System.Text.StringBuilder(key.ToString());
                foreach (var asm in asms)
                {
                    Type[] types;
                    try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                    var mod = asm.ManifestModule;
                    var cache = new Dictionary<int, bool>();
                    foreach (var t in types)
                    {
                        if (t == null) continue;
                        MethodBase[] ms;
                        try
                        {
                            var l = new List<MethodBase>();
                            l.AddRange(t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                            l.AddRange(t.GetConstructors(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
                            ms = l.ToArray();
                        }
                        catch { continue; }
                        foreach (var m in ms)
                        {
                            MethodBody body;
                            try { body = m.GetMethodBody(); } catch { continue; }
                            if (body == null) continue;
                            var il = body.GetILAsByteArray();
                            for (int i = 0; i + 4 < il.Length; i++)
                            {
                                if (il[i] != 0x28 && il[i] != 0x6F) continue;
                                int tok = BitConverter.ToInt32(il, i + 1);
                                int kind = tok >> 24;
                                if (kind != 0x0A && kind != 0x06) continue;
                                bool hit;
                                if (!cache.TryGetValue(tok, out hit)) { try { var mb = mod.ResolveMethod(tok); hit = mb == setE || mb == getE; } catch { hit = false; } cache[tok] = hit; }
                                if (hit) { list.Add(m); sb.Append('\n').Append(asm.GetName().Name).Append('|').Append(m.MetadataToken); break; }
                            }
                        }
                    }
                }
                try { File.WriteAllText(cachePath, sb.ToString()); } catch { }
                scanMs = sw.ElapsedMilliseconds;
                if (g == gen) found = list;
            }
            catch (Exception ex) { scanError = ex.Message; found = new List<MethodBase>(); patchFail = 1; }
        }
        private static long scanMs = -1;
        private static string scanError = "";

        // OnUpdate 에서: 찾은 함수를 곡 밖에서 조금씩 바꿔 끼운다 (한 프레임 8개), 그리고 안전장치
        internal static void Tick()
        {
            if (shut) return;
            if (active && lastStep < Time.frameCount - 1) { StopAll(); Main.Entry.Logger.Log("[화면 밖 타일] 단계가 한 프레임 안 돌아 전부 켜고 멈춤"); loopLost = true; }
            if (Ready || found == null || Hitch.Playing) return;
            var list = found;
            var psw = Stopwatch.StartNew();
            // 맵을 불러오는 중에만 한다(한 번에 0.7초쯤, 큰 함수 하나가 90ms): 메뉴·편집 화면에서 끊기지 않게
            if (!PerfOverlay.InLoading) return;
            for (; patchAt < list.Count && psw.ElapsedMilliseconds < 1000; patchAt++)
            {
                var m = list[patchAt];
                if (m == null || m.ContainsGenericParameters || (m.DeclaringType != null && m.DeclaringType.ContainsGenericParameters)) continue;
                // Also reject stale/cached own entries at the final patch boundary.
                if (OwnAssembly(m.DeclaringType?.Assembly.GetName().Name)) continue;
                try { harmony.Patch(m, transpiler: new HarmonyMethod(typeof(TileCull), nameof(Transpiler))); }
                catch (Exception ex) { patchFail++; if (Edition.Dev) Main.Entry.Logger.Log("[화면 밖 타일] 바꿔 끼우기 실패 " + m.DeclaringType + "." + m.Name + ": " + ex.Message); }
            }
            patchMs += psw.ElapsedMilliseconds; patchFrames++; if (psw.ElapsedMilliseconds > patchWorst) patchWorst = psw.ElapsedMilliseconds;
            if (patchAt >= list.Count)
            {
                Ready = patchFail == 0;
                InstallLoop();
                Main.Entry.Logger.Log("[화면 밖 타일] 렌더러 켜기/끄기 부르는 함수 " + list.Count + "개 바꿔 끼움, 세대 " + gen + (scanMs >= 0 ? " (훑기 " + scanMs + "ms)" : " (지난 결과)")
                    + ", " + patchFrames + "프레임에 나눠 " + patchMs + "ms, 한 프레임 최대 " + patchWorst + "ms"
                    + (patchFail > 0 ? " - 실패 " + patchFail + "개라 쓰지 않음 " + scanError : ""));
                found = null;
            }
        }
        private static bool loopLost;

        // ── PlayerLoop: PostLateUpdate 맨 앞 ──
        private struct TileCullStep { }
        private static void InstallLoop()
        {
            var root = PlayerLoop.GetCurrentPlayerLoop();
            var subs = root.subSystemList;
            for (int i = 0; i < subs.Length; i++)
            {
                if (subs[i].type != typeof(UnityEngine.PlayerLoop.PostLateUpdate)) continue;
                var list = new List<PlayerLoopSystem>(subs[i].subSystemList ?? new PlayerLoopSystem[0]);
                if (list.Exists(s => s.type == typeof(TileCullStep))) return;
                list.Insert(0, new PlayerLoopSystem { type = typeof(TileCullStep), updateDelegate = Step });
                subs[i].subSystemList = list.ToArray();
                root.subSystemList = subs;
                PlayerLoop.SetPlayerLoop(root);
                return;
            }
        }

        // ── 매 프레임 ──
        private static void Step()
        {
            lastStep = Time.frameCount;
            try
            {
                if (!Hitch.Playing) blocked = false;
                // 곡이 끝나도(완주·실패) 바로 전부 켜지 않는다: 큰 맵에서 수만 개 렌더러를 한 프레임에 켜면 곡 끝 순간 끊겼다(2.4.4).
                // 이미 돌던 것은 카메라를 따라 계속 돌리고, 다시 켜기는 멈추는 순간(타일 다시 만들기·장면 초기화·편집으로 나가기·장면 바뀜)에 한다.
                bool want = !shut && !blocked && pauseDepth == 0 && Enabled && !ForceOff && Ready && !loopLost && (Hitch.Playing || active) && ADOBase.customLevel != null && ADOBase.lm != null && ADOBase.lm.listFloors != null
                    && ADOBase.lm.listFloors.Count >= MinFloors && ADOBase.controller != null && ADOBase.controller.camy != null
                    && (!active || Hitch.Playing || (ReferenceEquals(ADOBase.lm.listFloors, builtList) && builtList.Count == fl.Length));   // 곡 뒤에 계속 돌 때: 같은 타일 목록일 때만 (장면이 바뀌면 멈춤)
                if (!want) { if (active) StopAll(); return; }
                // 카메라·줌을 먼저 본다: 예전에는 Build 뒤에 보고 멈춰서, 멀리 줌아웃한 채로는 매 프레임 Build(타일 4천 개 5ms)와 StopAll(전부 켜기)을 되풀이했다
                var cam = ADOBase.controller.camy.camobj;
                if (cam == null) { StopAll(); return; }
                Vector2 c = cam.transform.position;
                float h = cam.orthographic ? Mathf.Abs(cam.orthographicSize) : 50f;   // 맵이 줌을 음수로 두면(화면 뒤집기) 크기도 음수라 범위가 비어 멈췄다
                float r = h * Mathf.Sqrt(1f + cam.aspect * cam.aspect);
                r = r * 1.25f + 3f;   // 여유: 흔들림·그리기 직전 작은 움직임
                int x0 = Mathf.FloorToInt((c.x - r) / Cell), x1 = Mathf.FloorToInt((c.x + r) / Cell);
                int y0 = Mathf.FloorToInt((c.y - r) / Cell), y1 = Mathf.FloorToInt((c.y + r) / Cell);
                long cellWidth=(long)x1-x0+1,cellHeight=(long)y1-y0+1;
                if (cellWidth<=0 || cellHeight<=0 || cellWidth>200000 || cellHeight>200000 || cellWidth*cellHeight>200000) { if (active) { StopAll(); Main.Entry.Logger.Log("[화면 밖 타일] 멀리 줌아웃이라 쉼 (카메라 크기 " + h.ToString("0") + ")"); } farUntil = Time.frameCount + 30; return; }   // 너무 멀리 줌아웃: 하지 않음
                if (!active)
                {
                    if (Time.frameCount < farUntil) return;   // 줌 경계에서 켰다 껐다 하지 않게 잠깐 쉰다
                    Build();
                }
                Move(x0, y0, x1, y1);
                RecolorDefer.AfterStep();
                if (++frames % 30 == 0) Requalify();
            }
            catch (Exception ex) { StopAll(); Main.Entry.Logger.Log("[화면 밖 타일] 오류로 멈춤: " + ex.Message); loopLost = true; }
        }

        private static long Key(int x, int y) { return ((long)x << 32) ^ (uint)y; }

        private static List<scrFloor> builtList;
        private static int farUntil;
        private static void Build()
        {
            var sw = Stopwatch.StartNew();
            var list = ADOBase.lm.listFloors;
            int n = list.Count;
            builtList = list;
            fl = list.ToArray(); rend = new Renderer[n]; glowR = new Renderer[n]; wantGlow = new bool[n]; bnd = new Bounds[n]; st = new byte[n]; want = new bool[n]; cover = new int[n]; quiet = new int[n];
            cells.Clear(); culled.Clear(); byGo.Clear(); floorIndex.Clear(); haveRect = false;
            for (int i = 0; i < n; i++)
            {
                var f = fl[i];
                st[i] = 3;
                if (f == null) continue;
                byGo[f.gameObject] = i;
                floorIndex[f] = i;
                var fr = f.floorRenderer;
                var r = fr != null ? fr.renderer : null;
                if (r == null || !(r is MeshRenderer) || r.gameObject != f.gameObject) continue;
                rend[i] = r;
                if (CullGlow && f.topGlow != null && !f.isFake && f.topGlow.transform.parent == f.transform) glowR[i] = f.topGlow;
                Register(i);
            }
            active = true;
            Main.Entry.Logger.Log("[화면 밖 타일] 곡 시작: 타일 " + n + "개 등록 " + sw.ElapsedMilliseconds + "ms");
        }

        // 관리에 넣기: 지금 크기로 칸에 등록 (켜진 상태에서)
        private static void Register(int i)
        {
            var r = rend[i];
            bnd[i] = r.bounds;
            var b = bnd[i];
            int x0 = Mathf.FloorToInt(b.min.x / Cell), x1 = Mathf.FloorToInt(b.max.x / Cell);
            int y0 = Mathf.FloorToInt(b.min.y / Cell), y1 = Mathf.FloorToInt(b.max.y / Cell);
            if ((x1 - x0 + 1) * (y1 - y0 + 1) > 64 || float.IsNaN(b.center.x) || float.IsInfinity(b.extents.x)) { st[i] = 2; quiet[i] = int.MinValue; return; }   // 너무 큼: 관리 안 함
            st[i] = 0;
            cover[i] = 0;
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    List<int> l;
                    long k = Key(x, y);
                    if (!cells.TryGetValue(k, out l)) { l = new List<int>(); cells[k] = l; }
                    l.Add(i);
                    if (haveRect && x >= rx0 && x <= rx1 && y >= ry0 && y <= ry1) cover[i]++;
                }
            if (haveRect && cover[i] == 0) Cull(i);
        }

        private static void Unregister(int i)
        {
            var b = bnd[i];
            int x0 = Mathf.FloorToInt(b.min.x / Cell), x1 = Mathf.FloorToInt(b.max.x / Cell);
            int y0 = Mathf.FloorToInt(b.min.y / Cell), y1 = Mathf.FloorToInt(b.max.y / Cell);
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    List<int> l;
                    if (cells.TryGetValue(Key(x, y), out l)) l.Remove(i);
                }
        }

        private static void Cull(int i)
        {
            if (st[i] != 0) return;
            var r = rend[i];
            if (r == null || !r.enabled) return;   // 게임이 이미 꺼 둔 것은 건드리지 않는다
            want[i] = true;
            culled[r] = i;
            r.enabled = false;
            var g = glowR[i];
            if (!ReferenceEquals(g, null) && g != null && g.enabled) { wantGlow[i] = true; culled[g] = -(i + 1); g.enabled = false; }
            st[i] = 1;
            if (Verify) fl[i].transform.hasChanged = false;
            Culls++;
        }
        private static void Uncull(int i)
        {
            if (st[i] != 1) return;
            var r = rend[i];
            RecolorDefer.FlushFloor(fl[i]);   // 미뤄 둔 색·스타일을 렌더러를 켜기 전에 적용
            culled.Remove(r);
            st[i] = 0;
            if (r != null && want[i]) r.enabled = true;
            var g = glowR[i];
            if (!ReferenceEquals(g, null) && culled.Remove(g) && g != null && wantGlow[i]) g.enabled = true;
            Unculls++;
        }

        // 켤 칸 범위를 옮긴다: 빠지는 칸의 타일은 덮는 칸이 0 이 되면 끄고, 새로 드는 칸의 타일은 켠다
        private static void Move(int x0, int y0, int x1, int y1)
        {
            if (haveRect && x0 == rx0 && y0 == ry0 && x1 == rx1 && y1 == ry1) return;
            // 먼저 새로 드는 칸 (켜기)
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                {
                    if (haveRect && x >= rx0 && x <= rx1 && y >= ry0 && y <= ry1) continue;
                    List<int> l;
                    if (!cells.TryGetValue(Key(x, y), out l)) continue;
                    foreach (var i in l) { if (st[i] > 1) continue; if (cover[i]++ == 0) Uncull(i); }
                }
            // 빠지는 칸 (끄기)
            if (haveRect)
            {
                for (int x = rx0; x <= rx1; x++)
                    for (int y = ry0; y <= ry1; y++)
                    {
                        if (x >= x0 && x <= x1 && y >= y0 && y <= y1) continue;
                        List<int> l;
                        if (!cells.TryGetValue(Key(x, y), out l)) continue;
                        foreach (var i in l) { if (st[i] > 1) continue; if (--cover[i] == 0) Cull(i); }
                    }
            }
            else
            {
                // 처음: 어느 칸에도 안 덮인 타일은 끈다
                for (int i = 0; i < st.Length; i++) if (st[i] == 0 && cover[i] == 0) Cull(i);
            }
            rx0 = x0; ry0 = y0; rx1 = x1; ry1 = y1; haveRect = true;
        }

        // ── 움직임·모양 바뀜: 켜고 관리에서 뺀다 ──
        private static void Pin(int i)
        {
            if (st[i] == 3) return;
            if (st[i] == 2) { quiet[i] = Math.Min(quiet[i], 0); return; }
            if (st[i] == 1) Uncull(i);
            Unregister(i);
            st[i] = 2; quiet[i] = 0; cover[i] = 0;
            fl[i].transform.hasChanged = false;
            Pins++;
        }
        private static void PinGo(GameObject go)
        {
            int i;
            if (active && !ReferenceEquals(go, null) && byGo.TryGetValue(go, out i)) Pin(i);
        }
        public static void PinSelf(scrFloor __instance) { if (active) PinGo(__instance.gameObject); }
        internal static bool PinOnScript;   // (개발자용 비교) 예전처럼 스크립트가 돌면 관리에서 뺀다
        internal static long DeferMeshKept;
        public static void ScriptPrefix(scrFloor __instance)
        {
            if (!active) return;
            if (PinOnScript) { PinGo(__instance.gameObject); return; }
            if (RecolorDefer.Count > 0) RecolorDefer.FlushFloor(__instance);   // 스크립트가 색을 읽거나 덮기 전에 미룬 것을 먼저
        }
        public static void PinComp(Component __instance)
        {
            if (!active) return;
            // 미뤄 둔 색 바꾸기를 화면 밖 타일에 미리 적용하며 생긴 메시 갱신은 그 타일을 켜지 않는다(화면 밖 그대로).
            // 스타일 메시 모양(길이·폭)만 조금 바뀌고, 켜는 칸 범위는 화면보다 25%+3 넓어 바뀐 크기를 덮는다.
            if (RecolorDefer.InMeshUpdate && !PinOnScript && !ReferenceEquals(__instance, null))
            {
                int i;
                if (byGo.TryGetValue(__instance.gameObject, out i) && st[i] == 1) { DeferMeshKept++; return; }
            }
            PinGo(__instance.gameObject);
        }
        public static void PinArg(scrFloor target) { if (active && target != null) PinGo(target.gameObject); }
        public static void PinFx(ffxPlusBase __instance)
        {
            if (!active) return;
            if (__instance.floor != null) { PinGo(__instance.floor.gameObject); if (__instance.floor.prevfloor != null) PinGo(__instance.floor.prevfloor.gameObject); }
            // 등장 효과의 prevFloor 는 직접 읽는다 (2.4.4 는 부를 때마다 리플렉션으로 필드를 찾아 효과 몰림 비용이 늘었다; 사라짐 효과에는 이 필드가 없다)
            var ap = __instance as ffxFloorAppearPlus;
            if (!ReferenceEquals(ap, null) && ap.prevFloor != null) PinGo(ap.prevFloor.gameObject);
        }

        // 관리 밖 타일: 30프레임마다 봐서 두 번(약 1초 이상) 연속 안 움직였으면 새 크기로 다시 관리
        private static void Requalify()
        {
            for (int i = 0; i < st.Length; i++)
            {
                if (st[i] != 2 || quiet[i] == int.MinValue) continue;
                var f = fl[i];
                if (f == null || rend[i] == null) { st[i] = 3; continue; }
                if (f.transform.hasChanged) { f.transform.hasChanged = false; quiet[i] = 0; continue; }
                if (++quiet[i] < 3) continue;
                if (!rend[i].enabled) continue;   // 게임이 꺼 둔 상태면 다음에
                Register(i);
            }
        }

        // 타일 다시 만들기·장면 초기화·편집으로 나가기 앞: 전부 켜고, 이 곡에서는 다시 시작하지 않는다
        internal static void SongBegin() { blocked = false; }
        private static int pauseDepth;
        public static void PauseHook() { pauseDepth++; StopAll(); }
        public static void ResumeHook() { if (pauseDepth > 0) pauseDepth--; }
        public static void StopHook() { if (Hitch.Playing) blocked = true; StopAll(); }

        // 전부 원래대로
        public static void StopAll()
        {
            if (!active) return;
            RecolorDefer.FlushAll();
            active = false;
            foreach (var kv in culled) { var r = kv.Key; int v = kv.Value; if (r != null && (v >= 0 ? want[v] : wantGlow[-v - 1])) r.enabled = true; }
            culled.Clear(); cells.Clear(); byGo.Clear(); floorIndex.Clear(); haveRect = false;
            fl = new scrFloor[0]; rend = new Renderer[0]; builtList = null;
            if (Verify || Edition.Dev) Main.Entry.Logger.Log("[화면 밖 타일] 멈춤: 끔 " + Culls + "번, 켬 " + Unculls + "번, 움직여 뺌 " + Pins + "번, 게임의 enabled 읽기/쓰기 대신 " + ReadHits + "번"
                + (Verify ? " | 검증: 화면과 겹친 꺼진 타일 " + Violations + "번, 꺼진 채 움직인 타일 " + Moved + "번" : ""));
            Culls = Unculls = Pins = ReadHits = Violations = Moved = 0;
        }

        // 모드 끄기·다시 불러오기 (Harmony 를 풀기 전에): 전부 켜고, PlayerLoop 단계·검증 이벤트를 떼고, 끝나지 않은 훑기 결과는 버린다
        private static bool shut;
        internal static void Shutdown()
        {
            shut = true; gen++;
            StopAll();
            found = null; Ready = false;
            if (Verify) { Camera.onPreCull -= VerifyPreCull; }
            var root = PlayerLoop.GetCurrentPlayerLoop();
            var subs = root.subSystemList;
            if (subs == null) return;
            for (int i = 0; i < subs.Length; i++)
            {
                var l = subs[i].subSystemList;
                if (l == null || !Array.Exists(l, x => x.type == typeof(TileCullStep))) continue;
                subs[i].subSystemList = Array.FindAll(l, x => x.type != typeof(TileCullStep));
                root.subSystemList = subs;
                PlayerLoop.SetPlayerLoop(root);
                return;
            }
        }

        // ── (개발자용) 검증: 메인 카메라 그리기 직전 ──
        internal static void VerifyInstall() { Camera.onPreCull += VerifyPreCull; }
        private static void VerifyPreCull(Camera cam)
        {
            if (!active || !Verify || ADOBase.controller == null || ADOBase.controller.camy == null || cam != ADOBase.controller.camy.camobj) return;
            // 실제 화면 사각형(회전 포함)을 감싸는 상자
            float h = cam.orthographicSize, w = h * cam.aspect;
            var t = cam.transform;
            Vector3 c = t.position, ax = t.right * w, ay = t.up * h;
            float ex = Mathf.Abs(ax.x) + Mathf.Abs(ay.x), ey = Mathf.Abs(ax.y) + Mathf.Abs(ay.y);
            foreach (var kv in culled)
            {
                int i = kv.Value;
                if (i < 0) continue;
                var b = bnd[i];
                if (b.max.x >= c.x - ex && b.min.x <= c.x + ex && b.max.y >= c.y - ey && b.min.y <= c.y + ey) Violations++;
                if (fl[i] != null && fl[i].transform.hasChanged) { Moved++; fl[i].transform.hasChanged = false; }
            }
        }

        // (개발자용 자동 시험 culltest) 지금 상태와 전부 켠 상태로 메인 카메라를 그려 비교
        private static int Diff(Color32[] a, Color32[] b)
        {
            int d = 0;
            for (int i = 0; i < a.Length; i++) if (a[i].r != b[i].r || a[i].g != b[i].g || a[i].b != b[i].b || a[i].a != b[i].a) d++;
            return d;
        }

        internal static string RenderCompare()
        {
            if (!active) return "꺼져 있음";
            var cam = ADOBase.controller.camy.camobj;
            var old = cam.targetTexture;
            int W = old != null ? old.width : Screen.width, H = old != null ? old.height : Screen.height;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32);
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            // 카메라의 화면 필터(모션 블러 등)는 지난 그리기를 기억해 같은 장면도 두 번 그리면 달라진다: 비교하는 동안 끈다.
            // 대조군: 같은 상태로 두 번 그린 것도 비교한다(0 이어야 이 비교를 믿을 수 있다).
            int oldMask = int.MinValue;
            int fl0Layer = 9;
            foreach (var kv in culled) { if (kv.Value < 0) continue; fl0Layer = kv.Key.gameObject.layer; break; }
            var fx = new List<Behaviour>();
            foreach (var bh in cam.GetComponents<Behaviour>()) if (!(bh is Camera) && bh.enabled) { bh.enabled = false; fx.Add(bh); }
            try
            {
                cam.targetTexture = rt;
                oldMask = cam.cullingMask; cam.cullingMask = 1 << fl0Layer;   // 타일 층만 (다른 것은 그릴 때마다 조금씩 움직여 비교가 흐려진다)
                RenderTexture.active = rt;
                Color32[] a, a2, b;
                cam.Render(); tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); a = tex.GetPixels32();
                cam.Render(); tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); a2 = tex.GetPixels32();
                var saved = new List<Renderer>();
                foreach (var kv in culled) if (kv.Value >= 0 ? want[kv.Value] : wantGlow[-kv.Value - 1]) { kv.Key.enabled = true; saved.Add(kv.Key); }
                cam.Render(); tex.ReadPixels(new Rect(0, 0, W, H), 0, 0); b = tex.GetPixels32();
                foreach (var r in saved) r.enabled = false;
                return "꺼 둔 타일 " + culled.Count + "개, " + W + "x" + H + " 픽셀 중 다른 것 " + Diff(a2, b) + "개 (대조군: 같은 상태 첫 번째와 두 번째 " + Diff(a, a2) + "개, 끈 필터 " + fx.Count + "개)";
            }
            finally
            {
                foreach (var bh in fx) bh.enabled = true;
                RenderTexture.active = null;
                cam.targetTexture = old;
                if (oldMask != int.MinValue) cam.cullingMask = oldMask;
                rt.Release(); UnityEngine.Object.Destroy(rt); UnityEngine.Object.Destroy(tex);
            }
        }
    }
}
