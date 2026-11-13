extern alias melon;
using System.IO;
using UnityEngine;
using UnityModManagerNet;
using melon::MelonLoader;
using melon::MelonLoader.Utils;

[assembly: MelonInfo(typeof(StutterFix.MelonEntry), "StutterFix", StutterFix.MelonEntry.ModVersion, "pding4569")]
[assembly: MelonGame("7th Beat Games", "A Dance of Fire and Ice")]
// 패치는 Main.Load 가 직접 건다
[assembly: HarmonyDontPatchAll]

namespace StutterFix
{
    // MelonLoader 빌드: UMM 이 부르던 Load·OnUpdate·OnLateUpdate·OnFixedUpdate·OnUnload 를 MelonLoader 가 대신 부른다.
    // 설정 창은 원래도 단축키로 여는 따로 뜨는 창이라 그대로 쓴다.
    public sealed class MelonEntry : MelonMod
    {
        public const string ModVersion = "2.8.1";
        private UnityModManager.ModEntry entry;

        public override void OnInitializeMelon()
        {
            string dir = Path.Combine(MelonEnvironment.UserDataDirectory, "StutterFix");
            Directory.CreateDirectory(dir);
            var info = new UnityModManager.ModInfo { Id = "StutterFix", DisplayName = "Stutter Fix", Author = "pding4569", Version = ModVersion, AssemblyName = "StutterFix.dll" };
            entry = new UnityModManager.ModEntry(info, dir + Path.DirectorySeparatorChar, LoggerInstance);
            if (!Main.Load(entry)) { LoggerInstance.Error("불러오기 실패"); entry = null; }
        }

        public override void OnUpdate() { var e = entry; if (e != null && e.Active && e.OnUpdate != null) e.OnUpdate(e, Time.deltaTime); }
        public override void OnLateUpdate() { var e = entry; if (e != null && e.Active && e.OnLateUpdate != null) e.OnLateUpdate(e, Time.deltaTime); }
        public override void OnFixedUpdate() { var e = entry; if (e != null && e.Active && e.OnFixedUpdate != null) e.OnFixedUpdate(e, Time.fixedDeltaTime); }

        public override void OnDeinitializeMelon()
        {
            var e = entry; entry = null;
            if (e != null && e.OnUnload != null) { try { e.OnUnload(e); } catch { } }
        }
    }
}
