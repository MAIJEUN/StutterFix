using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace StutterFix
{
    // 같은 타일에 같은 스타일을 다시 입히면 건너뛴다.
    //
    // 타일 색 바꾸기(ffxRecolorFloorPlus) 한 번이 타일 6443개를 훑으며 최대 43ms를 쓴다.
    // 효과 하나짜리라 효과 몰림 나누기로는 쪼갤 수 없다.
    // 타일마다 하는 일 중 가장 무거운 것이 SetTrackStyle 이다.
    //   재질 값 8개 설정(텍스처 3, 수치 3, 색, 정수) + 도형 크기 + 자식 타일에 같은 일 반복
    // 이 맵의 색 바꾸기는 대부분 스타일은 그대로 두고 색만 바꾸므로,
    // 이미 같은 인자로 스타일을 입힌 타일에 다시 입히는 것은 결과가 똑같은 헛수고다.
    //
    // 타일(객체)마다 마지막으로 받은 인자를 기억해 두고, 모두 같으면 원래 함수를 부르지 않는다.
    // 기억은 ConditionalWeakTable 이라 맵을 다시 불러와 타일이 사라지면 같이 사라진다.
    public static class FloorFix
    {
        internal static bool Enabled = true;
        internal static long Skipped, Applied;

        private class Last { public MethodBase Method; public object[] Args; }
        private static readonly ConditionalWeakTable<object, Last> last = new ConditionalWeakTable<object, Last>();

        internal static void Install(Harmony harmony)
        {
            try
            {
                var floor = AccessTools.TypeByName("scrFloor");
                if (floor == null) return;
                int n = 0;
                foreach (var m in floor.GetMethods(AccessTools.all))
                {
                    if (m.Name != "SetTrackStyle" || m.DeclaringType != floor || m.IsAbstract) continue;
                    try
                    {
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(FloorFix), nameof(Prefix)));
                        n++;
                        Main.Entry.Logger.Log("patched scrFloor.SetTrackStyle(" +
                            string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name + " " + p.Name)) + ")");
                    }
                    catch (Exception ex) { Main.Entry.Logger.Error("SetTrackStyle 패치 실패: " + ex.Message); }
                }
            }
            catch (Exception ex) { Main.Entry.Logger.Error("floor fix 실패: " + ex.Message); }
        }

        public static bool Prefix(object __instance, MethodBase __originalMethod, object[] __args)
        {
            if (!Enabled || __instance == null) return true;
            try
            {
                Last prev;
                if (last.TryGetValue(__instance, out prev) && prev.Method == __originalMethod && SameArgs(prev.Args, __args))
                {
                    Skipped++;
                    return false;
                }

                // 인자 배열은 Harmony 가 재사용할 수 있으므로 복사해 둔다.
                var copy = (object[])__args.Clone();
                if (prev == null) last.Add(__instance, new Last { Method = __originalMethod, Args = copy });
                else { prev.Method = __originalMethod; prev.Args = copy; }
                Applied++;
            }
            catch { }
            return true;
        }

        private static bool SameArgs(object[] a, object[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (ReferenceEquals(a[i], b[i])) continue;
                if (a[i] == null || b[i] == null) return false;
                if (!a[i].Equals(b[i])) return false;   // 값 형식(색, 열거형, bool)은 값으로, 참조 형식은 같은 객체인지로 비교된다
            }
            return true;
        }
    }
}
