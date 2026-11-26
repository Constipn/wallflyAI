// RecordingSink implementation. Capacity is reserved up front for roughly 400 packets
// per second; once it is exhausted the sink stops recording instead of reallocating, so
// the capture loop stays allocation-free for the whole measurement.
#include "collector.h"
#include "wasapi/clock.h"

#include <cmath>

namespace probe {

const char* event_kind_name(wf::StreamEvent::Kind k) {
    switch (k) {
        case wf::StreamEvent::Started: return "Started";
        case wf::StreamEvent::FirstPacket: return "FirstPacket";
        case wf::StreamEvent::DeviceInvalidated: return "DeviceInvalidated";
        case wf::StreamEvent::Error: return "Error";
        case wf::StreamEvent::Stopped: return "Stopped";
    }
    return "?";
}

RecordingSink::RecordingSink(std::string label, int expected_seconds) : label_(std::move(label)) {
    const size_t cap = static_cast<size_t>(expected_seconds > 0 ? expected_seconds : 1) * 400 + 2048;
    packets_.reserve(cap);
    events_.reserve(256);
}

void RecordingSink::reset_packets() {
    packets_.clear();
    cum_frames_ = 0;
    dropped_ = false;
}

void RecordingSink::on_packet(const wf::WfPacket& p) {
    rate_ = p.sample_rate;
    if (first_packet_qpc_ == 0) first_packet_qpc_ = p.qpc_100ns;

    PacketRecord r;
    r.recv_qpc_100ns = wf::qpc_now_100ns();
    r.qpc_100ns = p.qpc_100ns;
    r.device_position = p.device_position;
    r.cum_frames = cum_frames_;
    r.frames = p.frames;
    r.flags = p.flags;

    float peak = 0.0f;
    uint32_t nonzero = 0;
    for (uint32_t i = 0; i < p.frames; ++i) {
        const float v = std::fabs(p.mono[i]);
        if (v > peak) peak = v;
        if (v != 0.0f) ++nonzero;
    }
    r.peak = peak;
    r.nonzero = nonzero;
    r.all_zero = (peak == 0.0f);

    cum_frames_ += p.frames;
    if (packets_.size() < packets_.capacity()) packets_.push_back(r);
    else dropped_ = true;
}

void RecordingSink::on_event(const wf::StreamEvent& e) {
    EventRecord r;
    r.qpc_100ns = wf::qpc_now_100ns();
    r.kind = e.kind;
    r.hr = e.hr;
    if (events_.size() < events_.capacity()) events_.push_back(r);
}

} // namespace probe
