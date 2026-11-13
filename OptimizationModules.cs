using System;
using System.Collections.Generic;
using HarmonyLib;

namespace StutterFix
{
    internal interface IOptimizationModule
    {
        string Key {get;}
        void Install(Harmony harmony);
        void Uninstall();
    }
    internal static class OptimizationModules
    {
        private sealed class Entry{internal string Key;internal IOptimizationModule Module;internal Harmony Harmony;}
        private static readonly List<Entry> active=new List<Entry>();
        internal static int Count=>active.Count;
        internal static void Install(IOptimizationModule module)
        {
            Harmony h=null;string key="unknown";
            try
            {
                key=module.Key;
                if(string.IsNullOrEmpty(key))throw new ArgumentException("Missing module key");
                foreach(char c in key)if(!char.IsLetterOrDigit(c)&&c!='-')throw new ArgumentException("Invalid module key");
                foreach(var e in active)if(e.Key==key)return;
                h=new Harmony("StutterFix.optimization."+key);
                module.Install(h);active.Add(new Entry{Key=key,Module=module,Harmony=h});
            }
            catch(Exception ex)
            {
                try{module.Uninstall();}catch{}
                try{if(h!=null)h.UnpatchOwner(h.Id);}catch{}
                Main.Entry.Logger.Log("[최적화 모듈] "+key+" 설치 안 함: "+ex.Message);
            }
        }
        internal static void Uninstall(string key)
        {
            for(int i=active.Count-1;i>=0;i--)
            {
                var e=active[i];if(e.Key!=key)continue;
                try{e.Module.Uninstall();}catch(Exception ex){Main.Entry.Logger.Log("[최적화 모듈] 정리: "+ex.Message);}
                finally{try{e.Harmony.UnpatchOwner(e.Harmony.Id);}catch(Exception ex){Main.Entry.Logger.Log("[최적화 모듈] 패치 해제: "+ex.Message);}active.RemoveAt(i);}
            }
        }
        internal static void Shutdown(){while(active.Count>0)Uninstall(active[active.Count-1].Key);}
    }
    internal sealed class TagIndexModule:IOptimizationModule
    {
        internal static readonly TagIndexModule Instance=new TagIndexModule();
        public string Key=>"tags";
        public void Install(Harmony harmony)
        {
            if(!TagIndex.Install(harmony))throw new MissingMethodException("Tag preparation hook unavailable");
        }
        public void Uninstall(){TagIndex.Shutdown();}
    }
}
