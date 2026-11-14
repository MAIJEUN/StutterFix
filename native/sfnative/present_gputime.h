// Render-thread-only, bounded, allocation-free after initialize().
#pragma once
#include <d3d11.h>
namespace sf_ppgpu {
struct D3D {
    using Query=Microsoft::WRL::ComPtr<ID3D11Query>;
    Microsoft::WRL::ComPtr<ID3D11Device> device;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context;
    bool initialize(IDXGISwapChain* swapchain) {
        if(FAILED(swapchain->GetDevice(IID_PPV_ARGS(&device))))return false;
        device->GetImmediateContext(&context);return context!=nullptr;
    }
    bool create(Query& q,D3D11_QUERY type) {
        D3D11_QUERY_DESC d{type,0};return SUCCEEDED(device->CreateQuery(&d,&q));
    }
    void begin(Query& q) {context->Begin(q.Get());}
    void end(Query& q) {context->End(q.Get());}
    HRESULT get(Query& q,void* p,UINT size) {return context->GetData(q.Get(),p,size,D3D11_ASYNC_GETDATA_DONOTFLUSH);}
    void reset() {context.Reset();device.Reset();}
};
struct Distribution {
    // Cumulative histogram: percentile upper bounds, 8us buckets through 65536us.
    std::array<unsigned long long,8193> bins{};
    unsigned long long count=0;
    double sum=0, maximum=0;
    void add(double us) {
        ++count;sum+=us;maximum=(std::max)(maximum,us);
        const size_t bin=us>=65536?8192:static_cast<size_t>(us/8);
        ++bins[bin];
    }
    long long mean() const {return count?static_cast<long long>(sum/static_cast<double>(count)+0.5):0;}
    long long percentile(unsigned p) const {
        if(!count)return 0;
        auto target=(count*p+99)/100, seen=0ull;
        for(size_t i=0;i<bins.size();++i) {
            seen+=bins[i];if(seen>=target)return i==8192?static_cast<long long>(maximum+0.5):static_cast<long long>((i+1)*8);
        }
        return 0;
    }
};
template<class Backend> struct Tracker {
    Backend api;
    struct Slot {
        typename Backend::Query disjoint;
        std::array<typename Backend::Query,2> f,a,b,c,done;
        std::array<std::array<typename Backend::Query,32>,2> cameras;
        std::array<bool,2> rendered{};
        std::array<unsigned,2> cameraStarts{},cameraEnds{};
        bool pending=false;
        unsigned used=0;
        std::array<unsigned long long,2> serial{};
    };
    std::array<Slot,16> slots{};
    Distribution render, interval, after, wall, frame;
    std::array<double,16> cameraSum{};
    std::array<unsigned long long,16> cameraSamples{};
    unsigned long long cameraSkipped=0;
    bool cameraMode=false;
    unsigned head=0,tail=0,queued=0;
    int active=-1;
    bool ready=false, frameOpen=false;
    unsigned long long serial=0,checked=0,unobserved=0,disjoints=0,failures=0,drops=0,missing=0,discarded=0;
    bool initialize(IDXGISwapChain* swapchain,bool cameras=false) {
        cameraMode=cameras;
        if(!api.initialize(swapchain)) {++failures;stop();return false;}
        for(auto& s:slots) {
            bool ok=api.create(s.disjoint,D3D11_QUERY_TIMESTAMP_DISJOINT);
            for(unsigned i=0;i<2 && ok;++i)
                ok=api.create(s.f[i],D3D11_QUERY_TIMESTAMP) && api.create(s.a[i],D3D11_QUERY_TIMESTAMP) && api.create(s.b[i],D3D11_QUERY_TIMESTAMP) &&
                   api.create(s.c[i],D3D11_QUERY_TIMESTAMP) && api.create(s.done[i],D3D11_QUERY_EVENT);
            if(cameraMode)for(auto& row:s.cameras)for(auto& q:row)if(ok)ok=api.create(q,D3D11_QUERY_TIMESTAMP);
            if(!ok) {
                ++failures;stop();return false;
            }
        }
        ready=true;return true;
    }
    void stop() {
        if(active>=0) {
            auto& s=slots[static_cast<size_t>(active)];api.end(s.disjoint);
            discarded+=s.used+(frameOpen?1:0);
        }
        discarded+=queued*2;
        for(auto& s:slots)s=Slot{};
        api.reset();ready=frameOpen=false;active=-1;queued=head=tail=0;
    }
    HRESULT get(typename Backend::Query& q,void* p,UINT size) {
        const auto hr=api.get(q,p,size);if(FAILED(hr))++failures;return hr;
    }
    void collect() {
        // Ordered retirement preserves adjacent-frame identity even if readiness differs.
        for(unsigned n=0;n<slots.size() && queued;++n) {
            auto& s=slots[tail];D3D11_QUERY_DATA_TIMESTAMP_DISJOINT d{};
            std::array<UINT64,2> f{},a{},b{},c{};
            std::array<std::array<UINT64,32>,2> cams{};
            HRESULT hr=get(s.disjoint,&d,sizeof(d));
            if(hr==S_FALSE)break;
            if(hr==S_OK && !d.Disjoint && d.Frequency) {
                for(unsigned i=0;i<2 && hr==S_OK;++i) {
                    hr=get(s.f[i],&f[i],sizeof(UINT64));
                    if(hr==S_OK && s.rendered[i])hr=get(s.a[i],&a[i],sizeof(UINT64));
                    if(hr==S_OK)hr=get(s.b[i],&b[i],sizeof(UINT64));
                    if(hr==S_OK)hr=get(s.c[i],&c[i],sizeof(UINT64));
                    for(unsigned k=0;k<16 && hr==S_OK;++k)if((s.cameraStarts[i]&s.cameraEnds[i])&(1u<<k)) {
                        hr=get(s.cameras[i][2*k],&cams[i][2*k],sizeof(UINT64));
                        if(hr==S_OK)hr=get(s.cameras[i][2*k+1],&cams[i][2*k+1],sizeof(UINT64));
                    }
                }
                if(hr==S_FALSE)break;
                if(hr==S_OK && f[0]<=b[0] && b[0]<=c[0] && c[0]<=f[1] && f[1]<=b[1] && b[1]<=c[1]) {
                    const double scale=1000000.0/static_cast<double>(d.Frequency);
                    for(unsigned i=0;i<2;++i) {
                        frame.add(static_cast<double>(b[i]-f[i])*scale);
                        if(s.rendered[i] && f[i]<=a[i] && a[i]<=b[i])render.add(static_cast<double>(b[i]-a[i])*scale);
                        else if(s.rendered[i])++failures;
                        after.add(static_cast<double>(c[i]-b[i])*scale);
                        for(unsigned k=0;k<16;++k)if((s.cameraStarts[i]&s.cameraEnds[i])&(1u<<k)) {
                            const auto x=cams[i][2*k],y=cams[i][2*k+1];
                            if(f[i]<=x && x<=y && y<=b[i]) {cameraSum[k]+=static_cast<double>(y-x)*scale;++cameraSamples[k];}
                            else ++failures;
                        }
                    }
                    // Both b timestamps are inside ONE disjoint epoch. Sample every
                    // other adjacent interval rather than assuming cross-epoch continuity.
                    if(s.serial[0]+1==s.serial[1])interval.add(static_cast<double>(b[1]-b[0])*scale);
                } else if(hr==S_OK)++failures;
            } else if(hr==S_OK)++disjoints;
            s.pending=false;tail=(tail+1)%static_cast<unsigned>(slots.size());--queued;
        }
    }
    void beginFrame() {
        if(!ready || frameOpen)return; // first camera, including offscreen, per Present
        collect();
        if(active<0) {
            if(queued==slots.size())return;
            active=static_cast<int>(head);slots[head].used=0;api.begin(slots[head].disjoint);
        }
        auto& s=slots[static_cast<size_t>(active)];
        s.rendered[s.used]=false;s.cameraStarts[s.used]=s.cameraEnds[s.used]=0;
        api.end(s.f[s.used]);frameOpen=true;
    }
    void begin() {
        if(!frameOpen)return;
        auto& s=slots[static_cast<size_t>(active)];
        if(!s.rendered[s.used]) {api.end(s.a[s.used]);s.rendered[s.used]=true;}
    }
    void camera(unsigned index,bool end) {
        if(!ready || !cameraMode || !frameOpen || index>=16)return;
        auto& s=slots[static_cast<size_t>(active)];const auto bit=1u<<index;
        auto& mask=end?s.cameraEnds[s.used]:s.cameraStarts[s.used];
        if(mask&bit) {++cameraSkipped;return;} // never overwrite a query on repeated Camera.Render
        if(end && !(s.cameraStarts[s.used]&bit))return;
        api.end(s.cameras[s.used][2*index+(end?1:0)]);mask|=bit;
    }
    bool beforePresent() {
        ++serial;
        if(!ready)return false;
        if(!frameOpen) {++missing;if(queued==slots.size())++drops;return false;}
        auto& s=slots[static_cast<size_t>(active)];s.serial[s.used]=serial;
        api.end(s.b[s.used]);api.end(s.done[s.used]);return true;
    }
    void afterPresent(bool sampled,double us) {
        if(!ready)return;
        wall.add(us);
        if(sampled) {
            auto& s=slots[static_cast<size_t>(active)];
            // First GPU operation after QPC at return; query read is deliberately later.
            api.end(s.c[s.used]);
            BOOL done=FALSE;const auto hr=get(s.done[s.used],&done,sizeof(done));
            if(hr==S_OK || hr==S_FALSE) {++checked;if(hr==S_FALSE || !done)++unobserved;}
            ++s.used;frameOpen=false;
            if(s.used==2) {
                api.end(s.disjoint);s.pending=true;head=(head+1)%static_cast<unsigned>(slots.size());++queued;active=-1;
            }
        }
        collect();
    }
    template<size_t N> void publish(std::array<std::atomic<long long>,N>& stats) const {
        stats[36]=static_cast<long long>(render.count);stats[37]=render.mean();stats[38]=render.percentile(50);stats[39]=render.percentile(95);
        stats[40]=static_cast<long long>(render.maximum+0.5);
        stats[41]=static_cast<long long>(interval.count);stats[42]=interval.mean();stats[43]=interval.percentile(50);stats[44]=interval.percentile(95);stats[45]=static_cast<long long>(interval.maximum+0.5);
        stats[46]=static_cast<long long>(wall.count);stats[47]=wall.mean();stats[48]=wall.percentile(95);
        stats[49]=static_cast<long long>(checked);stats[50]=static_cast<long long>(unobserved);stats[51]=checked?static_cast<long long>(unobserved*1000000/checked):0;
        stats[52]=static_cast<long long>(disjoints);stats[53]=static_cast<long long>(failures);stats[54]=static_cast<long long>(drops);stats[55]=queued*2+(active>=0?slots[static_cast<size_t>(active)].used:0);
        stats[56]=static_cast<long long>(missing);stats[57]=static_cast<long long>(discarded);
        stats[58]=after.mean();stats[59]=after.percentile(95);stats[60]=ready?1:0;
        stats[61]=static_cast<long long>(frame.count);stats[62]=frame.mean();stats[63]=frame.percentile(50);
        stats[64]=frame.percentile(95);stats[65]=static_cast<long long>(frame.maximum+0.5);
        stats[66]=cameraMode?1:0;stats[67]=static_cast<long long>(cameraSkipped);
        for(unsigned k=0;k<16;++k) {
            stats[68+2*k]=static_cast<long long>(cameraSamples[k]);
            stats[69+2*k]=frame.count?static_cast<long long>(cameraSum[k]/static_cast<double>(frame.count)+0.5):0;
        }
    }
};
}
