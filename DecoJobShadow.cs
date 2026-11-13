#if DEV || AUTOTEST
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using HarmonyLib;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Jobs;
using UnityEngine.SceneManagement;

namespace StutterFix
{
    // Opt-in, read-only shadow. Never invokes UpdatePosition or writes game fields/Transforms.
    internal static class DecoJobShadow
    {
        private const string Id = "StutterFix.decojobshadow";
        private const int Chunk = 64;
        private struct Input
        {
            public Vector2 Pivot, Scale, CamScale, TextureScale, Offset, Multiplier;
            public float StartRot, Angle;
        }
        private struct Output
        {
            public Vector3 Position, Scale;
            public Quaternion Rotation;
        }
        // One timing pair per 64 decorations, not per hot game call.
        private struct ChunkTime { public long Ticks; public int Thread; }
        private struct CalculateJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<Input> Inputs;
            [WriteOnly, NativeDisableParallelForRestriction] public NativeArray<Output> Outputs;
            [WriteOnly] public NativeArray<ChunkTime> Times;
            public Vector3 Camera;
            public int Count;
            public void Execute(int chunk)
            {
                long start = Stopwatch.GetTimestamp();
                int end = Math.Min(Count, (chunk + 1) * Chunk);
                for (int i = chunk * Chunk; i < end; i++) Outputs[i] = Calculate(Inputs[i], Camera);
                Times[chunk] = new ChunkTime { Ticks = Stopwatch.GetTimestamp() - start, Thread = JobsUtility.ThreadIndex };
            }
        }
        private static Output Calculate(Input x, Vector3 camera)
        {
            // Preserve the game's operator signatures and order (including + zero and * one).
            Vector2 p = x.Pivot + Vector2.zero;
            Vector3 start = p;
            Vector2 basePos = (Vector2)(camera - start) * x.Multiplier + (Vector2)start;
            Vector2 offset = x.Offset * x.CamScale / x.TextureScale;
            Vector2 end = basePos + offset;
            Vector2 scale = x.Scale * x.CamScale * (Vector2)Vector3.one;
            return new Output {
                Position = new Vector3(end.x, end.y, 0f),
                Rotation = Quaternion.Euler(0f, 0f, x.StartRot + x.Angle),
                Scale = new Vector3(scale.x, scale.y, 1f)
            };
        }

        private static readonly AccessTools.FieldRef<scrParallax, bool> inited = AccessTools.FieldRefAccess<scrParallax, bool>("inited");
        private static readonly AccessTools.FieldRef<scrParallax, bool> isDecoration = AccessTools.FieldRefAccess<scrParallax, bool>("isDecoration");
        private static readonly AccessTools.FieldRef<scrParallax, Transform> cached = AccessTools.FieldRefAccess<scrParallax, Transform>("cachedTransform");
        private static readonly List<scrDecoration> selected = new List<scrDecoration>();
        private static readonly List<Transform> targets = new List<Transform>();
        private static readonly Dictionary<Transform, int> indices = new Dictionary<Transform, int>();
        private static readonly HashSet<Transform> wanted = new HashSet<Transform>();
        private static readonly HashSet<Transform> parents = new HashSet<Transform>();
        private static readonly HashSet<int> threads = new HashSet<int>();
        private static NativeArray<Input> inputs;
        private static NativeArray<Output> sequential, parallel, actuals;
        private static NativeArray<ChunkTime> chunkTimes;
        private static TransformAccessArray access;
        private static JobHandle pending;
        private static bool scheduled, running, patched;
        private static scrDecorationManager owner;
        private static int capacity, lastFrame = -1, workers, mainThreadIndex, maxParents;
        private static long frames, samples, candidates, seqJobMismatch, seqActualMismatch, jobActualMismatch;
        private static long selectTicks, gatherTicks, actualTicks, seqTicks, scheduleTicks, completeTicks, chunkTicks, mainChunkTicks, compareTicks, syncTicks, resizeTicks;
        private static long adds, removes, resizes, arrayBuildTicks, arraySetTicks, changedFrames, changedSyncTicks;
        private static int arrayBenchmarkN;
        private static string first = "none", stopped = "none";
        private static double firstSong, lastSong;

        internal static string Command(string arg)
        {
            if (arg == "shadow begin") return Begin();
            if (arg == "shadow end") return End();
            if (arg == "writetest") return WriteTest();
            return "decojob shadow begin|end|writetest";
        }
        // 더미 오브젝트(장식과 무관)로: 메인 스레드 설정자(position/rotation/localScale = ...)와 작업 스레드(TransformAccess)가
        // 같은 값을 넣었을 때 읽어 낸 Transform 값이 비트 단위로 같은지. 부모는 원점·회전 없음·크기 1(장식 컨테이너와 같은 조건).
        private struct WriteJob : IJobParallelForTransform
        {
            [ReadOnly] public NativeArray<Vector3> Pos, Scale;
            [ReadOnly] public NativeArray<Quaternion> Rot;
            public void Execute(int i, TransformAccess t) { t.position = Pos[i]; t.rotation = Rot[i]; t.localScale = Scale[i]; }
        }
        private static string WriteTest()
        {
            const int N = 4000;
            var rnd = new System.Random(12345);
            var parent = new GameObject("decojob-writetest-parent");
            var objs = new GameObject[N]; var tr = new Transform[N];
            var pos = new NativeArray<Vector3>(N, Allocator.Persistent); var rot = new NativeArray<Quaternion>(N, Allocator.Persistent); var scl = new NativeArray<Vector3>(N, Allocator.Persistent);
            var a = new Output[N]; var b = new Output[N];
            try
            {
                for (int i = 0; i < N; i++)
                {
                    objs[i] = new GameObject("w" + i); tr[i] = objs[i].transform; tr[i].SetParent(parent.transform, false);
                    float z = i % 7 == 0 ? 0f : (float)((rnd.NextDouble() * 2 - 1) * (i % 3 == 0 ? 3600.0 : 720.0));
                    pos[i] = new Vector3((float)((rnd.NextDouble() * 2 - 1) * 2000), (float)((rnd.NextDouble() * 2 - 1) * 2000), 0f);
                    rot[i] = Quaternion.Euler(0f, 0f, z);
                    scl[i] = new Vector3((float)(rnd.NextDouble() * 4), (float)(rnd.NextDouble() * 4), 1f);
                }
                for (int i = 0; i < N; i++) { tr[i].position = pos[i]; tr[i].rotation = rot[i]; tr[i].localScale = scl[i]; a[i] = Read(tr[i]); }
                for (int i = 0; i < N; i++) { tr[i].position = Vector3.zero; tr[i].rotation = Quaternion.identity; tr[i].localScale = Vector3.one; }
                var taa = new TransformAccessArray(tr, 64);
                try
                {
                    var h = new WriteJob { Pos = pos, Rot = rot, Scale = scl }.Schedule(taa);
                    h.Complete();
                }
                finally { taa.Dispose(); }
                for (int i = 0; i < N; i++) b[i] = Read(tr[i]);
                long bitMis = 0, realMis = 0; long[] f = new long[10], fr = new long[10];
                for (int i = 0; i < N; i++)
                {
                    float[] x = Flat(a[i]), y = Flat(b[i]); bool real = false, bit = false;
                    for (int k = 0; k < 10; k++)
                    {
                        if (!Bits(x[k], y[k])) { f[k]++; bit = true; if (x[k] != y[k]) { fr[k]++; real = true; } }
                    }
                    if (bit) bitMis++; if (real) realMis++;
                }
                // 쓰기 시간: 장식 약 2,150개분을 반복해 메인 스레드 설정자 3개 vs 작업 스레드 TransformAccess (스케줄+완료)
                const int M = 2150, R = 200;
                var tr2 = new Transform[M]; Array.Copy(tr, tr2, M);
                var taa2 = new TransformAccessArray(tr2, 64);
                double mainMs, jobMs, jobSchedMs;
                try
                {
                    var job = new WriteJob { Pos = pos, Rot = rot, Scale = scl };
                    for (int w = 0; w < 5; w++) { job.Schedule(taa2).Complete(); }   // 예열
                    long t0 = Stopwatch.GetTimestamp();
                    for (int r = 0; r < R; r++) for (int i = 0; i < M; i++) { tr2[i].position = pos[i]; tr2[i].rotation = rot[i]; tr2[i].localScale = scl[i]; }
                    long t1 = Stopwatch.GetTimestamp();
                    long sch = 0;
                    for (int r = 0; r < R; r++) { long q0 = Stopwatch.GetTimestamp(); var h2 = job.Schedule(taa2); sch += Stopwatch.GetTimestamp() - q0; h2.Complete(); }
                    long t2 = Stopwatch.GetTimestamp();
                    double tk2 = Stopwatch.Frequency / 1000.0;
                    mainMs = (t1 - t0) / tk2 / R; jobMs = (t2 - t1) / tk2 / R; jobSchedMs = sch / tk2 / R;
                }
                finally { taa2.Dispose(); }
                var sb = new StringBuilder("writetime M=" + M + " per-frame-ms main-thread-3-setters=" + mainMs.ToString("F3") + " job-Schedule+Complete=" + jobMs.ToString("F3") + " (Schedule part " + jobSchedMs.ToString("F3") + ") | writetest N=" + N + " main-thread-setter vs job-TransformAccess readback: bit-mismatch objects=" + bitMis + " real(excluding +0/-0)=" + realMis + " | by field bit|real: ");
                for (int k = 0; k < 10; k++) sb.Append(fieldNames[k]).Append('=').Append(f[k]).Append('|').Append(fr[k]).Append(' ');
                return sb.ToString();
            }
            finally
            {
                for (int i = 0; i < N; i++) if (objs[i] != null) UnityEngine.Object.Destroy(objs[i]);
                UnityEngine.Object.Destroy(parent);
                if (pos.IsCreated) pos.Dispose(); if (rot.IsCreated) rot.Dispose(); if (scl.IsCreated) scl.Dispose();
            }
        }
        private static Output Read(Transform t) { return new Output { Position = t.position, Rotation = t.rotation, Scale = t.localScale }; }
        private static float[] Flat(Output o) { return new[] { o.Position.x, o.Position.y, o.Position.z, o.Rotation.x, o.Rotation.y, o.Rotation.z, o.Rotation.w, o.Scale.x, o.Scale.y, o.Scale.z }; }
        private static string Begin()
        {
            Stop();
            frames = samples = candidates = seqJobMismatch = seqActualMismatch = jobActualMismatch = 0; System.Array.Clear(fieldMis,0,10); System.Array.Clear(fieldMisNoZero,0,10); realMis = 0; System.Array.Clear(normMis,0,4); normRealMis = normBitMis = 0;
            selectTicks = gatherTicks = actualTicks = seqTicks = scheduleTicks = completeTicks = chunkTicks = mainChunkTicks = compareTicks = syncTicks = resizeTicks = 0;
            adds = removes = resizes = arrayBuildTicks = arraySetTicks = changedFrames = changedSyncTicks = 0;
            arrayBenchmarkN = maxParents = 0; first = "none"; stopped = "none"; threads.Clear(); lastFrame = -1;
            firstSong = lastSong = Song();
            try
            {
                owner = scrDecorationManager.instance;
                if (owner == null || !Hitch.Playing) { owner = null; return "begin refused: start song playback first"; }
                workers = JobsUtility.JobWorkerCount;
                mainThreadIndex = JobsUtility.ThreadIndex;
                running = true;
                SceneManager.activeSceneChanged += SceneChanged;
                Camera.onPreCull += CheckLifetime;
                patched = true;
                new Harmony(Id).Patch(AccessTools.Method(typeof(scrDecorationManager), "LateUpdate"),
                    postfix: new HarmonyMethod(typeof(DecoJobShadow), nameof(LatePost)) { priority = Priority.Last });
                return "shadow begun; JobWorkerCount=" + workers + "; chunk=64; FPS invalid for A/B; first frame includes cold setup";
            }
            catch (Exception ex) { stopped = ex.ToString(); Stop(); return "begin failed: " + stopped; }
        }
        private static void SceneChanged(Scene a, Scene b) { stopped = "scene changed"; Stop(); }
        private static void CheckLifetime(Camera camera)
        {
            if (running && (!Hitch.Playing || owner == null || scrDecorationManager.instance != owner))
            { stopped = "song stopped or manager changed"; Stop(); }
        }
        private static double Song() => scrConductor.instance == null ? 0 : scrConductor.instance.songposition_minusi;

        public static void LatePost(scrDecorationManager __instance)
        {
            if (!running) return;
            if (!Hitch.Playing || __instance != owner) { stopped = "song stopped or manager changed"; Stop(); return; }
            if (lastFrame == Time.frameCount) return;
            lastFrame = Time.frameCount;
            try { Sample(__instance); }
            catch (Exception ex)
            {
                stopped = ex.ToString();
                Stop();
                Main.Entry.Logger.Log("[decojob] shadow stopped: " + stopped);
            }
        }

        private static bool Identity(Transform t)
        {
            if (t == null) return true;
            Vector3 p = t.position, s = t.lossyScale; Quaternion q = t.rotation;
            return p.x == 0f && p.y == 0f && p.z == 0f && q.x == 0f && q.y == 0f && q.z == 0f && q.w == 1f && s.x == 1f && s.y == 1f && s.z == 1f;
        }
        private static void Sample(scrDecorationManager manager)
        {
            long t = Stopwatch.GetTimestamp();
            selected.Clear(); parents.Clear();
            // Scan the original list rather than invoking Dormancy.List (which mutates its state).
            // Invisible/dormant decorations do not run UpdatePosition and must not be compared.
            var all = manager.allDecorations;
            var camera = ADOBase.controller.camy.transform;
            Vector3 cameraPos = camera.position; // once per manager/frame
            for (int i = 0; i < all.Count; i++)
            {
                var d = all[i]; candidates++;
                if (d == null || d.GetType() != typeof(scrVisualDecoration) || !d.GetVisible()
                    || (object)d.followPlanet != null || d.stickToFloor || d.lockRotation) continue;
                var p = d.parallax; var pivot = d.pivotTrans;
                if (p == null || pivot == null || !inited(p) || !isDecoration(p) || !ReferenceEquals(p.decoration, d)
                    || !ReferenceEquals(cached(p), pivot) || p.cameraTransform != camera
                    || p.clampToScreen || p.dontAlterX || p.dontAlterY || (ADOBase.customLevel == null && !p.enabled)) continue;
                var parent = pivot.parent;
                if (!Identity(parent)) continue;
                selected.Add(d);
                if (parent != null) parents.Add(parent);
            }
            selectTicks += Stopwatch.GetTimestamp() - t;
            maxParents = Math.Max(maxParents, parents.Count);
            int n = selected.Count;
            EnsureCapacity(n);
            t = Stopwatch.GetTimestamp();
            for (int i = 0; i < n; i++)
            {
                var d = selected[i]; var p = d.parallax;
                inputs[i] = new Input { Pivot = d.pivotPosVec, Scale = d.scaleVec, CamScale = d.camScaleMultiplier,
                    TextureScale = d.textureScaleMultiplier, Offset = d.parallaxOffset, Multiplier = new Vector2(p.multiplier_x, p.multiplier_y),
                    StartRot = d.startRot, Angle = d.rotAngle };
            }
            gatherTicks += Stopwatch.GetTimestamp() - t;
            t = Stopwatch.GetTimestamp();
            long changesBefore = adds + removes;
            SyncTargets();
            long syncElapsed = Stopwatch.GetTimestamp() - t;
            syncTicks += syncElapsed;
            if (adds + removes != changesBefore) { changedFrames++; changedSyncTicks += syncElapsed; }
            if (n > 0 && (arrayBenchmarkN == 0 || (arrayBenchmarkN < 2150 && n >= 2150))) BenchmarkArray();
            t = Stopwatch.GetTimestamp();
            for (int i = 0; i < n; i++) sequential[i] = Calculate(inputs[i], cameraPos);
            seqTicks += Stopwatch.GetTimestamp() - t;
            int chunks = (n + Chunk - 1) / Chunk;
            if (n > 0)
            {
                var job = new CalculateJob { Inputs = inputs, Outputs = parallel, Times = chunkTimes, Count = n, Camera = cameraPos };
                t = Stopwatch.GetTimestamp();
                pending = job.Schedule(chunks, 1); scheduled = true;
                scheduleTicks += Stopwatch.GetTimestamp() - t;
                t = Stopwatch.GetTimestamp();
                pending.Complete(); scheduled = false;
                completeTicks += Stopwatch.GetTimestamp() - t;
                for (int i = 0; i < chunks; i++)
                {
                    var time = chunkTimes[i]; chunkTicks += time.Ticks; threads.Add(time.Thread);
                    if (time.Thread == mainThreadIndex) mainChunkTicks += time.Ticks;
                }
            }
            t = Stopwatch.GetTimestamp();
            for (int i = 0; i < n; i++)
            {
                var pivot = selected[i].pivotTrans;
                actuals[i] = new Output { Position = pivot.position, Rotation = pivot.rotation, Scale = pivot.localScale };
            }
            actualTicks += Stopwatch.GetTimestamp() - t;
            t = Stopwatch.GetTimestamp();
            for (int i = 0; i < n; i++)
            {
                Output actual = actuals[i], a = sequential[i], b = parallel[i];
                string field;
                if (!Equal(a, b, out field)) { seqJobMismatch++; Remember(i, "seq/job", field, a, b); }
                if (!Equal(a, actual, out field)) { seqActualMismatch++; Remember(i, "seq/actual", field, a, actual); FieldCount(a, actual); }
                if (!Equal(b, actual, out field)) { jobActualMismatch++; Remember(i, "job/actual", field, b, actual); }
            }
            compareTicks += Stopwatch.GetTimestamp() - t;
            frames++; samples += n; lastSong = Song();
        }
        private static void EnsureCapacity(int n)
        {
            if (n <= capacity) return;
            long t = Stopwatch.GetTimestamp();
            DisposeArrays();
            capacity = Math.Max(256, Math.Max(n, capacity * 2));
            inputs = new NativeArray<Input>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            sequential = new NativeArray<Output>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            parallel = new NativeArray<Output>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            actuals = new NativeArray<Output>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            chunkTimes = new NativeArray<ChunkTime>((capacity + Chunk - 1) / Chunk, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            resizes++; resizeTicks += Stopwatch.GetTimestamp() - t;
        }
        // Swap-back maintenance: track real membership changes; no Transform job is ever scheduled.
        private static void SyncTargets()
        {
            wanted.Clear();
            for (int i = 0; i < selected.Count; i++) wanted.Add(selected[i].pivotTrans);
            if (!access.isCreated) access = new TransformAccessArray(Math.Max(256, selected.Count));
            for (int i = targets.Count - 1; i >= 0; i--)
            {
                var old = targets[i];
                if (wanted.Contains(old)) continue;
                int last = targets.Count - 1;
                access.RemoveAtSwapBack(i); indices.Remove(old);
                if (i != last) { targets[i] = targets[last]; indices[targets[i]] = i; }
                targets.RemoveAt(last); removes++;
            }
            for (int i = 0; i < selected.Count; i++)
            {
                var target = selected[i].pivotTrans;
                if (indices.ContainsKey(target)) continue;
                access.Add(target); indices.Add(target, targets.Count); targets.Add(target); adds++;
            }
        }
        private static void BenchmarkArray()
        {
            // One-shot actual cohort size (report it; never claim exactly 2150 unless it is).
            Transform[] cohort = new Transform[Math.Min(2150, targets.Count)];
            targets.CopyTo(0, cohort, 0, cohort.Length);
            TransformAccessArray temp = default;
            try
            {
                long t = Stopwatch.GetTimestamp();
                temp = new TransformAccessArray(cohort.Length);
                for (int i = 0; i < cohort.Length; i++) temp.Add(cohort[i]);
                arrayBuildTicks = Stopwatch.GetTimestamp() - t;
                t = Stopwatch.GetTimestamp(); temp.SetTransforms(cohort);
                arraySetTicks = Stopwatch.GetTimestamp() - t; arrayBenchmarkN = cohort.Length;
            }
            finally { if (temp.isCreated) temp.Dispose(); }
        }
        private static bool Equal(Output a, Output b, out string field)
        {
            if (!Bits(a.Position.x,b.Position.x)) { field="position.x"; return false; }
            if (!Bits(a.Position.y,b.Position.y)) { field="position.y"; return false; }
            if (!Bits(a.Position.z,b.Position.z)) { field="position.z"; return false; }
            if (!Bits(a.Rotation.x,b.Rotation.x)) { field="rotation.x"; return false; }
            if (!Bits(a.Rotation.y,b.Rotation.y)) { field="rotation.y"; return false; }
            if (!Bits(a.Rotation.z,b.Rotation.z)) { field="rotation.z"; return false; }
            if (!Bits(a.Rotation.w,b.Rotation.w)) { field="rotation.w"; return false; }
            if (!Bits(a.Scale.x,b.Scale.x)) { field="localScale.x"; return false; }
            if (!Bits(a.Scale.y,b.Scale.y)) { field="localScale.y"; return false; }
            if (!Bits(a.Scale.z,b.Scale.z)) { field="localScale.z"; return false; }
            field=""; return true;
        }
        // seq/actual 불일치의 필드별 개수와, +0/-0 부호만 다른 것을 같다고 봤을 때 남는 불일치(진짜 값 차이) 개수
        private static readonly long[] fieldMis = new long[10], fieldMisNoZero = new long[10]; private static long realMis;
        private static readonly string[] fieldNames = { "pos.x","pos.y","pos.z","rot.x","rot.y","rot.z","rot.w","scale.x","scale.y","scale.z" };
        private static void FieldCount(Output a, Output b)
        {
            float[] x = { a.Position.x,a.Position.y,a.Position.z,a.Rotation.x,a.Rotation.y,a.Rotation.z,a.Rotation.w,a.Scale.x,a.Scale.y,a.Scale.z };
            float[] y = { b.Position.x,b.Position.y,b.Position.z,b.Rotation.x,b.Rotation.y,b.Rotation.z,b.Rotation.w,b.Scale.x,b.Scale.y,b.Scale.z };
            bool real = false;
            for (int k = 0; k < 10; k++)
            {
                if (Bits(x[k], y[k])) continue;
                fieldMis[k]++;
                if (x[k] != y[k]) { fieldMisNoZero[k]++; real = true; }   // float == 는 +0 == -0 이 참
            }
            if (real) realMis++;
            // Transform.rotation 설정자가 정규화하는지: 순차 계산 값을 정규화해 실제와 비교 (±0 은 같다고 봄)
            var q = Quaternion.Normalize(new Quaternion(a.Rotation.x, a.Rotation.y, a.Rotation.z, a.Rotation.w));
            float[] nq = { q.x, q.y, q.z, q.w }; float[] yq = { b.Rotation.x, b.Rotation.y, b.Rotation.z, b.Rotation.w };
            bool nreal = false; bool nbit = false;
            for (int k = 0; k < 4; k++) { if (nq[k] != yq[k]) { nreal = true; normMis[k]++; } if (!Bits(nq[k], yq[k])) nbit = true; }
            if (nreal) normRealMis++; if (nbit) normBitMis++;
        }
        private static readonly long[] normMis = new long[4]; private static long normRealMis, normBitMis;
        private static string FieldReport()
        {
            var sb = new System.Text.StringBuilder("seq/actual by field (bit | excluding +0/-0): ");
            for (int k = 0; k < 10; k++) sb.Append(fieldNames[k]).Append('=').Append(fieldMis[k]).Append('|').Append(fieldMisNoZero[k]).Append(' ');
            sb.Append("decos-with-real-value-difference=").Append(realMis);
            sb.Append(" | after Quaternion.Normalize(seq): real-difference decos=").Append(normRealMis).Append(" (x,y,z,w=").Append(normMis[0]).Append(',').Append(normMis[1]).Append(',').Append(normMis[2]).Append(',').Append(normMis[3]).Append(") bit-difference decos incl +0/-0=").Append(normBitMis);
            return sb.ToString();
        }
        private static bool Bits(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
        private static string V(float f) => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "/0x" + BitConverter.SingleToInt32Bits(f).ToString("X8");
        private static string Describe(Output v) => "pos="+V(v.Position.x)+","+V(v.Position.y)+","+V(v.Position.z)
            +" rot="+V(v.Rotation.x)+","+V(v.Rotation.y)+","+V(v.Rotation.z)+","+V(v.Rotation.w)
            +" scale="+V(v.Scale.x)+","+V(v.Scale.y)+","+V(v.Scale.z);
        private static void Remember(int i, string pair, string field, Output a, Output b)
        {
            if (first != "none") return;
            first = "frame="+Time.frameCount+" song="+Song().ToString("F3")+" index="+i+" deco="+selected[i].name+" pair="+pair+" field="+field+" A["+Describe(a)+"] B["+Describe(b)+"]";
        }
        private static string End()
        {
            Stop();
            double tk = Stopwatch.Frequency / 1000.0, f = Math.Max(1, frames);
            var sb = new StringBuilder();
            sb.AppendFormat("song={0:F3}..{1:F3} frames={2} compared={3} eligible/frame={4:F1} scanned/frame={5:F1}\n", firstSong,lastSong,frames,samples,samples/f,candidates/f);
            sb.AppendLine(FieldReport());
            sb.AppendFormat("bit-mismatch seq/job={0} seq/actual={1} job/actual={2} (+0/-0 distinguished); first={3}\n",seqJobMismatch,seqActualMismatch,jobActualMismatch,first);
            sb.AppendFormat("ms/frame selection={0:F4} gather={1:F4} seq={2:F4} Schedule={3:F4} Complete={4:F4} chunks-sum={5:F4} main-chunks={6:F4} worker-chunks={7:F4}\n",selectTicks/tk/f,gatherTicks/tk/f,seqTicks/tk/f,scheduleTicks/tk/f,completeTicks/tk/f,chunkTicks/tk/f,mainChunkTicks/tk/f,(chunkTicks-mainChunkTicks)/tk/f);
            sb.AppendFormat("gather-us/deco={0:F4} TAA-sync-ms/frame={1:F4} add={2} remove={3} allocation-ms/frame={4:F4} resizes={5} compare-ms/frame={6:F4} actual-read-ms/frame={7:F4}\n",gatherTicks/tk*1000/Math.Max(1,samples),syncTicks/tk/f,adds,removes,resizeTicks/tk/f,resizes,compareTicks/tk/f,actualTicks/tk/f);
            sb.AppendFormat("TAA changed-frames={0} ms/changed-frame={1:F4} stable-frames={2} ms/stable-frame={3:F4}\n", changedFrames, changedSyncTicks/tk/Math.Max(1,changedFrames), frames-changedFrames, (syncTicks-changedSyncTicks)/tk/Math.Max(1,frames-changedFrames));
            sb.AppendFormat("TAA-one-shot n={0} create+Add-ms={1:F4} SetTransforms-ms={2:F4}; workerCount={3} observedThreadIndices={4} mainIndex={5} maxImmediateParents={6}\n",arrayBenchmarkN,arrayBuildTicks/tk,arraySetTicks/tk,workers,string.Join(",",threads),mainThreadIndex,maxParents);
            sb.AppendFormat("MODEL lower-bound-input+Schedule={0:F4}ms; synchronous-input+Schedule+Complete+TAA={1:F4}ms/frame. Excludes writes and production selection; no speedup claim. stop={2}; FPS INVALID for A/B; includes cold first frame.", (gatherTicks+scheduleTicks)/tk/f,(gatherTicks+scheduleTicks+completeTicks+syncTicks)/tk/f,stopped);
            sb.AppendFormat("\nFULL shadow-selection+gather+Schedule+Complete+TAA={0:F4}ms/frame; verdict={1}", (selectTicks+gatherTicks+scheduleTicks+completeTicks+syncTicks)/tk/f, samples == 0 ? "NO SAMPLES" : "runtime comparison collected; inspect all mismatch counters");
            return sb.ToString();
        }
        internal static void Stop()
        {
            running = false;
            SceneManager.activeSceneChanged -= SceneChanged;
            Camera.onPreCull -= CheckLifetime;
            // Attempt unpatch even if Patch threw after partially installing.
            try { if (patched) { new Harmony(Id).UnpatchOwner(Id); patched = false; } }
            finally
            {
                if (scheduled) { pending.Complete(); scheduled = false; }
                if (access.isCreated) access.Dispose();
                DisposeArrays(); capacity = 0; owner = null;
                selected.Clear(); targets.Clear(); indices.Clear(); wanted.Clear(); parents.Clear();
            }
        }
        private static void DisposeArrays()
        {
            if (inputs.IsCreated) inputs.Dispose();
            if (sequential.IsCreated) sequential.Dispose();
            if (parallel.IsCreated) parallel.Dispose();
            if (actuals.IsCreated) actuals.Dispose();
            if (chunkTimes.IsCreated) chunkTimes.Dispose();
        }
    }
}
#endif
