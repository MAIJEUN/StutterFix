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
        // true면 미루지 않고, 화면 밖 타일의 색 애니메이션만 없앤다 (가볍고 색 오류가 없다).
        internal static bool NoTweenMode = true;
        internal static float Margin = 2.5f;     // 화면 크기의 몇 배까지 "가깝다"고 볼지
        internal static long Deferred, Applied, Passed;
        internal static int LastRate;

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
        private static int scanTick;
        private static int callsThisSecond;
        private static float rateTimer;
        internal static bool heavyMode;
        internal static int HeavyThreshold = 2500;   // 초당 ColorFloor 호출 수 기준
        internal static int FrameThreshold = 120;    // 한 프레임에 이만큼 몰리면 즉시 개입
        private static int callsThisFrame;
        private static float lowSeconds;
        private static float lastSize = -1f;
        private static Vector3 lastPos;

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

            // 가벼운 구간에서는 색칠이 적어 미룰 이유가 없는데도 끼어들면 손해다.
            // 최근 호출량이 임계치를 넘을 때만 개입한다.
            callsThisSecond++;
            callsThisFrame++;

            // 1초마다 판정하면 무거운 구간 시작 후 최대 1초간 그대로 끊긴다.
            // 한 프레임에 호출이 몰리는 순간 바로 개입을 시작한다.
            if (!heavyMode && callsThisFrame >= FrameThreshold)
            {
                heavyMode = true;
                lowSeconds = 0f;
            }
            if (!heavyMode) return true;

            var comp = __instance as Component;
            if (comp == null) return true;

            if (IsNear(comp.transform.position))
            {
                Passed++;
                return true;                 // 화면 근처면 지금 칠한다
            }

            // 화면 밖이면 애니메이션이 어차피 보이지 않는다.
            // duration을 0으로 만들면 DOTween 트윈 생성이 사라져 호출 비용과 GC가 크게 준다.
            // 색 자체는 지금 적용되므로 나중에 틀린 색이 보일 일도 없다.
            if (NoTweenMode)
            {
                if (__args.Length > 7 && __args[7] is float && (float)__args[7] > 0f) __args[7] = 0f;
                Deferred++;
                return true;
            }

            // 화면 밖이면 마지막 값만 기억해둔다. 나중 이벤트가 덮어써도 최종 결과는 같다.
            int id = comp.GetInstanceID();
            Pending p;
            if (!pending.TryGetValue(id, out p))
            {
                p = new Pending { Floor = comp };
                pending[id] = p;
            }
            // 매번 새 배열을 만들면 15만 번의 쓰레기가 생겨 GC가 자주 돈다. 기존 배열에 덮어쓴다.
            if (p.Args == null || p.Args.Length != __args.Length) p.Args = new object[__args.Length];
            Array.Copy(__args, p.Args, __args.Length);
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
            callsThisFrame = 0;

            // 켜는 것은 즉시, 끄는 것은 천천히 한다. 경계에서 켜졌다 꺼졌다 하면 더 불안정해진다.
            rateTimer += dt;
            if (rateTimer >= 0.25f)
            {
                int rate = (int)(callsThisSecond / rateTimer);
                LastRate = rate;
                callsThisSecond = 0;
                rateTimer = 0f;

                if (rate >= HeavyThreshold) { heavyMode = true; lowSeconds = 0f; }
                else if (heavyMode)
                {
                    lowSeconds += 0.25f;
                    if (lowSeconds >= 2f) { heavyMode = false; FlushAll(); }
                }
            }

            if (pending.Count == 0 || colorFloor == null) return;

            cam = Camera.main;

            // 카메라가 줌아웃되거나 크게 움직이면 화면 밖이던 타일이 한꺼번에 들어온다.
            // 그때 조금씩만 칠하면 옛날 색이 잠깐 보이므로, 변화가 크면 즉시 전부 칠한다.
            bool cameraJumped = false;
            if (cam != null)
            {
                float size = cam.orthographicSize;
                Vector3 pos = cam.transform.position;
                if (Mathf.Abs(size - lastSize) > lastSize * 0.05f ||
                    (pos - lastPos).sqrMagnitude > size * size * 0.25f)
                {
                    cameraJumped = true;
                }
                lastSize = size;
                lastPos = pos;
            }

            // 평소에는 대기 목록 전체를 매 프레임 훑는 비용이 아까워 세 프레임에 한 번만 확인한다.
            if (!cameraJumped && ++scanTick < 3) return;
            scanTick = 0;
            ready.Clear();
            foreach (var kv in pending)
            {
                var p = kv.Value;
                if (p.Floor == null) { ready.Add(kv.Key); continue; }
                if (IsNear(p.Floor.transform.position)) ready.Add(kv.Key);
            }

            // 한 프레임에 너무 많이 몰리면 그 자체로 멈칫하므로 상한을 두되,
            // 카메라가 크게 바뀐 직후에는 색이 틀려 보이지 않도록 상한을 풀어준다.
            int cap = cameraJumped ? ready.Count : (ready.Count > 1500 ? 400 : 120);
            int limit = Math.Min(ready.Count, cap);
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
                    // 화면 밖에서 이미 지나간 변화라 애니메이션(duration)을 0으로 줘서 즉시 적용한다.
                    // DOTween 트윈을 새로 만드는 비용이 적용 비용의 대부분이었다.
                    invoker(floor, (TrackColorType)a[0], (Color)a[1], (Color)a[2], (float)a[3],
                        (TrackColorPulse)a[4], (float)a[5], (int)a[6], 0f, (DG.Tweening.Ease)a[8]);
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
        // 켤 때마다 0부터 세야 켠 상태와 끈 상태를 공정하게 비교할 수 있다.
        internal static void ResetStats()
        {
            Deferred = Applied = Passed = 0;
            LastRate = 0;
        }

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
                return "미룸 " + Deferred.ToString("N0") + " / 바로칠함 " + Passed.ToString("N0") +
                       " (" + (100.0 * Deferred / t).ToString("F0") + "% 절약), 나중적용 " + Applied.ToString("N0") +
                       ", 대기 " + pending.Count + "\n    초당 호출 " + LastRate.ToString("N0") + "회, 현재 " +
                       (heavyMode ? "무거운 구간 (개입 중)" : "가벼운 구간 (개입 안 함)");
            }
        }
    }
}
