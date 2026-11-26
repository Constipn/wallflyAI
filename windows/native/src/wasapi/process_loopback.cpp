// ActivateAudioInterfaceAsync on the process-loopback virtual device, resolved from
// mmdevapi.dll at run time, with a hand-written IActivateAudioInterfaceCompletionHandler
// (IUnknown + IAgileObject, no WRL). Initialize is tried event-driven first, then polled.
#include "wasapi/process_loopback.h"
#include "wasapi/process_loopback_defs.h"

#include <mmdeviceapi.h>
#include <cstdio>

namespace wf {
namespace {

using PfnActivateAudioInterfaceAsync = HRESULT(WINAPI*)(LPCWSTR, REFIID, PROPVARIANT*,
                                                        IActivateAudioInterfaceCompletionHandler*,
                                                        IActivateAudioInterfaceAsyncOperation**);

PfnActivateAudioInterfaceAsync resolve_activate() {
    static PfnActivateAudioInterfaceAsync fn = [] {
        HMODULE m = LoadLibraryW(L"mmdevapi.dll"); // intentionally leaked: process lifetime
        return m ? reinterpret_cast<PfnActivateAudioInterfaceAsync>(
                       GetProcAddress(m, "ActivateAudioInterfaceAsync"))
                 : nullptr;
    }();
    return fn;
}

class ActivationHandler final : public IActivateAudioInterfaceCompletionHandler, public IAgileObject {
public:
    explicit ActivationHandler(HANDLE done) : done_(done) {}

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override {
        if (!ppv) return E_POINTER;
        if (IsEqualIID(riid, __uuidof(IUnknown)) ||
            IsEqualIID(riid, __uuidof(IActivateAudioInterfaceCompletionHandler))) {
            *ppv = static_cast<IActivateAudioInterfaceCompletionHandler*>(this);
        } else if (IsEqualIID(riid, __uuidof(IAgileObject))) {
            *ppv = static_cast<IAgileObject*>(this);
        } else {
            *ppv = nullptr;
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&ref_); }
    ULONG STDMETHODCALLTYPE Release() override {
        LONG r = InterlockedDecrement(&ref_);
        if (r == 0) delete this;
        return static_cast<ULONG>(r);
    }
    // No work here beyond waking the caller (documented requirement for this callback).
    HRESULT STDMETHODCALLTYPE ActivateCompleted(IActivateAudioInterfaceAsyncOperation*) override {
        SetEvent(done_);
        return S_OK;
    }

private:
    ~ActivationHandler() = default;
    LONG ref_ = 1;
    HANDLE done_;
};

HRESULT activate_client(const ProcessLoopbackOptions& opt, ComPtr<IAudioClient>* out) {
    PfnActivateAudioInterfaceAsync fn = resolve_activate();
    if (!fn) return HRESULT_FROM_WIN32(ERROR_PROC_NOT_FOUND);

    WF_AUDIOCLIENT_ACTIVATION_PARAMS params{};
    params.ActivationType = WF_AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
    params.ProcessLoopbackParams.TargetProcessId =
        opt.target_pid ? opt.target_pid : GetCurrentProcessId();
    params.ProcessLoopbackParams.ProcessLoopbackMode =
        opt.mode == ProcessLoopbackMode::ExcludeTree
            ? WF_PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE
            : WF_PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;

    PROPVARIANT activate_params{};
    PropVariantInit(&activate_params);
    activate_params.vt = VT_BLOB;
    activate_params.blob.cbSize = sizeof(params);
    activate_params.blob.pBlobData = reinterpret_cast<BYTE*>(&params);

    HANDLE done = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!done) return HRESULT_FROM_WIN32(GetLastError());

    ActivationHandler* handler = new ActivationHandler(done);
    ComPtr<IActivateAudioInterfaceAsyncOperation> op;
    HRESULT hr = fn(WF_VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, __uuidof(IAudioClient),
                    &activate_params, handler, op.put());
    if (SUCCEEDED(hr)) {
        if (WaitForSingleObject(done, 5000) != WAIT_OBJECT_0) {
            hr = HRESULT_FROM_WIN32(ERROR_TIMEOUT);
        } else {
            HRESULT activate_hr = E_FAIL;
            ComPtr<IUnknown> unk;
            hr = op->GetActivateResult(&activate_hr, unk.put());
            if (SUCCEEDED(hr)) hr = activate_hr;
            if (SUCCEEDED(hr) && unk) hr = unk->QueryInterface(__uuidof(IAudioClient), out->put_void());
            else if (SUCCEEDED(hr)) hr = E_NOINTERFACE;
        }
    }
    handler->Release();
    CloseHandle(done);
    return hr;
}

} // namespace

bool process_loopback_api_available() { return resolve_activate() != nullptr; }

HRESULT ProcessLoopbackStream::start(const ProcessLoopbackOptions& opt, IPacketSink* sink) {
    diag_ = StreamDiagnostics{};

    struct Attempt { bool ev; bool autoconv; uint16_t ch; };
    const Attempt ladder[] = {
        {true, true, opt.channels}, {true, false, opt.channels},
        {false, true, opt.channels}, {false, false, opt.channels},
        {true, true, 2}, {false, true, 2},
    };

    HRESULT last = E_FAIL;
    char note[192];
    for (size_t i = 0; i < sizeof(ladder) / sizeof(ladder[0]); ++i) {
        // Every attempt needs a fresh client: Initialize may be called only once.
        ComPtr<IAudioClient> client;
        HRESULT hr = activate_client(opt, &client);
        activation_hr_ = hr;
        if (FAILED(hr)) { diag_.init_hr = hr; return hr; }

        WAVEFORMATEX wfx{};
        make_float_format(&wfx, opt.rate, ladder[i].ch);

        DWORD flags = AUDCLNT_STREAMFLAGS_LOOPBACK;
        if (ladder[i].ev) flags |= AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
        if (ladder[i].autoconv) flags |= AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;

        hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags,
                                static_cast<REFERENCE_TIME>(opt.buffer_ms) * 10000, 0, &wfx, nullptr);
        if (FAILED(hr)) {
            last = hr;
            std::snprintf(note, sizeof(note), "attempt %zu (event=%d autoconv=%d ch=%u) -> %s; ",
                          i + 1, ladder[i].ev ? 1 : 0, ladder[i].autoconv ? 1 : 0,
                          (unsigned)ladder[i].ch, hr_str(hr).c_str());
            diag_.notes += note;
            continue;
        }

        OpenedCapture opened;
        if (ladder[i].ev) {
            opened.event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
            hr = opened.event ? client->SetEventHandle(opened.event)
                              : HRESULT_FROM_WIN32(GetLastError());
            if (FAILED(hr)) {
                opened.close();
                last = hr;
                std::snprintf(note, sizeof(note), "attempt %zu SetEventHandle -> %s; ", i + 1,
                              hr_str(hr).c_str());
                diag_.notes += note;
                continue;
            }
        }

        UINT32 buffer_frames = 0;
        hr = client->GetBufferSize(&buffer_frames);
        if (FAILED(hr)) { opened.close(); last = hr; continue; }

        ComPtr<IAudioCaptureClient> cap;
        hr = client->GetService(__uuidof(IAudioCaptureClient), cap.put_void());
        if (FAILED(hr)) { opened.close(); last = hr; continue; }

        describe_format(&wfx, &opened.fmt);
        opened.client = client;
        opened.capture = cap;
        opened.buffer_frames = buffer_frames;
        opened.diag.opened = true;
        opened.diag.event_driven = ladder[i].ev;
        opened.diag.init_attempt = static_cast<int>(i) + 1;
        opened.diag.sample_rate = opt.rate;
        opened.diag.channels = ladder[i].ch;
        opened.diag.sample_format = "float32";
        opened.diag.format = opened.fmt;
        opened.diag.buffer_frames = buffer_frames;
        opened.diag.stream_flags = flags;
        opened.diag.format_text = format_summary(&wfx);
        opened.diag.notes = diag_.notes;

        diag_ = opened.diag;
        const StreamKind kind = opt.mode == ProcessLoopbackMode::ExcludeTree
                                    ? StreamKind::ProcessLoopbackExclude
                                    : StreamKind::ProcessLoopbackInclude;
        hr = runner_.start(std::move(opened), kind, sink);
        if (FAILED(hr)) diag_.init_hr = hr;
        return hr;
    }

    diag_.init_hr = last;
    return last;
}

} // namespace wf
