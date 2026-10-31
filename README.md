# Wallfly

A native macOS meeting notebook. Wallfly records your microphone and the meeting audio separately and labels them **Me** and **Others**. You write notes during the call, and afterwards they are expanded using the transcript.

- Notes first: type during the meeting, then expand your notes with the transcript when you finish.
- Separate channels: microphone and meeting playback are captured separately, so Me and Others stay apart.
- Teams speaker names: speakers in Microsoft Teams calls are recognized, and voices can be matched locally after the meeting.
- Works with Teams, Google Meet and other calls; capture the whole Mac or a selected app.
- No meeting bot, no calendar integration, no telemetry.

## Download

[Wallfly.dmg](https://github.com/Constipn/wallflyAI/releases/latest/download/Wallfly.dmg)

## Install

1. Open the DMG and drag Wallfly to Applications.
2. The app is not notarized. Either run

   ```sh
   xattr -dr com.apple.quarantine /Applications/Wallfly.app
   ```

   or try to open it once and then use System Settings → Privacy & Security → "Open Anyway" ("Dennoch öffnen" on German systems).
3. Grant microphone and screen and system audio permissions.

## Requirements

macOS 26 or later, Apple Silicon.

## Bring your own API keys

Wallfly has no backend. Enter your own keys in Settings; they are stored in the macOS Keychain.

- Transcription: Deepgram or OpenAI. Alternatively, local Whisper through the separately installed OpenWhispr app and a downloaded model.
- Note enhancement: Anthropic, OpenAI or a custom OpenAI-compatible API address, or none.

A ChatGPT or Claude subscription is not enough; you need an API key from the provider.

## Build from source

Requires the Swift 6.2 toolchain (Xcode 26).

```sh
./scripts/build.sh
```

This produces `dist/Wallfly.app`. The script creates a self-signed identity in `.local-signing/`. Keep that folder so macOS permissions survive rebuilds. The first build needs network access to download the Swift packages and the signing tool.

## License

MIT, see [LICENSE](LICENSE).

The vendored WebRTC audio-processing code keeps its BSD license and patent grant (`Sources/WebRTCAEC/LICENSE`, `Sources/WebRTCAEC/vendor-ns/LICENSE`, `Sources/WebRTCAEC/vendor-ns/PATENTS`, `Sources/WebRTCAEC/JSONCPP-LICENSE`). FluidAudio is under its own license.
