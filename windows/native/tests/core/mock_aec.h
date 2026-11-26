// Test-only view into the recording AEC mock (mock_aec.cpp). The mock implements
// src/aec/AECBridge.h, logs the call order and can subtract the last rendered frame so a
// wrong render/capture order shows up as non-zero output.
#pragma once
#include <cstdint>
#include <vector>

namespace mock_aec {

enum class Call : uint8_t { Render = 0, Capture = 1, Reset = 2, SetNs = 3 };

struct Entry {
    Call  kind = Call::Render;
    float first = 0.0f;   // first sample of the frame (render/capture) or the NS level
};

// Subtract the frame last passed to mn_aec_render from every capture frame.
void set_echo_subtraction(bool on);
// Emulate the 224-sample algorithmic delay of AEC3 + NS on the capture path.
void set_delay(int samples);
void clear_log();

const std::vector<Entry>& log();
int renders();
int captures();
int resets();
int ns_level();

}  // namespace mock_aec
