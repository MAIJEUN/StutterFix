using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace StutterFix
{
    // Independent, opt-in replacement of ONLY scrConductor.hitSoundsData scheduling.
    // All Unity API calls stay on the main thread; workers receive immutable PCM only.
    internal static class HitSoundBatch
    {
        internal const string Owner = "StutterFix.hitsoundbatch";
        private const int PoolSize = 16, MaxGroups = 2, MaxUploads = 2, MaxVoices = 200000;
        private const long MaxUploadBytes = 768000; // Two 0.5s stereo buffers at 96kHz.
        private const long PcmLimit = 64L * 1024 * 1024;
        private static Harmony harmony;
        private static bool enabled, installed;
        private static Session current, retiring;
        private static string reason = "disabled";
        private static int captures, fallbacks, lastCount, lastUploads, peakUploads;
        private static int uploadFrame = -1, uploadsThisFrame;
        private static long uploadBytesThisFrame, lastUploadBytes, peakUploadBytes;
#if DEV || AUTOTEST
        private static string lastVerification = "no captured run";
#endif
        private static readonly AccessTools.FieldRef<scrConductor, IList> Data =
            AccessTools.FieldRefAccess<scrConductor, IList>("hitSoundsData");
        private static readonly AccessTools.FieldRef<scrConductor, int> Cursor =
            AccessTools.FieldRefAccess<scrConductor, int>("nextHitSoundToSchedule");
        private static readonly FieldInfo VoiceType = AccessTools.Field(
            AccessTools.Inner(typeof(scrConductor), "HitSoundsData"), "hitSound");
        private static readonly FieldInfo VoiceTime = AccessTools.Field(
            AccessTools.Inner(typeof(scrConductor), "HitSoundsData"), "time");
        private static readonly FieldInfo VoiceVolume = AccessTools.Field(
            AccessTools.Inner(typeof(scrConductor), "HitSoundsData"), "volume");

        internal static bool Enabled
        {
            get => enabled;
            set
            {
                if (enabled == value) return;
                enabled = value;
                if (!value) { Release("disabled", true); Compat.ReleaseHitSoundGuard(); }
                else if (installed) Compat.EnsureHitSoundGuard();
            }
        }

        private sealed class Session
        {
            internal scrConductor Conductor;
            internal IList Original;
            internal readonly List<FormatGroup> Formats = new List<FormatGroup>();
            internal AudioClip[] Inputs;
            internal double[] Times;
            internal float[] Volumes;
            internal double Origin, SongClock;
            internal float Pitch, HitVolume;
            internal AudioMixerGroup Group;
            internal int Rate, Frames, Segments, Requested = -1;
            internal long PcmBytes;
            internal GameObject Root;
            internal readonly AutoResetEvent Wake = new AutoResetEvent(false);
            internal Thread Worker, Verifier;
            internal volatile bool Cancel;
            internal volatile string Error;
            internal int Rendered, Scheduled;
#if DEV || AUTOTEST
            internal volatile string Verification = "not requested";
            internal int Verified;
            internal double MaxError;
            internal double MaxTimeErrorSamples;
            internal long Differences;
#endif
            internal bool Taken;
            internal int OutputRate;
        }
        private sealed class FormatGroup
        {
            internal int Channels, Next;
            internal HitSoundMix.Voice[] Voices;
            internal readonly List<int> Indices = new List<int>();
            internal AudioSource[] Sources;
            internal AudioClip[] Clips;
            internal double[] Until;
            internal readonly ConcurrentQueue<float[]> Free = new ConcurrentQueue<float[]>();
            internal readonly ConcurrentQueue<Rendered> Ready = new ConcurrentQueue<Rendered>();
#if DEV || AUTOTEST
            internal long[] ObservedHashes, ExpectedHashes;
#endif
        }
        private struct Rendered { internal int Index; internal float[] Pcm; }

        internal static void Install()
        {
            if (installed) return;
            harmony = new Harmony(Owner);
            try
            {
                harmony.Patch(AccessTools.Method(typeof(scrConductor), "PlayHitTimes"),
                    prefix: new HarmonyMethod(typeof(HitSoundBatch), nameof(BeforePrepare)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(HitSoundBatch), nameof(AfterPrepare)) { priority = Priority.First });
                harmony.Patch(AccessTools.Method(typeof(scrConductor), "Update"),
                    prefix: new HarmonyMethod(typeof(HitSoundBatch), nameof(BeforeUpdate)) { priority = Priority.First });
                PatchStop(typeof(AudioManager), "StopAllSounds", nameof(StopPrefix));
                PatchStop(typeof(scrConductor), "Rewind", nameof(StopPrefix));
                foreach (var name in new[] { "Restart", "OnPlayerDied", "FailAction", "Fail2Action", "QuitToMainMenu" })
                    PatchStop(typeof(scrController), name, nameof(StopPrefix));
                // 끝까지 깬 곡은 마지막 소리가 이어서 나야 하므로 남은 예약을 게임에 돌려준다.
                PatchStop(typeof(scrController), "OnLandOnPortal", nameof(PortalPrefix));
                PatchStop(typeof(scnEditor), "SwitchToEditMode", nameof(StopPrefix));
                SceneManager.activeSceneChanged += SceneChanged;
                AudioSettings.OnAudioConfigurationChanged += AudioChanged;
                installed = true;
                if (enabled) Compat.EnsureHitSoundGuard();
            }
            catch
            {
                harmony.UnpatchOwner(Owner);
                SceneManager.activeSceneChanged -= SceneChanged;
                AudioSettings.OnAudioConfigurationChanged -= AudioChanged;
                throw;
            }
        }

        private static void PatchStop(Type type, string name, string prefix)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (method.Name == name)
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(HitSoundBatch), prefix));
        }
        // 죽음/다시 하기/나가기: 이 판은 끝났으므로 아직 안 들려준 히트음을 게임에 돌려주면 안 된다.
        // (돌려주면 게임이 5초 앞 예약 창을 다시 채우고, 상태가 Fail 로 바뀌기 전 한 프레임 사이에 예약돼 죽은 뒤에도 소리가 난다. 2.8.1 수정)
        public static void StopPrefix() { Release("game stop/rebuild", true, false, false); }
        public static void PortalPrefix() { Release("level end", true, false); }
        private static void SceneChanged(Scene a, Scene b) { Release("scene changed", true, false); }
        private static void AudioChanged(bool changed) { Release("audio configuration changed", true, false); }
        public static void BeforePrepare(out bool __state)
        {
            Release("preparing", true, false);
            __state = enabled && Compat.EnsureHitSoundGuard();
        }

        public static void AfterPrepare(scrConductor __instance, bool __state)
        {
            if (!enabled) return;
            // A newly removed late-loaded Quartz postfix may still exist in THIS
            // invocation's old Harmony wrapper. Take over only on the next preparation.
            if (__state) { reason = "late Quartz detected: takeover deferred until next preparation"; return; }
            Session s = null;
            try
            {
                if (!Compat.HitSoundGuardReady) throw new NotSupportedException("Quartz guard unavailable");
                if (Volatile.Read(ref retiring) != null) throw new NotSupportedException("previous worker cleanup pending");
                s = Capture(__instance);
                current = s;
                s.Worker = new Thread(() => Work(s)) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "StutterFix hit PCM" };
                s.Requested = Math.Min(s.Segments - 1, 3);
                s.Worker.Start();
                s.Wake.Set();
                // Preparation only. Never wait for PCM in an Update.
                var deadline = Environment.TickCount + 250;
                while (!InitialReady(s) && s.Error == null && unchecked(Environment.TickCount - deadline) < 0)
                    Thread.Sleep(1);
                if (s.Error != null || !InitialReady(s))
                    throw new NotSupportedException(s.Error ?? "initial PCM timeout");
                if (s.Origin < AudioSettings.dspTime + 0.05) throw new NotSupportedException("insufficient initial DSP lead");
                Pump(s);
                foreach (var g in s.Formats)
                    if (g.Next == 0) throw new NotSupportedException("initial group schedule unavailable");
                Cursor(__instance) = s.Original.Count;
                s.Taken = true;
                captures++;
                reason = "captured";
            }
            catch (Exception ex)
            {
                if (s != null && current == null) current = s;
                Release("capture fallback: " + ex.Message, true);
                fallbacks++;
            }
        }

        private static bool Finite(double v) => !double.IsNaN(v) && !double.IsInfinity(v);
        private static Session Capture(scrConductor c)
        {
            var am = ADOBase.audioManager;
            var list = Data(c);
            lastCount = list?.Count ?? 0;
            if (!GCS.d_hitsounds || !c.isGameWorld || ADOBase.lm == null || c.song == null || am == null ||
                list == null || list.Count == 0 || list.Count > MaxVoices || Cursor(c) != 0)
                throw new NotSupportedException("no fresh supported hit list");
            foreach (var name in new[] { "Play", "_Play", "MakeSource" })
            {
                var info = Harmony.GetPatchInfo(AccessTools.Method(typeof(AudioManager), name));
                if (info != null && (info.Prefixes.Count != 0 || info.Postfixes.Count != 0 || info.Transpilers.Count != 0 || info.Finalizers.Count != 0))
                    throw new NotSupportedException("custom AudioManager path: " + name);
            }
            var prefab = am.audioSourcePrefab;
            // 유니티 AudioSource 기본값(playOnAwake=true, reverbZoneMix=1)은 클립 없이 만들어지고 장면에 AudioReverbZone 이 없으면 소리에 영향이 없다(2026-10-20 실게임 확인: 게임 프리팹이 기본값 그대로).
            if (prefab == null || prefab.loop || prefab.spatialBlend != 0 || prefab.spatialize || prefab.panStereo != 0 ||
                prefab.ignoreListenerPause || prefab.ignoreListenerVolume || UnityEngine.Object.FindObjectsOfType<AudioReverbZone>().Length != 0 ||
                prefab.gameObject.GetComponents<Component>().Length != 2 || prefab.transform.childCount != 0)
                throw new NotSupportedException("source has 3D/loop/pan/reverb/filter or custom components [" + (prefab == null ? "null" :
                    "playOnAwake=" + prefab.playOnAwake + " loop=" + prefab.loop + " spatialBlend=" + prefab.spatialBlend + " spatialize=" + prefab.spatialize +
                    " pan=" + prefab.panStereo + " ignorePause=" + prefab.ignoreListenerPause + " ignoreVolume=" + prefab.ignoreListenerVolume +
                    " reverb=" + prefab.reverbZoneMix + " comps=" + string.Join("+", System.Array.ConvertAll(prefab.gameObject.GetComponents<Component>(), x => x == null ? "null" : x.GetType().Name)) +
                    " children=" + prefab.transform.childCount) + "]");
            var s = new Session { Conductor = c, Original = list, Origin = double.PositiveInfinity,
                SongClock = c.dspTimeSong, Pitch = c.song.pitch, HitVolume = c.hitSoundVolume,
                Group = c.hitSoundGroup != null ? c.hitSoundGroup : am.fallbackMixerGroup,
                Inputs = new AudioClip[list.Count],
                Times = new double[list.Count], Volumes = new float[list.Count] };
            // Return partially created objects to the common exception/cleanup path.
            current = s;
            if (!Finite(s.Pitch) || s.Pitch <= 0 || !Finite(s.SongClock)) throw new NotSupportedException("invalid clock/pitch");
            var cache = new Dictionary<AudioClip, float[]>();
            double end = double.NegativeInfinity;
            for (int i = 0; i < list.Count; i++)
            {
                object item = list[i];
                double time = (double)VoiceTime.GetValue(item);
                float volume = (float)VoiceVolume.GetValue(item);
                if (!Finite(time) || !Finite(volume) || time < AudioSettings.dspTime || (i > 0 && time < s.Times[i - 1]))
                    throw new NotSupportedException("invalid/past/unsorted event");
                var clip = am.FindOrLoadAudioClip("snd" + (HitSound)VoiceType.GetValue(item));
                // 게임 히트음은 CompressedInMemory 이고 처음 쓰기 전에는 Unloaded 다(2026-10-20 실게임 확인). 스트리밍만 막고, 안 불러온 클립은 짧게 불러온다.
                if (clip != null && clip.loadType != AudioClipLoadType.Streaming && clip.loadState == AudioDataLoadState.Unloaded) clip.LoadAudioData();
                if (clip != null && clip.loadState == AudioDataLoadState.Loading)
                {
                    int loadDeadline = Environment.TickCount + 200;
                    while (clip.loadState == AudioDataLoadState.Loading && unchecked(Environment.TickCount - loadDeadline) < 0) Thread.Sleep(1);
                }
                if (clip == null || clip.loadType == AudioClipLoadType.Streaming || clip.loadState != AudioDataLoadState.Loaded ||
                    clip.samples <= 0 || clip.frequency < 8000 || clip.frequency > 96000 || clip.channels < 1 || clip.channels > 2 ||
                    (double)clip.samples / clip.frequency > 8)
                    throw new NotSupportedException("unreadable/streaming/compressed/long clip [" + (clip == null ? "null" : clip.name + " loadType=" + clip.loadType + " loadState=" + clip.loadState + " samples=" + clip.samples + " freq=" + clip.frequency + " ch=" + clip.channels + " sec=" + ((double)clip.samples / System.Math.Max(1, clip.frequency)).ToString("F2")) + "]");
                if (s.Rate == 0) s.Rate = clip.frequency;
                HitSoundMix.RequireFormat(s.Rate, clip.frequency, clip.channels);
                int groupIndex = -1;
                for (int gi = 0; gi < s.Formats.Count; gi++)
                    if (s.Formats[gi].Channels == clip.channels) { groupIndex = gi; break; }
                if (groupIndex < 0)
                {
                    if (s.Formats.Count >= MaxGroups) throw new NotSupportedException("format group limit");
                    groupIndex = s.Formats.Count;
                    s.Formats.Add(new FormatGroup { Channels = clip.channels });
                }
                s.Formats[groupIndex].Indices.Add(i);
                if (!cache.TryGetValue(clip, out var pcm))
                {
                    long count = (long)clip.samples * clip.channels;
                    if (cache.Count >= 64 || s.PcmBytes + count * 4 > PcmLimit) throw new NotSupportedException("PCM cache limit");
                    pcm = new float[(int)count];
                    if (!clip.GetData(pcm, 0)) throw new NotSupportedException("GetData failed");
                    foreach (float sample in pcm) if (!Finite(sample)) throw new NotSupportedException("nonfinite PCM");
                    cache.Add(clip, pcm); s.PcmBytes += count * 4;
                }
                s.Inputs[i] = clip; s.Times[i] = time; s.Volumes[i] = volume;
                s.Origin = Math.Min(s.Origin, time);
                end = Math.Max(end, time + (double)clip.samples / clip.frequency);
            }
            s.Frames = s.Rate / 2;
            s.Origin -= (double)HitSoundMix.LeadFrames / s.Rate;
            if (end - s.Origin > 7200) throw new NotSupportedException("duration limit (2 hours)");
            long total = 0;
            foreach (var g in s.Formats)
            {
                g.Voices = new HitSoundMix.Voice[g.Indices.Count];
                for (int j = 0; j < g.Voices.Length; j++)
                {
                    int i = g.Indices[j];
                    var v = new HitSoundMix.Voice { Start = HitSoundMix.Sample(s.Times[i] - s.Origin, s.Rate),
                        Pcm = cache[s.Inputs[i]], Gain = Mathf.Clamp01(s.Volumes[i]) };
                    g.Voices[j] = v;
                    total = Math.Max(total, v.Start + v.Pcm.Length / g.Channels);
                }
            }
            s.Segments = checked((int)((total + s.Frames - 1) / s.Frames));
            s.OutputRate = AudioSettings.outputSampleRate;
            int clipFrames = s.Frames + HitSoundMix.PadFrames;
            // Count managed reusable buffers AND native AudioClip PCM conservatively.
            long poolBytes = 0;
            foreach (var g in s.Formats) poolBytes += (long)PoolSize * clipFrames * g.Channels * 4 * 2;
            if (s.PcmBytes + poolBytes > PcmLimit) throw new NotSupportedException("PCM plus pool limit");
            s.PcmBytes += poolBytes;
            s.Root = new GameObject("StutterFix hit sound batch");
            s.Root.transform.SetParent(c.transform, false);
            foreach (var g in s.Formats)
            {
#if DEV || AUTOTEST
                g.ObservedHashes = new long[s.Segments]; g.ExpectedHashes = new long[s.Segments];
#endif
                g.Sources = new AudioSource[PoolSize]; g.Clips = new AudioClip[PoolSize]; g.Until = new double[PoolSize];
                for (int i = 0; i < PoolSize; i++)
                {
                    g.Sources[i] = Object.Instantiate(prefab, s.Root.transform);
                    g.Sources[i].Stop(); g.Sources[i].playOnAwake = false;
                    g.Sources[i].pitch = 1; g.Sources[i].volume = 1; g.Sources[i].priority = 128;
                    g.Sources[i].outputAudioMixerGroup = s.Group;
                    g.Clips[i] = AudioClip.Create("StutterFix PCM " + g.Channels + "ch " + i, clipFrames, g.Channels, s.Rate, false);
                    g.Sources[i].clip = g.Clips[i];
                    g.Free.Enqueue(new float[clipFrames * g.Channels]);
                }
            }
            return s;
        }

        private static bool InitialReady(Session s)
        {
            foreach (var g in s.Formats) if (g.Ready.Count < Math.Min(2, s.Segments)) return false;
            return true;
        }

        private static void Work(Session s)
        {
            try
            {
                int index = 0;
                Func<bool> cancelled = () => s.Cancel;
                while (!s.Cancel)
                {
                    if (index >= s.Segments) return;
                    bool available = true;
                    foreach (var g in s.Formats) if (g.Free.IsEmpty) available = false;
                    if (index > Volatile.Read(ref s.Requested) || !available) { s.Wake.WaitOne(); continue; }
                    foreach (var g in s.Formats)
                    {
                        if (!g.Free.TryDequeue(out var buffer)) throw new InvalidOperationException("group buffer unavailable");
                        HitSoundMix.RenderSegment(g.Voices, g.Channels, index, s.Segments, s.Frames, buffer, cancelled);
                        if (s.Cancel) return;
                        foreach (float value in buffer) if (!Finite(value)) throw new InvalidOperationException("nonfinite mix");
#if DEV || AUTOTEST
                        Volatile.Write(ref g.ObservedHashes[index], Fingerprint(buffer));
#endif
                        g.Ready.Enqueue(new Rendered { Index = index, Pcm = buffer });
                        Interlocked.Increment(ref s.Rendered);
                    }
                    index++;
                }
            }
            catch (Exception ex) { s.Error = ex.GetType().Name + ": " + ex.Message; }
        }

        public static void BeforeUpdate(scrConductor __instance)
        {
            var s = current;
            if (s == null || s.Conductor != __instance || !s.Taken) return;
            try
            {
                // 죽음·실패·완주 상태이거나 게임 월드가 아니면 남은 예약을 놓고 게임에 돌려주지 않는다(FailAction 이 Update 안 UpdateInput 에서 불리는 틈 보호).
                var runCtrl = ADOBase.controller;
                if (!__instance.isGameWorld || (runCtrl != null && (runCtrl.state == States.Fail || runCtrl.state == States.Fail2)))
                { Release("run ended", true, false, false); return; }
                if (!enabled || Data(__instance) != s.Original || s.Original.Count != s.Times.Length || __instance.song == null || __instance.song.pitch != s.Pitch ||
                    __instance.dspTimeSong != s.SongClock || __instance.hitSoundVolume != s.HitVolume || !GCS.d_hitsounds ||
                    (__instance.hitSoundGroup != null ? __instance.hitSoundGroup : ADOBase.audioManager.fallbackMixerGroup) != s.Group ||
                    AudioSettings.outputSampleRate != s.OutputRate || s.Error != null)
                { fallbacks++; Release("runtime condition changed: " + (s.Error ?? "clock/volume/pitch/list/group"), true); return; }
                // Listener pause freezes DSP scheduling, including partially played segments.
                if (!AudioListener.pause) Pump(s);
            }
            catch (Exception ex) { fallbacks++; Release("pump fallback: " + ex.Message, true); }
        }
        private static void Pump(Session s)
        {
            if (uploadFrame != Time.frameCount) { uploadFrame = Time.frameCount; uploadsThisFrame = 0; uploadBytesThisFrame = 0; }
            lastUploads = uploadsThisFrame;
            lastUploadBytes = uploadBytesThisFrame;
            double now = AudioSettings.dspTime;
            int request = Math.Min(s.Segments - 1, Math.Max(3, (int)Math.Floor((now + 6 - s.Origin) * s.Rate / s.Frames)));
            if (request > s.Requested) { Volatile.Write(ref s.Requested, request); s.Wake.Set(); }
            while (uploadsThisFrame < MaxUploads)
            {
                // Earliest segment first, then stable group order. Both formats use
                // the exact same integer sample boundary and DSP start expression.
                FormatGroup g = null;
                foreach (var candidate in s.Formats)
                    if (candidate.Next < s.Segments && (g == null || candidate.Next < g.Next)) g = candidate;
                if (g == null) break;
                double start = s.Origin + (double)g.Next * s.Frames / s.Rate;
                if (start > now + 6) break;
                // Fail before the deadline instead of silently omitting a late segment.
                if (start < now + 0.02) throw new InvalidOperationException("segment deadline missed");
                long bytes = (long)(s.Frames + HitSoundMix.PadFrames) * g.Channels * 4;
                if (uploadBytesThisFrame + bytes > MaxUploadBytes) break;
                int slot = g.Next % PoolSize;
                if (g.Until[slot] > now - 0.05) break;
                if (!g.Ready.TryDequeue(out var result)) break;
                if (result.Index != g.Next) throw new InvalidOperationException("segment order");
                g.Sources[slot].Stop();
                if (!g.Clips[slot].SetData(result.Pcm, 0)) throw new InvalidOperationException("SetData failed");
                g.Sources[slot].PlayScheduled(start);
                g.Until[slot] = start + (double)(s.Frames + HitSoundMix.PadFrames) / s.Rate;
                g.Free.Enqueue(result.Pcm); s.Wake.Set();
                g.Next++; s.Scheduled++; uploadsThisFrame++; lastUploads = uploadsThisFrame;
                uploadBytesThisFrame += bytes; lastUploadBytes = uploadBytesThisFrame;
            }
            peakUploads = Math.Max(peakUploads, lastUploads);
            peakUploadBytes = Math.Max(peakUploadBytes, lastUploadBytes);
        }

        private static void Release(string why, bool restore, bool replayTails = true, bool giveBack = true)
        {
            reason = why;
            var s = current; current = null;
            if (s == null) return;
#if DEV || AUTOTEST
            lastVerification = VerificationState(s) + " (snapshot at release: " + why + ")";
#endif
            s.Cancel = true; s.Wake.Set();
            try
            {
                foreach (var g in s.Formats)
                    if (g.Sources != null) foreach (var source in g.Sources) if (source != null) { source.Stop(); source.clip = null; source.enabled = false; } // Destroy 는 프레임 끝이라 예약 채널을 바로 끊는다
                if (s.Root != null) s.Root.SetActive(false);
                if (restore && s.Taken && s.Conductor != null && Data(s.Conductor) == s.Original)
                {
                double now = AudioSettings.dspTime;
                int first = s.Original.Count;
                if (giveBack)
                    for (int i = 0; i < s.Original.Count; i++)
                        if ((double)VoiceTime.GetValue(s.Original[i]) >= now) { first = i; break; }
                Cursor(s.Conductor) = first;
                // Rebuild only tails which started before handback. Future entries remain
                // the original game's responsibility; no game sources are stopped here.
                if (replayTails && s.Original.Count == s.Times.Length && GCS.d_hitsounds && ADOBase.audioManager != null)
                    for (int i = 0; i < first; i++)
                    {
                        long offset = HitSoundMix.Sample(now - s.Times[i], s.Inputs[i].frequency);
                        if (offset < 0 || offset >= s.Inputs[i].samples) continue;
                        object item = s.Original[i];
                        var source = AudioManager.Play("snd" + (HitSound)VoiceType.GetValue(item), now, s.Group, s.Volumes[i]);
                        if (source != null) source.timeSamples = (int)offset;
                    }
                }
            }
            catch (Exception ex) { reason += "; handback: " + ex.Message; }
            finally
            {
                if (s.Root != null) Object.Destroy(s.Root);
                foreach (var g in s.Formats)
                    if (g.Clips != null) foreach (var clip in g.Clips) if (clip != null) Object.Destroy(clip);
                // Cleanup waits outside the game thread, then drops every captured reference.
                retiring = s;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        if (s.Worker != null && (s.Worker.ThreadState & ThreadState.Unstarted) == 0) s.Worker.Join();
                        if (s.Verifier != null && (s.Verifier.ThreadState & ThreadState.Unstarted) == 0) s.Verifier.Join();
                    }
                    finally { s.Wake.Dispose(); Interlocked.CompareExchange(ref retiring, null, s); }
                });
            }
        }

#if DEV || AUTOTEST
        private static long Fingerprint(float[] pcm)
        {
            unchecked
            {
                ulong hash = 14695981039346656037UL;
                foreach (float sample in pcm) hash = (hash ^ (uint)BitConverter.SingleToInt32Bits(sample)) * 1099511628211UL;
                return hash == 0 ? 1 : (long)hash;
            }
        }
#endif

        internal static void Shutdown()
        {
            enabled = false;
            Release("unloaded", true);
            Compat.ReleaseHitSoundGuard();
            SceneManager.activeSceneChanged -= SceneChanged;
            AudioSettings.OnAudioConfigurationChanged -= AudioChanged;
            harmony?.UnpatchOwner(Owner);
            installed = false;
        }

#if DEV || AUTOTEST
        internal static string Command(string arg)
        {
            var s = current;
            if (arg == "compare") return "PCM comparison only: listener output mixes song/holds/UI/mixer; isolated two-pass recording is not implemented. Use verify; audible A/B remains required.";
            if (arg == "verify")
            {
                if (s == null || !s.Taken) return "verify unavailable: " + reason + "; last=" + lastVerification;
                if (s.Verifier == null)
                {
                    s.Verification = "running";
                    s.Verifier = new Thread(() => VerifyAll(s)) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "StutterFix PCM verify" };
                    s.Verifier.Start();
                }
                return VerificationState(s);
            }
            int used = 0, pool = 0;
            string formats = "";
            if (s != null) foreach (var g in s.Formats)
            {
                if (g.Until != null) foreach (var t in g.Until) if (t > AudioSettings.dspTime) used++;
                pool += g.Sources?.Length ?? 0;
                formats += (formats.Length == 0 ? "" : ",") + s.Rate + "Hz/" + g.Channels + "ch:next=" + g.Next;
            }
            return "enabled=" + enabled + " reason=" + reason + " list=" + lastCount + " captures=" + captures +
                " groups=" + (s?.Formats.Count ?? 0) + " formats=" + formats +
                " segments=" + (s?.Segments ?? 0) + " rendered=" + (s?.Rendered ?? 0) + " scheduled=" + (s?.Scheduled ?? 0) +
                " pool=" + used + "/" + pool + " SetData_last=" + lastUploads + " SetData_peak=" + peakUploads +
                " SetData_bytes_last=" + lastUploadBytes + " SetData_bytes_peak=" + peakUploadBytes +
                " fallbacks=" + fallbacks + " PCM_bytes=" + (s?.PcmBytes ?? 0) + " quartz_guard=" + Compat.HitSoundGuardState;
        }

        private static string VerificationState(Session s)
        {
            int checkedHashes = 0, mismatches = 0;
            foreach (var g in s.Formats)
            if (g.ObservedHashes != null) for (int i = 0; i < s.Segments; i++)
            {
                long observed = Volatile.Read(ref g.ObservedHashes[i]), expected = Volatile.Read(ref g.ExpectedHashes[i]);
                if (observed == 0 || expected == 0) continue;
                checkedHashes++; if (observed != expected) mismatches++;
            }
            return s.Verification + " group_segments=" + s.Verified + "/" + (s.Segments * s.Formats.Count) + " groups=" + s.Formats.Count + " max_error=" + s.MaxError + " differing_samples=" + s.Differences +
                " actual_PCM_hashes=" + checkedHashes + " hash_mismatches=" + mismatches + " pending_actual=" + (s.Segments * s.Formats.Count - checkedHashes) +
                " group_alignment_samples=0 (shared rate/origin/boundaries)" +
                " max_time_error_samples=" + s.MaxTimeErrorSamples + " (PCM quantization); audio-output timing unmeasured";
        }

        private static void VerifyAll(Session s)
        {
            try
            {
                foreach (var g in s.Formats)
                {
                var actual = new float[s.Frames * g.Channels];
                var expected = new float[actual.Length];
                var segmented = new float[(s.Frames + HitSoundMix.PadFrames) * g.Channels];
                // Reconstruct starts independently from captured original absolute times.
                var voices = new HitSoundMix.Voice[g.Voices.Length];
                for (int i = 0; i < voices.Length; i++)
                {
                    int original = g.Indices[i];
                    voices[i] = new HitSoundMix.Voice {
                        Start = checked((long)Math.Round((s.Times[original] - s.Origin) * s.Rate, MidpointRounding.AwayFromZero)),
                        Pcm = g.Voices[i].Pcm, Gain = Math.Max(0, Math.Min(1, s.Volumes[original])) };
                }
                for (int i = 0; i < voices.Length; i++)
                    s.MaxTimeErrorSamples = Math.Max(s.MaxTimeErrorSamples, Math.Abs(voices[i].Start - (s.Times[g.Indices[i]] - s.Origin) * s.Rate));
                Func<bool> cancelled = () => s.Cancel;
                for (int i = 0; i < s.Segments && !s.Cancel; i++)
                {
                    HitSoundMix.Render(g.Voices, g.Channels, (long)i * s.Frames, actual, cancelled);
                    double error = HitSoundMix.Verify(voices, g.Channels, (long)i * s.Frames, actual, expected, cancelled, out var diff);
                    HitSoundMix.RenderSegment(voices, g.Channels, i, s.Segments, s.Frames, segmented, cancelled);
                    Volatile.Write(ref g.ExpectedHashes[i], Fingerprint(segmented));
                    s.MaxError = Math.Max(s.MaxError, error); s.Differences += diff;
                    Interlocked.Increment(ref s.Verified);
                }
                }
                s.Verification = s.Cancel ? "cancelled" : "complete (pure PCM only)";
            }
            catch (Exception ex) { s.Verification = "failed: " + ex.Message; }
        }
#endif
    }
}
