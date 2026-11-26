// M4: Windows ducks other playback when a stream opens on the default *communications*
// capture device. Phase A measures the render level with no capture stream, phase B with
// the mic open; the level is read twice over -- endpoint meter and loopback capture.
#include "commands.h"
#include "wasapi/clock.h"
#include "wasapi/com_util.h"
#include "wasapi/device_loopback.h"
#include "wasapi/enumerator.h"
#include "wasapi/mic_stream.h"
#include "wasapi/sessions.h"

#include <cmath>
#include <cstdio>

namespace probe {
namespace {

struct Level {
    size_t n = 0;
    double mean = 0.0;
    double max = 0.0;
};

Level mean_peak(const std::vector<PacketRecord>& p) {
    Level l;
    l.n = p.size();
    if (p.empty()) return l;
    double sum = 0.0;
    for (const PacketRecord& r : p) {
        sum += static_cast<double>(r.peak);
        if (r.peak > l.max) l.max = r.peak;
    }
    l.mean = sum / static_cast<double>(p.size());
    return l;
}

Level mean_peak(const std::vector<float>& v) {
    Level l;
    l.n = v.size();
    if (v.empty()) return l;
    double sum = 0.0;
    for (float f : v) {
        sum += f;
        if (f > l.max) l.max = f;
    }
    l.mean = sum / static_cast<double>(v.size());
    return l;
}

// Samples the render endpoint's own peak meter every 100 ms. Unlike loopback capture
// this keeps reporting while the endpoint is idle, so the phase always has data.
void sample_meter(const wchar_t* render_id, const char* what, int seconds, std::vector<float>* out,
                  HRESULT* last_hr) {
    const int ticks = seconds * 10;
    for (int i = 0; i < ticks; ++i) {
        float peak = -1.0f;
        *last_hr = wf::endpoint_peak(render_id, &peak);
        if (SUCCEEDED(*last_hr)) out->push_back(peak);
        if (i % 10 == 0) {
            std::printf("\r  %s: %d s remaining   ", what, seconds - i / 10);
            std::fflush(stdout);
        }
        Sleep(100);
    }
    std::printf("\r  %s: done                \n", what);
}

} // namespace

int cmd_ducking(const Args& a) {
    const bool comm_category = (a.category == "communications");
    const int half = a.seconds > 1 ? a.seconds / 2 : 1;

    Report rep("ducking", a.out_dir);
    rep.md("# Communication-device ducking (M4)");
    rep.md("");
    rep.md(conditions_block(a.command_line));
    rep.md("**Manual step required:** start continuous audio playback from another application "
           "(e.g. a music or video player) on the default render endpoint *before* this run, and "
           "leave it playing for the whole measurement. Without playback there is nothing that "
           "could be ducked and the result is meaningless.");
    rep.md("");
    rep.md("Windows triggers ducking from the *device* (default communications capture endpoint), "
           "not from the stream category; the category is varied with `--category` so both can be "
           "checked. The render level is read two ways: the endpoint's own peak meter "
           "(`IAudioMeterInformation`, sampled every 100 ms) and the loopback capture stream.");
    rep.md("");

    std::wstring comm_id, console_id, render_id;
    HRESULT comm_hr = wf::default_endpoint_id(eCapture, eCommunications, &comm_id);
    wf::default_endpoint_id(eCapture, eConsole, &console_id);
    wf::default_endpoint_id(eRender, eConsole, &render_id);

    RecordingSink loop_sink("device-loopback", a.seconds + 4);
    wf::DeviceLoopbackStream loop;
    wf::DeviceLoopbackOptions dopt;
    HRESULT loop_hr = loop.start(nullptr, dopt, &loop_sink);
    std::printf("device loopback on %s: %s\n", loop.device_name().c_str(),
                wf::hr_str(loop_hr).c_str());

    std::vector<float> meter_a, meter_b;
    HRESULT meter_hr = S_OK;
    sample_meter(render_id.c_str(), "phase A (no capture stream open)", half, &meter_a, &meter_hr);
    const int64_t t_switch = wf::qpc_now_100ns();

    RecordingSink mic_sink("mic", a.seconds + 4);
    wf::MicStream mic;
    wf::MicOptions mopt;
    mopt.prefer_raw = true;
    mopt.use_communications_category = comm_category;
    HRESULT mic_hr = FAILED(comm_hr) ? comm_hr : mic.start(comm_id.c_str(), mopt, &mic_sink);
    std::printf("mic on default communications capture endpoint: %s\n", wf::hr_str(mic_hr).c_str());

    sample_meter(render_id.c_str(), "phase B (mic open)", half, &meter_b, &meter_hr);

    mic.stop();
    loop.stop();

    const std::vector<PacketRecord> a_pkts = packets_before(loop_sink.packets(), t_switch);
    const std::vector<PacketRecord> b_pkts = packets_from(loop_sink.packets(), t_switch);
    const StreamStats as = compute_stats(a_pkts, loop_sink.rate());
    const StreamStats bs = compute_stats(b_pkts, loop_sink.rate());

    const Level ma = mean_peak(meter_a), mb = mean_peak(meter_b);
    const Level la = mean_peak(a_pkts), lb = mean_peak(b_pkts);
    const double meter_delta = to_dbfs(mb.mean) - to_dbfs(ma.mean);
    const double loop_delta = to_dbfs(lb.mean) - to_dbfs(la.mean);

    rep.md("## Configuration");
    rep.md("");
    rep.mdf("- Default **communications** capture endpoint: `%s` (%s)",
            wf::narrow(comm_id.c_str()).c_str(), wf::hr_str(comm_hr).c_str());
    rep.mdf("- Default **console** capture endpoint: `%s`", wf::narrow(console_id.c_str()).c_str());
    rep.mdf("- Same device for both roles: **%s** (if yes, the user's normal microphone is the "
            "ducking trigger)",
            comm_id == console_id ? "yes" : "no");
    rep.mdf("- Stream category: **%s**", comm_category ? "AudioCategory_Communications"
                                                       : "AudioCategory_Other");
    rep.mdf("- RAW requested: yes, %s (%s)", mic.diag().raw_accepted ? "accepted" : "rejected",
            wf::hr_str(mic.diag().raw_hr).c_str());
    rep.mdf("- Mic open result: %s", wf::hr_str(mic_hr).c_str());
    rep.mdf("- Render endpoint observed: **%s** (`%s`)", loop.device_name().c_str(),
            wf::narrow(render_id.c_str()).c_str());
    rep.mdf("- Device loopback open result: %s", wf::hr_str(loop_hr).c_str());
    rep.md("");

    rep.md("## Render level before and after opening the mic");
    rep.md("");
    rep.md("| Probe | Phase | samples | mean level | max level |");
    rep.md("|---|---|---|---|---|");
    rep.mdf("| endpoint meter | A: no capture stream | %zu | %.1f dBFS | %.1f dBFS |", ma.n,
            to_dbfs(ma.mean), to_dbfs(ma.max));
    rep.mdf("| endpoint meter | B: mic open | %zu | %.1f dBFS | %.1f dBFS |", mb.n,
            to_dbfs(mb.mean), to_dbfs(mb.max));
    rep.mdf("| loopback capture | A: no capture stream | %zu | %.1f dBFS | %.1f dBFS |", la.n,
            to_dbfs(la.mean), to_dbfs(la.max));
    rep.mdf("| loopback capture | B: mic open | %zu | %.1f dBFS | %.1f dBFS |", lb.n,
            to_dbfs(lb.mean), to_dbfs(lb.max));
    rep.md("");
    rep.mdf("Delta (B - A): endpoint meter **%+.1f dB**, loopback capture **%+.1f dB**.",
            meter_delta, loop_delta);
    rep.md("");

    const bool had_playback = ma.max > 1e-3 || la.max > 1e-3;
    const bool ducked = had_playback && (meter_delta < -2.0 || loop_delta < -2.0);
    rep.md("## Conclusion");
    rep.md("");
    if (!had_playback) {
        rep.md("- **No playback was detected in phase A** (render level below -60 dBFS). Repeat the "
               "run with audio playing from another application; this result says nothing about "
               "ducking.");
    } else if (ducked) {
        rep.mdf("- Opening the microphone on the default communications device **ducked** other "
                "playback by about %.1f dB in this configuration.",
                -(meter_delta < loop_delta ? meter_delta : loop_delta));
        rep.md("- Mitigation: point the user at Sound -> Communications -> \"Do nothing\", or pick "
               "a capture endpoint that is not the default communications device.");
    } else {
        rep.md("- **No ducking observed** in this configuration.");
    }
    rep.mdf("- Endpoint meter HRESULT (last sample): %s", wf::hr_str(meter_hr).c_str());
    rep.md("");

    write_stream_block(rep, "Device loopback, phase A", loop.diag(), as, loop_sink.events());
    write_stream_block(rep, "Device loopback, phase B", loop.diag(), bs, loop_sink.events());
    write_stream_block(rep, "Microphone",
                       mic.diag(), compute_stats(mic_sink.packets(), mic_sink.rate()),
                       mic_sink.events());

    rep.csv("probe,phase,index,value");
    for (size_t i = 0; i < meter_a.size(); ++i)
        rep.csv(fmt("endpoint-meter,A,%zu,%.6f", i, static_cast<double>(meter_a[i])));
    for (size_t i = 0; i < meter_b.size(); ++i)
        rep.csv(fmt("endpoint-meter,B,%zu,%.6f", i, static_cast<double>(meter_b[i])));
    for (size_t i = 0; i < a_pkts.size(); ++i)
        rep.csv(fmt("loopback-peak,A,%zu,%.6f", i, static_cast<double>(a_pkts[i].peak)));
    for (size_t i = 0; i < b_pkts.size(); ++i)
        rep.csv(fmt("loopback-peak,B,%zu,%.6f", i, static_cast<double>(b_pkts[i].peak)));

    rep.set_json("{\"measurement\":\"ducking\",\"category\":\"" + json_escape(a.category) +
                 "\",\"windows\":\"" + json_escape(windows_build()) +
                 "\",\"comm_equals_console_capture\":" + (comm_id == console_id ? "true" : "false") +
                 ",\"meter_a_mean_dbfs\":" + fmt("%.2f", to_dbfs(ma.mean)) +
                 ",\"meter_b_mean_dbfs\":" + fmt("%.2f", to_dbfs(mb.mean)) +
                 ",\"meter_delta_db\":" + fmt("%.2f", meter_delta) +
                 ",\"loopback_delta_db\":" + fmt("%.2f", loop_delta) + ",\"playback_detected\":" +
                 (had_playback ? "true" : "false") + ",\"ducked\":" + (ducked ? "true" : "false") +
                 ",\"mic\":" + stats_json("mic", mic.diag(), StreamStats{}) + "}");

    const std::string detail =
        fmt("category %s, meter %.1f -> %.1f dBFS (%+.1f dB), loopback %+.1f dB, %s",
            a.category.c_str(), to_dbfs(ma.mean), to_dbfs(mb.mean), meter_delta, loop_delta,
            !had_playback ? "no playback" : (ducked ? "DUCKED" : "no ducking"));
    rep.finish(!had_playback ? "WARN" : (ducked ? "FAIL" : "PASS"), detail);
    std::printf("%s\n", detail.c_str());
    return 0;
}

} // namespace probe
