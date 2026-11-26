// M6: one snapshot per second of every render and capture audio session (pid, parent,
// process name, state, peak). This is how the app will later decide which process tree
// carries the meeting audio and whether anything else holds the microphone.
#include "commands.h"
#include "wasapi/com_util.h"
#include "wasapi/sessions.h"

#include <cstdio>
#include <map>

namespace probe {

int cmd_sessions(const Args& a) {
    Report rep("sessions", a.out_dir);
    rep.md("# Audio sessions per second (M6)");
    rep.md("");
    rep.md(conditions_block(a.command_line));
    rep.md("Render sessions tell us which process tree produces the meeting audio (and therefore "
           "whether process loopback include-mode on `ms-teams.exe` is enough); capture sessions "
           "tell us who else holds the microphone.");
    rep.md("");

    rep.csv("second,flow,endpoint,pid,parent_pid,process,parent_process,state,peak,system_sounds");

    struct Agg {
        std::string process, parent, endpoint, flow;
        DWORD pid = 0, parent_pid = 0;
        float peak_max = 0.0f;
        int active_seconds = 0;
        int seen_seconds = 0;
    };
    std::map<std::string, Agg> agg;

    for (int sec = 0; sec < a.seconds; ++sec) {
        std::vector<wf::SessionInfo> render, capture;
        HRESULT hr_r = wf::list_sessions(eRender, &render);
        HRESULT hr_c = wf::list_sessions(eCapture, &capture);

        rep.mdf("## t = %d s", sec);
        rep.md("");
        if (FAILED(hr_r)) rep.mdf("Render enumeration failed: %s", wf::hr_str(hr_r).c_str());
        if (FAILED(hr_c)) rep.mdf("Capture enumeration failed: %s", wf::hr_str(hr_c).c_str());
        rep.md("| Flow | Endpoint | PID | Parent | Process | Parent process | State | Peak |");
        rep.md("|---|---|---|---|---|---|---|---|");

        auto dump = [&](const std::vector<wf::SessionInfo>& list, const char* flow) {
            for (const wf::SessionInfo& s : list) {
                // The system-sounds session has no owning process of its own; everything
                // else is named after its image so the Teams process tree is recognisable.
                const std::string label =
                    (s.pid == 0 || s.process_name.empty())
                        ? (s.is_system_sounds ? "(system sounds)" : "(no process)")
                        : s.process_name + (s.is_system_sounds ? " (system sounds)" : "");
                const char* name = label.c_str();
                rep.mdf("| %s | %s | %lu | %lu | %s | %s | %s | %.4f |", flow,
                        s.endpoint_name.c_str(), s.pid, s.parent_pid, name,
                        s.parent_name.empty() ? "-" : s.parent_name.c_str(),
                        wf::session_state_name(s.state), static_cast<double>(s.peak));
                rep.csv(fmt("%d,%s,\"%s\",%lu,%lu,\"%s\",\"%s\",%s,%.6f,%s", sec, flow,
                            s.endpoint_name.c_str(), s.pid, s.parent_pid, name,
                            s.parent_name.c_str(), wf::session_state_name(s.state),
                            static_cast<double>(s.peak), s.is_system_sounds ? "yes" : "no"));

                const std::string key = std::string(flow) + "|" + s.endpoint_name + "|" +
                                        fmt("%lu", s.pid) + "|" + name;
                Agg& g = agg[key];
                g.flow = flow;
                g.endpoint = s.endpoint_name;
                g.pid = s.pid;
                g.parent_pid = s.parent_pid;
                g.process = name;
                g.parent = s.parent_name;
                if (s.peak > g.peak_max) g.peak_max = s.peak;
                ++g.seen_seconds;
                if (s.state == AudioSessionStateActive) ++g.active_seconds;
            }
        };
        dump(render, "render");
        dump(capture, "capture");
        rep.md("");

        std::printf("\r  t=%d/%d  render sessions %zu, capture sessions %zu      ", sec + 1,
                    a.seconds, render.size(), capture.size());
        std::fflush(stdout);
        if (sec + 1 < a.seconds) Sleep(1000);
    }
    std::printf("\n");

    rep.md("## Summary over the whole run");
    rep.md("");
    rep.md("| Flow | Endpoint | PID | Parent PID | Process | Parent process | seconds seen | "
           "seconds active | max peak |");
    rep.md("|---|---|---|---|---|---|---|---|---|");
    std::string json = "{\"measurement\":\"sessions\",\"seconds\":" + fmt("%d", a.seconds) +
                       ",\"windows\":\"" + json_escape(windows_build()) + "\",\"sessions\":[";
    bool first = true;
    for (const auto& kv : agg) {
        const Agg& g = kv.second;
        rep.mdf("| %s | %s | %lu | %lu | %s | %s | %d | %d | %.4f |", g.flow.c_str(),
                g.endpoint.c_str(), g.pid, g.parent_pid, g.process.c_str(),
                g.parent.empty() ? "-" : g.parent.c_str(), g.seen_seconds, g.active_seconds,
                static_cast<double>(g.peak_max));
        if (!first) json += ",";
        first = false;
        json += "{\"flow\":\"" + json_escape(g.flow) + "\",\"endpoint\":\"" +
                json_escape(g.endpoint) + "\",\"pid\":" + fmt("%lu", g.pid) + ",\"parent_pid\":" +
                fmt("%lu", g.parent_pid) + ",\"process\":\"" + json_escape(g.process) +
                "\",\"parent_process\":\"" + json_escape(g.parent) + "\",\"seconds_seen\":" +
                fmt("%d", g.seen_seconds) + ",\"seconds_active\":" + fmt("%d", g.active_seconds) +
                ",\"max_peak\":" + fmt("%.6f", static_cast<double>(g.peak_max)) + "}";
    }
    json += "]}";
    rep.set_json(json);
    rep.md("");

    const std::string detail = fmt("%zu distinct sessions over %d s", agg.size(), a.seconds);
    rep.finish("INFO", detail);
    std::printf("%s\n", detail.c_str());
    return 0;
}

} // namespace probe
