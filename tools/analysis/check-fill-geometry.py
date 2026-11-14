"""Execute the actual homogeneous clipping/hull code with managed vector stubs; no Unity/game process."""
from pathlib import Path
import subprocess

root = Path(__file__).resolve().parents[2]
out = root / 'out/fillcensus/geometry-fixture'
out.mkdir(parents=True, exist_ok=True)
(out / 'Test.csproj').write_text('''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><DefineConstants>DEV</DefineConstants><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="Program.cs" /><Compile Include="../../../FillGeometry.cs" /></ItemGroup></Project>''', encoding='utf-8')
(out / 'Program.cs').write_text(r'''
using System;
using System.Collections.Generic;
using StutterFix;
namespace UnityEngine {
    internal struct Vector2 {
        public float x,y;public Vector2(float x,float y){this.x=x;this.y=y;}
    }
    internal struct Vector4 {
        public float x,y,z,w;public Vector4(float x,float y,float z,float w){this.x=x;this.y=y;this.z=z;this.w=w;}
        public static Vector4 operator +(Vector4 a,Vector4 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z,a.w+b.w);
        public static Vector4 operator -(Vector4 a,Vector4 b)=>new(a.x-b.x,a.y-b.y,a.z-b.z,a.w-b.w);
        public static Vector4 operator *(Vector4 a,float s)=>new(a.x*s,a.y*s,a.z*s,a.w*s);
    }
}
class Program {
    static UnityEngine.Vector4 V(float x,float y,float z=0,float w=1)=>new(x,y,z,w);
    static void Expect(double value,double expected,string name){if(Math.Abs(value-expected)>1e-6)throw new Exception(name+": "+value+" != "+expected);}
    static double Triangle(UnityEngine.Vector4 a,UnityEngine.Vector4 b,UnityEngine.Vector4 c)=>FillGeometry.Area(FillGeometry.Clip(a,b,c));
    static void Main(){
        Expect(Triangle(V(-1,-1),V(1,-1),V(1,1)),2,"inside half screen");
        Expect(Triangle(V(-10,-10),V(10,-10),V(0,10)),4,"oversized clipped to screen");
        Expect(Triangle(V(2,0),V(3,0),V(2,1)),0,"offscreen rejected");
        Expect(Triangle(V(-1,-1,-2),V(1,-1,-2),V(0,1,-2)),0,"behind near plane");
        Expect(Triangle(V(-1,-1,2),V(1,-1,2),V(0,1,2)),0,"beyond far plane");
        Expect(Triangle(V(-1,-1),V(1,-1),V(0,1,-2)),1.5,"near-plane crossing");
        Expect(Triangle(V(-2,-2,0,2),V(2,-2,0,2),V(2,2,0,2)),2,"homogeneous perspective divide");
        Expect(Triangle(V(1,1),V(1,-1),V(-1,-1)),2,"flipped winding");
        Expect(Triangle(V(0,0),V(0,0),V(0,0)),0,"degenerate");
        var points=new List<UnityEngine.Vector2>{new(-1,-1),new(1,-1),new(1,1),new(-1,1),new(0,0),new(-1,-1)};
        Expect(FillGeometry.Area(FillGeometry.Hull(points)),4,"bounds silhouette union without six-face double count");
        Expect(FillGeometry.Area(FillGeometry.Hull(new())),0,"empty hull");
        Console.WriteLine("PASS actual FillGeometry: 11 analytic clipping/hull cases incl giant/fullscreen, offscreen, near/far, perspective, flips, degenerate and bounds silhouette");
    }
}
''', encoding='utf-8')
r = subprocess.run(['dotnet', 'run', '--project', str(out / 'Test.csproj'), '-c', 'Release'], cwd=root, capture_output=True, text=True)
(out.parent / 'geometry-fixture.txt').write_text(r.stdout+r.stderr, encoding='utf-8')
print(r.stdout+r.stderr)
raise SystemExit(r.returncode)
