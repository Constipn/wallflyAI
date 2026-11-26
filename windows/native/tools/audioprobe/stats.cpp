// Statistics implementation. The fit is centred before summing so the large absolute
// QPC values (100 ns since boot) do not lose precision; residuals are reported as
// absolute deviations in milliseconds, which is the unit the 3 ms spec rules use.
#include "stats.h"

#include <algorithm>
#include <audioclient.h>
#include <cmath>

namespace probe {

StreamStats compute_stats(const std::vector<PacketRecord>& p, uint32_t rate) {
    StreamStats s;
    s.packets = p.size();
    if (p.empty()) return s;

    s.min_frames = p[0].frames;
    s.max_frames = p[0].frames;
    for (const PacketRecord& r : p) {
        s.frames += r.frames;
        s.min_frames = std::min(s.min_frames, r.frames);
        s.max_frames = std::max(s.max_frames, r.frames);
        if (r.flags & AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR) ++s.n_timestamp_error;
        if (r.flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) ++s.n_discontinuity;
        if (r.flags & AUDCLNT_BUFFERFLAGS_SILENT) ++s.n_silent;
        if (r.all_zero) ++s.zero_packets;
        if (r.peak > s.peak_max) s.peak_max = r.peak;
        if (r.qpc_100ns == 0) s.qpc_all_nonzero = false;
    }
    s.mean_frames = static_cast<double>(s.frames) / static_cast<double>(s.packets);
    s.zero_fraction = static_cast<double>(s.zero_packets) / static_cast<double>(s.packets);
    s.first_qpc = p.front().qpc_100ns;
    s.last_qpc = p.back().qpc_100ns;

    for (size_t i = 1; i < p.size(); ++i) {
        if (p[i].qpc_100ns < p[i - 1].qpc_100ns) s.qpc_monotonic = false;
        const double gap_ms = static_cast<double>(p[i].recv_qpc_100ns - p[i - 1].recv_qpc_100ns) / 10000.0;
        if (gap_ms > s.max_recv_gap_ms) s.max_recv_gap_ms = gap_ms;
    }
    s.span_sec = static_cast<double>(p.back().recv_qpc_100ns - p.front().recv_qpc_100ns) / 1e7;
    if (s.span_sec > 0) s.packets_per_sec = static_cast<double>(s.packets) / s.span_sec;

    if (p.size() < 8 || rate == 0) return s;

    // Least-squares fit qpc = a + b * cum_frames, centred on the means.
    double mx = 0.0, my = 0.0;
    for (const PacketRecord& r : p) {
        mx += static_cast<double>(r.cum_frames);
        my += static_cast<double>(r.qpc_100ns);
    }
    const double n = static_cast<double>(p.size());
    mx /= n;
    my /= n;
    double sxx = 0.0, sxy = 0.0;
    for (const PacketRecord& r : p) {
        const double dx = static_cast<double>(r.cum_frames) - mx;
        sxx += dx * dx;
        sxy += dx * (static_cast<double>(r.qpc_100ns) - my);
    }
    if (sxx <= 0.0) return s;
    const double b = sxy / sxx;              // 100 ns per frame
    const double a = my - b * mx;

    std::vector<double> resid;
    resid.reserve(p.size());
    for (const PacketRecord& r : p) {
        const double fitted = a + b * static_cast<double>(r.cum_frames);
        resid.push_back(std::fabs((static_cast<double>(r.qpc_100ns) - fitted) / 10000.0));
    }
    std::sort(resid.begin(), resid.end());
    auto pct = [&](double q) {
        const size_t idx = static_cast<size_t>(q * static_cast<double>(resid.size() - 1) + 0.5);
        return resid[std::min(idx, resid.size() - 1)];
    };
    s.resid_p50_ms = pct(0.50);
    s.resid_p95_ms = pct(0.95);
    s.resid_max_ms = resid.back();
    const double nominal = 1e7 / static_cast<double>(rate);
    s.slope_ppm = (b / nominal - 1.0) * 1e6;
    s.fit_valid = true;

    // device_position against QPC: frames per 100 ns -> Hz.
    bool any_devpos = false;
    for (const PacketRecord& r : p) {
        if (r.device_position != 0) { any_devpos = true; break; }
    }
    if (any_devpos) {
        double dmx = 0.0, dmy = 0.0;
        for (const PacketRecord& r : p) {
            dmx += static_cast<double>(r.qpc_100ns);
            dmy += static_cast<double>(r.device_position);
        }
        dmx /= n;
        dmy /= n;
        double dsxx = 0.0, dsxy = 0.0;
        for (const PacketRecord& r : p) {
            const double dx = static_cast<double>(r.qpc_100ns) - dmx;
            dsxx += dx * dx;
            dsxy += dx * (static_cast<double>(r.device_position) - dmy);
        }
        if (dsxx > 0.0) {
            s.devpos_rate_hz = (dsxy / dsxx) * 1e7;
            s.devpos_ppm = (s.devpos_rate_hz / static_cast<double>(rate) - 1.0) * 1e6;
            s.devpos_valid = true;
        }
    }
    return s;
}

} // namespace probe
