<div align="center">

# Wallfly 🪰

**Your AI fly on the wall for meetings.**

Open-source AI meeting notes for macOS. Wallfly records and transcribes your meetings and turns your own notes into clean meeting notes with to-dos. No bot joins your call, and you don't need an account.

[![License: MIT](https://img.shields.io/github/license/Constipn/wallflyAI)](LICENSE)
[![Latest release](https://img.shields.io/github/v/release/Constipn/wallflyAI)](https://github.com/Constipn/wallflyAI/releases/latest)
[![Platform: macOS 26+ on Apple Silicon](https://img.shields.io/badge/platform-macOS%2026%2B%20%C2%B7%20Apple%20Silicon-lightgrey)](#download)

[**Download for macOS**](https://github.com/Constipn/wallflyAI/releases/latest/download/Wallfly.dmg) · Windows coming soon · [Report a bug](https://github.com/Constipn/wallflyAI/issues)

![Wallfly demo](assets/demo.gif)

</div>

---

## Why Wallfly?

Meetings are where decisions happen, and where details get lost. Wallfly sits quietly on your Mac, listens like a fly on the wall, and helps you turn your notes into clean meeting notes when the call ends.

An **open-source alternative to cloud notetakers like Otter.ai and Fireflies**:

- 🔒 **Private by design.** No account, no Wallfly server, no telemetry. Your meetings are stored on your Mac. Bring your own API keys, or run fully local.
- 🤫 **No bot joins your call.** Works with Microsoft Teams, Google Meet and other call apps by capturing their audio on your Mac. Teams calls can be detected automatically.
- 📝 **Notes first.** Type key points during the meeting. Afterwards, Wallfly expands them with the transcript into structured notes, a title and to-dos.
- 🗣️ **Knows who said what.** Your microphone (**Me**) and the meeting audio (**Others**) are recorded separately. Teams speakers are recognized by name, and voices are matched on your Mac after the meeting.
- 🔎 **Search** across all your meetings.
- 🌍 **English and German**, or automatic language detection.
- 🆓 **Free and open source.** MIT licensed. Read the code, fork it, improve it.

## Download

[**Download Wallfly.dmg**](https://github.com/Constipn/wallflyAI/releases/latest/download/Wallfly.dmg) from the [Releases page](https://github.com/Constipn/wallflyAI/releases/latest). Requires macOS 26 or later on Apple Silicon.

🪟 **Windows version coming soon.**

1. Open the DMG and drag Wallfly to Applications.
2. The app is not notarized. Either run

   ```sh
   xattr -dr com.apple.quarantine /Applications/Wallfly.app
   ```

   or try to open it once and then use System Settings → Privacy & Security → "Open Anyway" ("Dennoch öffnen" on German systems).
3. On first launch, a short setup asks for your storage location, AI providers and API keys, and the microphone and screen and system audio permissions.

<details>
<summary>Build from source</summary>

Requires the Swift 6.2 toolchain (Xcode 26).

```sh
git clone https://github.com/Constipn/wallflyAI.git
cd wallflyAI
./scripts/build.sh
open dist/Wallfly.app
```

The script creates a self-signed identity in `.local-signing/`. Keep that folder so macOS permissions survive rebuilds. The first build needs network access to download the Swift packages and the signing tool.

</details>

## How it works

1. **Record.** Start a recording. Wallfly captures your microphone and the meeting audio separately.
2. **Transcribe.** Choose Deepgram (streamed live during the call), OpenAI, or local Whisper through the separately installed [OpenWhispr](https://github.com/OpenWhispr/openwhispr) app.
3. **Expand.** After the meeting, Wallfly sends your notes and the transcript to the AI provider you chose: Anthropic Claude, OpenAI, or any OpenAI-compatible API, including a local server on `localhost`. You get structured notes, a title and to-dos. You can also turn this off.
4. **Keep.** Each meeting is saved as a JSON file in a folder you choose. Speaker voices are matched on your Mac.

You need API keys from the providers you use. A ChatGPT or Claude subscription is not enough. Keys are stored in the macOS Keychain.

## Privacy

- **Stored on your Mac:** meetings as JSON files in `~/Library/Application Support/Wallfly`, or a folder you pick in Settings. A cloud-synced folder will sync your meetings. By default, audio is deleted after processing; transcript, notes and speaker names are kept. You can choose to keep the audio.
- **Sent over the network**, only to the providers you choose:
  - Audio for transcription goes to Deepgram or OpenAI. With local Whisper, audio stays on your Mac.
  - Your notes and the transcript go to the AI provider when you expand them. Turn this off, or use a local OpenAI-compatible server.
  - The speaker-recognition models are downloaded once from Hugging Face.
- **Fully local:** local Whisper plus a local OpenAI-compatible server (or no AI notes) keeps everything on your Mac.
- **Telemetry:** none. There is no Wallfly server and no account.

> ⚠️ **Recording consent:** Recording conversations without consent is illegal in many countries (e.g. Germany, § 201 StGB). Always make sure every participant agrees to being recorded.

## How Wallfly compares

| | Wallfly | Typical cloud notetakers |
|---|:---:|:---:|
| Open source | ✅ MIT | ❌ |
| No bot in your call | ✅ | Often a bot joins |
| No account needed | ✅ | ❌ |
| Meetings stored on your device | ✅ | ❌ Vendor cloud |
| Fully local option | ✅ | ❌ |
| Price | Free; you pay AI providers per use | Subscription |

## Local MCP access

Wallfly includes `Contents/MacOS/wallfly-mcp` inside the app. In **Settings → MCP**, copy the configuration into a client supporting local stdio MCP servers. For the usual installation:

```json
{
  "mcpServers": {
    "wallfly": {
      "command": "/Applications/Wallfly.app/Contents/MacOS/wallfly-mcp",
      "args": []
    }
  }
}
```

Client configuration formats vary; use this command and argument list in your client's corresponding settings. Restart the connection after configuration. If you move the app, copy the configuration again. The helper follows the library selected in Wallfly, even after a storage move. For a manually selected library, use `"args": ["--library", "/absolute/path/to/library"]`.

The tools are `search_meetings` (text, participant and ISO 8601 date-time filters; empty query lists meetings), `get_meeting` (notes, tasks and metadata), and `get_transcript` (speakers, relative seconds, provisional state and known gaps). Search returns up to 20 meetings by default, maximum 100. Transcript pages default to 100 sections, maximum 500. To continue, pass `next_cursor` as `cursor` with all other arguments unchanged. If saved data changes, restart without a cursor.

All saved meetings in the library are readable by a configured client. No audio or credentials are returned, and no files are modified or uploaded by this helper. Your AI client may send the retrieved text to its provider. The helper makes no additional AI requests. Wallfly's window can remain closed; the Mac and library must be available. This is a local integration, with no cloud endpoint or background login service.

If the library is unavailable, connect its drive or open Wallfly to select its location. Corrupt records are reported as skipped during search; restore them from a backup or inspect them in Wallfly. The settings availability indicator is not a connection indicator. No Python, Node, or separate helper installation is needed.

## Contributing

Contributions are welcome! Open an [issue](https://github.com/Constipn/wallflyAI/issues) for bugs and ideas, or look at the issues labeled [`good first issue`](https://github.com/Constipn/wallflyAI/labels/good%20first%20issue).

If Wallfly saves you time, a ⭐ on GitHub helps others find it.

## License

[MIT](LICENSE) © 2026 Constipn

The vendored WebRTC audio-processing code keeps its BSD license and patent grant (`Sources/WebRTCAEC/LICENSE`, `Sources/WebRTCAEC/vendor-ns/LICENSE`, `Sources/WebRTCAEC/vendor-ns/PATENTS`, `Sources/WebRTCAEC/JSONCPP-LICENSE`). FluidAudio is under its own license.
