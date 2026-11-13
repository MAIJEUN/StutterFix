#if DEV || AUTOTEST
using System;
using HarmonyLib;
using UnityEngine;
namespace StutterFix {
internal static class FilterHalfPerf {
 const string Id="StutterFix.FilterHalfPerf";static string activeName;static long calls,frames;static int lastFrame;
 public sealed class Capture {public RenderTexture source,output,destination;}
 internal static void Command(string name){
 Stop();if(name=="off")return;var t=AccessTools.TypeByName(name);if(t==null)throw new Exception("filter type missing");
 activeName=name;calls=frames=0;lastFrame=-1;new Harmony(Id).Patch(AccessTools.Method(t,"OnRenderImage",new[]{typeof(RenderTexture),typeof(RenderTexture)}),prefix:new HarmonyMethod(typeof(FilterHalfPerf),nameof(Before)),postfix:new HarmonyMethod(typeof(FilterHalfPerf),nameof(After)),finalizer:new HarmonyMethod(typeof(FilterHalfPerf),nameof(Final)));
 }
 internal static void Stop(){if(activeName!=null){Main.Entry.Logger.Log("[filterhalfperf] "+activeName+" calls="+calls+" frames="+frames);activeName=null;}new Harmony(Id).UnpatchOwner(Id);}
 public static void Before(ref RenderTexture __0,ref RenderTexture __1,out Capture __state){
 __state=null;if(__0==null)return;if(Hitch.Playing){calls++;if(lastFrame!=Time.frameCount){lastFrame=Time.frameCount;frames++;}}var desc=__0.descriptor;desc.width=Math.Max(1,desc.width/2);desc.height=Math.Max(1,desc.height/2);desc.depthBufferBits=0;
 var a=RenderTexture.GetTemporary(desc);var b=RenderTexture.GetTemporary(desc);
 __state=new Capture{source=a,output=b,destination=__1};Graphics.Blit(__0,a);__0=a;__1=b;
 }
 public static void After(Capture __state){if(__state!=null)Graphics.Blit(__state.output,__state.destination);}
 public static Exception Final(Exception __exception,Capture __state){if(__state!=null){RenderTexture.ReleaseTemporary(__state.source);RenderTexture.ReleaseTemporary(__state.output);}return __exception;}
}
}
#endif
