// compare (AP2/AP3 evidence for T1): which capture path actually hears the meeting audio?
//
// For --seconds N (default 60) these streams run IN PARALLEL, each into its own
// RecordingSink:
//   * process loopback, include mode, on the oldest ms-teams.exe (if running),
//   * process loopback, include mode, on every ms-teams_modulehost.exe,
//   * process loopback, include mode, on --pid N (if given),
//   * process loopback, exclude mode, of this probe's own tree (= "whole system"),
//   * device loopback on the default CONSOLE render endpoint,
//   * device loopback on the default COMMUNICATIONS render endpoint (skipped, with a note,
//     when it is the same endpoint),
// with a silent keep-alive render stream on each of the two endpoints (as the app runs one,
// and because device loopback delivers nothing while its endpoint is idle), plus once per
// second the render-session listing of `sessions` (pid, process, endpoint, state, peak) of
// every other process whose session is active or carries signal.
//
// Output (stdout): one CSV-like line per second with peak dBFS and the share of non-zero
// samples per stream and the render sessions that carry signal, then a summary table (active
// seconds = seconds with a peak above -50 dBFS) and the one-line verdict
// "hears audio: <streams>". Nothing is written to disk unless --out (report folder) or
// --wav <dir> (one 32-bit float WAV per stream) is given.
#include "commands.h"
#include "wasapi/clock.h"
#include "wasapi/com_util.h"
#include "wasapi/device_loopback.h"
#include "wasapi/enumerator.h"
#include "wasapi/keepalive_render.h"
#include "wasapi/process_loopback.h"
#include "wasapi/sessions.h"

#include <tlhelp32.h>

#include <cmath>
#include <cstdio>
#include <memory>
#include <string>
#include <vector>

namespace probe {

namespace {

constexpr double kActiveDbfs = -50.0;   // a second counts as "active" above this peak

// ---------------------------------------------------------------------------
// Optional WAV writer (mono float32 at the stream's own rate). The capture thread is the
// only writer; the header sizes are patched on close.
// ---------------------------------------------------------------------------
class WavWriter {
public:
    explicit WavWriter(std::wstring path) : path_(std::move(path)) {}
    ~WavWriter() { close(); }

    void write(const float* data, uint32_t frames, uint32_t rate) {
        if (failed_ || frames == 0) return;
        if (!f_) {
            if (_wfopen_s(&f_, path_.c_str(), L"wb") != 0 || !f_) {
                failed_ = true;
                f_ = nullptr;
                return;
            }
            rate_ = rate;
            write_header(0);
        }
        if (rate != rate_) return;   // a rate change mid-stream is not representable
        bytes_ += std::fwrite(data, sizeof(float), frames, f_) * sizeof(float);
    }

    void close() {
        if (!f_) return;
        std::fseek(f_, 0, SEEK_SET);
        write_header(static_cast<uint32_t>(bytes_));
        std::fclose(f_);
        f_ = nullptr;
    }

private:
    void put32(uint32_t v) { std::fwrite(&v, 4, 1, f_); }
    void put16(uint16_t v) { std::fwrite(&v, 2, 1, f_); }
    void write_header(uint32_t data_bytes) {
        std::fwrite("RIFF", 1, 4, f_);
        put32(36 + data_bytes);
        std::fwrite("WAVEfmt ", 1, 8, f_);
        put32(16);
        put16(3);                         // WAVE_FORMAT_IEEE_FLOAT
        put16(1);                         // mono
        put32(rate_);
        put32(rate_ * 4);
        put16(4);
        put16(32);
        std::fwrite("data", 1, 4, f_);
        put32(data_bytes);
    }

    std::wstring path_;
    FILE* f_ = nullptr;
    bool failed_ = false;
    uint32_t rate_ = 0;
    size_t bytes_ = 0;
};

// RecordingSink for the statistics, plus the optional WAV.
class TeeSink final : public wf::IPacketSink {
public:
    TeeSink(const std::string& label, int seconds, const std::wstring& wav_path)
        : rec_(label, seconds + 2) {
        if (!wav_path.empty()) wav_.reset(new WavWriter(wav_path));
    }
    void on_packet(const wf::WfPacket& p) override {
        rec_.on_packet(p);
        if (wav_ && p.mono) wav_->write(p.mono, p.frames, p.sample_rate);
    }
    void on_event(const wf::StreamEvent& e) override { rec_.on_event(e); }
    void close_wav() {
        if (wav_) wav_->close();
    }
    const RecordingSink& rec() const { return rec_; }

private:
    RecordingSink rec_;
    std::unique_ptr<WavWriter> wav_;
};

struct Probe {
    std::string label;
    std::string what;                  // human readable target
    HRESULT hr = E_FAIL;
    std::unique_ptr<TeeSink> sink;
    std::unique_ptr<wf::ProcessLoopbackStream> proc;
    std::unique_ptr<wf::DeviceLoopbackStream> dev;
    // Per second: peak (linear), non-zero samples, samples, packets.
    std::vector<double> peak;
    std::vector<uint64_t> nonzero, samples, packets;
};

struct ProcInfo {
    DWORD pid = 0;
    ULONGLONG created = 0;
};

std::vector<ProcInfo> processes_named(const wchar_t* exe) {
    std::vector<ProcInfo> out;
    HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snap == INVALID_HANDLE_VALUE) return out;
    PROCESSENTRY32W pe;
    pe.dwSize = sizeof(pe);
    for (BOOL ok = Process32FirstW(snap, &pe); ok; ok = Process32NextW(snap, &pe)) {
        if (CompareStringOrdinal(pe.szExeFile, -1, exe, -1, TRUE) != CSTR_EQUAL) continue;
        ProcInfo p;
        p.pid = pe.th32ProcessID;
        HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, p.pid);
        if (h) {
            FILETIME c, e, k, u;
            if (GetProcessTimes(h, &c, &e, &k, &u)) {
                p.created = (static_cast<ULONGLONG>(c.dwHighDateTime) << 32) | c.dwLowDateTime;
            }
            CloseHandle(h);
        }
        out.push_back(p);
    }
    CloseHandle(snap);
    return out;
}

std::wstring wav_path(const std::wstring& dir, const std::string& label) {
    if (dir.empty()) return std::wstring();
    std::wstring name = wf::widen(label.c_str());
    for (wchar_t& c : name) {
        if (c == L':' || c == L'/' || c == L'\\') c = L'-';
    }
    std::wstring d = dir;
    if (!d.empty() && d.back() != L'\\' && d.back() != L'/') d += L'\\';
    return d + L"compare-" + name + L".wav";
}

std::string db_text(double peak) {
    if (peak <= 0.0) return "-inf";
    return fmt("%.1f", to_dbfs(peak));
}

}  // namespace

int cmd_compare(const Args& a) {
    const int seconds = a.seconds_given ? a.seconds : 60;
    if (!a.wav_dir.empty()) CreateDirectoryW(a.wav_dir.c_str(), nullptr);
    std::vector<std::unique_ptr<Probe>> probes;

    auto add_process = [&](const std::string& label, const std::string& what,
                           wf::ProcessLoopbackMode mode, DWORD pid) {
        auto p = std::unique_ptr<Probe>(new Probe());
        p->label = label;
        p->what = what;
        p->sink.reset(new TeeSink(label, seconds, wav_path(a.wav_dir, label)));
        p->proc.reset(new wf::ProcessLoopbackStream());
        wf::ProcessLoopbackOptions opt;
        opt.mode = mode;
        opt.target_pid = pid;
        opt.rate = a.rate;
        opt.channels = 1;
        p->hr = p->proc->start(opt, p->sink.get());
        probes.push_back(std::move(p));
    };
    auto add_device = [&](const std::string& label, const std::wstring& id) {
        auto p = std::unique_ptr<Probe>(new Probe());
        p->label = label;
        p->sink.reset(new TeeSink(label, seconds, wav_path(a.wav_dir, label)));
        p->dev.reset(new wf::DeviceLoopbackStream());
        wf::DeviceLoopbackOptions opt;
        p->hr = p->dev->start(id.c_str(), opt, p->sink.get());
        p->what = p->dev->device_name().empty() ? wf::narrow(id.c_str()) : p->dev->device_name();
        probes.push_back(std::move(p));
    };

    // --- Teams process trees -------------------------------------------------------
    std::vector<ProcInfo> teams = processes_named(L"ms-teams.exe");
    std::printf("compare: %d s, process-loopback rate %u Hz%s\n", seconds, a.rate,
                a.wav_dir.empty() ? ", no audio files" : ", WAV per stream");
    if (!teams.empty()) {
        ProcInfo oldest = teams[0];
        for (const ProcInfo& p : teams) {
            if (p.created != 0 && (oldest.created == 0 || p.created < oldest.created)) oldest = p;
        }
        add_process(fmt("teams:%lu", oldest.pid), fmt("include ms-teams.exe pid %lu (oldest of %zu)",
                                                      oldest.pid, teams.size()),
                    wf::ProcessLoopbackMode::IncludeTree, oldest.pid);
    } else {
        std::printf("  ms-teams.exe: not running (no Teams include stream)\n");
    }
    for (const ProcInfo& p : processes_named(L"ms-teams_modulehost.exe")) {
        add_process(fmt("modulehost:%lu", p.pid), fmt("include ms-teams_modulehost.exe pid %lu", p.pid),
                    wf::ProcessLoopbackMode::IncludeTree, p.pid);
    }
    if (a.pid != 0) {
        add_process(fmt("pid:%lu", a.pid), fmt("include pid %lu (--pid)", a.pid),
                    wf::ProcessLoopbackMode::IncludeTree, a.pid);
    }
    add_process("exclude-self", "exclude this probe's process tree (whole system)",
                wf::ProcessLoopbackMode::ExcludeTree, 0);

    // --- device loopback on the console / communications default ---------------------
    std::wstring console_id, comms_id;
    const HRESULT console_hr = wf::default_endpoint_id(eRender, eConsole, &console_id);
    const HRESULT comms_hr = wf::default_endpoint_id(eRender, eCommunications, &comms_id);
    wf::KeepAliveRender keep_console, keep_comms;
    if (SUCCEEDED(console_hr)) {
        keep_console.start(console_id.c_str());
        add_device("device-console", console_id);
    } else {
        std::printf("  default console render endpoint: %s\n", wf::hr_str(console_hr).c_str());
    }
    if (SUCCEEDED(comms_hr) && SUCCEEDED(console_hr) &&
        CompareStringOrdinal(comms_id.c_str(), -1, console_id.c_str(), -1, TRUE) == CSTR_EQUAL) {
        std::printf("  default communications render endpoint = console endpoint: "
                    "device-comms skipped (device-console covers it)\n");
    } else if (SUCCEEDED(comms_hr)) {
        keep_comms.start(comms_id.c_str());
        add_device("device-comms", comms_id);
    } else {
        std::printf("  default communications render endpoint: %s\n", wf::hr_str(comms_hr).c_str());
    }

    for (const auto& p : probes) {
        std::printf("  %-18s %s: %s\n", p->label.c_str(), p->what.c_str(),
                    SUCCEEDED(p->hr) ? "open" : wf::hr_str(p->hr).c_str());
    }
    std::printf("  keep-alive console: %s, communications: %s\n",
                keep_console.running() ? "running" : "-", keep_comms.running() ? "running" : "-");

    // --- run: one render-session snapshot per second ------------------------------
    const int64_t t0 = wf::qpc_now_100ns();
    std::vector<std::string> session_lines(static_cast<size_t>(seconds));
    for (int sec = 0; sec < seconds; ++sec) {
        const int64_t until = t0 + static_cast<int64_t>(sec + 1) * 10000000;
        std::vector<wf::SessionInfo> render;
        wf::list_sessions(eRender, &render);
        std::string line;
        for (const wf::SessionInfo& s : render) {
            if (s.pid == GetCurrentProcessId()) continue;   // our own silent keep-alives
            if (s.state != AudioSessionStateActive && !(s.peak > 0.0f)) continue;
            const std::string name =
                s.process_name.empty() ? (s.is_system_sounds ? "(system sounds)" : "(no process)")
                                       : s.process_name;
            if (!line.empty()) line += "; ";
            line += fmt("%s(%lu)@%s %s %s", name.c_str(), s.pid, s.endpoint_name.c_str(),
                        s.state == AudioSessionStateActive ? "active" : wf::session_state_name(s.state),
                        db_text(s.peak).c_str());
        }
        session_lines[static_cast<size_t>(sec)] = line.empty() ? "-" : line;
        std::printf("\r  t=%d/%d s   ", sec + 1, seconds);
        std::fflush(stdout);
        const int64_t left = until - wf::qpc_now_100ns();
        if (left > 0) Sleep(static_cast<DWORD>(left / 10000));
    }
    std::printf("\n");
    for (auto& p : probes) {
        if (p->proc) p->proc->stop();
        if (p->dev) p->dev->stop();
        p->sink->close_wav();
    }
    keep_console.stop();
    keep_comms.stop();

    // --- per second -----------------------------------------------------------------
    for (auto& p : probes) {
        p->peak.assign(static_cast<size_t>(seconds), 0.0);
        p->nonzero.assign(static_cast<size_t>(seconds), 0);
        p->samples.assign(static_cast<size_t>(seconds), 0);
        p->packets.assign(static_cast<size_t>(seconds), 0);
        for (const PacketRecord& r : p->sink->rec().packets()) {
            const int64_t sec = (r.recv_qpc_100ns - t0) / 10000000;
            if (sec < 0 || sec >= seconds) continue;
            const size_t i = static_cast<size_t>(sec);
            if (r.peak > p->peak[i]) p->peak[i] = r.peak;
            p->nonzero[i] += r.nonzero;
            p->samples[i] += r.frames;
            ++p->packets[i];
        }
    }
    std::string header = "t";
    for (const auto& p : probes) header += "," + p->label + "_peak_dbfs," + p->label + "_nonzero_pct";
    header += ",render_sessions";
    std::vector<std::string> csv;
    csv.push_back(header);
    std::printf("%s\n", header.c_str());
    for (int sec = 0; sec < seconds; ++sec) {
        const size_t i = static_cast<size_t>(sec);
        std::string line = fmt("%d", sec);
        for (const auto& p : probes) {
            const std::string pct =
                p->samples[i] ? fmt("%.1f", 100.0 * static_cast<double>(p->nonzero[i]) /
                                                static_cast<double>(p->samples[i]))
                              : std::string("-");
            line += "," + (p->packets[i] ? db_text(p->peak[i]) : std::string("-")) + "," + pct;
        }
        line += ",\"" + session_lines[i] + "\"";
        csv.push_back(line);
        std::printf("%s\n", line.c_str());
    }

    // --- summary ----------------------------------------------------------------------
    std::vector<std::string> summary;
    summary.push_back(fmt("%-18s | %-8s | %7s | %6s | %13s | %9s | %s", "stream", "open", "packets",
                          "pkts/s", "active seconds", "max dBFS", "non-zero %"));
    std::string hears;
    for (const auto& p : probes) {
        int active = 0;
        double maxp = 0.0;
        uint64_t nz = 0, n = 0, pk = 0;
        for (int sec = 0; sec < seconds; ++sec) {
            const size_t i = static_cast<size_t>(sec);
            if (p->packets[i] && p->peak[i] > 0.0 && to_dbfs(p->peak[i]) > kActiveDbfs) ++active;
            if (p->peak[i] > maxp) maxp = p->peak[i];
            nz += p->nonzero[i];
            n += p->samples[i];
            pk += p->packets[i];
        }
        summary.push_back(fmt("%-18s | %-8s | %7llu | %6.1f | %6d of %-4d | %9s | %s",
                              p->label.c_str(), SUCCEEDED(p->hr) ? "ok" : "FAILED",
                              static_cast<unsigned long long>(pk),
                              static_cast<double>(pk) / seconds, active, seconds,
                              db_text(maxp).c_str(),
                              n ? fmt("%.1f", 100.0 * static_cast<double>(nz) / static_cast<double>(n)).c_str()
                                : "-"));
        if (active > 0) hears += (hears.empty() ? "" : ", ") + p->label;
    }
    const std::string verdict = "hears audio: " + (hears.empty() ? std::string("none") : hears);
    std::printf("\nsummary (active second = peak > %.0f dBFS)\n", kActiveDbfs);
    for (const std::string& s : summary) std::printf("%s\n", s.c_str());
    std::printf("%s\n", verdict.c_str());

    if (!a.out_dir.empty()) {
        Report rep("compare", a.out_dir);
        rep.md("# Capture paths compared");
        rep.md("");
        rep.md(conditions_block(a.command_line));
        rep.md("```");
        for (const std::string& s : summary) rep.md(s);
        rep.md(verdict);
        rep.md("```");
        for (const std::string& line : csv) rep.csv(line);
        rep.set_json("{\"measurement\":\"compare\",\"seconds\":" + fmt("%d", seconds) +
                     ",\"verdict\":\"" + json_escape(verdict) + "\"}");
        rep.finish("INFO", verdict);
    }
    return 0;
}

}  // namespace probe
