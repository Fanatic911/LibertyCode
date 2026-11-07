"""The folder the agent works in (every file path is confined to it) + the undo stack."""
from __future__ import annotations

import os
import re
from typing import Optional

from .models import ToolError

_HINT = "Do not retry with another path or command. Tell the user you can only work inside the workspace."

_WIN = os.name == "nt"
_SEPS = "\\/" if _WIN else "/"

if _WIN:
    _OUTSIDE_RX = re.compile(
        r"""(?<!\w)(?:[A-Za-z]:(?=[\\/]|\s|$|["'])|\\\\)[^\s"'|&<>;]*"""
        r"""|%(?:userprofile|appdata|localappdata|homedrive|homepath|systemroot|systemdrive|windir|programfiles|programdata|public)%"""
        r"""|(?<![\w.~])\.\.(?=[\\/]|$|\s|["'])|(?:(?<=\s)|^)~(?=[\\/]|\s|$)""",
        re.IGNORECASE)
else:
    # Linux / macOS: absolute paths (/etc/passwd), $HOME, ~ and ~user, .., file:// URLs.
    # An absolute path must follow a space, quote, '=' ... and not a word character, '.', ':' or '*' (so that
    # 'a/b', './x', 'https://host/p', 'sed s/a/b/', '*/node_modules/*' and '10 / 2' are not mistaken for paths).
    _OUTSIDE_RX = re.compile(
        r"""\bfile:/{1,3}[^\s"'|&<>;)]*"""
        r"""|(?<![\w.~:/\-*)}\]])/(?=[\w.~$@+%-])[^\s"'|&<>;)]*"""
        r"""|(?<=\bcd )/(?=\s|$|[;&|])|(?<=\s)/(?=$|[;&|)])"""   # a lone "/" ("cd /", "ls /"), but not "10 / 2"
        r"""|\$\{?(?:HOME|OLDPWD|TMPDIR|XDG_\w+)\b\}?"""
        r"""|(?<![\w.~])\.\.(?=/|$|\s|["'])"""
        r"""|(?:(?<=\s)|^)~[\w.-]*(?=/|\s|$|[;&|)])""")

_POSIX_OK = {"/dev/null", "/dev/stdin", "/dev/stdout", "/dev/stderr"}


class Workspace:
    def __init__(self, root: str):
        self.root = os.path.realpath(root).rstrip(_SEPS) or os.path.realpath(root)

    def _inside(self, full: str) -> bool:
        a, b = os.path.normcase(full.rstrip(_SEPS)), os.path.normcase(self.root)
        return a == b or a.startswith(b + os.sep)

    def resolve(self, path: str) -> str:
        if not path or not path.strip():
            raise ToolError("Missing path.")
        lexical = os.path.abspath(path if os.path.isabs(path) else os.path.join(self.root, path))
        if not self._inside(lexical) and not self._inside(os.path.realpath(lexical)):
            raise ToolError(f"Access denied: '{path}' is outside the workspace.", _HINT)
        # a link or junction inside the workspace must not lead out of it
        real = os.path.realpath(lexical)
        if not self._inside(real):
            raise ToolError(f"Access denied: '{path}' leads outside the workspace.", _HINT)
        return real

    def outside_path(self, command: str) -> Optional[str]:
        """The first thing in a shell command that points outside the workspace (other drive, parent folder,
        home...), or None."""
        for m in _OUTSIDE_RX.finditer(command):
            v = m.group(0)
            if not _WIN:
                v = v.rstrip("`,.")
                if v in _POSIX_OK:
                    continue
                if v.lower().startswith("file:"):
                    return v
                absolute = v.startswith("/")
            else:
                absolute = len(v) >= 2 and (v[1] == ":" or v.startswith("\\\\"))
            if absolute:
                try:
                    if self._inside(os.path.abspath(v)):
                        continue
                except Exception:
                    pass
            return v
        return None

    def rel(self, full: str) -> str:
        r = os.path.relpath(full, self.root)
        return "." if r == "." else r


class UndoStack:
    """Remembers file contents before each change so /undo can restore them."""

    def __init__(self) -> None:
        self._turns: list = []
        self._cur: Optional[list] = None

    def begin_turn(self) -> None:
        self._cur = None

    def record(self, path: str) -> None:
        if self._cur is None:
            self._cur = []
            self._turns.append(self._cur)
        if any(p == path for p, _ in self._cur):
            return
        data = None
        if os.path.isfile(path):
            with open(path, "rb") as f:
                data = f.read()
        self._cur.append((path, data))

    def undo_last(self) -> list:
        """Undoes the last turn that changed files. Returns (path, 'restored'|'removed'|'failed: ...') lines."""
        report = []
        if not self._turns:
            return report
        turn = self._turns.pop()
        self._cur = None
        for path, data in reversed(turn):
            try:
                if data is None:
                    if os.path.exists(path):
                        os.remove(path)
                    report.append((path, "removed"))
                else:
                    os.makedirs(os.path.dirname(path), exist_ok=True)
                    with open(path, "wb") as f:
                        f.write(data)
                    report.append((path, "restored"))
            except Exception as ex:
                report.append((path, "failed: " + str(ex)))
        return report
