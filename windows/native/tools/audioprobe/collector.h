// IPacketSink that records one row per WASAPI packet plus the stream events. The hot
// path only appends to a pre-reserved vector (single producer: the capture thread);
// the main thread reads everything after the stream has been stopped and joined.
#pragma once
#include <stdint.h>
#include <string>
#include <vector>

#include "stats.h"
#include "wasapi/capture_types.h"

namespace probe {

struct EventRecord {
    int64_t qpc_100ns = 0;
    wf::StreamEvent::Kind kind = wf::StreamEvent::Started;
    HRESULT hr = S_OK;
};

class RecordingSink final : public wf::IPacketSink {
public:
    RecordingSink(std::string label, int expected_seconds);

    void on_packet(const wf::WfPacket& p) override;
    void on_event(const wf::StreamEvent& e) override;

    const std::string& label() const { return label_; }
    const std::vector<PacketRecord>& packets() const { return packets_; }
    const std::vector<EventRecord>& events() const { return events_; }
    uint32_t rate() const { return rate_; }
    int64_t first_packet_qpc() const { return first_packet_qpc_; }
    bool dropped() const { return dropped_; }

    // Resets the recording window so a command can measure two phases with one stream.
    void reset_packets();

private:
    std::string label_;
    std::vector<PacketRecord> packets_;
    std::vector<EventRecord> events_;
    uint64_t cum_frames_ = 0;
    uint32_t rate_ = 0;
    int64_t first_packet_qpc_ = 0;
    bool dropped_ = false; // capacity exhausted -> stopped appending rather than allocating
};

const char* event_kind_name(wf::StreamEvent::Kind k);

} // namespace probe
