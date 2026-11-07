"""Desktop notification when the AI has answered. Fire-and-forget: it must never block or crash the app.

  Windows  PowerShell toast            macOS  osascript (Notification Center)
  Linux    notify-send, else kdialog   WSL    Windows toast through powershell.exe
Without a graphical session (SSH, container) or without any of these tools, the terminal bell rings instead."""
from __future__ import annotations

import base64
import os
import re
import shutil
import subprocess
import sys
import threading
from typing import Optional

from . import logger

# Title and body travel in environment variables, so no text is ever pasted into a script (no quoting issues).
_WINDOWS_SCRIPT = r"""
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
$xml = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
$text = $xml.GetElementsByTagName('text')
$text.Item(0).AppendChild($xml.CreateTextNode($env:LC_TITLE)) > $null
$text.Item(1).AppendChild($xml.CreateTextNode($env:LC_BODY)) > $null
$toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
$appId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show($toast)
"""

_MAC_SCRIPT = 'display notification (system attribute "LC_BODY") with title (system attribute "LC_TITLE")'


def _clean(s: str) -> str:
    s = (s or "").replace("`", "").replace("**", "").replace("#", "")
    s = re.sub(r"\s+", " ", s).strip()
    if not s:
        return "Done."
    return s[:139] + "…" if len(s) > 140 else s


def _is_wsl() -> bool:
    if os.environ.get("WSL_DISTRO_NAME") or os.environ.get("WSL_INTEROP"):
        return True
    try:
        with open("/proc/version", "r", encoding="utf-8") as f:
            return "microsoft" in f.read().lower()
    except Exception:
        return False


def _has_session() -> bool:
    """A graphical / D-Bus session to deliver the notification to (false over plain SSH or in a container)."""
    return bool(os.environ.get("DISPLAY") or os.environ.get("WAYLAND_DISPLAY")
                or os.environ.get("DBUS_SESSION_BUS_ADDRESS"))


def _windows_command() -> list:
    enc = base64.b64encode(_WINDOWS_SCRIPT.encode("utf-16-le")).decode("ascii")
    return ["powershell.exe", "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-EncodedCommand", enc]


def _command(title: str, body: str, env: dict) -> Optional[list]:
    """The command that shows the notification on this system, or None when there is no way to (-> bell).
    May add variables to env."""
    if os.name == "nt":
        return _windows_command()
    if sys.platform == "darwin":
        return ["osascript", "-e", _MAC_SCRIPT]
    if _is_wsl() and shutil.which("powershell.exe"):
        wslenv = env.get("WSLENV", "")
        env["WSLENV"] = "LC_TITLE:LC_BODY" + (":" + wslenv if wslenv else "")
        return _windows_command()
    if not _has_session():
        return None
    if shutil.which("notify-send"):
        return ["notify-send", "-a", "LibertyCode", "--", title, body]
    if shutil.which("kdialog"):
        return ["kdialog", "--title", title, "--passivepopup", body, "5"]
    return None


def _bell() -> None:
    try:
        sys.stdout.write("\a")
        sys.stdout.flush()
    except Exception:
        pass


def send(title: str, body: str) -> None:
    try:
        body = _clean(body)
        env = dict(os.environ, LC_TITLE=title, LC_BODY=body)
        cmd = _command(title, body, env)
        if cmd is None:
            _bell()
            return
        kwargs: dict = dict(env=env, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        if os.name == "nt":
            kwargs["creationflags"] = getattr(subprocess, "CREATE_NO_WINDOW", 0)
        p = subprocess.Popen(cmd, **kwargs)

        def reap() -> None:
            try:
                p.communicate(timeout=10)
            except Exception:
                try:
                    p.kill()
                except Exception:
                    pass

        threading.Thread(target=reap, daemon=True).start()
    except Exception as ex:
        logger.warn("Notification failed: " + str(ex))
        _bell()
