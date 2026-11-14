"""Offline regression of actual RunLod projection/lifetime methods; no Unity/game."""
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]
source = (root / 'RunLod.cs').read_text(encoding='utf-8-sig')


def method(name):
    start = source.index('        ' + name)
    brace = source.index('{', start)
    depth = 1
    end = brace + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]


geometry = '\n'.join(method(s) for s in (
    'static Vector3 VPt(', 'static void ProjectBounds(',
    'static void ViewRect(', 'static bool OnScreen('))
lifetime = '\n'.join(method(s) for s in (
    'static void Restore(', 'internal static void Pause(',
    'internal static void SongEnded('))
# Compile the actual early guard, ending before unrelated drawing/resource code.
start = source.index('        static void PreCullBody(')
end = source.index('            if (cam != c)', start)
guard = source[start:end] + '            reached = true;\n        }'
reset = (root / 'SceneReset.cs').read_text(encoding='utf-8-sig')
assert reset.index('RunLod.SongEnded();') < reset.index('DecoDetach.OnTransition();')
assert reset.index('RunLod.SongEnded();') < reset.index('Resetting = true;')

program = r'''
using System;
using System.Collections.Generic;
using Vector3 = System.Numerics.Vector3;
class ObjectBase { public static implicit operator bool(ObjectBase o)=>o!=null; }
class GameObject:ObjectBase { public int layer; public bool activeSelf=true; public void SetActive(bool v){activeSelf=v;} }
class Renderer:ObjectBase { public GameObject gameObject=new GameObject(); }
class Camera:ObjectBase {
    public static event Action<Camera> onPreCull;
    public int removed;
    public void RemoveCommandBuffer(CameraEvent e, Buffer b){removed++;}
}
enum CameraEvent { BeforeForwardAlpha }
class Buffer { public bool released; public void Release(){released=true;} }
class scnEditor:ObjectBase { public static scnEditor instance; public bool playMode; }
class SceneReset { public static bool Resetting; }
class Config { public bool RunLod; }
class Main { public static Config Config; }
class Bounds {
    public Vector3 center, size;
    public Vector3 min=>center-size/2;
    public Vector3 max=>center+size/2;
}
class Projection {
    public Vector3 X, Y; public float Tx, Ty;
    public Vector3 MultiplyPoint(Vector3 p)=>new Vector3(Vector3.Dot(X,p)+Tx,Vector3.Dot(Y,p)+Ty,0);
}
class Mathf {
    public static float Min(float a,float b)=>Math.Min(a,b);
    public static float Max(float a,float b)=>Math.Max(a,b);
    public static float Clamp01(float x)=>Math.Clamp(x,0,1);
}
class Test {
    class Item { public float X0,Y0,X1,Y1; }
    class Cand { public bool Moved; public Renderer R; public int Layer; }
    static Projection vp;
    static bool On,anyMoved,reached;
    static Camera cam;
    static Buffer cb;
    static List<Cand> cands=new List<Cand>();
    static List<GameObject> comps=new List<GameObject>();
    static void PreCull(Camera c){}
'''
tests = r'''
    static void Check(bool b,string s){if(!b)throw new Exception(s);}
    static void Near(float a,float b){Check(Math.Abs(a-b)<2e-5f,$"bounds {a} != {b}");}
    static (float,float,float,float) Oracle(Bounds b){
        var p=vp.MultiplyPoint(b.center);var e=b.size/2;
        float dx=Vector3.Dot(Vector3.Abs(vp.X),e),dy=Vector3.Dot(Vector3.Abs(vp.Y),e);
        return ((p.X-dx+1)/2,(p.Y-dy+1)/2,(p.X+dx+1)/2,(p.Y+dy+1)/2);
    }
    static (float,float) OldX(Bounds b){
        var a=VPt(b.min);var c=VPt(b.max);
        var d=VPt(new Vector3(b.min.X,b.max.Y,b.center.Z));
        var e=VPt(new Vector3(b.max.X,b.min.Y,b.center.Z));
        return (Math.Min(Math.Min(a.X,c.X),Math.Min(d.X,e.X)),Math.Max(Math.Max(a.X,c.X),Math.Max(d.X,e.X)));
    }
    static void Arm(){
        On=anyMoved=true;reached=false;cam=new Camera();cb=new Buffer();
        cands.Clear();cands.Add(new Cand{R=new Renderer(),Layer=7,Moved=true});
        cands[0].R.gameObject.layer=31;comps.Clear();comps.Add(new GameObject());
    }
    static void Restored(Buffer old,Camera oldCam){
        Check(!On&&!anyMoved&&cb==null&&cam==null&&old.released&&oldCam.removed==1,"buffer restore");
        Check(!cands[0].Moved&&cands[0].R.gameObject.layer==7&&!comps[0].activeSelf,"original/composite restore");
    }
    static void Main(){
        // 45-degree XY camera roll plus depth tilt: the old sampled extrema
        // omit a corner that remains on screen.
        vp=new Projection{X=new Vector3(1,-1,1),Y=new Vector3(0,1,0),Tx=-3.5f};
        var b=new Bounds{center=Vector3.Zero,size=new Vector3(2,2,2)};
        var old=OldX(b);Check(old.Item2<0,"negative control not offscreen");
        Check(OnScreen(b,out _),"visible depth corner culled");
        ProjectBounds(b,out var x0,out var y0,out var x1,out var y1);
        Console.WriteLine($"REGRESSION old_xmax={old.Item2} corrected_xmax={x1} old_culled=True corrected_visible=True");
        var random=new Random(6602);
        for(int i=0;i<2000;i++){
            float Next()=>(float)(random.NextDouble()*4-2);
            vp=new Projection{X=new Vector3(Next(),Next(),Next()),Y=new Vector3(Next(),Next(),Next()),Tx=Next(),Ty=Next()};
            b=new Bounds{center=new Vector3(Next(),Next(),Next()),size=new Vector3(Math.Abs(Next()),Math.Abs(Next()),i%2==0?0:Math.Abs(Next()))};
            var o=Oracle(b);ProjectBounds(b,out x0,out y0,out x1,out y1);
            Near(x0,o.Item1);Near(y0,o.Item2);Near(x1,o.Item3);Near(y1,o.Item4);
            var item=new Item();ViewRect(b,item);
            Near(item.X0,Math.Clamp(o.Item1,0,1));Near(item.Y0,Math.Clamp(o.Item2,0,1));
            Near(item.X1,Math.Clamp(o.Item3,0,1));Near(item.Y1,Math.Clamp(o.Item4,0,1));
            Check(OnScreen(b,out _)==(o.Item3>=0&&o.Item4>=0&&o.Item1<=1&&o.Item2<=1),"culling oracle");
        }
        Main.Config=new Config{RunLod=true};scnEditor.instance=new scnEditor{playMode=false};
        Arm();var buffer=cb;var camera=cam;PreCullBody(camera);Restored(buffer,camera);Check(!reached,"editor guard");
        Arm();buffer=cb;camera=cam;scnEditor.instance.playMode=true;SceneReset.Resetting=true;
        PreCullBody(camera);Restored(buffer,camera);Check(!reached,"reset guard");SceneReset.Resetting=false;
        Arm();PreCullBody(cam);Check(reached&&On,"playback retained");
        Arm();buffer=cb;camera=cam;SongEnded();Restored(buffer,camera);Pause();
        Main.Config.RunLod=false;Arm();scnEditor.instance.playMode=false;PreCullBody(cam);
        Check(reached&&On,"manual paused diagnostic retained");SongEnded();Check(On,"manual SongEnded retained");
        Console.WriteLine("PASS actual RunLod methods: 2000 affine orthographic AABB cases, clamp/cull; editor/reset/playing guards; layers/composites/buffer restore; idempotent Pause; manual diagnostics");
    }
}
'''
# Match the public Unity vector component names in the extracted product methods.
geometry = geometry.replace('.x', '.X').replace('.y', '.Y').replace('.z', '.Z')
program = program.replace('class Main {', 'class AppMain {')
lifetime = lifetime.replace('Main.Config', 'AppMain.Config')
guard = guard.replace('Main.Config', 'AppMain.Config')
tests = tests.replace('Main.Config', 'AppMain.Config')
out = root / 'out/tester-black-triangle/bounds-fixture'
out.mkdir(parents=True, exist_ok=True)
(out / 'Bounds.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="Program.cs" /></ItemGroup></Project>', encoding='utf-8')
(out / 'Program.cs').write_text(program + geometry + lifetime + guard + tests, encoding='utf-8')
run = subprocess.run(['dotnet', 'run', '--project', str(out / 'Bounds.csproj'), '-c', 'Release'], cwd=root, capture_output=True, text=True)
log = run.stdout + run.stderr
(out.parent / 'bounds-fixture.txt').write_text(log, encoding='utf-8')
print(log)
raise SystemExit(run.returncode)
