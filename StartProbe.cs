using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace StutterFix
{
    // 곡 시작(3.6초 멈춤)과 맵 불러오기가 어디서 시간을 쓰는지 잰다.
    //
    // 곡 시작 멈춤은 한 프레임의 Update 3611ms 였고, 그 프레임에 장식 켜기/끄기 5062번,
    // 효과 걸러내기 5만 번, GC 2번이 있었다. 에디터에서 재생을 누르면 scnEditor.Play 가
    //   RemakePath(타일 경로 다시 만들기) -> ReloadAssets(장식/배경/곡) -> scnGame.Play(되감기, 효과 준비)
    // 를 한 프레임 안에 다 한다. IL 로 본 호출 순서대로 굵직한 함수들에 시간을 달아 둔다.
    // 이 함수들은 재생 시작/맵 로딩 때만 불리므로 평소 플레이에는 비용이 없다.
    // 가장 바깥 호출이 끝날 때 불린 순서대로 들여쓰기해서 한꺼번에 찍는다.
    public static class StartProbe
    {
        internal static float LogOverMs = 5f;

        private static readonly string[][] Targets =
        {
            new[] { "scnEditor", "Play" }, new[] { "scnEditor", "RemakePath" }, new[] { "scnEditor", "DrawFloorOffsetLines" },
            new[] { "scnEditor", "DrawHolds" }, new[] { "scnEditor", "DrawFloorNums" }, new[] { "scnEditor", "DrawMultiPlanet" },
            new[] { "scnEditor", "ClearFloorGlows" }, new[] { "scnEditor", "DeselectFloors" }, new[] { "scnEditor", "DeselectAllDecorations" },
            new[] { "scnEditor", "SwitchToEditMode" }, new[] { "scnEditor", "OpenLevel" }, new[] { "scnEditor", "OpenLevelCo" },
            new[] { "scnGame", "RemakePath" }, new[] { "scnGame", "ApplyEventsToFloors" }, new[] { "scnGame", "ReloadAssets" },
            new[] { "scnGame", "ReloadSong" }, new[] { "scnGame", "UpdateBackgroundSprites" }, new[] { "scnGame", "UpdateDecorationObjects" },
            new[] { "scnGame", "UpdateFloorSprites" }, new[] { "scnGame", "SetBackground" }, new[] { "scnGame", "UpdateVideo" },
            new[] { "scnGame", "ReloadCustomSounds" }, new[] { "scnGame", "Play" }, new[] { "scnGame", "FinishCustomLevelLoading" },
            new[] { "scnGame", "PrepVfx" }, new[] { "scnGame", "LoadLevel" },
            new[] { "ADOBase", "FlushUnusedMemory" },
            new[] { "LevelData", "LoadLevel" },
            new[] { "scrUIController", "LevelFinishedLoading" },
            new[] { "TextureManager", "Unload" }, new[] { "TextureManager", "MarkAllUnused" },
            new[] { "scrDecorationManager", "ResetDecorations" }, new[] { "scrDecorationManager", "ResetDecorationHitboxEvents" },
            new[] { "scrDecorationManager", "ShowEmptyDecorations" }, new[] { "scrDecorationManager", "ToggleClickableBoxColliderForLevelEditor" },
            new[] { "scrLevelMaker", "MakeLevel" }, new[] { "scrLevelMaker", "DrawHolds" }, new[] { "scrLevelMaker", "DrawMultiPlanet" },
            new[] { "scrLevelMaker", "CalculateFloorEntryTimes" }, new[] { "scrLevelMaker", "CalculateFloorAngleLengths" },
            new[] { "scrConductor", "SetupConductorWithLevelData" }, new[] { "scrConductor", "Rewind" }, new[] { "scrConductor", "Start" },
            new[] { "scrConductor", "StartMusic" },
            new[] { "scrController", "Awake_Rewind" }, new[] { "scrController", "Start_Rewind" }, new[] { "scrController", "ResetInputEventFfx" },
            new[] { "scrCamera", "Rewind" }, new[] { "scrCamera", "SetupRTCam" },
            new[] { "scrPlayer", "Rewind" }, new[] { "scrPlanet", "FirstFloorAngleSetup" }, new[] { "PlanetarySystem", "LoadPlanetColors" },
            new[] { "ffxSetDefaultText", "UpdateHudTexts" }, new[] { "AudioManager", "StopAllSounds" },
            new[] { "scrMistakesManager", "RevertToLastCheckpoint" },
            new[] { "DG.Tweening.DOTween", "KillAll" },
        };

        // 장식 수만큼(23만 번) 불리는 함수들. 한 번씩 기록하면 목록이 터지므로 이름별 합계만 센다.
        private static readonly string[][] HotTargets =
        {
            new[] { "scrDecorationManager", "CreateDecoration" }, new[] { "TextureManager", "GetOrAddSprite" },
            new[] { "TextureManager", "LoadTexture" }, new[] { "scnEditor", "UpdateImageLoadResult" },
            new[] { "scrDecoration", "Setup" }, new[] { "scrDecoration", "UpdateHitbox" },
            new[] { "scrDecorationManager", "TryAddDecorationToDictionary" }, new[] { "scnGame", "ApplyEvent" },
            new[] { "scrFloor", "SetTrackStyle" }, new[] { "scrFloor", "UpdateAngle" },
            new[] { "UnityEngine.Texture2D", "Apply" },
        };

        private class Hot { public int Count; public double Ms; }
        private static readonly Dictionary<string, Hot> hot = new Dictionary<string, Hot>();

        public static void HotPre(out long __state) { __state = Stopwatch.GetTimestamp(); }

        public static void HotPost(MethodBase __originalMethod, long __state)
        {
            if (depth == 0) return;   // 재생/로딩 밖에서 불린 것은 세지 않는다
            string name;
            if (!names.TryGetValue(__originalMethod, out name)) return;
            Hot h;
            if (!hot.TryGetValue(name, out h)) { h = new Hot(); hot[name] = h; }
            h.Count++;
            h.Ms += (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
        }

        private class Entry { public string Name; public int Depth; public double Ms; }
        private static readonly List<Entry> entries = new List<Entry>();
        private static int depth;
        private static readonly Dictionary<MethodBase, string> names = new Dictionary<MethodBase, string>();

        internal static void Install(Harmony harmony)
        {
            int n = 0;
            var pre = new HarmonyMethod(typeof(StartProbe), nameof(Pre));
            var post = new HarmonyMethod(typeof(StartProbe), nameof(Post));
            foreach (var t in Targets)
            {
                var type = AccessTools.TypeByName(t[0]);
                if (type == null) continue;
                foreach (var m in type.GetMethods(AccessTools.all))
                {
                    if (m.Name != t[1] || m.DeclaringType != type || m.IsAbstract || m.ContainsGenericParameters) continue;
                    try
                    {
                        harmony.Patch(m, prefix: pre, finalizer: post);
                        names[m] = type.Name + "." + m.Name;
                        n++;
                    }
                    catch { }
                }
            }
            var hpre = new HarmonyMethod(typeof(StartProbe), nameof(HotPre));
            var hpost = new HarmonyMethod(typeof(StartProbe), nameof(HotPost));
            int h = 0;
            foreach (var t in HotTargets)
            {
                var type = AccessTools.TypeByName(t[0]);
                if (type == null) continue;
                foreach (var m in type.GetMethods(AccessTools.all))
                {
                    if (m.Name != t[1] || m.DeclaringType != type || m.IsAbstract || m.ContainsGenericParameters) continue;
                    try
                    {
                        harmony.Patch(m, prefix: hpre, postfix: hpost);
                        names[m] = type.Name + "." + m.Name;
                        h++;
                    }
                    catch { }
                }
            }
            Main.Entry.Logger.Log("[시작시간] 함수 " + n + "개, 자주 불리는 함수 " + h + "개 감쌈");
        }

        public static void Pre(MethodBase __originalMethod, out object __state)
        {
            string name;
            names.TryGetValue(__originalMethod, out name);
            var e = new Entry { Name = name ?? __originalMethod.Name, Depth = depth };
            entries.Add(e);
            depth++;
            // 시작 시각과 그 순간까지의 GC 횟수를 같이 들고 간다
            __state = new long[] { Stopwatch.GetTimestamp(), GC.CollectionCount(0), entries.Count - 1 };
        }

        public static Exception Post(object __state, Exception __exception)
        {
            try
            {
                var s = (long[])__state;
                depth = Math.Max(0, depth - 1);
                var e = entries[(int)s[2]];
                e.Ms = (Stopwatch.GetTimestamp() - s[0]) * 1000.0 / Stopwatch.Frequency;
                long gcNow = GC.CollectionCount(0);
                if (gcNow != s[1]) e.Name += " (GC " + (gcNow - s[1]) + "번)";
                if (depth == 0) Flush();
            }
            catch { }
            return __exception;
        }

        private static void Flush()
        {
            if (entries.Count > 0 && entries[0].Ms >= LogOverMs)
            {
                var log = Main.Entry.Logger;
                foreach (var e in entries)
                {
                    if (e.Ms < LogOverMs) continue;
                    log.Log("[시작시간] " + new string(' ', e.Depth * 2) + e.Name + " " + e.Ms.ToString("F0") + "ms");
                }
                foreach (var kv in hot)
                    if (kv.Value.Ms >= LogOverMs)
                        log.Log("[시작시간]   (합계) " + kv.Key + " " + kv.Value.Count + "번 " + kv.Value.Ms.ToString("F0") + "ms");
            }
            entries.Clear();
            hot.Clear();
        }
    }
}
