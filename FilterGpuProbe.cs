#if DEV || AUTOTEST
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering;
namespace StutterFix {
internal sealed class FilterGpuProbe:MonoBehaviour {
 const string Id="StutterFix.FilterGpuProbe";
 [DllImport("filtergpu",CallingConvention=CallingConvention.Cdecl)] static extern IntPtr fg_event();
 [DllImport("filtergpu",CallingConvention=CallingConvention.Cdecl)] static extern ulong fg_stat(int t,int k);
 static FilterGpuProbe instance;static IntPtr callback;
 static readonly Dictionary<Type,int> ids=new Dictionary<Type,int>();
 static readonly List<string> names=new List<string>();
 static readonly long[] frames=new long[1024];static readonly int[] lastFrame=new int[1024];
 static readonly long[] distribution=new long[1024];
 static double totalSeconds;static readonly double[] seconds=new double[1024];static long totalFrames;static int frame=-1,enabledCount;
 CommandBuffer cb;
 static void Issue(int id,IntPtr texture){var c=instance.cb;c.Clear();c.IssuePluginEventAndData(callback,id,texture);Graphics.ExecuteCommandBuffer(c);}
 internal static void Command(string arg){
 switch(arg.Trim()){
 case "on":
 Stop();if(SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D11)throw new Exception("D3D11 required");
 ids.Clear();names.Clear();Array.Clear(frames,0,1024);Array.Clear(lastFrame,0,1024);Array.Clear(distribution,0,1024);totalFrames=0;totalSeconds=0;Array.Clear(seconds,0,1024);frame=-1;enabledCount=0;
 callback=fg_event();var go=new GameObject(Id);DontDestroyOnLoad(go);instance=go.AddComponent<FilterGpuProbe>();instance.cb=new CommandBuffer();Issue(4,IntPtr.Zero);
 var h=new Harmony(Id);
 foreach(var a in AppDomain.CurrentDomain.GetAssemblies()){
 Type[] types;try{types=a.GetTypes();}catch(ReflectionTypeLoadException e){types=e.Types;}
 foreach(var t in types){if(t==null||!t.Name.StartsWith("CameraFilterPack_"))continue;
 var m=t.GetMethod("OnRenderImage",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,new[]{typeof(RenderTexture),typeof(RenderTexture)},null);
 if(m==null||ids.ContainsKey(t))continue;if(names.Count==1024)throw new Exception("filter limit");
 ids[t]=names.Count;names.Add(t.Name);h.Patch(m,prefix:new HarmonyMethod(typeof(FilterGpuProbe),nameof(Before)),finalizer:new HarmonyMethod(typeof(FilterGpuProbe),nameof(After)));}}
 Main.Entry.Logger.Log("[filtergpu] on filters="+names.Count+" size="+Screen.width+"x"+Screen.height);break;
 case "state":Report();break;
 case "off":Stop();break;
 case "reset":if(instance==null)throw new Exception("filtergpu off");Issue(4,IntPtr.Zero);Array.Clear(frames,0,1024);Array.Clear(distribution,0,1024);totalFrames=0;totalSeconds=0;Array.Clear(seconds,0,1024);break;
 default:throw new Exception("filtergpu on|state|off|reset");}}
 public static void Before(object __instance,RenderTexture __0,out bool __state){
 __state=false;if(instance==null||__0==null||!Hitch.Playing)return;int id;if(!ids.TryGetValue(__instance.GetType(),out id))return;
 if(frame!=Time.frameCount){if(frame>=0)distribution[Math.Min(1023,enabledCount)]++;frame=Time.frameCount;enabledCount=0;}
 if(lastFrame[id]!=frame){lastFrame[id]=frame;frames[id]++;seconds[id]+=Time.unscaledDeltaTime;}enabledCount++;
 Issue(1024+id,__0.GetNativeTexturePtr());__state=true;}
 public static Exception After(Exception __exception,bool __state){if(__state&&instance!=null)Issue(2,IntPtr.Zero);return __exception;}
 void LateUpdate(){if(Hitch.Playing){totalFrames++;totalSeconds+=Time.unscaledDeltaTime;}}
 internal static void Report(){if(instance==null)return;
 for(int i=0;i<names.Count;i++){ulong n=fg_stat(i,0),ns=fg_stat(i,1);if(n>0)Main.Entry.Logger.Log(FormattableString.Invariant($"[filtergpu] {names[i]} samples={n} mean_us={(double)ns/n/1000:R} sum_ms={(double)ns/1e6:R} frames={frames[i]} total_frames={totalFrames} active_seconds={seconds[i]:R} song_seconds={totalSeconds:R}"));}
 for(int i=0;i<1024;i++)if(distribution[i]>0)Main.Entry.Logger.Log("[filtergpu] concurrent="+i+" frames="+distribution[i]);
 Main.Entry.Logger.Log("[filtergpu] skipped="+fg_stat(-1,0)+" invalid="+fg_stat(-1,1));}
 internal static void Stop(){new Harmony(Id).UnpatchOwner(Id);if(instance==null)return;Report();Issue(3,IntPtr.Zero);var r=instance;instance=null;r.cb.Release();Destroy(r.gameObject);}
}
}
#endif
