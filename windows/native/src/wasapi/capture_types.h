// Common types every WASAPI capture primitive hands to its consumer: one mono float
// packet with its QPC timestamp plus the WASAPI buffer flags, and a small stream-event
// channel. Packet memory is owned by the stream and valid only inside on_packet().
#pragma once
#include <windows.h>
#include <stdint.h>

namespace wf {

struct WfPacket {
    const float* mono;        // frames samples, already averaged to mono, [-1, 1]
    uint32_t frames;
    int64_t qpc_100ns;        // pu64QPCPosition from IAudioCaptureClient::GetBuffer
    uint64_t device_position; // pu64DevicePosition (frames since stream start)
    uint32_t flags;           // AUDCLNT_BUFFERFLAGS_*
    uint32_t sample_rate;
};

enum class StreamKind {
    Mic,
    DeviceLoopback,
    ProcessLoopbackExclude,
    ProcessLoopbackInclude,
    KeepAliveRender,
};

const char* stream_kind_name(StreamKind k);

struct StreamEvent {
    enum Kind { Started, FirstPacket, DeviceInvalidated, Error, Stopped } kind;
    HRESULT hr;
};

class IPacketSink {
public:
    virtual ~IPacketSink() = default;
    virtual void on_packet(const WfPacket&) = 0;
    virtual void on_event(const StreamEvent&) = 0;
};

} // namespace wf
