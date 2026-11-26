// Minimal COM helpers for the WASAPI layer: ref-counted pointer, CoTaskMem/PROPVARIANT
// guards, an MTA init guard and HRESULT/UTF-8 formatting. No WRL, no exceptions, no RTTI
// (project builds with -fno-exceptions -fno-rtti), so everything here is failure-returning.
#pragma once
#include <windows.h>
#include <objbase.h>
#include <propidl.h>
#include <stdint.h>
#include <string>

namespace wf {

template <class T>
class ComPtr {
public:
    ComPtr() = default;
    ComPtr(const ComPtr& o) : p_(o.p_) { if (p_) p_->AddRef(); }
    ComPtr(ComPtr&& o) noexcept : p_(o.p_) { o.p_ = nullptr; }
    ~ComPtr() { reset(); }
    ComPtr& operator=(const ComPtr& o) {
        if (this != &o) { T* n = o.p_; if (n) n->AddRef(); reset(); p_ = n; }
        return *this;
    }
    ComPtr& operator=(ComPtr&& o) noexcept {
        if (this != &o) { reset(); p_ = o.p_; o.p_ = nullptr; }
        return *this;
    }
    void reset() { if (p_) { p_->Release(); p_ = nullptr; } }
    T** put() { reset(); return &p_; }
    void** put_void() { reset(); return reinterpret_cast<void**>(&p_); }
    void attach(T* p) { reset(); p_ = p; }
    T* get() const { return p_; }
    T* operator->() const { return p_; }
    explicit operator bool() const { return p_ != nullptr; }

private:
    T* p_ = nullptr;
};

// CoTaskMemFree-owning wrapper for endpoint ids and WAVEFORMATEX blobs.
template <class T>
struct CoTaskMem {
    T* p = nullptr;
    CoTaskMem() = default;
    CoTaskMem(const CoTaskMem&) = delete;
    CoTaskMem& operator=(const CoTaskMem&) = delete;
    ~CoTaskMem() { if (p) CoTaskMemFree(p); }
    T** put() { if (p) { CoTaskMemFree(p); p = nullptr; } return &p; }
    void** put_void() { return reinterpret_cast<void**>(put()); }
    T* get() const { return p; }
};

struct PropVar {
    PROPVARIANT v{};
    PropVar() { PropVariantInit(&v); }
    PropVar(const PropVar&) = delete;
    PropVar& operator=(const PropVar&) = delete;
    ~PropVar() { PropVariantClear(&v); }
};

// CoInitializeEx(MTA) for the lifetime of the scope; RPC_E_CHANGED_MODE is tolerated.
struct MtaScope {
    HRESULT hr = E_FAIL;
    bool owns = false;
    MtaScope() {
        hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        owns = SUCCEEDED(hr);
    }
    MtaScope(const MtaScope&) = delete;
    MtaScope& operator=(const MtaScope&) = delete;
    ~MtaScope() { if (owns) CoUninitialize(); }
};

std::string narrow(const wchar_t* s);
std::wstring widen(const char* s);
// "0x88890004 (AUDCLNT_E_DEVICE_INVALIDATED)" for the codes this layer can produce.
std::string hr_str(HRESULT hr);

} // namespace wf
