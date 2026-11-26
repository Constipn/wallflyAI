// M5: keep a process-loopback reference running while the user switches the default
// output or connects a Bluetooth headset, and log every IMMNotificationClient event with
// the packet gap it caused. Answers whether exclude mode survives an endpoint change.
#include "commands.h"
#include "wasapi/clock.h"
#include "wasapi/com_util.h"
#include "wasapi/enumerator.h"
#include "wasapi/notification_client.h"
#include "wasapi/process_loopback.h"

#include <cstdio>
#include <vector>

namespace probe {
namespace {

struct Log {
    CRITICAL_SECTION cs;
    std::vector<wf::DeviceNotification> items;
    Log() { InitializeCriticalSection(&cs); }
    ~Log() { DeleteCriticalSection(&cs); }
};

void on_notification(void* ctx, const wf::DeviceNotification& n) {
    Log* log = static_cast<Log*>(ctx);
    EnterCriticalSection(&log->cs);
    if (log->items.size() < 4096) log->items.push_back(n);
    LeaveCriticalSection(&log->cs);
}

} // namespace

int cmd_switch(const Args& a) {
    Report rep("switch", a.out_dir);
    rep.md("# Output-device switching with a process-loopback reference (M5)");
    rep.md("");
    rep.md(conditions_block(a.command_line));
    rep.md("**Manual step required:** while this runs, change the default playback device "
           "(Settings -> System -> Sound) and/or connect a Bluetooth headset, with audio playing. "
           "The report pairs each device event with the packet gap it produced.");
    rep.md("");

    std::vector<wf::EndpointInfo> before;
    wf::list_endpoints(eRender, DEVICE_STATE_ACTIVE, &before);
    std::wstring def_before;
    wf::default_endpoint_id(eRender, eConsole, &def_before);

    Log log;
    wf::NotificationClient notifier;
    HRESULT note_hr = notifier.register_with_system(&on_notification, &log);

    RecordingSink sink("process-exclude", a.seconds);
    wf::ProcessLoopbackStream proc;
    wf::ProcessLoopbackOptions popt;
    popt.mode = wf::ProcessLoopbackMode::ExcludeTree;
    popt.rate = a.rate;
    popt.channels = 1;
    const int64_t t0 = wf::qpc_now_100ns();
    HRESULT proc_hr = proc.start(popt, &sink);

    std::printf("process-exclude: %s, notifications: %s\n", wf::hr_str(proc_hr).c_str(),
                wf::hr_str(note_hr).c_str());
    std::printf("Switch the default output device / connect Bluetooth now.\n");
    countdown("measuring", a.seconds);

    proc.stop();
    notifier.unregister();

    std::wstring def_after;
    wf::default_endpoint_id(eRender, eConsole, &def_after);
    std::vector<wf::EndpointInfo> after;
    wf::list_endpoints(eRender, DEVICE_STATE_ACTIVE, &after);

    const StreamStats s = compute_stats(sink.packets(), sink.rate());

    rep.md("## Endpoints before and after");
    rep.md("");
    rep.mdf("- Default render endpoint before: `%s`", wf::narrow(def_before.c_str()).c_str());
    rep.mdf("- Default render endpoint after: `%s`", wf::narrow(def_after.c_str()).c_str());
    rep.mdf("- Default endpoint changed: **%s**", def_before == def_after ? "no" : "yes");
    rep.mdf("- Active render endpoints: %zu before, %zu after", before.size(), after.size());
    rep.md("");

    rep.md("## Device events");
    rep.md("");
    rep.mdf("Registration HRESULT: %s", wf::hr_str(note_hr).c_str());
    rep.md("");
    rep.md("| t (s) | Event | Flow | Role | State | Endpoint id |");
    rep.md("|---|---|---|---|---|---|");
    EnterCriticalSection(&log.cs);
    std::vector<wf::DeviceNotification> items = log.items;
    LeaveCriticalSection(&log.cs);
    for (const wf::DeviceNotification& n : items) {
        rep.mdf("| %.2f | %s | %s | %s | %s | `%s` |",
                static_cast<double>(n.qpc_100ns - t0) / 1e7, wf::notification_kind_name(n.kind),
                n.kind == wf::DeviceNotification::DefaultChanged ? wf::data_flow_name(n.flow) : "-",
                n.kind == wf::DeviceNotification::DefaultChanged ? wf::role_name(n.role) : "-",
                n.kind == wf::DeviceNotification::StateChanged
                    ? wf::device_state_name(n.state)
                    : "-",
                wf::narrow(n.device_id.c_str()).c_str());
    }
    if (items.empty()) rep.md("| - | (no device events during the run) | | | | |");
    rep.md("");

    rep.md("## Reference stream behaviour");
    rep.md("");
    write_stream_block(rep, "Process loopback (exclude own process tree)", proc.diag(), s,
                       sink.events());
    rep.mdf("- Largest gap between packets: **%.0f ms** (watchdog threshold is 500 ms).",
            s.max_recv_gap_ms);
    rep.mdf("- DATA_DISCONTINUITY flags: **%u**, TIMESTAMP_ERROR: **%u**.", s.n_discontinuity,
            s.n_timestamp_error);
    rep.mdf("- Format stayed **%s** for the whole run (the virtual device format is requested by "
            "us, not derived from the endpoint).",
            proc.diag().format_text.c_str());
    rep.md("");

    write_packet_csv_header(rep);
    write_packet_csv(rep, "process-exclude", sink.packets());

    rep.set_json("{\"measurement\":\"switch\",\"seconds\":" + fmt("%d", a.seconds) +
                 ",\"windows\":\"" + json_escape(windows_build()) + "\",\"device_events\":" +
                 fmt("%zu", items.size()) + ",\"default_changed\":" +
                 (def_before == def_after ? "false" : "true") + ",\"stream\":" +
                 stats_json("process-exclude", proc.diag(), s) + "}");

    const std::string detail =
        fmt("%zu device events, default %s, %zu packets, largest gap %.0f ms, %u discontinuities",
            items.size(), def_before == def_after ? "unchanged" : "changed", s.packets,
            s.max_recv_gap_ms, s.n_discontinuity);
    rep.finish(s.packets > 0 ? "INFO" : "FAIL", detail);
    std::printf("%s\n", detail.c_str());
    return 0;
}

} // namespace probe
