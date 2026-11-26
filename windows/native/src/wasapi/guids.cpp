// Single translation unit that instantiates the COM GUIDs this layer references.
// llvm-mingw's libuuid.a does not carry every CLSID/IID we need, so <initguid.h>
// emits selectany definitions here instead of relying on the import library.
#define INITGUID
#include <initguid.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <audiopolicy.h>
#include <endpointvolume.h>
#include <functiondiscoverykeys_devpkey.h>

// Nothing else: the header inclusions above are the definitions.
