// Actual Tracker code with a deterministic non-GPU backend. Never runs a game.
#include <windows.h>
#include <dxgi.h>
#include <wrl/client.h>
#include <array>
#include <atomic>
#include <algorithm>
#include <cassert>
#include <cstdio>
#include "../../native/sfnative/present_gputime.h"
struct Fake {
    struct Query {D3D11_QUERY type=D3D11_QUERY_EVENT;UINT64 value=0;};
    int creates=0,failCreate=0,begins=0,ends=0,gets=0;
    bool blocked=false,disjoint=false,eventPending=false,failGet=false,resetDone=false;
    UINT64 tick=0,frequency=1000000;
    bool initialize(IDXGISwapChain*) {return true;}
    bool create(Query& q,D3D11_QUERY type) {q.type=type;return ++creates!=failCreate;}
    void begin(Query&) {++begins;}
    void end(Query& q) {++ends;if(q.type==D3D11_QUERY_TIMESTAMP)q.value=(tick+=1000);}
    HRESULT get(Query& q,void* data,UINT) {
        ++gets;
        if(failGet)return E_FAIL;
        if(q.type==D3D11_QUERY_EVENT) {*static_cast<BOOL*>(data)=!eventPending;return eventPending?S_FALSE:S_OK;}
        if(blocked)return S_FALSE;
        if(q.type==D3D11_QUERY_TIMESTAMP_DISJOINT)
            *static_cast<D3D11_QUERY_DATA_TIMESTAMP_DISJOINT*>(data)={frequency,disjoint};
        else *static_cast<UINT64*>(data)=q.value;
        return S_OK;
    }
    void reset() {resetDone=true;}
};
using Tracker=sf_ppgpu::Tracker<Fake>;
static void frame(Tracker& t) {t.beginFrame();t.begin();auto sampled=t.beforePresent();t.afterPresent(sampled,2800);}
int main() {
    static Tracker t;
    assert(t.initialize(nullptr) && t.api.creates==176);
    t.begin();assert(t.api.begins==0); // never silently label a screen fallback as whole-camera scope
    t.beginFrame();t.begin();t.begin();assert(t.api.begins==1);
    t.afterPresent(t.beforePresent(),2800);
    frame(t);
    assert(t.render.count==2 && t.render.mean()==1000 && t.after.mean()==1000);
    assert(t.interval.count==1 && t.interval.mean()==4000 && t.checked==2 && t.unobserved==0);
    assert(t.frame.count==2 && t.frame.mean()==2000);
    t.api.eventPending=true;frame(t);frame(t);assert(t.unobserved==2 && t.checked==4);
    t.api.disjoint=true;frame(t);frame(t);assert(t.disjoints==1 && t.render.count==4);
    t.api.disjoint=false;frame(t);frame(t);assert(t.interval.count==3); // cannot bridge invalid epoch
    frame(t);
    t.afterPresent(t.beforePresent(),2800);assert(t.missing==1);
    frame(t);assert(t.interval.count==3); // cannot bridge a missing camera event
    t.api.blocked=true;
    for(int i=0;i<32;++i)frame(t);
    assert(t.queued==16);
    const auto ends=t.api.ends;
    for(int i=0;i<8;++i)frame(t);
    assert(t.drops==8 && t.queued==16 && t.api.ends==ends); // no reuse or wait
    t.api.blocked=false;t.collect();assert(t.queued==0);
    t.api.failGet=true;frame(t);frame(t);assert(t.failures>0);
    t.api.failGet=false;t.beginFrame();t.begin();t.stop();
    assert(!t.ready && t.active==-1 && t.queued==0 && t.api.resetDone && t.discarded==1);
    const auto oldBegins=t.api.begins;t.begin();assert(t.api.begins==oldBegins);
    static std::array<std::atomic<long long>,100> stats{};t.publish(stats);
    assert(stats[53]>0 && stats[57]==1 && stats[60]==0);
    static Tracker failed;failed.api.failCreate=7;
    assert(!failed.initialize(nullptr) && failed.failures==1 && failed.api.resetDone && !failed.ready);
    static Tracker cameras;assert(cameras.initialize(nullptr,true) && cameras.api.creates==1200);
    for(int i=0;i<2;++i) {
        cameras.beginFrame();cameras.beginFrame(); // offscreen starts frame first
        cameras.camera(0,false);cameras.camera(0,true);
        cameras.begin(); // legacy screen start later
        cameras.camera(1,false);cameras.camera(1,true);
        cameras.camera(0,false);cameras.camera(0,true); // no query overwrite
        cameras.afterPresent(cameras.beforePresent(),2800);
    }
    assert(cameras.failures==0 && cameras.frame.mean()==6000 && cameras.render.mean()==3000);
    assert(cameras.cameraSum[0]==2000 && cameras.cameraSamples[0]==2 && cameras.cameraSkipped==4);
    cameras.publish(stats);assert(stats[61]==2 && stats[62]==6000 && stats[69]==1000 && stats[71]==1000);
    // A frame without a screen camera remains a valid whole-camera sample.
    for(int i=0;i<2;++i) {cameras.beginFrame();cameras.afterPresent(cameras.beforePresent(),100);}
    assert(cameras.frame.count==4 && cameras.render.count==2 && cameras.failures==0);
    cameras.publish(stats);assert(stats[69]==500); // mean per frame, not per appearance
    for(int i=0;i<2;++i) {
        cameras.beginFrame();cameras.camera(8,false);cameras.camera(8,true);
        cameras.afterPresent(cameras.beforePresent(),100);
    }
    cameras.publish(stats);assert(stats[84]==2 && stats[85]==333 && cameras.failures==0);
    const auto recovered=cameras.frame.count,filterSamples=cameras.cameraSamples[8];
    cameras.api.disjoint=true;
    for(int i=0;i<2;++i) {
        cameras.beginFrame();cameras.camera(8,false);cameras.camera(8,true);
        cameras.afterPresent(cameras.beforePresent(),100);
    }
    assert(cameras.frame.count==recovered && cameras.cameraSamples[8]==filterSamples && cameras.disjoints==1);
    cameras.stop();
    sf_ppgpu::Distribution d;d.add(1);d.add(9);d.add(70000);
    assert(d.percentile(50)==16 && d.percentile(95)==70000);
    std::puts("PASS: 16 pair slots / 32 frames saturation, same-disjoint b->b, no overwrite/wait, ordered retirement, offscreen frame vs screen scopes, optional camera/filter pairs and per-frame denominator, repeated marker dedup, missing/disjoint exclusion including filters, EVENT pending, query failure, partial-create cleanup, stop-active cleanup, histogram overflow, stats");
}
