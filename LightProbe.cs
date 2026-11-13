#if AUTOTEST
using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace StutterFix
{
    // 진단 전용(AUTOTEST): 메인 스레드의 큰 단계를 통째로 건너뛰어 그 단계의 최대 비용을 잰다. 화면·동작은 틀려진다.
    // gpudeco light skip <이름> on|off
    internal static class LightProbe
    {
        static Harmony h;
        static readonly Dictionary<MethodBase, string> names = new Dictionary<MethodBase, string>();
        static readonly Dictionary<string, bool> skip = new Dictionary<string, bool>();

        static void Add(Type t, string method, string key)
        {
            var m = t == null ? null : (AccessTools.DeclaredMethod(t, method) ?? AccessTools.Method(t, method));
            if (m == null || m.IsAbstract) { Main.Entry.Logger.Log("[LightProbe] 없음: " + key); return; }
            try
            {
                names[m] = key;
                h.Patch(m, prefix: new HarmonyMethod(typeof(LightProbe), nameof(Prefix)) { priority = Priority.First });
            }
            catch (Exception e) { names.Remove(m); Main.Entry.Logger.Log("[LightProbe] 패치 실패 " + key + ": " + e.Message.Substring(0, Math.Min(80, e.Message.Length))); }
        }

        static void Install()
        {
            if (h != null) return;
            h = new Harmony("StutterFix.LightProbe");
            Add(typeof(scrDecorationManager), "LateUpdate", "decomgr");
            Add(typeof(scrFloor), "Update", "floorupdate");
            Add(typeof(scrFloor), "LateUpdate", "floorlate");
            Add(typeof(scrVfxPlus), "Update", "vfx");
            Add(AccessTools.TypeByName("DG.Tweening.Core.DOTweenComponent"), "Update", "dotween");
            // 타일 쪽 진단(타일이 많은 맵용): 메시 갱신·스타일·색·타일 이동 적용
            Add(AccessTools.TypeByName("FloorMesh"), "UpdateAllRequired", "tilemeshflush");
            Add(typeof(scrFloor), "SetTrackStyle", "tilestyle");
            Add(typeof(scrFloor), "ColorFloor", "tilecolor");
            Add(typeof(FloorAnim), "Apply", "tileanimapply");
            foreach(var kv in new[]{("ffxMoveDecorationsPlus","movedeco"),("ffxMoveFloorPlus","movefloor"),("ffxSetFilterAdvancedPlus","filteradv"),("ffxSetFilterPlus","filter"),
                ("ffxMoveCameraPlus","camera"),("ffxFlashPlus","flash"),("ffxCustomBackgroundPlus","bg"),("ffxSetObjectPlus","setobj"),("ffxSetHitsound","hitsound"),("ffxBloomPlus","bloom")})
                Add(AccessTools.TypeByName(kv.Item1), "StartEffect", kv.Item2);
        }

        // StartEffect 전체 시간(원래 코드로 도는 호출 포함). light measure on|off
        internal static long MdTicks, MdCalls;
        static MethodBase mdMethod;
        static bool MdPrefix(out long __state) { __state = System.Diagnostics.Stopwatch.GetTimestamp(); return true; }
        static void MdPostfix(long __state) { MdTicks += System.Diagnostics.Stopwatch.GetTimestamp() - __state; MdCalls++; }
        internal static void Measure(bool on)
        {
            Install();
            if (on && mdMethod == null)
            {
                var m = AccessTools.DeclaredMethod(typeof(ffxMoveDecorationsPlus), "StartEffect");
                h.Patch(m, prefix: new HarmonyMethod(typeof(LightProbe), nameof(MdPrefix)) { priority = Priority.First + 1 }, postfix: new HarmonyMethod(typeof(LightProbe), nameof(MdPostfix)) { priority = Priority.Last });
                mdMethod = m;
            }
        }
        static bool Prefix(MethodBase __originalMethod)
        {
            string k;
            return !(names.TryGetValue(__originalMethod, out k) && skip.TryGetValue(k, out bool on) && on);
        }

        internal static void Set(string key, bool on)
        {
            Install();
            skip[key] = on;
            Main.Entry.Logger.Log("[LightProbe] " + key + "=" + on);
        }

        internal static void Uninstall()
        {
            try { if (h != null) h.UnpatchOwner("StutterFix.LightProbe"); } catch { }
            h = null; names.Clear(); skip.Clear();
        }
    }
}
#endif
