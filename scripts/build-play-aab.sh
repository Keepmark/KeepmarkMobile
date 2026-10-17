#!/usr/bin/env bash
# Play Store upload bundle: Release AAB signed with the upload key kept outside
# the repo (~/keys/keepmark-upload.keystore, alias "keepmark").
#
# The keystore password is never written on the command line, so it cannot end up
# in the shell history or in `ps`:
#   1. KEEPMARK_KEY_PASS already exported (CI secret) -> used as is;
#   2. else KEEPMARK_KEY_PASS_FILE (mode 600 file, first line) -> used;
#   3. else asked interactively here, without echo.
# The variable is exported only inside this process; the parent shell never sees
# it (it stays readable in /proc/<pid>/environ of the child, as for any msbuild
# secret).
#
# Usage (from KeepmarkMobile/):
#   scripts/build-play-aab.sh              # prompt, then build and verify
#   scripts/build-play-aab.sh --dry-run    # print the command, build nothing
set -euo pipefail

cd "$(dirname "$0")/.."

DRY_RUN=0
case "${1:-}" in
  --dry-run) DRY_RUN=1 ;;
  "") ;;
  *) echo "Usage: $0 [--dry-run]" >&2; exit 2 ;;
esac

PROJECT=src/KeepmarkMobile
FRAMEWORK=net10.0-android
OUT="$PROJECT/bin/Release/$FRAMEWORK/com.keepmark.mobile-Signed.aab"
KEYSTORE="${KEEPMARK_KEYSTORE:-$HOME/keys/keepmark-upload.keystore}"
ALIAS="${KEEPMARK_KEY_ALIAS:-keepmark}"

fail() { echo "$1" >&2; exit 1; }

[[ -f "$KEYSTORE" ]] || fail "Keystore not found: $KEYSTORE (set KEEPMARK_KEYSTORE)"
[[ -n "${JAVA_HOME:-}"  || ! -d "$HOME/jdk-21" ]]        || export JAVA_HOME="$HOME/jdk-21"
[[ -n "${ANDROID_HOME:-}" || ! -d "$HOME/android-sdk" ]] || export ANDROID_HOME="$HOME/android-sdk"
[[ -z "${JAVA_HOME:-}" ]] || export PATH="$JAVA_HOME/bin:$PATH"
[[ -n "${ANDROID_HOME:-}" ]] || fail "ANDROID_HOME is not set and $HOME/android-sdk is missing"

if [[ -z "${KEEPMARK_KEY_PASS:-}" && -n "${KEEPMARK_KEY_PASS_FILE:-}" ]]; then
  [[ -f "$KEEPMARK_KEY_PASS_FILE" ]] || fail "KEEPMARK_KEY_PASS_FILE not found: $KEEPMARK_KEY_PASS_FILE"
  KEEPMARK_KEY_PASS=$(head -n1 "$KEEPMARK_KEY_PASS_FILE")
  [[ $(stat -c '%a' "$KEEPMARK_KEY_PASS_FILE") == 600 ]] || \
    echo "warning: $KEEPMARK_KEY_PASS_FILE is not mode 600" >&2
  export KEEPMARK_KEY_PASS
fi

if (( ! DRY_RUN )) && [[ -z "${KEEPMARK_KEY_PASS:-}" ]]; then
  if [[ -t 0 ]]; then
    read -rs -p "Password of $(basename "$KEYSTORE") (alias $ALIAS): " KEEPMARK_KEY_PASS
    echo
  else
    read -rs KEEPMARK_KEY_PASS   # no terminal: read one line from the pipe
  fi
  export KEEPMARK_KEY_PASS
fi

cmd=(dotnet build "$PROJECT" -f "$FRAMEWORK" -c Release -t:SignAndroidPackage
     -p:AndroidKeyStore=true
     -p:AndroidSigningKeyStore="$KEYSTORE"
     -p:AndroidSigningKeyAlias="$ALIAS")

if (( DRY_RUN )); then
  echo "${cmd[*]}"
  echo "keystore: $KEYSTORE (alias $ALIAS)"
  [[ -n "${KEEPMARK_KEY_PASS:-}" ]] && echo "password: from environment" || echo "password: asked on the terminal"
  exit 0
fi

start=$(date +%s)
"${cmd[@]}"

echo
[[ -f "$OUT" ]] || fail "Expected artifact missing: $OUT"
[[ $(stat -c %Y "$OUT") -ge $start ]] || \
  fail "Stale artifact: $OUT was not rewritten by this build (a failed signing keeps the previous file)"
ls -l "$OUT"
sha256sum "$OUT"
keytool -printcert -jarfile "$OUT" | grep -E "Owner|Valid from|SHA256" | head -4 || true
