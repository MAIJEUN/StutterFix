#include <windows.h>
#include <dxgi.h>
#include <d3d11.h>
#include <array>
#include <atomic>
#include <cassert>
#include <cstdio>
#include "../../native/sfnative/fill_pipeline.h"
struct FakePipe {
    struct Query {bool ended=false;};
    bool blocked=false,fail=false,resetDone=false;int creates=0,failCreate=0,begins=0,ends=0;
    bool initialize(IDXGISwapChain*){return true;}
    bool create(Query&,D3D11_QUERY t){assert(t==D3D11_QUERY_PIPELINE_STATISTICS);return ++creates!=failCreate;}
    void begin(Query& q){assert(!q.ended);++begins;}
    void end(Query& q){q.ended=true;++ends;}
    HRESULT get(Query& q,void* data,UINT size){
        assert(q.ended && size==sizeof(D3D11_QUERY_DATA_PIPELINE_STATISTICS));
        if(blocked)return S_FALSE;
        q.ended=false;if(fail)return E_FAIL;
        auto d=static_cast<D3D11_QUERY_DATA_PIPELINE_STATISTICS*>(data);d->PSInvocations=1000;d->CPrimitives=17;return S_OK;
    }
    void reset(){resetDone=true;}
};
int main(){
    sf_ppgpu::Pipeline<FakePipe> p;assert(p.initialize(nullptr)&&p.api.creates==16);
    p.begin();p.end();p.beforePresent();assert(p.samples==1&&p.ps==1000&&p.primitives==17);
    p.api.blocked=true;for(int i=0;i<16;i++){p.begin();p.end();p.beforePresent();}
    auto begins=p.api.begins;p.begin();p.end();assert(p.drops==1&&p.api.begins==begins&&p.queued==16);
    p.api.blocked=false;p.collect();assert(p.queued==0&&p.samples==17);
    p.begin();p.beforePresent();assert(p.missing==1&&p.samples==17); // invalid scope excluded
    p.begin();p.begin();p.end();p.beforePresent();assert(p.repeated==1); // never nested D3D Begin
    p.api.fail=true;p.begin();p.end();p.beforePresent();assert(p.failures==1);p.api.fail=false;
    p.begin();p.stop();assert(p.api.resetDone&&!p.ready&&!p.open&&p.queued==0);
    std::array<std::atomic<long long>,110> stats{};p.publish(stats);assert(stats[100]==0&&stats[104]==1);
    sf_ppgpu::Pipeline<FakePipe> fail;fail.api.failCreate=4;
    assert(!fail.initialize(nullptr)&&fail.failures==1&&fail.api.resetDone);
    sf_ppgpu::Pipeline<FakePipe> bounded;bounded.limit=2;assert(bounded.initialize(nullptr));
    for(int i=0;i<10;i++){bounded.begin();bounded.end();bounded.beforePresent();}
    assert(bounded.submitted==2 && bounded.samples==2 && bounded.api.begins==2);
    bounded.stop();bounded=sf_ppgpu::Pipeline<FakePipe>{};assert(bounded.initialize(nullptr));
    assert(bounded.samples==0 && bounded.submitted==0 && bounded.limit==30);bounded.stop();
    sf_ppgpu::Pipeline<FakePipe> repeat;assert(repeat.initialize(nullptr));
    repeat.begin();repeat.end();repeat.begin();repeat.end();assert(repeat.samples==1&&repeat.repeated==1);repeat.stop();
    puts("PASS actual Pipeline: PS/CPrimitive sums, bounded 16-query ring, saturation skips, asynchronous retirement, missing-end exclusion, no nested Begin, failure retirement, active/partial-create cleanup, append-only stats");
}
