#if DEV || AUTOTEST || GPUDECO
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace StutterFix
{
    internal static partial class DecoBake
    {
        static bool gpuCollapsePrepare,gpuCollapseOn;
        static string gpuCollapseFault,gpuCollapsePendingFallback;
        static Shader gpuCollapseShader,gpuCollapseBlendShader;
        const string CollapseNoDirectKeyword="NO_DIRECT_TEXTURES";
        // True only for a prepared array address that cannot change to a direct slot.
        static bool[] gpuCollapseArrayOnly;
        static int gpuCollapseOwnedCursor;
        static readonly List<Material> gpuCollapseMaterials=new List<Material>();
        static readonly List<CollapseSpan> gpuCollapseSpans=new List<CollapseSpan>();
        static readonly List<Texture2DArray> gpuCollapseArrays=new List<Texture2DArray>();
        static readonly List<Texture2D> gpuCollapseDirect=new List<Texture2D>(); // Borrowed, never destroyed.
        static readonly List<GpuLiveRow> gpuCollapseBarriers=new List<GpuLiveRow>();
        // 동점 때문에 독립 렌더러로 뺀 장식의 장벽 줄 -> 그 장식 항목. 기준 상태 복원으로 장식이 다시 통합에 들어오면 그 줄을 지운다.
        static readonly Dictionary<GpuLiveRow,int> gpuTieBarrierItem=new Dictionary<GpuLiveRow,int>();
        static readonly List<GpuLiveRow> gpuCollapseAuxiliaries=new List<GpuLiveRow>();
        static ComputeBuffer gpuCollapseTexture;
        static bool[] gpuCollapseIncluded;
        static bool[] gpuCollapseTextureFallback;
        static List<int>[] gpuCollapseDirectSet; // 항목마다 쓸 수 있는 직접 텍스처 번호(그림 바뀜 포함). DIRECT_FEW 구간 판단용
        const string CollapseFewKeyword="DIRECT_FEW";
        static int gpuCollapseTextureFallbackCount;
        static GpuLiveRow[] gpuCollapseOwnedRows;
        static HashSet<int> gpuCollapseSeen;
        static int gpuCollapseBarrierCursor,gpuCollapseAuxCursor;
        static readonly Dictionary<string,Material> gpuCollapseSharedMats=new Dictionary<string,Material>();
        static double gpuCollapseReplanTime=-100;
        static readonly List<GpuLiveRow> gpuCollapseBarrierSweep=new List<GpuLiveRow>(1100);
        // 재생 중에는 장벽 6만여 개를 매 프레임 전부 훑지 않고 프레임당 이 개수씩 순환 점검한다(전체 점검은 진입 때만).
        const int CollapseBarrierSweepPerFrame=8; // 준비 때 본 렌더러 전체(재계획에서 새로 생긴 것만 장벽으로 더하기 위함)
        static Vector3 gpuCollapseCameraPosition,gpuCollapseCameraForward;
        static ComputeBuffer gpuCollapseAllVerts,gpuCollapseAllMap;static int gpuCollapseAllCount;
        sealed class CollapseSpan : IDisposable
        {
            internal int[] Items;
            internal int[] Recs;   // 자리마다 형태 칸 기록 번호(1부터, 0 = 사각형 전체). null = 모두 0
            internal int[] AllIndices;
            internal int[] ActiveIndices;
            internal int LastUploadedIndexCount=-1;
            internal bool IndexDirty=true;
            internal Mesh Mesh;
            internal MeshRenderer Renderer;
            internal Material Material;
            internal bool SharedMat,Nudged;
            internal GraphicsBuffer Vertices;
            internal ComputeBuffer Map;
            internal int Base;
            internal SceneRenderOrder.Key First,Last;
            internal Vector3 CenterNow;   // 정렬 중심(정적 보정 포함)
            internal int DynState;        // 0 그대로, 1 거리 보정으로 뒤에 그리기, 2 독립 렌더러
            internal bool NoDirect; internal List<int> Few;   // 재질 텍스처 묶음 정보(복사한 재질에 다시 묶을 때)
#if AUTOTEST
            internal int SingleArray=-1;
            internal int SingleTile=-1;
#endif
            public void Dispose()
            {
                if(Renderer){Renderer.enabled=false;UnityEngine.Object.Destroy(Renderer.gameObject);}
                Vertices?.Dispose();Map?.Dispose();
                if(Mesh)UnityEngine.Object.Destroy(Mesh);
                if(Material && !SharedMat)UnityEngine.Object.Destroy(Material);
            }
        }
        static void InvalidateRecordedCollapse(){
#if AUTOTEST
            collapseDiagReady=false;
#endif
            if(gpuLiveSlices.Count>0 && gpuLiveSlices[0].Gpu!=null)gpuLiveSlices[0].Gpu.ReleaseRecordedCollapse();}
        static void HideDrawCollapse()
        {
#if AUTOTEST
            EndMapUploadContext();
#endif
            LodRelease();
            gpuCollapseOn=false;gpuCollapsePendingFallback=null;
#if AUTOTEST
            collapseDiagReady=collapseDiagFreeze=collapseDiagCount=false;
            collapseDiagDispatches=collapseDiagSkips=0;
            collapseDiagEnabled=null;
            RestoreCollapseDiagSuppression();
            if(collapseDiagPreviousHide.HasValue)DrawCollapsePolicy.HideProxies=collapseDiagPreviousHide.Value;
            collapseDiagPreviousHide=null;
#endif
            foreach(var s in gpuCollapseSpans)if(s.Renderer)s.Renderer.enabled=false;
        }
        static void DisposeDrawCollapse()
        {
            InvalidateRecordedCollapse();
            HideDrawCollapse();
            ClearImageArrayBanks();
            gpuSpanOfItem=null;gpuSpanVisible=null;gpuItemHiddenState=null;gpuSpanDirty.Clear();gpuSpanDirtyMark=null;
            gpuBarrierRowById=null;gpuBarrierWatch.Clear();gpuBarrierChecked.Clear();gpuWatchRows.Clear();gpuWatchSig=null;gpuSpansByBucket=null;gpuWatchActive=null;gpuWatchBucket=null;gpuWatchMat=null;gpuBucketMembers.Clear();gpuBucketDirty.Clear();gpuDynExcludedItems.Clear();gpuDynZero=null;gpuSpanExcluded=null;gpuCollapseMapCpu=null;gpuCollapseSlotOfItem=null;gpuCollapseIncludedSaved=null;gpuCollapseFallbackSaved=null;
            foreach(var s in gpuCollapseSpans)s.Dispose();gpuCollapseSpans.Clear();
            foreach(var sm in gpuCollapseSharedMats.Values)if(sm)UnityEngine.Object.Destroy(sm);gpuCollapseSharedMats.Clear();
            gpuCollapseAllVerts?.Dispose();gpuCollapseAllVerts=null;gpuCollapseAllMap?.Dispose();gpuCollapseAllMap=null;gpuCollapseAllCount=0;
            gpuCollapseTexture?.Dispose();gpuCollapseTexture=null;gpuCollapseShader=null;gpuCollapseOwnedCursor=0;
            foreach(var a in gpuCollapseArrays)if(a)UnityEngine.Object.Destroy(a);
            gpuCollapseArrays.Clear();gpuCollapseBarriers.Clear();gpuTieBarrierItem.Clear();gpuCollapseAuxiliaries.Clear();
            gpuCollapseDirect.Clear();
            gpuCollapseArrayOnly=null;
            gpuInstRows.Clear();gpuInstItem.Clear();gpuItemInstIds=null;gpuItemSlots=null;gpuSlotDepth=null;gpuSpanHasInst=null;gpuItemDepthNow=null;gpuInstBase=int.MaxValue;
            gpuSlotBits=null;gpuItemShapeStart=gpuItemShapeCount=null;gpuShapeTable=Array.Empty<Vector4>();gpuShapeItems=0;
            gpuCollapseIncluded=null;gpuCollapseSeen=null;gpuCollapseKeysValid=false;gpuCollapseScanned=false;gpuCollapseDirtyIds.Clear();gpuCollapseNewDecos.Clear();gpuCollapseOwnedRows=null;gpuCollapseTextureFallback=null;gpuCollapseTextureFallbackCount=0;
        }
        static bool CollapseSupportedFormat(Texture2D t)
        {
            if(!t || t.mipmapCount!=1 || t.width<=0 || t.height<=0)return false;
            var f=t.graphicsFormat;
            if(f==GraphicsFormat.RGBA_DXT5_UNorm || f==GraphicsFormat.RGBA_DXT5_SRGB || f==GraphicsFormat.RGBA_DXT1_UNorm || f==GraphicsFormat.RGBA_DXT1_SRGB)
                return t.width%4==0 && t.height%4==0;
            return f==GraphicsFormat.R8G8B8A8_UNorm || f==GraphicsFormat.R8G8B8A8_SRGB || f==GraphicsFormat.B8G8R8A8_UNorm || f==GraphicsFormat.B8G8R8A8_SRGB;
        }
        static int CollapseSize(Texture2D t)
        {
            if(!CollapseSupportedFormat(t))return 0;
            int m=Math.Max(t.width,t.height);
            if(m<=512)return 512;
            if(m<=1024)return 1024;
            return 0;
        }
        // 화면 복사(Grab) 혼합 장식은 정렬 묶음(층,순서,큐,거리)에 다른 렌더러가 없을 때만 맡는다. 섞여 있으면 화면 복사 셰이더와
        // 일반 셰이더 사이의 엔진 순서를 구간 대표 렌더러로 그대로 재현할 수 없어(7777 124초) 원래 렌더러로 남긴다.
        static void DemoteSharedGrabRows(List<GpuLiveRow> rows,Dictionary<int,GpuDecoData.Item> items)
        {
            if(rows==null || !DrawCollapsePolicy.BlendModes)return;
            var count=new Dictionary<(int,int,int,float),int>();
            var shaders=new Dictionary<(int,int,int,float),HashSet<int>>();
            foreach(var row in rows)
            {
                if(!row.R || !row.M || row.AuxiliaryOwner>=0)continue;var k=row.Key;var bk=(k.Layer,k.Order,k.Queue,k.Distance);
                count.TryGetValue(bk,out int c0);count[bk]=c0+1;
                if(!shaders.TryGetValue(bk,out var hs)){hs=new HashSet<int>();shaders[bk]=hs;}hs.Add(k.Shader);
            }
            int demoted=0;
            foreach(var row in rows)
            {
                if(row.Item<0 || !items.TryGetValue(row.Item,out var it) || it.BlendMode<=0)continue;
                var k=row.Key;var bk=(k.Layer,k.Order,k.Queue,k.Distance);
                // 화면 복사 장식: 묶음에 다른 렌더러가 하나라도 있으면. 더하기 등 화면 복사 없는 혼합 장식(원래 셰이더 Particles/Additive 등):
                // 묶음에 셰이더가 다른 렌더러가 있으면(다른 셰이더 사이 엔진 순서를 모른다, 7777 124초 skygwangwon/cir_light 순서 뒤바뀜)
                bool bad=GpuDecoBlend.NeedsGrab(it.BlendMode) ? (count.TryGetValue(bk,out int c) && c>1) : (shaders.TryGetValue(bk,out var hs2) && hs2.Count>1);
                if(bad){row.Item=-1;demoted++;}
            }
            if(demoted>0 && GpuDecoPlayer.Product)Main.Entry.Logger.Log("[GPU 장식] 화면 복사 혼합 장식 중 다른 렌더러와 같은 정렬 묶음이라 원래 렌더러로 남긴 것: "+demoted+"개");
        }
#if AUTOTEST
        static readonly Dictionary<(int,int,int,float),string> tieDiagKind=new Dictionary<(int,int,int,float),string>();
#endif
        static void DemoteTieExcludedRows(List<GpuLiveRow> rows)
        {
            if(rows==null || rows.Count==0)return;
            // 키 = (층,순서,큐,거리,셰이더). 같은 셰이더 안에서만 재질 ID 순서가 측정으로 확인됐다. 다른 셰이더 사이 순서는 아직 모른다
            // (다른 셰이더까지 순위로 비교하면 7777 124초에서 화면이 밝게 틀렸다: 셰이더 ID 만 106.9, 화면 복사 뒤 규칙 더해도 107 대 원래 74).
            var activeBucketMinMat=new Dictionary<(int,int,int,float,int),long>();
            var activeBucketMaxMat=new Dictionary<(int,int,int,float,int),long>();
            foreach(var row in rows)
            {
                if(row.Item>=0 || row.AuxiliaryOwner>=0 || !row.R || !row.M)continue;
                if(!DrawCollapseBarrierActive(row) || row.R is SpriteMask)continue;
                var bk=row.Key;
                var bkey=(bk.Layer,bk.Order,bk.Queue,bk.Distance);var skey=(bk.Layer,bk.Order,bk.Queue,bk.Distance,bk.Shader);
                if(!activeBucketMinMat.TryGetValue(skey,out long curMin) || bk.Rank<curMin)activeBucketMinMat[skey]=bk.Rank;
                if(!activeBucketMaxMat.TryGetValue(skey,out long curMax) || bk.Rank>curMax)activeBucketMaxMat[skey]=bk.Rank;
#if AUTOTEST
                if(!tieDiagKind.ContainsKey(bkey))tieDiagKind[bkey]=row.R.GetType().Name+":"+(row.M?row.M.shader.name:"-")+":L"+bk.Layer+"/O"+bk.Order+(row.R.GetComponentInParent<scrDecoration>() is scrDecoration dd0?":deco:"+dd0.name+":blend="+(dd0 is scrVisualDecoration vv0?GpuDecoBlend.Id(vv0)+":take="+GpuDecoBlend.Takeable(vv0,vv0.spriteRenderer)+":mask="+vv0.spriteRenderer.maskInteraction+":fx="+(vv0.blendModeEffect?vv0.blendModeEffect.MaskMode.ToString():"-")+":mesh="+(vv0.meshRenderer&&vv0.meshRenderer.enabled):"-"):"");
#endif
            }
            if(activeBucketMinMat.Count==0)return;
            var demoted=new HashSet<int>();
#if AUTOTEST
            var tieCount=new Dictionary<string,int>();
#endif
            foreach(var row in rows)
            {
                if(row.Item<0 || !row.R || !row.M)continue;
                var ok=row.Key;
                var bkey=(ok.Layer,ok.Order,ok.Queue,ok.Distance);var skey=(ok.Layer,ok.Order,ok.Queue,ok.Distance,ok.Shader);
                if(!activeBucketMinMat.TryGetValue(skey,out long minBarrierMat))continue;
                if(DrawCollapsePolicy.KeepBeforeBarrier)
                {
                    if(ok.Rank<minBarrierMat)continue;
                    if(DrawCollapsePolicy.KeepAfterBarrier && activeBucketMaxMat.TryGetValue(skey,out long maxBarrierMat) && ok.Rank>maxBarrierMat)continue;
                }
                demoted.Add(row.Item);
#if AUTOTEST
                if(tieDiagKind.TryGetValue(bkey,out var tk)){tieCount.TryGetValue(tk,out int tc);tieCount[tk]=tc+1;}
#endif
                row.Item=-1;
            }
#if AUTOTEST
            if(GpuDecoPlayer.Product)Main.Entry.Logger.Log("[GPU 장식] 동점 정리 장벽 종류(내린 장식 수): "+string.Join(" | ",tieCount.OrderByDescending(x=>x.Value).Take(12).Select(x=>x.Key+"="+x.Value)));
            tieDiagKind.Clear();
#endif
            if(demoted.Count==0)return;
            foreach(var row in rows)if(demoted.Contains(row.AuxiliaryOwner))row.AuxiliaryOwner=-1;
            if(GpuDecoPlayer.Product)Main.Entry.Logger.Log("[GPU 장식] 동점 묶음 사전 정리(원본 컬링 유지): 내린 장식 "+demoted.Count+"개");
        }
        static string CollapseTier(Texture2D t)=>CollapseSize(t)+"/"+t.graphicsFormat;
        // The graphics format determines payload size. A texel is not always
        // one byte (DXT1 = half a byte; RGBA32 = four bytes). This is allocation
        // payload, not evidence that those bytes are physically resident.
        static long CollapseArrayBytes(int width,int height,int slices,GraphicsFormat format)
        {
            int bw=(int)GraphicsFormatUtility.GetBlockWidth(format);
            int bh=(int)GraphicsFormatUtility.GetBlockHeight(format);
            int bytes=(int)GraphicsFormatUtility.GetBlockSize(format);
            RequireLive(bw>0 && bh>0 && bytes>0 && slices>0,"drawcollapse unknown array payload");
            return checked(DrawCollapsePolicy.TextureBytes(width,height,1,bw,bh,bytes)*slices);
        }
        static void ValidateDrawCollapseItem(int item)
        {
            if(gpuCollapseIncluded!=null && !gpuCollapseIncluded[item] && !DrawCollapseTextureFallback(item))
                if(!MergeHidden(item))gpuCollapsePendingFallback="visible_item_outside_prepared_window item="+item;
        }
        static bool DrawCollapseTextureFallback(int item)=>gpuCollapseTextureFallback!=null && gpuCollapseTextureFallback[item];
        static void PrepareDrawCollapse(List<GpuLiveRow> masterRows)
        {
            if(!gpuCollapsePrepare)return;
            RequireLive(!gpuRenderMergePrepare && gpuScope.WholeSong && gpuLiveSlices.Count==1
                && gpuLiveSlices[0].Independent && (GpuPlaybackState.Paused || GpuDecoPlayer.Preparing),"drawcollapse needs independent paused song preparation");
            RequireLive(gpuLiveShader.HasKernel("EvaluateCollapse"),"drawcollapse bundle missing EvaluateCollapse");
            var sortMode=gpuLiveCamera.transparencySortMode;
            if(sortMode==TransparencySortMode.Default)sortMode=GraphicsSettings.transparencySortMode;
            RequireLive(gpuLiveCamera.orthographic && (sortMode==TransparencySortMode.Default || sortMode==TransparencySortMode.Orthographic),
                "drawcollapse requires orthographic camera sorting");
            RequireLive(UnityEngine.Object.FindObjectsByType<SortingGroup>(FindObjectsInactive.Include,FindObjectsSortMode.None)
                .All(g=>!g.enabled || !g.gameObject.activeInHierarchy),"drawcollapse unsupported active SortingGroup");
            RequireLive(DrawCollapsePolicy.PartialMaskAdmit || UnityEngine.Object.FindObjectsByType<SpriteMask>(FindObjectsInactive.Include,FindObjectsSortMode.None)
                .All(m=>!m.enabled || !m.gameObject.activeInHierarchy),"drawcollapse unsupported active SpriteMask");
            gpuCollapseShader=gpuLiveBundle.LoadAsset<Shader>("Assets/Shaders/GpuDecoBatch.shader");
            gpuCollapseBlendShader=gpuLiveBundle.LoadAsset<Shader>("Assets/Shaders/GpuDecoBatchBlend.shader");
            if(gpuCollapseBlendShader && !gpuCollapseBlendShader.isSupported)gpuCollapseBlendShader=null;
            RequireLive(gpuCollapseShader && gpuCollapseShader.isSupported,"drawcollapse bundle missing/unsupported GpuDecoBatch.shader");
            var source=gpuLiveSlices[0];
            gpuCollapseCameraPosition=gpuLiveCamera.transform.position;gpuCollapseCameraForward=gpuLiveCamera.transform.forward;
            var local=source.Data.Items.Select((x,i)=>new{ x.Index, Item=i }).ToDictionary(x=>x.Index,x=>x.Item);
            gpuCollapseIncluded=new bool[source.Data.Items.Length];
            gpuCollapseArrayOnly=new bool[source.Data.Items.Length];
            gpuCollapseTextureFallback=new bool[source.Data.Items.Length];gpuCollapseDirectSet=new List<int>[source.Data.Items.Length];
            gpuCollapseOwnedRows=new GpuLiveRow[source.Data.Items.Length];
            gpuCollapseSeen=new HashSet<int>();
            // 모드가 만든 독립 GPU 렌더러(소유 장식마다 1개)는 장벽 후보가 아니다.
            foreach(var rr in source.Renderers)if(rr)gpuCollapseSeen.Add(rr.GetInstanceID());
            foreach(var row in masterRows)
            {
                if(row.R)gpuCollapseSeen.Add(row.R.GetInstanceID());
                if(row.AuxiliaryOwner>=0)
                {
                    RequireLive(!row.R.enabled || !row.R.gameObject.activeInHierarchy || row.R.forceRenderingOff,
                        "drawcollapse admitted auxiliary is visible");
                    gpuCollapseAuxiliaries.Add(row);continue;
                }
                if(row.Item<0)
                {
                    // Keep potential CPU barriers even when currently invisible.
                    // They may wake during the ten-second measurement window.
                    gpuCollapseBarriers.Add(row);
                    continue;
                }
                int i=local[row.Item];gpuCollapseOwnedRows[i]=row;
                if(!GpuDecoPlayer.Product && !VisibleInWindow(table,row.Item,257.5,270.25))continue;
                gpuCollapseIncluded[i]=true;
            }
            var textures=new Dictionary<int,Texture2D>();
            var info=new Vector4[source.Data.Items.Length*2];
            for(int i=0;i<source.Data.Items.Length;i++)
            {
                var row=gpuCollapseOwnedRows[i];var sprite=row?.R as SpriteRenderer;
                RequireLive(sprite && sprite.sprite && row.M && (row.M.shader.name=="Sprites/Tile" || (DrawCollapsePolicy.BlendModes && GpuDecoBlend.ShaderFits(row.M.shader,source.Data.Items[i].BlendMode)))
                    && sprite.maskInteraction==SpriteMaskInteraction.None,"drawcollapse unsupported owned material/mask");
                RequireLive(row.M.shaderKeywords.All(k=>k=="USE_POINT_SAMPLING" || (source.Data.Items[i].BlendMode>0 && (k.StartsWith("BLENDMODES_MODE_") || k=="FORCE_POINT_SAMPLING"))),"drawcollapse unsupported shader keywords: "+string.Join(",",row.M.shaderKeywords));
                RequireLive((!row.M.HasProperty("_Color") || row.M.GetColor("_Color")==Color.white)
                    && (!row.M.HasProperty("_Opacity") || row.M.GetFloat("_Opacity")==1),
                    "drawcollapse unsupported material tint/opacity");
                var t=sprite.sprite.texture;
                RequireLive(t && t.GetInstanceID()==source.Data.Items[i].TextureId && t.mipmapCount==1
                    && ((t.wrapModeU==TextureWrapMode.Clamp && t.wrapModeV==TextureWrapMode.Clamp) || GpuImageCollapseUvInUnitRange(source.Data.Items[i].UV)),
                    "drawcollapse texture changed or unsupported mip/wrap");
                textures[t.GetInstanceID()]=t;
            }
            RequireLive(SystemInfo.supports2DArrayTextures && (SystemInfo.copyTextureSupport&CopyTextureSupport.DifferentTypes)!=0,
                "drawcollapse Texture2D-to-array copy unsupported");
#if AUTOTEST || GPUDECO
            RegisterGpuImageCollapseVariants(textures);
#endif
            if (GpuDecoPlayer.Product)
            {
                var all=textures.Values.ToArray();
                Main.Entry.Logger.Log("[GPU 장식] 곡 전체 텍스처 "+all.Length+"개 (배열 후보 "+all.Count(t=>CollapseSize(t)>0)+", 직접 "+all.Count(t=>CollapseSize(t)==0)+", 배열 종류 "+all.Where(t=>CollapseSize(t)>0).Select(CollapseTier).Distinct().Count()+")");
            }
            var addresses=new Dictionary<int,Vector4>();long total=0;int copies=0;
            RequireLive(SystemInfo.maxTextureArraySlices>0,"drawcollapse texture array slices unavailable");
            var texUsage=new Dictionary<int,int>();
            for(int i=0;i<source.Data.Items.Length;i++)
            {
                int tid=source.Data.Items[i].TextureId;
                texUsage.TryGetValue(tid,out int uc);texUsage[tid]=uc+1;
            }
            var smallGroups=textures.Values.Where(t=>CollapseSize(t)>0).GroupBy(CollapseTier)
                .OrderByDescending(g=>g.Sum(t=>texUsage.TryGetValue(t.GetInstanceID(),out int u)?u:0)).ToArray();
            var exactGroups=new List<Texture2D[]>();
            if(DrawCollapsePolicy.ExactBigArray)
            {
                int maxExact=Math.Max(1,Math.Min(2,DrawCollapsePolicy.ArrayBindings-Math.Min(2,smallGroups.Length)));
                var bigCandidates=textures.Values.Where(t=>CollapseSize(t)==0 && CollapseSupportedFormat(t) && (long)t.width*t.height<=16777216L)
                    .GroupBy(t=>t.width+"x"+t.height+"/"+t.graphicsFormat)
                    .Where(g=>g.Count()>=DrawCollapsePolicy.ExactBigMin)
                    .OrderByDescending(g=>g.Count()).ThenBy(g=>g.Key,StringComparer.Ordinal).Take(maxExact);
                foreach(var bg in bigCandidates)exactGroups.Add(bg.Take(SystemInfo.maxTextureArraySlices).ToArray());
            }
            var exactIds=new HashSet<int>(exactGroups.SelectMany(g=>g.Select(t=>t.GetInstanceID())));
            var admittedSmall=new List<Texture2D>();
            int availArraySlots=Math.Max(0,DrawCollapsePolicy.ArrayBindings-exactGroups.Count);
            int remSlots=availArraySlots;
            for(int group=0;group<smallGroups.Length && remSlots>0;group++)
            {
                var ordered=smallGroups[group].OrderByDescending(t=>texUsage.TryGetValue(t.GetInstanceID(),out int u)?u:0).ToArray();
                int take=(int)Math.Min(ordered.Length,(long)remSlots*SystemInfo.maxTextureArraySlices);
                admittedSmall.AddRange(ordered.Take(take));
                remSlots-=(take+SystemInfo.maxTextureArraySlices-1)/SystemInfo.maxTextureArraySlices;
            }
            var small=admittedSmall.ToArray();
            var smallIds=new HashSet<int>(small.Select(t=>t.GetInstanceID()));
            var direct=textures.Values.Where(t=>!smallIds.Contains(t.GetInstanceID()) && !exactIds.Contains(t.GetInstanceID()))
                .OrderByDescending(t=>texUsage.TryGetValue(t.GetInstanceID(),out int u)?u:0)
                .Take(DrawCollapsePolicy.DirectBindings).ToArray();
            int arrays=0;
            var exactPlans=new Dictionary<Texture2D[],ImageArrayLeasePlan>();
            foreach(var g in exactGroups)exactPlans[g]=PlanImageArray(g,source);
            foreach(var tier in small.GroupBy(CollapseTier))
            {
                var group=tier.ToArray();
                RequireLive(SystemInfo.maxTextureArraySlices>0,"drawcollapse texture array slices unavailable");
                for(int start=0;start<group.Length;start+=SystemInfo.maxTextureArraySlices)
                {
                    int depth=Math.Min(SystemInfo.maxTextureArraySlices,group.Length-start),maxW=0,maxH=0;
                    for(int slice=0;slice<depth;slice++)
                    {var tex=group[start+slice];maxW=Math.Max(maxW,tex.width);maxH=Math.Max(maxH,tex.height);}
                    long payload=CollapseArrayBytes(maxW,maxH,depth,group[start].graphicsFormat);
                    total=checked(total+payload);arrays++;
                    GpuLog("DRAWCOLLAPSE_ARRAY_PLAN kind=tier width="+maxW+" height="+maxH+" slices="+depth+" format="+group[start].graphicsFormat+" bytes="+payload);
                }
            }
            foreach(var exactGroup in exactGroups)
            {
                int depth=exactPlans[exactGroup]?.Slices??exactGroup.Length;
                arrays+=1;long payload=CollapseArrayBytes(exactGroup[0].width,exactGroup[0].height,depth,exactGroup[0].graphicsFormat);
                total=checked(total+payload);
                GpuLog("DRAWCOLLAPSE_EXACT_ARRAY size="+exactGroup[0].width+"x"+exactGroup[0].height+" slices="+exactGroup.Length);
                GpuLog("DRAWCOLLAPSE_ARRAY_PLAN kind=exact width="+exactGroup[0].width+" height="+exactGroup[0].height+" slices="+depth+" format="+exactGroup[0].graphicsFormat+" bytes="+payload);
            }
            GpuLog("DRAWCOLLAPSE_ARRAY_TOTAL layout=small_dxt5_tiers_plus_borrowed_2d bytes_known="+total+" total_mb="+(total/1048576.0).ToString("F6",Inv)
                +" planned_arrays_known="+arrays+" direct_textures="+direct.Length+" active_texture_slots="+(arrays+direct.Length)
                +" shader_declared_slots="+DrawCollapsePolicy.TextureBindings+" samplers=2 arrays_allocated=0 copy_calls=0 free_vram=UNVERIFIED budget_scope=format_block_payload_not_residency");
            RequireLive(SystemInfo.graphicsMemorySize>0 && total<(long)SystemInfo.graphicsMemorySize*1048576,
                "drawcollapse array budget exceeds total VRAM"); // Free VRAM still needs external measurement.
            RequireLive(arrays<=DrawCollapsePolicy.ArrayBindings && direct.Length<=DrawCollapsePolicy.DirectBindings,
                "drawcollapse texture binding budget exceeds 4 arrays + 32 borrowed 2D");
            foreach(var tier in small.GroupBy(CollapseTier))
            {
                var group=tier.ToArray();var t=group[0];
                for(int start=0;start<group.Length;start+=SystemInfo.maxTextureArraySlices)
                {
                    RequireLive(SystemInfo.maxTextureArraySlices>0 && gpuCollapseArrays.Count<DrawCollapsePolicy.ArrayBindings,"drawcollapse exceeds four array bindings");
                    int depth=Math.Min(SystemInfo.maxTextureArraySlices,group.Length-start),array=gpuCollapseArrays.Count;
                    int maxW=0,maxH=0;
                    for(int slice=0;slice<depth;slice++){var tx=group[start+slice];if(tx.width>maxW)maxW=tx.width;if(tx.height>maxH)maxH=tx.height;}
                    var a=new Texture2DArray(maxW,maxH,depth,t.graphicsFormat,TextureCreationFlags.None,1)
                        {name="GpuDeco collapse tier "+array,wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Bilinear};
                    gpuCollapseArrays.Add(a);
                    RequireLive(a.graphicsFormat==t.graphicsFormat,"drawcollapse array format conversion rejected");
                    for(int slice=0;slice<depth;slice++)
                    {
                        var tex=group[start+slice];addresses.Add(tex.GetInstanceID(),new Vector4(array,slice,tex.width/(float)maxW,tex.height/(float)maxH));
                        Graphics.CopyTexture(tex,0,0,0,0,tex.width,tex.height,a,slice,0,0,0);copies++;
                    }
                }
            }
            foreach(var exactGroup in exactGroups)
            {
                RequireLive(gpuCollapseArrays.Count<DrawCollapsePolicy.ArrayBindings,"drawcollapse exact array exceeds array bindings");
                var t0=exactGroup[0];int exArray=gpuCollapseArrays.Count;
                var lease=exactPlans[exactGroup];
                var ea=new Texture2DArray(t0.width,t0.height,lease?.Slices??exactGroup.Length,t0.graphicsFormat,TextureCreationFlags.None,1)
                    {name="GpuDeco collapse exact "+t0.width+"x"+t0.height,wrapMode=TextureWrapMode.Clamp,filterMode=FilterMode.Bilinear};
                gpuCollapseArrays.Add(ea);
                RequireLive(ea.graphicsFormat==t0.graphicsFormat,"drawcollapse exact array format conversion rejected");
                if(lease!=null)
                {
                    // Apply BEFORE CopyTexture: applying after GPU copies could
                    // upload stale CPU pixels over the correct GPU contents.
                    // The array is only sampled/copied on the GPU thereafter.
                    if(DrawCollapsePolicy.ImageArrayGpuOnly && ea.isReadable)ea.Apply(false,true);
                    var bank=new ImageArrayBank{Plan=lease,Texture=ea,Array=exArray,Loaded=new int[lease.Slices],Originals=exactGroup};
                    foreach(var tex in exactGroup)
                    {
                        // A negative slice is a virtual registry entry, never
                        // sampled: each actual item resolves its private lease.
                        addresses.Add(tex.GetInstanceID(),new Vector4(exArray,-1,1,1));
                        gpuImageArrayBanks.Add(tex.GetInstanceID(),bank);
                    }
                    continue;
                }
                for(int slice=0;slice<exactGroup.Length;slice++)
                {
                    var tex=exactGroup[slice];addresses.Add(tex.GetInstanceID(),new Vector4(exArray,slice,1f,1f));
                    Graphics.CopyTexture(tex,0,0,0,0,tex.width,tex.height,ea,slice,0,0,0);copies++;
                }
            }
            foreach(var t in direct)
            {
                addresses.Add(t.GetInstanceID(),new Vector4(DrawCollapsePolicy.ArrayBindings+gpuCollapseDirect.Count,0,1,1));gpuCollapseDirect.Add(t);
            }
            for(int i=0;i<source.Data.Items.Length;i++)
            {
                if(!addresses.TryGetValue(source.Data.Items[i].TextureId,out var address))
                {
                    gpuCollapseTextureFallback[i]=true;gpuCollapseTextureFallbackCount++;gpuCollapseIncluded[i]=false;continue;
                }
                var t=textures[source.Data.Items[i].TextureId];
                address=ResolveImageArrayAddress(i,t,address);
                // Use the final address, not CollapseSize: exactGroup's large
                // textures also live in _Tex arrays. Missing addresses stay false.
                gpuCollapseArrayOnly[i]=address.x>=0 && address.x<gpuCollapseArrays.Count
                    && address.x<DrawCollapsePolicy.ArrayBindings;
                var dset=new List<int>();gpuCollapseDirectSet[i]=dset;
                if(address.x>=DrawCollapsePolicy.ArrayBindings)dset.Add((int)address.x-DrawCollapsePolicy.ArrayBindings);
#if AUTOTEST || GPUDECO
                if(GpuImageActor(gpuLiveOwnedIndices[source.MasterItems[i]]))
                {
                    int ownerF=gpuLiveOwnedIndices[source.MasterItems[i]];
                    if(gpuImageTimeline==null || gpuImageVariants==null)gpuCollapseDirectSet[i]=null; // 알 수 없음 = 전체 변형
                    else foreach(int evIdx in gpuImageTimeline.EventsForDecoration(ownerF))
                    {
                        var v=gpuImageVariants[gpuImageTimeline.Event(evIdx).Selector];
                        if(v.Status!=GpuDecoImageVariant.Kind.ReadyQuad)continue;
                        if(!addresses.TryGetValue(v.TextureId,out var fAddr)){gpuCollapseDirectSet[i]=null;break;}
                        if(fAddr.x>=DrawCollapsePolicy.ArrayBindings && !dset.Contains((int)fAddr.x-DrawCollapsePolicy.ArrayBindings))dset.Add((int)fAddr.x-DrawCollapsePolicy.ArrayBindings);
                    }
                }
                if(gpuCollapseArrayOnly[i] && GpuImageActor(gpuLiveOwnedIndices[source.MasterItems[i]]))
                {
                    int owner=gpuLiveOwnedIndices[source.MasterItems[i]];
                    bool allInArray=gpuImageTimeline!=null && gpuImageVariants!=null;
                    if(allInArray)
                        foreach(int evIdx in gpuImageTimeline.EventsForDecoration(owner))
                        {
                            var v=gpuImageVariants[gpuImageTimeline.Event(evIdx).Selector];
                            if(v.Status!=GpuDecoImageVariant.Kind.ReadyQuad)continue;
                            if(!addresses.TryGetValue(v.TextureId,out var vAddr) || vAddr.x<0 || vAddr.x>=gpuCollapseArrays.Count || vAddr.x>=DrawCollapsePolicy.ArrayBindings)
                            {allInArray=false;break;}
                        }
                    if(!allInArray)gpuCollapseArrayOnly[i]=false;
                }
#endif
                info[i*2]=CollapseInfo0(address,gpuCollapseOwnedRows[i].M,source.Data.Items[i],t);
                info[i*2+1]=new Vector4(address.z,address.w,address.z/t.width*.5f,address.w/t.height*.5f);
            }
            // 형태 칸 기록은 항목 기록(항목마다 2개) 뒤에 붙인다(_CollapseTexture[_Count*2 + 번호-1]).
            PrepareShapes(source);
            // 형태 칸을 켜면 기록 자리를 늘 예약해 둔다(칸이 늦게 도착해도 버퍼를 다시 만들지 않고 올리기만 한다)
            if(DrawCollapsePolicy.ShapeCells && DrawCollapsePolicy.GlobalVerts){var ext=new Vector4[info.Length+Math.Max(gpuShapeTable.Length,ShapeReserve)];Array.Copy(info,ext,info.Length);Array.Copy(gpuShapeTable,0,ext,info.Length,gpuShapeTable.Length);info=ext;}
            gpuCollapseTexture=new ComputeBuffer(info.Length,16);gpuCollapseTexture.SetData(info);
#if AUTOTEST || GPUDECO
            RememberGpuImageCollapseAddresses(addresses);
#endif
#if AUTOTEST
            PrepareSingleArrayProof(source);
#endif
            // Replace admitted overflow rows with the ACTUAL independent draw
            // key (including its material ID and sorting bounds), not the hidden
            // SpriteRenderer's key. Retain even currently invisible overflow.
            for(int i=0;i<gpuCollapseTextureFallback.Length;i++)if(DrawCollapseTextureFallback(i))
            {
                var renderer=source.Renderers[i];var material=renderer.sharedMaterial;
                var key=GpuDecoScene.SortKey(renderer,material,gpuLiveCamera);
                gpuCollapseBarriers.Add(new GpuLiveRow{R=renderer,M=material,Key=key,Item=-1});
            }
            SaveCollapseBaseState();
            BuildDrawCollapseSpans();
            if (GpuDecoPlayer.Product && DrawCollapsePolicy.RecordedDispatch && DrawCollapsePolicy.GlobalVerts)
                source.Gpu.RecordCollapseAll(gpuCollapseTexture,gpuCollapseAllVerts,gpuCollapseAllMap,gpuCollapseAllCount);
            GpuLog("DRAWCOLLAPSE_PREPARED spans="+gpuCollapseSpans.Count+" included="+gpuCollapseIncluded.Count(x=>x)
                +" arrays="+gpuCollapseArrays.Count+" bytes="+total+" copy_calls="+copies
                +" texture_fallback_items="+gpuCollapseTextureFallbackCount+" texture_fallback_dispatch="+(gpuCollapseTextureFallbackCount>0)
                +" submeshes_per_renderer=1 direct_evaluate=True window=260..270 conservative_hidden_barriers=True free_vram=UNVERIFIED");
            if(GpuDecoPlayer.Product)ReorderCopyForFallback();
        }
        // 동점/텍스처 초과로 독립 렌더러에 남긴 장식이 번호상 흩어져 있으면 "보이는 장식이 든 복사 덩어리(512개)"가 거의 전부 켜져
        // 곡 후반(알파>0 항목이 많을 때) 28,000개를 매 프레임 복사한다(GPU 18ms). 그런 장식을 복사 순서 맨 앞으로 모아 덩어리 몇 개만 복사한다.
        static void ReorderCopyForFallback()
        {
            var slice=gpuLiveSlices[0];
            if(gpuCollapseTextureFallbackCount==0 || slice.NativeCopy==null || slice.CopyMask==null)return;
            long t0=Stopwatch.GetTimestamp();
            int n=slice.Data.Items.Length;
            var baseOrder=Enumerable.Range(0,n).OrderBy(i=>CopyLocalityHint(slice.Data.Items[i].Index)).ThenBy(i=>i).ToArray();
            var order=baseOrder.Where(i=>gpuCollapseTextureFallback[i]).Concat(baseOrder.Where(i=>!gpuCollapseTextureFallback[i])).ToArray();
            try{slice.NativeCopy.Dispose();}catch{}
            slice.NativeCopy=null;
            slice.CopyMask=new GpuDecoCopyMask(n,order);
            slice.NativeCopy=GpuDecoCopy.TryCreate(gpuLiveShader,slice.Gpu.VertexBuffer,slice.Buffers,n*4,order);
            RequireLive(slice.NativeCopy!=null,"native copy unavailable after reorder: "+GpuDecoCopy.Status);
            if(GpuDecoPlayer.Product)Main.Entry.Logger.Log("[GPU 장식] 독립 렌더러 장식 "+gpuCollapseTextureFallbackCount+"개를 복사 순서 앞으로 모음 "+DcMs(t0).ToString("F0")+"ms");
        }
        static bool PlayerCollapseTextureBudget(IEnumerable<Texture2D> textures)
        {
            var all=textures.ToArray();
            return GpuDecoPlayerPolicy.FitsTextures(all.Length,all.Count(t=>CollapseSize(t)==0),
                all.Where(t=>CollapseSize(t)>0).GroupBy(CollapseTier).Select(g=>g.Count()).ToArray(),SystemInfo.maxTextureArraySlices);
        }
        static Material DrawCollapseMaterial(GpuLiveRow row)
        {
            if(!row.R)return null;
            if(row.Key.Slot==0 && row.M)return row.M;
            row.R.GetSharedMaterials(gpuCollapseMaterials);
            return row.Key.Slot<gpuCollapseMaterials.Count?gpuCollapseMaterials[row.Key.Slot]:null;
        }
        static bool DrawCollapseBarrierActive(GpuLiveRow row)
            =>row.R && row.R.enabled && row.R.gameObject.activeInHierarchy && !row.R.forceRenderingOff
                && (!(row.R is SpriteRenderer sprite) || sprite.sprite && sprite.color.a>0);
        static bool gpuCollapseKeysValid,gpuCollapseScanned;
        static Vector3 gpuCollapseKeyCamPos,gpuCollapseKeyFwd;
        static readonly HashSet<int> gpuCollapseDirtyIds=new HashSet<int>();
        static readonly List<scrDecoration> gpuCollapseNewDecos=new List<scrDecoration>();
        static SceneRenderOrder.Key DcShiftKey(SceneRenderOrder.Key k,float shift)
            =>shift==0f?k:new SceneRenderOrder.Key(k.Layer,k.Order,k.Queue,k.Distance-shift,k.Material,k.Renderer,k.Slot,k.Shader);
        // Dormancy 가 이미 거는 장식 후크(ApplyColor/SetVisible/SetMaskingType/EnableMeshRenderer/Setup/Awake)와 SetDepth 후크에서 부른다.
        internal static void CollapseMarkDirty(scrDecoration d)
        {
            var vd=d as scrVisualDecoration;if(vd==null)return;
            if(vd.spriteRenderer)gpuCollapseDirtyIds.Add(vd.spriteRenderer.GetInstanceID());
            if(vd.meshRenderer)gpuCollapseDirtyIds.Add(vd.meshRenderer.GetInstanceID());
        }
        internal static void CollapseMarkNew(scrDecoration d){if(gpuCollapseSeen!=null)gpuCollapseNewDecos.Add(d);CollapseMarkDirty(d);}
        static int TryAddCollapseBarrier(Renderer r)
        {
            if(!r || !r.gameObject.scene.IsValid() || !gpuCollapseSeen.Add(r.GetInstanceID()))return 0;
            if(gpuLiveRoot && r.transform.IsChildOf(gpuLiveRoot.transform))return 0;
            if((gpuLiveCamera.cullingMask&(1<<r.gameObject.layer))==0 || r.GetComponentInParent<SortingGroup>())return 0;
            var mats=r.sharedMaterials;if(mats==null)return 0;int added=0;
            for(int slot=0;slot<mats.Length;slot++)
            {
                var m=mats[slot];if(!m || m.renderQueue<=2500)continue;
                gpuCollapseBarriers.Add(new GpuLiveRow{R=r,M=m,Item=-1,AuxiliaryOwner=-1,Key=GpuDecoScene.SortKey(r,m,gpuLiveCamera,slot)});added++;
            }
            return added;
        }
        static double dcScanMs,dcTieMs,dcPlanMs,dcMeshMs,dcMatMs,dcMeshBuildMs,dcBufMs,dcGoMs,dcDisposeMs;
        static double DcMs(long from)=>(Stopwatch.GetTimestamp()-from)*1000.0/Stopwatch.Frequency;
#if AUTOTEST
        static readonly Dictionary<(int,int,int,float),HashSet<string>> diagBucketKinds=new Dictionary<(int,int,int,float),HashSet<string>>();
        static readonly Dictionary<string,int> diagExcludeKinds=new Dictionary<string,int>();
#endif
        // ---- 놀고 있는 구간 끄기: 알파가 상수 0 인 항목만 든 구간은 대표 렌더러를 꺼 둔다 ----
        static int[] gpuSpanOfItem;      // 슬라이스 항목 번호 -> 구간 번호(통합에 안 들어간 항목은 -1)
        static int[] gpuSpanVisible;     // 구간마다 "보일 수 있는" 항목 수
        static bool[] gpuItemHiddenState;
        static readonly List<int> gpuSpanDirty=new List<int>();
        static bool[] gpuSpanDirtyMark;
        static int gpuSpanEnabledNow;
        static void BuildSpanAlphaTracking()
        {
            gpuSpanOfItem=null;gpuSpanVisible=null;gpuItemHiddenState=null;gpuSpanDirty.Clear();gpuSpanDirtyMark=null;
            if(!GpuDecoPlayer.Product || !DrawCollapsePolicy.CullIdleSpans || gpuLiveSlices.Count==0 || gpuCollapseSpans.Count==0)return;
            int n=gpuLiveSlices[0].Data.Items.Length;
            gpuSpanOfItem=new int[n];for(int i=0;i<n;i++)gpuSpanOfItem[i]=-1;
            gpuSpanVisible=new int[gpuCollapseSpans.Count];gpuSpanDirtyMark=new bool[gpuCollapseSpans.Count];
            for(int k=0;k<gpuCollapseSpans.Count;k++)
            {
                var items=gpuCollapseSpans[k].Items;gpuSpanVisible[k]=items.Length;
                for(int j=0;j<items.Length;j++)if(items[j]<gpuInstBase)gpuSpanOfItem[items[j]]=k;
                MarkSpanDirty(k);
            }
            gpuItemHiddenState=new bool[n]; // 처음엔 모두 보일 수 있다고 두고, RefreshAlpha 가 실제 상태로 맞춘다
        }
        static void MarkSpanDirty(int k){if(!gpuSpanDirtyMark[k]){gpuSpanDirtyMark[k]=true;gpuSpanDirty.Add(k);}}
        static void CollapseSpanAlpha(int item,bool hidden)
        {
            if(gpuSpanOfItem==null || (uint)item>=(uint)gpuSpanOfItem.Length)return;
            int k=gpuSpanOfItem[item];if(k<0 || gpuItemHiddenState[item]==hidden)return;
            gpuItemHiddenState[item]=hidden;
            if(gpuSpanHasInst!=null && gpuSpanHasInst[k])return;   // 인스턴스 자리가 든 구간은 항상 전체 색인으로 그린다
            gpuSpanVisible[k]+=hidden?-1:1;
            gpuCollapseSpans[k].IndexDirty=true;
            MarkSpanDirty(k);
        }
        // 통합 디스패치 직전에 한 번: 바뀐 구간만 켜고 끈다.
        static Material[] diagShareOrig;
        // 진단 전용: 모든 구간 렌더러가 머티리얼 하나를 공유하게 해 머티리얼 교체 비용의 상한을 잰다(화면은 틀려진다).
        internal static void DiagShareMaterial(bool on)
        {
            if(on)
            {
                if(diagShareOrig!=null || gpuCollapseSpans.Count==0)return;
                diagShareOrig=new Material[gpuCollapseSpans.Count];var shared=gpuCollapseSpans[0].Renderer.sharedMaterial;
                for(int k=0;k<gpuCollapseSpans.Count;k++){var r=gpuCollapseSpans[k].Renderer;diagShareOrig[k]=r.sharedMaterial;r.sharedMaterial=shared;}
            }
            else if(diagShareOrig!=null)
            {
                for(int k=0;k<gpuCollapseSpans.Count && k<diagShareOrig.Length;k++){var r=gpuCollapseSpans[k].Renderer;if(r)r.sharedMaterial=diagShareOrig[k];}
                diagShareOrig=null;
            }
            Main.Entry.Logger.Log("[gpudeco] 구간 머티리얼 공유 "+(on?"켬":"끔"));
        }
        internal static void ForceIndexRebuild(){if(gpuSpanOfItem==null)return;for(int k=0;k<gpuCollapseSpans.Count;k++){gpuCollapseSpans[k].IndexDirty=true;MarkSpanDirty(k);}}
        internal static void MarkAllSpansDirty(){if(gpuSpanOfItem==null)return;for(int k=0;k<gpuCollapseSpans.Count;k++)MarkSpanDirty(k);}
#if DEV || AUTOTEST
        internal static long gpuSpanToggles;
#endif
        static void ApplySpanCulling()
        {
            if(gpuSpanOfItem==null)return;
            for(int n=0;n<gpuSpanDirty.Count;n++)
            {
                int k=gpuSpanDirty[n];gpuSpanDirtyMark[k]=false;
                var s=gpuCollapseSpans[k];
                var r=s.Renderer;bool want=gpuSpanVisible[k]>0 && !DrawCollapsePolicy.HideProxies;
                if(r && r.enabled!=want){r.enabled=want;
#if AUTOTEST
                    gpuSpanToggles++;
#endif
                }
                if(want && s.IndexDirty && s.AllIndices!=null && s.ActiveIndices!=null && s.Mesh)
                {
                    s.IndexDirty=false;
                    var items=s.Items;int visCount=gpuSpanVisible[k];
                    if(visCount>=items.Length || !DrawCollapsePolicy.CompactIndices)
                    {
                        if(s.LastUploadedIndexCount!=s.AllIndices.Length)
                        {
                            s.Mesh.SetIndices(s.AllIndices,0,s.AllIndices.Length,MeshTopology.Triangles,0,false,0);
                            s.LastUploadedIndexCount=s.AllIndices.Length;
                        }
                    }
                    else
                    {
                        var src=s.AllIndices;var dst=s.ActiveIndices;int write=0;
                        for(int j=0;j<items.Length;j++)
                        {
                            if(gpuItemHiddenState[items[j]])continue;
                            int b6=j*6;
                            dst[write]=src[b6];dst[write+1]=src[b6+1];dst[write+2]=src[b6+2];
                            dst[write+3]=src[b6+3];dst[write+4]=src[b6+4];dst[write+5]=src[b6+5];
                            write+=6;
                        }
                        if(write>0)
                        {
                            s.Mesh.SetIndices(dst,0,write,MeshTopology.Triangles,0,false,0);
                            s.LastUploadedIndexCount=write;
                        }
                    }
                }
            }
            gpuSpanDirty.Clear();
        }
        // ---- 곡 중에 깊이가 바뀌는 장벽: 실제 그리는 순서가 원래 ID 순서와 어긋나는 구간은 독립 렌더러로 넘긴다 ----
        static uint[] gpuCollapseMapCpu;
        static int[] gpuCollapseSlotOfItem;
        // ---- 곡 중 깊이가 바뀌는 소유 장식(깊이 인스턴스) ----
        // 그 장식이 갖는 깊이마다 정렬 키가 다른 그리기 자리를 하나씩 계획하고, 지금 깊이의 자리만 항목 번호를 넣는다(나머지는 빈 자리).
        // 구간 항목 번호가 gpuInstBase 이상이면 인스턴스 줄이다: 실제 항목은 gpuInstItem, 정렬 키는 gpuInstRows.
        static int gpuInstBase=int.MaxValue;
        static readonly List<GpuLiveRow> gpuInstRows=new List<GpuLiveRow>();
        static readonly List<int> gpuInstItem=new List<int>();
        static int[] gpuItemDepthNow;                       // 항목 → 지금 깊이(깊이 인스턴스 항목만 의미)
        static Dictionary<int,List<int>> gpuItemSlots;      // 깊이 인스턴스 항목 → 그 항목의 모든 자리
        static Dictionary<int,List<int>> gpuItemInstIds;    // 깊이 인스턴스 항목 → 인스턴스 줄 번호들
        static int[] gpuSlotDepth;                          // 자리 → 그 자리의 깊이
        static bool[] gpuSpanHasInst;                       // 인스턴스 자리가 든 구간(빈 구간 끄기·색인 줄이기를 하지 않는다)
        static int CR(int id)=>id<gpuInstBase?id:gpuInstItem[id-gpuInstBase];
        static GpuLiveRow CRow(int id)=>id<gpuInstBase?gpuCollapseOwnedRows[id]:gpuInstRows[id-gpuInstBase];
        static bool DepthItem(int item)=>gpuItemInstIds!=null && gpuItemInstIds.ContainsKey(item);
        static int SpanGameLayer(int id){var row=CRow(id);return DepthItem(CR(id))?DepthGameLayer(RowDepth(row)):row.R.gameObject.layer;}
        // 계획 때: 깊이가 바뀌는 통합 항목마다 다른 깊이의 줄을 더한다. 그 깊이 묶음에 켜진 장벽이 있거나 같은 재질의 줄이 이미 있으면
        // (원래도 순서가 재질·ID 동점 규칙에 달림) 그 항목은 통합에서 빼고 독립 렌더러로 둔다.
        static void BuildDepthInstanceRows(List<DrawCollapsePolicy.Row> rows,GpuLiveSlice source,Dictionary<(int,int,int,float,int),long> activeMin)
        {
            gpuInstRows.Clear();gpuInstItem.Clear();gpuItemInstIds=null;gpuItemSlots=null;gpuSlotDepth=null;
            int n=gpuCollapseOwnedRows.Length;gpuInstBase=n;
            gpuItemDepthNow=new int[n];
            // 모든 항목의 지금 깊이(깊이 변경 후크가 같은 값이면 아무것도 하지 않게)
            for(int i=0;i<n;i++){int m0=source.MasterItems[i];var r0=(uint)m0<(uint)gpuLiveOwnedRenderers.Length?gpuLiveOwnedRenderers[m0]:null;gpuItemDepthNow[i]=r0?-r0.sortingOrder:0;}
            if(!DrawCollapsePolicy.DepthInstances || gpuMasterDepths==null || gpuMasterDepths.Length==0)return;
            var taken=new HashSet<(int,int,int,float,long)>();
            foreach(var b in gpuCollapseBarriers)taken.Add((b.Key.Layer,b.Key.Order,b.Key.Queue,b.Key.Distance,b.Key.Rank));
            for(int i=0;i<n;i++)if(gpuCollapseIncluded[i] && gpuCollapseOwnedRows[i]!=null){var k=gpuCollapseOwnedRows[i].Key;taken.Add((k.Layer,k.Order,k.Queue,k.Distance,k.Rank));}
            int made=0,dropped=0;
            for(int i=0;i<n;i++)
            {
                int master=source.MasterItems[i];
                var depths=(uint)master<(uint)gpuMasterDepths.Length?gpuMasterDepths[master]:null;
                if(depths==null)continue;
                var orow=gpuCollapseOwnedRows[i];
                if(!gpuCollapseIncluded[i] || orow==null || !orow.R)continue;
                int primary=RowDepth(orow);
                var keys=new List<SceneRenderOrder.Key>();bool bad=false;
                foreach(int dep in depths)
                {
                    if(dep==primary)continue;
                    var k=new SceneRenderOrder.Key(SortingLayer.GetLayerValueFromID(DepthSortingLayerId(dep)),-dep,orow.Key.Queue,orow.Key.Distance,orow.Key.Material,orow.Key.Renderer,orow.Key.Slot,orow.Key.Shader);
                    if(activeMin.ContainsKey((k.Layer,k.Order,k.Queue,k.Distance,k.Shader)) || taken.Contains((k.Layer,k.Order,k.Queue,k.Distance,k.Rank))){bad=true;break;}
                    keys.Add(k);
                }
                if(bad)
                {
                    gpuCollapseIncluded[i]=false;dropped++;
                    if(!gpuCollapseTextureFallback[i]){gpuCollapseTextureFallback[i]=true;gpuCollapseTextureFallbackCount++;}
                    var ir=source.Renderers[i];
                    if(ir){var tieRow=new GpuLiveRow{R=ir,M=ir.sharedMaterial,Item=-1,AuxiliaryOwner=-1,Key=GpuDecoScene.SortKey(ir,ir.sharedMaterial,gpuLiveCamera)};gpuCollapseBarriers.Add(tieRow);gpuTieBarrierItem[tieRow]=i;}
                    continue;
                }
                var ids=new List<int>();
                foreach(var k in keys)
                {
                    taken.Add((k.Layer,k.Order,k.Queue,k.Distance,k.Rank));
                    int id=n+gpuInstRows.Count;
                    gpuInstRows.Add(new GpuLiveRow{R=orow.R,M=orow.M,Item=orow.Item,AuxiliaryOwner=-1,Key=k,Signature=orow.Signature});
                    gpuInstItem.Add(i);ids.Add(id);
                    rows.Add(new DrawCollapsePolicy.Row(k,true,id));
                }
                if(gpuItemInstIds==null)gpuItemInstIds=new Dictionary<int,List<int>>();
                gpuItemInstIds[i]=ids;made+=ids.Count;
            }
            Main.Entry.Logger.Log("[GPU 장식] 깊이 인스턴스 "+made+"줄(항목 "+(gpuItemInstIds!=null?gpuItemInstIds.Count:0)+"개), 순서 모호로 통합에서 뺀 항목 "+dropped+"개");
        }
        // 활성화 직전(곡 중간 시작 포함): 원래 렌더러의 지금 깊이로 자리를 맞춘다.
        static void SyncAllDepthItems()
        {
            if(gpuItemInstIds==null || gpuItemDepthNow==null || gpuCollapseMapCpu==null)return;
            var source=gpuLiveSlices[0];bool dirty=false;
            foreach(var kv in gpuItemInstIds)
            {
                int i=kv.Key;int master=source.MasterItems[i];
                gpuItemDepthNow[i]=-gpuLiveOwnedRenderers[master].sortingOrder;
                if(gpuCollapseIncluded[i] && WriteItemSlots(i,!G1DrawHidden(master),false))dirty=true;
            }
            if(dirty)
#if AUTOTEST
                UploadCollapseMapAll();
#else
                gpuCollapseAllMap?.SetData(gpuCollapseMapCpu);
#endif
        }
        // 게임 SetDepth: 깊이 >= 0 이면 Bg 층·레이어 9, 음수면 Default 층·레이어 7, 순서 -깊이
        static int DepthGameLayer(int depth)=>depth>=0?9:7;
        static int DepthSortingLayerId(int depth)=>SortingLayer.NameToID(depth>=0?"Bg":"Default");
        static int RowDepth(GpuLiveRow row)=>-row.Key.Order;
        // 지금 깊이에 맞는 줄(정렬 키 점검용)
        static GpuLiveRow CurrentDepthRow(int item)
        {
            var row=gpuCollapseOwnedRows[item];
            if(row==null || gpuItemInstIds==null || !gpuItemInstIds.TryGetValue(item,out var ids))return row;
            int now=gpuItemDepthNow[item];
            if(RowDepth(row)==now)return row;
            foreach(int id in ids){var r=gpuInstRows[id-gpuInstBase];if(RowDepth(r)==now)return r;}
            return row;
        }
        // 항목의 모든 자리에 쓴다: show 면 깊이가 맞는 자리만 항목 번호, 나머지는 빈 자리. upload 면 바뀐 자리만 바로 올린다.
        static bool WriteItemSlots(int item,bool show,bool upload)
        {
            bool dirty=false;
            if(gpuItemSlots!=null && gpuItemSlots.TryGetValue(item,out var slots))
            {
                int now=gpuItemDepthNow[item];
                foreach(int s in slots)
                {
                    uint v=show && gpuSlotDepth[s]==now?((uint)item|(gpuSlotBits!=null?gpuSlotBits[s]:0u)):uint.MaxValue;
                    if(gpuCollapseMapCpu[s]==v)continue;
                    gpuCollapseMapCpu[s]=v;dirty=true;
                    if(upload)
#if AUTOTEST
                        UploadCollapseMapSlot(s);
#else
                        gpuCollapseAllMap?.SetData(gpuCollapseMapCpu,s,s,1);
#endif
                }
                return dirty;
            }
            int slot=gpuCollapseSlotOfItem!=null?gpuCollapseSlotOfItem[item]:-1;
            if(slot<0)return false;
            uint value=show?((uint)item|(gpuSlotBits!=null?gpuSlotBits[slot]:0u)):uint.MaxValue;
            if(gpuCollapseMapCpu[slot]==value)return false;
            gpuCollapseMapCpu[slot]=value;
            if(upload)
#if AUTOTEST
                UploadCollapseMapSlot(slot);
#else
                gpuCollapseAllMap?.SetData(gpuCollapseMapCpu,slot,slot,1);
#endif
            return true;
        }
        // SetDepth 후크(제품): 소유 장식의 깊이가 바뀌었다. 독립 렌더러(텍스처 대체·동점 제외 등)는 원래와 같은 정렬로 두고,
        // 통합 중이면 지금 깊이의 자리로 옮긴다. 준비하지 않은 깊이면 원래 방식으로 돌아간다.
        internal static void GpuOwnedDepthChanged(scrVisualDecoration d)
        {
            try
            {
                if(!gpuLiveActive || !d || !gpuLiveMasterByDeco.TryGetValue(d,out int master))return;
                var r=d.spriteRenderer;if(!r)return;
                int depth=-r.sortingOrder;
                for(int s=0;s<gpuLiveSlices.Count;s++)
                {
                    int item=gpuLiveMasterToSliceItem[master][s];if(item<0)continue;
                    var ir=gpuLiveSlices[s].Renderers[item];
                    if(ir && (ir.sortingOrder!=r.sortingOrder || ir.sortingLayerID!=r.sortingLayerID)){ir.sortingLayerID=r.sortingLayerID;ir.sortingOrder=r.sortingOrder;ir.gameObject.layer=r.gameObject.layer;if(gpuCollapseOn)EnsureWatchedBarrier(ir);}
                }
                if(!gpuCollapseOn || gpuItemDepthNow==null || gpuLiveSlices.Count==0)return;
                int it=gpuLiveMasterToSliceItem[master][0];
                if(it<0 || (uint)it>=(uint)gpuItemDepthNow.Length || gpuItemDepthNow[it]==depth)return;
                gpuItemDepthNow[it]=depth;
                if(!gpuCollapseIncluded[it])return;
                bool prepared=false;
                if(gpuItemSlots!=null && gpuItemSlots.TryGetValue(it,out var slots))foreach(int sl in slots)if(gpuSlotDepth[sl]==depth){prepared=true;break;}
                if(!prepared){gpuCollapsePendingFallback=gpuCollapsePendingFallback??("소유 장식 깊이 변경(준비 안 한 깊이) item="+it+" depth="+depth);return;}
                SyncG1CollapseItem(it);
            }
            catch(Exception e){gpuCollapsePendingFallback=gpuCollapsePendingFallback??("소유 장식 깊이 변경 처리 예외: "+e.Message);}
        }
        static Dictionary<int,GpuLiveRow> gpuBarrierRowById;
        static readonly HashSet<int> gpuBarrierWatch=new HashSet<int>();
        static readonly Dictionary<int,(int,int,int,float,int)> gpuBarrierChecked=new Dictionary<int,(int,int,int,float,int)>();
        static readonly List<int> gpuDynExcludedItems=new List<int>();
        static bool[] gpuCollapseIncludedSaved,gpuCollapseFallbackSaved;
        static int gpuCollapseFallbackCountSaved;
        static int gpuDynTickFrame,gpuDynExcludedSpans,gpuDynTickCount,gpuDynActiveSeen,gpuDynSigChanged,gpuDynLogged,gpuDynLogged2;
        // 준비 때 한 번: 통합 계획의 기준 상태를 저장한다(동적 제외·CPU 처리 제외를 라운드마다 되돌리기 위해).
        static void SaveCollapseBaseState()
        {
            gpuCollapseIncludedSaved=gpuCollapseIncluded!=null?(bool[])gpuCollapseIncluded.Clone():null;
            gpuCollapseFallbackSaved=gpuCollapseTextureFallback!=null?(bool[])gpuCollapseTextureFallback.Clone():null;
            gpuCollapseFallbackCountSaved=gpuCollapseTextureFallbackCount;
        }
        static void RestoreCollapseBaseState()
        {
            if(gpuCollapseIncludedSaved!=null && gpuCollapseIncluded!=null && gpuCollapseIncluded.Length==gpuCollapseIncludedSaved.Length)Array.Copy(gpuCollapseIncludedSaved,gpuCollapseIncluded,gpuCollapseIncluded.Length);
            if(gpuCollapseFallbackSaved!=null && gpuCollapseTextureFallback!=null && gpuCollapseTextureFallback.Length==gpuCollapseFallbackSaved.Length)
            {Array.Copy(gpuCollapseFallbackSaved,gpuCollapseTextureFallback,gpuCollapseTextureFallback.Length);gpuCollapseTextureFallbackCount=gpuCollapseFallbackCountSaved;}
            ClearCollapseDynamicState();
        }
        // 미리 만든 계획을 그대로 쓸 때: 포함/폴백 배열은 그 계획과 짝이므로 두고, 지난 판의 동적 기록만 지운다.
        static readonly List<int> gpuStaticFallbackItems=new List<int>();
        static void SyncStaticFallbackList()
        {
            gpuStaticFallbackItems.Clear();
            if(gpuCollapseTextureFallback==null)return;
            for(int i=0;i<gpuCollapseTextureFallback.Length;i++)
                if(gpuCollapseTextureFallback[i])gpuStaticFallbackItems.Add(i);
        }
        static void ClearCollapseDynamicState()
        {
            gpuDynExcludedItems.Clear();gpuStaticFallbackItems.Clear();gpuDynZero=null;gpuBarrierWatch.Clear();gpuBarrierChecked.Clear();gpuDynExcludedSpans=0;gpuBucketMembers.Clear();gpuBucketDirty.Clear();
        }
        static readonly List<GpuLiveRow> gpuWatchRows=new List<GpuLiveRow>();
        static (int,float,int,int)[] gpuWatchSig;
        static bool[] gpuWatchActive;
        static (int,int,int,int)[] gpuWatchBucket;
        static long[] gpuWatchMat;   // 감시 장벽의 동점 순위(셰이더 ID, 재질 ID)
        static Dictionary<(int,int,int,int),List<int>> gpuSpansByBucket;
        static readonly Dictionary<(int,int,int,int),HashSet<int>> gpuBucketMembers=new Dictionary<(int,int,int,int),HashSet<int>>();
        static readonly HashSet<(int,int,int,int)> gpuBucketDirty=new HashSet<(int,int,int,int)>();
        static (int,int,int,int) BKey(int layer,int order,int queue,float dist)=>(layer,order,queue,(int)Math.Round(dist*1000.0));
        static void BuildBarrierIndex()
        {
            gpuBarrierRowById=new Dictionary<int,GpuLiveRow>(gpuCollapseBarriers.Count);
            foreach(var b in gpuCollapseBarriers)if(b.R)gpuBarrierRowById[b.R.GetInstanceID()]=b;
            // 구간이 있는 정렬 묶음 표
            gpuSpansByBucket=new Dictionary<(int,int,int,int),List<int>>();
            for(int k=0;k<gpuCollapseSpans.Count;k++)
            {
                var f=gpuCollapseSpans[k].First;var key=BKey(f.Layer,f.Order,f.Queue,f.Distance);
                if(!gpuSpansByBucket.TryGetValue(key,out var list)){list=new List<int>();gpuSpansByBucket[key]=list;}
                list.Add(k);
            }
            // 감시 대상: (1) 소유하지 않은 장식의 렌더러(깊이·위치가 곡 중에 바뀔 수 있음) (2) 구간이 있는 묶음에 든 모든 장벽(켜지면 순서가 달라질 수 있음)
            gpuWatchRows.Clear();var seen=new HashSet<int>();
            foreach(int wid in gpuBarrierWatch)
                if(gpuBarrierRowById.TryGetValue(wid,out var wrow) && seen.Add(wid))gpuWatchRows.Add(wrow);
            int n=gpuWatchRows.Count;
            gpuWatchSig=new (int,float,int,int)[n];gpuWatchActive=new bool[n];gpuWatchBucket=new (int,int,int,int)[n];gpuWatchMat=new long[n];
            gpuBucketMembers.Clear();gpuBucketDirty.Clear();
#if AUTOTEST
            Main.Entry.Logger.Log("[GPU 장식] 감시 장벽 "+n+"개(비소유 장식 렌더러 + 구간 묶음의 장벽), 구간 묶음 "+gpuSpansByBucket.Count+"개");
#endif
        }
        // SetDepth 후크(제품에도 설치)에서 부른다: 깊이가 바뀐 장식이 장벽이면 감시 목록에 넣는다.
        static void EnsureWatchedBarrier(Renderer r)
        {
            if(!r || gpuBarrierRowById==null || !gpuBarrierRowById.TryGetValue(r.GetInstanceID(),out var row))return;
            gpuBarrierWatch.Add(r.GetInstanceID());
            for(int i=0;i<gpuWatchRows.Count;i++)if(ReferenceEquals(gpuWatchRows[i],row))return;
            gpuWatchRows.Add(row);
            int n=gpuWatchRows.Count;
            Array.Resize(ref gpuWatchSig,n);
            Array.Resize(ref gpuWatchActive,n);
            Array.Resize(ref gpuWatchBucket,n);
            Array.Resize(ref gpuWatchMat,n);
        }
        internal static void GpuDepthChanged(scrVisualDecoration d)
        {
            if(!gpuCollapseOn || gpuBarrierRowById==null || !d)return;
            if(d.spriteRenderer)EnsureWatchedBarrier(d.spriteRenderer);
            if(d.meshRenderer)EnsureWatchedBarrier(d.meshRenderer);
        }
        static void WatchRemove(int w)
        {
            if(gpuBucketMembers.TryGetValue(gpuWatchBucket[w],out var set))set.Remove(w);
            gpuBucketDirty.Add(gpuWatchBucket[w]);
        }
        static void DynamicOrderTick()
        {
            if(gpuWatchRows.Count==0 || !gpuCollapseOn)return;
            if(Time.frameCount-gpuDynTickFrame<8)return;
            gpuDynTickFrame=Time.frameCount;
#if AUTOTEST
            gpuDynTickCount++;
#endif
            var cam=gpuLiveCamera;if(!cam)return;
            float shift=Vector3.Dot(cam.transform.position-gpuCollapseCameraPosition,gpuCollapseCameraForward);
            for(int w=0;w<gpuWatchRows.Count;w++)
            {
                var row=gpuWatchRows[w];var r=row.R;if(!r)continue;
                if(!DrawCollapseBarrierActive(row)){if(gpuWatchActive[w]){gpuWatchActive[w]=false;gpuWatchSig[w]=default;WatchRemove(w);}continue;}
                var origMat=DrawCollapseMaterial(row);
                if(!origMat)continue;
                // 값싼 서명(깊이·위치·머티리얼·층)이 그대로면 이미 본 상태다.
                var sig=(r.sortingOrder,r.transform.position.z,origMat.GetInstanceID(),r.sortingLayerID);
#if AUTOTEST
                gpuDynActiveSeen++;
#endif
                if(gpuWatchActive[w] && gpuWatchSig[w].Equals(sig))continue;
#if AUTOTEST
                gpuDynSigChanged++;
#endif
                gpuWatchSig[w]=sig;
                var cur=GpuDecoScene.SortKey(r,origMat,cam,row.Key.Slot);
                var nb=BKey(cur.Layer,cur.Order,cur.Queue,cur.Distance+shift);
                if(gpuWatchActive[w] && !gpuWatchBucket[w].Equals(nb))WatchRemove(w);
                gpuWatchActive[w]=true;gpuWatchBucket[w]=nb;gpuWatchMat[w]=cur.Rank;
#if AUTOTEST
                if(false)Main.Entry.Logger.Log("[GPU 장식] 감시 활성 w="+w+" "+r.GetType().Name+":"+r.name+" 키 "+DcKey(cur)+" 버킷 "+nb+" 구간 묶음에 있음 "+(gpuSpansByBucket!=null&&gpuSpansByBucket.ContainsKey(nb))+" shift "+shift.ToString("F2"));
#endif
                if(!gpuBucketMembers.TryGetValue(nb,out var members)){members=new HashSet<int>();gpuBucketMembers[nb]=members;}
                members.Add(w);gpuBucketDirty.Add(nb);
            }
            if(gpuBucketDirty.Count==0)return;
            foreach(var key in gpuBucketDirty)ReclassifyBucket(key);
            gpuBucketDirty.Clear();
        }
        // 묶음의 켜진 장벽 집합으로 구간마다 상태를 정한다: 가르거나 앞뒤 양쪽이면 독립 렌더러, 앞쪽에만 있으면 거리 보정, 아니면 그대로.
        static void ReclassifyBucket(in (int,int,int,int) key)
        {
            if(gpuSpansByBucket==null || !gpuSpansByBucket.TryGetValue(key,out var spans))return;
            gpuBucketMembers.TryGetValue(key,out var members);
            foreach(int k in spans)
            {
                var sp=gpuCollapseSpans[k];if(!sp.Renderer || !sp.Mesh)continue;
                long first=sp.First.Rank,last=sp.Last.Rank;
                bool before=false,after=false,straddle=false;
                if(members!=null)foreach(int w in members){long a=gpuWatchMat[w];if((a>>32)!=(first>>32))continue; /* 다른 셰이더 장벽은 엔진 순서 */ if(a<first)before=true;else if(a>last)after=true;else straddle=true;}
                int state=(straddle||(before&&after))?2:(before?1:(after?3:0));
#if AUTOTEST
                if(state!=0 && gpuDynLogged2++<6)Main.Entry.Logger.Log("[GPU 장식] 분류 묶음 "+key+" 구간 "+k+" (항목 "+sp.Items.Length+") 첫 id "+first+" 끝 id "+last+" 켜진 장벽 "+(members==null?0:members.Count)+" 앞 "+before+" 뒤 "+after+" 가름 "+straddle+" → 상태 "+state+" (이전 "+sp.DynState+")");
#endif
                if(state==sp.DynState)continue;
                if(state==2)ExcludeSpanDynamic(k);else if(sp.DynState==2)IncludeSpanDynamic(k);
                bool nowNudge=state==1||state==3;
                {
                    // 처음 계획과 같은 규칙: 모든 장벽이 구간 앞이면 가깝게(뒤에 그리기), 아니면 멀게(먼저 그리기)
                    var c=sp.CenterNow+(nowNudge?gpuCollapseCameraForward*(state==1?-DrawCollapsePolicy.TieNudge:DrawCollapsePolicy.TieNudge):Vector3.zero);
                    var nb=new Bounds(c,new Vector3(2000000,2000000,0));
                    sp.Mesh.bounds=nb;
                    sp.Renderer.bounds=nb; // 이미 만든 렌더러는 메시 경계가 바뀌어도 월드 경계를 다시 안 구한다: 정렬 거리에 쓰이는 월드 경계를 직접 설정
                }
                sp.DynState=state;
            }
        }
        // 독립 렌더러로 돌려진 항목(깊이가 바뀐 장벽과 순서가 어긋난 구간)은 알파가 0 이면 그리지 않는다.
        // 원래 게임은 안 보이는 장식을 forceRenderingOff 로 건너뛰는데, 이 항목들은 렌더러가 켜진 채 알파 0 으로 화면 크기 그림을 계속 그려 한 구간(약 1초)에 60 FPS 로 떨어졌다.
        static bool[] gpuDynZero;
        internal static bool DynExcludedZero(int item)=>gpuDynZero!=null && (uint)item<(uint)gpuDynZero.Length && gpuDynZero[item];
        static bool DynAlphaZero(int master)
        {
            if(G1DrawHidden(master))return true;
            float a=EvalMasterSlotCurrent(master*16+10),b=EvalMasterSlotCurrent(master*16+11);
            return !(a>0f) || !(b>0f);
        }
        static void ExcludedVisibilityTick()
        {
            if(gpuLiveSlices.Count==0)return;
            var slice=gpuLiveSlices[0];
            int total=gpuCollapseIncluded!=null?gpuCollapseIncluded.Length:0;
            if(total==0)return;
            if(gpuStaticFallbackItems.Count==0 && gpuCollapseTextureFallbackCount>0)SyncStaticFallbackList();
            var list=gpuDynExcludedItems;
            if(list.Count==0 && gpuStaticFallbackItems.Count==0)return;
            if(gpuDynZero==null || gpuDynZero.Length!=total)
            {
                gpuDynZero=new bool[total];
                for(int i=0;i<gpuStaticFallbackItems.Count;i++)
                {
                    int it=gpuStaticFallbackItems[i];
                    gpuDynZero[it]=DynAlphaZero(slice.MasterItems[it]);
                    if(gpuDynZero[it])slice.RefreshItem(it);
                }
            }
            for(int i=0;i<list.Count;i++)
            {
                int item=list[i];bool z=DynAlphaZero(slice.MasterItems[item]);
                if(z!=gpuDynZero[item]){gpuDynZero[item]=z;slice.RefreshItem(item);}
            }
            for(int i=0;i<gpuStaticFallbackItems.Count;i++)
            {
                int item=gpuStaticFallbackItems[i];bool z=DynAlphaZero(slice.MasterItems[item]);
                if(z!=gpuDynZero[item]){gpuDynZero[item]=z;slice.RefreshItem(item);}
            }
        }
        static bool[] gpuSpanExcluded;
        static bool gpuSpanExcludedFlag(int k)=>gpuSpanExcluded!=null && (uint)k<(uint)gpuSpanExcluded.Length && gpuSpanExcluded[k];
        static void ExcludeSpanDynamic(int k)
        {
            if(gpuSpanExcluded==null || gpuSpanExcluded.Length!=gpuCollapseSpans.Count)gpuSpanExcluded=new bool[gpuCollapseSpans.Count];
            if(gpuSpanExcluded[k])return;
            gpuSpanExcluded[k]=true;gpuDynExcludedSpans++;
            var sp=gpuCollapseSpans[k];var slice=gpuLiveSlices[0];bool mapDirty=false;
            foreach(int id in sp.Items)
            {
                int item=CR(id);
                if(!gpuCollapseIncluded[item])continue;
                gpuCollapseIncluded[item]=false;gpuDynExcludedItems.Add(item);
                if(gpuDynZero==null || gpuDynZero.Length!=gpuCollapseIncluded.Length)gpuDynZero=new bool[gpuCollapseIncluded.Length];
                gpuDynZero[item]=DynAlphaZero(slice.MasterItems[item]);
                if(!gpuCollapseTextureFallback[item]){gpuCollapseTextureFallback[item]=true;gpuCollapseTextureFallbackCount++;}
                if(WriteItemSlots(item,false,false))mapDirty=true;
                if(gpuSpanOfItem!=null)gpuSpanOfItem[item]=-1;
                slice.RefreshItem(item);
            }
            if(mapDirty && gpuCollapseAllMap!=null)
#if AUTOTEST
                UploadCollapseMapAll();
#else
                gpuCollapseAllMap.SetData(gpuCollapseMapCpu);
#endif
            if(gpuSpanVisible!=null){gpuSpanVisible[k]=0;MarkSpanDirty(k);}
            Main.Entry.Logger.Log("[GPU 장식] 곡 중 깊이가 바뀐 장벽과 순서가 어긋나 구간 "+k+"(항목 "+sp.Items.Length+"개)를 독립 렌더러로 전환 (누적 "+gpuDynExcludedSpans+"개)");
        }
        static void IncludeSpanDynamic(int k)
        {
            if(!gpuSpanExcludedFlag(k))return;
            gpuSpanExcluded[k]=false;gpuDynExcludedSpans=Math.Max(0,gpuDynExcludedSpans-1);
            var sp=gpuCollapseSpans[k];var slice=gpuLiveSlices[0];bool mapDirty=false;int visible=0;
            foreach(int id in sp.Items)
            {
                int item=CR(id);
                if(gpuCollapseIncluded[item])continue;   // 같은 항목의 다른 깊이 자리가 이미 되돌렸다
                if(gpuCollapseIncludedSaved!=null && !gpuCollapseIncludedSaved[item])continue; // 준비 때부터 통합에서 빠진 항목은 그대로
                gpuCollapseIncluded[item]=true;gpuDynExcludedItems.Remove(item);
                if(gpuDynZero!=null && (uint)item<(uint)gpuDynZero.Length)gpuDynZero[item]=false;
                bool saved=gpuCollapseFallbackSaved!=null && gpuCollapseFallbackSaved[item];
                if(gpuCollapseTextureFallback[item] && !saved){gpuCollapseTextureFallback[item]=false;gpuCollapseTextureFallbackCount--;}
                if(WriteItemSlots(item,true,false))mapDirty=true;
                if(gpuSpanOfItem!=null){if(id==item)gpuSpanOfItem[item]=k;bool hid=slice.IsHiddenAlpha(item);gpuItemHiddenState[item]=hid;if(!hid)visible++;}
                slice.RefreshItem(item);
            }
            if(mapDirty && gpuCollapseAllMap!=null)
#if AUTOTEST
                UploadCollapseMapAll();
#else
                gpuCollapseAllMap.SetData(gpuCollapseMapCpu);
#endif
            if(gpuSpanHasInst!=null && gpuSpanHasInst[k])visible=sp.Items.Length;
            if(gpuSpanVisible!=null){gpuSpanVisible[k]=visible;MarkSpanDirty(k);}
            Main.Entry.Logger.Log("[GPU 장식] 어긋나게 하던 장벽이 숨어 구간 "+k+"(항목 "+sp.Items.Length+"개)를 다시 통합으로 되돌림");
        }
        static int gpuCollapsePlannedFallback;
        // 제품: 활성화 때 CPU 처리로 남은 장식(진행 중인 네이티브 트윈 등)은 통합에서 빼고 원본 렌더러를 정렬 장벽으로 둔다.
        // 곡 중에 더 늘어나면 가드(개수 불일치)가 걸려 원본으로 복귀한다.
        static void ExcludeCpuFallbackFromCollapse()
        {
            gpuCollapsePlannedFallback=0;
            var source=gpuLiveSlices[0];
            for(int i=0;i<source.MasterItems.Length;i++)
            {
                if(!GpuCpuFallback(source.MasterItems[i]))continue;
                gpuCollapsePlannedFallback++;
                if(!gpuCollapseIncluded[i])continue;
                gpuCollapseIncluded[i]=false;gpuFallbackExcludedItems.Add(i);
                var orow=gpuCollapseOwnedRows[i];
                if(orow!=null && orow.R){var brow=new GpuLiveRow{R=orow.R,M=orow.M,Item=-1,AuxiliaryOwner=-1,Key=orow.Key,Signature=orow.Signature};gpuCollapseBarriers.Add(brow);gpuFallbackBarrierRows.Add(brow);}
            }
            if(gpuCollapsePlannedFallback!=gpuLiveCpuFallbackCount)throw new InvalidOperationException("CPU 처리 장식 개수 불일치");
        }
        internal static void ReplanCollapseForFallback()
        {
            ExcludeCpuFallbackFromCollapse();
            RebuildDrawCollapse();
        }
        static void BuildDrawCollapseSpans()
        {
#if AUTOTEST
            if(singleTileFlags!=null)Array.Fill(singleTileFlags,-1);
#endif
            long dcT0=Stopwatch.GetTimestamp();int nudgedSpans=0;
            InvalidateRecordedCollapse();
            var source=gpuLiveSlices[0];
            var rows=new List<DrawCollapsePolicy.Row>();
            // ---- 프로토타입: 키 캐시 + 변경 추적 재계획 ----
            // 직교 카메라가 앞축으로 움직이면 모든 정렬 거리가 같은 값만큼 변한다. 캐시한 키는 그 값으로 일괄 보정한다(앞축이 바뀌면 전부 다시 읽는다).
            var camNow=gpuLiveCamera.transform;
            bool keysValid=DrawCollapsePolicy.KeyCache && gpuCollapseKeysValid
                && Vector3.Dot(camNow.forward,gpuCollapseKeyFwd)>=0.999999f;
            float keyShift=keysValid?Vector3.Dot(camNow.position-gpuCollapseKeyCamPos,gpuCollapseKeyFwd):0f;
            // 곡 시작 뒤 파괴된 후보 장벽은 더 이상 그려지지 않으므로 뺀다.
            int lostBarriers=gpuCollapseBarriers.RemoveAll(b=>!b.R || !b.M);
            // 지난 계획에서 동점으로 뺀 장식이 기준 상태 복원으로 다시 통합에 들어오면, 남은 장벽 줄(그 장식의 독립 렌더러)이
            // 통합 항목 자신과 같은 재질로 동점이 되어 그 판 GPU 장식 전체가 꺼졌다(Arche 다시 하기 중간 시작). 줄을 지우고 아래에서 다시 판정한다.
            int staleTie=0;
            if(gpuTieBarrierItem.Count>0)
            {
                var stale=new List<GpuLiveRow>();
                foreach(var p in gpuTieBarrierItem)if((uint)p.Value>=(uint)gpuCollapseIncluded.Length || gpuCollapseIncluded[p.Value])stale.Add(p.Key);
                if(stale.Count>0)
                {
                    var staleSet=new HashSet<GpuLiveRow>(stale);
                    staleTie=gpuCollapseBarriers.RemoveAll(b=>staleSet.Contains(b));
                    foreach(var row in stale)gpuTieBarrierItem.Remove(row);
                }
            }
            int addedBarriers=0;
            if(gpuCollapseSeen!=null)
            {
                if(DrawCollapsePolicy.FullScanOnReplan || !gpuCollapseScanned)
                {
                    foreach(var r in Resources.FindObjectsOfTypeAll<Renderer>())addedBarriers+=TryAddCollapseBarrier(r);
                    gpuCollapseScanned=true;
                }
                else
                {
                    // 새로 만들어진 장식의 렌더러만 후크로 추적한 목록에서 가져온다.
                    foreach(var d in gpuCollapseNewDecos)
                    {
                        if((object)d==null)continue;
                        var vd=d as scrVisualDecoration;if(vd==null)continue;
                        addedBarriers+=TryAddCollapseBarrier(vd.spriteRenderer);
                        addedBarriers+=TryAddCollapseBarrier(vd.meshRenderer);
                    }
                }
                gpuCollapseNewDecos.Clear();
            }
            dcScanMs=DcMs(dcT0);long dcT1=Stopwatch.GetTimestamp();
            // 소유 장식은 변환이 얼어 있어 키가 변하지 않는다: 보정만 하고 다시 읽지 않는다.
            if(keysValid)
            {
                for(int i=0;i<gpuCollapseOwnedRows.Length;i++){var orow=gpuCollapseOwnedRows[i];if(orow!=null)orow.Key=DcShiftKey(orow.Key,keyShift);}
            }
            else
            {
                for(int i=0;i<gpuCollapseOwnedRows.Length;i++)
                {
                    var row=gpuCollapseOwnedRows[i];var material=DrawCollapseMaterial(row);
                    RequireLive(row.R && material,"drawcollapse replan owned renderer lost item="+i);
                    RequireLive(material==row.M,"drawcollapse replan owned material changed item="+i);
                    row.Key=GpuDecoScene.SortKey(row.R,material,gpuLiveCamera,row.Key.Slot);
                }
            }
            // 장벽: 지금 활성이거나 변경 이벤트가 온 것만 다시 읽고, 나머지는 보정한 캐시 키를 쓴다.
#if AUTOTEST
            diagBucketKinds.Clear();
#endif
            // 키 = (층,순서,큐,거리,셰이더). 같은 셰이더 안에서만 재질 ID 순서가 측정으로 확인됐다. 다른 셰이더 사이 순서는 아직 모른다
            // (다른 셰이더까지 순위로 비교하면 7777 124초에서 화면이 밝게 틀렸다: 셰이더 ID 만 106.9, 화면 복사 뒤 규칙 더해도 107 대 원래 74).
            var activeBucketMinMat=new Dictionary<(int,int,int,float,int),long>();
            var activeBucketMaxMat=new Dictionary<(int,int,int,float,int),long>();
            var allBucketMin=new Dictionary<(int,int,int,float,int),long>();
            var allBucketMax=new Dictionary<(int,int,int,float,int),long>();
            int rereadBarriers=0;
            foreach(var row in gpuCollapseBarriers)
            {
                bool active=DrawCollapseBarrierActive(row);
                bool tieBarrier=active && !(row.R is SpriteMask); // 스프라이트 마스크는 색을 그리지 않는다(활성 마스크가 있으면 기능을 쓰지 않음)
                bool refresh=!keysValid || active || gpuCollapseDirtyIds.Contains(row.R.GetInstanceID());
                if(refresh)
                {
                    var material=DrawCollapseMaterial(row);
                    RequireLive(row.R && material,"drawcollapse replan barrier lost");
                    row.M=material;row.Key=GpuDecoScene.SortKey(row.R,material,gpuLiveCamera,row.Key.Slot);rereadBarriers++;
                }
                else row.Key=DcShiftKey(row.Key,keyShift);
                if(!(row.R is SpriteMask))
                {
                    // 켜짐과 상관없이 같은 묶음·같은 셰이더의 모든 장벽: 거리 밀기 방향(나중에 켜져도 순서가 맞게)
                    var ak=row.Key;var akey=(ak.Layer,ak.Order,ak.Queue,ak.Distance,ak.Shader);
                    if(!allBucketMin.TryGetValue(akey,out long a0) || ak.Rank<a0)allBucketMin[akey]=ak.Rank;
                    if(!allBucketMax.TryGetValue(akey,out long a1) || ak.Rank>a1)allBucketMax[akey]=ak.Rank;
                }
                if(tieBarrier)
                {
                    var bk=row.Key;var bkey=(bk.Layer,bk.Order,bk.Queue,bk.Distance);var skey=(bk.Layer,bk.Order,bk.Queue,bk.Distance,bk.Shader);
#if AUTOTEST
                    if(GpuDecoPlayer.Product){if(!diagBucketKinds.TryGetValue(bkey,out var ks)){ks=new HashSet<string>();diagBucketKinds[bkey]=ks;}ks.Add(row.R.GetType().Name+":"+row.R.name+(active?"[활성]":"[숨김]"));}
#endif
                    if(!activeBucketMinMat.TryGetValue(skey,out long curMin) || bk.Rank<curMin)activeBucketMinMat[skey]=bk.Rank;
                    if(!activeBucketMaxMat.TryGetValue(skey,out long curMax) || bk.Rank>curMax)activeBucketMaxMat[skey]=bk.Rank;
                }
            }
#if AUTOTEST
            if(GpuDecoPlayer.Product)
            {
                var kinds=new Dictionary<string,int>();
                foreach(var row in gpuCollapseBarriers)
                {
                    bool act=DrawCollapseBarrierActive(row);
                    string k=(row.R?row.R.GetType().Name:"null")+":"+(row.R?row.R.name:"")+(act?" [활성]":" [숨김]");
                    kinds.TryGetValue(k,out int c);kinds[k]=c+1;
                }
                Main.Entry.Logger.Log("[GPU 장식] 장벽 종류: "+string.Join(" | ",kinds.OrderByDescending(x=>x.Value).Take(14).Select(x=>x.Key+"="+x.Value)));
            }
            if(GpuDecoPlayer.Product)
            {
                var ob=new HashSet<(int,int,int,int)>();
                for(int i=0;i<gpuCollapseOwnedRows.Length;i++){var orw=gpuCollapseOwnedRows[i];if(orw!=null && gpuCollapseIncluded[i]){var ok2=orw.Key;ob.Add(BKey(ok2.Layer,ok2.Order,ok2.Queue,ok2.Distance));}}
                var kk=new Dictionary<string,int>();
                foreach(var row in gpuCollapseBarriers)
                {
                    if(!row.R || !DrawCollapseBarrierActive(row))continue;
                    var bk=row.Key;if(!ob.Contains(BKey(bk.Layer,bk.Order,bk.Queue,bk.Distance)))continue;
                    string k=row.R.GetType().Name+":"+row.R.name;kk.TryGetValue(k,out int c);kk[k]=c+1;
                }
                Main.Entry.Logger.Log("[GPU 장식] 계획 시점에 켜져 있고 소유 장식과 같은 묶음인 장벽: "+(kk.Count==0?"없음":string.Join(" | ",kk.OrderByDescending(x=>x.Value).Take(10).Select(x=>x.Key+"="+x.Value))));
            }
#endif
            int tieExcluded=0;var newBarriers=new List<GpuLiveRow>();
            for(int i=0;i<gpuCollapseOwnedRows.Length;i++)
            {
                if(!gpuCollapseIncluded[i])continue;
                var orow=gpuCollapseOwnedRows[i];if(orow==null || !orow.R)continue;
                var ok=orow.Key;
                if(!activeBucketMinMat.TryGetValue((ok.Layer,ok.Order,ok.Queue,ok.Distance,ok.Shader),out long minBarrierMat))continue;
                if(DrawCollapsePolicy.KeepBeforeBarrier)
                {
                    if(ok.Rank<minBarrierMat)continue; // 모든 활성 장벽보다 앞: 구간에 유지(대표 거리를 +미세 이동)
                    if(DrawCollapsePolicy.KeepAfterBarrier && activeBucketMaxMat.TryGetValue((ok.Layer,ok.Order,ok.Queue,ok.Distance,ok.Shader),out long maxBarrierMat) && ok.Rank>maxBarrierMat)continue;
                }
                gpuCollapseIncluded[i]=false;
#if AUTOTEST
                if(GpuDecoPlayer.Product && diagBucketKinds.TryGetValue((ok.Layer,ok.Order,ok.Queue,ok.Distance),out var dk))foreach(var kk in dk){diagExcludeKinds.TryGetValue(kk,out int dc);diagExcludeKinds[kk]=dc+1;}
#endif
                if(!gpuCollapseTextureFallback[i]){gpuCollapseTextureFallback[i]=true;gpuCollapseTextureFallbackCount++;}
                var ir=source.Renderers[i];
                if(ir){var tieRow=new GpuLiveRow{R=ir,M=ir.sharedMaterial,Item=-1,AuxiliaryOwner=-1,Key=GpuDecoScene.SortKey(ir,ir.sharedMaterial,gpuLiveCamera)};newBarriers.Add(tieRow);gpuTieBarrierItem[tieRow]=i;}
                tieExcluded++;
            }
            var newBarrierSet=new HashSet<GpuLiveRow>(newBarriers);
            gpuCollapseBarriers.AddRange(newBarriers);
            dcTieMs=DcMs(dcT1);
            GpuLog("DRAWCOLLAPSE_TIE_EXCLUDED owned_items="+tieExcluded+" active_buckets="+activeBucketMinMat.Count);
#if AUTOTEST
            if(GpuDecoPlayer.Product)Main.Entry.Logger.Log("[GPU 장식] 제외 장식과 같은 묶음의 장벽(제외 항목 수 기준): "+string.Join(" | ",diagExcludeKinds.OrderByDescending(x=>x.Value).Take(16).Select(x=>x.Key+"="+x.Value)));
            diagExcludeKinds.Clear();
#endif
            if(GpuDecoPlayer.Product)Main.Entry.Logger.Log("[GPU 장식] 동점 묶음 때문에 독립 렌더러로 남긴 장식 "+tieExcluded+"개 (동점 묶음 "+activeBucketMinMat.Count+"개)");
            GpuLog("DRAWCOLLAPSE_REPLAN barriers_removed="+lostBarriers+" stale_tie_removed="+staleTie+" barriers_added="+addedBarriers+" barriers_now="+gpuCollapseBarriers.Count+" reread="+rereadBarriers+" keys_cached="+keysValid+" shift="+keyShift.ToString("R",Inv));
            BuildDepthInstanceRows(rows,source,activeBucketMinMat);
            foreach(var row in gpuCollapseBarriers)
                rows.Add(new DrawCollapsePolicy.Row(row.Key,false,row.R.GetInstanceID()));
            for(int i=0;i<gpuCollapseOwnedRows.Length;i++)
                if(gpuCollapseIncluded[i])rows.Add(new DrawCollapsePolicy.Row(gpuCollapseOwnedRows[i].Key,true,i));
            gpuCollapseCameraPosition=gpuLiveCamera.transform.position;
            gpuCollapseCameraForward=gpuLiveCamera.transform.forward;gpuCollapseOwnedCursor=0;
            gpuCollapseKeyCamPos=gpuCollapseCameraPosition;gpuCollapseKeyFwd=gpuCollapseCameraForward;gpuCollapseKeysValid=DrawCollapsePolicy.KeyCache;
            gpuCollapseDirtyIds.Clear();
            long dcT2=Stopwatch.GetTimestamp();
            // 혼합 모드 항목(화면 복사를 쓰는 항목) 표: 일반 항목 다음에 오면 구간을 끊는다
            {var src0=gpuLiveSlices[0].Data.Items;var gi=new bool[src0.Length+gpuInstRows.Count];for(int q=0;q<gi.Length;q++)gi[q]=DrawCollapsePolicy.BlendModes && GpuDecoBlend.NeedsGrab(src0[CR(q)].BlendMode);DrawCollapsePolicy.GrabItems=gi;}
            var plan=DrawCollapsePolicy.Plan(rows);
            DrawCollapsePolicy.GrabItems=null;
            plan=ExpandShapeSpans(plan,out var planRecs);
            dcPlanMs=DcMs(dcT2);dcMeshMs=0;long dcT3=Stopwatch.GetTimestamp();
            RequireLive(plan.Count>0 && plan.Count<=DrawCollapsePolicy.MaxSpans,"drawcollapse conservative prepared spans="+plan.Count+" exceeds renderer budget");
            dcMatMs=dcMeshBuildMs=dcBufMs=dcGoMs=0;
            // Old embedded bundles have no local keyword: retain all direct
            // bindings and the original variant even for array-only spans.
            bool supportsNoDirect=gpuCollapseShader.keywordSpace.FindKeyword(CollapseNoDirectKeyword).isValid;
            int baseRun=0,noDirectSpans=0,fewSpans=0;
            bool supportsFew=gpuCollapseShader.keywordSpace.FindKeyword(CollapseFewKeyword).isValid;

            if(DrawCollapsePolicy.GlobalDirect)for(int a=0;a<DrawCollapsePolicy.DirectBindings && gpuCollapseDirect.Count>0;a++)Shader.SetGlobalTexture("_Big"+a,gpuCollapseDirect[Math.Min(a,gpuCollapseDirect.Count-1)]);
            // 화면 복사(Grab) 구간이 있는 정렬 묶음: 유니티는 화면 복사 셰이더를 같은 묶음의 일반 셰이더보다 뒤에 그린다.
            // 그 묶음의 구간들을 모두 같은 화면 복사 셰이더로 그려 대표 렌더러 순서가 계획(재질 ID) 순서를 따르게 한다(7777 124초).
            var grabBuckets=new HashSet<(int,int,int,float)>();
            if(gpuCollapseBlendShader)foreach(var it0 in plan){if(it0.Any(it=>GpuDecoBlend.NeedsGrab(source.Data.Items[CR(it)].BlendMode))){var fk1=CRow(it0[0]).Key;grabBuckets.Add((fk1.Layer,fk1.Order,fk1.Queue,fk1.Distance));}}
            for(int planIndex=0;planIndex<plan.Count;planIndex++)
            {
                var items=plan[planIndex];
                long dcL0=Stopwatch.GetTimestamp();
                var first=CRow(items[0]);var last=CRow(items[items.Length-1]);
                var s=new CollapseSpan{Items=items,First=first.Key,Last=last.Key,Recs=planRecs?[planIndex]};gpuCollapseSpans.Add(s);
                s.Base=baseRun;baseRun+=items.Length;
                int spanLayer=SpanGameLayer(items[0]);
                RequireLive(items.All(i=>SpanGameLayer(i)==spanLayer),
                    "drawcollapse span crosses camera culling layers");
                // 블렌드 상태와 큐가 같은 구간은 재질 하나를 공유한다(엔진의 CanFlattenSharedMaterialData 해시 비용을 구간 수만큼 안 낸다).
                var blend=new float[3];int bi=0;
                foreach(string p in new[]{"_SrcBlend","_DstBlend","_BlendOp"})
                {
                    float expected=first.M.HasProperty(p)?first.M.GetFloat(p):(p=="_SrcBlend"?1:p=="_DstBlend"?10:0);
                    RequireLive(items.All(i=>!CRow(i).M.HasProperty(p) || CRow(i).M.GetFloat(p)==expected),
                        "drawcollapse incompatible blend states "+p);
                    blend[bi++]=expected;
                }
                bool noDirect=supportsNoDirect && gpuCollapseArrayOnly!=null
                    && items.All(i=>(uint)CR(i)<(uint)gpuCollapseArrayOnly.Length && gpuCollapseArrayOnly[CR(i)]);
                // 직접 텍스처를 조금만 쓰는 구간: 쓰는 것만 작은 칸에 매기는 DIRECT_FEW 변형(같은 그림, 바인딩·셰이더가 작다).
                List<int> few=null;
                if(!noDirect && supportsFew && DrawCollapsePolicy.FewDirect)
                {
                    var u=new SortedSet<int>();
                    foreach(int it0 in items){int it=CR(it0);var ds=(uint)it<(uint)gpuCollapseDirectSet.Length?gpuCollapseDirectSet[it]:null;if(ds==null){u=null;break;}foreach(int d in ds)u.Add(d);if(u.Count>DrawCollapsePolicy.FewDirectBindings)break;}
                    if(u!=null && u.Count<=DrawCollapsePolicy.FewDirectBindings)few=u.ToList();
                }
                // 혼합 모드 항목이 있는 구간은 화면 복사(GrabPass)를 쓰는 셰이더로 그린다
                bool grabSpan=gpuCollapseBlendShader && (items.Any(it=>GpuDecoBlend.NeedsGrab(source.Data.Items[CR(it)].BlendMode)) || grabBuckets.Contains((first.Key.Layer,first.Key.Order,first.Key.Queue,first.Key.Distance)));
                string matKey=first.M.renderQueue+"/"+blend[0]+"/"+blend[1]+"/"+blend[2]+"/"+noDirect+(few!=null?"/f"+string.Join(",",few):"")+(grabSpan?"/grab":"");
#if AUTOTEST
                int singleBinding=SingleArrayForSpan(items,grabSpan);
                if(singleBinding>=0)matKey+="/single"+singleBinding;
                int singleTile=SingleTileForSpan(items,singleBinding);
                if(singleTile>=0)matKey+="/tile"+singleTile;
#endif
                if(!gpuCollapseSharedMats.TryGetValue(matKey,out var shared) || !shared)
                {
                    shared=new Material(grabSpan?gpuCollapseBlendShader:gpuCollapseShader){name="GpuDeco collapse shared",renderQueue=first.M.renderQueue};
#if AUTOTEST
                    // One shader object for ALL plain spans: a per-span shader
                    // object would change shader-before-material tie ordering.
                    if(singleArrayShader&&!grabSpan)shared.shader=singleArrayShader;
#endif
                    shared.SetFloat("_SrcBlend",blend[0]);shared.SetFloat("_DstBlend",blend[1]);shared.SetFloat("_BlendOp",blend[2]);
                    if(noDirect)shared.EnableKeyword(CollapseNoDirectKeyword);
                    if(few!=null)shared.EnableKeyword(CollapseFewKeyword);
                    BindCollapseTextures(shared,noDirect,few);
#if AUTOTEST
                    BindSingleArray(shared,singleBinding,singleTile);
#endif
                    gpuCollapseSharedMats[matKey]=shared;
                }
                s.Material=shared;s.SharedMat=true;s.NoDirect=noDirect;s.Few=few;
#if AUTOTEST
                s.SingleArray=singleBinding;
                s.SingleTile=singleTile;
#endif
                if(noDirect)noDirectSpans++;
                if(few!=null)fewSpans++;
                dcMatMs+=DcMs(dcL0);long dcL1=Stopwatch.GetTimestamp();
                if(DrawCollapsePolicy.GlobalVerts)
                {
                    // 정점 자리는 전역 버퍼의 번호만 담는다(x). 실제 위치·색·UV 는 정점 셰이더가 전역 버퍼에서 읽는다.
                    s.Mesh=new Mesh{name="GpuDeco collapse span",indexFormat=IndexFormat.UInt32};
                    var slotIds=new Vector3[items.Length*4];
                    for(int n=0;n<items.Length;n++)for(int k4=0;k4<4;k4++)slotIds[n*4+k4]=new Vector3((s.Base+n)*4+k4,0,0);
                    s.Mesh.SetVertices(slotIds);
                }
                else
                {
                s.Mesh=new Mesh{name="GpuDeco collapse span",indexFormat=IndexFormat.UInt32,vertexBufferTarget=GraphicsBuffer.Target.Raw};
                s.Mesh.SetVertexBufferParams(items.Length*4,
                    new VertexAttributeDescriptor(VertexAttribute.Position,VertexAttributeFormat.Float32,3),
                    new VertexAttributeDescriptor(VertexAttribute.Color,VertexAttributeFormat.Float32,4),
                    new VertexAttributeDescriptor(VertexAttribute.TexCoord0,VertexAttributeFormat.Float32,4),
                    new VertexAttributeDescriptor(VertexAttribute.TexCoord1,VertexAttributeFormat.Float32,4));
                s.Mesh.SetVertexBufferData(new float[items.Length*60],0,0,items.Length*60);
                }
                var indices=new int[items.Length*6];
                for(int n=0;n<items.Length;n++)for(int t=0;t<6;t++)indices[n*6+t]=n*4+source.Data.Items[CR(items[n])].Triangles[t];
                s.AllIndices=indices;s.ActiveIndices=new int[indices.Length];s.LastUploadedIndexCount=indices.Length;s.IndexDirty=true;
                s.Mesh.subMeshCount=1;s.Mesh.SetIndices(indices,MeshTopology.Triangles,0,false);
                // Refresh the sort center too: a start-of-song mesh bound is stale at ready.
                Vector3 center=first.R.bounds.center;
                if(first.R is SpriteRenderer sprite && sprite.spriteSortPoint==SpriteSortPoint.Pivot)center=first.R.transform.position;
                // 활성 장벽이 있는 정렬 묶음에서 시작하는 구간: 구간에 남은 장식은 모두 장벽보다 앞에 그려져야 하므로, 대표 렌더러의 정렬 거리를
                // 아주 조금 더 멀게(먼저 그려지게) 해서 재질 ID 동점 해소를 엔진의 동점 규칙에 맡기지 않는다. 정점 위치는 GPU 가 쓰므로 화면은 같다.
                var baseCenter=center;
                var fk0=(first.Key.Layer,first.Key.Order,first.Key.Queue,first.Key.Distance,first.Key.Shader);
                if(DrawCollapsePolicy.NudgeTieSpans && (activeBucketMinMat.ContainsKey(fk0) || allBucketMin.ContainsKey(fk0)))
                {
                    var fk=fk0;
                    // 방향은 같은 셰이더 장벽 전체 기준(준비 때 꺼져 있던 장벽이 곡 중에 켜져도 맞게). 구간이 장벽 범위 안이면 켜진 장벽 기준.
                    bool afterAll=allBucketMax.TryGetValue(fk,out long amx) && first.Key.Rank>amx;
                    if(!afterAll && allBucketMin.TryGetValue(fk,out long amn) && first.Key.Rank>amn)afterAll=activeBucketMaxMat.TryGetValue(fk,out long mx) && first.Key.Rank>mx;
                    center+=gpuCollapseCameraForward*(afterAll?-DrawCollapsePolicy.TieNudge:DrawCollapsePolicy.TieNudge);s.Nudged=true;nudgedSpans++;s.DynState=afterAll?1:3;
                }
                s.CenterNow=baseCenter;s.Mesh.bounds=new Bounds(center,new Vector3(2000000,2000000,0));s.Mesh.UploadMeshData(false);
                if(!DrawCollapsePolicy.GlobalVerts)s.Vertices=s.Mesh.GetVertexBuffer(0);
                dcMeshBuildMs+=DcMs(dcL1);long dcL2=Stopwatch.GetTimestamp();
                RequireLive(s.Mesh.subMeshCount==1 && (DrawCollapsePolicy.GlobalVerts || s.Mesh.GetVertexBufferStride(0)==60),"drawcollapse raw mesh layout");
                if(!DrawCollapsePolicy.GlobalVerts){s.Map=new ComputeBuffer(items.Length,4);s.Map.SetData(items.Select(i=>(uint)CR(i)).ToArray());}
                dcBufMs+=DcMs(dcL2);long dcL3=Stopwatch.GetTimestamp();
                var go=new GameObject("GpuDeco collapse span"){hideFlags=HideFlags.HideAndDontSave,layer=spanLayer};
                go.transform.SetParent(gpuLiveRoot.transform,false);go.AddComponent<MeshFilter>().sharedMesh=s.Mesh;
                s.Renderer=go.AddComponent<MeshRenderer>();s.Renderer.enabled=false;s.Renderer.sharedMaterial=s.Material;
                s.Renderer.motionVectorGenerationMode=MotionVectorGenerationMode.ForceNoMotion;s.Renderer.allowOcclusionWhenDynamic=false;
                if(DepthItem(CR(items[0]))){int fd=RowDepth(first);s.Renderer.sortingLayerID=DepthSortingLayerId(fd);s.Renderer.sortingOrder=-fd;}
                else{s.Renderer.sortingLayerID=first.R.sortingLayerID;s.Renderer.sortingOrder=first.R.sortingOrder;}
                s.Renderer.shadowCastingMode=ShadowCastingMode.Off;s.Renderer.receiveShadows=false;
                s.Renderer.lightProbeUsage=LightProbeUsage.Off;s.Renderer.reflectionProbeUsage=ReflectionProbeUsage.Off;
                RequireLive(s.Renderer.sharedMaterials.Length==1,"drawcollapse requires one material");
                dcGoMs+=DcMs(dcL3);
            }
            if(DrawCollapsePolicy.GlobalVerts)
            {
                gpuCollapseAllVerts?.Dispose();gpuCollapseAllMap?.Dispose();
                gpuCollapseAllCount=baseRun;
                var allMap=new uint[baseRun];int at=0;
                var slotDepth=new int[baseRun];var slotBits=new uint[baseRun];
                if(gpuItemInstIds!=null || gpuShapeItems>0)gpuItemSlots=new Dictionary<int,List<int>>();
                foreach(var sp in gpuCollapseSpans)for(int e=0;e<sp.Items.Length;e++)
                {
                    int id=sp.Items[e];int it=CR(id);slotDepth[at]=RowDepth(CRow(id));
                    int rec=sp.Recs!=null?sp.Recs[e]:0;slotBits[at]=(uint)rec<<18;
                    if(gpuItemSlots!=null && (DepthItem(it) || rec>0)){if(!gpuItemSlots.TryGetValue(it,out var ls)){ls=new List<int>();gpuItemSlots[it]=ls;}ls.Add(at);}
                    allMap[at++]=(uint)it|slotBits[at-1];
                }
                gpuSlotBits=slotBits;
                gpuSlotDepth=slotDepth;
                gpuCollapseMapCpu=allMap;gpuCollapseSlotOfItem=new int[gpuLiveSlices[0].Data.Items.Length];
                for(int q=0;q<gpuCollapseSlotOfItem.Length;q++)gpuCollapseSlotOfItem[q]=-1;
                for(int q=0;q<allMap.Length;q++)gpuCollapseSlotOfItem[allMap[q]&0x3FFFFu]=q;   // 높은 비트 = 형태 칸 기록 번호
                // 모든 항목의 숨김(SetVisible)·깊이 상태를 자리표에 다시 적용한다. 새 자리표는 모두 "보임"으로 채워지므로
                // 켜는 시점에 다시 계획하면(형태 칸 도착·곡 중간 시작) 숨어 있어야 할 장식이 그려졌다(7777 124초: 원래 안 보이는 장식 수십 개, 평균 밝기 74 -> 95).
                {var mi=gpuLiveSlices[0].MasterItems;for(int q=0;q<gpuCollapseSlotOfItem.Length;q++)if(gpuCollapseSlotOfItem[q]>=0 || (gpuItemSlots!=null && gpuItemSlots.ContainsKey(q)))WriteItemSlots(q,!G1DrawHidden(mi[q]),false);}
                gpuCollapseAllVerts=new ComputeBuffer(Math.Max(1,baseRun*60),4,ComputeBufferType.Raw);
                gpuCollapseAllMap=new ComputeBuffer(Math.Max(1,baseRun),4);gpuCollapseAllMap.SetData(allMap);
#if AUTOTEST
                NewMapUploadContext();
#endif
                Shader.SetGlobalBuffer("_CollapseVerts",gpuCollapseAllVerts);
            }
            // 같은 (층,순서,큐,거리) 묶음에서 이어지는 구간들이 재질을 공유하면 완전 동점이 되어 순서가 렌더러 ID(생성 순서의 역순)로 뒤집힌다.
            // 그런 구간들은 재질을 따로 만들되 뒤 구간부터 만들어 앞 구간의 인스턴스 ID 가 더 작게(= 먼저 그려지게) 한다.
            int multiSpans=0;
            for(int ri=0;ri<gpuCollapseSpans.Count;)
            {
                int rj=ri+1;
                while(rj<gpuCollapseSpans.Count && DcSameBucket(gpuCollapseSpans[ri].First,gpuCollapseSpans[rj].First))rj++;
                if(rj-ri>=2)
                {
                    multiSpans+=rj-ri;
                    for(int k=rj-1;k>=ri;k--)
                    {
                        var sp=gpuCollapseSpans[k];var m=new Material(sp.Material){name="GpuDeco collapse ordered"};
                        // The copy constructor retains shader keywords and texture bindings.
                        RequireLive(m.IsKeywordEnabled(CollapseNoDirectKeyword)==sp.Material.IsKeywordEnabled(CollapseNoDirectKeyword)
                            && m.IsKeywordEnabled(CollapseFewKeyword)==sp.Material.IsKeywordEnabled(CollapseFewKeyword),
                            "drawcollapse ordered material lost texture variant");
                        // 재질 복사는 Properties 에 없는 텍스처·배열 값을 옮기지 않는다(Exit 에서 동점 구간 장식이 투명해짐). 다시 묶는다.
                        BindCollapseTextures(m,sp.NoDirect,sp.Few);
#if AUTOTEST
                        BindSingleArray(m,sp.SingleArray,sp.SingleTile);
#endif
                        sp.Material=m;sp.SharedMat=false;sp.Renderer.sharedMaterial=m;
                    }
                }
                ri=rj;
            }
            BuildBarrierIndex();
            gpuSpanHasInst=new bool[gpuCollapseSpans.Count];
            for(int k=0;k<gpuCollapseSpans.Count;k++)foreach(int id in gpuCollapseSpans[k].Items)if(id>=gpuInstBase){gpuSpanHasInst[k]=true;break;}
            BuildSpanAlphaTracking();
            gpuSpanExcluded=null;
            GpuLog("DRAWCOLLAPSE_SAME_BUCKET_SPANS spans_in_multi_runs="+multiSpans+" nudged_spans="+nudgedSpans);
            if(GpuDecoPlayer.Product)Main.Entry.Logger.Log("[GPU 장식] 동점 묶음 구간 "+multiSpans+"개(재질 따로), 거리 보정 "+nudgedSpans+"개, 공유 재질 종류 "+gpuCollapseSharedMats.Count);
            GpuLog("DRAWCOLLAPSE_NODIRECT spans="+noDirectSpans+" of "+gpuCollapseSpans.Count);
#if AUTOTEST
            if(singleArrayShader)GpuLog("SINGLE_ARRAY_SPANS selected="+gpuCollapseSpans.Count(sp=>sp.SingleArray>=0)+" total="+gpuCollapseSpans.Count+" items="+gpuCollapseSpans.Where(sp=>sp.SingleArray>=0).Sum(sp=>sp.Items.Length)+" ordering_plan_unchanged=True");
#endif
            if(GpuDecoPlayer.Product)Main.Entry.Logger.Log("[GPU 장식] 그리기 구간 "+gpuCollapseSpans.Count+"개 중 배열 텍스처만 쓰는 구간 "+noDirectSpans+"개, 직접 텍스처 적은 구간 "+fewSpans+"개, 화면 복사 구간 "+gpuCollapseSpans.Count(sp=>sp.Material && sp.Material.shader==gpuCollapseBlendShader)+"개 (모드별 항목: "+string.Join(",",gpuCollapseSpans.SelectMany(sp=>sp.Items).Select(id=>source.Data.Items[CR(id)].BlendMode).Where(b=>b>0).GroupBy(b=>b).OrderBy(g=>g.Key).Select(g=>g.Key+"="+g.Count()))+")");
        }
        // 텍스처 정보 앞쪽: x = 바인딩 + 점 샘플링 128 + U 랩 반복 256 + V 랩 반복 512 + 혼합 모드*1024, y = 배열 층,
        // zw = UV 배율(재질 RepeatX/Y 실효값, 1 이면 0). 랩 비트는 그 텍스처의 실제 랩 모드(Repeat 일 때만 frac, 아니면 가장자리 고정)를 따른다.
        internal static Vector4 CollapseInfo0(Vector4 address,Material m,GpuDecoData.Item it,Texture tex=null)
        {
            bool rep=DrawCollapsePolicy.Repeat && it!=null && (it.RepeatX!=1f || it.RepeatY!=1f);
            bool point=m && (m.IsKeywordEnabled("USE_POINT_SAMPLING") || m.IsKeywordEnabled("FORCE_POINT_SAMPLING"));
            // 원래 셰이더(Sprites/Tile, Particles/Additive)는 키워드가 없으면 텍스처 자체의 필터로 읽는다: 점 필터 텍스처는 점 샘플링(7777 확대 배경)
            if(tex && tex.filterMode==FilterMode.Point)point=true;
            int blend=it!=null && DrawCollapsePolicy.BlendModes ? it.BlendMode : 0;
            return new Vector4(address.x+(point?128:0)+(rep&&it.WrapU?256:0)+(rep&&it.WrapV?512:0)+blend*1024,address.y,rep?it.RepeatX:0,rep?it.RepeatY:0);
        }
        // 구간 재질의 텍스처 묶기: 배열(_SfDecoTex*), 직접 텍스처(_Big*), DIRECT_FEW 의 _DirectMap. Properties 에 없으므로 재질마다 직접 넣는다.
        static void BindCollapseTextures(Material m,bool noDirect,List<int> few)
        {
            for(int a=0;a<DrawCollapsePolicy.ArrayBindings && gpuCollapseArrays.Count>0;a++)m.SetTexture("_SfDecoTex"+a,gpuCollapseArrays[Math.Min(a,gpuCollapseArrays.Count-1)]);
            if(noDirect)return;
            if(few!=null)
            {
                var map=new float[DrawCollapsePolicy.DirectBindings];
                for(int k=0;k<few.Count;k++){map[few[k]]=k;m.SetTexture("_Big"+k,gpuCollapseDirect[few[k]]);}
                if(gpuCollapseDirect.Count>0)for(int k=few.Count;k<DrawCollapsePolicy.FewDirectBindings;k++)m.SetTexture("_Big"+k,gpuCollapseDirect[few.Count>0?few[0]:0]);
                m.SetFloatArray("_DirectMap",map);
                return;
            }
            if(!DrawCollapsePolicy.GlobalDirect)for(int a=0;a<DrawCollapsePolicy.DirectBindings && gpuCollapseDirect.Count>0;a++)m.SetTexture("_Big"+a,gpuCollapseDirect[Math.Min(a,gpuCollapseDirect.Count-1)]);
        }
        static bool DcSameBucket(SceneRenderOrder.Key a,SceneRenderOrder.Key b)=>a.Layer==b.Layer && a.Order==b.Order && a.Queue==b.Queue && a.Distance==b.Distance;
        static void RebuildDrawCollapse()
        {
            RequireDrawCollapse();
            RequireLive(!gpuCollapseOn && gpuCollapseTexture!=null && gpuCollapseShader,
                "drawcollapse ready requires prepared textures and collapse off");
            // Only span resources are replaced. Texture arrays, borrowed textures,
            // texture address buffer, and the loaded shader survive every ready.
            long dcReadyT0=Stopwatch.GetTimestamp();
            foreach(var span in gpuCollapseSpans)span.Dispose();gpuCollapseSpans.Clear();
            dcDisposeMs=DcMs(dcReadyT0);
            try {BuildDrawCollapseSpans();}
            catch
            {
                foreach(var span in gpuCollapseSpans)span.Dispose();gpuCollapseSpans.Clear();
                throw;
            }
            GpuLog("DRAWCOLLAPSE_REPLAN_MS scan="+dcScanMs.ToString("F1",Inv)+" tie="+dcTieMs.ToString("F1",Inv)+" plan="+dcPlanMs.ToString("F1",Inv)+" mat="+dcMatMs.ToString("F1",Inv)+" mesh="+dcMeshBuildMs.ToString("F1",Inv)+" buf="+dcBufMs.ToString("F1",Inv)+" go="+dcGoMs.ToString("F1",Inv)+" dispose="+dcDisposeMs.ToString("F1",Inv)+" total="+DcMs(dcReadyT0).ToString("F1",Inv));
            gpuCollapseReplanTime=Time.realtimeSinceStartupAsDouble;
            SyncStaticFallbackList();
            if(GpuDecoPlayer.Product && gpuCollapseTextureFallbackCount>0)ReorderCopyForFallback();
            GpuLog("DRAWCOLLAPSE_REPLANNED spans="+gpuCollapseSpans.Count+" included="+gpuCollapseIncluded.Count(x=>x)
                +" barriers="+gpuCollapseBarriers.Count+" arrays_reused="+gpuCollapseArrays.Count
                +" texture_buffer_reused=True shader_reused=True copy_calls=0 owned_checks_per_frame="+DrawCollapsePolicy.OwnedChecksPerFrame);
        }
        static int DcTriple(int layer,int order,int queue,SceneRenderOrder.Key k)
        {
            int c=layer.CompareTo(k.Layer);if(c!=0)return c;
            c=order.CompareTo(k.Order);if(c!=0)return c;
            return queue.CompareTo(k.Queue);
        }
        static string DcKey(SceneRenderOrder.Key k)=>"("+k.Layer+","+k.Order+","+k.Queue+","+k.Distance.ToString("R",Inv)+",m"+k.Material+")";
        static List<GpuLiveRow> CollapseBarriersToCheck(bool full)
        {
            if(full || gpuCollapseBarriers.Count<=CollapseBarrierSweepPerFrame)return gpuCollapseBarriers;
            gpuCollapseBarrierSweep.Clear();int count=gpuCollapseBarriers.Count;
            if(gpuCollapseBarrierCursor>=count)gpuCollapseBarrierCursor=0;
            for(int n=0;n<CollapseBarrierSweepPerFrame;n++){gpuCollapseBarrierSweep.Add(gpuCollapseBarriers[gpuCollapseBarrierCursor]);if(++gpuCollapseBarrierCursor>=count)gpuCollapseBarrierCursor=0;}
            return gpuCollapseBarrierSweep;
        }
#if AUTOTEST
        internal static bool DiagFull; static int diagOrderLogged;
        static void DiagOrder(string reason){if(diagOrderLogged++<25)Main.Entry.Logger.Log("[gpudeco] fullcheck 위반: "+reason);}
        // 진단: 지금 씬의 그리는 렌더러 중 곡 시작 때 계획의 장벽 목록에 없던 것(곡 중에 새로 생긴 것)을 센다.
        static void DiagNewBarriers()
        {
            var known=new HashSet<int>();foreach(var b in gpuCollapseBarriers)if(b.R)known.Add(b.R.GetInstanceID());
            foreach(var o in gpuCollapseOwnedRows)if(o!=null && o.R)known.Add(o.R.GetInstanceID());
            if(gpuCollapseSeen!=null)foreach(int id in gpuCollapseSeen)known.Add(id);
            var kinds=new Dictionary<string,int>();int total=0;
            foreach(var r in Resources.FindObjectsOfTypeAll<Renderer>())
            {
                if(!r || !r.gameObject.scene.IsValid() || known.Contains(r.GetInstanceID()))continue;
                if((gpuLiveCamera.cullingMask&(1<<r.gameObject.layer))==0)continue;
                var mats=r.sharedMaterials;bool trans=false;foreach(var m in mats)if(m && m.renderQueue>2500)trans=true;if(!trans)continue;
                bool act=r.enabled && r.gameObject.activeInHierarchy && !r.forceRenderingOff;
                string k=r.GetType().Name+":"+r.name+(act?"[활성]":"[숨김]");kinds.TryGetValue(k,out int c);kinds[k]=c+1;total++;
            }
            Main.Entry.Logger.Log("[gpudeco] 곡 중 새로 생긴 그리는 렌더러(계획에 없던 것) "+total+"개: "+string.Join(" | ",kinds.OrderByDescending(x=>x.Value).Take(10).Select(x=>x.Key+"="+x.Value)));
        }
        static Dictionary<int,string> diagSpanSnap;
        // 진단: 구간(첫 항목 기준)마다 대표 렌더러의 정렬·위치·머티리얼·켜짐 값을 저장/비교한다.
        internal static void DiagSpans(string mode)
        {
            var cur=new Dictionary<int,string>();
            foreach(var sp in gpuCollapseSpans)
            {
                if(!sp.Renderer)continue;
                var m=sp.Renderer.sharedMaterial;
                cur[sp.Items[0]]=sp.Items.Length+"/L"+sp.Renderer.sortingLayerID+"/O"+sp.Renderer.sortingOrder+"/Q"+(m?m.renderQueue:-1)+"/Z"+sp.Renderer.bounds.center.z.ToString("F4")+"/en"+(sp.Renderer.enabled?1:0)+"/lay"+sp.Renderer.gameObject.layer+"/nudge"+(sp.Nudged?1:0)+"/"+(m?m.shader.name:"-");
            }
            if(mode=="enableall")
            {
                int off=0,offItems=0,big=0;
                for(int k=0;k<gpuCollapseSpans.Count;k++){var sp=gpuCollapseSpans[k];if(sp.Renderer && !sp.Renderer.enabled){off++;offItems+=sp.Items.Length;if(sp.Items.Length>100){big++;Main.Entry.Logger.Log("[gpudeco] 꺼져 있던 큰 구간 "+k+" 항목 "+sp.Items.Length+" 보임수 "+(gpuSpanVisible!=null?gpuSpanVisible[k]:-1));}sp.Renderer.enabled=true;}}
                Main.Entry.Logger.Log("[gpudeco] 구간 전부 켬: 꺼져 있던 구간 "+off+"개(항목 "+offItems+", 큰 구간 "+big+") / 전체 "+gpuCollapseSpans.Count);
                return;
            }
            if(mode=="snap"){diagSpanSnap=cur;Main.Entry.Logger.Log("[gpudeco] 구간 값 저장 "+cur.Count+"개");return;}
            if(diagSpanSnap==null)return;
            int same=0,diff=0,onlyA=0,onlyB=0;var ex=new List<string>();
            foreach(var kv in diagSpanSnap)
            {
                if(!cur.TryGetValue(kv.Key,out var v)){onlyA++;continue;}
                if(v==kv.Value){same++;continue;}
                diff++;if(ex.Count<8)ex.Add("첫항목"+kv.Key+" 저장["+kv.Value+"] 지금["+v+"]");
            }
            foreach(var kv in cur)if(!diagSpanSnap.ContainsKey(kv.Key))onlyB++;
            Main.Entry.Logger.Log("[gpudeco] 구간 값 비교: 같음 "+same+", 다름 "+diff+", 저장에만 "+onlyA+", 지금에만 "+onlyB);
            foreach(var e in ex)Main.Entry.Logger.Log("[gpudeco] 구간 차이 "+e);
        }
        static HashSet<int> diagExclSnap;
        // 진단: 지금 통합에서 빠져 독립 렌더러로 그려지는 항목(준비 때부터 빠진 것 제외)을 저장/비교한다.
        internal static void DiagExcl(string mode)
        {
            var cur=new HashSet<int>();
            for(int i=0;i<gpuCollapseIncluded.Length;i++)if(!gpuCollapseIncluded[i] && !(gpuCollapseIncludedSaved!=null && !gpuCollapseIncludedSaved[i]))cur.Add(i);
            if(mode=="snap"){diagExclSnap=cur;Main.Entry.Logger.Log("[gpudeco] 독립 렌더러 항목 저장 "+cur.Count+"개");return;}
            if(diagExclSnap==null){Main.Entry.Logger.Log("[gpudeco] 저장된 항목 없음");return;}
            int onlyA=0,onlyB=0;var sa=new List<string>();var sb=new List<string>();
            foreach(int i in diagExclSnap)if(!cur.Contains(i)){onlyA++;if(sa.Count<4){var k=gpuCollapseOwnedRows[i].Key;sa.Add(i+":"+DcKey(k));}}
            foreach(int i in cur)if(!diagExclSnap.Contains(i)){onlyB++;if(sb.Count<4){var k=gpuCollapseOwnedRows[i].Key;sb.Add(i+":"+DcKey(k));}}
            Main.Entry.Logger.Log("[gpudeco] 독립 렌더러 항목 비교: 저장 "+diagExclSnap.Count+"개 / 지금 "+cur.Count+"개 | 저장에만 "+onlyA+" ["+string.Join(" ",sa)+"] | 지금에만 "+onlyB+" ["+string.Join(" ",sb)+"]");
        }
        internal static void RunFullCheck()
        {
            DiagFull=true;diagOrderLogged=0;
            try{try{DiagNewBarriers();}catch(Exception e0){Main.Entry.Logger.Log("[gpudeco] 새 렌더러 점검 예외: "+e0);}
                ValidateDrawCollapse(true);Main.Entry.Logger.Log("[gpudeco] fullcheck 끝: 위반 "+diagOrderLogged+"개(치명 없음) 구간 "+gpuCollapseSpans.Count+" 장벽 "+gpuCollapseBarriers.Count+" | 동적 점검 횟수 "+gpuDynTickCount+", 활성 장벽 본 횟수 "+gpuDynActiveSeen+", 서명 바뀜 "+gpuDynSigChanged+", 제외한 구간 "+gpuDynExcludedSpans);}
            catch(Exception e){Main.Entry.Logger.Log("[gpudeco] fullcheck 치명: "+e.Message.Substring(0,Math.Min(400,e.Message.Length))+" | 위반 "+diagOrderLogged);}
            finally{DiagFull=false;}
        }
#endif
        static void ValidateDrawCollapse(bool full=true)
        {
            SyncAllDepthItems();
#if AUTOTEST
            var guard=new DrawCollapseGuard(full && !GpuDecoPlayer.Product, DiagFull ? DiagOrder : (GpuDecoPlayer.Product && !full ? GpuDecoPlayer.OrderViolation : (Action<string>)null));
#else
            var guard=new DrawCollapseGuard(false, GpuDecoPlayer.Product && !full ? GpuDecoPlayer.OrderViolation : (Action<string>)null);
#endif
#if AUTOTEST
            long dcV0=Stopwatch.GetTimestamp();
#endif
            RequireDrawCollapse(guard);
            var sweep=CollapseBarriersToCheck(full); // 순환 커서는 프레임당 한 번만 전진
            bool prepared=gpuCollapseSpans.Count>0 && gpuCollapseTexture!=null && gpuCollapseIncluded!=null;
            guard.Check(prepared,"drawcollapse prepare before live prepare required");
            guard.Check(gpuLiveCpuFallbackCount==0 || GpuDecoPlayer.Product && gpuLiveCpuFallbackCount==gpuCollapsePlannedFallback,"drawcollapse requires zero CPU fallback; disable before fallback");
            guard.Check(gpuLiveCamera,"drawcollapse camera lost");
            float shift=0;
            if(gpuLiveCamera)
            {
                float dot=Vector3.Dot(gpuLiveCamera.transform.forward,gpuCollapseCameraForward);
                var mode=gpuLiveCamera.transparencySortMode;
                if(mode==TransparencySortMode.Default)mode=GraphicsSettings.transparencySortMode;
                if(!(gpuLiveCamera.orthographic && (mode==TransparencySortMode.Default || mode==TransparencySortMode.Orthographic)
                    && dot>=0.999999f))guard.Check(false,"drawcollapse camera sort axis/mode changed dot="+dot.ToString("R",Inv));
                shift=Vector3.Dot(gpuLiveCamera.transform.position-gpuCollapseCameraPosition,gpuCollapseCameraForward);
            }
            if(prepared && gpuLiveSlices.Count==1 && full)
                for(int i=0;i<gpuCollapseIncluded.Length;i++)
                    guard.Check(gpuCollapseIncluded[i] || DrawCollapseTextureFallback(i) || MergeHidden(i),
                        "drawcollapse visible item outside prepared window item="+i);
            // 보조 렌더러(소유 장식마다 최대 3개, 수만 개)는 진입 때 전부, 재생 중에는 프레임당 일부씩 순환 점검한다
            // (전부 훑으면 enabled/gameObject/active 속성 읽기가 프레임당 수십만 번이라 수십 ms 가 든다).
            {
                int acount=gpuCollapseAuxiliaries.Count;
                int achecks=full?acount:Math.Min(acount,CollapseBarrierSweepPerFrame);
                for(int n=0;n<achecks;n++)
                {
                    int ai=full?n:gpuCollapseAuxCursor++%acount;
                    var r=gpuCollapseAuxiliaries[ai];
                    guard.Check(!r.R || !r.R.enabled || !r.R.gameObject.activeInHierarchy || r.R.forceRenderingOff,
                        "drawcollapse omitted auxiliary became visible");
                }
                if(!full && acount>0)gpuCollapseAuxCursor%=acount;
            }
            if(gpuLiveCamera)
            {
                // These are the small nonowned population, not all owned items.
                foreach(var b in sweep)
                {
                    if(!b.R || !b.M)continue; // 파괴된 렌더러는 더 이상 그려지지 않는다.
                    if(!DrawCollapseBarrierActive(b))continue;
                    // 장벽 키가 움직이는 것(파티클 거리, 효과가 바꾸는 순서)은 정상이다. 구간 앞뒤 불변식은 아래 대표/장벽 순서 검사가 매 프레임 확인한다.
                }
                // Owned transforms are frozen by GPU ownership. A bounded sweep
                // also catches foreign sort/material writes without a 23,547-row
                // scan in every playback frame. Full entry checks visit them all.
                if(gpuCollapseOwnedRows!=null)
                {
                    int count=gpuCollapseOwnedRows.Length;
                    // 재계획 직후(몇 초 안)에는 소유 장식 키를 방금 다시 읽었으므로 전수 점검을 건너뛴다(진입 0.9초의 후보).
                    bool fullOwned=full && Time.realtimeSinceStartupAsDouble-gpuCollapseReplanTime>=8;
                    int checks=fullOwned?count:Math.Min(count,DrawCollapsePolicy.OwnedChecksPerFrame);
                    for(int n=0;n<checks;n++)
                    {
                        int i=fullOwned?n:gpuCollapseOwnedCursor++%count;
                        CheckDrawCollapseKey(CurrentDepthRow(i),shift,guard,i);
                    }
                    if(!fullOwned && count>0)gpuCollapseOwnedCursor%=count;
                }
            }
            if(gpuLiveCamera)
            {
                foreach(var span in gpuCollapseSpans)
                    guard.Check(span.Renderer && span.Material,"drawcollapse representative lost");
                // 장벽 하나당 정수(층,순서,큐) 비교로 구간 범위를 먼저 거른다. 범위 밖(구간 앞/뒤)이면 거리(bounds)를 읽지 않는다.
                if(!DrawCollapsePolicy.SkipOrderSweep || full)
                foreach(var barrier in sweep)
                {
                    if(!DrawCollapseBarrierActive(barrier))continue;
                    var r=barrier.R;
                    int bl=SortingLayer.GetLayerValueFromID(r.sortingLayerID),bo=r.sortingOrder,bq=barrier.M.renderQueue;
                    SceneRenderOrder.Key key=default;bool haveKey=false;
                    foreach(var span in gpuCollapseSpans)
                    {
                        if(!span.Renderer || !span.Material)continue;
                        if(GpuDecoPlayer.Product && !span.Renderer.enabled)continue; // 안 그리는 구간(끔·동적 제외)은 순서가 상관없다
                        if(DcTriple(bl,bo,bq,span.First)<0 || DcTriple(bl,bo,bq,span.Last)>0)continue; // 구간 앞 또는 뒤: 안전
                        // 구간 범위 안/경계: 실제 거리까지 읽어 순서를 확인한다.
                        if(!haveKey){key=GpuDecoScene.SortKey(r,DrawCollapseMaterial(barrier),gpuLiveCamera,barrier.Key.Slot);haveKey=true;}
                        var proxy=GpuDecoScene.SortKey(span.Renderer,span.Material,gpuLiveCamera);
                        var first=CRow(span.Items[0]);
                        var last=CRow(span.Items[span.Items.Length-1]);
                        if(!first.R || !last.R)continue; // Already recorded by the owned checks.
                        var firstKey=LiveRowKey(first,CR(span.Items[0]));
                        var lastKey=LiveRowKey(last,CR(span.Items[span.Items.Length-1]));
                        var judge=key;
                        bool before=judge.CompareTo(firstKey)<0,after=judge.CompareTo(lastKey)>0;
                        if(!((before && key.CompareTo(proxy)<0) || (after && key.CompareTo(proxy)>0)))guard.Check(false,
                            "drawcollapse proxy/barrier order changed renderer="+r.GetInstanceID()
                            +" name="+r.name+" key="+DcKey(key)+" first="+DcKey(firstKey)+" last="+DcKey(lastKey)+" proxy="+DcKey(proxy)
                            +" span_items="+span.Items.Length
                            +" plan_key="+DcKey(barrier.Key)+" judge="+DcKey(judge)+" order_now="+r.sortingOrder+" layer_now="+r.sortingLayerID+" mat_now="+(r.sharedMaterial?r.sharedMaterial.name:"null")+" mat_plan="+(barrier.M?barrier.M.name:"null")
                            +" active="+DrawCollapseBarrierActive(barrier)+" watch_idx="+gpuWatchRows.IndexOf(barrier)+" in_dict="+(gpuBarrierRowById!=null && gpuBarrierRowById.TryGetValue(r.GetInstanceID(),out var dr) && ReferenceEquals(dr,barrier))+" deco_owned="+(r.GetComponentInParent<scrVisualDecoration>() is scrVisualDecoration pv && gpuLiveMasterByDeco.ContainsKey(pv))+" parent="+(r.transform.parent?r.transform.parent.name:"-"));
                    }
                }
            }
#if AUTOTEST
            if(full)GpuLog("DRAWCOLLAPSE_VALIDATE_MS full="+DcMs(dcV0).ToString("F1",Inv)+" barriers="+gpuCollapseBarriers.Count+" auxiliaries="+gpuCollapseAuxiliaries.Count);
#endif
            guard.Finish(GpuLog);
        }
        // 줄의 지금 정렬 키. 깊이 인스턴스 항목은 렌더러의 지금 깊이가 아니라 그 줄의 층·순서를 쓴다.
        static SceneRenderOrder.Key LiveRowKey(GpuLiveRow row,int item)
        {
            if(!DepthItem(item))return GpuDecoScene.SortKey(row.R,row.M,gpuLiveCamera,row.Key.Slot);
            return new SceneRenderOrder.Key(row.Key.Layer,row.Key.Order,row.M?row.M.renderQueue:-1,GpuDecoScene.Distance(row.R,gpuLiveCamera),row.M?row.M.GetInstanceID():0,row.R.GetInstanceID(),row.Key.Slot,SceneRenderOrder.ShaderRank(row.M));
        }
        static void CheckDrawCollapseKey(GpuLiveRow row,float shift,DrawCollapseGuard guard,int item)
        {
            if(row==null || !row.R || !row.M){guard.Check(false,"drawcollapse owned item="+item+" lost");return;}
            var material=DrawCollapseMaterial(row);
            var now=GpuDecoScene.SortKey(row.R,material,gpuLiveCamera,row.Key.Slot);
            // Avoid formatting thousands of success-path diagnostics at entry.
            if(!DrawCollapsePolicy.SameSortKey(row.Key,now,shift))
                guard.Check(false,"drawcollapse owned item="+item+" sort key changed name="+row.R.name
                    +" layer="+row.Key.Layer+"->"+now.Layer+" order="+row.Key.Order+"->"+now.Order
                    +" queue="+row.Key.Queue+"->"+now.Queue+" material="+row.Key.Material+"->"+now.Material
                    +" distance_delta="+(row.Key.Distance-now.Distance).ToString("R",Inv)+" camera_shift="+shift.ToString("R",Inv));
        }
        static void CheckDrawCollapseBeforeDispatch(double time)
        {
            if(!gpuCollapseOn)return;
#if AUTOTEST
            long vk0=System.Diagnostics.Stopwatch.GetTimestamp();
#endif
            if(GpuDecoPlayer.Product){DynamicOrderTick();
#if AUTOTEST
                PhV1+=System.Diagnostics.Stopwatch.GetTimestamp()-vk0;long vk1=System.Diagnostics.Stopwatch.GetTimestamp();
#endif
                ExcludedVisibilityTick();
#if AUTOTEST
                PhV2+=System.Diagnostics.Stopwatch.GetTimestamp()-vk1;
#endif
            }
            string reason=gpuCollapsePendingFallback;
            if(reason==null)
            {
                try
                {
                    RequireLive(GpuDecoPlayer.Product || time>=257.5 && time<=270.25,"outside_prepared_260..270_window");
                    // 제품: 순서 점검은 로그만 남기므로 몇 프레임에 한 번이면 된다 (매 프레임 속성 읽기 약 0.09ms).
                    if(!GpuDecoPlayer.Product || (Time.frameCount&63)==0)
                    {
#if AUTOTEST
                        long vk2=System.Diagnostics.Stopwatch.GetTimestamp();
#endif
                        ValidateDrawCollapse(false);
#if AUTOTEST
                        long vd=System.Diagnostics.Stopwatch.GetTimestamp()-vk2;PhV3+=vd;PhV3Calls++;if(vd>PhV3Max)PhV3Max=vd;
#endif
                    }
                }
                catch(Exception e){reason=e.GetType().Name+":"+e.Message;}
            }
            if(reason==null)return;
            // RefreshAlpha must run outside its own pendingAlpha enumeration.
            // The caller now executes normal Evaluate/copy in THIS dispatch with
            // freshly restored masks. No recursive dispatch or whole-GPU recovery.
            if (GpuDecoPlayer.Product) throw new InvalidOperationException(reason);
            gpuCollapseFault=reason;HideDrawCollapse();
            foreach(var slice in gpuLiveSlices)slice.RefreshAlpha();
            GpuLog("DRAWCOLLAPSE_FALLBACK reason="+reason.Replace('\r',' ').Replace('\n',' ')
                +" independent_restored=True");
        }
        static void DispatchDrawCollapse(double time,Vector4 camera,float orthoSize)
        {
#if AUTOTEST
            // The map's only GPU consumers are the collapse dispatch backends
            // below (including recorded commands); flush before every backend.
            FlushCollapseMapUploads();
#endif
#if AUTOTEST
            // Explicit paused ablation. Unlike the historical nocompute guard,
            // readiness is not inferred from one possibly culled renderer.
            if(collapseDiagFreeze && !GpuPlaybackState.Paused)collapseDiagFreeze=false;
            if(collapseDiagFreeze && collapseDiagReady)
            {if(collapseDiagCount)collapseDiagSkips++;return;}
#endif
            if(DrawCollapsePolicy.SkipComputeForTest && gpuCollapseSpans.Count>0 && gpuCollapseSpans[0].Renderer.enabled && (Time.frameCount&63)!=0)return; // 측정 전용: 계산 건너뛰기(위치 멈춤)
            if(DrawCollapsePolicy.SkipPausedCompute && GpuPlaybackState.Paused && gpuCollapseSpans.Count>0 && gpuCollapseSpans[0].Renderer.enabled)return;
            var source=gpuLiveSlices[0];
            if(DrawCollapsePolicy.GlobalVerts && DrawCollapsePolicy.DirectDispatch)
            {
                Vector4 clipD=Vector4.zero;
                if(DrawCollapsePolicy.ClipOffscreen)
                {
                    float halfH=orthoSize*1.05f+.5f,halfW=halfH*ADOBase.controller.camy.camobj.aspect;float rad=camera.z*Mathf.Deg2Rad;
                    clipD=new Vector4(Mathf.Cos(rad),Mathf.Sin(rad),halfW,halfH);
                }
                source.Gpu.DispatchCollapseAllDirect(gpuCollapseTexture,gpuCollapseAllVerts,gpuCollapseAllMap,gpuCollapseAllCount,
                    camera,time,clipD);
                if(RenderLod.Enabled || lodCompute!=null)LodAfterDispatch();
                foreach(var s in gpuCollapseSpans)if(s.Renderer.enabled==DrawCollapsePolicy.HideProxies)s.Renderer.enabled=!DrawCollapsePolicy.HideProxies;
            }
            else if(GpuDecoPlayer.Product && DrawCollapsePolicy.GlobalVerts && DrawCollapsePolicy.MinimalDispatch)
            {
                Vector4 clipM=Vector4.zero;
                if(DrawCollapsePolicy.ClipOffscreen)
                {
                    float halfH=(orthoSize*1.05f+.5f)*DrawCollapsePolicy.ClipScale,halfW=halfH*ADOBase.controller.camy.camobj.aspect;float rad=camera.z*Mathf.Deg2Rad;
                    clipM=new Vector4(Mathf.Cos(rad),Mathf.Sin(rad),halfW,halfH);
                }
#if AUTOTEST
                long ex0=Stopwatch.GetTimestamp();
#endif
                source.Gpu.DispatchCollapseMinimal(gpuCollapseTexture,gpuCollapseAllVerts,gpuCollapseAllMap,gpuCollapseAllCount,
                    camera,time,clipM);
#if AUTOTEST
                long ex1=Stopwatch.GetTimestamp();
#endif
                ApplySpanCulling();
                if(RenderLod.Enabled || lodCompute!=null)LodAfterDispatch();
                if(gpuSpanOfItem==null && (Time.frameCount&7)==0)
                foreach(var s in gpuCollapseSpans)if(s.Renderer.enabled==DrawCollapsePolicy.HideProxies)s.Renderer.enabled=!DrawCollapsePolicy.HideProxies;
#if AUTOTEST
                gpuCostExecTicks+=ex1-ex0;gpuCostProxyTicks+=Stopwatch.GetTimestamp()-ex1;
#endif
            }
            else if(DrawCollapsePolicy.RecordedDispatch)
            {
                if(!source.Gpu.HasRecordedCollapse)
                {
                    if(DrawCollapsePolicy.GlobalVerts)source.Gpu.RecordCollapseAll(gpuCollapseTexture,gpuCollapseAllVerts,gpuCollapseAllMap,gpuCollapseAllCount);
                    else
                    {
                        var tg=new List<GraphicsBuffer>();var mp=new List<ComputeBuffer>();var ct=new List<int>();
                        foreach(var s in gpuCollapseSpans){tg.Add(s.Vertices);mp.Add(s.Map);ct.Add(s.Items.Length);}
                        source.Gpu.RecordCollapse(gpuCollapseTexture,tg,mp,ct);
                    }
                }
                Vector4 clip=Vector4.zero;
                if(DrawCollapsePolicy.ClipOffscreen)
                {
                    float halfH=orthoSize*1.05f+.5f,halfW=halfH*ADOBase.controller.camy.camobj.aspect;float rad=camera.z*Mathf.Deg2Rad;
                    clip=new Vector4(Mathf.Cos(rad),Mathf.Sin(rad),halfW,halfH);
                }
#if AUTOTEST
                long ex0=Stopwatch.GetTimestamp();
#endif
                source.Gpu.ExecuteRecordedCollapse(camera,time,clip);
#if AUTOTEST
                long ex1=Stopwatch.GetTimestamp();
#endif
                ApplySpanCulling();
                if(gpuSpanOfItem==null && (!GpuDecoPlayer.Product || (Time.frameCount&7)==0))
                foreach(var s in gpuCollapseSpans)if(s.Renderer.enabled==DrawCollapsePolicy.HideProxies)s.Renderer.enabled=!DrawCollapsePolicy.HideProxies;
#if AUTOTEST
                gpuCostExecTicks+=ex1-ex0;gpuCostProxyTicks+=Stopwatch.GetTimestamp()-ex1;
#endif
            }
            else
            {
            if(DrawCollapsePolicy.GlobalVerts)
            {
                source.Gpu.DispatchCollapseAll(gpuCollapseTexture,gpuCollapseAllVerts,gpuCollapseAllMap,gpuCollapseAllCount);
                foreach(var s in gpuCollapseSpans)if(s.Renderer.enabled==DrawCollapsePolicy.HideProxies)s.Renderer.enabled=!DrawCollapsePolicy.HideProxies;
            }
            else
            {
            source.Gpu.BeginCollapse(gpuCollapseTexture);
            foreach(var s in gpuCollapseSpans)
            {source.Gpu.DispatchCollapseSpan(s.Vertices,s.Map,s.Items.Length);if(s.Renderer.enabled==DrawCollapsePolicy.HideProxies)s.Renderer.enabled=!DrawCollapsePolicy.HideProxies;}
            }
            }
            if(gpuCollapseTextureFallbackCount>0 && ((source.CopyMask?.Low??ulong.MaxValue)|(source.CopyMask?.High??ulong.MaxValue))!=0)
            {
                // Overflow alone retains the original evaluated/copy path. Its
                // CopyMask retains chunks containing visible fallback items;
                // neighboring admitted buffers may be copied but stay hidden.
                // Spans still evaluate directly into their representative VBs.
#if AUTOTEST
                long fe0=Stopwatch.GetTimestamp();
#endif
                // Restore evaluator modes (including AlphaEarlyOut) after any collapse backend.
                source.Gpu.SetLiveUniforms(camera,time);
                source.Gpu.DispatchEvaluate();
#if AUTOTEST
                long fe1=Stopwatch.GetTimestamp();
#endif
                ulong low=source.CopyMask?.Low??ulong.MaxValue,high=source.CopyMask?.High??ulong.MaxValue;
                if(source.NativeCopy==null || !source.NativeCopy.Execute(low,high))Graphics.ExecuteCommandBuffer(source.Commands);
#if AUTOTEST
                gpuCostFbEvalTicks+=fe1-fe0;gpuCostFbCopyTicks+=Stopwatch.GetTimestamp()-fe1;
#endif
            }
#if AUTOTEST
            collapseDiagReady=true;
            if(collapseDiagCount)collapseDiagDispatches++;
#endif
        }
    }
}
#endif
