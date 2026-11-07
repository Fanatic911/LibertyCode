# LibertyCode — Linux & macOS Support

This folder contains the Linux and macOS versions of LibertyCode.

---

## Installation — Debian / Ubuntu

**Requirements:** Debian 11+ or Ubuntu 20.04+ · `sudo` access

```bash
sudo apt install ./libertycode_1.0.0_all.deb
```

That's it. `apt` installs Python 3 automatically, then the package sets up the virtual environment, installs all dependencies, and creates the `libertycode` command.

The installer files in this folder are removed automatically after installation.

**Run:**
```bash
libertycode        # project hub
libertycode .      # current folder as workspace
```

**Uninstall:**
```bash
sudo apt remove libertycode
```

---

## Installation — macOS

**Requirements:** macOS 11 (Big Sur) or newer

Double-click **`LibertyCode-Installer.app`** in Finder.

Terminal opens and handles everything:
- Installs Python 3.11 via Homebrew (and Homebrew itself if needed)
- Creates a virtual environment in `~/Library/Application Support/LibertyCode/.venv`
- Installs all Python dependencies
- Creates `~/Applications/LibertyCode.app`
- Deletes the installer

> **Gatekeeper:** right-click → **Open** → **Open** to bypass the unidentified developer warning.

**Run:**
```bash
open ~/Applications/LibertyCode.app
# or
~/Applications/LibertyCode.app/Contents/MacOS/libertycode
```

---

## Contents

| File / Folder | Description |
|---|---|
| `libertycode_1.0.0_all.deb` | Debian/Ubuntu package |
| `LibertyCode-Installer.app` | macOS installer bundle |
| `liberty_code/` | Python source module |
| `requirements.txt` | Python dependencies |
| `package/` | Windows helper scripts (`.bat`) |
| `SECURITY.md` | Security policy |

---

## Manual Installation (any Linux)

If you are not on Debian/Ubuntu, you can install manually:

```bash
# 1. Ensure Python 3.10+ is installed
python3 --version

# 2. Create a virtual environment
python3 -m venv .venv
source .venv/bin/activate

# 3. Install dependencies
pip install -r requirements.txt

# 4. Run
python3 -m liberty_code
```

---

For the full documentation, see the main [README.md](../README.md).  
For security information, see [SECURITY.md](SECURITY.md).
