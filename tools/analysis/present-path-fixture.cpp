// CPU-only COM test double. No D3D device, window, game, GPU or installed DLL.
#include <windows.h>
#include <dxgi1_6.h>
#include <wrl/client.h>
#include <array>
#include <atomic>
#include <algorithm>
#include <cassert>
#include <cstdio>
using Microsoft::WRL::ComPtr;
#define API extern "C"
#define SF_PRESENTPATH_FIXTURE
namespace sf_presentpath {static void fixtureTrigger();static bool fixtureArmAfter();}
#include "../../native/sfnative/presentpath.h"
struct Fake {
    void** vt; ULONG refs=1; UINT latency=1, sync=99, flags=99;
    bool waitable=false, full=false, rejectSet=false, rejectResize=false, rejectThree=false;
    UINT resizeAttempts=0;
    UINT resizeCount=0, resizeW=0, resizeH=0, resizeFlags=0;
    DXGI_FORMAT resizeFormat=DXGI_FORMAT_UNKNOWN;
    DXGI_SWAP_CHAIN_DESC desc{};
};
static std::array<void*,41> swapTable{}, otherTable{};
static std::array<void*,14> deviceTable{};
static Fake dev{deviceTable.data()}, sc{swapTable.data()};
static ULONG STDMETHODCALLTYPE add(Fake* p) {return ++p->refs;}
static ULONG STDMETHODCALLTYPE release(Fake* p) {return --p->refs;}
static HRESULT STDMETHODCALLTYPE query(Fake* p,REFIID iid,void** out) {
    *out=nullptr;
    if(iid==__uuidof(IUnknown) || (p==&dev && iid==__uuidof(IDXGIDevice1)) ||
       (p==&sc && (iid==__uuidof(IDXGISwapChain1) || iid==__uuidof(IDXGISwapChain2))))
        {*out=p;add(p);return S_OK;}
    return E_NOINTERFACE;
}
static HRESULT STDMETHODCALLTYPE getDevice(Fake*,REFIID iid,void** out) {return query(&dev,iid,out);}
static HRESULT STDMETHODCALLTYPE desc(Fake* p,DXGI_SWAP_CHAIN_DESC* d) {*d=p->desc;return S_OK;}
static HRESULT STDMETHODCALLTYPE desc1(Fake* p,DXGI_SWAP_CHAIN_DESC1* d) {
    *d={};d->Width=1280;d->Height=720;d->BufferCount=p->desc.BufferCount;
    d->SwapEffect=p->desc.SwapEffect;d->Flags=p->desc.Flags;return S_OK;
}
static HRESULT STDMETHODCALLTYPE fullscreen(Fake* p,BOOL* full,IDXGIOutput**) {*full=p->full;return S_OK;}
static HRESULT STDMETHODCALLTYPE output(Fake* p,UINT sync,UINT flags) {p->sync=sync;p->flags=flags;return S_OK;}
static HRESULT STDMETHODCALLTYPE getLatency(Fake* p,UINT* v) {*v=p->latency;return S_OK;}
static HRESULT STDMETHODCALLTYPE setLatency(Fake* p,UINT v) {if(p->rejectSet)return E_FAIL;p->latency=v;return S_OK;}
static HRESULT STDMETHODCALLTYPE resizeOutput(Fake* p,UINT count,UINT w,UINT h,DXGI_FORMAT format,UINT flags) {
    ++p->resizeAttempts;
    p->resizeCount=count;p->resizeW=w;p->resizeH=h;p->resizeFormat=format;p->resizeFlags=flags;
    if(p->rejectResize || (p->rejectThree && count==3))return DXGI_ERROR_INVALID_CALL;
    if(count)p->desc.BufferCount=count;
    p->desc.Flags=flags;return S_OK;
}
static HRESULT resize(UINT count=2,UINT flags=0x842) {
    auto fn=reinterpret_cast<sf_presentpath::ResizeFn>(sc.vt[13]);
    return fn(reinterpret_cast<IDXGISwapChain*>(&sc),count,1281,721,DXGI_FORMAT_R8G8B8A8_UNORM,flags);
}
static std::array<unsigned char,0x398> engineSettings{};
static bool rejectAdjust=false;
static void verifyAfterBoundary();
static bool fakeAdjust(void* settings,const void* rect,int w,int h,int bw,int bh,unsigned long long refresh,bool full,int vsync) {
    verifyAfterBoundary();
    assert(settings==engineSettings.data() && reinterpret_cast<uintptr_t>(rect)%16==0);
    assert(w==1280 && h==720 && bw==w && bh==h && refresh==(1ull<<32) && !full && vsync==17);
    assert(std::equal(static_cast<const unsigned char*>(rect),static_cast<const unsigned char*>(rect)+16,engineSettings.data()+0x388));
    if(rejectAdjust)return false;
    ::resize();return true;
}
static std::array<void*,381> gfxTable{};
struct FakeGfx {void** vt=gfxTable.data()+1;long long a=1,b=0;} gfx;
static bool skipAfter=false;
static void verifyAfterBoundary() {assert(gfx.vt==gfxTable.data()+1 && gfx.a==gfx.b);}
static void fakeAfter(void* self) {assert(self==&gfx);if(!skipAfter)++gfx.b;}
namespace sf_presentpath {
static bool fixtureArmAfter() {return armAfter(&gfx,gfxTable.data()+1);}
static void fixtureTrigger() {
    stats[33]=gfx.a;stats[34]=gfx.b;
    if(gfx.a==gfx.b)triggerAdjust(engineSettings.data(),fakeAdjust);
}}
static void after() {
    reinterpret_cast<sf_presentpath::AfterFn>(gfx.vt[0xbd8/8])(&gfx);
}
static void trigger() {
    gfx.a=gfx.b+1;sf_presentpath::event(8,nullptr);
    assert(sf_presentpath_stat(32)==1); // no Adjust in the rendering event
    after();assert(gfx.vt==gfxTable.data()+1);
}
static void enable(int id) {sf_presentpath_pin(&sc);sf_presentpath_pending();sf_presentpath::event(id,&sc);}
static void disable() {sf_presentpath_disable();sf_presentpath_pending();sf_presentpath::event(0,nullptr);}
static void present(UINT sync,UINT flags) {
    auto fn=reinterpret_cast<sf_presentpath::Fn>(sc.vt[8]);
    assert(fn(reinterpret_cast<IDXGISwapChain*>(&sc),sync,flags)==S_OK);
}
int main() {
    gfxTable[0]=reinterpret_cast<void*>(0x1234); // RTTI prefix preserved
    gfxTable[1+0xbd8/8]=reinterpret_cast<void*>(fakeAfter);
    swapTable[0]=deviceTable[0]=reinterpret_cast<void*>(query);
    swapTable[1]=deviceTable[1]=reinterpret_cast<void*>(add);
    swapTable[2]=deviceTable[2]=reinterpret_cast<void*>(release);
    swapTable[7]=reinterpret_cast<void*>(getDevice);swapTable[8]=reinterpret_cast<void*>(output);
    swapTable[11]=reinterpret_cast<void*>(fullscreen);swapTable[12]=reinterpret_cast<void*>(desc);
    swapTable[13]=reinterpret_cast<void*>(resizeOutput);
    swapTable[18]=reinterpret_cast<void*>(desc1);
    swapTable[31]=deviceTable[12]=reinterpret_cast<void*>(setLatency);
    swapTable[32]=deviceTable[13]=reinterpret_cast<void*>(getLatency);
    sc.desc.BufferCount=2;sc.desc.SwapEffect=DXGI_SWAP_EFFECT_FLIP_DISCARD;sc.desc.Windowed=TRUE;
    *reinterpret_cast<Fake**>(engineSettings.data()+0x320)=&sc;
    *reinterpret_cast<int*>(engineSettings.data()+0x338)=17;
    for(unsigned i=0;i<16;++i)engineSettings[0x388+i]=static_cast<unsigned char>(i);
    const auto original=swapTable;
    sf_presentpath_pin(&sc);sf_presentpath::event(5,&sc);
    assert(sf_presentpath_stat(-1)==0 && sf_presentpath_stat(2)==2 && sc.refs==1 && dev.refs==1 && sc.vt==swapTable.data());
    enable(1);assert(sf_presentpath_stat(-1)==1);
    present(1,0x40);assert(sc.sync==1 && sc.flags==0x40);
    assert(sf_presentpath_stat(7)==1 && sf_presentpath_stat(14)==0);
    disable();assert(sf_presentpath_stat(-1)==0 && sc.vt==swapTable.data() && sc.refs==1 && dev.refs==1);
    enable(2);assert(sf_presentpath_stat(-1)==-1 && sc.vt==swapTable.data() && sc.refs==1);disable();
    sc.desc.Flags=DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING;
    enable(2);present(0,0);assert(sc.flags==DXGI_PRESENT_ALLOW_TEARING && sf_presentpath_stat(14)==1);
    present(1,0);assert(sc.flags==0); // sync interval must be zero
    present(0,DXGI_PRESENT_TEST);assert(sc.flags==DXGI_PRESENT_TEST);
    present(0,DXGI_PRESENT_DO_NOT_WAIT);assert(sc.flags==DXGI_PRESENT_DO_NOT_WAIT);
    sc.full=true;present(0,0);assert(sc.flags==0);sc.full=false;
    present(0,DXGI_PRESENT_ALLOW_TEARING);assert(sf_presentpath_stat(14)==1); // engine already requested it
    sf_presentpath_disable();present(0,0);assert(sc.flags==0);disable();
    enable(3);assert(dev.latency==4 && sf_presentpath_stat(16)==1);disable();assert(dev.latency==1);
    dev.latency=4;enable(3);assert(sf_presentpath_stat(-1)==-1);disable();dev.latency=1;
    dev.rejectSet=true;enable(3);assert(sf_presentpath_stat(-1)==-1);disable();dev.rejectSet=false;
    sc.desc.Flags|=DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
    enable(3);assert(sc.latency==4 && dev.latency==1 && sf_presentpath_stat(16)==2);
    disable();assert(sc.latency==1 && sc.refs==1 && dev.refs==1);
    sc.desc.Flags=0x842;
    enable(6);assert(sf_presentpath_stat(-1)==1 && sf_presentpath_stat(26)==2 && sc.desc.BufferCount==2);
    assert(resize(0)==S_OK); // DXGI preserve-current input must still become 3
    assert(sc.resizeCount==3 && sc.desc.BufferCount==3 && sf_presentpath_stat(2)==3);
    assert(sc.resizeW==1281 && sc.resizeH==721 && sc.resizeFormat==DXGI_FORMAT_R8G8B8A8_UNORM && sc.resizeFlags==0x842);
    assert(sf_presentpath_stat(20)==1 && sf_presentpath_stat(21)==S_OK && sf_presentpath_stat(27)==1);
    present(0,512);assert(sf_presentpath_stat(15)==0);
    disable();assert(sf_presentpath_stat(-1)==2 && sc.vt==sf_presentpath::table.data() && sc.refs>1);
    assert(resize()==S_OK && sc.resizeCount==2 && sc.desc.BufferCount==2);
    sf_presentpath::event(0,nullptr);
    assert(sf_presentpath_stat(-1)==0 && sf_presentpath_stat(2)==2 && sc.refs==1 && dev.refs==1 && sc.vt==swapTable.data());
    enable(6);sc.rejectResize=true;
    assert(resize()==DXGI_ERROR_INVALID_CALL && sf_presentpath_stat(25)==1 && sf_presentpath_stat(2)==2);
    sc.rejectResize=false;disable();assert(sf_presentpath_stat(-1)==0 && sc.refs==1);
    enable(6);sc.rejectThree=true;
    assert(resize(0)==S_OK && sc.desc.BufferCount==2 && sc.resizeCount==0);
    assert(sf_presentpath_stat(21)==DXGI_ERROR_INVALID_CALL && sf_presentpath_stat(28)==S_OK && sf_presentpath_stat(29)==1 && sf_presentpath_stat(25)==1 && sf_presentpath_stat(27)==0);
    sc.rejectThree=false;disable();assert(sf_presentpath_stat(-1)==0);
    enable(7);trigger();present(0,0);
    assert(sf_presentpath_stat(2)==2 && sf_presentpath_stat(27)==0 && sf_presentpath_stat(20)==1 && sf_presentpath_stat(30)==1 && sf_presentpath_stat(31)==1 && sf_presentpath_stat(32)==2);
    disable();assert(sc.refs==1 && sc.vt==swapTable.data());
    enable(7);rejectAdjust=true;trigger();
    assert(sf_presentpath_stat(31)==0 && sf_presentpath_stat(20)==0 && sf_presentpath_stat(32)==2);
    rejectAdjust=false;disable();
    enable(7);*reinterpret_cast<Fake**>(engineSettings.data()+0x320)=nullptr;trigger();
    assert(sf_presentpath_stat(31)==-1 && sf_presentpath_stat(20)==0);
    *reinterpret_cast<Fake**>(engineSettings.data()+0x320)=&sc;disable();
    enable(7);skipAfter=true;trigger();skipAfter=false;
    assert(sf_presentpath_stat(33)!=sf_presentpath_stat(34) && sf_presentpath_stat(20)==0 && sf_presentpath_stat(31)==-1);disable();
    enable(7);gfx.a=gfx.b+1;sf_presentpath::event(8,nullptr);
    assert(gfx.vt[-1]==gfxTable[0]);
    sf_presentpath_disable();after();assert(sf_presentpath_stat(20)==0 && gfx.vt==gfxTable.data()+1);disable();
    enable(7);sf_presentpath::event(8,nullptr);disable();
    assert(gfx.vt==gfxTable.data()+1 && sf_presentpath_stat(20)==0);
    enable(7);gfx.a=gfx.b+1;sf_presentpath::event(8,nullptr);
    for(size_t i=0;i<380;++i)if(i!=0xbd8/8)assert(gfx.vt[i]==gfxTable[i+1]);
    std::array<void*,381> gfxOverlay=sf_presentpath::afterTable;
    gfx.vt=gfxOverlay.data()+1;after(); // foreign owner retains a forwarding callback
    assert(sf_presentpath_stat(-1)==-3 && sf_presentpath_stat(20)==0 && gfx.vt==gfxOverlay.data()+1);
    assert(!sf_presentpath::armAfter(&gfx,gfxTable.data()+1));
    gfx.vt=sf_presentpath::afterTable.data()+1;disable(); // fixture-only owner recovery
    assert(gfx.vt==gfxTable.data()+1);
    enable(6);trigger();assert(sc.desc.BufferCount==3);
    disable();assert(sf_presentpath_stat(-1)==2);trigger();sf_presentpath::event(0,nullptr);
    assert(sc.desc.BufferCount==2 && sf_presentpath_stat(-1)==0 && gfx.vt==gfxTable.data()+1);
    enable(6);assert(resize(2,0x42)==S_OK); // flags forwarded, tearing mismatch refuses count rewrite
    assert(sc.resizeCount==2 && sc.resizeFlags==0x42 && sf_presentpath_stat(25)==1);
    disable();sc.desc.Flags=0x842;
    sc.desc.BufferCount=3;enable(6);assert(sf_presentpath_stat(-1)==-1 && sc.refs==1);disable();sc.desc.BufferCount=2;
    sc.full=true;enable(6);assert(sf_presentpath_stat(-1)==-1 && sc.refs==1);disable();sc.full=false;
    // Restore failure retains hook/references and can be retried; never detach at count 3.
    enable(6);assert(resize()==S_OK);sc.rejectResize=true;disable();
    assert(resize()==DXGI_ERROR_INVALID_CALL);sf_presentpath::event(0,nullptr);
    assert(sf_presentpath_stat(-1)==2 && sc.refs>1 && sc.desc.BufferCount==3);
    sc.rejectResize=false;assert(resize()==S_OK);sf_presentpath::event(0,nullptr);
    assert(sf_presentpath_stat(-1)==0 && sc.refs==1 && dev.refs==1);
    // Our fake chain has no D3D11 device: gputime must fail closed without a vptr lease.
    enable(9);assert(sf_presentpath_stat(-1)==-1 && sf_presentpath_stat(53)==1 && sc.refs==1 && dev.refs==1 && sc.vt==swapTable.data());
    disable();assert(sf_presentpath_stat(-1)==0 && sf_presentpath_stat(60)==0);
    assert(swapTable==original && gfxTable[0]==reinterpret_cast<void*>(0x1234) &&
        gfxTable[1+0xbd8/8]==reinterpret_cast<void*>(fakeAfter)); // shared tables unchanged
    puts("PASS OnAfterPresent: deferred one-shot; original callback before Adjust; equal counter guard; RTTI/full table copy; detach before Adjust; cancellation; early off; count restore; overlay refusal/recovery; shared table unchanged");
    enable(6);assert(resize()==S_OK);otherTable=sf_presentpath::table;sc.vt=otherTable.data();disable();
    assert(sf_presentpath_stat(-1)==-3 && sc.vt==otherTable.data());
    present(1,0x40);assert(sc.flags==0x40); // overlay retains safe pass-through
    assert(sf_presentpath_stat(-2)==0);
    assert(resize()==S_OK && sc.resizeCount==2); // retained overlay chain restores count through our callback
    puts("PASS observe; tearing guards/no-op; device/waitable latency; buffers3 rewrite/preserve-input; untouched dimensions/format/flags; original HRESULT/fallback success and failure; resizeprobe unchanged count; engine Adjust ABI/viewport alignment/identity/refusal; no-op/fullscreen refusal; deferred off/restore retry; refcounts; shared-vtable parity; overlay takeover pass-through");
}
