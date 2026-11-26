// M2: does a loopback source keep delivering packets while nothing plays? Phase A runs
// both loopback kinds with no keep-alive, phase B repeats with a silent render stream.
// The answer decides keepalive_mode and whether the 0.5 s reference watchdog may fire.
#include "commands.h"
#include "wasapi/clock.h"
#include "wasapi/com_util.h"
#include "wasapi/device_loopback.h"
#include "wasapi/keepalive_render.h"
#include "wasapi/process_loopback.h"

#include <cstdio>

namespace probe {

int cmd_silence(const Args& a) {
    Report rep("silence", a.out_dir);
    rep.md("# Loopback behaviour during silence (M2)");
    rep.md("");
    rep.md(conditions_block(a.command_line));
    rep.md("**Manual condition:** nothing should be playing on this machine for the whole run. "
           "If the numbers below show signal, the report says so and the silence conclusion does "
           "not hold.");
    rep.md("");

    RecordingSink proc_sink("process-exclude", a.seconds * 2 + 4);
    RecordingSink dev_sink("device-loopback", a.seconds * 2 + 4);

    wf::ProcessLoopbackStream proc;
    wf::ProcessLoopbackOptions popt;
    popt.mode = wf::ProcessLoopbackMode::ExcludeTree;
    popt.rate = a.rate;
    popt.channels = 1;
    HRESULT proc_hr = proc.start(popt, &proc_sink);

    wf::DeviceLoopbackStream dev;
    wf::DeviceLoopbackOptions dopt;
    HRESULT dev_hr = dev.start(nullptr, dopt, &dev_sink);

    std::printf("process-exclude: %s\ndevice-loopback: %s (%s)\n",
                wf::hr_str(proc_hr).c_str(), wf::hr_str(dev_hr).c_str(), dev.device_name().c_str());
    std::printf("Please keep the machine silent now.\n");

    countdown("phase A (no keep-alive)", a.seconds);
    const int64_t t_switch = wf::qpc_now_100ns();

    wf::KeepAliveRender keepalive;
    HRESULT ka_hr = keepalive.start(nullptr);
    uint64_t ka_pos_start = 0, ka_freq = 0;
    int64_t ka_qpc = 0;
    keepalive.clock_position(&ka_pos_start, &ka_freq, &ka_qpc);
    std::printf("keep-alive render: %s (%s)\n", wf::hr_str(ka_hr).c_str(),
                keepalive.device_name().c_str());

    countdown("phase B (silent keep-alive render active)", a.seconds);
    uint64_t ka_pos_end = 0;
    keepalive.clock_position(&ka_pos_end, &ka_freq, &ka_qpc);

    proc.stop();
    dev.stop();
    keepalive.stop();

    const std::vector<PacketRecord> proc_a = packets_before(proc_sink.packets(), t_switch);
    const std::vector<PacketRecord> proc_b = packets_from(proc_sink.packets(), t_switch);
    const std::vector<PacketRecord> dev_a = packets_before(dev_sink.packets(), t_switch);
    const std::vector<PacketRecord> dev_b = packets_from(dev_sink.packets(), t_switch);

    const StreamStats proc_sa = compute_stats(proc_a, proc_sink.rate());
    const StreamStats proc_sb = compute_stats(proc_b, proc_sink.rate());
    const StreamStats dev_sa = compute_stats(dev_a, dev_sink.rate());
    const StreamStats dev_sb = compute_stats(dev_b, dev_sink.rate());

    rep.md("## Streams");
    rep.md("");
    rep.mdf("- Process loopback (exclude own tree): start %s, requested %u Hz mono float",
            wf::hr_str(proc_hr).c_str(), a.rate);
    rep.mdf("- Device loopback: start %s on **%s**", wf::hr_str(dev_hr).c_str(),
            dev.device_name().c_str());
    rep.mdf("- Keep-alive render: start %s on **%s**, %s", wf::hr_str(ka_hr).c_str(),
            keepalive.device_name().c_str(), keepalive.format_text().c_str());
    if (ka_freq) {
        const double advanced = static_cast<double>(ka_pos_end - ka_pos_start) /
                                static_cast<double>(ka_freq);
        rep.mdf("- Keep-alive `IAudioClock` advanced **%.2f s** during phase B (0 would mean a "
                "stalled render engine)", advanced);
    }
    rep.md("");

    rep.md("## Delivery per phase");
    rep.md("");
    rep.mdf("Each phase lasts %d s.", a.seconds);
    rep.md("");
    rep.md("| Phase | Stream | packets | packets/s | frames | all-zero packets | SILENT flags | "
           "largest gap | peak |");
    rep.md("|---|---|---|---|---|---|---|---|---|");
    auto row = [&](const char* phase, const char* name, const StreamStats& s) {
        rep.mdf("| %s | %s | %zu | %.1f | %llu | %zu (%.0f %%) | %u | %.0f ms | %.1f dBFS |",
                phase, name, s.packets, s.packets_per_sec,
                static_cast<unsigned long long>(s.frames), s.zero_packets,
                s.zero_fraction * 100.0, s.n_silent, s.max_recv_gap_ms, to_dbfs(s.peak_max));
    };
    row("A: no keep-alive", "process-exclude", proc_sa);
    row("A: no keep-alive", "device-loopback", dev_sa);
    row("B: keep-alive on", "process-exclude", proc_sb);
    row("B: keep-alive on", "device-loopback", dev_sb);
    rep.md("");

    write_stream_block(rep, "Process loopback, phase A", proc.diag(), proc_sa, proc_sink.events());
    write_stream_block(rep, "Device loopback, phase A", dev.diag(), dev_sa, dev_sink.events());

    // --- conclusions -----------------------------------------------------------------
    const double expected_pkts = static_cast<double>(a.seconds) * 20.0; // 20 ms buffer -> >= 50/s
    const bool proc_alive_a = proc_sa.packets > 0 &&
                              proc_sa.max_recv_gap_ms < 500.0 &&
                              static_cast<double>(proc_sa.packets) > expected_pkts * 0.5;
    const bool dev_alive_a = dev_sa.packets > 0 && dev_sa.max_recv_gap_ms < 500.0;
    const bool dev_alive_b = dev_sb.packets > 0 && dev_sb.max_recv_gap_ms < 500.0;
    const bool was_silent = proc_sa.peak_max < 1e-4 && dev_sa.peak_max < 1e-4 &&
                            proc_sb.peak_max < 1e-4 && dev_sb.peak_max < 1e-4;

    rep.md("## Conclusions");
    rep.md("");
    if (!was_silent) {
        rep.mdf("- **Something was playing during the run** (peak process-exclude %.1f dBFS, "
                "device-loopback %.1f dBFS). The silence conclusions below are not valid; repeat "
                "with a silent machine.",
                to_dbfs(proc_sa.peak_max > proc_sb.peak_max ? proc_sa.peak_max : proc_sb.peak_max),
                to_dbfs(dev_sa.peak_max > dev_sb.peak_max ? dev_sa.peak_max : dev_sb.peak_max));
    } else {
        rep.md("- The run was silent (no stream exceeded -80 dBFS), so the delivery numbers above "
               "describe genuine silence.");
    }
    // "largest gap" is meaningless with zero packets, so say so explicitly.
    auto delivery = [](const StreamStats& s) {
        return s.packets == 0 ? std::string("**delivered nothing at all** (0 packets)")
                              : fmt("delivered **%zu packets**, largest gap %.0f ms", s.packets,
                                    s.max_recv_gap_ms);
    };
    rep.mdf("- Process loopback without a keep-alive: %s -> **%s** during silence.",
            delivery(proc_sa).c_str(), proc_alive_a ? "keeps running" : "stalls");
    rep.mdf("- Device loopback without a keep-alive: %s -> **%s** during silence.",
            delivery(dev_sa).c_str(), dev_alive_a ? "keeps running" : "stalls");
    rep.mdf("- Device loopback with the silent keep-alive render stream: %s -> **%s**.",
            delivery(dev_sb).c_str(), dev_alive_b ? "keeps running" : "stalls");
    rep.mdf("- Recommended `keepalive_mode`: **%d** (0 = none, 1 = silent render stream, "
            "2 = clock-driven zeros).",
            proc_alive_a ? 0 : (dev_alive_b && !dev_alive_a ? 1 : 2));
    rep.mdf("- Reference watchdog (0.5 s without packets): %s for the process-loopback reference.",
            proc_alive_a ? "safe to keep enabled" : "**must be suppressed or keep-alive gated**");
    rep.md("");

    write_packet_csv_header(rep);
    write_packet_csv(rep, "phaseA/process-exclude", proc_a);
    write_packet_csv(rep, "phaseA/device-loopback", dev_a);
    write_packet_csv(rep, "phaseB/process-exclude", proc_b);
    write_packet_csv(rep, "phaseB/device-loopback", dev_b);

    std::string json = "{\"measurement\":\"silence\",\"seconds_per_phase\":" + fmt("%d", a.seconds) +
                       ",\"windows\":\"" + json_escape(windows_build()) + "\",\"phases\":{\"a\":[" +
                       stats_json("process-exclude", proc.diag(), proc_sa) + "," +
                       stats_json("device-loopback", dev.diag(), dev_sa) + "],\"b\":[" +
                       stats_json("process-exclude", proc.diag(), proc_sb) + "," +
                       stats_json("device-loopback", dev.diag(), dev_sb) + "]}," +
                       "\"process_loopback_delivers_in_silence\":" +
                       (proc_alive_a ? "true" : "false") + ",\"machine_was_silent\":" +
                       (was_silent ? "true" : "false") + "}";
    rep.set_json(json);

    const std::string detail =
        fmt("silence: process-exclude %zu pkt (A, gap %.0f ms) / %zu pkt (B); device-loopback "
            "%zu pkt (A) / %zu pkt (B, gap %.0f ms); machine %s",
            proc_sa.packets, proc_sa.max_recv_gap_ms, proc_sb.packets, dev_sa.packets,
            dev_sb.packets, dev_sb.max_recv_gap_ms, was_silent ? "silent" : "NOT silent");
    rep.finish(was_silent ? "INFO" : "WARN", detail);
    std::printf("%s\n", detail.c_str());
    return 0;
}

} // namespace probe
