// Describes a WAVEFORMATEX in the few shapes WASAPI shared mode can hand us and
// converts an interleaved packet to mono float by averaging channels. The conversion
// is allocation-free; the caller supplies a buffer sized for the endpoint period.
#pragma once
#include <windows.h>
#include <mmreg.h>
#include <stdint.h>
#include <string>

namespace wf {

enum class SampleFmt { Unknown, F32, I16, I24, I32 };

struct PcmFormat {
    SampleFmt fmt = SampleFmt::Unknown;
    uint16_t channels = 0;
    uint32_t rate = 0;
    uint16_t block_align = 0; // bytes per frame
};

bool describe_format(const WAVEFORMATEX* wfx, PcmFormat* out);
// Same sample rate, channel count and sample format (the frame size follows from them).
// This is the "did the mic format really change" test of spec 3.1.
inline bool same_pcm_format(const PcmFormat& a, const PcmFormat& b) {
    return a.fmt == b.fmt && a.channels == b.channels && a.rate == b.rate;
}
const char* sample_fmt_name(SampleFmt f);
std::string format_summary(const WAVEFORMATEX* wfx);

// src holds `frames` interleaved frames in fmt; dst receives `frames` mono floats.
void to_mono_float(const PcmFormat& fmt, const BYTE* src, uint32_t frames, float* dst);

// Fills a WAVEFORMATEX describing mono/stereo 32-bit float at `rate` (process loopback
// has no mix format, so we must state one).
void make_float_format(WAVEFORMATEX* wfx, uint32_t rate, uint16_t channels);

} // namespace wf
