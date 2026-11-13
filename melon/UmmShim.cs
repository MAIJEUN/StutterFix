extern alias melon;
using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

// MelonLoader 빌드 전용: 소스가 쓰는 UMM API 를 같은 이름으로 흉내 낸다 (UnityModManager.dll 은 MelonLoader 에 없다).
// 로그는 MelonLoader 로그로, 설정은 UMM 과 같은 Settings.xml 형식으로 UserData/StutterFix 에 둔다.
namespace UnityModManagerNet
{
    public static class UnityModManager
    {
        public static readonly List<ModEntry> modEntries = new List<ModEntry>();   // 다른 UMM 모드는 없다
        public static ModEntry FindMod(string id) { return null; }

        public static class UI { }

        public class ModInfo
        {
            public string Id, DisplayName, Author, Version, ManagerVersion, AssemblyName, EntryMethod, HomePage, Repository;
        }

        public class ModEntry
        {
            public readonly ModInfo Info;
            public readonly string Path;
            public readonly ModLogger Logger;
            public bool Enabled = true, Active = true;
            public Action<ModEntry> OnGUI, OnSaveGUI, OnShowGUI, OnHideGUI;
            public Action<ModEntry, float> OnUpdate, OnLateUpdate, OnFixedUpdate;
            public Func<ModEntry, bool, bool> OnToggle;
            public Func<ModEntry, bool> OnUnload;

            public ModEntry(ModInfo info, string path, melon::MelonLoader.MelonLogger.Instance log)
            {
                Info = info; Path = path; Logger = new ModLogger(log);
            }

            public class ModLogger
            {
                private readonly melon::MelonLoader.MelonLogger.Instance log;
                public ModLogger(melon::MelonLoader.MelonLogger.Instance log) { this.log = log; }
                public void Log(string s) { log.Msg(s); }
                public void NativeLog(string s) { log.Msg(s); }
                public void Warning(string s) { log.Warning(s); }
                public void Error(string s) { log.Error(s); }
                public void Critical(string s) { log.Error(s); }
                public void LogException(Exception e) { log.Error(e.ToString()); }
                public void LogException(string key, Exception e) { log.Error(key + ": " + e); }
            }
        }

        public class ModSettings
        {
            public virtual string GetPath(ModEntry modEntry) { return System.IO.Path.Combine(modEntry.Path, "Settings.xml"); }
            public virtual void Save(ModEntry modEntry) { Save(this, modEntry); }

            public static void Save<T>(T data, ModEntry modEntry) where T : ModSettings, new()
            {
                string path = data.GetPath(modEntry), tmp = path + ".tmp";
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    using (var w = new StreamWriter(tmp)) new XmlSerializer(typeof(T)).Serialize(w, data);
                    if (File.Exists(path)) File.Delete(path);
                    File.Move(tmp, path);
                }
                catch (Exception e) { modEntry.Logger.Error("설정 저장 실패: " + e.Message); }
            }

            public static T Load<T>(ModEntry modEntry) where T : ModSettings, new()
            {
                var t = new T();
                string path = t.GetPath(modEntry);
                if (!File.Exists(path)) return t;
                try { using (var r = File.OpenRead(path)) return (T)new XmlSerializer(typeof(T)).Deserialize(r); }
                catch (Exception e) { modEntry.Logger.Error("설정 읽기 실패, 기본값 사용: " + e.Message); return t; }
            }
        }
    }
}
