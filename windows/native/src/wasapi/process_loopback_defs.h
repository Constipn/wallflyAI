// Local declarations of the process-loopback activation contract. llvm-mingw ships no
// audioclientactivationparams.h, so the documented binary layout is restated here under
// WF_ names (no Microsoft header text is copied). Used only by process_loopback.cpp.
#pragma once
#include <windows.h>
#include <audioclient.h>

// Device "path" handed to ActivateAudioInterfaceAsync for the process-loopback tap.
#define WF_VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK L"VAD\\Process_Loopback"

// AUDCLNT_ERR(0x25): OS does not support this activation type (pre-Win11 22H2 servers).
#define WF_AUDCLNT_E_UNSUPPORTED_ACTIVATION_TYPE ((HRESULT)0x88890025L)

typedef enum WF_PROCESS_LOOPBACK_MODE {
    WF_PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0,
    WF_PROCESS_LOOPBACK_MODE_EXCLUDE_TARGET_PROCESS_TREE = 1
} WF_PROCESS_LOOPBACK_MODE;

typedef struct WF_AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS {
    DWORD TargetProcessId;
    WF_PROCESS_LOOPBACK_MODE ProcessLoopbackMode;
} WF_AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS;

typedef enum WF_AUDIOCLIENT_ACTIVATION_TYPE {
    WF_AUDIOCLIENT_ACTIVATION_TYPE_DEFAULT = 0,
    WF_AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1
} WF_AUDIOCLIENT_ACTIVATION_TYPE;

// The documented layout has a union whose only member is the process-loopback params,
// which is layout-identical to a plain member and avoids mingw's DUMMYUNIONNAME macro.
typedef struct WF_AUDIOCLIENT_ACTIVATION_PARAMS {
    WF_AUDIOCLIENT_ACTIVATION_TYPE ActivationType;
    WF_AUDIOCLIENT_PROCESS_LOOPBACK_PARAMS ProcessLoopbackParams;
} WF_AUDIOCLIENT_ACTIVATION_PARAMS;
