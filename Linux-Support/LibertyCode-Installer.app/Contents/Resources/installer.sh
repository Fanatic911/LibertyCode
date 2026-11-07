#!/bin/sh
set -e
BUNDLE_DIR="$(cd "$(dirname "$0")/../.." && pwd)"
INSTALL_DIR="$HOME/Library/Application Support/LibertyCode"
VENV="$INSTALL_DIR/.venv"

echo ""
echo "╔══════════════════════════════════════════╗"
echo "║      LibertyCode — macOS Installer       ║"
echo "╚══════════════════════════════════════════╝"
echo ""

mkdir -p "$INSTALL_DIR"

# ── 1. Find Python 3.10+ ─────────────────────────────────────────────────────
PY=""
for c in python3.13 python3.12 python3.11 python3.10 python3; do
    if command -v "$c" >/dev/null 2>&1 && \
       "$c" -c 'import sys; sys.exit(0 if sys.version_info>=(3,10) else 1)' 2>/dev/null; then
        PY="$c"; break
    fi
done

# ── 2. Install Python via Homebrew if missing ─────────────────────────────────
if [ -z "$PY" ]; then
    echo "[INFO] Python 3.10+ not found."
    if ! command -v brew >/dev/null 2>&1; then
        echo "[INFO] Installing Homebrew..."
        /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
        [ -f /opt/homebrew/bin/brew ] && eval "$(/opt/homebrew/bin/brew shellenv)"
        [ -f /usr/local/bin/brew ]    && eval "$(/usr/local/bin/brew shellenv)"
    fi
    echo "[INFO] Installing Python 3.11 via Homebrew..."
    brew install python@3.11
    PY="python3.11"
fi
echo "[OK] Python: $($PY --version 2>&1) ($PY)"

# ── 3. Copy module from bundle ────────────────────────────────────────────────
cp -r "$BUNDLE_DIR/Contents/Resources/liberty_code" "$INSTALL_DIR/"
cp    "$BUNDLE_DIR/Contents/Resources/requirements.txt" "$INSTALL_DIR/"

# ── 4. Create venv and install dependencies ───────────────────────────────────
if [ ! -x "$VENV/bin/python" ]; then
    echo "[INFO] Creating venv..."
    rm -rf "$VENV"
    "$PY" -m venv "$VENV"
fi
echo "[INFO] Installing Python packages..."
"$VENV/bin/python" -m pip install --upgrade pip -q
"$VENV/bin/python" -m pip install -r "$INSTALL_DIR/requirements.txt" -q

# ── 5. Create LibertyCode.app launcher ───────────────────────────────────────
LAUNCHER_BUNDLE="$HOME/Applications/LibertyCode.app"
mkdir -p "$LAUNCHER_BUNDLE/Contents/MacOS"

cat > "$LAUNCHER_BUNDLE/Contents/MacOS/libertycode" << LAUNCHEXEC
#!/bin/sh
INSTALL_DIR="\$HOME/Library/Application Support/LibertyCode"
VENV="\$INSTALL_DIR/.venv"
if [ ! -x "\$VENV/bin/python" ]; then
    osascript -e 'display alert "LibertyCode" message "Venv not found. Please reinstall LibertyCode." as critical'
    exit 1
fi
osascript << TERM
tell application "Terminal"
    activate
    do script "PYTHONPATH=\"\$INSTALL_DIR\" \"\$VENV/bin/python\" -m liberty_code; exec bash"
end tell
TERM
LAUNCHEXEC
chmod +x "$LAUNCHER_BUNDLE/Contents/MacOS/libertycode"

cat > "$LAUNCHER_BUNDLE/Contents/Info.plist" << LINFO
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
  "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key><string>libertycode</string>
    <key>CFBundleIdentifier</key><string>xyz.misterfanatic.libertycode</string>
    <key>CFBundleName</key><string>LibertyCode</string>
    <key>CFBundleVersion</key><string>1.0.0</string>
    <key>CFBundleShortVersionString</key><string>1.0.0</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>LSMinimumSystemVersion</key><string>11.0</string>
</dict>
</plist>
LINFO

# ── 6. Remove installer bundle ────────────────────────────────────────────────
echo "[INFO] Cleaning up installer..."
rm -rf "$BUNDLE_DIR"

echo ""
echo "[OK] LibertyCode installed successfully!"
echo "     Launcher: ~/Applications/LibertyCode.app"
echo ""
echo "Press Enter to close..."
read -r _
