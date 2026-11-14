#if AUTOTEST
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace StutterFix
{
    // Explicit, non-persistent research lease. No initialization until a command.
    internal static class PresentPath
    {
        [DllImport("sfnative", CallingConvention=CallingConvention.Cdecl)] static extern IntPtr sf_presentpath_event_ptr();
        [DllImport("sfnative", CallingConvention=CallingConvention.Cdecl)] static extern long sf_presentpath_stat(int i);
        [DllImport("sfnative", CallingConvention=CallingConvention.Cdecl)] static extern void sf_presentpath_pin(IntPtr p);
        [DllImport("sfnative", CallingConvention=CallingConvention.Cdecl)] static extern void sf_presentpath_disable();
        [DllImport("sfnative", CallingConvention=CallingConvention.Cdecl)] static extern void sf_presentpath_pending();
        [DllImport("sfnative", CallingConvention=CallingConvention.Cdecl)] static extern int sf_framegen_installed();
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr process, IntPtr addr, byte[] bytes, IntPtr size, out IntPtr read);
        static IntPtr callback, captured;
        static bool stopping;
        static string requested;
        static int startFrame;
        static bool assertPending;
        static long refreshBefore;
        static bool statePending;
        static long stateRefreshBefore;
        static CommandBuffer gpuBegin;
        static CommandBuffer gpuFrameBegin;
        static bool gpuCameraTiming;
        static bool gpuPipeTiming;
        static Camera gpuPipeCamera;
        static CommandBuffer gpuPipeStart,gpuPipeEnd;
        static readonly Camera[] gpuCameras=new Camera[8];
        static readonly CommandBuffer[] gpuCameraStarts=new CommandBuffer[8],gpuCameraEnds=new CommandBuffer[8];
        static readonly CommandBuffer[] gpuFilterStarts=new CommandBuffer[8],gpuFilterEnds=new CommandBuffer[8];
        static int gpuCameraCount;
        static readonly Camera.CameraCallback gpuCameraCallback=GpuCamera;
        static readonly Camera.CameraCallback gpuPreCullCallback=GpuPreCull;
        static void GpuPreCull(Camera camera)
        {
            if(gpuFrameBegin==null || stopping)return;
            // Immediate render event precedes this camera's culling/render work,
            // even when Unity renders camRT before the first screen camera.
            Graphics.ExecuteCommandBuffer(gpuFrameBegin);
            if(gpuPipeTiming && camera==gpuPipeCamera)Graphics.ExecuteCommandBuffer(gpuPipeStart);
            if(!gpuCameraTiming)return;
            for(int i=0;i<gpuCameraCount;i++)if(gpuCameras[i]==camera)
            {
                Graphics.ExecuteCommandBuffer(gpuCameraStarts[i]);return;
            }
            if(gpuCameraCount==8)return;
            int k=gpuCameraCount++;
            gpuCameras[k]=camera;
            var first=new CommandBuffer {name="PresentPath.CameraBegin"};
            var last=new CommandBuffer {name="PresentPath.CameraEnd"};
            first.IssuePluginEventAndData(callback,100+k,IntPtr.Zero);
            last.IssuePluginEventAndData(callback,200+k,IntPtr.Zero);
            gpuCameraStarts[k]=first;gpuCameraEnds[k]=last;
            camera.AddCommandBuffer(CameraEvent.AfterEverything,last);
            var fxFirst=new CommandBuffer {name="PresentPath.ImageEffectsBegin"};
            var fxLast=new CommandBuffer {name="PresentPath.ImageEffectsEnd"};
            fxFirst.IssuePluginEventAndData(callback,108+k,IntPtr.Zero);
            fxLast.IssuePluginEventAndData(callback,208+k,IntPtr.Zero);
            gpuFilterStarts[k]=fxFirst;gpuFilterEnds[k]=fxLast;
            camera.AddCommandBuffer(CameraEvent.BeforeImageEffects,fxFirst);
            camera.AddCommandBuffer(CameraEvent.AfterImageEffects,fxLast);
            Graphics.ExecuteCommandBuffer(first);
        }
        static void GpuCamera(Camera camera)
        {
            // Unity invokes this before rendering the camera; native deduplicates
            // multiple screen cameras until the next real Present. No per-frame allocation.
            if(gpuBegin!=null && !stopping && camera.targetTexture==null)
                Graphics.ExecuteCommandBuffer(gpuBegin);
        }
        static void StopGpuCamera()
        {
            if(gpuPipeCamera!=null && gpuPipeEnd!=null)gpuPipeCamera.RemoveCommandBuffer(CameraEvent.AfterEverything,gpuPipeEnd);
            if(gpuPipeStart!=null)gpuPipeStart.Release();
            if(gpuPipeEnd!=null)gpuPipeEnd.Release();
            gpuPipeStart=gpuPipeEnd=null;gpuPipeCamera=null;gpuPipeTiming=false;
            Camera.onPreRender-=gpuCameraCallback;
            Camera.onPreCull-=gpuPreCullCallback;
            if(gpuBegin!=null) {gpuBegin.Release();gpuBegin=null;}
            if(gpuFrameBegin!=null) {gpuFrameBegin.Release();gpuFrameBegin=null;}
            for(int i=0;i<gpuCameraCount;i++)
            {
                if(gpuCameras[i]!=null)
                {
                    gpuCameras[i].RemoveCommandBuffer(CameraEvent.AfterEverything,gpuCameraEnds[i]);
                    gpuCameras[i].RemoveCommandBuffer(CameraEvent.BeforeImageEffects,gpuFilterStarts[i]);
                    gpuCameras[i].RemoveCommandBuffer(CameraEvent.AfterImageEffects,gpuFilterEnds[i]);
                }
                gpuCameraStarts[i].Release();gpuCameraEnds[i].Release();
                gpuCameraStarts[i]=gpuCameraEnds[i]=null;gpuCameras[i]=null;
                gpuFilterStarts[i].Release();gpuFilterEnds[i].Release();
                gpuFilterStarts[i]=gpuFilterEnds[i]=null;
            }
            gpuCameraCount=0;gpuCameraTiming=false;
        }
        static BufferResize resize;
        static readonly FrameTiming[] timing=new FrameTiming[1];
        static long Stat(int i) { return callback==IntPtr.Zero ? 0 : sf_presentpath_stat(i); }
        static byte[] Read(long address, int length)
        {
            var b=new byte[length]; IntPtr n;
            if(address==0 || !ReadProcessMemory(GetCurrentProcess(), (IntPtr)address, b, (IntPtr)length, out n) || (long)n!=length)
                throw new Exception("presentpath: unreadable address");
            return b;
        }
        static void VerifyBinary()
        {
            if(Application.unityVersion!="6000.3.25f1" || SystemInfo.graphicsDeviceType!=GraphicsDeviceType.Direct3D11)
                throw new Exception("presentpath: requires pinned Unity 6000.3.25f1 D3D11");
            var path=Path.Combine(Path.GetDirectoryName(Application.dataPath), "UnityPlayer.dll");
            using(var hash=SHA256.Create()) using(var f=File.OpenRead(path))
                if(BitConverter.ToString(hash.ComputeHash(f)).Replace("-", "").ToLowerInvariant()!="7e362b4c6ecee6fb493b1872cda5a5a0caf44f3b131b3c4bad132f0d1309f436")
                    throw new Exception("presentpath: UnityPlayer SHA256 mismatch");
        }
        static IntPtr Resolve()
        {
            // The lease entry verifies the full file. ACK polling must only read memory:
            // hashing UnityPlayer on every Update can itself stall each frame.
            long module=(long)GetModuleHandleW("UnityPlayer.dll");
            // PresentFrame loads settings from this RIP-relative global, then chain at +0x320.
            var guard=Read(module+0x930c84, 7);
            if(BitConverter.ToString(guard)!="48-8B-05-85-DE-8E-01") throw new Exception("presentpath: runtime instruction mismatch");
            long settings=BitConverter.ToInt64(Read(module+0x221eb10,8),0);
            long sc=BitConverter.ToInt64(Read(settings+0x320,8),0);
            if(sc==0) throw new Exception("presentpath: no swapchain");
            return (IntPtr)sc;
        }
        static void Issue(int id, IntPtr ptr)
        {
            using(var cb=new CommandBuffer())
            {
                cb.IssuePluginEventAndData(callback,id,ptr);
                Graphics.ExecuteCommandBuffer(cb);
            }
            // ExecuteCommandBuffer queues the event. GL.Flush can disturb Unity's
            // Wait/Present pairing; observe the render ACK on later frames instead.
        }
        internal static bool Step(string arg)
        {
            var a=arg.Split(new[]{' '}, StringSplitOptions.RemoveEmptyEntries);
            if(a.Length==1 && a[0]=="off")
            {
                Stop();
                if(resize!=null && !resize.Done) {resize.Check();return false;}
                if(resize!=null) resize.Check();
                if(callback!=IntPtr.Zero && Stat(-1)<0) throw new Exception("presentpath: restore failed status="+Stat(-1));
                if(callback!=IntPtr.Zero && Stat(-1)!=0) return false;
                State(); return true;
            }
            if(a.Length==1 && a[0]=="state")
            {
                if(stopping) {if(Stat(-1)==2) return false;State();return true;}
                if(requested!=null && Stat(-1)<0) {State();return true;}
                if(statePending)
                {
                    if(Stat(18)<=stateRefreshBefore) return false;
                    statePending=false;State();return true;
                }
                if(callback!=IntPtr.Zero && requested!=null) {stateRefreshBefore=Stat(18);Issue(4,IntPtr.Zero);}
                else
                {
                    if(!SfNative.Ready) throw new Exception("presentpath: native unavailable");
                    VerifyBinary();var sc=Resolve();callback=sf_presentpath_event_ptr();
                    if(callback==IntPtr.Zero) throw new Exception("presentpath: missing native event");
                    stateRefreshBefore=Stat(18);
                    sf_presentpath_pin(sc);Issue(5,sc); // read-only snapshot; no instance hook
                }
                statePending=true;
                return false; // wait for render-thread snapshot ACK before logging actual state
            }
            if((a.Length==2 || (a.Length==3 && a[1]=="gputime" && (a[2]=="cameras" || a[2]=="pipestats")) || (a.Length==4 && a[1]=="gputime" && a[2]=="pipestats")) && a[0]=="on")
            {
                int id=a[1]=="observe"?1:a[1]=="tearing"?2:a[1]=="latency4"?3:a[1]=="buffers3"?6:a[1]=="resizeprobe"?7:a[1]=="gputime"?9:0;
                if(id==0) throw new Exception("presentpath: unknown candidate");
                if(requested!=null || stopping) throw new Exception("presentpath: finish off before on");
                if(!SfNative.Ready || sf_framegen_installed()!=0) throw new Exception("presentpath: native unavailable or framegen installed");
                Camera pipeCamera=id==9 && a.Length==3 && a[2]=="pipestats"?GpuDecoScene.FindMain():null;
                if(a.Length==4)pipeCamera=GpuDecoScene.FindMain();
                int pipeFrames=30;if(a.Length==4 && (!int.TryParse(a[3],out pipeFrames)||pipeFrames<1||pipeFrames>300))throw new Exception("pipestats frames 1..300");
                VerifyBinary();captured=Resolve();callback=sf_presentpath_event_ptr();
                if(callback==IntPtr.Zero) throw new Exception("presentpath: missing native event");
                if(id==6 || id==7) resize=new BufferResize(id==7);
                else resize=null;
                requested=a[1];startFrame=Time.frameCount;
                FrameTimingManager.CaptureFrameTimings();
                gpuCameraTiming=id==9 && a.Length>=3;
                gpuPipeTiming=pipeCamera!=null;gpuPipeCamera=pipeCamera;
                if(gpuPipeTiming)
                {
                    gpuPipeStart=new CommandBuffer{name="PresentPath.PipelineBegin"};
                    gpuPipeEnd=new CommandBuffer{name="PresentPath.PipelineEnd"};
                    gpuPipeStart.IssuePluginEventAndData(callback,300,IntPtr.Zero);
                    gpuPipeEnd.IssuePluginEventAndData(callback,301,IntPtr.Zero);
                    gpuPipeCamera.AddCommandBuffer(CameraEvent.AfterEverything,gpuPipeEnd);
                }
                sf_presentpath_pin(captured);sf_presentpath_pending();
                Log("queued "+requested+"; verify with assert after elapsed frames");Issue(gpuPipeTiming?14:gpuCameraTiming?13:id,captured);
                if(gpuPipeTiming)Issue(302,(IntPtr)pipeFrames);
                if(id==9)
                {
                    gpuBegin=new CommandBuffer {name="PresentPath.GpuBegin"};
                    gpuBegin.IssuePluginEventAndData(callback,10,IntPtr.Zero);
                    gpuFrameBegin=new CommandBuffer {name="PresentPath.GpuFrameBegin"};
                    gpuFrameBegin.IssuePluginEventAndData(callback,11,IntPtr.Zero);
                    Camera.onPreRender+=gpuCameraCallback;
                    Camera.onPreCull+=gpuPreCullCallback;
                }
                return true;
            }
            if(a.Length==1 && a[0]=="assert")
            {
                if(callback==IntPtr.Zero || requested==null || stopping) throw new Exception("presentpath INVALID: no active lease");
                if(resize!=null) {resize.Check();if(!resize.Ready)return false;}
                if(Stat(-1)<0) throw new Exception("presentpath INVALID: native rejected status="+Stat(-1));
                if(Stat(-1)==2) return false;
                if(!assertPending) {refreshBefore=Stat(18);assertPending=true;Issue(4,IntPtr.Zero);return false;}
                if(Stat(18)<=refreshBefore) return false;
                assertPending=false;
                if(requested==null || stopping || Stat(-1)!=1 || Time.frameCount<=startFrame || Stat(7)==0 || Resolve()!=captured || Stat(15)!=0)
                    throw new Exception("presentpath INVALID: inactive/recreated/failed/no observed Present");
                if(Stat(19)!=1) throw new Exception("presentpath INVALID: overlay replaced instance vtable");
                if(requested=="gputime" && (Stat(61)==0 || Stat(53)!=0 || Stat(60)!=1))
                    throw new Exception("presentpath INVALID: no recovered GPU samples/query failure/not ready");
                if(gpuPipeTiming && (Stat(100)!=1 || Stat(101)!=Stat(109) || Stat(104)!=0 || Stat(107)!=0 || Stat(108)!=0))
                    throw new Exception("presentpath INVALID: pipeline unavailable/no samples/failure/missing camera end");
                if(requested=="tearing" && Stat(14)==0) throw new Exception("presentpath INVALID: tearing no-op (already enabled or ineligible)");
                if(requested=="latency4" && Stat(6)!=4) throw new Exception("presentpath INVALID: latency target not observed");
                if(requested=="buffers3" && (Stat(2)!=3 || Stat(20)==0 || Stat(25)!=0 || Stat(27)==0 || Stat(17)<0))
                    throw new Exception("presentpath INVALID: buffers3 resize/count/desc verification failed");
                if(requested=="resizeprobe" && (Stat(2)!=Stat(26) || Stat(20)==0 || Stat(25)!=0 || Stat(27)!=0 || Stat(21)<0))
                    throw new Exception("presentpath INVALID: resizeprobe count/resize failed");
                State();Log("accepted "+requested);return true;
            }
            throw new Exception("presentpath state|on observe|on tearing|on latency4|on buffers3|on resizeprobe|on gputime [cameras|pipestats [1..300]]|assert|off");
        }
        internal static void Stop()
        {
            StopGpuCamera();
            if(callback==IntPtr.Zero || requested==null || stopping) return;
            // Immediately stops flag changes; render event restores COM latency and vptr, with ACK.
            sf_presentpath_disable();sf_presentpath_pending();stopping=true;assertPending=statePending=false;
            if(resize!=null) resize.Restore();
            Log("off queued; status=0 is restoration ACK");
            Issue(0,IntPtr.Zero);
        }
        static void State()
        {
            if(stopping && Stat(-1)==0 && (resize==null || resize.Done)) {stopping=false;requested=null;}
            uint samples=FrameTimingManager.GetLatestTimings(1,timing);
            FrameTimingManager.CaptureFrameTimings();
            Log("state requested="+(requested??"off")+" stopping="+stopping+" status="+Stat(-1)+" mode="+Stat(-2)+
                " desc1HR="+Stat(17)+" size="+Stat(0)+"x"+Stat(1)+" buffers="+Stat(2)+" effect="+Stat(3)+" creationFlags="+Stat(4)+
                " windowed="+Stat(5)+" latency="+Stat(6)+" latencyAPI="+Stat(16)+" presents="+Stat(7)+" sync="+Stat(8)+
                " inputFlags="+Stat(9)+" outputFlags="+Stat(10)+" presentHR="+Stat(11)+" presentLastUs="+Stat(12)+
                " presentMeanUs="+(Stat(7)>0?Stat(13)/(double)Stat(7):0)+" modified="+Stat(14)+" failures="+Stat(15)+
                " refresh="+Stat(18)+" ownsVtable="+Stat(19)+" currentWaitSamples="+samples+" currentWaitMs="+(samples>0?timing[0].cpuMainThreadPresentWaitTime:-1)+
                " resizeCalls="+Stat(20)+" resizeHR="+Stat(21)+" resizeInputCount="+Stat(22)+" resizeOutputCount="+Stat(23)+
                " resizeFlags="+Stat(24)+" resizeFailures="+Stat(25)+" originalBuffers="+Stat(26)+" resizeRewrites="+Stat(27)+
                " fallbackHR="+Stat(28)+" fallbackCalls="+Stat(29)+" triggerCalls="+Stat(30)+" adjustResult="+Stat(31)+" triggerPhase="+Stat(32)+
                " adjGuardA="+Stat(33)+" adjGuardB="+Stat(34)+" afterPresentCalls="+Stat(35)+
                " resizeReady="+(resize!=null && resize.Ready)+" resizeDone="+(resize!=null && resize.Done)+
                " resizeError="+(resize==null?"none":resize.Error)+
                " waitScope=latest_FrameTiming_not_exclusive_CPU_time");
            if(requested=="gputime" || Stat(46)>0 || Stat(53)>0)
                Log("gpu recovered="+Stat(36)+" renderMeanUs="+Stat(37)+" renderP50Us="+Stat(38)+" renderP95Us="+Stat(39)+" renderMaxUs="+Stat(40)+
                    " bbSamples="+Stat(41)+" bbMeanUs="+Stat(42)+" bbP50Us="+Stat(43)+" bbP95Us="+Stat(44)+" bbMaxUs="+Stat(45)+
                    " presentSamples="+Stat(46)+" presentMeanUs="+Stat(47)+" presentP95Us="+Stat(48)+
                    " eventChecks="+Stat(49)+" eventNotObserved="+Stat(50)+" eventNotObservedPct="+(Stat(51)/10000.0)+
                    " disjoint="+Stat(52)+" queryFailures="+Stat(53)+" ringDrops="+Stat(54)+" pending="+Stat(55)+" missingBegin="+Stat(56)+" discardedOnStop="+Stat(57)+
                    " bcMeanUs="+Stat(58)+" bcP95Us="+Stat(59)+" ready="+Stat(60)+
                    " gpuFrameSamples="+Stat(61)+" gpuFrameMeanUs="+Stat(62)+" gpuFrameP50Us="+Stat(63)+" gpuFrameP95Us="+Stat(64)+" gpuFrameMaxUs="+Stat(65)+
                    " cameraTiming="+Stat(66)+" cameraRepeatedMarkersSkipped="+Stat(67)+" gpuFrameScope=first_any_camera_preCull_event_to_prePresent_including_offscreen"+
                    " scope=first_screen_camera_event_to_prePresent percentile=8us_upper_bound_overflow_max eventScope=after_return_nonblocking_observation bbScope=every_other_adjacent_interval_within_one_disjoint_epoch");
            if(gpuPipeTiming)
            {
                double pipeSamples=Stat(101),pixels=gpuPipeCamera!=null?(double)gpuPipeCamera.pixelWidth*gpuPipeCamera.pixelHeight:0;
                Log("pipeline camera="+(gpuPipeCamera!=null?gpuPipeCamera.name:"destroyed")+" ready="+Stat(100)+" samples="+Stat(101)+
                    " PSInvocationsSum="+Stat(102)+" CPrimitivesSum="+Stat(103)+" PSInvocationsMean="+(pipeSamples>0?Stat(102)/pipeSamples:0)+
                    " CPrimitivesMean="+(pipeSamples>0?Stat(103)/pipeSamples:0)+" screenPixels="+pixels+" PSLayers="+(pipeSamples>0&&pixels>0?Stat(102)/pipeSamples/pixels:0)+
                    " queryFailures="+Stat(104)+" ringDrops="+Stat(105)+" pending="+Stat(106)+" missingEnd="+Stat(107)+" repeatedBegin="+Stat(108)+" targetSamples="+Stat(109)+" submitted="+Stat(110)+
                    " scope=main_camera_preCull_to_AfterEverything_including_effects;PSInvocations-is-shader-invocations-not-blend-writes");
            }
            if(gpuCameraTiming)
            {
                // Bounded insertion sort by mean per recovered frame; allocate only on state.
                var order=new int[gpuCameraCount];
                for(int i=0;i<order.Length;i++) {int j=i;while(j>0 && Stat(69+2*order[j-1])<Stat(69+2*i)) {order[j]=order[j-1];j--;}order[j]=i;}
                foreach(int k in order)Log("gpuCamera index="+k+" name="+(gpuCameras[k]!=null?gpuCameras[k].name:"destroyed")+
                    " target="+(gpuCameras[k]!=null && gpuCameras[k].targetTexture!=null?"RT":"screen")+
                    " samples="+Stat(68+2*k)+" meanPerFrameUs="+Stat(69+2*k)+" denominator="+Stat(61)+
                    " filterSamples="+Stat(84+2*k)+" filterMeanPerFrameUs="+Stat(85+2*k)+
                    " scope=preCull_event_to_AfterEverything_inclusive filterScope=BeforeImageEffects_to_AfterImageEffects_aggregate");
            }
        }
        // An independent Unity Update survives EndBatch/Stop and a mod disable long enough
        // to finish restoration. Screen.SetResolution queues Unity's normal resize path.
        sealed class ResizePump : MonoBehaviour
        {
            void Update() {if(resize!=null) resize.Pump();}
        }
        sealed class BufferResize
        {
            readonly int w=Screen.width, h=Screen.height;
            readonly FullScreenMode screenMode=Screen.fullScreenMode;
            readonly ScreenPrefs prefs;
            readonly bool probe;
            GameObject driver;
            int phase=1, frame;
            long calls, presents;
            bool restore;
            volatile string error;
            readonly System.Diagnostics.Stopwatch clock=System.Diagnostics.Stopwatch.StartNew();
            long deadline=15000;
            System.Threading.Timer watchdog;
            readonly object watchGate=new object();
            bool watchArmed;
            readonly string watchdogPath=Path.Combine(Main.Entry.Path,"presentpath-watchdog.txt");
            internal bool Ready, Done;
            internal string Error {get {return error??"none";}}
            internal BufferResize(bool diagnostic=false)
            {
                probe=diagnostic;
                if(screenMode!=FullScreenMode.FullScreenWindow && screenMode!=FullScreenMode.Windowed)
                    throw new Exception("presentpath: requires windowed or FullScreenWindow");
                if(w<2 || h<2)throw new Exception("presentpath: invalid screen size");
                prefs=new ScreenPrefs();
                StartDriver();Arm();
                Log("resize snapshot="+w+"x"+h+" mode="+screenMode+" probe="+probe+" registryValues="+prefs.Count);
            }
            void Arm()
            {
                lock(watchGate)
                {
                    System.Threading.Interlocked.Exchange(ref deadline,clock.ElapsedMilliseconds+15000);
                    watchArmed=true;
                    if(watchdog==null)watchdog=new System.Threading.Timer(Watchdog,null,1000,1000);
                }
            }
            internal void Disarm()
            {
                lock(watchGate) {watchArmed=false;var timer=watchdog;watchdog=null;if(timer!=null)timer.Dispose();}
            }
            internal void Watchdog(object unused)
            {
                string line;
                lock(watchGate)
                {
                    if(!watchArmed || clock.ElapsedMilliseconds<=System.Threading.Interlocked.Read(ref deadline))return;
                    // No Unity/COM/registry APIs on this thread. Only atomic cancellation.
                    sf_presentpath_disable();
                    error="watchdog ACK timeout; main/render progress unavailable; restoration pending";
                    Disarm();
                    line="status=-1 failed "+error+" resizeHR="+Stat(21)+" triggerPhase="+Stat(32);
                    try {File.AppendAllText(watchdogPath,"[presentpath] "+line+Environment.NewLine);}catch { }
                }
                // UMM logging also exposes failure to the existing bench log monitor.
                try {Log(line);}catch { }
            }
            internal void Check()
            {
                if(error!=null)throw new Exception("presentpath status=-1 failed: "+error);
                if((Ready || Done) && (Screen.width!=w || Screen.height!=h || Screen.fullScreenMode!=screenMode))
                    throw new Exception("presentpath status=-1 failed: screen changed after ACK");
            }
            internal void Restore()
            {
                if(restore || Done)return;
                restore=true;Ready=false;phase=1;Arm();
                if(driver==null)StartDriver();
            }
            void StartDriver()
            {
                driver=new GameObject("StutterFix.PresentBuffersRestore");
                UnityEngine.Object.DontDestroyOnLoad(driver);driver.AddComponent<ResizePump>();
            }
            void DestroyDriver() {Disarm();UnityEngine.Object.Destroy(driver);driver=null;}
            void Restored(string message)
            {
                Done=true;DestroyDriver();Log(message);
                if(error!=null)Log("status=-1 failed: "+error+"; rollback ACK received; preferences restored");
            }
            void Request(int width, int nextPhase)
            {
                calls=Stat(20);frame=Time.frameCount;phase=nextPhase;Arm();
                Log("resize Screen.SetResolution queued phase="+(restore?"restore":"apply")+
                    " size="+width+"x"+h+" mode="+screenMode+" callsBefore="+calls);
                Screen.SetResolution(width,h,screenMode);
                Log("resize Screen.SetResolution returned; awaiting resize/screen ACK");
            }
            internal void Pump()
            {
                if(Done || (phase==0 && !restore))return;
                try
                {
                    if(clock.ElapsedMilliseconds>System.Threading.Interlocked.Read(ref deadline))throw new Exception("resize ACK timeout phase="+phase);
                    if(phase==1)
                    {
                        if(!restore && Stat(-1)==2)return;
                        if(Stat(-1)<0)throw new Exception("native status="+Stat(-1));
                        // Even an early/probe off can leave a deferred 1px request. Queue
                        // the original size after it and wait before restoring preferences.
                        if(restore && Stat(-1)==0) {Request(w,5);return;}
                        if(Resolve()!=captured || Stat(19)!=1)throw new Exception("swapchain/vtable replaced");
                        if(!restore && (Screen.width!=w || Screen.height!=h || Screen.fullScreenMode!=screenMode || Stat(0)!=w || Stat(1)!=h))
                            throw new Exception("screen/backbuffer dimensions differ; exact-size trigger refused");
                        // A width change forces Unity to release/recreate its backbuffer.
                        // Do not call internal Adjust or hook the device OnAfterPresent.
                        // Rollback may start at w-1 already, so return to w first.
                        Request(restore && Screen.width!=w?w:w-1,restore && Screen.width!=w?6:2);return;
                    }
                    if(phase==2 || phase==6)
                    {
                        if(Time.frameCount<=frame+2)return;
                        if(Stat(-1)<0)throw new Exception("native status="+Stat(-1));
                        if(Screen.fullScreenMode!=screenMode)throw new Exception("screen mode changed");
                        if(Resolve()!=captured || Stat(19)!=1)throw new Exception("swapchain/vtable replaced");
                        if(Stat(20)>calls && (Stat(21)<0 || (!restore && Stat(25)!=0)))
                            throw new Exception("Screen.SetResolution resize HRESULT/rewrite failed");
                        int targetWidth=phase==2?w-1:w;
                        // FSW can keep the physical monitor size despite the 1px request.
                        // Only the outbound pulse may retain the captured size; never
                        // accept arbitrary dimensions or a request without ResizeBuffers.
                        bool fswPulse=phase==2 && screenMode==FullScreenMode.FullScreenWindow;
                        if((Screen.width!=targetWidth && !(fswPulse && Screen.width==w)) || Screen.height!=h || Stat(20)<=calls ||
                           (Stat(0)!=targetWidth && !(fswPulse && Stat(0)==w)) || Stat(1)!=h)return;
                        Log("resize screen ACK size="+targetWidth+"x"+h+" actualScreen="+Screen.width+"x"+Screen.height+
                            " desc="+Stat(0)+"x"+Stat(1)+" calls="+Stat(20)+" hr="+Stat(21)+" fallbackHR="+Stat(28)+" presents="+Stat(7));
                        if(Stat(17)<0 || Stat(2)!=(restore || probe?Stat(26):3))
                            throw new Exception("Screen.SetResolution resize/count verification failed");
                        if(phase==2) {Request(w,6);return;}
                        presents=Stat(7);phase=3;return;
                    }
                    if(phase==3)
                    {
                        if(Stat(7)<=presents)return;
                        if(Stat(15)!=0)throw new Exception("Present failed after resize");
                        if(Resolve()!=captured || Stat(19)!=1 || Screen.width!=w || Screen.height!=h || Stat(0)!=w || Stat(1)!=h || Screen.fullScreenMode!=screenMode)
                            throw new Exception("identity/screen changed after resize");
                        prefs.Restore();
                        if(!restore) {Ready=true;phase=0;DestroyDriver();Log("resize ready; Present advanced; screen/preferences preserved");return;}
                        Log("resize restored count; detach queued");Issue(0,IntPtr.Zero);phase=4;return;
                    }
                    if(phase==4)
                    {
                        if(Stat(-1)==2)return;
                        if(Stat(-1)!=0 || Stat(2)!=Stat(26) || Resolve()!=captured || Screen.width!=w || Screen.height!=h || Screen.fullScreenMode!=screenMode)
                            throw new Exception("off restoration rejected");
                        prefs.Restore();Restored("off ACK; buffers/screen/preferences restored");
                    }
                    if(phase==5 && Time.frameCount>frame+2 && Screen.width==w && Screen.height==h && Screen.fullScreenMode==screenMode)
                    {
                        if(Resolve()!=captured || Stat(-1)!=0 || Stat(2)!=Stat(26))throw new Exception("early off identity/count mismatch");
                        prefs.Restore();Restored("off ACK; count unchanged; screen/preferences restored");
                    }
                }
                catch(Exception ex)
                {
                    error=(error==null?"":error+"; ")+ex.Message;Ready=false;
                    sf_presentpath_disable();
                    if(!restore)
                    {
                        Restore();sf_presentpath_pending();
                        Log("resize failed; rollback queued: "+error);Issue(0,IntPtr.Zero);return;
                    }
                    try {Request(w,5);}catch(Exception e) {error+="; screen restore request: "+e.Message;}
                    try {prefs.Restore();}catch(Exception e) {error+="; preferences: "+e.Message;}
                    // Keep callback/ref ownership if the engine cannot restore the count.
                    Done=true;DestroyDriver();
                    Log("status=-1 failed: "+error+" nativeStatus="+Stat(-1)+" restoration not certified");
                }
            }
        }
        // Preserve raw hashed registry names/types and the corresponding PlayerPrefs cache.
        // Refuse unfamiliar types before touching Screen. Only Screenmanager values are owned.
        sealed class ScreenPrefs
        {
            [DllImport("advapi32.dll",CharSet=CharSet.Unicode)] static extern int RegOpenKeyEx(IntPtr root,string path,int options,int access,out IntPtr key);
            [DllImport("advapi32.dll")] static extern int RegCloseKey(IntPtr key);
            [DllImport("advapi32.dll",CharSet=CharSet.Unicode)] static extern int RegEnumValue(IntPtr key,uint i,StringBuilder name,ref uint length,IntPtr reserved,out uint type,byte[] data,ref uint size);
            [DllImport("advapi32.dll",CharSet=CharSet.Unicode)] static extern int RegSetValueEx(IntPtr key,string name,int reserved,uint type,byte[] data,int size);
            [DllImport("advapi32.dll",CharSet=CharSet.Unicode)] static extern int RegDeleteValue(IntPtr key,string name);
            readonly string path="Software\\"+Application.companyName+"\\"+Application.productName;
            readonly Dictionary<string,byte[]> saved;
            readonly Dictionary<string,int?> cached=new Dictionary<string,int?>();
            internal int Count {get {return saved.Count;}}
            IntPtr Open()
            {
                IntPtr key;int e=RegOpenKeyEx(new IntPtr(unchecked((int)0x80000001)),path,0,0x2001f,out key);
                if(e!=0)throw new Exception("Screenmanager registry open="+e);return key;
            }
            static Dictionary<string,byte[]> ReadPrefs(IntPtr key)
            {
                var values=new Dictionary<string,byte[]>();
                for(uint i=0;;i++)
                {
                    var name=new StringBuilder(16384);uint length=16384,type,size=65536;var data=new byte[size];
                    int e=RegEnumValue(key,i,name,ref length,IntPtr.Zero,out type,data,ref size);
                    if(e==259)break;if(e!=0)throw new Exception("Screenmanager enum="+e);
                    string n=name.ToString();if(!n.StartsWith("Screenmanager ",StringComparison.Ordinal))continue;
                    if(type!=4 || size!=4)throw new Exception("Screenmanager non-DWORD: "+n);
                    Array.Resize(ref data,4);values.Add(n,data);
                }
                return values;
            }
            internal ScreenPrefs()
            {
                var key=Open();try {saved=ReadPrefs(key);}finally {RegCloseKey(key);}
                foreach(var n in saved.Keys)
                {
                    int hash=n.LastIndexOf("_h",StringComparison.Ordinal);if(hash<0)throw new Exception("Screenmanager unhashed name: "+n);
                    string logical=n.Substring(0,hash);
                    cached[logical]=PlayerPrefs.HasKey(logical)?(int?)PlayerPrefs.GetInt(logical):null;
                }
            }
            internal void Restore()
            {
                var key=Open();
                try
                {
                    // Flush first so newly created native Screenmanager cache keys are
                    // enumerable and removed from BOTH cache and registry below.
                    PlayerPrefs.Save();
                    var current=ReadPrefs(key);
                    foreach(var n in current.Keys)
                    {
                        int hash=n.LastIndexOf("_h",StringComparison.Ordinal);
                        if(hash<0)throw new Exception("Screenmanager unhashed new name: "+n);
                        string logical=n.Substring(0,hash);
                        if(!cached.ContainsKey(logical))PlayerPrefs.DeleteKey(logical);
                    }
                    foreach(var p in cached) {if(p.Value.HasValue)PlayerPrefs.SetInt(p.Key,p.Value.Value);else PlayerPrefs.DeleteKey(p.Key);}
                    PlayerPrefs.Save();
                    current=ReadPrefs(key);
                    foreach(var n in current.Keys)if(!saved.ContainsKey(n) && RegDeleteValue(key,n)!=0)throw new Exception("Screenmanager delete failed");
                    foreach(var p in saved)if(RegSetValueEx(key,p.Key,0,4,p.Value,4)!=0)throw new Exception("Screenmanager restore failed");
                    var check=ReadPrefs(key);if(check.Count!=saved.Count)throw new Exception("Screenmanager count mismatch");
                    foreach(var p in saved)if(!check.ContainsKey(p.Key) || BitConverter.ToUInt32(check[p.Key],0)!=BitConverter.ToUInt32(p.Value,0))throw new Exception("Screenmanager value mismatch");
                }
                finally {RegCloseKey(key);}
            }
        }
        static void Log(string s) {Main.Entry.Logger.Log("[presentpath] "+s);}
    }
}
#endif
