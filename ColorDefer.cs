using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace StutterFix
{
    // 화면 밖 타일의 색칠을 미뤘다가, 화면에 가까워질 때 한 번만 칠한다.
    //
    // 측정 근거: 무거운 구간에서 scrFloor.ColorFloor 가 초당 18,500회 호출되는데
    // (색 변경 이벤트 1회가 타일 545개를 칠하고 초당 34회 발생),
    // 실제로 화면에 보이는 타일은 6,242개 중 44~272개뿐이었다.
    // 같은 인자로 다시 칠하는 중복은 0%라 중복 제거는 효과가 없었다.
    //
    // 미뤄둔 색은 타일이 카메라 범위에 들어오는 순간 적용하므로 최종 화면은 같아야 한다.
    // 다만 색이 서서히 변하는 연출(tween)은 화면 밖에서 진행되지 않고 들어올 때 최종값으로 적용된다.
    public static class ColorDefer
    {
        internal static bool Enabled;
        internal static float Margin = 2.0f;     // 화면 크기의 몇 배까지 "가깝다"고 볼지
        internal static long Deferred, Applied, Passed;

        private class Pending
        {
            public Component Floor;
            public object[] Args;
        }

        private static readonly Dictionary<int, Pending> pending = new Dictionary<int, Pending>();
        private static readonly List<int> ready = new List<int>();
        private static MethodInfo colorFloor;
        private static Harmony harmony;
        private static bool patched;
        private static bool reentry;
        private static Camera cam;

        internal static void Install()
        {
            if (patched) return;
            try
            {
                harmony = new Harmony("StutterFix.ColorDefer");
                var type = AccessTools.TypeByName("scrFloor");
                foreach (var m in type.GetMethods(AccessTools.all))
                {
                    if (m.Name != "ColorFloor" || m.ContainsGenericParameters) continue;
                    colorFloor = m;
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(ColorDefer), nameof(Prefix)));
                    patched = true;
                }
                Main.Entry.Logger.Log("color defer patched: " + patched);
            }
            catch (Exception ex)
            {
                Main.Entry.Logger.Error("color defer patch failed: " + ex.Message);
            }
        }

        public static bool Prefix(object __instance, object[] __args)
        {
            if (!Enabled || reentry) return true;

            var comp = __instance as Component;
            if (comp == null) return true;

            if (IsNear(comp.transform.position))
            {
                Passed++;
                return true;                 // 화면 근처면 지금 칠한다
            }

            // 화면 밖이면 마지막 값만 기억해둔다. 나중 이벤트가 덮어써도 최종 결과는 같다.
            int id = comp.GetInstanceID();
            Pending p;
            if (!pending.TryGetValue(id, out p))
            {
                p = new Pending { Floor = comp };
                pending[id] = p;
            }
            p.Args = (object[])__args.Clone();
            Deferred++;
            return false;                    // 원래 색칠을 건너뛴다
        }

        private static bool IsNear(Vector3 p)
        {
            if (cam == null) cam = Camera.main;
            if (cam == null) return true;    // 카메라를 모르면 안전하게 그냥 칠한다

            float h = cam.orthographicSize * Margin;
            float w = h * cam.aspect;
            Vector3 c = cam.transform.position;
            return Math.Abs(p.x - c.x) <= w && Math.Abs(p.y - c.y) <= h;
        }

        internal static void Tick(float dt)
        {
            if (!Enabled)
            {
                if (pending.Count > 0) FlushAll();
                return;
            }
            if (pending.Count == 0 || colorFloor == null) return;

            cam = Camera.main;
            ready.Clear();
            foreach (var kv in pending)
            {
                var p = kv.Value;
                if (p.Floor == null) { ready.Add(kv.Key); continue; }
                if (IsNear(p.Floor.transform.position)) ready.Add(kv.Key);
            }

            // 한 프레임에 너무 많이 몰리면 그 자체로 멈칫하므로 상한을 둔다.
            int limit = Math.Min(ready.Count, 150);
            for (int i = 0; i < limit; i++)
            {
                Apply(ready[i]);
            }
        }

        // 리플렉션 Invoke 는 직접 호출보다 수십 배 느려서 평균 FPS를 깎았다.
        // 게임 어셈블리를 참조해 타입이 맞는 델리게이트로 직접 호출한다.
        private delegate void ColorFloorDelegate(
            scrFloor floor, TrackColorType colorType, Color color1, Color color2, float opacity,
            TrackColorPulse pulse, float pulseLength, int styleIndex, float duration, DG.Tweening.Ease ease);

        private static ColorFloorDelegate invoker;

        private static void Apply(int id)
        {
            Pending p;
            if (!pending.TryGetValue(id, out p)) return;
            pending.Remove(id);
            if (p.Floor == null || p.Args == null) return;

            var floor = p.Floor as scrFloor;
            if (floor == null) return;

            if (invoker == null)
            {
                try
                {
                    invoker = (ColorFloorDelegate)Delegate.CreateDelegate(typeof(ColorFloorDelegate), colorFloor);
                }
                catch (Exception ex)
                {
                    Main.Entry.Logger.Error("delegate 생성 실패, 리플렉션으로 대체: " + ex.Message);
                }
            }

            var a = p.Args;
            reentry = true;
            try
            {
                if (invoker != null)
                {
                    invoker(floor, (TrackColorType)a[0], (Color)a[1], (Color)a[2], (float)a[3],
                        (TrackColorPulse)a[4], (float)a[5], (int)a[6], (float)a[7], (DG.Tweening.Ease)a[8]);
                }
                else
                {
                    colorFloor.Invoke(p.Floor, a);
                }
                Applied++;
            }
            catch { }
            finally { reentry = false; }
        }

        // 기능을 끌 때는 미뤄둔 색을 전부 적용해서 화면을 원래 상태로 되돌린다.
        internal static void FlushAll()
        {
            var ids = new List<int>(pending.Keys);
            foreach (var id in ids) Apply(id);
            pending.Clear();
        }

        internal static string Status
        {
            get
            {
                long t = Deferred + Passed;
                if (t == 0) return "(호출 없음)";
                return $"미룸 {Deferred:N0} / 바로칠함 {Passed:N0} ({100.0 * Deferred / t:F0}% 절약), 나중적용 {Applied:N0}, 대기 {pending.Count}";
            }
        }
    }
}
