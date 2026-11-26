// Report implementation. The output folder is found by walking up from the executable
// to the "native" directory and stepping into its sibling analysis\audio-probe, so the
// probe writes to the repository even when it is started from elsewhere.
#include "report.h"
#include "wasapi/com_util.h"

#include <windows.h>
#include <cstdarg>
#include <cstdio>

namespace probe {
namespace {

std::wstring exe_dir() {
    WCHAR buf[MAX_PATH * 2];
    DWORD n = GetModuleFileNameW(nullptr, buf, static_cast<DWORD>(sizeof(buf) / sizeof(buf[0])));
    std::wstring p(buf, n);
    size_t slash = p.find_last_of(L"\\/");
    return slash == std::wstring::npos ? L"." : p.substr(0, slash);
}

bool make_dirs(const std::wstring& path) {
    if (path.empty()) return false;
    if (GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES) return true;
    size_t slash = path.find_last_of(L"\\/");
    if (slash != std::wstring::npos && slash > 2) {
        if (!make_dirs(path.substr(0, slash))) return false;
    }
    return CreateDirectoryW(path.c_str(), nullptr) ||
           GetLastError() == ERROR_ALREADY_EXISTS;
}

std::wstring default_out_dir() {
    std::wstring p = exe_dir();
    for (int i = 0; i < 8 && p.size() > 3; ++i) {
        size_t slash = p.find_last_of(L"\\/");
        if (slash == std::wstring::npos) break;
        std::wstring leaf = p.substr(slash + 1);
        if (leaf == L"native") return p.substr(0, slash) + L"\\analysis\\audio-probe";
        p = p.substr(0, slash);
    }
    return exe_dir() + L"\\audio-probe";
}

bool write_file(const std::wstring& path, const std::string& text, bool append) {
    HANDLE h = CreateFileW(path.c_str(), append ? FILE_APPEND_DATA : GENERIC_WRITE,
                           FILE_SHARE_READ, nullptr, append ? OPEN_ALWAYS : CREATE_ALWAYS,
                           FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    DWORD written = 0;
    BOOL ok = WriteFile(h, text.data(), static_cast<DWORD>(text.size()), &written, nullptr);
    CloseHandle(h);
    return ok && written == text.size();
}

} // namespace

std::string fmt(const char* f, ...) {
    char buf[4096];
    va_list ap;
    va_start(ap, f);
    int n = std::vsnprintf(buf, sizeof(buf), f, ap);
    va_end(ap);
    if (n < 0) return {};
    return std::string(buf, static_cast<size_t>(n) < sizeof(buf) ? static_cast<size_t>(n)
                                                                 : sizeof(buf) - 1);
}

std::string now_stamp() {
    SYSTEMTIME st{};
    GetLocalTime(&st);
    return fmt("%04u%02u%02u-%02u%02u", st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute);
}

std::string now_iso() {
    SYSTEMTIME st{};
    GetLocalTime(&st);
    return fmt("%04u-%02u-%02u %02u:%02u:%02u", st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute,
               st.wSecond);
}

std::string windows_build() {
    using RtlGetVersionFn = LONG(WINAPI*)(PRTL_OSVERSIONINFOW);
    HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
    auto fn = ntdll ? reinterpret_cast<RtlGetVersionFn>(GetProcAddress(ntdll, "RtlGetVersion")) : nullptr;
    if (!fn) return "unknown";
    RTL_OSVERSIONINFOW vi{};
    vi.dwOSVersionInfoSize = sizeof(vi);
    if (fn(&vi) != 0) return "unknown";
    return fmt("%lu.%lu build %lu", vi.dwMajorVersion, vi.dwMinorVersion, vi.dwBuildNumber);
}

std::string machine_name() {
    WCHAR buf[256];
    DWORD n = static_cast<DWORD>(sizeof(buf) / sizeof(buf[0]));
    if (!GetComputerNameW(buf, &n)) return "unknown";
    return wf::narrow(buf);
}

std::string json_escape(const std::string& s) {
    std::string o;
    o.reserve(s.size() + 8);
    for (char c : s) {
        switch (c) {
            case '"': o += "\\\""; break;
            case '\\': o += "\\\\"; break;
            case '\n': o += "\\n"; break;
            case '\r': o += "\\r"; break;
            case '\t': o += "\\t"; break;
            default:
                if (static_cast<unsigned char>(c) < 0x20) o += fmt("\\u%04x", c);
                else o += c;
        }
    }
    return o;
}

std::string conditions_block(const std::string& command_line) {
    std::string s;
    s += "## Run conditions\n\n";
    s += "| | |\n|---|---|\n";
    s += "| Time (local) | " + now_iso() + " |\n";
    s += "| Machine | " + machine_name() + " |\n";
    s += "| Windows | " + windows_build() + " |\n";
    s += "| Command | `" + command_line + "` |\n";
    s += "| Probe build | wallfly-audioprobe (llvm-mingw, " __DATE__ " " __TIME__ ") |\n";
    s += "\n";
    return s;
}

Report::Report(std::string name, const std::wstring& out_dir_override)
    : name_(std::move(name)), stamp_(now_stamp()) {
    dir_ = out_dir_override.empty() ? default_out_dir() : out_dir_override;
    make_dirs(dir_);
}

void Report::md(const std::string& line) {
    md_ += line;
    md_ += "\n";
}

void Report::mdf(const char* f, ...) {
    char buf[4096];
    va_list ap;
    va_start(ap, f);
    int n = std::vsnprintf(buf, sizeof(buf), f, ap);
    va_end(ap);
    if (n > 0) md_.append(buf, static_cast<size_t>(n) < sizeof(buf) ? static_cast<size_t>(n)
                                                                    : sizeof(buf) - 1);
    md_ += "\n";
}

void Report::csv(const std::string& line) {
    csv_ += line;
    csv_ += "\n";
}

bool Report::finish(const std::string& verdict, const std::string& summary_detail) {
    const std::wstring base = dir_ + L"\\" + wf::widen((stamp_ + "-" + name_).c_str());
    bool ok = write_file(base + L".md", md_, false);
    if (!csv_.empty()) ok = write_file(base + L".csv", csv_, false) && ok;
    if (!json_.empty()) ok = write_file(base + L".json", json_, false) && ok;

    const std::wstring summary = dir_ + L"\\SUMMARY.md";
    if (GetFileAttributesW(summary.c_str()) == INVALID_FILE_ATTRIBUTES) {
        write_file(summary,
                   "# WASAPI probe summary\n\n"
                   "One row per `wallfly-audioprobe` run. Details are in the dated report "
                   "next to this file.\n\n"
                   "| Run | Measurement | Verdict | Detail |\n|---|---|---|---|\n",
                   false);
    }
    const std::string row = "| " + stamp_ + " | " + name_ + " | " + verdict + " | " +
                            summary_detail + " |\n";
    write_file(summary, row, true);

    std::printf("\nreport: %s\n", wf::narrow((base + L".md").c_str()).c_str());
    if (!csv_.empty()) std::printf("csv:    %s\n", wf::narrow((base + L".csv").c_str()).c_str());
    if (!json_.empty()) std::printf("json:   %s\n", wf::narrow((base + L".json").c_str()).c_str());
    return ok;
}

} // namespace probe
