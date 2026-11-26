// Command surface of wallfly-audioprobe. Each subcommand measures one row of the
// R-M table (M1..M6) and writes a dated report; the shared helpers below format the
// per-stream result blocks the same way everywhere so reports stay comparable.
#pragma once
#include <string>
#include <vector>

#include "collector.h"
#include "report.h"
#include "stats.h"
#include "wasapi/capture_loop.h"

namespace probe {

struct Args {
    std::string command;
    std::string command_line;      // echoed into every report
    int seconds = 20;
    std::string ref = "both";      // process-exclude | device-loopback | both
    std::wstring mic;              // empty / "default" -> default console capture endpoint
    std::wstring out_dir;          // --out override
    std::string category = "other";// other | communications (M4)
    unsigned rate = 48000;         // process loopback requested rate
    unsigned long pid = 0;         // include-mode target
    int restart_mic_ms = 0;        // onset: reopen the mic every N ms (0 = never)
    int buffer_ms = 20;            // onset: process-loopback buffer request
    bool no_mic = false;           // onset: process-loopback stream only
    bool keepalive = false;        // onset: run the silent keep-alive render stream too
    bool seconds_given = false;    // --seconds was on the command line
    std::wstring wav_dir;          // compare: write one WAV per stream here (else no audio files)
};

int cmd_devices(const Args&);
int cmd_jitter(const Args&);
int cmd_silence(const Args&);
int cmd_sessions(const Args&);
int cmd_ducking(const Args&);
int cmd_switch(const Args&);
int cmd_onset(const Args&);
int cmd_compare(const Args&);

// --- shared helpers ---------------------------------------------------------------
std::vector<PacketRecord> packets_before(const std::vector<PacketRecord>& all, int64_t qpc);
std::vector<PacketRecord> packets_from(const std::vector<PacketRecord>& all, int64_t qpc);

void write_stream_block(Report& r, const std::string& title, const wf::StreamDiagnostics& diag,
                        const StreamStats& s, const std::vector<EventRecord>& events);
void write_packet_csv_header(Report& r);
void write_packet_csv(Report& r, const std::string& stream, const std::vector<PacketRecord>& p);
std::string stats_json(const std::string& stream, const wf::StreamDiagnostics& diag,
                       const StreamStats& s);
double to_dbfs(double amplitude);
void countdown(const char* what, int seconds);

} // namespace probe
