// M1: run the mic and the chosen reference stream(s) at the same time and compare their
// QPC timestamps against the delivered sample count. Pass/fail follows the plan:
// residual p95 < 3 ms, zero TIMESTAMP_ERROR, cross-stream drift < 20 ppm.
#include "commands.h"
#include "wasapi/com_util.h"
#include "wasapi/device_loopback.h"
#include "wasapi/mic_stream.h"
#include "wasapi/process_loopback.h"

#include <cstdio>

namespace probe {

int cmd_jitter(const Args& a) {
    const bool want_proc = (a.ref == "both" || a.ref == "process-exclude");
    const bool want_dev = (a.ref == "both" || a.ref == "device-loopback");
    if (!want_proc && !want_dev) {
        std::printf("--ref must be process-exclude, device-loopback or both\n");
        return 2;
    }

    Report rep("jitter", a.out_dir);
    rep.md("# WASAPI timestamp jitter and drift (M1)");
    rep.md("");
    rep.md(conditions_block(a.command_line));
    rep.md("Criteria from the plan: QPC residual **p95 < 3 ms**, **0 TIMESTAMP_ERROR**, "
           "cross-stream drift **< 20 ppm**. Failing any of them means `timestamp_mode=1` "
           "(device-position PLL) instead of raw QPC anchoring.");
    rep.md("");

    RecordingSink mic_sink("mic", a.seconds);
    RecordingSink proc_sink("process-exclude", a.seconds);
    RecordingSink dev_sink("device-loopback", a.seconds);

    wf::MicStream mic;
    wf::MicOptions mopt;
    mopt.prefer_raw = true;
    mopt.use_communications_category = false;
    HRESULT mic_hr = mic.start(a.mic.empty() ? nullptr : a.mic.c_str(), mopt, &mic_sink);

    wf::ProcessLoopbackStream proc;
    HRESULT proc_hr = S_FALSE;
    if (want_proc) {
        wf::ProcessLoopbackOptions popt;
        popt.mode = wf::ProcessLoopbackMode::ExcludeTree;
        popt.rate = a.rate;
        popt.channels = 1;
        proc_hr = proc.start(popt, &proc_sink);
    }

    wf::DeviceLoopbackStream dev;
    HRESULT dev_hr = S_FALSE;
    if (want_dev) {
        wf::DeviceLoopbackOptions dopt;
        dev_hr = dev.start(nullptr, dopt, &dev_sink);
    }

    std::printf("mic:             %s (%s)\n", SUCCEEDED(mic_hr) ? "started" : "FAILED",
                wf::hr_str(mic_hr).c_str());
    if (want_proc)
        std::printf("process-exclude: %s (%s)\n", SUCCEEDED(proc_hr) ? "started" : "FAILED",
                    wf::hr_str(proc_hr).c_str());
    if (want_dev)
        std::printf("device-loopback: %s (%s)\n", SUCCEEDED(dev_hr) ? "started" : "FAILED",
                    wf::hr_str(dev_hr).c_str());
    std::printf("Play some audio so the reference streams carry signal.\n");

    countdown("measuring", a.seconds);

    mic.stop();
    if (want_proc) proc.stop();
    if (want_dev) dev.stop();

    rep.md("## Streams");
    rep.md("");
    rep.mdf("- Microphone: **%s** (`%s`)", mic.device_name().c_str(),
            wf::narrow(mic.device_id().c_str()).c_str());
    rep.mdf("- Start HRESULTs: mic %s%s%s", wf::hr_str(mic_hr).c_str(),
            want_proc ? (", process-exclude " + wf::hr_str(proc_hr)).c_str() : "",
            want_dev ? (", device-loopback " + wf::hr_str(dev_hr)).c_str() : "");
    if (want_dev) rep.mdf("- Device loopback endpoint: **%s**", dev.device_name().c_str());
    rep.md("");

    const StreamStats mic_s = compute_stats(mic_sink.packets(), mic_sink.rate());
    const StreamStats proc_s = compute_stats(proc_sink.packets(), proc_sink.rate());
    const StreamStats dev_s = compute_stats(dev_sink.packets(), dev_sink.rate());

    rep.md("## Per-stream results");
    rep.md("");
    write_stream_block(rep, "Microphone", mic.diag(), mic_s, mic_sink.events());
    if (want_proc)
        write_stream_block(rep, "Process loopback (exclude own process tree)", proc.diag(), proc_s,
                           proc_sink.events());
    if (want_dev)
        write_stream_block(rep, "Device loopback (default render endpoint)", dev.diag(), dev_s,
                           dev_sink.events());

    // --- cross-stream drift -----------------------------------------------------------
    rep.md("## Cross-stream clock drift");
    rep.md("");
    rep.md("Both streams timestamp with the same QPC, so the difference of their "
           "QPC-vs-samples slopes is the drift the resampler has to absorb.");
    rep.md("");
    rep.md("| Pair | Drift | Within 20 ppm |");
    rep.md("|---|---|---|");
    bool drift_ok = true;
    auto drift_row = [&](const char* name, const StreamStats& ref, bool enabled) {
        if (!enabled || !mic_s.fit_valid || !ref.fit_valid) {
            if (enabled) rep.mdf("| mic vs %s | not measurable | - |", name);
            return;
        }
        const double d = mic_s.slope_ppm - ref.slope_ppm;
        const bool ok = (d > -20.0 && d < 20.0);
        drift_ok = drift_ok && ok;
        rep.mdf("| mic vs %s | %+.1f ppm | %s |", name, d, ok ? "yes" : "**no**");
    };
    drift_row("process-exclude", proc_s, want_proc);
    drift_row("device-loopback", dev_s, want_dev);
    rep.md("");

    // --- verdict ----------------------------------------------------------------------
    struct Row { const char* name; const StreamStats* s; bool on; };
    const Row rows[] = {{"mic", &mic_s, true},
                        {"process-exclude", &proc_s, want_proc},
                        {"device-loopback", &dev_s, want_dev}};
    bool p95_ok = true, ts_ok = true, any_data = false;
    rep.md("## Verdict");
    rep.md("");
    rep.md("| Stream | packets | p95 residual | TIMESTAMP_ERROR | QPC non-zero | Pass |");
    rep.md("|---|---|---|---|---|---|");
    for (const Row& row : rows) {
        if (!row.on) continue;
        const StreamStats& s = *row.s;
        if (s.packets == 0) {
            rep.mdf("| %s | 0 | - | - | - | **no data** |", row.name);
            p95_ok = false;
            continue;
        }
        any_data = true;
        const bool ok = s.fit_valid && s.resid_p95_ms < 3.0 && s.n_timestamp_error == 0 &&
                        s.qpc_all_nonzero;
        p95_ok = p95_ok && (s.fit_valid && s.resid_p95_ms < 3.0);
        ts_ok = ts_ok && (s.n_timestamp_error == 0);
        rep.mdf("| %s | %zu | %.3f ms | %u | %s | %s |", row.name, s.packets, s.resid_p95_ms,
                s.n_timestamp_error, s.qpc_all_nonzero ? "yes" : "NO", ok ? "yes" : "**no**");
    }
    rep.md("");
    const bool pass = any_data && p95_ok && ts_ok && drift_ok;
    rep.mdf("**Result: %s** -> recommended `timestamp_mode` = **%d** (0 = raw QPC anchoring, "
            "1 = smoothed device-position PLL).",
            pass ? "PASS" : "FAIL", pass ? 0 : 1);
    rep.md("");

    write_packet_csv_header(rep);
    write_packet_csv(rep, "mic", mic_sink.packets());
    if (want_proc) write_packet_csv(rep, "process-exclude", proc_sink.packets());
    if (want_dev) write_packet_csv(rep, "device-loopback", dev_sink.packets());

    std::string json = "{\"measurement\":\"jitter\",\"seconds\":" + fmt("%d", a.seconds) +
                       ",\"windows\":\"" + json_escape(windows_build()) + "\",\"streams\":[";
    json += stats_json("mic", mic.diag(), mic_s);
    if (want_proc) json += "," + stats_json("process-exclude", proc.diag(), proc_s);
    if (want_dev) json += "," + stats_json("device-loopback", dev.diag(), dev_s);
    json += "],\"pass\":" + std::string(pass ? "true" : "false") + "}";
    rep.set_json(json);

    const std::string detail =
        fmt("mic p95 %.2f ms (%zu pkt), process-exclude p95 %.2f ms (%zu pkt), device-loopback "
            "p95 %.2f ms (%zu pkt), ts-errors %u/%u/%u",
            mic_s.resid_p95_ms, mic_s.packets, proc_s.resid_p95_ms, proc_s.packets,
            dev_s.resid_p95_ms, dev_s.packets, mic_s.n_timestamp_error, proc_s.n_timestamp_error,
            dev_s.n_timestamp_error);
    rep.finish(pass ? "PASS" : "FAIL", detail);
    std::printf("%s\n%s\n", pass ? "PASS" : "FAIL", detail.c_str());
    return pass ? 0 : 1;
}

} // namespace probe
