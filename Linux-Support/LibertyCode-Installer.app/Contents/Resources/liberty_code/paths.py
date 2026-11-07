r"""Where LibertyCode keeps its data. No admin rights needed: everything lives in the user profile.

  Windows  %APPDATA%\LibertyCode
  macOS    ~/Library/Application Support/LibertyCode   (or ~/.config/LibertyCode if an earlier version made it)
  Linux    $XDG_CONFIG_HOME/LibertyCode, else ~/.config/LibertyCode
Set LIBERTY_HOME to put everything in one folder of your choice."""
from __future__ import annotations

import os
import sys
from pathlib import Path

_override = os.environ.get("LIBERTY_HOME", "").strip()


def _app_data() -> str:
    home = Path.home()
    if os.name == "nt":
        return os.environ.get("APPDATA") or str(home / "AppData" / "Roaming")
    if sys.platform == "darwin":
        native = home / "Library" / "Application Support"
        # Earlier versions used ~/.config on macOS: keep that folder if it is the only one that exists.
        legacy = home / ".config"
        if (legacy / "LibertyCode").is_dir() and not (native / "LibertyCode").is_dir():
            return str(legacy)
        return str(native)
    xdg = os.environ.get("XDG_CONFIG_HOME", "").strip()
    return xdg if xdg and os.path.isabs(xdg) else str(home / ".config")


DATA_DIR = _override if _override else os.path.join(_app_data(), "LibertyCode")
PROJECTS_DIR = (os.path.join(_override, "Projects") if _override
                else os.path.join(str(Path.home()), "LibertyCode", "Projects"))

SETTINGS_FILE = os.path.join(DATA_DIR, "settings.json")
ENV_FILE = os.path.join(DATA_DIR, ".env.enc")
LOG_FILE = os.path.join(DATA_DIR, "libertycode.log")
DEBUG_FILE = os.path.join(DATA_DIR, "debug.log")
PROMPT_FILE = os.path.join(DATA_DIR, "prompt.txt")
HISTORY_FILE = os.path.join(DATA_DIR, "history.txt")


def ensure_directories() -> None:
    os.makedirs(DATA_DIR, exist_ok=True)
    os.makedirs(PROJECTS_DIR, exist_ok=True)
    if os.name != "nt":
        try:
            os.chmod(DATA_DIR, 0o700)   # holds the encrypted API keys, the history and the logs: private to the user
        except OSError:
            pass
