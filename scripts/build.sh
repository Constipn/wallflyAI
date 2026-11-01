#!/bin/zsh
set -euo pipefail
cd "${0:A:h:h}"
shasum -a 256 -c Sources/WebRTCAEC/SHA256SUMS Sources/WebRTCAEC/SHA256SUMS-NS --quiet
export CLANG_MODULE_CACHE_PATH="$PWD/.build/ModuleCache"
export SWIFT_MODULECACHE_PATH="$PWD/.build/ModuleCache"
# The outer Codex/macOS sandbox remains in force; SwiftPM's nested manifest sandbox
# cannot initialize inside it. All build caches remain in the project directory.
swift build --disable-sandbox --build-system native --configuration release --cache-path .build/cache --config-path .build/config --security-path .build/security -Xswiftc -module-cache-path -Xswiftc "$CLANG_MODULE_CACHE_PATH"
# Keep temporary app bundles out of Spotlight/app discovery, including on failed builds.
stage_dir=$(mktemp -d "$PWD/.build/Wallfly-stage-XXXXXXXX.noindex")
app="$stage_dir/Wallfly.app"
cleanup_stage() { [[ ! -d "$stage_dir" ]] || /bin/rm -rf -- "$stage_dir"; }
trap cleanup_stage EXIT
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp .build/release/MeetingNotes "$app/Contents/MacOS/MeetingNotes"
cp .build/release/wallfly-mcp "$app/Contents/MacOS/wallfly-mcp"
./scripts/sign-app.sh "$app/Contents/MacOS/wallfly-mcp" io.github.constipn.wallfly.mcp
mkdir -p "$app/Contents/Resources/Licenses"
for dependency in swift-sdk swift-log swift-system eventsource; do
    license=".build/checkouts/$dependency/LICENSE"
    if [[ ! -f "$license" ]]; then
        if [[ -f "$license.txt" ]]; then license="$license.txt"; else license="$license.md"; fi
    fi
    cp "$license" "$app/Contents/Resources/Licenses/$dependency.txt"
done
cp Sources/WebRTCAEC/LICENSE "$app/Contents/Resources/Licenses/WebRTC-AEC3.txt"
cp Sources/WebRTCAEC/JSONCPP-LICENSE "$app/Contents/Resources/Licenses/JsonCpp.txt"
cp Sources/WebRTCAEC/PROVENANCE.md "$app/Contents/Resources/Licenses/WebRTC-AEC3-Provenance.md"
cp Sources/WebRTCAEC/vendor-ns/LICENSE "$app/Contents/Resources/Licenses/WebRTC-NS.txt"
cp Sources/WebRTCAEC/vendor-ns/PATENTS "$app/Contents/Resources/Licenses/WebRTC-NS-PATENTS.txt"
cp Sources/WebRTCAEC/PROVENANCE-NS.md "$app/Contents/Resources/Licenses/WebRTC-NS-Provenance.md"
# FluidAudio (offline speaker diarization) and the code compiled into it; the models are downloaded at runtime.
fluidaudio=".build/checkouts/FluidAudio"
cp "$fluidaudio/LICENSE" "$app/Contents/Resources/Licenses/FluidAudio.txt"
cp "$fluidaudio/ThirdPartyLicenses/fastcluster-LICENSE.md" "$app/Contents/Resources/Licenses/FluidAudio-fastcluster.md"
cp "$fluidaudio/ThirdPartyLicenses/vbx-LICENSE.md" "$app/Contents/Resources/Licenses/FluidAudio-VBx.md"
cat > "$app/Contents/Resources/Licenses/pyannote-community-1-models.txt" <<'MODELS'
Speaker diarization models

Wallfly downloads the speaker diarization models "pyannote speaker-diarization-community-1"
(segmentation, WeSpeaker embedding and PLDA), converted to Core ML by FluidInference
(https://huggingface.co/FluidInference/speaker-diarization-coreml), on first use into
~/Library/Application Support/FluidAudio/Models/. They are not part of this app bundle.

Original models: pyannote.audio community-1 by pyannoteAI (https://huggingface.co/pyannote/speaker-diarization-community-1),
licensed under Creative Commons Attribution 4.0 International (CC BY 4.0,
https://creativecommons.org/licenses/by/4.0/). The licence of the Core ML conversion is stated in the
FluidInference repository above.
MODELS
python3 - "$app/Contents/Resources/Licenses/ThirdPartyAudio.txt" <<'PYLICENSE'
from pathlib import Path
import sys, re
notices = []
for path in sorted(Path('Sources/WebRTCAEC/vendor').rglob('*')) + sorted(Path('Sources/WebRTCAEC/vendor-ns').rglob('*')):
    if path.is_file() and path.suffix in {'.h', '.c', '.cc', '.cpp'}:
        text = path.read_text()
        match = re.match(r'\s*(/\*.*?\*/|(?://[^\n]*\n)+)', text, re.S)
        if match:
            notice = match.group(0)
            if notice not in notices: notices.append(notice)
Path(sys.argv[1]).write_text('Third-party audio source notices\n\n' + '\n\n'.join(notices))
PYLICENSE
helper="$app/Contents/Library/LoginItems/Wallfly Detector.app"
mkdir -p "$helper/Contents/MacOS" "$helper/Contents/Resources"
cp .build/release/MeetingNotesDetector "$helper/Contents/MacOS/MeetingNotesDetector"
env_file=""
if [[ -z "${WALLFLY_RELEASE:-}" && -f "$PWD/.env" ]]; then env_file="$PWD/.env"; fi
python3 - "$app" "$env_file" <<'PY'
import plistlib, sys, pathlib
app=pathlib.Path(sys.argv[1])
info={
 'CFBundleName':'Wallfly','CFBundleDisplayName':'Wallfly',
 'CFBundleIdentifier':'io.github.constipn.wallfly','CFBundleExecutable':'MeetingNotes',
 'CFBundlePackageType':'APPL','CFBundleDevelopmentRegion':'en','CFBundleLocalizations':['en'],'CFBundleShortVersionString':'1.14.0','CFBundleVersion':'32',
 'CFBundleIconFile':'MeetingNotes.icns',
 'LSMinimumSystemVersion':'26.0','NSHighResolutionCapable':True,
 'LSApplicationCategoryType':'public.app-category.productivity',
 'CFBundleURLTypes':[{'CFBundleURLName':'io.github.constipn.wallfly.detected-meeting','CFBundleURLSchemes':['meetingnotes']}],
 'UTExportedTypeDeclarations':[{'UTTypeIdentifier':'io.github.constipn.wallfly.meeting-id','UTTypeDescription':'Wallfly meeting reference','UTTypeConformsTo':['public.data']}],
 'NSMicrophoneUsageDescription':'Records your voice separately and labels it as Me in the transcript.',
 'NSAudioCaptureUsageDescription':'Records meeting audio from Teams, Google Meet and other calls.',
 'NSScreenCaptureUsageDescription':'Records the system audio of your meeting. Screen images are not saved.',
 'NSAppTransportSecurity':{'NSAllowsLocalNetworking':True,'NSExceptionDomains':{'127.0.0.1':{'NSExceptionAllowsInsecureHTTPLoads':True}}}
}
if sys.argv[2]: info['MeetingNotesEnvFile']=sys.argv[2]
with (app/'Contents/Info.plist').open('wb') as f: plistlib.dump(info,f)
helper=app/'Contents/Library/LoginItems/Wallfly Detector.app'
with (helper/'Contents/Info.plist').open('wb') as f:
 plistlib.dump({
  'CFBundleName':'Wallfly Detector','CFBundleDisplayName':'Wallfly Detector',
  'CFBundleIdentifier':'io.github.constipn.wallfly.detector','CFBundleExecutable':'MeetingNotesDetector',
  'CFBundlePackageType':'APPL','CFBundleDevelopmentRegion':'en','CFBundleLocalizations':['en'],'CFBundleShortVersionString':'1.10.3','CFBundleVersion':'28',
  'CFBundleIconFile':'MeetingNotes.icns','LSMinimumSystemVersion':'26.0',
  'LSUIElement':True,'NSHighResolutionCapable':True,
  'NSAccessibilityUsageDescription':'Detects active Teams calls and offers to open Wallfly. Chat and meeting text is not read.'
 },f)
PY
swift -module-cache-path "$CLANG_MODULE_CACHE_PATH" scripts/make-icon.swift .build/MeetingNotes.iconset
python3 - "$app/Contents/Resources/MeetingNotes.icns" <<'PYICON'
import pathlib,struct,sys
root=pathlib.Path('.build/MeetingNotes.iconset')
entries=[('icp4','icon_16x16.png'),('icp5','icon_32x32.png'),('icp6','icon_32x32@2x.png'),('ic07','icon_128x128.png'),('ic08','icon_256x256.png'),('ic09','icon_512x512.png'),('ic10','icon_512x512@2x.png')]
body=b''
for code,name in entries:
 data=(root/name).read_bytes()
 body+=code.encode('ascii')+struct.pack('>I',len(data)+8)+data
pathlib.Path(sys.argv[1]).write_bytes(b'icns'+struct.pack('>I',len(body)+8)+body)
PYICON
cp "$app/Contents/Resources/MeetingNotes.icns" "$helper/Contents/Resources/MeetingNotes.icns"
./scripts/sign-app.sh "$helper" io.github.constipn.wallfly.detector
./scripts/sign-app.sh "$app"
mkdir -p "$PWD/dist"
final_app="$PWD/dist/Wallfly.app"
# Keep one compressed rollback copy instead of accumulating discoverable app bundles.
# Archive first: a failed archive leaves the current app intact.
if [[ -d "$final_app" ]]; then
    ditto -c -k --keepParent "$final_app" "$stage_dir/Wallfly-previous.zip"
    mv -f "$stage_dir/Wallfly-previous.zip" "$PWD/.build/Wallfly-previous.zip"
    mv "$final_app" "$stage_dir/previous.app"
fi
mv "$app" "$final_app"
if [[ -d "$PWD/dist/Meeting Notes.app" ]]; then
    ditto -c -k --keepParent "$PWD/dist/Meeting Notes.app" "$stage_dir/Meeting Notes-previous.zip"
    mv -f "$stage_dir/Meeting Notes-previous.zip" "$PWD/.build/Meeting Notes-previous.zip"
    mv "$PWD/dist/Meeting Notes.app" "$stage_dir/legacy.app"
fi
print "Built: $final_app"
