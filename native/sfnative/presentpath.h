// Dormant diagnostic exports. Only the AUTOTEST managed command calls these.
// All COM operations and vptr changes run on Unity's render thread.
#include "present_gputime.h"
#include "fill_pipeline.h"
namespace sf_presentpath {
using Fn = HRESULT (STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using ResizeFn = HRESULT (STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);
static ComPtr<IDXGISwapChain> chain;
static ComPtr<IDXGISwapChain2> chain2;
static ComPtr<IDXGIDevice1> device1;
static void** before = nullptr;
static std::array<void*,41> table{};
static Fn next = nullptr;
static ResizeFn nextResize = nullptr;
static UINT savedBuffers = 0, savedFlags = 0;
static std::atomic<UINT> bufferTarget{0};
static bool bufferLease = false;
static UINT savedLatency = 0;
static bool changedLatency = false;
static std::atomic<int> mode{0}, status{0};
// status: 0 off, 1 installed, -1 rejected, -2 restore failure, -3 overlay owns vptr.
// Append-only indices: 20 resize calls, 21 HRESULT, 22 input count, 23 output count,
// 24 flags, 25 resize failures/guard refusals, 26 saved count, 27 successful rewrites.
// 28 fallback HRESULT, 29 fallback calls, 30 engine trigger calls,
// 31 Adjust result, 32 trigger phase (1 entered, 2 returned).
// 33/34 Adjust guard A/B; 35 OnAfterPresent callbacks.
// 36..60 legacy GPU; 61..67 frame; 68..99 camera and image-effect pairs.
// Append-only, published by the render-thread state ACK.
static std::array<std::atomic<long long>,112> stats{};
static sf_ppgpu::Tracker<sf_ppgpu::D3D> gpu;
static sf_ppgpu::Pipeline<sf_ppgpu::D3D> pipeline;
using AdjustFn=bool (*)(void*,const void*,int,int,int,int,unsigned long long,bool,int);
using AfterFn=void (*)(void*);
static void* afterDevice=nullptr;
static void** afterBefore=nullptr;
// Exact pinned GfxDeviceD3D11 primary vtable: 0xBE0 bytes; include RTTI[-1].
static std::array<void*,381> afterTable{};
static AfterFn afterNext=nullptr;
static std::atomic<bool> triggerArmed{false};
static void runTrigger(void* expectedDevice);
static bool detachAfter() {
    triggerArmed=false;
    if(!afterDevice)return true;
    auto cell=reinterpret_cast<void**>(afterDevice);
    InterlockedCompareExchangePointer(cell,afterBefore,afterTable.data()+1);
    if(*cell!=afterBefore)return false; // retain storage/next for an overlay chain
    afterDevice=nullptr;return true;
}
static void afterPresent(void* self) {
    afterNext(self); // Unity increments B and performs its event-query bookkeeping first.
    ++stats[35];
    const bool armed=triggerArmed.exchange(false);
    if(!detachAfter()) {stats[31]=-1;stats[32]=2;status=-3;return;}
    if(armed)runTrigger(self);
    else {stats[31]=-1;stats[32]=2;}
}
static bool armAfter(void* device,void** expected) {
    if(!device || afterDevice || *reinterpret_cast<void***>(device)!=expected)return false;
    std::copy_n(expected-1,afterTable.size(),afterTable.begin());
    afterNext=reinterpret_cast<AfterFn>(expected[0xbd8/8]);
    afterTable[1+0xbd8/8]=reinterpret_cast<void*>(afterPresent);
    afterBefore=expected;afterDevice=device;triggerArmed=true;
    if(InterlockedCompareExchangePointer(reinterpret_cast<void**>(device),afterTable.data()+1,expected)!=expected) {
        triggerArmed=false;afterDevice=nullptr;return false;
    }
    return true;
}
static void triggerAdjust(unsigned char* settings,AdjustFn adjust) {
    if(!chain || !settings || !adjust ||
       *reinterpret_cast<IDXGISwapChain**>(settings+0x320)!=chain.Get() ||
       *reinterpret_cast<void***>(chain.Get())!=table.data()) return;
    alignas(16) std::array<unsigned char,16> viewport{};
    std::copy_n(settings+0x388,16,viewport.begin());
    int w=static_cast<int>(stats[0].load()),h=static_cast<int>(stats[1].load());
    if(w<2 || h<2)return;
    // Non-exclusive only; refresh 0/1 matches HandleWindowResize.
    stats[31]=adjust(settings,viewport.data(),w,h,w,h,1ull<<32,false,
        *reinterpret_cast<int*>(settings+0x338))?1:0;
}
static void describe() {
    gpu.publish(stats);pipeline.publish(stats);
    if(!chain) return;
    DXGI_SWAP_CHAIN_DESC d{};
    HRESULT descHR=chain->GetDesc(&d);
    if(FAILED(descHR)) {stats[17]=descHR;stats[19]=0;++stats[18];return;}
    stats[0]=d.BufferDesc.Width;stats[1]=d.BufferDesc.Height;
    stats[2]=d.BufferCount;stats[3]=d.SwapEffect;stats[4]=d.Flags;stats[5]=d.Windowed;
    ComPtr<IDXGISwapChain1> one;
    DXGI_SWAP_CHAIN_DESC1 d1{};
    HRESULT hr=chain.As(&one);
    if(SUCCEEDED(hr)) hr=one->GetDesc1(&d1);
    stats[17]=hr;
    if(SUCCEEDED(hr)) {stats[0]=d1.Width;stats[1]=d1.Height;stats[2]=d1.BufferCount;stats[3]=d1.SwapEffect;stats[4]=d1.Flags;}
    UINT v=0; hr=chain2 ? chain2->GetMaximumFrameLatency(&v) : device1 ? device1->GetMaximumFrameLatency(&v) : E_NOINTERFACE;
    stats[6]=SUCCEEDED(hr)?v:-1;stats[16]=chain2?2:device1?1:0;
    stats[19]=*reinterpret_cast<void***>(chain.Get())==table.data()?1:0;
    ++stats[18]; // release/seq_cst ACK for managed assertion refresh
}
static HRESULT STDMETHODCALLTYPE present(IDXGISwapChain* self,UINT sync,UINT flags) {
    UINT output=flags;
    if(mode==2 && sync==0 && !(flags & (DXGI_PRESENT_TEST|DXGI_PRESENT_DO_NOT_WAIT))) {
        DXGI_SWAP_CHAIN_DESC d{}; BOOL full=TRUE;
        if(SUCCEEDED(self->GetDesc(&d)) && SUCCEEDED(self->GetFullscreenState(&full,nullptr)) && !full &&
           (d.Flags & DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING) &&
           (d.SwapEffect==DXGI_SWAP_EFFECT_FLIP_DISCARD || d.SwapEffect==DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL))
            output |= DXGI_PRESENT_ALLOW_TEARING;
    }
    const bool measuring=mode==9 && !(flags & DXGI_PRESENT_TEST);
    if(measuring)pipeline.beforePresent();
    const bool sampled=measuring && gpu.beforePresent();
    LARGE_INTEGER a{},b{},f{};QueryPerformanceCounter(&a);
    HRESULT hr=next(self,sync,output);
    QueryPerformanceCounter(&b);QueryPerformanceFrequency(&f);
    if(measuring)gpu.afterPresent(sampled,static_cast<double>(b.QuadPart-a.QuadPart)*1000000.0/static_cast<double>(f.QuadPart));
    ++stats[7];stats[8]=sync;stats[9]=flags;stats[10]=output;stats[11]=hr;
    stats[12]=(b.QuadPart-a.QuadPart)*1000000/f.QuadPart;stats[13]+=stats[12].load();
    if(output!=flags) ++stats[14];
    if(FAILED(hr)) ++stats[15];
    return hr;
}
static HRESULT STDMETHODCALLTYPE resize(IDXGISwapChain* self,UINT count,UINT w,UINT h,DXGI_FORMAT format,UINT flags) {
    const UINT target=bufferTarget.load();
    const bool guarded=target && ((flags ^ savedFlags) & DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING);
    const UINT output=target && !guarded ? target : count;
    // Unity owns release/unbind/rebind. Never invoke ResizeBuffers ourselves or modify flags.
    ++stats[20];stats[22]=count;stats[23]=output;stats[24]=flags;
    HRESULT hr=nextResize(self,output,w,h,format,flags);
    stats[21]=hr; // publish before any retry or descriptor/latency query
    if(FAILED(hr) || guarded) ++stats[25];
    if(SUCCEEDED(hr) && output!=count) ++stats[27];
    if(FAILED(hr) && output!=count) {
        ++stats[29];
        hr=nextResize(self,count,w,h,format,flags);
        stats[28]=hr; // return real recovery HRESULT; never fabricate success
        bufferTarget=savedBuffers; // failed candidate cannot rewrite again
    }
    describe();
    return hr;
}
static void stop() {
    mode=0;
    gpu.stop();gpu.publish(stats);pipeline.stop();pipeline.publish(stats);
    if(!detachAfter()) {status=-3;return;}
    if(!chain) {status=0;return;}
    if(bufferLease) {
        bufferTarget=savedBuffers;
        describe();
        // Keep the hook and COM references until Unity has performed the restoring resize.
        if(*reinterpret_cast<void***>(chain.Get())!=table.data()) {status=-3;return;}
        if(stats[2]!=savedBuffers || FAILED(static_cast<HRESULT>(stats[17].load()))) {status=2;return;}
    }
    bool ok=true;
    if(changedLatency) {
        HRESULT hr=chain2?chain2->SetMaximumFrameLatency(savedLatency):device1->SetMaximumFrameLatency(savedLatency);
        UINT checkValue=0;
        if(SUCCEEDED(hr)) hr=chain2?chain2->GetMaximumFrameLatency(&checkValue):device1->GetMaximumFrameLatency(&checkValue);
        ok=SUCCEEDED(hr) && checkValue==savedLatency;
        if(ok) changedLatency=false;
    }
    auto cell=reinterpret_cast<void**>(chain.Get());
    if(*cell!=table.data()) {status=-3;describe();return;} // overlay chain retained; pass-through forever
    InterlockedCompareExchangePointer(cell,before,table.data());
    if(*cell!=before || !ok) {status=-2;return;}
    describe();bufferTarget=0;bufferLease=false;
    chain.Reset();chain2.Reset();device1.Reset();before=nullptr;status=0;
}
static void runTrigger(void* expectedDevice) {
        // Managed Resolve pins the exact PE SHA256. No ScreenManager/window API,
        // no synthetic WM_SIZE, and no direct DXGI resize from our own resources.
        // A false Adjust result is diagnostic, never routed to Unity's fatal UI.
#ifndef SF_PRESENTPATH_FIXTURE
        auto module=reinterpret_cast<unsigned char*>(GetModuleHandleW(L"UnityPlayer.dll"));
        if(chain && module && *reinterpret_cast<void**>(module+0x221eb10)) {
            auto device=*reinterpret_cast<unsigned char**>(module+0x22736c0);
            if(!device || device!=expectedDevice) {stats[32]=2;return;}
            stats[33]=*reinterpret_cast<volatile long long*>(device+0x5e68);
            stats[34]=*reinterpret_cast<volatile long long*>(device+0x5e70);
            if(stats[33]!=stats[34]) {stats[32]=2;return;}
            auto settings=*reinterpret_cast<unsigned char**>(module+0x221eb10);
            const unsigned char guard[]={0x48,0x89,0x5c,0x24,0x08};
            DXGI_SWAP_CHAIN_DESC d{};BOOL full=TRUE;RECT client{};
            if(SUCCEEDED(chain->GetDesc(&d)) && d.Windowed &&
               SUCCEEDED(chain->GetFullscreenState(&full,nullptr)) && !full &&
               GetClientRect(d.OutputWindow,&client) &&
               client.right-client.left==stats[0] && client.bottom-client.top==stats[1] &&
               d.BufferDesc.Width==stats[0] && d.BufferDesc.Height==stats[1] &&
               std::equal(std::begin(guard),std::end(guard),module+0x947b80))
                triggerAdjust(settings,reinterpret_cast<AdjustFn>(module+0x947b80));
        }
#else
        (void)expectedDevice;
        fixtureTrigger();
#endif
        stats[32]=2;describe();return;
}
static void __stdcall event(int id,void* ptr) {
    const bool pipeTiming=id==14;
    const bool cameraTiming=id==13 || pipeTiming;
    if(cameraTiming)id=9;
    ComPtr<IDXGISwapChain> pinned;
    if((id>=1 && id<=3) || id==5 || id==6 || id==7 || id==9) pinned.Attach(static_cast<IDXGISwapChain*>(ptr));
    if(id==0) {stop();return;}
    if(id==10) {if(mode==9)gpu.begin();return;}
    if(id==11) {if(mode==9)gpu.beginFrame();return;}
    if(id==300) {if(mode==9)pipeline.begin();return;}
    if(id==301) {if(mode==9)pipeline.end();return;}
    if(id==302) {auto n=reinterpret_cast<uintptr_t>(ptr);if(mode==9 && n>=1 && n<=300)pipeline.limit=static_cast<unsigned>(n);return;}
    if(id>=100 && id<116) {if(mode==9)gpu.camera(static_cast<unsigned>(id-100),false);return;}
    if(id>=200 && id<216) {if(mode==9)gpu.camera(static_cast<unsigned>(id-200),true);return;}
    if(id==4) {describe();return;}
    if(id==8) {
        ++stats[30];stats[32]=1;stats[31]=-1;
#ifndef SF_PRESENTPATH_FIXTURE
        auto module=reinterpret_cast<unsigned char*>(GetModuleHandleW(L"UnityPlayer.dll"));
        auto device=module?*reinterpret_cast<void**>(module+0x22736c0):nullptr;
        // Only the pinned concrete device, never a shared table or code-page patch.
        if(chain && (mode==6 || mode==7 || status==2) && module &&
           *reinterpret_cast<void**>(module+0x1e3bcd0+0xbd8)==module+0x930dc0 &&
           armAfter(device,reinterpret_cast<void**>(module+0x1e3bcd0)))return;
#else
        if(fixtureArmAfter())return;
#endif
        stats[32]=2;return;
    }
    if(id==5) {
        if(chain) {describe();return;}
        if(!pinned) return;
        chain=pinned;
        DXGI_SWAP_CHAIN_DESC d{};
        if(SUCCEEDED(chain->GetDesc(&d))) {
            if(d.Flags & DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT) chain.As(&chain2);
            if(!chain2) chain->GetDevice(IID_PPV_ARGS(&device1));
        }
        describe();chain.Reset();chain2.Reset();device1.Reset();return;
    }
    if(chain || id<1 || (id>3 && id!=6 && id!=7 && id!=9) || !ptr) {status=-1;return;}
    auto* sc=static_cast<IDXGISwapChain*>(ptr);
    DXGI_SWAP_CHAIN_DESC desc{};
    if(FAILED(sc->GetDesc(&desc))) {status=-1;return;}
    for(auto& s:stats) s=0;
    gpu=sf_ppgpu::Tracker<sf_ppgpu::D3D>{};
    pipeline=sf_ppgpu::Pipeline<sf_ppgpu::D3D>{};
    chain=sc;
    ComPtr<IDXGISwapChain4> four;ComPtr<IDXGISwapChain3> three;ComPtr<IDXGISwapChain1> one;
    unsigned size=18;
    if(SUCCEEDED(sc->QueryInterface(IID_PPV_ARGS(&four))) && four.Get()==sc) size=41;
    else if(SUCCEEDED(sc->QueryInterface(IID_PPV_ARGS(&three))) && three.Get()==sc) size=40;
    else if(SUCCEEDED(sc->QueryInterface(IID_PPV_ARGS(&chain2))) && chain2.Get()==sc) size=36;
    else if(SUCCEEDED(sc->QueryInterface(IID_PPV_ARGS(&one))) && one.Get()==sc) size=29;
    chain2.Reset();
    if(desc.Flags & DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT) sc->QueryInterface(IID_PPV_ARGS(&chain2));
    if(!chain2) sc->GetDevice(IID_PPV_ARGS(&device1));
    describe();
    if(id==6 || id==7) {
        BOOL full=TRUE;
        if(FAILED(static_cast<HRESULT>(stats[17].load())) || desc.BufferCount!=2 || !desc.Windowed ||
           FAILED(sc->GetFullscreenState(&full,nullptr)) || full ||
           (desc.SwapEffect!=DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL && desc.SwapEffect!=DXGI_SWAP_EFFECT_FLIP_DISCARD)) {
            chain.Reset();chain2.Reset();device1.Reset();status=-1;return;
        }
        savedBuffers=desc.BufferCount;savedFlags=desc.Flags;stats[26]=savedBuffers;
        bufferLease=id==6;bufferTarget=id==6?3:0;
    }
    if(id==2 && (!(desc.Flags & DXGI_SWAP_CHAIN_FLAG_ALLOW_TEARING) || !desc.Windowed)) {
        chain.Reset();chain2.Reset();device1.Reset();status=-1;return; // cannot retrofit creation flags
    }
    if(id==3) {
        UINT v=0;
        HRESULT hr=chain2?chain2->GetMaximumFrameLatency(&v):device1?device1->GetMaximumFrameLatency(&v):E_NOINTERFACE;
        if(FAILED(hr) || v==4) {chain.Reset();chain2.Reset();device1.Reset();status=-1;return;}
        savedLatency=v;
        hr=chain2?chain2->SetMaximumFrameLatency(4):device1->SetMaximumFrameLatency(4);
        if(FAILED(hr)) {chain.Reset();chain2.Reset();device1.Reset();status=-1;return;}
        changedLatency=true;
    }
    if(id==9 && (!gpu.initialize(sc,cameraTiming) || (pipeTiming && !pipeline.initialize(sc)))) {
        gpu.stop();pipeline.stop();pipeline.publish(stats);
        gpu.publish(stats);chain.Reset();chain2.Reset();device1.Reset();status=-1;return;
    }
    before=*reinterpret_cast<void***>(sc);
    std::copy_n(before,size,table.begin());next=reinterpret_cast<Fn>(before[8]);
    table[8]=reinterpret_cast<void*>(present);
    nextResize=reinterpret_cast<ResizeFn>(before[13]);
    if(id==6 || id==7) table[13]=reinterpret_cast<void*>(resize);
    mode=id; // 1 observe, 2 tearing, 3 latency4, 6 buffers3
    if(InterlockedCompareExchangePointer(reinterpret_cast<void**>(sc),table.data(),before)!=before) {
        // No hook was installed. Temporarily point stop at the table only if ours; retain state on conflict.
        gpu.stop();gpu.publish(stats);pipeline.stop();pipeline.publish(stats);status=-3;mode=0;bufferTarget=0;return;
    }
    describe();status=1;
}
}
API void* sf_presentpath_event_ptr() {return reinterpret_cast<void*>(sf_presentpath::event);}
API void sf_presentpath_pin(void* ptr) {if(ptr) static_cast<IDXGISwapChain*>(ptr)->AddRef();}
API void sf_presentpath_disable() {
    sf_presentpath::triggerArmed=false;
    sf_presentpath::mode=0;
    if(sf_presentpath::bufferTarget.load()) sf_presentpath::bufferTarget=sf_presentpath::savedBuffers;
}
API void sf_presentpath_pending() {sf_presentpath::status=2;}
API long long sf_presentpath_stat(int i) {
    if(i==-1) return sf_presentpath::status;
    if(i==-2) return sf_presentpath::mode;
    return i>=0 && i<112?sf_presentpath::stats[i].load():-1;
}
