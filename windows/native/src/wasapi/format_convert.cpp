// Implementation of the WAVEFORMATEX description/conversion helpers.
// WAVE_FORMAT_EXTENSIBLE subtype GUIDs are defined locally so the build does not
// depend on ksuser/ksmedia symbol availability in llvm-mingw.
#include "wasapi/format_convert.h"
#include "wasapi/com_util.h"
#include <cstdio>
#include <cstring>

namespace wf {
namespace {

const GUID kSubtypePcm = {0x00000001, 0x0000, 0x0010, {0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71}};
const GUID kSubtypeFloat = {0x00000003, 0x0000, 0x0010, {0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71}};

inline float clamp1(float v) { return v > 1.0f ? 1.0f : (v < -1.0f ? -1.0f : v); }

} // namespace

const char* sample_fmt_name(SampleFmt f) {
    switch (f) {
        case SampleFmt::F32: return "float32";
        case SampleFmt::I16: return "pcm16";
        case SampleFmt::I24: return "pcm24";
        case SampleFmt::I32: return "pcm32";
        default: return "unknown";
    }
}

bool describe_format(const WAVEFORMATEX* wfx, PcmFormat* out) {
    if (!wfx || !out) return false;
    out->channels = wfx->nChannels;
    out->rate = wfx->nSamplesPerSec;
    out->block_align = wfx->nBlockAlign;
    out->fmt = SampleFmt::Unknown;

    WORD tag = wfx->wFormatTag;
    WORD bits = wfx->wBitsPerSample;
    if (tag == WAVE_FORMAT_EXTENSIBLE && wfx->cbSize >= 22) {
        const WAVEFORMATEXTENSIBLE* ext = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(wfx);
        if (IsEqualGUID(ext->SubFormat, kSubtypeFloat)) tag = WAVE_FORMAT_IEEE_FLOAT;
        else if (IsEqualGUID(ext->SubFormat, kSubtypePcm)) tag = WAVE_FORMAT_PCM;
        else return false;
    }
    if (tag == WAVE_FORMAT_IEEE_FLOAT && bits == 32) out->fmt = SampleFmt::F32;
    else if (tag == WAVE_FORMAT_PCM && bits == 16) out->fmt = SampleFmt::I16;
    else if (tag == WAVE_FORMAT_PCM && bits == 24) out->fmt = SampleFmt::I24;
    else if (tag == WAVE_FORMAT_PCM && bits == 32) out->fmt = SampleFmt::I32;
    else return false;
    return out->channels > 0 && out->rate > 0 && out->block_align > 0;
}

std::string format_summary(const WAVEFORMATEX* wfx) {
    if (!wfx) return "(null)";
    PcmFormat f{};
    char buf[160];
    if (describe_format(wfx, &f)) {
        std::snprintf(buf, sizeof(buf), "%u Hz, %u ch, %s (%u bit, align %u)",
                      f.rate, (unsigned)f.channels, sample_fmt_name(f.fmt),
                      (unsigned)wfx->wBitsPerSample, (unsigned)wfx->nBlockAlign);
    } else {
        std::snprintf(buf, sizeof(buf), "%lu Hz, %u ch, tag 0x%x, %u bit (unsupported)",
                      (unsigned long)wfx->nSamplesPerSec, (unsigned)wfx->nChannels,
                      (unsigned)wfx->wFormatTag, (unsigned)wfx->wBitsPerSample);
    }
    return std::string(buf);
}

void to_mono_float(const PcmFormat& fmt, const BYTE* src, uint32_t frames, float* dst) {
    const uint32_t ch = fmt.channels;
    const float inv = 1.0f / static_cast<float>(ch ? ch : 1);
    switch (fmt.fmt) {
        case SampleFmt::F32: {
            const float* s = reinterpret_cast<const float*>(src);
            for (uint32_t i = 0; i < frames; ++i) {
                float acc = 0.0f;
                for (uint32_t c = 0; c < ch; ++c) acc += s[i * ch + c];
                dst[i] = clamp1(acc * inv);
            }
            break;
        }
        case SampleFmt::I16: {
            const int16_t* s = reinterpret_cast<const int16_t*>(src);
            for (uint32_t i = 0; i < frames; ++i) {
                int32_t acc = 0;
                for (uint32_t c = 0; c < ch; ++c) acc += s[i * ch + c];
                dst[i] = clamp1((static_cast<float>(acc) * inv) * (1.0f / 32768.0f));
            }
            break;
        }
        case SampleFmt::I24: {
            for (uint32_t i = 0; i < frames; ++i) {
                float acc = 0.0f;
                const BYTE* f0 = src + static_cast<size_t>(i) * fmt.block_align;
                for (uint32_t c = 0; c < ch; ++c) {
                    const BYTE* p = f0 + c * 3;
                    int32_t v = (static_cast<int32_t>(p[2]) << 24) | (static_cast<int32_t>(p[1]) << 16) |
                                (static_cast<int32_t>(p[0]) << 8);
                    acc += static_cast<float>(v >> 8);
                }
                dst[i] = clamp1((acc * inv) * (1.0f / 8388608.0f));
            }
            break;
        }
        case SampleFmt::I32: {
            const int32_t* s = reinterpret_cast<const int32_t*>(src);
            for (uint32_t i = 0; i < frames; ++i) {
                double acc = 0.0;
                for (uint32_t c = 0; c < ch; ++c) acc += static_cast<double>(s[i * ch + c]);
                dst[i] = clamp1(static_cast<float>(acc * inv * (1.0 / 2147483648.0)));
            }
            break;
        }
        default:
            std::memset(dst, 0, sizeof(float) * frames);
            break;
    }
}

void make_float_format(WAVEFORMATEX* wfx, uint32_t rate, uint16_t channels) {
    std::memset(wfx, 0, sizeof(WAVEFORMATEX));
    wfx->wFormatTag = WAVE_FORMAT_IEEE_FLOAT;
    wfx->nChannels = channels;
    wfx->nSamplesPerSec = rate;
    wfx->wBitsPerSample = 32;
    wfx->nBlockAlign = static_cast<WORD>(channels * 4);
    wfx->nAvgBytesPerSec = rate * wfx->nBlockAlign;
    wfx->cbSize = 0;
}

} // namespace wf
