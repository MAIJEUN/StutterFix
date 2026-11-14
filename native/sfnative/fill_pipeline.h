#pragma once
// Main camera only. Separate bounded query ring; never overlaps pipeline queries.
namespace sf_ppgpu {
template<class Backend> struct Pipeline {
    Backend api;
    struct Slot {typename Backend::Query query;bool pending=false;};
    std::array<Slot,16> slots{};
    unsigned head=0,tail=0,queued=0;
    bool ready=false,open=false,valid=true,seen=false;
    unsigned limit=30,submitted=0;
    unsigned long long samples=0,ps=0,primitives=0,failures=0,drops=0,missing=0,repeated=0;
    bool initialize(IDXGISwapChain* chain) {
        if(!api.initialize(chain)){++failures;stop();return false;}
        for(auto& s:slots)if(!api.create(s.query,D3D11_QUERY_PIPELINE_STATISTICS)){++failures;stop();return false;}
        ready=true;return true;
    }
    void collect() {
        while(queued) {
            auto& s=slots[tail];D3D11_QUERY_DATA_PIPELINE_STATISTICS d{};
            auto hr=api.get(s.query,&d,sizeof(d));
            if(hr==S_FALSE)break;
            if(hr==S_OK && s.pending){++samples;ps+=d.PSInvocations;primitives+=d.CPrimitives;}else if(FAILED(hr))++failures;
            s.pending=false;tail=(tail+1)%16;--queued;
        }
    }
    void begin() {
        if(!ready)return;
        if(open){++repeated;valid=false;return;}
        if(seen){++repeated;return;}
        collect();if(submitted>=limit)return;
        if(queued==16){++drops;return;}
        api.begin(slots[head].query);open=true;valid=true;seen=true;
    }
    void end() {
        if(!ready||!open)return;
        api.end(slots[head].query);open=false;slots[head].pending=valid;++submitted;
        head=(head+1)%16;++queued;collect();
    }
    void beforePresent() {
        if(!ready)return;
        seen=false;
        if(open) {
            // End malformed camera scope to balance D3D Begin, but never publish it.
            api.end(slots[head].query);open=false;++missing;++submitted;
            // Do not reuse an in-flight query: enqueue and mark it invalid.
            slots[head].pending=false;head=(head+1)%16;++queued;
        }
        collect();
    }
    void stop() {
        if(open)api.end(slots[head].query);
        for(auto& s:slots) s=Slot{};
        api.reset();ready=open=seen=false;head=tail=queued=0;
    }
    template<size_t N> void publish(std::array<std::atomic<long long>,N>& stats) const {
        if constexpr(N>=110) {
            stats[100]=ready;stats[101]=samples;stats[102]=ps;stats[103]=primitives;
            stats[104]=failures;stats[105]=drops;stats[106]=queued;stats[107]=missing;stats[108]=repeated;
            stats[109]=limit;
            if constexpr(N>=111)stats[110]=submitted;
        }
    }
};
}
