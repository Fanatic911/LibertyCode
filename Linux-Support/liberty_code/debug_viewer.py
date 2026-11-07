"""Debug window. The main app starts a second copy of itself (`python -m liberty_code --debug-viewer <file>`)
in a new console window. That copy only follows debug.log and prints it with colours, in real time."""
from __future__ import annotations

import os
import shlex
import shutil
import subprocess
import sys
import time
from typing import Optional

from . import logger

# ══ Launcher (main app side) ═══════════════════════════════════════════════════
_proc: Optional[subprocess.Popen] = None


def viewer_script(cmd: list, pythonpath: str) -> str:
    """One shell line that works in any fresh terminal (macOS Terminal, gnome-terminal's server...), whatever its
    environment: sets PYTHONPATH so `-m liberty_code` is importable, then replaces the shell by the viewer."""
    return "export PYTHONPATH=" + shlex.quote(pythonpath) + "; exec " + " ".join(shlex.quote(a) for a in cmd)


# (executable, arguments placed before the command to run). Specific terminals first, the Debian alternative last.
_LINUX_TERMINALS = (
    ("gnome-terminal", ["--"]), ("konsole", ["-e"]), ("xfce4-terminal", ["-x"]), ("mate-terminal", ["-x"]),
    ("tilix", ["-e"]), ("terminator", ["-x"]), ("kitty", []), ("alacritty", ["-e"]), ("wezterm", ["start", "--"]),
    ("foot", []), ("xterm", ["-e"]), ("x-terminal-emulator", ["-e"]),
)


def linux_terminal_commands(script: str) -> list:
    """Candidate argv lists to open `script` in a new terminal window ($TERMINAL first when the user set one)."""
    out = []
    user = os.environ.get("TERMINAL", "").strip()
    if user and shutil.which(user):
        out.append([user, "-e", "sh", "-c", script])
    for exe, flags in _LINUX_TERMINALS:
        path = shutil.which(exe)
        if path:
            out.append([path] + flags + ["sh", "-c", script])
    return out


def launch(file: str) -> bool:
    global _proc
    try:
        if _proc is not None and _proc.poll() is None:
            return True   # window already open
        cmd = [sys.executable, "-m", "liberty_code", "--debug-viewer", file, "--pid", str(os.getpid())]
        env = dict(os.environ)
        pkg_parent = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        env["PYTHONPATH"] = os.pathsep.join(x for x in (pkg_parent, env.get("PYTHONPATH", "")) if x)

        if os.name == "nt":
            _proc = subprocess.Popen(cmd, env=env, creationflags=subprocess.CREATE_NEW_CONSOLE)
            return True

        script = viewer_script(cmd, env["PYTHONPATH"])

        if sys.platform == "darwin":
            # Terminal.app starts a fresh login shell that does NOT inherit our environment: the script sets it.
            osa = script.replace("\\", "\\\\").replace('"', '\\"')
            _proc = subprocess.Popen(["osascript", "-e", f'tell application "Terminal" to do script "{osa}"',
                                      "-e", 'tell application "Terminal" to activate'],
                                     stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            return True

        # Linux: needs a graphical session; then the first terminal emulator that exists.
        if not (os.environ.get("DISPLAY") or os.environ.get("WAYLAND_DISPLAY")):
            logger.trace("ERROR", "No graphical session (DISPLAY / WAYLAND_DISPLAY): cannot open the debug window. "
                                  "Follow the log with: tail -f " + file)
            return False
        for argv in linux_terminal_commands(script):
            try:
                _proc = subprocess.Popen(argv, env=env, stdin=subprocess.DEVNULL,
                                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                return True
            except OSError:
                continue
        return False
    except Exception as ex:  # noqa: BLE001
        logger.trace("ERROR", "Could not open the debug window: " + str(ex))
        return False


def close_window() -> None:
    """Closes the debug window (called when LibertyCode exits)."""
    try:
        if _proc is not None and _proc.poll() is None:
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(_proc.pid), "/T", "/F"], capture_output=True, timeout=10)
            else:
                _proc.kill()
    except Exception:
        pass


# ══ Viewer (new window side) ═══════════════════════════════════════════════════
_COLORS = {
    "ERROR": "\x1b[31m", "STACK": "\x1b[38;5;88m", "WARN": "\x1b[33m", "REQ": "\x1b[36m", "RES": "\x1b[38;5;30m",
    "AI": "\x1b[32m", "TOOL": "\x1b[35m", "STEP": "\x1b[34m", "USER": "\x1b[97m", "MODEL": "\x1b[38;5;136m",
    "DEBUG": "\x1b[90m",
}
_DEFAULT = "\x1b[37m"
_RESET = "\x1b[0m"
_PATTERN = "dd:dd:dd.ddd ["
_PREFIX_LEN = 20


class _Viewer:
    def __init__(self) -> None:
        self.head = ""
        self.line_len = 0
        self.color = _DEFAULT

    @staticmethod
    def _could_be_prefix(h: str) -> bool:
        for i in range(min(len(h), len(_PATTERN))):
            p = _PATTERN[i]
            if (not h[i].isdigit()) if p == "d" else h[i] != p:
                return False
        return True

    def _flush(self) -> None:
        h = self.head
        self.head = ""
        if len(h) >= _PREFIX_LEN and self._could_be_prefix(h) and h[19] == "]":
            self.color = _COLORS.get(h[14:19].strip(), _DEFAULT)
        sys.stdout.write(self.color + h)

    def settle(self) -> None:
        """If the buffered start of a line can no longer be a timestamp prefix, print it now."""
        if not self.head:
            return
        if not self._could_be_prefix(self.head):
            sys.stdout.write(self.color + self.head)
            self.head = ""
            self.line_len = _PREFIX_LEN

    def feed(self, s: str) -> None:
        for ch in s:
            if ch == "\r":
                continue
            if ch == "\n":
                if self.head:
                    self._flush()
                sys.stdout.write("\n")
                self.line_len = 0
                continue
            if self.line_len < _PREFIX_LEN:
                self.head += ch
                self.line_len += 1
                if self.line_len == _PREFIX_LEN:
                    self._flush()
            else:
                sys.stdout.write(self.color + ch)
        self.settle()
        sys.stdout.flush()


def _is_alive(pid: int) -> bool:
    if pid <= 0:
        return True
    try:
        if os.name == "nt":
            import ctypes
            h = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid)   # PROCESS_QUERY_LIMITED_INFORMATION
            if not h:
                return False
            code = ctypes.c_ulong()
            ctypes.windll.kernel32.GetExitCodeProcess(h, ctypes.byref(code))
            ctypes.windll.kernel32.CloseHandle(h)
            return code.value == 259   # STILL_ACTIVE
        os.kill(pid, 0)
        return True
    except PermissionError:
        return True    # the process exists (it just is not ours)
    except OSError:
        return False


def run(file: str, parent_pid: int) -> None:
    try:
        _run_core(file, parent_pid)
    except Exception as ex:  # noqa: BLE001
        sys.stdout.write(f"\x1b[31mDebug window error: {ex!r}\x1b[0m\nPress Enter to close.\n")
        try:
            input()
        except Exception:
            pass


def _run_core(file: str, parent_pid: int) -> None:
    from . import ui
    ui.init()
    ui.set_title()
    sys.stdout.write("\x1b[33mLibertyCode  —  Debug Console\n==============================\x1b[0m\n")
    sys.stdout.write("\x1b[90mLive log: " + file + "\n")
    sys.stdout.write("Turn it off with /settings set debug false (this window closes by itself).\x1b[0m\n\n")
    sys.stdout.flush()

    # Wait for the file (it is created just before this window opens).
    for _ in range(50):
        if os.path.exists(file):
            break
        time.sleep(0.1)

    viewer = _Viewer()
    import codecs
    decoder = codecs.getincrementaldecoder("utf-8")(errors="replace")
    with open(file, "rb") as fs:
        while True:
            try:
                if os.path.getsize(file) < fs.tell():
                    fs.seek(0)   # file recreated (debug restarted)
            except OSError:
                pass
            data = fs.read(16384)
            if data:
                text = decoder.decode(data)
                close = logger.CLOSE_MARKER in text
                if close:
                    text = text.replace(logger.CLOSE_MARKER + "\n", "").replace(logger.CLOSE_MARKER, "")
                viewer.feed(text)
                if close:
                    sys.stdout.write(_RESET + "\x1b[90m-- debug mode off, closing --\x1b[0m\n")
                    sys.stdout.flush()
                    time.sleep(0.9)
                    return
                continue
            if not _is_alive(parent_pid):
                viewer.settle()
                sys.stdout.write(_RESET)
                return   # LibertyCode has exited: close this window too
            viewer.settle()
            sys.stdout.flush()
            time.sleep(0.06)
