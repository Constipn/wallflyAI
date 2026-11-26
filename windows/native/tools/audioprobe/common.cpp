// Shared report formatting for the probe commands: one markdown block per stream, the
// per-packet CSV, the JSON mirror and the small helpers (phase split, dBFS, countdown).
// Keeping this in one place is what makes two runs comparable line by line.
#include "commands.h"
#include "wasapi/com_util.h"

#include <audioclient.h>
#include <cmath>
#include <cstdio>

namespace probe {

std::vector<PacketRecord> packets_before(const std::vector<PacketRecord>& all, int64_t qpc) {
    std::vector<PacketRecord> out;
    uint64_t cum = 0;
    for (const PacketRecord& r : all) {
        if (r.recv_qpc_100ns >= qpc) break;
        PacketRecord c = r;
        c.cum_frames = cum;
        cum += c.frames;
        out.push_back(c);
    }
    return out;
}

std::vector<PacketRecord> packets_from(const std::vector<PacketRecord>& all, int64_t qpc) {
    std::vector<PacketRecord> out;
    uint64_t cum = 0;
    for (const PacketRecord& r : all) {
        if (r.recv_qpc_100ns < qpc) continue;
        PacketRecord c = r;
        c.cum_frames = cum;
        cum += c.frames;
        out.push_back(c);
    }
    return out;
}

double to_dbfs(double amplitude) {
    if (amplitude <= 1e-9) return -180.0;
    return 20.0 * std::log10(amplitude);
}

void countdown(const char* what, int seconds) {
    for (int i = seconds; i > 0; --i) {
        std::printf("\r  %s: %d s remaining   ", what, i);
        std::fflush(stdout);
        Sleep(1000);
    }
    std::printf("\r  %s: done                \n", what);
}

void write_stream_block(Report& r, const std::string& title, const wf::StreamDiagnostics& diag,
                        const StreamStats& s, const std::vector<EventRecord>& events) {
    r.md("### " + title);
    r.md("");
    r.md("| Property | Value |");
    r.md("|---|---|");
    r.mdf("| opened | %s |", diag.opened ? "yes" : "no");
    r.mdf("| open attempt | %d%s |", diag.init_attempt,
          diag.notes.empty() ? "" : (" (" + diag.notes + ")").c_str());
    if (!diag.opened) r.mdf("| init HRESULT | %s |", wf::hr_str(diag.init_hr).c_str());
    r.mdf("| callback mode | %s |", diag.event_driven ? "event-driven" : "timer polling (10 ms)");
    r.mdf("| format | %s |", diag.format_text.empty() ? "-" : diag.format_text.c_str());
    r.mdf("| endpoint buffer | %u frames |", diag.buffer_frames);
    r.mdf("| stream flags | 0x%08X |", diag.stream_flags);
    if (diag.raw_requested) {
        r.mdf("| RAW requested | yes, %s (%s) |", diag.raw_accepted ? "accepted" : "rejected",
              wf::hr_str(diag.raw_hr).c_str());
    } else {
        r.md("| RAW requested | no |");
    }
    r.mdf("| packets | %zu |", s.packets);
    r.mdf("| frames | %llu |", static_cast<unsigned long long>(s.frames));
    r.mdf("| span | %.2f s |", s.span_sec);
    r.mdf("| packets/s | %.1f |", s.packets_per_sec);
    r.mdf("| frames/packet | mean %.1f, min %u, max %u |", s.mean_frames, s.min_frames, s.max_frames);
    r.mdf("| largest gap between packets | %.1f ms |", s.max_recv_gap_ms);
    if (s.fit_valid) {
        r.mdf("| QPC residual p50 / p95 / max | %.3f / %.3f / %.3f ms |", s.resid_p50_ms,
              s.resid_p95_ms, s.resid_max_ms);
        r.mdf("| QPC slope vs nominal rate | %+.1f ppm |", s.slope_ppm);
    } else {
        r.md("| QPC residual | too few packets for a fit |");
    }
    if (s.devpos_valid) {
        r.mdf("| device_position vs QPC | %.2f Hz (%+.1f ppm) |", s.devpos_rate_hz, s.devpos_ppm);
    } else {
        r.md("| device_position vs QPC | device_position stayed 0 (not reported by this stream) |");
    }
    r.mdf("| QPC all non-zero / monotonic | %s / %s |", s.qpc_all_nonzero ? "yes" : "NO",
          s.qpc_monotonic ? "yes" : "NO");
    r.mdf("| TIMESTAMP_ERROR / DATA_DISCONTINUITY / SILENT | %u / %u / %u |", s.n_timestamp_error,
          s.n_discontinuity, s.n_silent);
    r.mdf("| all-zero packets | %zu of %zu (%.1f %%) |", s.zero_packets, s.packets,
          s.zero_fraction * 100.0);
    r.mdf("| peak amplitude | %.6f (%.1f dBFS) |", s.peak_max, to_dbfs(s.peak_max));

    std::string ev;
    for (const EventRecord& e : events) {
        if (!ev.empty()) ev += ", ";
        ev += event_kind_name(e.kind);
        if (FAILED(e.hr)) ev += "(" + wf::hr_str(e.hr) + ")";
    }
    r.mdf("| stream events | %s |", ev.empty() ? "-" : ev.c_str());
    r.md("");
}

void write_packet_csv_header(Report& r) {
    r.csv("stream,index,recv_qpc_100ns,qpc_100ns,device_position,cum_frames,frames,flags,peak");
}

void write_packet_csv(Report& r, const std::string& stream, const std::vector<PacketRecord>& p) {
    for (size_t i = 0; i < p.size(); ++i) {
        r.csv(fmt("%s,%zu,%lld,%lld,%llu,%llu,%u,0x%X,%.6f", stream.c_str(), i,
                  static_cast<long long>(p[i].recv_qpc_100ns),
                  static_cast<long long>(p[i].qpc_100ns),
                  static_cast<unsigned long long>(p[i].device_position),
                  static_cast<unsigned long long>(p[i].cum_frames), p[i].frames, p[i].flags,
                  static_cast<double>(p[i].peak)));
    }
}

std::string stats_json(const std::string& stream, const wf::StreamDiagnostics& diag,
                       const StreamStats& s) {
    std::string j = "{";
    j += "\"stream\":\"" + json_escape(stream) + "\",";
    j += fmt("\"opened\":%s,", diag.opened ? "true" : "false");
    j += fmt("\"init_attempt\":%d,", diag.init_attempt);
    j += "\"init_hr\":\"" + json_escape(wf::hr_str(diag.init_hr)) + "\",";
    j += fmt("\"event_driven\":%s,", diag.event_driven ? "true" : "false");
    j += "\"format\":\"" + json_escape(diag.format_text) + "\",";
    j += fmt("\"buffer_frames\":%u,", diag.buffer_frames);
    j += fmt("\"stream_flags\":%u,", diag.stream_flags);
    j += fmt("\"raw_requested\":%s,", diag.raw_requested ? "true" : "false");
    j += fmt("\"raw_accepted\":%s,", diag.raw_accepted ? "true" : "false");
    j += "\"raw_hr\":\"" + json_escape(wf::hr_str(diag.raw_hr)) + "\",";
    j += "\"notes\":\"" + json_escape(diag.notes) + "\",";
    j += fmt("\"packets\":%zu,", s.packets);
    j += fmt("\"frames\":%llu,", static_cast<unsigned long long>(s.frames));
    j += fmt("\"span_sec\":%.4f,", s.span_sec);
    j += fmt("\"packets_per_sec\":%.3f,", s.packets_per_sec);
    j += fmt("\"mean_frames\":%.3f,", s.mean_frames);
    j += fmt("\"min_frames\":%u,\"max_frames\":%u,", s.min_frames, s.max_frames);
    j += fmt("\"max_recv_gap_ms\":%.3f,", s.max_recv_gap_ms);
    j += fmt("\"fit_valid\":%s,", s.fit_valid ? "true" : "false");
    j += fmt("\"resid_p50_ms\":%.4f,\"resid_p95_ms\":%.4f,\"resid_max_ms\":%.4f,", s.resid_p50_ms,
             s.resid_p95_ms, s.resid_max_ms);
    j += fmt("\"slope_ppm\":%.3f,", s.slope_ppm);
    j += fmt("\"devpos_valid\":%s,\"devpos_rate_hz\":%.4f,\"devpos_ppm\":%.3f,",
             s.devpos_valid ? "true" : "false", s.devpos_rate_hz, s.devpos_ppm);
    j += fmt("\"timestamp_error\":%u,\"discontinuity\":%u,\"silent\":%u,", s.n_timestamp_error,
             s.n_discontinuity, s.n_silent);
    j += fmt("\"zero_packets\":%zu,\"zero_fraction\":%.4f,", s.zero_packets, s.zero_fraction);
    j += fmt("\"peak\":%.6f,", s.peak_max);
    j += fmt("\"qpc_all_nonzero\":%s,\"qpc_monotonic\":%s", s.qpc_all_nonzero ? "true" : "false",
             s.qpc_monotonic ? "true" : "false");
    j += "}";
    return j;
}

} // namespace probe
