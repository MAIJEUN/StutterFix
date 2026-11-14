#if AUTOTEST
using System;
using System.IO;
using System.Linq;
using UnityEngine;
namespace StutterFix
{
    internal static partial class DecoBake
    {
        static AssetBundle singleArrayBundle;
        static Shader singleArrayShader;
        static int[] singleArrayProof;
        static int[] singleTileFlags;
        static bool singleArrayReference,singleArrayControlled;
        static bool singleArrayOptimize=true;
        static bool singleArrayLean;
        static bool singleArrayFastPixel;
        internal static void RequireSingleArrayDiagnostics()
        {RequireLive(!singleArrayFastPixel,"singlearray fast pixel: disable/release experiment before LOD or shader diagnostics");}
        static bool singleControlDirectOnly;
        static Texture2DArray singleArrayControl;
        static Texture2D single2DControl;
        static string SingleArrayCommand(string path)
        {
            if(path=="state")return SingleArrayState();
            if(path=="fast on"||path=="fast off")
            {
                RequireLive(singleArrayShader&&!gpuLivePrepared&&!gpuLiveActive,"singlearray fast requires loaded shader before preparation");
                bool on=path=="fast on";
                RequireLive(!on || singleArrayShader.keywordSpace.FindKeyword("SF_FAST_PIXEL").isValid,"fast pixel variant missing in bundle");
                RequireLive(!on || (!RenderLod.Enabled && Shader.GetGlobalFloat("_SfDebugFlat")==0 && Shader.GetGlobalFloat("_SfDebugMode")==0 && Shader.GetGlobalFloat("_SfNoDiscard")==0),"fast pixel requires normal rendering, LOD and diagnostics off");
                singleArrayFastPixel=on;return SingleArrayState();
            }
            if(path=="lean on"||path=="lean off")
            {
                RequireLive(singleArrayShader&&!gpuLivePrepared&&!gpuLiveActive,"singlearray lean policy requires loaded shader before preparation");
                singleArrayLean=path=="lean on";return SingleArrayState();
            }
            if(path=="optimize on"||path=="optimize off")
            {
                RequireLive(singleArrayShader&&!gpuLivePrepared&&!gpuLiveActive,"singlearray optimize policy requires loaded shader before preparation");
                singleArrayOptimize=path=="optimize on";return SingleArrayState();
            }
            if(path.StartsWith("reference ",StringComparison.Ordinal) || path.StartsWith("control ",StringComparison.Ordinal) || path.StartsWith("control2d ",StringComparison.Ordinal))
            {
                RequireLive(gpuLiveActive&&gpuCollapseOn&&GpuPlaybackState.Paused&&singleArrayShader,"singlearray comparison requires paused active candidate");
                bool reference=path.StartsWith("reference ",StringComparison.Ordinal);
                bool directControl=path.StartsWith("control2d ",StringComparison.Ordinal);
                string value=path.Substring(reference||directControl?10:8);RequireLive(value=="on"||value=="off","singlearray reference|control|control2d on|off");
                bool on=value=="on";
                if(reference){RequireLive(!singleArrayControlled,"restore control before shader comparison");singleArrayReference=on;}
                else{RequireLive(!singleArrayReference,"restore candidate shader before negative control");singleArrayControlled=on;singleControlDirectOnly=directControl&&on;}
                var seen=new System.Collections.Generic.HashSet<Material>();
                foreach(var s in gpuCollapseSpans)
                {
                    if(!s.Material || (s.Material.shader!=singleArrayShader&&s.Material.shader!=gpuCollapseShader) || !seen.Add(s.Material))continue;
                    var m=s.Material;m.shader=singleArrayReference?gpuCollapseShader:singleArrayShader;
                    m.DisableKeyword(CollapseNoDirectKeyword);m.DisableKeyword(CollapseFewKeyword);m.DisableKeyword("SF_SINGLE_ARRAY");m.DisableKeyword("SF_SINGLE_2D");
                    if(s.NoDirect)m.EnableKeyword(CollapseNoDirectKeyword);if(s.Few!=null)m.EnableKeyword(CollapseFewKeyword);
                    BindCollapseTextures(m,s.NoDirect,s.Few);
                    if(!singleArrayReference)
                    {
                        BindSingleArray(m,s.SingleArray,s.SingleTile);
                        if(singleArrayControlled&&s.SingleArray>=0&&(!singleControlDirectOnly||s.SingleArray>=DrawCollapsePolicy.ArrayBindings))
                        {
                            if(s.SingleArray>=DrawCollapsePolicy.ArrayBindings)
                            {
                                if(!single2DControl)
                                {
                                    single2DControl=new Texture2D(1,1,TextureFormat.RGBA32,false,true);
                                    single2DControl.SetPixels32(new[]{new Color32(255,0,255,255)});single2DControl.Apply(false,true);
                                }
                                m.SetTexture("_SfSingle2D",single2DControl);
                            }
                            else
                            {
                                if(!singleArrayControl)
                                {
                                    int depth=gpuCollapseArrays.Max(a=>a.depth);
                                    singleArrayControl=new Texture2DArray(1,1,depth,TextureFormat.RGBA32,false,true);
                                    for(int i=0;i<depth;i++)singleArrayControl.SetPixels32(new[]{new Color32(255,0,255,255)},i);
                                    singleArrayControl.Apply(false,true);
                                }
                                m.SetTexture("_SfSingleArray",singleArrayControl);
                            }
                        }
                    }
                }
                return SingleArrayState()+" material_IDs_unchanged=True comparison_only=True";
            }
            RequireLive(!gpuLivePrepared&&!gpuLiveActive&&gpuCollapseSpans.Count==0,"singlearray requires unprepared boundary");
            ResetSingleArrayExperiment();
            if(path=="off")return "SINGLE_ARRAY_OFF";
            try
            {
                RequireLive(Path.IsPathRooted(path)&&File.Exists(path),"singlearray requires an explicit existing local bundle path");
                singleArrayBundle=AssetBundle.LoadFromFile(path);
                RequireLive(singleArrayBundle,"singlearray bundle unavailable");
                singleArrayShader=singleArrayBundle.LoadAsset<Shader>("Assets/Shaders/SingleArray.shader");
                RequireLive(singleArrayShader&&singleArrayShader.isSupported&&singleArrayShader.name=="StutterFix/ResearchSingleArray","singlearray shader unavailable");
                RequireLive(singleArrayShader.keywordSpace.FindKeyword("SF_SINGLE_ARRAY").isValid&&singleArrayShader.keywordSpace.FindKeyword("SF_SINGLE_2D").isValid,"single resource shader variants missing");
                return "SINGLE_ARRAY_ON shader="+singleArrayShader.name+" default=False research_only=True";
            }
            catch{ResetSingleArrayExperiment();throw;}
        }
        internal static void ResetSingleArrayExperiment()
        {
            // Never unload a shader from underneath an unreleased live renderer.
            if(gpuLiveActive || gpuCollapseSpans.Count!=0)return;
            singleArrayProof=singleTileFlags=null;singleArrayShader=null;singleArrayReference=singleArrayControlled=singleControlDirectOnly=false;singleArrayOptimize=true;singleArrayLean=singleArrayFastPixel=false;
            if(singleArrayControl)UnityEngine.Object.Destroy(singleArrayControl);singleArrayControl=null;
            if(single2DControl)UnityEngine.Object.Destroy(single2DControl);single2DControl=null;
            try
            {
                if(singleArrayBundle){singleArrayBundle.Unload(true);GpuLog("SINGLE_ARRAY_RELEASE bundle_unloaded=True proof_released=True");}
            }
            catch(Exception e){GpuLog("SINGLE_ARRAY_RELEASE bundle_unloaded=UNCONFIRMED error="+e.GetType().Name);}
            finally{singleArrayBundle=null;}
        }
        static string SingleArrayState()
        {
            // Explicit command boundaries only. Do not scan materials each frame.
            int active=0,lean=0,fixedDirect=0,mismatch=0,textureUnavailable=0;
            foreach(var s in gpuCollapseSpans)
            {
                var m=s.Material;if(!m || !singleArrayShader || m.shader!=singleArrayShader)continue;
                bool array=m.IsKeywordEnabled("SF_SINGLE_ARRAY"),direct=m.IsKeywordEnabled("SF_SINGLE_2D"),lv=m.IsKeywordEnabled("SF_LEAN_VERTEX");
                bool expected=singleArrayOptimize&&s.SingleArray>=0;
                bool tileLinear=m.IsKeywordEnabled("SF_TILE_LINEAR"),tilePoint=m.IsKeywordEnabled("SF_TILE_POINT");
                if(tileLinear||tilePoint)fixedDirect++;
                bool expectedTile=singleArrayOptimize&&singleArrayFastPixel&&s.SingleTile>=0;
                if((tileLinear||tilePoint)!=expectedTile || tileLinear&&tilePoint || expectedTile&&tilePoint!=(s.SingleTile==1))mismatch++;
                if(array||direct)active++;if(lv)lean++;
                if((array||direct)!=expected || array&&direct || lv!=(expected&&singleArrayLean) || m.IsKeywordEnabled("SF_FAST_PIXEL")!=singleArrayFastPixel){mismatch++;continue;}
                if(!expected || singleArrayControlled)continue;
                bool isArray=s.SingleArray<DrawCollapsePolicy.ArrayBindings;
                Texture texture=isArray?gpuCollapseArrays[s.SingleArray]:gpuCollapseDirect[s.SingleArray-DrawCollapsePolicy.ArrayBindings];
                if(array!=isArray)mismatch++;
                // Resources absent from ShaderLab Properties can render while
                // Unity's material getter reports null. Keep that UNCONFIRMED,
                // separate from a returned-but-wrong resource or keyword.
                var bound=m.GetTexture(isArray?"_SfSingleArray":"_SfSingle2D");
                if(!bound)textureUnavailable++;else if(bound!=texture)mismatch++;
            }
            return "SINGLE_ARRAY_STATE loaded="+(bool)singleArrayShader+" reference="+singleArrayReference+" control="+singleArrayControlled
            +" optimize="+singleArrayOptimize+" selected_spans="+gpuCollapseSpans.Count(s=>s.SingleArray>=0)+" direct_spans="+gpuCollapseSpans.Count(s=>s.SingleArray>=DrawCollapsePolicy.ArrayBindings)
            +" direct_control_only="+singleControlDirectOnly+" bundle_alive="+(bool)singleArrayBundle+" lean="+singleArrayLean+" fast_pixel="+singleArrayFastPixel+" fixed_direct_spans="+gpuCollapseSpans.Count(s=>s.SingleTile>=0)+" actual_fixed_direct_spans="+fixedDirect+" actual_specialized_spans="+active+" actual_lean_spans="+lean+" actual_material_mismatches="+mismatch+" texture_query_unavailable="+textureUnavailable;
        }
        static void PrepareSingleArrayProof(GpuLiveSlice source)
        {
            singleArrayProof=singleTileFlags=null;if(!singleArrayShader)return;
            RequireLive(gpuImageCollapseAddresses!=null,"singlearray missing texture registry");
            singleArrayProof=new int[source.Data.Items.Length];
            singleTileFlags=Enumerable.Repeat(-1,source.Data.Items.Length).ToArray();
            for(int i=0;i<singleArrayProof.Length;i++)
            {
                var proof=new SingleArrayProof();
                ObserveSingleAddress(ref proof,source.Data.Items[i].TextureId);
                int owner=gpuLiveOwnedIndices[source.MasterItems[i]];
                if(GpuImageActor(owner))
                {
                    if(gpuImageTimeline==null || gpuImageVariants==null)proof.Unknown();
                    else foreach(int e in gpuImageTimeline.EventsForDecoration(owner))
                    {
                        var v=gpuImageVariants[gpuImageTimeline.Event(e).Selector];
                        if(v.Status!=GpuDecoImageVariant.Kind.ReadyQuad)proof.Unknown();
                        else ObserveSingleAddress(ref proof,v.TextureId);
                    }
                }
                singleArrayProof[i]=proof.Binding;
            }
            GpuLog("SINGLE_ARRAY_PROOF items="+singleArrayProof.Length+" proven="+singleArrayProof.Count(i=>i>=0)+" unproven="+singleArrayProof.Count(i=>i<0)+" uses_all_future_events=True pixel_or_FPS_acceptance=False");
        }
        static void ObserveSingleAddress(ref SingleArrayProof proof,int texture)
        {
            int limit=DrawCollapsePolicy.ArrayBindings+gpuCollapseDirect.Count;
            if(!gpuImageCollapseAddresses.TryGetValue(texture,out var a) || a.x!=Mathf.Floor(a.x) || a.x<0 || a.x>=limit
                || a.x<DrawCollapsePolicy.ArrayBindings&&a.x>=gpuCollapseArrays.Count){proof.Unknown();return;}
            proof.Observe((int)a.x,limit);
        }
        static int SingleArrayForSpan(int[] items,bool grab)
        {
            if(grab || !singleArrayShader || singleArrayProof==null)return -1;
            var proof=new SingleArrayProof();
            foreach(int it in items)
            {
                int row=CR(it);
                if((uint)row>=(uint)singleArrayProof.Length){proof.Unknown();break;}
                proof.Observe(singleArrayProof[row],DrawCollapsePolicy.ArrayBindings+gpuCollapseDirect.Count);
            }
            return proof.Binding;
        }
        static int SingleTileForSpan(int[] items,int binding)
        {
            if(!singleArrayFastPixel || !singleArrayLean || binding<DrawCollapsePolicy.ArrayBindings)return -1;
            if(!singleArrayShader.keywordSpace.FindKeyword("SF_TILE_LINEAR").isValid)return -1;
            var source=gpuLiveSlices[0];int flags=-1;
            var texture=gpuCollapseDirect[binding-DrawCollapsePolicy.ArrayBindings];
            foreach(int item in items)
            {
                int row=CR(item);
                // Image actors retain the generic single-resource path, even
                // if today's image appears constant. No future flag guesses.
                if(GpuImageActor(gpuLiveOwnedIndices[source.MasterItems[row]]))return -1;
                int f=(int)CollapseInfo0(new Vector4(binding,0,1,1),gpuCollapseOwnedRows[row].M,source.Data.Items[row],texture).x&~127;
                if((f&~128)!=0 || flags>=0 && flags!=f)return -1;
                flags=f;
            }
            if(flags<0)return -1;
            foreach(int item in items)singleTileFlags[CR(item)]=flags;
            return flags==128?1:0;
        }
        static void RequireSingleTileFlags(int item,Vector4 info)
        {
            if(singleTileFlags==null || (uint)item>=(uint)singleTileFlags.Length || singleTileFlags[item]<0)return;
            RequireLive(((int)info.x&~127)==singleTileFlags[item],"fixed direct sampling flags changed; original GPU failure recovery required");
        }
        static void BindSingleArray(Material material,int binding,int tile=-1)
        {
            if(!singleArrayShader || material.shader!=singleArrayShader)
            {RequireLive(binding<0,"singlearray selected on an unmodified shader");return;}
            material.DisableKeyword("SF_FAST_PIXEL");
            if(singleArrayFastPixel)material.EnableKeyword("SF_FAST_PIXEL");
            material.DisableKeyword("SF_TILE_LINEAR");material.DisableKeyword("SF_TILE_POINT");
            if(singleArrayOptimize && singleArrayFastPixel && tile>=0)material.EnableKeyword(tile==1?"SF_TILE_POINT":"SF_TILE_LINEAR");
            material.DisableKeyword("SF_SINGLE_ARRAY");material.DisableKeyword("SF_SINGLE_2D");material.DisableKeyword("SF_LEAN_VERTEX");if(binding<0)return;
            if(singleArrayOptimize&&singleArrayLean)material.EnableKeyword("SF_LEAN_VERTEX");
            if(binding<DrawCollapsePolicy.ArrayBindings)
            {
                RequireLive((uint)binding<(uint)gpuCollapseArrays.Count,"singlearray material/proof mismatch");
                if(singleArrayOptimize)material.EnableKeyword("SF_SINGLE_ARRAY");
                material.SetTexture("_SfSingleArray",gpuCollapseArrays[binding]);
            }
            else
            {
                int direct=binding-DrawCollapsePolicy.ArrayBindings;
                RequireLive((uint)direct<(uint)gpuCollapseDirect.Count,"single texture material/proof mismatch");
                if(singleArrayOptimize)material.EnableKeyword("SF_SINGLE_2D");
                material.SetTexture("_SfSingle2D",gpuCollapseDirect[direct]);
            }
        }
        static void RequireSingleArrayAddress(int item,Vector4 address)
        {
            if(singleArrayProof==null || (uint)item>=(uint)singleArrayProof.Length || singleArrayProof[item]<0)return;
            RequireLive(address.x==singleArrayProof[item],"singlearray future address violated; original GPU failure recovery required");
        }
    }
}
#endif
