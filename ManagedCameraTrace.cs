#if AUTOTEST
using System;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;
namespace StutterFix
{
    // Diagnostic only. Two existing managed functions, no Camera.Render hook,
    // GPU readback, texture pointer lookup or file IO during the song.
    internal static class ManagedCameraTrace
    {
        const string Id="StutterFix.managed-camera-trace";
        static Harmony harmony;
        static bool active;
        static int diagnosticFaults;
        struct Sample {internal long Count,Errors;internal double Sum,Max,Song;internal int Frame,Width,Height;internal bool Custom;internal float Rate;}
        static Sample[] samples;
        static AccessTools.FieldRef<scrCamera,RenderTexture> cameraRt;
        static readonly double[] low={160,280,310,328},high={175,298,328,340};
        static int Window(double song)
        {for(int i=0;i<low.Length;i++)if(song>=low[i] && song<high[i])return i;return -1;}
        public struct Stamp {internal long Tick;internal int Window;}
        internal static string Command(string command)
        {
            if(command=="off"){Stop();return "CAMERA_TRACE_OFF";}
            if(command=="report")return Report();
            if(command!="on")throw new ArgumentException("cameraframeprobe on|report|off");
            Stop();
            try
            {
                var render=AccessTools.DeclaredMethod(typeof(scrCamera),"UpdateCustomFrameRateScreen",Type.EmptyTypes);
                var set=AccessTools.DeclaredMethod(typeof(scrCamera),"SetCustomFrameRate",new[]{typeof(bool),typeof(float)});
                var field=AccessTools.Field(typeof(scrCamera),"camRT");
                if(render==null || set==null || field?.FieldType!=typeof(RenderTexture))return "CAMERA_TRACE_UNAVAILABLE signatures";
                cameraRt=AccessTools.FieldRefAccess<scrCamera,RenderTexture>(field);
                diagnosticFaults=0;samples=new Sample[low.Length*2];harmony=new Harmony(Id);
                var pre=new HarmonyMethod(typeof(ManagedCameraTrace),nameof(Before));
                harmony.Patch(render,prefix:pre,finalizer:new HarmonyMethod(typeof(ManagedCameraTrace),nameof(AfterRender)));
                harmony.Patch(set,prefix:pre,finalizer:new HarmonyMethod(typeof(ManagedCameraTrace),nameof(AfterSet)));
                active=true;
                return "CAMERA_TRACE_ON windows=160..175,280..298,310..328,328..340 clock=conductor_songposition_minusi methods=2 CPU_execution_time=False elapsed_includes_waits=True performance_acceptance=False";
            }
            catch(Exception e){Stop();return "CAMERA_TRACE_UNAVAILABLE "+e.GetType().Name;}
        }
        static void Before(out Stamp __state)
        {
            __state=default;
            try
            {
                if(!active || !Hitch.Playing)return;
                var c=scrConductor.instance;if(!c)return;
                int w=Window(c.songposition_minusi);if(w<0)return;
                __state.Window=w;__state.Tick=Stopwatch.GetTimestamp();
            }
            catch{diagnosticFaults++;active=false;}
        }
        static Exception AfterRender(scrCamera __instance,Stamp __state,Exception __exception)
        {Finish(__instance,__state,__exception,0);return __exception;}
        static Exception AfterSet(scrCamera __instance,Stamp __state,Exception __exception)
        {Finish(__instance,__state,__exception,1);return __exception;}
        static void Finish(scrCamera camera,Stamp stamp,Exception error,int method)
        {
            if(stamp.Tick==0)return;
            double ms=(Stopwatch.GetTimestamp()-stamp.Tick)*1000.0/Stopwatch.Frequency;
            try
            {
                int i=stamp.Window*2+method;var s=samples[i];s.Count++;s.Sum+=ms;if(error!=null)s.Errors++;
                if(ms>s.Max)
                {
                    s.Max=ms;s.Frame=Time.frameCount;s.Song=scrConductor.instance?scrConductor.instance.songposition_minusi:double.NaN;
                    s.Custom=camera.enableCustomFPS;s.Rate=camera.frameRate;var rt=cameraRt(camera);
                    s.Width=rt?rt.width:0;s.Height=rt?rt.height:0;
                }
                samples[i]=s;
            }
            catch{diagnosticFaults++;active=false;} // Diagnostics cannot throw from an engine finalizer.
        }
        static string Report()
        {
            var text=new StringBuilder("CAMERA_TRACE_REPORT active="+active+" diagnostic_faults="+diagnosticFaults+" elapsed_includes_waits=True");
            if(samples==null)return text.Append(" samples=UNAVAILABLE").ToString();
            var c=CultureInfo.InvariantCulture;
            for(int w=0;w<low.Length;w++)for(int m=0;m<2;m++)
            {
                var s=samples[w*2+m];text.Append("\nCAMERA_TRACE method=").Append(m==0?"UpdateCustomFrameRateScreen":"SetCustomFrameRate")
                    .Append(" from=").Append(low[w]).Append(" to=").Append(high[w]).Append(" calls=").Append(s.Count)
                    .Append(" errors=").Append(s.Errors).Append(" mean_ms=").Append((s.Sum/Math.Max(1,s.Count)).ToString("F6",c))
                    .Append(" max_ms=").Append(s.Max.ToString("F6",c)).Append(" max_frame=").Append(s.Frame)
                    .Append(" max_conductor_song=").Append(s.Song.ToString("F6",c)).Append(" custom=").Append(s.Custom)
                    .Append(" rate=").Append(s.Rate.ToString("R",c)).Append(" width=").Append(s.Width).Append(" height=").Append(s.Height);
            }
            return text.ToString();
        }
        internal static void Stop()
        {
            active=false;
            try{harmony?.UnpatchOwner(Id);}catch{}harmony=null;samples=null;cameraRt=null;
        }
    }
}
#endif
