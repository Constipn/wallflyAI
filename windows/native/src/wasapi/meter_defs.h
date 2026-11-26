// llvm-mingw's endpointvolume.h only forward-declares IAudioMeterInformation, so the
// vtable is restated here from the documented method order (GetPeakValue,
// GetMeteringChannelCount, GetChannelsPeakValues, QueryHardwareSupport).
#pragma once
#include <windows.h>
#include <unknwn.h>

#ifndef __IAudioMeterInformation_INTERFACE_DEFINED__
#define __IAudioMeterInformation_INTERFACE_DEFINED__

struct IAudioMeterInformation : public IUnknown {
    virtual HRESULT STDMETHODCALLTYPE GetPeakValue(float* pfPeak) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetMeteringChannelCount(UINT* pnChannelCount) = 0;
    virtual HRESULT STDMETHODCALLTYPE GetChannelsPeakValues(UINT32 u32ChannelCount,
                                                            float* afPeakValues) = 0;
    virtual HRESULT STDMETHODCALLTYPE QueryHardwareSupport(DWORD* pdwHardwareSupportMask) = 0;
};
// Teaches mingw's __uuidof about the interface (IID C02216F6-8C67-4B5B-9D00-D008E73E0064).
__CRT_UUID_DECL(IAudioMeterInformation, 0xc02216f6, 0x8c67, 0x4b5b, 0x9d, 0x00, 0xd0, 0x08,
                0xe7, 0x3e, 0x00, 0x64)

#endif // __IAudioMeterInformation_INTERFACE_DEFINED__
