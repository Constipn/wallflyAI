#!/bin/zsh
set -euo pipefail
cd "${0:A:h:h}"
if pgrep -x MeetingNotes >/dev/null; then
    print 'Please quit Wallfly completely with Cmd+Q first, then run this repair again.'
    exit 1
fi
app_path="$PWD/dist/Wallfly.app"
# Refuse to reset anything unless the corrected, persistently signed app is ready.
python3 scripts/verify-signature.py "$app_path" "$PWD/.local-signing/identity.crt" "$PWD/.local-signing/requirements.bin"
print 'Old screen and microphone permissions are reset for Wallfly only.'
print 'Other apps and saved meetings are not changed.'
/usr/bin/tccutil reset ScreenCapture io.github.constipn.wallfly
/usr/bin/tccutil reset Microphone io.github.constipn.wallfly
print 'On the next launch, please grant the screen/audio and microphone permissions once.'
open "$app_path"
