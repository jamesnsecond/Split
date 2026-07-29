#!/usr/bin/env bash
#
# Builds the Windows app. Runs on macOS — .NET cross-compiles the Windows
# binary, which is the whole reason this project is C# and not C++.
#
#   ./build.sh            compile Split.exe into build/
#   ./build.sh release    also produce the dist/ zip that goes on a release
#
# Same shape as the macOS build.sh next door: no project files to open, no
# IDE, one script.

set -euo pipefail

cd "$(dirname "$0")"

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
if [[ ! -x "$DOTNET" ]]; then
    if command -v dotnet >/dev/null 2>&1; then
        DOTNET="$(command -v dotnet)"
    else
        echo "error: no dotnet SDK found (looked in ~/.dotnet and PATH)." >&2
        exit 1
    fi
fi

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Split.csproj | head -1)"
if [[ -z "$VERSION" ]]; then
    echo "error: couldn't read <Version> out of Split.csproj." >&2
    exit 1
fi

BUILD_DIR="build"
DIST_DIR="dist"

echo "==> Split $VERSION (win-x64)"

rm -rf "$BUILD_DIR" obj bin
"$DOTNET" publish -c Release -o "$BUILD_DIR" --nologo -v minimal

EXE="$BUILD_DIR/Split.exe"
if [[ ! -f "$EXE" ]]; then
    echo "error: publish finished but $EXE is missing." >&2
    exit 1
fi

# Guard against a framework-dependent build sneaking out: those need a .NET
# install on the user's machine, and this app ships ready to run.
SIZE_BYTES="$(stat -f%z "$EXE" 2>/dev/null || stat -c%s "$EXE")"
if (( SIZE_BYTES < 20000000 )); then
    echo "error: $EXE is only $((SIZE_BYTES / 1000000))MB — that's not self-contained." >&2
    echo "       Check SelfContained/PublishSingleFile in Split.csproj." >&2
    exit 1
fi

echo "==> Built $EXE ($((SIZE_BYTES / 1048576))MB)"

if [[ "${1:-}" == "release" ]]; then
    rm -rf "$DIST_DIR"
    mkdir -p "$DIST_DIR"

    STAGE="$DIST_DIR/Split-$VERSION-windows-x64"
    mkdir -p "$STAGE"
    cp "$EXE" "$STAGE/"
    cp README.md "$STAGE/README.md"
    cp ../LICENSE "$STAGE/LICENSE"

    (cd "$DIST_DIR" && zip -qr "Split-$VERSION-windows-x64.zip" "Split-$VERSION-windows-x64")
    rm -rf "$STAGE"

    echo "==> $DIST_DIR/Split-$VERSION-windows-x64.zip"
    echo
    echo "    Not code-signed — there is no Windows signing certificate on this Mac,"
    echo "    so SmartScreen will show 'Windows protected your PC' on first run."
    echo "    The README tells users to click More info -> Run anyway."
fi
