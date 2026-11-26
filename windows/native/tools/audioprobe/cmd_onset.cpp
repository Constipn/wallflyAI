// Onset: what the process-loopback exclude stream (the Others / echo-reference source of
// others_mode 0) delivers when ANOTHER process starts and stops playing, packet by packet,
// next to the RAW mic exactly as a session opens it. Also logs every IMMNotificationClient
// event with its property key, because the engine's mic format-change filter depends on
// which endpoint and which key fire while audio plays.
//
// The player must not be in this process tree (exclude mode would hide it), so it is
// started by the caller, e.g. from PowerShell:
//   Start-Process powershell -ArgumentList '-File','play.ps1','-DelayMs','2000',...
//   wallfly-audioprobe onset --seconds 8
// Per packet: receive time, pu64QPCPosition, frames, flags, pu64DevicePosition and the
// derived deltas (QPC step vs. the step the previous packet's frame count predicts,
// receive interval, receive-minus-QPC lag).
#include "commands.h"
#include "wasapi/clock.h"
#include "wasapi/com_util.h"
#include "wasapi/enumerator.h"
#include "wasapi/keepalive_render.h"
#include "wasapi/mic_stream.h"
#include "wasapi/notification_client.h"
#include "wasapi/process_loopback.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <map>
#include <vector>

namespace probe {
namespace {

struct NoteLog {
    CRITICAL_SECTION cs;
    std::vector<wf::DeviceNotification> items;
    NoteLog() { InitializeCriticalSection(&cs); }
    ~NoteLog() { DeleteCriticalSection(&cs); }
};

void on_note(void* ctx, const wf::DeviceNotification& n) {
    NoteLog* log = static_cast<NoteLog*>(ctx);
    EnterCriticalSection(&log->cs);
    if (log->items.size() < 8192) log->items.push_back(n);
    LeaveCriticalSection(&log->cs);
}

struct Row {
    size_t index = 0;
    double t_ms = 0.0;         // receive time relative to the probe start
    int64_t recv = 0, qpc = 0;
    double dq_ms = 0.0;        // QPC step to this packet
    double dexp_ms = 0.0;      // step the previous packet's frame count predicts
    double step_err_ms = 0.0;  // dq - dexp
    double dr_ms = 0.0;        // receive interval
    double lag_ms = 0.0;       // receive time - QPC
    uint32_t frames = 0, flags = 0;
    uint64_t devpos = 0;
    int64_t ddevpos = 0;
    float peak = 0.0f;
};

std::vector<Row> derive(const std::vector<PacketRecord>& p, uint32_t rate, int64_t t0) {
    std::vector<Row> rows;
    rows.reserve(p.size());
    for (size_t i = 0; i < p.size(); ++i) {
        Row r;
        r.index = i;
        r.t_ms = static_cast<double>(p[i].recv_qpc_100ns - t0) / 1e4;
        r.recv = p[i].recv_qpc_100ns;
        r.qpc = p[i].qpc_100ns;
        r.frames = p[i].frames;
        r.flags = p[i].flags;
        r.devpos = p[i].device_position;
        r.peak = p[i].peak;
        r.lag_ms = static_cast<double>(r.recv - r.qpc) / 1e4;
        if (i > 0) {
            r.dq_ms = static_cast<double>(p[i].qpc_100ns - p[i - 1].qpc_100ns) / 1e4;
            r.dexp_ms = rate ? static_cast<double>(p[i - 1].frames) * 1000.0 / rate : 0.0;
            r.step_err_ms = r.dq_ms - r.dexp_ms;
            r.dr_ms = static_cast<double>(p[i].recv_qpc_100ns - p[i - 1].recv_qpc_100ns) / 1e4;
            r.ddevpos = static_cast<int64_t>(p[i].device_position) -
                        static_cast<int64_t>(p[i - 1].device_position);
        }
        rows.push_back(r);
    }
    return rows;
}

std::string row_text(const Row& r) {
    return fmt("%5zu t=%8.1f qpc=%lld dq=%7.2f exp=%6.2f err=%+8.2f dr=%6.1f lag=%7.2f "
               "fr=%4u fl=0x%X pos=%llu dpos=%lld pk=%.4f",
               r.index, r.t_ms, static_cast<long long>(r.qpc), r.dq_ms, r.dexp_ms, r.step_err_ms,
               r.dr_ms, r.lag_ms, r.frames, r.flags, static_cast<unsigned long long>(r.devpos),
               static_cast<long long>(r.ddevpos), static_cast<double>(r.peak));
}

bool anomalous(const Row& r, uint32_t modal_frames, double median_lag) {
    if (r.index == 0) return true;
    return std::fabs(r.step_err_ms) > 0.05 || r.frames != modal_frames || r.flags != 0 ||
           r.dr_ms > 20.0 || std::fabs(r.lag_ms - median_lag) > 5.0 ||
           (r.ddevpos != 0 && r.ddevpos != static_cast<int64_t>(r.frames));
}

void dump_stream(Report& rep, const char* name, const std::vector<Row>& rows) {
    rep.mdf("## %s: %zu packets", name, rows.size());
    rep.md("");
    std::printf("\n=== %s: %zu packets ===\n", name, rows.size());
    if (rows.empty()) return;

    std::map<uint32_t, size_t> frame_hist;
    std::vector<double> lags;
    for (const Row& r : rows) {
        ++frame_hist[r.frames];
        lags.push_back(r.lag_ms);
    }
    uint32_t modal = 0;
    size_t best = 0;
    for (const auto& kv : frame_hist) {
        if (kv.second > best) { best = kv.second; modal = kv.first; }
    }
    std::sort(lags.begin(), lags.end());
    const double median_lag = lags[lags.size() / 2];

    std::string hist;
    for (const auto& kv : frame_hist) hist += fmt("%u x%zu  ", kv.first, kv.second);
    rep.mdf("- frames per packet: %s", hist.c_str());
    rep.mdf("- receive-minus-QPC lag: min %.2f / median %.2f / max %.2f ms", lags.front(),
            median_lag, lags.back());
    std::printf("frames/packet: %s\nlag min %.2f median %.2f max %.2f ms\n", hist.c_str(),
                lags.front(), median_lag, lags.back());

    size_t first_sig = rows.size(), last_sig = rows.size();
    for (size_t i = 0; i < rows.size(); ++i) {
        if (rows[i].peak > 0.001f) {
            if (first_sig == rows.size()) first_sig = i;
            last_sig = i;
        }
    }
    if (first_sig < rows.size()) {
        rep.mdf("- first packet with signal: #%zu at %.1f ms, last: #%zu at %.1f ms", first_sig,
                rows[first_sig].t_ms, last_sig, rows[last_sig].t_ms);
        std::printf("first signal #%zu at %.1f ms, last signal #%zu at %.1f ms\n", first_sig,
                    rows[first_sig].t_ms, last_sig, rows[last_sig].t_ms);
    }

    double max_dr = 0.0, sum_err = 0.0;
    size_t n_anom = 0;
    rep.md("");
    rep.md("### Anomalous packets (QPC step != frames/rate, frame count, flags, receive gap "
           "> 20 ms, lag off the median by > 5 ms, device position jump)");
    rep.md("");
    rep.md("```");
    std::printf("--- anomalies ---\n");
    for (const Row& r : rows) {
        if (r.dr_ms > max_dr) max_dr = r.dr_ms;
        if (r.index > 0) sum_err += r.step_err_ms;
        if (!anomalous(r, modal, median_lag)) continue;
        ++n_anom;
        if (n_anom <= 200) {
            rep.md(row_text(r));
            if (n_anom <= 80) std::printf("%s\n", row_text(r).c_str());
        }
    }
    rep.md("```");
    rep.mdf("- %zu anomalous packets, largest receive gap %.1f ms, summed QPC step error %+.2f ms",
            n_anom, max_dr, sum_err);
    std::printf("%zu anomalous packets, largest receive gap %.1f ms, summed QPC step error "
                "%+.2f ms\n", n_anom, max_dr, sum_err);

    auto window = [&](const char* title, size_t centre, size_t before, size_t after) {
        if (centre >= rows.size()) return;
        const size_t lo = centre > before ? centre - before : 0;
        const size_t hi = std::min(rows.size(), centre + after);
        rep.md("");
        rep.mdf("### %s (packets %zu..%zu)", title, lo, hi - 1);
        rep.md("");
        rep.md("```");
        std::printf("--- %s ---\n", title);
        for (size_t i = lo; i < hi; ++i) {
            rep.md(row_text(rows[i]));
            std::printf("%s\n", row_text(rows[i]).c_str());
        }
        rep.md("```");
    };
    window("onset window", first_sig, 12, 30);
    window("stop window", last_sig, 4, 30);
    rep.md("");
}

}  // namespace

int cmd_onset(const Args& a) {
    Report rep("onset", a.out_dir);
    rep.md("# Process-loopback exclude stream at playback onset / stop");
    rep.md("");
    rep.md(conditions_block(a.command_line));
    rep.md("**Manual condition:** a process OUTSIDE this process tree starts playing about 2 s "
           "after the probe starts and stops about 3 s later.");
    rep.md("");

    NoteLog log;
    wf::NotificationClient notifier;
    const HRESULT note_hr = notifier.register_with_system(&on_note, &log);

    RecordingSink proc_sink("process-exclude", a.seconds + 2);
    RecordingSink mic_sink("mic", a.seconds + 2);
    const int64_t t0 = wf::qpc_now_100ns();

    wf::KeepAliveRender keepalive;
    if (a.keepalive) keepalive.start(nullptr);

    wf::ProcessLoopbackStream proc;
    wf::ProcessLoopbackOptions popt;
    popt.mode = wf::ProcessLoopbackMode::ExcludeTree;
    popt.rate = a.rate;
    popt.channels = 1;
    popt.buffer_ms = static_cast<uint32_t>(a.buffer_ms > 0 ? a.buffer_ms : 20);
    const HRESULT proc_hr = proc.start(popt, &proc_sink);

    wf::MicStream mic;
    wf::MicOptions mopt;
    mopt.prefer_raw = true;
    const HRESULT mic_hr =
        a.no_mic ? S_FALSE : mic.start(a.mic.empty() ? nullptr : a.mic.c_str(), mopt, &mic_sink);

    std::printf("process-exclude %s, mic %s (%s), notifications %s\n", wf::hr_str(proc_hr).c_str(),
                wf::hr_str(mic_hr).c_str(), mic.diag().format_text.c_str(),
                wf::hr_str(note_hr).c_str());

    // The mic format-change check of the session (RealPlant::mic_format_changed) re-reads
    // the format the same way the stream was opened; a plain GetMixFormat without the RAW
    // option is what the engine compared before 2026-11-01.
    if (SUCCEEDED(mic_hr)) {
        wf::ComPtr<IMMDevice> dev;
        wf::PcmFormat same_way{}, plain{};
        HRESULT q1 = E_FAIL, q2 = E_FAIL;
        if (SUCCEEDED(wf::open_endpoint(mic.device_id().c_str(), &dev))) {
            q1 = wf::query_capture_format(dev.get(), AudioCategory_Other, mic.diag().raw_accepted,
                                          &same_way);
            q2 = wf::query_capture_format(dev.get(), AudioCategory_Other, false, &plain);
        }
        const std::string line = fmt(
            "mic stream %u Hz %u ch; re-read like the open (RAW %s): %u Hz %u ch -> %s; plain "
            "mix format: %u Hz %u ch -> %s",
            mic.diag().format.rate, mic.diag().format.channels,
            mic.diag().raw_accepted ? "on" : "off", same_way.rate, same_way.channels,
            SUCCEEDED(q1) && wf::same_pcm_format(same_way, mic.diag().format) ? "same" : "DIFFERENT",
            plain.rate, plain.channels,
            SUCCEEDED(q2) && wf::same_pcm_format(plain, mic.diag().format) ? "same" : "DIFFERENT");
        rep.mdf("- %s", line.c_str());
        std::printf("%s\n", line.c_str());
    }
    // --restart-mic-ms: reopen the RAW mic periodically, as a session's mic rebuild does, to
    // see which endpoint notifications a mic (re)start itself produces.
    // --keepalive: the render clock of our own silent stream, sampled every ~5 ms; a
    // position that stops advancing marks an engine-wide stall (spec 3.3 watchdog gate).
    std::vector<int64_t> mic_restarts;
    std::vector<std::string> clock_stalls;
    {
        const int64_t end = t0 + static_cast<int64_t>(a.seconds) * 10000000;
        int64_t next = t0 + static_cast<int64_t>(a.restart_mic_ms) * 10000;
        uint64_t last_pos = 0, freq = 0;
        int64_t last_move = 0, sampled_qpc = 0;
        while (wf::qpc_now_100ns() < end) {
            Sleep(5);
            const int64_t now = wf::qpc_now_100ns();
            if (a.keepalive && keepalive.running()) {
                uint64_t pos = 0;
                if (SUCCEEDED(keepalive.clock_position(&pos, &freq, &sampled_qpc))) {
                    if (pos != last_pos || last_move == 0) {
                        if (last_move != 0 && now - last_move > 300000) {
                            clock_stalls.push_back(
                                fmt("keep-alive clock stood still %.1f ms (until t=%.1f ms)",
                                    static_cast<double>(now - last_move) / 1e4,
                                    static_cast<double>(now - t0) / 1e4));
                        }
                        last_pos = pos;
                        last_move = now;
                    }
                }
            }
            if (a.restart_mic_ms > 0 && !a.no_mic && now >= next) {
                mic.stop();
                mic.start(a.mic.empty() ? nullptr : a.mic.c_str(), mopt, &mic_sink);
                mic_restarts.push_back(wf::qpc_now_100ns());
                next += static_cast<int64_t>(a.restart_mic_ms) * 10000;
            }
        }
    }
    for (const std::string& s : clock_stalls) {
        rep.mdf("- %s", s.c_str());
        std::printf("%s\n", s.c_str());
    }
    if (a.keepalive && clock_stalls.empty()) {
        rep.md("- keep-alive clock never stood still for more than 30 ms");
        std::printf("keep-alive clock never stood still for more than 30 ms\n");
    }
    proc.stop();
    mic.stop();
    keepalive.stop();
    notifier.unregister();

    rep.mdf("- process-exclude: %s, %s, buffer %u frames, flags 0x%08X",
            wf::hr_str(proc_hr).c_str(), proc.diag().format_text.c_str(),
            proc.diag().buffer_frames, proc.diag().stream_flags);
    rep.mdf("- mic: %s, %s (RAW %s), mic endpoint `%s`", wf::hr_str(mic_hr).c_str(),
            mic.diag().format_text.c_str(), mic.diag().raw_accepted ? "accepted" : "no",
            wf::narrow(mic.device_id().c_str()).c_str());
    rep.md("");

    const std::vector<Row> prow = derive(proc_sink.packets(), proc_sink.rate(), t0);
    const std::vector<Row> mrow = derive(mic_sink.packets(), mic_sink.rate(), t0);
    dump_stream(rep, "process-exclude", prow);
    dump_stream(rep, "mic", mrow);

    // --- notifications ------------------------------------------------------------
    EnterCriticalSection(&log.cs);
    const std::vector<wf::DeviceNotification> notes = log.items;
    LeaveCriticalSection(&log.cs);
    std::map<std::string, size_t> by_key;
    rep.mdf("## Device notifications: %zu", notes.size());
    rep.md("");
    rep.md("```");
    std::printf("\n=== %zu device notifications ===\n", notes.size());
    for (size_t i = 0; i < notes.size(); ++i) {
        const wf::DeviceNotification& n = notes[i];
        const bool is_mic = (n.device_id == mic.device_id());
        std::string what = wf::notification_kind_name(n.kind);
        if (n.kind == wf::DeviceNotification::PropertyChanged) {
            what += " " + wf::property_key_text(n.key);
            ++by_key[std::string(is_mic ? "mic    " : "other  ") + wf::property_key_text(n.key) +
                     "  " + wf::narrow(n.device_id.c_str())];
        }
        const std::string line =
            fmt("t=%8.1f ms %s %s %s", static_cast<double>(n.qpc_100ns - t0) / 1e4,
                is_mic ? "MIC  " : "other", what.c_str(), wf::narrow(n.device_id.c_str()).c_str());
        if (i < 400) rep.md(line);
        if (i < 40) std::printf("%s\n", line.c_str());
    }
    rep.md("```");
    rep.md("");
    if (!mic_restarts.empty()) {
        std::string list;
        for (int64_t t : mic_restarts) list += fmt("%.1f ", static_cast<double>(t - t0) / 1e4);
        rep.mdf("- mic reopened at (ms): %s", list.c_str());
        std::printf("mic reopened at (ms): %s\n", list.c_str());
    }
    rep.md("| endpoint | key | count |");
    rep.md("|---|---|---|");
    std::printf("--- property changes by endpoint and key ---\n");
    for (const auto& kv : by_key) {
        rep.mdf("| %s | %zu |", kv.first.c_str(), kv.second);
        std::printf("%5zu  %s\n", kv.second, kv.first.c_str());
    }
    rep.md("");

    rep.csv("stream,index,t_ms,recv_qpc_100ns,qpc_100ns,dq_ms,expected_ms,step_err_ms,dr_ms,"
            "lag_ms,frames,flags,device_position,d_device_position,peak");
    auto csv_rows = [&](const char* name, const std::vector<Row>& rows) {
        for (const Row& r : rows) {
            rep.csv(fmt("%s,%zu,%.3f,%lld,%lld,%.4f,%.4f,%.4f,%.3f,%.3f,%u,0x%X,%llu,%lld,%.6f",
                        name, r.index, r.t_ms, static_cast<long long>(r.recv),
                        static_cast<long long>(r.qpc), r.dq_ms, r.dexp_ms, r.step_err_ms,
                        r.dr_ms, r.lag_ms, r.frames, r.flags,
                        static_cast<unsigned long long>(r.devpos),
                        static_cast<long long>(r.ddevpos), static_cast<double>(r.peak)));
        }
    };
    csv_rows("process-exclude", prow);
    csv_rows("mic", mrow);
    rep.set_json(fmt("{\"measurement\":\"onset\",\"seconds\":%d,\"process_packets\":%zu,"
                     "\"mic_packets\":%zu,\"notifications\":%zu}",
                     a.seconds, prow.size(), mrow.size(), notes.size()));
    rep.finish("INFO", fmt("%zu process-exclude packets, %zu notifications", prow.size(),
                           notes.size()));
    return 0;
}

}  // namespace probe
