"""Persistent settings (settings.json, same keys as the original C# version)."""
from __future__ import annotations

import json
import os
import re
import sys
from dataclasses import dataclass

from . import logger, loc, paths
from .models import PermissionMode


@dataclass
class AppSettings:
    Provider: str = ""
    Model: str = ""
    Workspace: str = ""
    Language: str = "en"
    AutoSelect: bool = False        # true: reopen the last project at startup, false: start on /project
    Notifications: bool = False     # desktop notification when the AI has finished answering
    Debug: bool = False             # live debug window
    Mode: PermissionMode = PermissionMode.ASK


def load() -> AppSettings:
    s = AppSettings()
    try:
        if os.path.exists(paths.SETTINGS_FILE):
            with open(paths.SETTINGS_FILE, "r", encoding="utf-8-sig") as f:
                d = json.load(f)
            s.Provider = str(d.get("Provider") or "")
            s.Model = str(d.get("Model") or "")
            s.Workspace = str(d.get("Workspace") or "")
            s.Language = str(d.get("Language") or "")
            s.AutoSelect = bool(d.get("AutoSelect", False))
            s.Notifications = bool(d.get("Notifications", False))
            s.Debug = bool(d.get("Debug", False))
            try:
                mode = d.get("Mode", "Ask")
                s.Mode = PermissionMode(mode) if isinstance(mode, str) else PermissionMode.ASK
            except ValueError:
                s.Mode = PermissionMode.ASK
    except Exception:
        s = AppSettings(Language="en")

    # Always default to English; the user can change with /settings set Language fr
    if not s.Language.strip():
        s.Language = "en"
    loc.lang = s.Language.lower()
    # An empty workspace means "no project open" (the /project hub).
    if not s.Workspace.strip() or not os.path.isdir(s.Workspace):
        s.Workspace = ""
    return s


def save(s: AppSettings) -> None:
    try:
        os.makedirs(paths.DATA_DIR, exist_ok=True)
        d = {
            "Provider": s.Provider, "Model": s.Model, "Workspace": s.Workspace, "Language": s.Language,
            "AutoSelect": s.AutoSelect, "Notifications": s.Notifications, "Debug": s.Debug,
            "Mode": s.Mode.value,
        }
        with open(paths.SETTINGS_FILE, "w", encoding="utf-8") as f:
            json.dump(d, f, indent=2, ensure_ascii=False)
    except Exception as ex:
        logger.error("Could not save settings: " + str(ex))
