// Report plumbing for the probe: resolves windows\analysis\audio-probe\, stamps every
// run with the exact conditions (Windows build, machine, devices, flags) and writes the
// matching .md/.csv/.json triple plus one row in SUMMARY.md.
#pragma once
#include <string>
#include <vector>

namespace probe {

std::string fmt(const char* f, ...);
std::string now_stamp();        // yyyymmdd-HHmm
std::string now_iso();          // yyyy-mm-dd HH:MM:SS (local)
std::string windows_build();    // "Windows 11 10.0.26200"
std::string machine_name();
std::string json_escape(const std::string& s);

class Report {
public:
    Report(std::string name, const std::wstring& out_dir_override);

    void md(const std::string& line);                  // one markdown line (newline added)
    void mdf(const char* f, ...);
    void csv(const std::string& line);
    void set_json(std::string body) { json_ = std::move(body); }

    // Writes the three files and appends the SUMMARY.md row. Returns false on I/O error.
    bool finish(const std::string& verdict, const std::string& summary_detail);

    const std::wstring& dir() const { return dir_; }
    const std::string& name() const { return name_; }

private:
    std::string name_;
    std::wstring dir_;
    std::string stamp_;
    std::string md_;
    std::string csv_;
    std::string json_;
};

// Shared markdown header: run conditions every report must state to be reproducible.
std::string conditions_block(const std::string& command_line);

} // namespace probe
