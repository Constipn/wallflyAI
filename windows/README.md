# Wallfly for Windows

Wallfly is a meeting notepad: it records the meeting audio next to your own notes, transcribes it live, tells the
speakers apart and, on request, turns your notes and the transcript into structured notes and to-dos. This folder
holds the native Windows app (WPF, C# / .NET 10) with its native audio core. The product overview is in the
[root README](../README.md); the license is in [`../LICENSE`](../LICENSE).

## Download and install

Installers are published on the GitHub Releases page of this repository, tagged `windows-v<version>`. Download
`Wallfly-Setup.exe` from the release and run it.

- The installer is per-user: it installs to `%LOCALAPPDATA%\Programs\Wallfly` and needs no administrator rights.
- The installer is not code-signed yet, so Windows SmartScreen may warn on first run. Choose **More info**, then
  **Run anyway**. See [`installer/README.md`](installer/README.md) for the signing plan.
- Uninstalling removes the program files only. Your meeting library and the keys in Windows Credential Manager stay.

## Requirements

- Windows 10 version 1809 (build 17763) or newer, or Windows 11; 64-bit (x64) only.
- No .NET installation is needed: the app ships a self-contained runtime.
- The local Whisper server (optional) needs the Microsoft Visual C++ 2015-2022 x64 runtime.
- For cloud transcription and note enhancement you need your own API keys (see "Data and privacy").

## What is included

`dist\Wallfly\` (and the installer) contain:

| Part | Purpose |
|---|---|
| `Wallfly.exe` | the app |
| `Wallfly Detector.exe` | optional background helper that offers to take notes when a Microsoft Teams call starts |
| `wallfly-mcp.exe` | local, read-only MCP server for AI clients (see below) |
| `WallflyAudio.dll` | native audio core: microphone and system-output capture, echo cancellation |
| `whisper\`, `models\` | local Whisper server and the Whisper, VAD and speaker-diarization models |
| `third-party\NOTICES.md` | licenses of all third-party components |

The model files are not stored in this repository. `tools\fetch-models.ps1` downloads them at build time from their
upstream releases; every file is pinned by SHA-256 in [`tools/models.lock.json`](tools/models.lock.json), verified
before use, and a mismatch aborts the download.

## Build from source

You need the .NET 10 SDK pinned in [`global.json`](global.json). `build.ps1` uses a project-local SDK in `.tools\dotnet`
if there is one. The native audio core additionally needs a C/C++ toolchain, which can be fetched without
administrator rights.

```powershell
cd windows
.\native\bootstrap-toolchain.ps1               # llvm-mingw, CMake and Ninja into .tools (SHA-256 verified)
.\build.ps1 -Models -Native -Publish           # models, native core, self-contained publish into dist\Wallfly
```

`.\build.ps1` without switches only builds the .NET solution. The switches are described in the header of
[`build.ps1`](build.ps1); the native core is described in [`native/README.md`](native/README.md).

To build the installer, install [Inno Setup 6](https://jrsoftware.org/isinfo.php) (per-user install is enough) and run:

```powershell
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" /DAppVersion=0.1.0 installer\wallfly.iss
# result: dist\installer\Wallfly-Setup-0.1.0.exe
```

The version of a build comes from `<Version>` in [`Directory.Build.props`](Directory.Build.props). The unit and UI
tests are not part of the public snapshot, so `build.ps1 -Test` does not apply here.

## Data and privacy

- **Local first.** Meetings, notes, transcripts and the kept audio live on your computer in `%LOCALAPPDATA%\Wallfly\`
  (one JSON file per meeting, audio under `Audio\`). The library can be moved in the app settings.
- **No telemetry.** The app sends no usage data, crash reports or analytics. The app log
  (`%LOCALAPPDATA%\Wallfly\logs\`) stays on your computer and never contains transcript text, titles, names or keys.
- **Cloud providers only if you choose them.** The default live transcription is Deepgram, which needs your own
  Deepgram API key; the audio is then sent to Deepgram. Local Whisper (`whisper\`, no network) and OpenAI are
  alternatives. Note enhancement (Anthropic, OpenAI or an OpenAI-compatible API of your choice) runs only when you
  click **Enhance notes**. Speaker separation after the meeting can run in the cloud (Deepgram) or on this device.
- **API keys** are stored in Windows Credential Manager (generic entries `Wallfly/...`). If no entry exists, the
  standard environment variables (`OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `DEEPGRAM_API_KEY`) are read. There is no
  `.env` file, and settings, exports and error messages never contain a key.

Check the legal situation before recording a call: recording consent rules depend on your country and on the people
in the meeting.

## MCP server

`wallfly-mcp.exe` gives an AI client such as Claude Desktop read-only access to your saved meetings, even when the app
is closed. It talks over stdio only (no network port) and offers three tools: `search_meetings`, `get_meeting` and
`get_transcript`. Audio paths, settings and keys never appear in a reply. Example for
`%APPDATA%\Claude\claude_desktop_config.json` (replace `<install folder>` with the full path of your Wallfly install folder):

```json
{
  "mcpServers": {
    "wallfly": {
      "command": "<install folder>\\wallfly-mcp.exe",
      "args": []
    }
  }
}
```

`"args": ["--library", "D:\\Wallfly"]` selects another library. The client can pass the content it reads on to its own
AI provider.

## Third-party licenses

Wallfly includes and downloads third-party components, among them WebRTC audio processing, whisper.cpp, sherpa-onnx
and the .NET runtime. Their licenses and the pinned versions are listed in
[`third-party/NOTICES.md`](third-party/NOTICES.md); the notice file is also shipped with the app.
