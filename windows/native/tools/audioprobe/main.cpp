// wallfly-audioprobe: measures the WASAPI behaviour the audio core depends on (R-M,
// M1..M6) and writes a dated markdown/csv/json report per run. Everything runs on an
// MTA thread; each capture stream owns its own Win32 capture thread.
#include "commands.h"
#include "wasapi/com_util.h"

#include <shellapi.h>
#include <cstdio>
#include <cstdlib>

using namespace probe;

namespace {

void usage() {
    std::printf(
        "wallfly-audioprobe -- WASAPI measurements for the Wallfly audio core\n"
        "\n"
        "  devices                         endpoints, defaults, mix formats, RAW support (M3)\n"
        "  jitter  --seconds N [--ref process-exclude|device-loopback|both] [--mic <id|default>]\n"
        "                                  QPC jitter/drift of mic vs reference (M1)\n"
        "  silence --seconds N             loopback behaviour while nothing plays, with and\n"
        "                                  without the silent keep-alive render stream (M2)\n"
        "  sessions [--seconds N]          per-second render/capture session dump (M6)\n"
        "  ducking --seconds N [--category other|communications]\n"
        "                                  render peak before/after opening the mic (M4)\n"
        "  switch  --seconds N             device-change events during a reference run (M5)\n"
        "  onset   --seconds N             per-packet log of the process-exclude stream and the\n"
        "                                  RAW mic plus every device notification with its key\n"
        "                                  while a process outside this tree starts/stops playing\n"
        "          [--restart-mic-ms N]    ... and reopen the mic every N ms\n"
        "  compare [--seconds N] [--pid N] [--wav <dir>] [--out <dir>]\n"
        "                                  IN PARALLEL: process-include on the oldest ms-teams.exe,\n"
        "                                  on every ms-teams_modulehost.exe and on --pid,\n"
        "                                  process-exclude of this tree, device loopback on the\n"
        "                                  default console and communications render endpoints\n"
        "                                  (each with a silent keep-alive); per second peak dBFS and\n"
        "                                  non-zero %% per stream plus the render sessions, then a\n"
        "                                  summary and \"hears audio: ...\" (default 60 s; stdout\n"
        "                                  only, a report only with --out, WAVs only with --wav)\n"
        "\n"
        "  --out <dir>                     report folder (default: windows\\analysis\\audio-probe)\n"
        "  --rate <hz>                     process-loopback request rate (default 48000)\n"
        "  --pid <n>                       include-mode target pid (sessions/jitter helper)\n");
}

std::string join_args(int argc, wchar_t** argv) {
    std::string s;
    for (int i = 1; i < argc; ++i) {
        if (i > 1) s += ' ';
        s += wf::narrow(argv[i]);
    }
    return s;
}

} // namespace

int main() {
    int argc = 0;
    wchar_t** argv = CommandLineToArgvW(GetCommandLineW(), &argc);
    if (!argv || argc < 2) {
        usage();
        return 2;
    }

    Args a;
    a.command = wf::narrow(argv[1]);
    a.command_line = "wallfly-audioprobe " + join_args(argc, argv);

    for (int i = 2; i < argc; ++i) {
        const std::string k = wf::narrow(argv[i]);
        const bool has_next = (i + 1 < argc);
        auto next = [&]() -> std::wstring { return has_next ? std::wstring(argv[++i]) : std::wstring(); };
        if (k == "--seconds" && has_next) {
            a.seconds = std::atoi(wf::narrow(argv[++i]).c_str());
            a.seconds_given = true;
        }
        else if (k == "--wav" && has_next) a.wav_dir = next();
        else if (k == "--ref" && has_next) a.ref = wf::narrow(argv[++i]);
        else if (k == "--mic" && has_next) a.mic = next();
        else if (k == "--out" && has_next) a.out_dir = next();
        else if (k == "--category" && has_next) a.category = wf::narrow(argv[++i]);
        else if (k == "--rate" && has_next) a.rate = static_cast<unsigned>(std::atoi(wf::narrow(argv[++i]).c_str()));
        else if (k == "--pid" && has_next) a.pid = static_cast<unsigned long>(std::atol(wf::narrow(argv[++i]).c_str()));
        else if (k == "--restart-mic-ms" && has_next) a.restart_mic_ms = std::atoi(wf::narrow(argv[++i]).c_str());
        else if (k == "--buffer-ms" && has_next) a.buffer_ms = std::atoi(wf::narrow(argv[++i]).c_str());
        else if (k == "--no-mic") a.no_mic = true;
        else if (k == "--keepalive") a.keepalive = true;
        else if (k == "-h" || k == "--help") { usage(); return 0; }
        else { std::printf("unknown option: %s\n\n", k.c_str()); usage(); return 2; }
    }
    if (a.seconds <= 0) a.seconds = 1;
    if (a.mic == L"default") a.mic.clear();

    wf::MtaScope mta;
    if (FAILED(mta.hr) && mta.hr != RPC_E_CHANGED_MODE) {
        std::printf("CoInitializeEx(MTA) failed: %s\n", wf::hr_str(mta.hr).c_str());
        return 1;
    }

    if (a.command == "devices") return cmd_devices(a);
    if (a.command == "jitter") return cmd_jitter(a);
    if (a.command == "silence") return cmd_silence(a);
    if (a.command == "sessions") return cmd_sessions(a);
    if (a.command == "ducking") return cmd_ducking(a);
    if (a.command == "switch") return cmd_switch(a);
    if (a.command == "onset") return cmd_onset(a);
    if (a.command == "compare") return cmd_compare(a);

    std::printf("unknown command: %s\n\n", a.command.c_str());
    usage();
    return 2;
}
