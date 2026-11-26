// Per-stream timing statistics for the WASAPI measurements (M1): least-squares fit of
// the QPC timestamps against the delivered sample count, residual percentiles in ms,
// clock slope in ppm, and the WASAPI buffer-flag counters.
#pragma once
#include <stdint.h>
#include <stddef.h>
#include <vector>

namespace probe {

struct PacketRecord {
    int64_t recv_qpc_100ns = 0;  // when our thread saw the packet
    int64_t qpc_100ns = 0;       // pu64QPCPosition
    uint64_t device_position = 0;
    uint64_t cum_frames = 0;     // frames delivered before this packet
    uint32_t frames = 0;
    uint32_t flags = 0;
    float peak = 0.0f;
    bool all_zero = false;
    uint32_t nonzero = 0;        // samples != 0.0f in this packet (compare)
};

struct StreamStats {
    size_t packets = 0;
    uint64_t frames = 0;
    double span_sec = 0.0;          // from recv timestamps
    double packets_per_sec = 0.0;
    double mean_frames = 0.0;
    uint32_t min_frames = 0, max_frames = 0;

    bool fit_valid = false;
    double resid_p50_ms = 0.0, resid_p95_ms = 0.0, resid_max_ms = 0.0;
    double slope_ppm = 0.0;         // QPC-vs-frames slope against the nominal rate

    bool devpos_valid = false;
    double devpos_rate_hz = 0.0;
    double devpos_ppm = 0.0;

    uint32_t n_timestamp_error = 0, n_discontinuity = 0, n_silent = 0;
    size_t zero_packets = 0;
    double zero_fraction = 0.0;
    double peak_max = 0.0;
    double rms = 0.0;               // not computed here; filled by callers that need it

    bool qpc_all_nonzero = true;
    bool qpc_monotonic = true;
    int64_t first_qpc = 0, last_qpc = 0;
    double max_recv_gap_ms = 0.0;   // largest wall-clock gap between two packets
};

StreamStats compute_stats(const std::vector<PacketRecord>& p, uint32_t rate);

} // namespace probe
