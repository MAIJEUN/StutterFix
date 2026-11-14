"""Exercise the actual BufferResize class with a deferred fake Unity/DXGI environment.
No game, display API, registry, GPU, or installed DLL is used.
"""
from pathlib import Path
import subprocess
root = Path(__file__).resolve().parents[2]
source = (root/'PresentPath.cs').read_text(encoding='utf-8-sig')
start = source.index('        sealed class BufferResize')
end = source.index('        // Preserve raw hashed', start)
pump = source[start:end].replace('System.Diagnostics.Stopwatch', 'FakeClock').replace('Main.Entry.Path', 'global::Main.Entry.Path')
resolve = source[source.index('        static IntPtr Resolve()'):source.index('        static void Issue(')].replace('Resolve()', 'MemoryResolve()')
out = root/'out/present-buffers/pump-fixture'
out.mkdir(parents=True, exist_ok=True)
(out/'Pump.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="Program.cs" /></ItemGroup></Project>')
prefix = r'''
using System;
using System.IO;
using UnityEngine;
class FakeClock {public static FakeClock StartNew()=>new FakeClock(); public long ElapsedMilliseconds=>(long)(Time.realtimeSinceStartup*1000);}
static class Main {internal static class Entry {internal static string Path=>AppContext.BaseDirectory;}}
namespace UnityEngine {
    class Object { public static void DontDestroyOnLoad(Object o){} public static void Destroy(Object o){} }
    class GameObject : Object { public GameObject(string n){} public T AddComponent<T>() where T:new(){return new T();} }
    class MonoBehaviour : Object {}
    enum FullScreenMode { FullScreenWindow=1,Windowed=3 }
    static class Time { public static int frameCount; public static float realtimeSinceStartup; }
    static class Screen {
        public static int width=1280,height=720;
        public static FullScreenMode fullScreenMode=FullScreenMode.FullScreenWindow;
        public static Action<int,int,FullScreenMode> OnRequest;
        public static void SetResolution(int w,int h,FullScreenMode mode){OnRequest(w,h,mode);}
    }
}
class Test {
    class ResizePump : MonoBehaviour {}
    class ScreenPrefs {internal int Count=>17; internal void Restore(){prefRestores++;}}
    static int prefRestores;
    static long[] stats=new long[36];
    static int status=1,target=3;
    static bool ignore,reject,identity=true,resolutionPending,requestThrows;
    static bool hook=true,skipResize,freezePresent,physicalDesc,physicalScreen;
    static int unexpectedWidth;
    static int rejectRequest;
    static int pendingW,pendingH,requests,directTriggers;
    static FullScreenMode pendingMode;
    static string logs="";
    static IntPtr captured=(IntPtr)1;
    static bool badGuard;
    static int reads;
    static IntPtr GetModuleHandleW(string name)=>(IntPtr)0x10000000;
    static byte[] Read(long address,int length){
        reads++;
        if(address==0x10930c84)return badGuard?new byte[7]:new byte[]{0x48,0x8B,0x05,0x85,0xDE,0x8E,0x01};
        if(address==0x1221eb10)return BitConverter.GetBytes(0x20000000L);
        if(address==0x20000320)return BitConverter.GetBytes((long)captured);
        throw new Exception("unexpected memory read");
    }
    static BufferResize resize;
    static long Stat(int i)=>i==-1?status:stats[i];
    static IntPtr Resolve()=>identity?captured:(IntPtr)2;
    static void Log(string s){logs+=s+"\n";}
    static void sf_presentpath_disable(){target=2;}
    static void sf_presentpath_pending(){status=2;}
    static void Issue(int id,IntPtr p){if(id==0){status=stats[2]==2?0:2;if(status==0)hook=false;} if(id==8){directTriggers++;throw new Exception("internal Adjust forbidden");}}
'''
suffix = r'''
    static void Expect(bool value,string name){if(!value)throw new Exception(name);}
    static void Reset(){
        if(resize!=null)resize.Disarm();
        stats=new long[36];stats[2]=stats[26]=2;stats[19]=1;
        status=1;target=3;ignore=reject=false;identity=true;prefRestores=0;
        resolutionPending=requestThrows=skipResize=freezePresent=false;hook=true;requests=directTriggers=rejectRequest=0;logs="";
        physicalDesc=physicalScreen=false;unexpectedWidth=0;
        Screen.OnRequest=(w,h,mode)=>{requests++;if(requestThrows)throw new Exception("request failed");resolutionPending=true;pendingW=w;pendingH=h;pendingMode=mode;};
        Screen.width=1280;Screen.height=720;Screen.fullScreenMode=FullScreenMode.FullScreenWindow;stats[0]=1280;stats[1]=720;Time.frameCount=0;Time.realtimeSinceStartup=0;
        resize=new BufferResize();
    }
    static void Tick(){
        Time.frameCount++;Time.realtimeSinceStartup+=0.1f;if(!freezePresent)stats[7]++;
        if(resolutionPending && !ignore){resolutionPending=false;
            if(hook && !skipResize){bool failed=reject || requests==rejectRequest;stats[20]++;stats[21]=failed?-1:0;if(failed)stats[25]++;else {stats[2]=target;stats[0]=unexpectedWidth!=0?unexpectedWidth:physicalDesc?1280:pendingW;stats[1]=pendingH;}}
            Screen.width=physicalScreen?1280:pendingW;Screen.height=pendingH;Screen.fullScreenMode=pendingMode;}
        resize.Pump();
    }
    static void Until(Func<bool> done){for(int i=0;i<400 && !done();i++)Tick();Expect(done(),"bounded completion");}
    static void Off(){sf_presentpath_disable();sf_presentpath_pending();Issue(0,IntPtr.Zero);resize.Restore();}
    static void Restored(){Expect(resize.Done && status==0 && stats[2]==2 && Screen.width==1280 && Screen.height==720 && !resolutionPending && prefRestores>0,"off restored");Expect(directTriggers==0,"no direct Adjust");}
    static void Main(){
        for(int i=0;i<100;i++)Expect(MemoryResolve()==captured,"memory identity");
        Expect(reads==300,"three memory reads per ACK; no filesystem dependencies");
        badGuard=true;bool rejected=false;try{MemoryResolve();}catch{rejected=true;}Expect(rejected,"runtime instruction guard retained");badGuard=false;
        Reset();Until(()=>resize.Ready);Expect(Screen.width==1280 && stats[2]==3 && requests==2 && stats[20]==2,"apply roundtrip ready");
        Off();Until(()=>resize.Done);resize.Check();Restored();
        Reset();Off();Until(()=>resize.Done);resize.Check();Restored(); // off before first resize
        Reset();Tick();Off();Until(()=>resize.Done);resize.Check();Restored(); // off with queued pulse
        Reset();reject=true;Until(()=>resize.Error!="none");reject=false;
        Until(()=>resize.Done);Restored();Expect(resize.Error!="none" && logs.Contains("status=-1 failed:") && logs.Contains("rollback ACK received"),"failed apply stays rejected and logs after rollback without Check");
        Reset();ignore=true;Until(()=>resize.Error!="none");ignore=false;
        Until(()=>resize.Done);Restored();Expect(logs.Contains("status=-1 failed:") && logs.Contains("rollback ACK received"),"timeout logs after rollback without Check");
        Reset();resize.Disarm();target=2;resize=new BufferResize(true);Until(()=>resize.Ready);
        Expect(stats[2]==2 && stats[20]>0,"probe count unchanged");Off();Until(()=>resize.Done);Restored();
        Reset();Tick();Tick();Off();Until(()=>resize.Done);resize.Check();Restored(); // off after 1px applied
        Reset();resize.Disarm();target=2;resize=new BufferResize(true);Tick();Off();Until(()=>resize.Done);resize.Check();Restored(); // probe off with deferred pulse
        Reset();requestThrows=true;Until(()=>resize.Error!="none");requestThrows=false;Until(()=>resize.Done);Restored();
        Reset();rejectRequest=2;Until(()=>resize.Error!="none");Until(()=>resize.Done);Restored(); // return-size HRESULT failure
        Reset();skipResize=true;Until(()=>resize.Error!="none");skipResize=false;Until(()=>resize.Done);Restored(); // screen ACK alone is insufficient
        Reset();Until(()=>logs.Contains("resize screen ACK size=1280"));freezePresent=true;Tick();Expect(!resize.Ready,"post-resize Present required");freezePresent=false;Until(()=>resize.Ready);Off();Until(()=>resize.Done);Restored();
        Reset();resize.Disarm();Screen.fullScreenMode=FullScreenMode.Windowed;resize=new BufferResize();Until(()=>resize.Ready);Off();Until(()=>resize.Done);resize.Check();Restored();
        Reset();physicalDesc=true;Until(()=>resize.Ready);Off();Until(()=>resize.Done);resize.Check();Restored();
        Reset();physicalDesc=physicalScreen=true;Until(()=>resize.Ready);Off();Until(()=>resize.Done);resize.Check();Restored();
        Reset();resize.Disarm();target=2;resize=new BufferResize(true);physicalDesc=physicalScreen=true;Until(()=>resize.Ready);Expect(stats[2]==2,"FSW physical-size probe");Off();Until(()=>resize.Done);resize.Check();Restored();
        Reset();unexpectedWidth=1260;Until(()=>resize.Error!="none");Expect(!resize.Ready,"arbitrary FSW descriptor rejected");unexpectedWidth=0;Until(()=>resize.Done);Restored();
        Reset();resize.Disarm();Screen.fullScreenMode=FullScreenMode.Windowed;resize=new BufferResize();physicalDesc=true;Until(()=>resize.Error!="none");Expect(!resize.Ready,"Windowed requires requested descriptor");physicalDesc=false;Until(()=>resize.Done);Restored();
        Reset();stats[0]=1279;Until(()=>resize.Done);Expect(stats[30]==0 && resize.Error!="none","dimension guard");
        Reset();Time.realtimeSinceStartup=16;resize.Watchdog(null);Expect(logs.Contains("status=-1") && target==2,"watchdog without main pump");
        Until(()=>resize.Done);Expect(resize.Error!="none","watchdog rejection retained");
        Reset();identity=false;Until(()=>resize.Done);
        Expect(resize.Error!="none" && !resize.Ready,"recreation rejected");
        resize.Disarm();
        Console.WriteLine("PASS actual memory-only Resolve: 100 calls / 300 reads, runtime byte guard retained; FSW physical descriptor and physical Screen ACK, probe and buffers3 roundtrip; arbitrary FSW width and unchanged Windowed descriptor rejected; Screen.SetResolution deferred 1px roundtrip; no direct Adjust; watchdog without Pump; actual BufferResize state machine: apply/off; early off; queued off; off at 1px; probe queued off; request exception; outbound/return HRESULT failure cleanup; missing resize ACK; subsequent Present required; ignored-resolution timeout cleanup; swapchain recreation refusal");
    }
}
'''
(out/'Program.cs').write_text(prefix+resolve+pump+suffix, encoding='utf-8')
result = subprocess.run(['dotnet','run','--project',str(out/'Pump.csproj'),'-c','Release'],cwd=root,capture_output=True,text=True)
(out.parent/'pump-fixture.txt').write_text(result.stdout+result.stderr,encoding='utf-8')
print(result.stdout+result.stderr)
raise SystemExit(result.returncode)
