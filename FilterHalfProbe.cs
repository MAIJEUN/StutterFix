#if DEV || AUTOTEST
using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
namespace StutterFix {
internal static class FilterHalfProbe {
 const string Id="StutterFix.FilterHalfProbe";static MethodInfo method;static bool nested;static int samples;static int frame;static double mse;static int maximum,controlMax;
 public sealed class Capture {public RenderTexture source,output,destination;public FieldInfo[] fields;public object[] values;public UnityEngine.Random.State random;}
 internal static void Command(string name){
 Stop();samples=maximum=controlMax=0;mse=0;
 if(name=="off")return;
 var t=AccessTools.TypeByName(name);if(t==null)throw new Exception("filter type missing: "+name);
 method=AccessTools.Method(t,"OnRenderImage",new[]{typeof(RenderTexture),typeof(RenderTexture)});
 new Harmony(Id).Patch(method,prefix:new HarmonyMethod(typeof(FilterHalfProbe),nameof(Before)),postfix:new HarmonyMethod(typeof(FilterHalfProbe),nameof(After)),finalizer:new HarmonyMethod(typeof(FilterHalfProbe),nameof(Final)));
 }
 internal static void Stop(){new Harmony(Id).UnpatchOwner(Id);}
 public static void Before(object __instance,RenderTexture __0,ref RenderTexture __1,out Capture __state){
 __state=null;if(nested||samples>=8||Time.frameCount-frame<90||__0==null)return;
 frame=Time.frameCount;var output=RenderTexture.GetTemporary(__0.descriptor);
 var f=__instance.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);var v=new object[f.Length];for(int i=0;i<f.Length;i++)v[i]=f[i].GetValue(__instance);
 __state=new Capture{source=__0,output=output,destination=__1,fields=f,values=v,random=UnityEngine.Random.state};__1=output;
 }
 static Color32[] Pixels(RenderTexture rt){
 var previous=RenderTexture.active;Texture2D t=null;
 try{RenderTexture.active=rt;t=new Texture2D(rt.width,rt.height,TextureFormat.RGBA32,false);t.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);t.Apply();return t.GetPixels32();}
 finally{RenderTexture.active=previous;if(t!=null)UnityEngine.Object.Destroy(t);}
 }
 public static void After(object __instance,Capture __state){
 if(__state==null)return;var c=__state;
 RenderTexture control=null,low=null,lowOut=null,up=null;
 var fields=c.fields;var postRandom=UnityEngine.Random.state;
 var values=new object[fields.Length];for(int i=0;i<fields.Length;i++)values[i]=fields[i].GetValue(__instance);
 try{
 Graphics.Blit(c.output,c.destination);nested=true;
 control=RenderTexture.GetTemporary(c.output.descriptor);
 for(int i=0;i<fields.Length;i++)if(!fields[i].IsInitOnly)fields[i].SetValue(__instance,c.values[i]);UnityEngine.Random.state=c.random;
 method.Invoke(__instance,new object[]{c.source,control});
 for(int i=0;i<fields.Length;i++)if(!fields[i].IsInitOnly)fields[i].SetValue(__instance,c.values[i]);UnityEngine.Random.state=c.random;
 var desc=c.source.descriptor;desc.width=Math.Max(1,desc.width/2);desc.height=Math.Max(1,desc.height/2);desc.depthBufferBits=0;
 low=RenderTexture.GetTemporary(desc);lowOut=RenderTexture.GetTemporary(desc);up=RenderTexture.GetTemporary(c.output.descriptor);
 Graphics.Blit(c.source,low);method.Invoke(__instance,new object[]{low,lowOut});Graphics.Blit(lowOut,up);
 var a=Pixels(c.output);var b=Pixels(up);var d=Pixels(control);
 int max=0,ctrl=0;double error=0;
 for(int i=0;i<a.Length;i++)for(int k=0;k<4;k++){int x=k==0?a[i].r:k==1?a[i].g:k==2?a[i].b:a[i].a;int y=k==0?b[i].r:k==1?b[i].g:k==2?b[i].b:b[i].a;int z=k==0?d[i].r:k==1?d[i].g:k==2?d[i].b:d[i].a;max=Math.Max(max,Math.Abs(x-y));ctrl=Math.Max(ctrl,Math.Abs(x-z));error+=(x-y)*(x-y);}
 double e=error/(a.Length*4.0);mse+=e;samples++;maximum=Math.Max(maximum,max);controlMax=Math.Max(controlMax,ctrl);
 Main.Entry.Logger.Log(FormattableString.Invariant($"[filterhalf] {__instance.GetType().Name} sample={samples} max={max} psnr={(e==0?double.PositiveInfinity:10*Math.Log10(255.0*255/e)):R} control_max={ctrl} size={c.source.width}x{c.source.height} cumulative_max={maximum} cumulative_psnr={(mse==0?double.PositiveInfinity:10*Math.Log10(255.0*255/(mse/samples))):R}"));
 }finally{nested=false;UnityEngine.Random.state=postRandom;for(int i=0;i<fields.Length;i++)if(!fields[i].IsInitOnly)fields[i].SetValue(__instance,values[i]);if(control!=null)RenderTexture.ReleaseTemporary(control);if(low!=null)RenderTexture.ReleaseTemporary(low);if(lowOut!=null)RenderTexture.ReleaseTemporary(lowOut);if(up!=null)RenderTexture.ReleaseTemporary(up);}
 }
 public static Exception Final(Exception __exception,Capture __state){if(__state!=null)RenderTexture.ReleaseTemporary(__state.output);return __exception;}
}
}
#endif
