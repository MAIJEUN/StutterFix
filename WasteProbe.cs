#if DEV || AUTOTEST
using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace StutterFix
{
    // Unity diagnostic sampling. No allocation/reflection/locks in the sampled path.
    internal static class WasteProbe
    {
        private const string Id = "StutterFix.WasteProbe";
        private static readonly string[] Names = { "scrPlanet.Update", "scrBgbar.Update", "scnGame.Update", "scrCamera.Update", "scrFlash.Update", "scrTextBonusColor.Update", "scrConductor.Update", "PlanetSprite.LateUpdate", "TileFlash.Update", "scrDecorationManager.UpdateHitboxSizes" };
        private static readonly long[] Calls = new long[10], Samples = new long[10], Ticks = new long[10];
        private static bool enabled, clockPlaying;
        private static long playingStart, playingTicks;
        private static int generation;

        internal struct Stamp { internal long Start; internal int Generation; }

        internal static void Tick(bool playing)
        {
            bool active = enabled && playing;
            if (active == clockPlaying) return;
            long now = Stopwatch.GetTimestamp();
            if (active) playingStart = now;
            else playingTicks += now - playingStart;
            clockPlaying = active;
        }

        internal static void Reset()
        {
            generation++;
            Array.Clear(Calls, 0, 10); Array.Clear(Samples, 0, 10); Array.Clear(Ticks, 0, 10);
            playingTicks = 0; clockPlaying = false;
            Tick(Hitch.Playing);
        }

        internal static void On()
        {
            if (enabled) return;
            if (Hitch.Playing) throw new InvalidOperationException("곡 밖(메뉴·게임 화면 열기 전)에서 wasteprobe on 을 실행하세요 (곡 중에는 패치를 못 겁니다).");
            var harmony = new Harmony(Id);
            try
            {
                for (int i = 0; i < Names.Length; i++)
                {
                    int dot = Names[i].LastIndexOf('.');
                    var type = AccessTools.TypeByName(Names[i].Substring(0, dot));
                    if (type == null) throw new MissingMemberException(Names[i]);
                    MethodInfo target = null;
                    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                        if (method.Name == Names[i].Substring(dot + 1))
                        {
                            if (target != null) throw new AmbiguousMatchException(Names[i]);
                            target = method;
                        }
                    if (target == null) throw new MissingMethodException(Names[i]);
                    harmony.Patch(target, new HarmonyMethod(typeof(WasteProbe), "Before" + i), new HarmonyMethod(typeof(WasteProbe), "After" + i));
                }
                enabled = true;
                Reset();
                Log("켬: 후보 10개 호출 수, 곡 중만, 시간 표본 1/64");
            }
            catch
            {
                harmony.UnpatchOwner(Id); enabled = false;
                throw;
            }
        }

        internal static void Off()
        {
            Tick(false);
            enabled = false; generation++;
            new Harmony(Id).UnpatchOwner(Id);
        }

        private static void Before(int index, out Stamp stamp)
        {
            stamp = default;
            if (!enabled || !Hitch.Playing) return;
            // First sample on call 64: less than 64 calls means no timing evidence.
            if ((++Calls[index] & 63) != 0) return;
            stamp.Generation = generation;
            stamp.Start = Stopwatch.GetTimestamp();
        }

        private static void After(int index, Stamp stamp)
        {
            if (stamp.Start == 0 || stamp.Generation != generation || !enabled) return;
            Ticks[index] += Stopwatch.GetTimestamp() - stamp.Start;
            Samples[index]++;
        }

        internal static void State()
        {
            Tick(Hitch.Playing);
            long elapsed = playingTicks + (clockPlaying ? Stopwatch.GetTimestamp() - playingStart : 0);
            double seconds = elapsed / (double)Stopwatch.Frequency;
            Log(string.Format("{0}, 곡 중 {1:F3}초, 시간 표본 1/64 (안쪽 호출 시간 포함, 합계는 추정)", enabled ? "켬" : "끔", seconds));
            for (int i = 0; i < 10; i++)
            {
                double mean = Samples[i] == 0 ? 0 : Ticks[i] * 1000000.0 / Stopwatch.Frequency / Samples[i];
                Log(string.Format("{0} 호출 {1}회 (초당 {2:F2}), 잰 표본 {3}개 평균 {4:F3} µs, 추정 합계 {5:F3} ms{6}",
                    Names[i], Calls[i], seconds > 0 ? Calls[i] / seconds : 0, Samples[i], mean, mean * Calls[i] / 1000,
                    Samples[i] == 0 ? " (표본 없음)" : ""));
            }
        }

        private static void Log(string text) { Main.Entry.Logger.Log("[낭비 측정] " + text); }
        private static void Before0(out Stamp __state) { Before(0, out __state); }
        private static void After0(Stamp __state) { After(0, __state); }
        private static void Before1(out Stamp __state) { Before(1, out __state); }
        private static void After1(Stamp __state) { After(1, __state); }
        private static void Before2(out Stamp __state) { Before(2, out __state); }
        private static void After2(Stamp __state) { After(2, __state); }
        private static void Before3(out Stamp __state) { Before(3, out __state); }
        private static void After3(Stamp __state) { After(3, __state); }
        private static void Before4(out Stamp __state) { Before(4, out __state); }
        private static void After4(Stamp __state) { After(4, __state); }
        private static void Before5(out Stamp __state) { Before(5, out __state); }
        private static void After5(Stamp __state) { After(5, __state); }
        private static void Before6(out Stamp __state) { Before(6, out __state); }
        private static void After6(Stamp __state) { After(6, __state); }
        private static void Before7(out Stamp __state) { Before(7, out __state); }
        private static void After7(Stamp __state) { After(7, __state); }
        private static void Before8(out Stamp __state) { Before(8, out __state); }
        private static void After8(Stamp __state) { After(8, __state); }
        private static void Before9(out Stamp __state) { Before(9, out __state); }
        private static void After9(Stamp __state) { After(9, __state); }
    }
}
#endif
