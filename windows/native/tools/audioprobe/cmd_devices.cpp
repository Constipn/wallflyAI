// M3: every endpoint with its mix format and RawProcessingSupported, plus the result of
// actually opening each active capture endpoint with AUDCLNT_STREAMOPTIONS_RAW -- the
// property and the open can disagree, and only the open decides what the mic gets.
#include "commands.h"
#include "wasapi/com_util.h"
#include "wasapi/enumerator.h"
#include "wasapi/process_loopback.h"

#include <cstdio>

namespace probe {
namespace {

class NullSink final : public wf::IPacketSink {
public:
    void on_packet(const wf::WfPacket&) override {}
    void on_event(const wf::StreamEvent&) override {}
};

const char* tri(int v) { return v < 0 ? "unknown" : (v ? "yes" : "no"); }

void endpoint_table(Report& r, const std::vector<wf::EndpointInfo>& list, const char* title) {
    r.md(std::string("### ") + title);
    r.md("");
    r.md("| Name | State | Default | Mix format | RawProcessingSupported | Endpoint id |");
    r.md("|---|---|---|---|---|---|");
    for (const wf::EndpointInfo& e : list) {
        std::string def;
        if (e.is_default_console) def += "console ";
        if (e.is_default_multimedia) def += "multimedia ";
        if (e.is_default_communications) def += "communications";
        if (def.empty()) def = "-";
        // Mix format is only queried for active endpoints; inactive ones show "-".
        const std::string mix = !e.mix_format.empty() ? e.mix_format
                                : (e.state == DEVICE_STATE_ACTIVE ? wf::hr_str(e.mix_hr) : "-");
        r.mdf("| %s | %s | %s | %s | %s | `%s` |", e.name.c_str(),
              wf::device_state_name(e.state), def.c_str(), mix.c_str(),
              tri(e.raw_supported), wf::narrow(e.id.c_str()).c_str());
    }
    r.md("");
}

} // namespace

int cmd_devices(const Args& a) {
    Report rep("devices", a.out_dir);
    rep.md("# WASAPI endpoints and RAW support (M3)");
    rep.md("");
    rep.md(conditions_block(a.command_line));

    std::vector<wf::EndpointInfo> render, capture;
    HRESULT hr_r = wf::list_endpoints(eRender, DEVICE_STATEMASK_ALL, &render);
    HRESULT hr_c = wf::list_endpoints(eCapture, DEVICE_STATEMASK_ALL, &capture);
    if (FAILED(hr_r) || FAILED(hr_c)) {
        rep.mdf("Endpoint enumeration failed: render %s, capture %s", wf::hr_str(hr_r).c_str(),
                wf::hr_str(hr_c).c_str());
        rep.finish("FAIL", "endpoint enumeration failed");
        return 1;
    }

    rep.md("## Default endpoints");
    rep.md("");
    rep.md("| Flow | Role | Endpoint id |");
    rep.md("|---|---|---|");
    const struct { EDataFlow f; ERole r; const char* fn; const char* rn; } defaults[] = {
        {eCapture, eConsole, "capture", "console"},
        {eCapture, eMultimedia, "capture", "multimedia"},
        {eCapture, eCommunications, "capture", "communications"},
        {eRender, eConsole, "render", "console"},
        {eRender, eMultimedia, "render", "multimedia"},
        {eRender, eCommunications, "render", "communications"},
    };
    for (const auto& d : defaults) {
        std::wstring id;
        HRESULT hr = wf::default_endpoint_id(d.f, d.r, &id);
        rep.mdf("| %s | %s | %s |", d.fn, d.rn,
                SUCCEEDED(hr) ? ("`" + wf::narrow(id.c_str()) + "`").c_str()
                              : wf::hr_str(hr).c_str());
    }
    rep.md("");

    rep.md("## Endpoints");
    rep.md("");
    endpoint_table(rep, capture, "Capture");
    endpoint_table(rep, render, "Render");

    // --- actually open every active capture endpoint with RAW ------------------------
    rep.md("## Opening each active capture endpoint with AUDCLNT_STREAMOPTIONS_RAW");
    rep.md("");
    rep.md("Shared mode, `AudioCategory_Other`, 20 ms buffer, event callback requested.");
    rep.md("");
    rep.md("A negotiated format that differs from the endpoint's mix format means RAW actually "
           "bypassed the processing chain (the APO channel count and effects disappear).");
    rep.md("");
    rep.md("| Capture endpoint | Mix format | Open result | RAW | Callback | Negotiated format | Buffer |");
    rep.md("|---|---|---|---|---|---|---|");
    rep.csv("endpoint,state,raw_property,open_hr,raw_accepted,raw_hr,event_driven,format,buffer_frames");

    int raw_ok = 0, raw_total = 0;
    for (const wf::EndpointInfo& e : capture) {
        if (e.state != DEVICE_STATE_ACTIVE) continue;
        ++raw_total;
        wf::ComPtr<IMMDevice> dev;
        HRESULT hr = wf::open_endpoint(e.id.c_str(), &dev);
        wf::OpenedCapture opened;
        if (SUCCEEDED(hr)) {
            wf::OpenSpec spec;
            spec.prefer_raw = true;
            spec.prefer_event = true;
            spec.category = AudioCategory_Other;
            spec.buffer_ms = 20;
            hr = wf::open_capture_on_device(dev.get(), spec, &opened);
        }
        const wf::StreamDiagnostics& d = opened.diag;
        if (d.raw_accepted) ++raw_ok;
        rep.mdf("| %s | %s | %s | %s (%s) | %s | %s | %u frames |", e.name.c_str(),
                e.mix_format.empty() ? "-" : e.mix_format.c_str(),
                SUCCEEDED(hr) ? "ok" : wf::hr_str(hr).c_str(),
                d.raw_accepted ? "accepted" : "rejected", wf::hr_str(d.raw_hr).c_str(),
                d.event_driven ? "event" : "polling",
                d.format_text.empty() ? "-" : d.format_text.c_str(), d.buffer_frames);
        rep.csv(fmt("\"%s\",%s,%s,%s,%s,%s,%s,\"%s\",%u", e.name.c_str(),
                    wf::device_state_name(e.state), tri(e.raw_supported),
                    wf::hr_str(hr).c_str(), d.raw_accepted ? "yes" : "no",
                    wf::hr_str(d.raw_hr).c_str(), d.event_driven ? "yes" : "no",
                    d.format_text.c_str(), d.buffer_frames));
        opened.close();
    }
    rep.md("");

    // --- process loopback activation -------------------------------------------------
    rep.md("## Process loopback availability");
    rep.md("");
    const bool api = wf::process_loopback_api_available();
    rep.mdf("- `mmdevapi!ActivateAudioInterfaceAsync` resolved: **%s**", api ? "yes" : "no");

    NullSink null_sink;
    wf::ProcessLoopbackStream pl;
    wf::ProcessLoopbackOptions opt;
    opt.mode = wf::ProcessLoopbackMode::ExcludeTree;
    opt.rate = a.rate;
    opt.channels = 1;
    HRESULT pl_hr = pl.start(opt, &null_sink);
    if (SUCCEEDED(pl_hr)) Sleep(300);
    pl.stop();
    rep.mdf("- Exclude-mode activation + Initialize: **%s**", wf::hr_str(pl_hr).c_str());
    rep.mdf("- Activation HRESULT: %s", wf::hr_str(pl.activation_hr()).c_str());
    rep.mdf("- Accepted configuration: attempt %d, %s, %s, buffer %u frames",
            pl.diag().init_attempt, pl.diag().event_driven ? "event-driven" : "timer polling",
            pl.diag().format_text.empty() ? "-" : pl.diag().format_text.c_str(),
            pl.diag().buffer_frames);
    if (!pl.diag().notes.empty()) rep.mdf("- Rejected attempts: %s", pl.diag().notes.c_str());
    rep.md("");

    std::string json = "{\"measurement\":\"devices\",\"windows\":\"" +
                       json_escape(windows_build()) + "\",\"capture_endpoints\":[";
    bool first = true;
    for (const wf::EndpointInfo& e : capture) {
        if (!first) json += ",";
        first = false;
        json += "{\"name\":\"" + json_escape(e.name) + "\",\"id\":\"" +
                json_escape(wf::narrow(e.id.c_str())) + "\",\"state\":\"" +
                wf::device_state_name(e.state) + "\",\"default_console\":" +
                (e.is_default_console ? "true" : "false") + ",\"default_communications\":" +
                (e.is_default_communications ? "true" : "false") + ",\"raw_property\":\"" +
                tri(e.raw_supported) + "\",\"mix\":\"" + json_escape(e.mix_format) + "\"}";
    }
    json += "],\"process_loopback\":" + stats_json("process-exclude", pl.diag(), StreamStats{});
    json += "}";
    rep.set_json(json);

    size_t active_render = 0;
    for (const wf::EndpointInfo& e : render)
        if (e.state == DEVICE_STATE_ACTIVE) ++active_render;
    const std::string detail =
        fmt("%d active capture / %zu active render endpoints (%zu / %zu incl. absent); RAW accepted "
            "on %d of %d active capture endpoints; process loopback %s",
            raw_total, active_render, capture.size(), render.size(), raw_ok, raw_total,
            SUCCEEDED(pl_hr) ? "OK" : "FAILED");
    rep.finish(SUCCEEDED(pl_hr) ? "INFO" : "WARN", detail);
    std::printf("%s\n", detail.c_str());
    return 0;
}

} // namespace probe
