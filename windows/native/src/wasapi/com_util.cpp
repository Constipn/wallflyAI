// UTF-16/UTF-8 conversion and HRESULT naming for the WASAPI layer. Only the codes this
// layer can actually produce are named; everything else is printed as a raw hex value.
#include "wasapi/com_util.h"
#include <audioclient.h>
#include <cstdio>

namespace wf {
namespace {
// Not declared by llvm-mingw's audioclient.h; AUDCLNT_ERR(0x25). Returned by
// ActivateAudioInterfaceAsync when process loopback is not supported by the OS.
constexpr HRESULT kUnsupportedActivationType = static_cast<HRESULT>(0x88890025L);
} // namespace

std::string narrow(const wchar_t* s) {
    if (!s) return {};
    int n = WideCharToMultiByte(CP_UTF8, 0, s, -1, nullptr, 0, nullptr, nullptr);
    if (n <= 1) return {};
    std::string out(static_cast<size_t>(n - 1), '\0');
    WideCharToMultiByte(CP_UTF8, 0, s, -1, out.data(), n, nullptr, nullptr);
    return out;
}

std::wstring widen(const char* s) {
    if (!s) return {};
    int n = MultiByteToWideChar(CP_UTF8, 0, s, -1, nullptr, 0);
    if (n <= 1) return {};
    std::wstring out(static_cast<size_t>(n - 1), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s, -1, out.data(), n);
    return out;
}

std::string hr_str(HRESULT hr) {
    const char* name = nullptr;
    switch (hr) {
        case S_OK: name = "S_OK"; break;
        case E_POINTER: name = "E_POINTER"; break;
        case E_INVALIDARG: name = "E_INVALIDARG"; break;
        case E_NOINTERFACE: name = "E_NOINTERFACE"; break;
        case E_NOTIMPL: name = "E_NOTIMPL"; break;
        case E_OUTOFMEMORY: name = "E_OUTOFMEMORY"; break;
        case E_ACCESSDENIED: name = "E_ACCESSDENIED"; break;
        case E_UNEXPECTED: name = "E_UNEXPECTED"; break;
        case AUDCLNT_E_NOT_INITIALIZED: name = "AUDCLNT_E_NOT_INITIALIZED"; break;
        case AUDCLNT_E_ALREADY_INITIALIZED: name = "AUDCLNT_E_ALREADY_INITIALIZED"; break;
        case AUDCLNT_E_WRONG_ENDPOINT_TYPE: name = "AUDCLNT_E_WRONG_ENDPOINT_TYPE"; break;
        case AUDCLNT_E_DEVICE_INVALIDATED: name = "AUDCLNT_E_DEVICE_INVALIDATED"; break;
        case AUDCLNT_E_NOT_STOPPED: name = "AUDCLNT_E_NOT_STOPPED"; break;
        case AUDCLNT_E_BUFFER_TOO_LARGE: name = "AUDCLNT_E_BUFFER_TOO_LARGE"; break;
        case AUDCLNT_E_OUT_OF_ORDER: name = "AUDCLNT_E_OUT_OF_ORDER"; break;
        case AUDCLNT_E_UNSUPPORTED_FORMAT: name = "AUDCLNT_E_UNSUPPORTED_FORMAT"; break;
        case AUDCLNT_E_INVALID_SIZE: name = "AUDCLNT_E_INVALID_SIZE"; break;
        case AUDCLNT_E_DEVICE_IN_USE: name = "AUDCLNT_E_DEVICE_IN_USE"; break;
        case AUDCLNT_E_BUFFER_OPERATION_PENDING: name = "AUDCLNT_E_BUFFER_OPERATION_PENDING"; break;
        case AUDCLNT_E_THREAD_NOT_REGISTERED: name = "AUDCLNT_E_THREAD_NOT_REGISTERED"; break;
        case AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED: name = "AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED"; break;
        case AUDCLNT_E_ENDPOINT_CREATE_FAILED: name = "AUDCLNT_E_ENDPOINT_CREATE_FAILED"; break;
        case AUDCLNT_E_SERVICE_NOT_RUNNING: name = "AUDCLNT_E_SERVICE_NOT_RUNNING"; break;
        case AUDCLNT_E_EVENTHANDLE_NOT_EXPECTED: name = "AUDCLNT_E_EVENTHANDLE_NOT_EXPECTED"; break;
        case AUDCLNT_E_EVENTHANDLE_NOT_SET: name = "AUDCLNT_E_EVENTHANDLE_NOT_SET"; break;
        case AUDCLNT_E_INCORRECT_BUFFER_SIZE: name = "AUDCLNT_E_INCORRECT_BUFFER_SIZE"; break;
        case AUDCLNT_E_BUFFER_SIZE_ERROR: name = "AUDCLNT_E_BUFFER_SIZE_ERROR"; break;
        case AUDCLNT_E_CPUUSAGE_EXCEEDED: name = "AUDCLNT_E_CPUUSAGE_EXCEEDED"; break;
        case AUDCLNT_E_BUFFER_ERROR: name = "AUDCLNT_E_BUFFER_ERROR"; break;
        case AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED: name = "AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED"; break;
        case AUDCLNT_E_INVALID_DEVICE_PERIOD: name = "AUDCLNT_E_INVALID_DEVICE_PERIOD"; break;
        case AUDCLNT_E_INVALID_STREAM_FLAG: name = "AUDCLNT_E_INVALID_STREAM_FLAG"; break;
        case kUnsupportedActivationType: name = "AUDCLNT_E_UNSUPPORTED_ACTIVATION_TYPE"; break;
        case AUDCLNT_S_BUFFER_EMPTY: name = "AUDCLNT_S_BUFFER_EMPTY"; break;
        default: break;
    }
    char buf[96];
    if (name) std::snprintf(buf, sizeof(buf), "0x%08lX (%s)", static_cast<unsigned long>(hr), name);
    else std::snprintf(buf, sizeof(buf), "0x%08lX", static_cast<unsigned long>(hr));
    return std::string(buf);
}

} // namespace wf
