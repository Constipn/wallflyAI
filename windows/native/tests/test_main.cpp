// Minimal hand-rolled test runner (same style as the .NET test runner): no framework dependency.
// Covers the C ABI probe plus the pure helpers of the WASAPI layer (format description and
// channel-averaging conversion); anything that needs a real endpoint lives in audioprobe.
#include "api/wallfly_audio.h"
#include "wasapi/com_util.h"
#include "wasapi/format_convert.h"

#include <audioclient.h>

#include <cmath>
#include <cstdio>
#include <cstring>

static int failures = 0;
#define CHECK(cond) do { if (!(cond)) { std::printf("FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond); ++failures; } } while (0)

// `near` is a legacy windef.h macro, so the helper is called approx().
static bool approx(float a, float b) { return std::fabs(a - b) < 1e-5f; }

static void test_abi() {
    CHECK(wf_abi_version() == WF_ABI_VERSION);
    uint32_t caps = 0xFFFFFFFFu;
    CHECK(wf_capabilities(&caps) == 0);
    CHECK((caps & ~(WF_CAP_PROCESS_LOOPBACK | WF_CAP_AEC3 | WF_CAP_FAKE)) == 0);
    CHECK((caps & WF_CAP_FAKE) != 0);   // the wf_fake_* injection path is always built in
    CHECK(wf_capabilities(nullptr) == WF_E_INVALID_ARG);
    std::printf("caps=0x%x\n", caps);
}

// The DLL exports exactly the C ABI of wallfly_audio.h - the 20 names the managed binding
// (Wallfly.Audio WallflyAudioNative.Exports) resolves plus the additive v1 extensions -
// and nothing else.
static void test_exports() {
    static const char* const kExports[] = {
        "wf_abi_version", "wf_capabilities", "wf_devices_list", "wf_audio_sessions_list",
        "wf_session_create", "wf_session_create_fake", "wf_session_start",
        "wf_session_set_mic_device", "wf_session_stop", "wf_session_destroy",
        "wf_session_event_handle", "wf_session_read_frames", "wf_session_read_events",
        "wf_session_get_levels", "wf_session_get_metrics", "wf_session_get_origin",
        "wf_fake_push", "wf_fake_set_now", "wf_fake_tick", "wf_fake_signal",
        // Additive within v1 (2026-11-23); the managed binding does not require them yet.
        "wf_session_get_diagnostics", "wf_power_requests_active",
        // Additive within v1 (2026-11-23, AP3): fake sessions only.
        "wf_fake_query",
    };
    constexpr size_t kCount = sizeof(kExports) / sizeof(kExports[0]);
    static_assert(kCount == 23, "ABI v1 has 23 exports");
    HMODULE dll = GetModuleHandleW(L"WallflyAudio.dll");
    CHECK(dll != nullptr);
    if (!dll) return;
    for (const char* name : kExports) {
        if (!GetProcAddress(dll, name)) {
            std::printf("missing export %s\n", name);
            CHECK(false);
        }
    }
    const BYTE* base = reinterpret_cast<const BYTE*>(dll);
    const auto* dos = reinterpret_cast<const IMAGE_DOS_HEADER*>(base);
    const auto* nt = reinterpret_cast<const IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    const IMAGE_DATA_DIRECTORY& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
    CHECK(dir.VirtualAddress != 0);
    if (dir.VirtualAddress == 0) return;
    const auto* ex = reinterpret_cast<const IMAGE_EXPORT_DIRECTORY*>(base + dir.VirtualAddress);
    CHECK(ex->NumberOfNames == kCount);
    const DWORD* names = reinterpret_cast<const DWORD*>(base + ex->AddressOfNames);
    for (DWORD i = 0; i < ex->NumberOfNames; ++i) {
        const char* name = reinterpret_cast<const char*>(base + names[i]);
        bool known = false;
        for (const char* want : kExports) known = known || std::strcmp(name, want) == 0;
        if (!known) {
            std::printf("unexpected export %s\n", name);
            CHECK(false);
        }
    }
    std::printf("exports=%lu\n", static_cast<unsigned long>(ex->NumberOfNames));
}

static void test_format_description() {
    WAVEFORMATEX wfx{};
    wf::make_float_format(&wfx, 48000, 1);
    wf::PcmFormat f{};
    CHECK(wf::describe_format(&wfx, &f));
    CHECK(f.fmt == wf::SampleFmt::F32);
    CHECK(f.rate == 48000 && f.channels == 1 && f.block_align == 4);

    // WAVE_FORMAT_EXTENSIBLE with the IEEE float subtype must describe the same way.
    WAVEFORMATEXTENSIBLE ext{};
    ext.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
    ext.Format.nChannels = 2;
    ext.Format.nSamplesPerSec = 44100;
    ext.Format.wBitsPerSample = 32;
    ext.Format.nBlockAlign = 8;
    ext.Format.cbSize = 22;
    ext.Samples.wValidBitsPerSample = 32;
    ext.SubFormat = GUID{0x00000003, 0x0000, 0x0010, {0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71}};
    CHECK(wf::describe_format(reinterpret_cast<const WAVEFORMATEX*>(&ext), &f));
    CHECK(f.fmt == wf::SampleFmt::F32 && f.channels == 2 && f.rate == 44100);

    WAVEFORMATEX bad{};
    bad.wFormatTag = WAVE_FORMAT_PCM;
    bad.wBitsPerSample = 8;
    bad.nChannels = 1;
    bad.nSamplesPerSec = 8000;
    bad.nBlockAlign = 1;
    CHECK(!wf::describe_format(&bad, &f));
}

static void test_mono_conversion() {
    // Float32 stereo -> mono average, with clipping.
    wf::PcmFormat f{wf::SampleFmt::F32, 2, 48000, 8};
    const float src[] = {1.0f, 0.0f, -1.0f, -1.0f, 2.0f, 2.0f, 0.25f, 0.75f};
    float dst[4] = {};
    wf::to_mono_float(f, reinterpret_cast<const BYTE*>(src), 4, dst);
    CHECK(approx(dst[0], 0.5f));
    CHECK(approx(dst[1], -1.0f));
    CHECK(approx(dst[2], 1.0f));   // clipped
    CHECK(approx(dst[3], 0.5f));

    // PCM16 mono -> full scale maps to -1.0 / ~+1.0.
    wf::PcmFormat p{wf::SampleFmt::I16, 1, 16000, 2};
    const int16_t pcm[] = {0, 16384, -32768, 32767};
    float out[4] = {};
    wf::to_mono_float(p, reinterpret_cast<const BYTE*>(pcm), 4, out);
    CHECK(approx(out[0], 0.0f));
    CHECK(approx(out[1], 0.5f));
    CHECK(approx(out[2], -1.0f));
    CHECK(out[3] > 0.999f && out[3] <= 1.0f);

    // PCM24 packed little-endian, 1 channel.
    wf::PcmFormat q{wf::SampleFmt::I24, 1, 48000, 3};
    const unsigned char pcm24[] = {0x00, 0x00, 0x40,   // +0x400000 = +0.5
                                   0x00, 0x00, 0xC0};  // -0x400000 = -0.5
    float o24[2] = {};
    wf::to_mono_float(q, pcm24, 2, o24);
    CHECK(approx(o24[0], 0.5f));
    CHECK(approx(o24[1], -0.5f));
}

static void test_hr_str() {
    CHECK(wf::hr_str(S_OK).find("S_OK") != std::string::npos);
    CHECK(wf::hr_str(AUDCLNT_E_DEVICE_INVALIDATED).find("DEVICE_INVALIDATED") != std::string::npos);
    CHECK(wf::narrow(L"abc") == "abc");
    CHECK(wf::widen("abc") == L"abc");
}

int main() {
    test_abi();
    test_exports();
    test_format_description();
    test_mono_conversion();
    test_hr_str();
    std::printf(failures ? "%d failure(s)\n" : "all tests passed\n", failures);
    return failures ? 1 : 0;
}
