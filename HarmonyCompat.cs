using HarmonyLib;

namespace StutterFix
{
    // 이 아이디로 건 패치를 전부 푼다. UMM 의 Harmony 는 인스턴스 UnpatchAll(id), MelonLoader 의 HarmonyX 는 정적 UnpatchID(id).
    internal static class HarmonyCompat
    {
        internal static void UnpatchOwner(this Harmony h, string id)
        {
#if MELON
            Harmony.UnpatchID(id);
#else
            h.UnpatchAll(id);
#endif
        }
    }
}
