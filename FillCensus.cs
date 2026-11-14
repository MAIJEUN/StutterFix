#if DEV || AUTOTEST
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
namespace StutterFix
{
    // Explicit live diagnostic lease only. No permanent Update/camera subscription.
    internal sealed class FillCensus : MonoBehaviour
    {
        sealed class Row
        {
            internal int Id,Seen,Eligible,Changed,GeometryChanged,ColorChanged,SpriteChanged,LastFrame;
            internal string Name,Deco,Texture,Shader,Blend,Parent,Approx,State,Sort,MaterialState;
            internal double Area,Alive,Faint,LastArea; internal bool AlphaKnown=true,Grab;
            internal Vector4[] Last;internal Color Color;internal int Sprite;
        }
        static FillCensus active;static bool waiting,success;
        readonly Dictionary<int,Row> rows=new Dictionary<int,Row>();
        readonly Dictionary<Texture2D,float> alphas=new Dictionary<Texture2D,float>();
        readonly Dictionary<string,int> states=new Dictionary<string,int>();
        readonly Dictionary<string,double> shaderAreas=new Dictionary<string,double>(),blendAreas=new Dictionary<string,double>();
        readonly Dictionary<string,string[]> shaderMetadata=new Dictionary<string,string[]>();
        readonly Dictionary<int,string> decoIdentities=new Dictionary<int,string>();
        double grabArea,grabCopyUpper;int grabRendererFrames,unknownGrabRendererFrames;
        Camera camera;int count,target,lastFrame=-1;float deadline;double pixels,total;
        string path;StreamWriter detail;
        static string F(double v)=>v.ToString("F3",CultureInfo.InvariantCulture);
        static string Clean(string s)=>(s??"unknown").Replace('\t',' ').Replace('\r',' ').Replace('\n',' ');
        static void Log(string s)=>Main.Entry.Logger.Log("[fillcensus] "+s);
        internal static bool Step(string arg)
        {
            if(arg.Trim()=="stop") {Stop();waiting=false;return true;}
            if(!waiting)
            {
                int n=30;if(arg.Trim().Length>0 && !int.TryParse(arg.Trim(),out n))throw new ArgumentException("fillcensus [1..300]|stop");
                if(n<1||n>300)throw new ArgumentException("fillcensus frames 1..300");
                if(active)throw new InvalidOperationException("fillcensus busy");
                if(Main.Config.GpuDeco!=0 || Main.Config.RunLod || GpuPlaybackState.Paused)
                    throw new InvalidOperationException("fillcensus requires live rlNR: GpuDeco=0 RunLod=false unpaused");
                success=false;waiting=true;
                try
                {
                    var c=GpuDecoScene.FindMain();if(!c)throw new InvalidOperationException("main camera missing");
                    active=new GameObject("StutterFix.FillCensus").AddComponent<FillCensus>();DontDestroyOnLoad(active.gameObject);
                    active.camera=c;active.target=n;active.deadline=Time.realtimeSinceStartup+600;
                    active.path=Path.Combine(Main.Entry.Path,"fillcensus-"+DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
                    active.detail=new StreamWriter(active.path+"-frames.tsv");
                    active.detail.WriteLine("frame\tid\tname\tstate\tareaPx\talphaPositiveEstimatePx\tcolorAlpha\tgeometry\tshader\tblend\ttexture\tsort\tdecoration\tparents");
                    // All diagnostic blits/readbacks precede the camera query Begin.
                    // Never issue our own draws while the main-camera pipeline scope is open.
                    active.LoadShaderMetadata();active.DecorationIdentities();active.PrewarmAlpha();active.deadline=Time.realtimeSinceStartup+600;
                    Camera.onPreRender+=active.Capture;
                    Log("started frames="+n+" camera="+c.name+" target="+(c.targetTexture?c.targetTexture.name:"screen")+" diagnostics perturb CPU/GPU; exclude this window from FPS comparisons");
                }
                catch {Stop();waiting=false;throw;}
            }
            if(active)return false;waiting=false;
            if(!success)throw new InvalidOperationException("fillcensus failed or cancelled; see log");return true;
        }
        void Update() {if(Time.realtimeSinceStartup>deadline) {Log("FAILED timeout");Stop();}}
        void OnDestroy() {Camera.onPreRender-=Capture;if(detail!=null){detail.Dispose();detail=null;}if(active==this)active=null;}
        internal static void Stop()
        {
            if(!active)return;var a=active;Camera.onPreRender-=a.Capture;active=null;
            if(a.detail!=null){a.detail.Dispose();a.detail=null;}a.rows.Clear();a.alphas.Clear();Destroy(a.gameObject);
        }
        static string Members(object o)
        {
            if(o==null)return "none";var parts=new List<string>();
            foreach(var field in o.GetType().GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance))
            {
                string k=field.Name.ToLowerInvariant();
                if(!(k.Contains("parallax")||k.Contains("follow")||k.Contains("tag")||k=="id"||k.Contains("decorationid")))continue;
                try {parts.Add(field.Name+"="+Clean(Convert.ToString(field.GetValue(o),CultureInfo.InvariantCulture)));}catch{}
            }
            return parts.Count==0?"unknown":string.Join(";",parts);
        }
        static string Parents(Renderer r)
        {
            var s=new List<string>();for(var t=r.transform;t;t=t.parent)
            {var p=t.GetComponent<scrParallax>();s.Add(t.name+(p?"[scrParallax:"+Members(p)+"]":""));}
            return Clean(string.Join("/",s))+";follow=inspect_fields_or_unknown";
        }
        void PrewarmAlpha()
        {
            foreach(var r in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if(!r||!r.gameObject.scene.IsValid())continue;
                var sr=r as SpriteRenderer;var m=r.sharedMaterial;
                var t=sr&&sr.sprite?sr.sprite.texture:m?m.mainTexture as Texture2D:null;
                if(t&&!alphas.ContainsKey(t))SampleAlpha(t);
            }
            Log("alpha preflight textures="+alphas.Count+";later new textures remain unknown (no diagnostic draws inside camera queries)");
        }
        void DecorationIdentities()
        {
            var manager=scrDecorationManager.instance;if(!manager)return;
            var tags=new Dictionary<int,List<string>>();
            if(manager.taggedDecorations!=null)foreach(var pair in manager.taggedDecorations)
                if(pair.Value!=null)foreach(var d in pair.Value)if(d)
                {int id=d.GetInstanceID();if(!tags.TryGetValue(id,out var list))tags[id]=list=new List<string>();list.Add(pair.Key);}
            for(int i=0;i<manager.allDecorations.Count;i++)
            {
                var d=manager.allDecorations[i];if(!d)continue;int id=d.GetInstanceID();tags.TryGetValue(id,out var list);
                decoIdentities[id]="instance="+id+";startupManagerIndex0="+i+";tags="+(list==null?"none":string.Join(",",list));
            }
        }
        void LoadShaderMetadata()
        {
            using(var stream=typeof(FillCensus).Assembly.GetManifestResourceStream("StutterFix.fillshaders"))
            {
                if(stream==null)throw new InvalidOperationException("fill shader metadata missing");
                using(var reader=new StreamReader(stream))
                {string line;while((line=reader.ReadLine())!=null){var cells=line.Split('\t');if(cells.Length==3)shaderMetadata[cells[0]]=cells;}}
            }
        }
        float Alpha(Texture2D t)=>t&&alphas.TryGetValue(t,out float value)?value:-1;
        float SampleAlpha(Texture2D t)
        {
            if(!t)return -1;if(alphas.TryGetValue(t,out float value))return value;
            // Point-like low-resolution GPU sampling, NOT a coverage bound. Atlas/repeat UVs are not weighted.
            RenderTexture rt=null;Texture2D cpu=null;var old=RenderTexture.active;
            try
            {
                if(!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.ARGBFloat))return alphas[t]=-1;
                rt=RenderTexture.GetTemporary(64,64,0,RenderTextureFormat.ARGBFloat,RenderTextureReadWrite.Linear);
                Graphics.Blit(t,rt);RenderTexture.active=rt;
                cpu=new Texture2D(64,64,TextureFormat.RGBAFloat,false,true);cpu.ReadPixels(new Rect(0,0,64,64),0,0);cpu.Apply();
                var data=cpu.GetPixels();int alive=0;foreach(var p in data)if(p.a>0)alive++;value=(float)alive/data.Length;
            }
            catch {value=-1;}
            finally {RenderTexture.active=old;if(rt)RenderTexture.ReleaseTemporary(rt);if(cpu)Destroy(cpu);}
            alphas[t]=value;return value;
        }
        static readonly int[] Box={0,2,3,0,3,1,4,5,7,4,7,6,0,1,5,0,5,4,2,6,7,2,7,3,0,4,6,0,6,2,1,3,7,1,7,5};
        static double Geometry(Renderer r,Camera c,out Vector4[] projected,out string approximation)
        {
            var sr=r as SpriteRenderer;var matrix=c.projectionMatrix*c.worldToCameraMatrix;
            if(sr && sr.sprite && sr.drawMode==SpriteDrawMode.Simple)
            {
                var vertices=sr.sprite.vertices;var triangles=sr.sprite.triangles;projected=new Vector4[vertices.Length];
                matrix*=r.localToWorldMatrix;
                for(int i=0;i<vertices.Length;i++)projected[i]=matrix*new Vector4(vertices[i].x*(sr.flipX?-1:1),vertices[i].y*(sr.flipY?-1:1),0,1);
                double a=0;for(int i=0;i+2<triangles.Length;i+=3)a+=FillGeometry.Area(FillGeometry.Clip(projected[triangles[i]],projected[triangles[i+1]],projected[triangles[i+2]]));
                approximation="sprite-triangles;shader-deformation-unmodeled";return a*c.pixelWidth*c.pixelHeight/4.0;
            }
            projected=new Vector4[8];var b=r.bounds;for(int i=0;i<8;i++)
            {var v=b.center+Vector3.Scale(b.extents,new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1));projected[i]=matrix*new Vector4(v.x,v.y,v.z,1);}
            var points=new List<Vector2>();for(int i=0;i<Box.Length;i+=3)points.AddRange(FillGeometry.Clip(projected[Box[i]],projected[Box[i+1]],projected[Box[i+2]]));
            // Include frustum vertices lying inside the box (also covers a box enclosing the frustum).
            for(int x=0;x<2;x++)for(int y=0;y<2;y++)for(int z=0;z<2;z++)
                if(b.Contains(c.ViewportToWorldPoint(new Vector3(x,y,z==0?c.nearClipPlane:c.farClipPlane))))
                    points.Add(new Vector2(x*2-1,y*2-1));
            approximation=r is ParticleSystemRenderer?"bounds-hull;particle-overlap-unknown":sr?"bounds-hull;sliced-tiled":"bounds-hull;mesh-approx";
            return FillGeometry.Area(FillGeometry.Hull(points))*c.pixelWidth*c.pixelHeight/4.0;
        }
        static bool Same(Vector4[] a,Vector4[] b)
        {if(a==null||a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(!a[i].Equals(b[i]))return false;return true;}
        void Flag(string name,bool set){if(!set)return;states.TryGetValue("flag/"+name,out int n);states["flag/"+name]=n+1;}
        void Capture(Camera c)
        {
            if(c!=camera||Time.frameCount==lastFrame)return;lastFrame=Time.frameCount;
            try
            {
                if(GpuPlaybackState.Paused||Main.Config.GpuDeco!=0||Main.Config.RunLod)throw new InvalidOperationException("live rlNR state changed");
                double screen=(double)c.pixelWidth*c.pixelHeight;if(screen<=0)throw new InvalidOperationException("empty camera viewport");
                if(count>0 && pixels!=screen)throw new InvalidOperationException("camera dimensions changed");pixels=screen;
                foreach(var r in Resources.FindObjectsOfTypeAll<Renderer>())
                {
                    if(!r||!r.gameObject.scene.IsValid()||r is SpriteMask)continue;
                    int id=r.GetInstanceID();if(!rows.TryGetValue(id,out Row row))
                    {
                        var deco=r.GetComponentInParent<scrVisualDecoration>();
                        string identity="none";if(deco&&!decoIdentities.TryGetValue(deco.GetInstanceID(),out identity))identity="instance="+deco.GetInstanceID()+";startupManagerIndex0=unknown";
                        row=new Row{Id=id,Name=Clean(r.name),Deco=identity,Parent=Parents(r)};rows[id]=row;
                    }
                    row.Seen++;var sr=r as SpriteRenderer;var m=r.sharedMaterial;
                    Color color=sr?sr.color:Color.white;if(m&&m.HasProperty("_Color"))color*=m.GetColor("_Color");
                    string materialState=m?m.GetInstanceID()+":"+string.Join(";",GpuDecoScene.Properties(m).Select(p=>p.Key+"="+p.Value)):"none";
                    materialState+=";sort="+r.sortingLayerID+"/"+r.sortingOrder;
                    var mpb=new MaterialPropertyBlock();r.GetPropertyBlock(mpb);
                    Flag("disabled",!r.enabled);Flag("inactive",!r.gameObject.activeInHierarchy);
                    Flag("forceRenderingOff",r.forceRenderingOff);Flag("isVisibleFalse",!r.isVisible);Flag("baseColorAlphaZero",color.a<=0);
                    string state=!r.gameObject.activeInHierarchy?"inactive":!r.enabled?"disabled":r.forceRenderingOff?"forceRenderingOff":
                        (c.cullingMask&(1<<r.gameObject.layer))==0?"cameraMask":r.shadowCastingMode==ShadowCastingMode.ShadowsOnly?"shadowsOnly":
                        sr&&!sr.sprite?"noSprite":!m?"noMaterial":!r.isVisible?"isVisibleFalse":color.a<=0&&mpb.isEmpty?"colorAlphaZero":"candidate";
                    states.TryGetValue(state,out int ns);states[state]=ns+1;
                    var texture=sr&&sr.sprite?sr.sprite.texture:(m?m.mainTexture as Texture2D:null);
                    float alpha=sr&&sr.sprite&&sr.sprite.associatedAlphaSplitTexture?-1:Alpha(state=="candidate"?texture:null);
                    double area=Geometry(r,c,out Vector4[] projected,out string approx);
                    if(double.IsNaN(area)||double.IsInfinity(area))throw new InvalidOperationException("nonfinite geometry renderer="+id);
                    if(state!="candidate")area=0;
                    int sprite=sr&&sr.sprite?sr.sprite.GetInstanceID():0;
                    bool geometry=!Same(row.Last,projected),cc=!row.Color.Equals(color),sp=row.Sprite!=sprite;
                    if(row.Last!=null) {if(geometry)row.GeometryChanged++;if(cc)row.ColorChanged++;if(sp)row.SpriteChanged++;
                        if(geometry||cc||sp||row.MaterialState!=materialState||row.State!=state||row.LastFrame!=count-1||!mpb.isEmpty)row.Changed++;}
                    row.MaterialState=materialState;
                    row.Last=projected;row.Color=color;row.Sprite=sprite;row.LastFrame=count;row.State=state;
                    row.Approx=approx+(!mpb.isEmpty?";propertyBlock-unmodeled":"")+(r.sharedMaterials.Length>1?";multiple-materials-unmodeled":"");
                    row.Shader=m&&m.shader?Clean(m.shader.name):"none";
                    row.Blend=m&&m.HasProperty("_SrcBlend")&&m.HasProperty("_DstBlend")?m.GetFloat("_SrcBlend")+"/"+m.GetFloat("_DstBlend"):"unknown";
                    bool catalogued=shaderMetadata.TryGetValue(row.Shader,out string[] meta);
                    if(row.Blend=="unknown" && catalogued)row.Blend="compiled-RGBsrc/dst/Asrc/dst="+meta[2];
                    shaderAreas.TryGetValue(row.Shader,out double sa);shaderAreas[row.Shader]=sa+area;
                    blendAreas.TryGetValue(row.Blend,out double ba);blendAreas[row.Blend]=ba+area;
                    row.Texture=texture?Clean(texture.name)+" "+texture.width+"x"+texture.height+" alphaPositive="+F(alpha):"unknown";
                    row.Sort=SortingLayer.IDToName(r.sortingLayerID)+"/"+r.sortingLayerID+"/"+r.sortingOrder+" queue="+(m?m.renderQueue:0);
                    row.Parent=Parents(r);row.Name=Clean(r.name)+" tag="+Clean(r.tag);
                    row.Grab=catalogued?int.Parse(meta[1],CultureInfo.InvariantCulture)>0:row.Shader.IndexOf("grab",StringComparison.OrdinalIgnoreCase)>=0;
                    if(area>0 && row.Grab){grabRendererFrames++;grabArea+=area;grabCopyUpper+=screen*(catalogued?int.Parse(meta[1],CultureInfo.InvariantCulture):1);}
                    if(area>0 && !catalogued)unknownGrabRendererFrames++;
                    row.Area+=area;row.LastArea=area;row.AlphaKnown &= alpha>=0 || area==0;row.Alive+=area*Math.Max(0,alpha);
                    if(area>0){row.Eligible++;if(color.a<0.05f&&mpb.isEmpty)row.Faint+=area;}total+=area;
                    detail.WriteLine(string.Join("\t",count,id,row.Name,state+";isVisible="+r.isVisible+";enabled="+r.enabled+";active="+r.gameObject.activeInHierarchy+";forceRenderingOff="+r.forceRenderingOff,F(area),alpha<0?"unknown":F(area*alpha),F(color.a),row.Approx,row.Shader,row.Blend,row.Texture,row.Sort,Clean(row.Deco),row.Parent));
                }
                count++;if(count==target){Report();success=true;Stop();}
            }
            catch(Exception e){Log("FAILED "+e);Stop();}
        }
        void Report()
        {
            detail.Flush();using(var output=new StreamWriter(path+"-summary.txt"))
            {
                Action<string> write=s=>{output.WriteLine(s);Log(s);};
                write("frames="+count+" screenPx="+pixels+" rendererIDs="+rows.Count+" meanAreaPx="+F(total/count)+" geometricLayers="+F(total/count/pixels));
                write("scope=renderer-geometry-once;not-draws-or-PSInvocations;depth-occlusion-shader-clipping-multipass-unmodeled;isVisible-is-any-camera;alpha=64x64-float-filtered-whole-texture-estimate-atlas-UV-repeat-unweighted;shaderMetadata=installed-source-name-match-only-not-runtime-shader-identity");
                foreach(var state in states)write("stateRendererFrames "+state.Key+"="+state.Value);
                var all=rows.Values.ToArray();
                foreach(var group in shaderAreas)write("shader "+group.Key+" meanPx="+F(group.Value/count));
                foreach(var group in blendAreas)write("blend "+group.Key+" meanPx="+F(group.Value/count));
                Func<Row,bool> fixedLayer=r=>count>1&&r.Seen==count&&r.Eligible==count&&r.Changed==0&&r.Approx=="sprite-triangles;shader-deformation-unmodeled";
                write("unchangedScreenGeometryCandidateMeanPx="+F(all.Where(fixedLayer).Sum(r=>r.Area)/count)+" changedOrIntermittentMeanPx="+F(all.Where(r=>!fixedLayer(r)).Sum(r=>r.Area)/count)+" faintMeanPx="+F(all.Sum(r=>r.Faint)/count)+" static-is-candidate-not-cache-proof-material-texture-animation-order-unverified");
                write("grabShaderRendererFrames="+grabRendererFrames+" meanRendererPx="+F(grabArea/count)+" copyPixelUpperEstimatePerFrame="+F(grabCopyUpper/count)+" uncataloguedRendererFrames="+unknownGrabRendererFrames+" actualGrabPassDrawCount=unknown-batching-shared-named-grabs;uncatalogued-uses-name-heuristic");
                write("TOP40 meanPx alphaPositiveEstimatePx id name colorAlpha changes geometryChanges colorChanges spriteChanges seen eligible shader blend texture sort deco parents geometry");
                foreach(var r in all.OrderByDescending(r=>r.Area).Take(40))
                    write(string.Join("\t",F(r.Area/count),r.AlphaKnown?F(r.Alive/count):"unknown",r.Id,r.Name,F(r.Color.a),r.Changed,r.GeometryChanged,r.ColorChanged,r.SpriteChanged,r.Seen,r.Eligible,r.Shader,r.Blend,r.Texture,r.Sort,Clean(r.Deco),r.Parent,r.Approx));
            }
            Log("complete files="+path+"-summary.txt ; "+path+"-frames.tsv");
        }
    }
}
#endif
