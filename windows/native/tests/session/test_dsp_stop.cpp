// The threaded stop protocol of wf::DspThread (src/session/dsp_thread.*) against a recording
// IDspHost - no hardware, no WASAPI, no fake session:
//   * the stop flush runs ON the DSP thread, which stays the only producer, and Stopped is
//     staged after the last frame and committed last;
//   * the stopping thread's wait is bounded even when a tick hangs, a hung tick is never
//     taken over, and a thread that recovers still delivers its flush;
//   * a bounded join reports a thread that does not leave (its owner must abandon it);
//   * the producer role can be taken over only between ticks, and the DSP thread never
//     produces again afterwards.
// Unlike the virtual-time suites this one runs a real thread and a few short bounded waits
// (a few hundred milliseconds in total). It links the session layer statically, next to
// the DLL, because DspThread is not part of the C ABI.
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <memory>
#include <vector>

#include "session/dsp_thread.h"
#include "test_util.h"

namespace {

constexpr int64_t kOrigin = 1000LL * 10000000LL;   // 1000 s, as in the session harness
constexpr int64_t kBlockTicks = 100000;            // 10 ms in 100 ns units
constexpr uint32_t kStopWaitMs = 2500;             // Session::kStopWaitMs

std::atomic<int64_t> g_clock{kOrigin};
int64_t clock_fn(void*) { return g_clock.load(); }

// Producer-side log, one letter per host call.
constexpr char kTick = 'T';
constexpr char kFrame = 'F';
constexpr char kEvent = 'E';
constexpr char kCommit = 'C';
constexpr char kStopped = 'S';

class Host final : public wf::IDspHost {
public:
    Host() : gate_(CreateEventW(nullptr, TRUE, TRUE, nullptr)) { log_.reserve(8192); }
    ~Host() override { CloseHandle(gate_); }
    Host(const Host&) = delete;
    Host& operator=(const Host&) = delete;

    // ---- IDspHost: called on the producer thread only ---------------------------
    std::shared_ptr<wf::StreamChannel> dsp_channel(wf::Role r) override {
        return r == wf::Role::Others ? others : nullptr;
    }
    void dsp_publish(const WfEvent&) override { log_.push_back(kEvent); }
    bool dsp_push_frame(const WfFrame& f) override {
        log_.push_back(kFrame);
        if (f.source == WF_SOURCE_OTHERS) ++others_frames;
        return true;
    }
    // The first host call of every tick; a closed gate makes the tick hang right here.
    void dsp_forward_supervisor_events() override {
        entered.fetch_add(1);
        WaitForSingleObject(gate_, INFINITE);
        log_.push_back(kTick);
    }
    size_t dsp_commit_events() override {
        log_.push_back(kCommit);
        return 0;
    }
    void dsp_notify() override {}
    void dsp_stopped(int64_t) override {
        log_.push_back(kStopped);
        stopped_thread.store(GetCurrentThreadId());
        stopped_calls.fetch_add(1);
    }

    // ---- test side ---------------------------------------------------------------
    void close_gate() { ResetEvent(gate_); }
    void open_gate() { SetEvent(gate_); }
    const std::vector<char>& log() const { return log_; }   // read only after a join

    std::shared_ptr<wf::StreamChannel> others;
    std::atomic<uint32_t> entered{0};
    std::atomic<DWORD> stopped_thread{0};
    std::atomic<uint32_t> stopped_calls{0};
    size_t others_frames = 0;

private:
    HANDLE gate_;
    std::vector<char> log_;
};

std::unique_ptr<wf::DspThread> make_dsp(Host& host) {
    auto dsp = std::make_unique<wf::DspThread>();
    wf::DspThread::Config c;   // exclude mode, NS 2, canceller on
    c.session_origin_100ns = kOrigin;
    dsp->configure(&host, c, &clock_fn, nullptr);
    return dsp;
}

bool wait_for(const std::atomic<uint32_t>& v, uint32_t at_least, ULONGLONG ms) {
    const ULONGLONG end = GetTickCount64() + ms;
    while (v.load() < at_least) {
        if (GetTickCount64() > end) return false;
        Sleep(1);
    }
    return true;
}

// The flush runs on the DSP thread; all frames precede Stopped, and Stopped is committed last.
void test_flush_on_dsp_thread() {
    Host host;
    host.others = std::make_shared<wf::StreamChannel>(wf::Role::Others, 1u, false,
                                                      std::make_shared<wf::MsgQueue>());
    // 200 ms of Others audio already sits in the ring (inside the 240 ms CaptureInbox).
    const int blocks = 20;
    float buf[160];
    for (float& v : buf) v = 0.25f;
    for (int n = 0; n < blocks; ++n) {
        const wf::WfPacket p{buf, 160u, kOrigin + n * kBlockTicks, static_cast<uint64_t>(n) * 160u,
                         0u, 16000u};
        host.others->deliver(p, kOrigin + n * kBlockTicks);
    }
    g_clock.store(kOrigin + blocks * kBlockTicks);

    std::unique_ptr<wf::DspThread> dsp = make_dsp(host);
    CHECK(dsp->start_thread());
    CHECK(dsp->thread_running());
    CHECK(dsp->finish_thread(kStopWaitMs));
    CHECK(dsp->join_thread(2000));
    CHECK(!dsp->thread_running());

    CHECK(host.stopped_calls.load() == 1);
    CHECK(host.stopped_thread.load() != 0 && host.stopped_thread.load() != GetCurrentThreadId());
    const std::vector<char>& log = host.log();
    CHECK(log.size() >= 2 && log[log.size() - 2] == kStopped && log.back() == kCommit);
    const auto stopped_at = std::find(log.begin(), log.end(), kStopped);
    CHECK(std::find(stopped_at, log.end(), kFrame) == log.end());   // no frame after Stopped
    CHECK(std::count(log.begin(), log.end(), kStopped) == 1);
    CHECK(host.others_frames == static_cast<size_t>(blocks));
    std::printf("  dsp flush: %zu Others frames, Stopped staged on the DSP thread, committed last\n",
                host.others_frames);
}

// A hung tick: the stop wait is bounded, the hung thread is not taken over, and once it
// recovers it still runs the flush and stages Stopped on its own thread.
void test_bounded_wait_and_recovery() {
    Host host;
    g_clock.store(kOrigin);
    std::unique_ptr<wf::DspThread> dsp = make_dsp(host);
    host.close_gate();
    CHECK(dsp->start_thread());
    CHECK(wait_for(host.entered, 1, 2000));   // the first tick now hangs inside the host

    const ULONGLONG t0 = GetTickCount64();
    CHECK(!dsp->finish_thread(150));          // gives up although the flush never ran
    const ULONGLONG waited = GetTickCount64() - t0;
    CHECK(waited >= 100 && waited < 1500);
    CHECK(!dsp->take_producer());             // busy inside a tick: no second producer
    CHECK(host.stopped_calls.load() == 0);

    host.open_gate();                         // the thread recovers ...
    CHECK(wait_for(host.stopped_calls, 1, 2000));   // ... and delivers its flush itself
    CHECK(dsp->join_thread(2000));
    CHECK(host.stopped_calls.load() == 1);
    CHECK(host.stopped_thread.load() != GetCurrentThreadId());
    CHECK(!host.log().empty() && host.log().back() == kCommit);
    std::printf("  dsp hung tick: stop wait returned after %llu ms, flush followed on recovery\n",
                static_cast<unsigned long long>(waited));
}

// A bounded join reports a thread that does not leave; once it can, it leaves without a flush.
void test_join_reports_hung_thread() {
    Host host;
    g_clock.store(kOrigin);
    std::unique_ptr<wf::DspThread> dsp = make_dsp(host);
    host.close_gate();
    CHECK(dsp->start_thread());
    CHECK(wait_for(host.entered, 1, 2000));
    CHECK(!dsp->join_thread(100));            // still inside the hung tick: abandon, not free
    CHECK(dsp->thread_running());
    host.open_gate();
    CHECK(dsp->join_thread(2000));
    CHECK(host.stopped_calls.load() == 0);    // exit request, not a stop: nothing flushed
}

// Between ticks the producer role can be taken over; the DSP thread then never enters a
// tick again and leaves at its next wake-up.
void test_take_over_between_ticks() {
    Host host;
    g_clock.store(kOrigin);
    std::unique_ptr<wf::DspThread> dsp = make_dsp(host);
    CHECK(dsp->start_thread());
    CHECK(wait_for(host.entered, 2, 2000));   // ticking normally
    bool taken = false;
    for (int i = 0; i < 100000 && !taken; ++i) {
        taken = dsp->take_producer();
        if (!taken) Sleep(0);
    }
    CHECK(taken);
    const uint32_t ticks_at_take = host.entered.load();
    Sleep(50);                                // several timer periods
    CHECK(dsp->join_thread(2000));
    CHECK(host.entered.load() == ticks_at_take);
    CHECK(host.stopped_calls.load() == 0);
    CHECK(!dsp->take_producer());             // taken once, for good
    std::printf("  dsp take-over: %u ticks before, none after\n", ticks_at_take);
}

}  // namespace

void test_dsp_stop() {
    test_flush_on_dsp_thread();
    test_bounded_wait_and_recovery();
    test_join_reports_hung_thread();
    test_take_over_between_ticks();
}
