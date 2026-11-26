#!/bin/zsh
set -euo pipefail
cd "${0:A:h:h}"
umask 077
app_path="${1:?Pass the app bundle to sign}"
bundle_id="${2:-io.github.constipn.wallfly}"
if [[ "$bundle_id" != io.github.constipn.wallfly && "$bundle_id" != io.github.constipn.wallfly.detector && "$bundle_id" != io.github.constipn.wallfly.mcp ]]; then
    print -u2 'Unexpected signing identifier.'
    exit 1
fi
requirement_stem="requirements"
if [[ "$bundle_id" == io.github.constipn.wallfly.detector ]]; then requirement_stem="detector-requirements"; fi
if [[ "$bundle_id" == io.github.constipn.wallfly.mcp ]]; then requirement_stem="mcp-requirements"; fi
sign_dir="${WALLFLY_SIGNING_DIR:-$PWD/.local-signing}"
# Release builds must use an existing identity at an explicit absolute path, never a fresh one.
if [[ -n "${WALLFLY_RELEASE:-}" ]]; then
    if [[ "$sign_dir" != /* ]]; then
        print -u2 "Release build: WALLFLY_SIGNING_DIR must be an absolute path (got: $sign_dir)."
        exit 1
    fi
    if [[ ! -f "$sign_dir/identity.crt" || ! -f "$sign_dir/identity.key" ]]; then
        print -u2 "Release build: identity.crt or identity.key is missing in $sign_dir. Refusing to generate a new identity."
        exit 1
    fi
fi
tool_dir="$PWD/.build/tools"
archive="$tool_dir/apple-codesign-0.29.0.tar.gz"
signer="$tool_dir/apple-codesign-0.29.0-aarch64-apple-darwin/rcodesign"
mkdir -p "$tool_dir" "$sign_dir"
chmod 700 "$sign_dir"
if [[ ! -x "$signer" ]]; then
    curl --fail --location --silent --show-error --max-time 120 \
        'https://github.com/indygreg/apple-platform-rs/releases/download/apple-codesign%2F0.29.0/apple-codesign-0.29.0-aarch64-apple-darwin.tar.gz' --output "$archive"
fi
if [[ -f "$archive" ]]; then
    python3 - "$archive" <<'PY'
import hashlib,sys
expected='d1a532150adaf90048260d76359261aa716abafc45c53c5dc18845029184334a'
assert hashlib.sha256(open(sys.argv[1],'rb').read()).hexdigest()==expected, 'Signing-tool archive checksum mismatch'
PY
fi
if [[ ! -x "$signer" ]]; then tar -xzf "$archive" -C "$tool_dir"; fi
# A partial identity must never silently create a new permission identity.
if [[ -f "$sign_dir/identity.key" && ! -f "$sign_dir/identity.crt" ]] || [[ ! -f "$sign_dir/identity.key" && -f "$sign_dir/identity.crt" ]]; then
    print -u2 "Local signing identity is incomplete. Restore $sign_dir from backup."
    exit 1
fi
if [[ ! -f "$sign_dir/identity.key" ]]; then
    for existing_app in "$PWD/dist/Wallfly.app" "$PWD/dist/Meeting Notes.app"; do
        if [[ ! -d "$existing_app" ]]; then continue; fi
        existing_requirement=$(codesign -d -r- "$existing_app" 2>&1)
        if [[ "$existing_requirement" == *'certificate root = H'* ]]; then
            print -u2 "The existing app uses a persistent signing identity. Restore $sign_dir instead of generating a new one."
            exit 1
        fi
    done
    openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out "$sign_dir/identity.key"
    cat > "$sign_dir/certificate.conf" <<'CONF'
[req]
distinguished_name=subject
x509_extensions=code_signing
prompt=no
[subject]
CN=Wallfly Local Signing
O=Local Development
[code_signing]
basicConstraints=critical,CA:TRUE
keyUsage=critical,digitalSignature,keyCertSign
extendedKeyUsage=codeSigning
subjectKeyIdentifier=hash
authorityKeyIdentifier=keyid:always
CONF
    openssl req -new -x509 -key "$sign_dir/identity.key" -out "$sign_dir/identity.crt" -days 3650 -config "$sign_dir/certificate.conf"
fi
chmod 600 "$sign_dir/identity.key" "$sign_dir/identity.crt"
python3 - "$sign_dir" "$bundle_id" "$requirement_stem" <<'PY'
from pathlib import Path
import subprocess,sys
root=Path(sys.argv[1])
hash_value=subprocess.check_output(['openssl','x509','-in',str(root/'identity.crt'),'-noout','-fingerprint','-sha1'],text=True).strip().split('=',1)[1].replace(':','').lower()
requirement='identifier "'+sys.argv[2]+'" and anchor = H"'+hash_value+'"'
(root/(sys.argv[3]+'.txt')).write_text(requirement+'\n')
subprocess.run(['csreq','-r','='+requirement,'-b',str(root/(sys.argv[3]+'.bin'))],check=True)
PY
# File-based signing keeps the key out of the app and avoids modifying any keychain
# or trust settings. No remote timestamp/account service is contacted.
nested_settings=()
if [[ "$bundle_id" == io.github.constipn.wallfly && -f "$app_path/Contents/MacOS/wallfly-mcp" ]]; then
    nested_settings=(--binary-identifier "Contents/MacOS/wallfly-mcp:io.github.constipn.wallfly.mcp"
        --code-requirements-file "Contents/MacOS/wallfly-mcp:$sign_dir/mcp-requirements.bin")
fi
"$signer" sign --config-file /dev/null --pem-source "$sign_dir/identity.crt" --pem-source "$sign_dir/identity.key" \
    --timestamp-url none --binary-identifier "$bundle_id" --shallow --code-requirements-file "$sign_dir/$requirement_stem.bin" "${nested_settings[@]}" "$app_path"
python3 scripts/verify-signature.py "$app_path" "$sign_dir/identity.crt" "$sign_dir/$requirement_stem.bin"
