#if DEV || AUTOTEST || GPUDECO
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace StutterFix
{
    internal static partial class DecoBake
    {
#if AUTOTEST || GPUDECO
        static void UpdateDrawCollapseTextureSlot(int item,Texture2D texture,Material material)
        {
            if(gpuCollapseTexture==null)return;
            RequireLive(ReferenceEquals(gpuImageCollapseAddressOwner,gpuCollapseTexture)&&gpuImageCollapseAddresses!=null,
                "image collapse address hooks not connected in PrepareDrawCollapse");
            if(texture&&!gpuImageCollapseAddresses.ContainsKey(texture.GetInstanceID()))
                GpuLog("GPUIMG_DIAG mode=collapse_runtime_missing item="+item+" texture_id="+texture.GetInstanceID()
                    +" registered="+(gpuImageCollapseRegisteredTextures!=null&&gpuImageCollapseRegisteredTextures.ContainsKey(texture.GetInstanceID()))
                    +" width="+texture.width+" height="+texture.height+" format="+texture.graphicsFormat+" tier="+CollapseSize(texture)
                    +" stage=slot_update performance=False");
            RequireLive(texture&&material&&(uint)item<(uint)gpuCollapseIncluded.Length
                &&gpuImageCollapseAddresses.TryGetValue(texture.GetInstanceID(),out _),"image_texture_not_registered_in_collapse");
            RequireGpuImageCollapseSampling(item,texture);
            var address=gpuImageCollapseAddresses[texture.GetInstanceID()];
            address=ResolveImageArrayAddress(item,texture,address);
#if AUTOTEST
            RequireSingleArrayAddress(item,address);
#endif
            gpuImageCollapseTextureUpload[0]=CollapseInfo0(address,material,gpuLiveSlices.Count>0 && (uint)item<(uint)gpuLiveSlices[0].Data.Items.Length?gpuLiveSlices[0].Data.Items[item]:null,texture);
#if AUTOTEST
            RequireSingleTileFlags(item,gpuImageCollapseTextureUpload[0]);
#endif
            gpuImageCollapseTextureUpload[1]=new Vector4(address.z,address.w,address.z/texture.width*.5f,address.w/texture.height*.5f);
            gpuCollapseTexture.SetData(gpuImageCollapseTextureUpload,0,item*2,2);
            UpdateShapeForTexture(item,texture);   // 형태 칸: 새 텍스처의 칸으로
            // The independent MPB is still maintained by the caller, so turning
            // collapse off restores the latest image without another setter.
        }
#endif
        internal static bool gpuPrePlan; // 재생 준비(로딩) 때 구간 계획을 미리 만드는 중: 소유가 아직 켜지기 전이라 활성 상태 검사를 완화한다
        static void RequireDrawCollapse(DrawCollapseGuard guard=null)
        {
            var checks=guard??new DrawCollapseGuard(false);
            checks.Check((gpuLiveActive || gpuPrePlan) && gpuLivePrepared && (gpuLiveCurrentSlice>=0 || gpuPrePlan) && gpuScope.WholeSong
                && gpuLiveSlices.Count==1 && gpuLiveSlices[0].Independent && !gpuRenderMergeOn,
                "drawcollapse requires independent whole-song GPU ownership");
            checks.Check((GpuDecoPlayer.Product ? (GpuLiveOwned>0 || gpuPrePlan && gpuLiveOwnedIndices.Length>0) : GpuLiveOwned==23547 || GpuLiveOwned==28523) && (gpuLiveCpuFallbackCount==0 || GpuDecoPlayer.Product && gpuLiveCpuFallbackCount==gpuCollapsePlannedFallback),
                "drawcollapse requires GPU owned=23547 or 28523, CPU fallback=0");
#if DEV || AUTOTEST
            checks.Check(!GpuDecoCostProbe.Enabled, // SingleSubmission/GpuListCaching 은 draw 통합과 독립이므로 함께 켜도 된다(스크립트 비용 시험)
                
                "drawcollapse must isolate the original independent baseline");
#endif
            checks.Check(GpuDecoCopy.ReadTotals().Errors==0,"drawcollapse native copy errors");
        }
#if DEV || AUTOTEST
        static bool GpuDrawCollapseCommand(string command)
        {
#if AUTOTEST
            if(command.StartsWith("drawcollapse ",StringComparison.Ordinal) && DrawCollapseGateCommand(command.Substring(13)))return true;
#endif
            if(command=="drawcollapse prepare")
            {
                RequireLive(!gpuLiveActive && !gpuLivePrepared,"drawcollapse prepare must precede live prepare");
                RequireLive(!gpuRenderMergePrepare,"drawcollapse and rendermerge preparations are exclusive");
                gpuCollapsePrepare=true;GpuLog("drawcollapse_prepare=True default_off=True");return true;
            }
            if(command=="drawcollapse off")
            {
                HideDrawCollapse();foreach(var s in gpuLiveSlices)s.RefreshAlpha();
                if(gpuLiveActive)DispatchGpuLiveSlice(BakeTime);
                GpuLog("drawcollapse_on=False independent_restored=True");return true;
            }
            if(command=="drawcollapse on" || command=="drawcollapse ready")
            {
                if(command=="drawcollapse ready")RebuildDrawCollapse();
                ValidateDrawCollapse();
                if(command.EndsWith(" on",StringComparison.Ordinal))
                {
                    gpuCollapseOn=true;
                    try
                    {
                        foreach(var s in gpuLiveSlices)s.RefreshAlpha();
                        DispatchGpuLiveSlice(BakeTime);
                    }
                    catch {HideDrawCollapse();foreach(var s in gpuLiveSlices)s.RefreshAlpha();DispatchGpuLiveSlice(BakeTime);throw;}
                }
                GpuLog("drawcollapse_on="+gpuCollapseOn+" spans="+gpuCollapseSpans.Count+" submeshes_per_renderer=1");return true;
            }
            if(command!="drawcollapse validate" && command!="drawcollapse diag")return false;
            RequireDrawCollapse();
            if(command.EndsWith(" validate",StringComparison.Ordinal))
            {
                GpuLog("DRAWCOLLAPSE_CONTEXT owned="+GpuLiveOwned+" GPU_owned="+(GpuLiveOwned-gpuLiveCpuFallbackCount)
                    +" fallback="+gpuLiveCpuFallbackCount+" display="+Screen.width+"x"+Screen.height
                    +"@"+Screen.currentResolution.refreshRateRatio.value.ToString("F3",Inv));return true;
            }
            RequireLive(GpuPlaybackState.Paused && g2Window==-1,"drawcollapse readback is paused diagnostic only");
            double song=scrConductor.instance.songposition_minusi;
            RequireLive(song>=259.75 && song<=270.25,"drawcollapse diagnostic requires song 260..270");
            var slice=gpuLiveSlices[0];var cam=gpuLiveCamera;
            RequireLive(cam && cam.orthographic,"drawcollapse requires the prepared orthographic camera");
            var planes=GeometryUtility.CalculateFrustumPlanes(cam);
            // Renderer bounds deliberately span 2,000,000 units. Read the actual
            // evaluated GPU quad instead; never trust original frozen Transforms.
            var vertices=new GpuDecoGpu.Vertex[slice.Data.Items.Length*4];
            slice.Gpu.VertexBuffer.GetData(vertices);
            var rows=new List<DrawCollapsePolicy.Row>();var proxies=new HashSet<int>();
            var textures=new Dictionary<int,Texture2D>();int unknown=0,sortingGroups=0,masks=0;
            for(int i=0;i<slice.MasterItems.Length;i++)
            {
                var proxy=slice.Renderers[i];if(proxy)proxies.Add(proxy.GetInstanceID());
                int master=slice.MasterItems[i];if(GpuCpuFallback(master))continue;
                var original=gpuLiveOwnedRenderers[master];
                RequireLive(original && original.sprite && proxy,"drawcollapse missing owned renderer/texture");
                var texture=original.sprite.texture;
                RequireLive(texture && texture.GetInstanceID()==slice.Data.Items[i].TextureId,
                    "drawcollapse texture changed since preparation");
                textures[texture.GetInstanceID()]=texture; // All owned textures, including currently invisible ones.
                if(!proxy.enabled || proxy.forceRenderingOff || !proxy.gameObject.activeInHierarchy)continue;
                var bounds=new Bounds(vertices[i*4].position,Vector3.zero);float alpha=0;bool area=false;
                for(int v=0;v<4;v++)
                {
                    var p=vertices[i*4+v];
                    RequireLive(DecoBakeCore.Finite(p.position.x) && DecoBakeCore.Finite(p.position.y)
                        && DecoBakeCore.Finite(p.position.z) && DecoBakeCore.Finite(p.color.a),"nonfinite drawcollapse vertex");
                    bounds.Encapsulate(p.position);alpha=Math.Max(alpha,p.color.a);
                }
                var triangles=slice.Data.Items[i].Triangles;
                for(int t=0;t<triangles.Length;t+=3)
                    if(Vector3.Cross(vertices[i*4+triangles[t+1]].position-vertices[i*4+triangles[t]].position,
                        vertices[i*4+triangles[t+2]].position-vertices[i*4+triangles[t]].position).sqrMagnitude>0)area=true;
                if(alpha<=0 || !area || !GeometryUtility.TestPlanesAABB(planes,bounds)
                    || (cam.cullingMask & (1<<proxy.gameObject.layer))==0)continue;
                var m=proxy.sharedMaterial;
                rows.Add(new DrawCollapsePolicy.Row(GpuDecoScene.SortKey(proxy,m,cam),true,proxy.GetInstanceID(),
                    texture.GetInstanceID(),MaterialSignature(m),proxy.gameObject.layer));
            }
            foreach(var r in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if(!r || proxies.Contains(r.GetInstanceID()) || !r.gameObject.scene.IsValid() || !r.enabled
                    || !r.gameObject.activeInHierarchy || r.forceRenderingOff
                    || (cam.cullingMask & (1<<r.gameObject.layer))==0 || !GeometryUtility.TestPlanesAABB(planes,r.bounds))continue;
                // Unknown shaders/MPBs/particles/masks cannot be discarded on a
                // guessed alpha property. Keep them and label this an upper bound.
                bool known=false;
                if(r is SpriteRenderer sprite)
                {
                    if(!sprite.sprite)continue;
                    var m=sprite.sharedMaterial;
                    known=m && m.shader && m.shader.name=="Sprites/Tile" && !sprite.HasPropertyBlock();
                    if(known && sprite.color.a<=0)continue;
                }
                if(!known)unknown++;
                if(r is SpriteMask)masks++;
                if(r.GetComponentInParent<SortingGroup>())sortingGroups++;
                var materials=r.sharedMaterials;
                if(materials.Length==0)
                {
                    rows.Add(new DrawCollapsePolicy.Row(GpuDecoScene.SortKey(r,null,cam),false,r.GetInstanceID()));continue;
                }
                for(int slot=0;slot<materials.Length;slot++)
                {
                    var m=materials[slot];if(m && m.renderQueue<=2500)continue;
                    rows.Add(new DrawCollapsePolicy.Row(GpuDecoScene.SortKey(r,m,cam,slot),false,r.GetInstanceID()));
                }
            }
            var counts=DrawCollapsePolicy.Count(rows);
            var memory=DrawCollapseMemory(textures.Values);
            // Bounds/alpha cannot prove texture coverage, occlusion or a custom
            // shader's pixel contribution. Do not turn conservative counts into
            // a spurious FAIL (>400) or authorize gate 2 from this diagnostic.
            GpuLog("DRAWCOLLAPSE_DIAG owned_visible="+counts.OwnedVisible+" barrier_visible="+counts.BarrierVisible
                +" active_barrier_spans="+counts.Spans+" same_tex_runs="+counts.SameTextureRuns
                +" textures_in_owned="+textures.Count+" array_mem_mb_dxt="+memory
                +" song="+song.ToString("F6",Inv)+" frame="+Time.frameCount+" owned="+GpuLiveOwned
                +" GPU_owned="+(GpuLiveOwned-gpuLiveCpuFallbackCount)+" unknown_barriers="+unknown
                +" sorting_groups="+sortingGroups+" ambiguous_ties="+counts.AmbiguousTies
                +" sprite_masks="+masks
                +" span_threshold="+DrawCollapsePolicy.SpanDecision(counts.Spans)
                +" visibility=conservative_bounds_alpha_candidates pixels_proven=False ordering_proven=False gate=HOLD timing_contaminated=True");
            return true;
        }
        static string DrawCollapseMemory(IEnumerable<Texture2D> textures)
        {
            long compressed=0,total=0;int unsupported=0,tiers=0,arrays=0;
            foreach(var tier in textures.GroupBy(t=>t.width+"x"+t.height+"/"+t.graphicsFormat+"/mips="+t.mipmapCount
                +"/filter="+t.filterMode+"/wrap="+t.wrapModeU+","+t.wrapModeV+"/aniso="+t.anisoLevel))
            {
                tiers++;
                var texture=tier.First();var format=texture.graphicsFormat;
                // Use the resident graphics format, not the logical TextureFormat
                // (e.g. RGB24 may be stored as four bytes per texel on the GPU).
                int bytes=format==GraphicsFormat.None?0:(int)GraphicsFormatUtility.GetBlockSize(format);
                int width=format==GraphicsFormat.None?0:(int)GraphicsFormatUtility.GetBlockWidth(format);
                int height=format==GraphicsFormat.None?0:(int)GraphicsFormatUtility.GetBlockHeight(format);
                bool bc=GraphicsFormatUtility.IsBCFormat(format);
                if(bytes<=0 || width<=0 || height<=0 || GraphicsFormatUtility.IsCompressedFormat(format) && !bc)
                {unsupported+=tier.Count();GpuLog("DRAWCOLLAPSE_ARRAY tier="+tier.Key+" slices="+tier.Count()+" bytes=NA unsupported=True");continue;}
                long size=checked(DrawCollapsePolicy.TextureBytes(texture.width,texture.height,texture.mipmapCount,width,height,bytes)*tier.Count());
                total=checked(total+size);if(bc)compressed=checked(compressed+size);
                arrays+=(tier.Count()+Math.Max(1,SystemInfo.maxTextureArraySlices)-1)/Math.Max(1,SystemInfo.maxTextureArraySlices);
                GpuLog("DRAWCOLLAPSE_ARRAY tier="+tier.Key+" slices="+tier.Count()+" bytes="+size
                    +" format="+texture.format+" max_slices="+SystemInfo.maxTextureArraySlices+" allocated=False");
            }
            bool copy=SystemInfo.supports2DArrayTextures && (SystemInfo.copyTextureSupport & CopyTextureSupport.DifferentTypes)!=0;
            string feasibility=!copy?"IMPOSSIBLE_COPY_SUPPORT":SystemInfo.graphicsMemorySize>0
                && total>(long)SystemInfo.graphicsMemorySize*1048576?"IMPOSSIBLE_VRAM_LOWER_BOUND":"UNVERIFIED";
            GpuLog("DRAWCOLLAPSE_ARRAY_TOTAL bytes_known="+total+" total_mb="+(unsupported==0?(total/1048576.0).ToString("F6",Inv):"NA")
                +" tiers="+tiers+" planned_arrays_known="+arrays+" unsupported_textures="+unsupported+" copy_support="+copy+" vram_mb="+SystemInfo.graphicsMemorySize
                +" driver_allocated_mb="+(UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver()/1048576.0).ToString("F3",Inv)
                +" feasibility="+feasibility+" arrays_allocated=0 copy_calls=0");
            return unsupported==0?(compressed/1048576.0).ToString("F6",Inv):"NA";
        }
#endif
    }
}
#endif
