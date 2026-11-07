"""Runs shell commands for the run_command tool and for !<cmd>."""
from __future__ import annotations

import os
import re
import shutil
import subprocess
import threading
import time

from . import logger
from .models import CancelToken, Cancelled

_DANGEROUS = re.compile(
    r"(^|[\s;&|(])(rm\s+-[a-z]*(rf|fr)|rm\s+-[a-z]*r\b|rmdir\s+/s|rd\s+/s|del\s+.*/[sfq]|format\s+[a-z]:"
    r"|remove-item\s+.*-recurse|shutdown|mkfs|dd\s+if=|reg\s+delete|diskpart|set-executionpolicy"
    r"|sudo|doas|diskutil\s+(erase|partition|secure)|chown\s+-[a-z]*r|chmod\s+-[a-z]*r|reboot|halt|poweroff"
    r"|git\s+push\s+.*(--force|\s-f\b)|git\s+reset\s+--hard|git\s+clean\s+-[a-z]*f)"
    r"|(curl|wget|iwr|irm)\s.*\|\s*(sh|bash|iex)",
    re.IGNORECASE)

_SAFE = re.compile(
    r"^(git\s+(status|diff|log|branch|show|rev-parse)(\s+[\w\-./=:@^~]+)*|ls(\s+[\w\-./]+)*"
    r"|dir(\s+[\w\-./\\:*?]+)*|pwd|whoami|dotnet\s+--(version|info|list-sdks)|node\s+(-v|--version)"
    r"|python3?\s+(-V|--version)|npm\s+(-v|--version))$",
    re.IGNORECASE)


_unix_shell: str = ""


def unix_shell() -> str:
    """bash when it exists (models write bashisms: [[ ]], source, <<<, {1..3}...; on Debian/Ubuntu /bin/sh is dash)."""
    global _unix_shell
    if not _unix_shell:
        _unix_shell = shutil.which("bash") or "/bin/sh"
    return _unix_shell


def unix_shell_name() -> str:
    return "bash" if os.path.basename(unix_shell()) == "bash" else "sh (POSIX, no bashisms)"


def is_dangerous(command: str) -> bool:
    return bool(_DANGEROUS.search(command))


def is_safe_read_only(command: str) -> bool:
    return bool(_SAFE.match(command.strip()))


def _kill_tree(p: subprocess.Popen) -> None:
    try:
        if os.name == "nt":
            subprocess.run(["taskkill", "/PID", str(p.pid), "/T", "/F"], capture_output=True, timeout=10)
        else:
            import signal
            pgid = os.getpgid(p.pid)
            os.killpg(pgid, signal.SIGTERM)          # let tools clean up (temp files, child servers...)
            end = time.monotonic() + 1.0
            while p.poll() is None and time.monotonic() < end:
                time.sleep(0.05)
            try:
                os.killpg(pgid, signal.SIGKILL)       # then make sure nothing survives in the group
            except ProcessLookupError:
                pass
    except Exception:
        try:
            p.kill()
        except Exception:
            pass


def run(command: str, cwd: str, timeout_seconds: int, cancel: CancelToken):
    """Returns (exit_code, output, timed_out). Raises Cancelled if the user interrupts."""
    wd = cwd if os.path.isdir(cwd) else os.getcwd()
    kwargs: dict = dict(cwd=wd, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
    if os.name == "nt":
        # chcp 65001 makes cmd.exe (and most tools) emit UTF-8, so accents are not garbled in the output.
        args = 'cmd.exe /d /s /c "chcp 65001>nul & ' + command + '"'
        kwargs["creationflags"] = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    else:
        args = [unix_shell(), "-c", command]
        kwargs["start_new_session"] = True

    logger.trace("TOOL", f"Shell: {command}\n  cwd={wd}, timeout={timeout_seconds}s")
    t0 = time.monotonic()
    try:
        p = subprocess.Popen(args, **kwargs)
    except Exception as ex:
        logger.exception("Shell start failed", ex)
        return -1, str(ex), False

    chunks: list = []

    def reader() -> None:
        try:
            for raw in iter(p.stdout.readline, b""):
                chunks.append(raw.decode("utf-8", errors="replace"))
        except Exception:
            pass

    t = threading.Thread(target=reader, daemon=True)
    t.start()

    timed_out = False
    deadline = t0 + timeout_seconds
    while p.poll() is None:
        if cancel.cancelled:
            _kill_tree(p)
            t.join(1)
            raise Cancelled()
        if time.monotonic() > deadline:
            _kill_tree(p)
            timed_out = True
            break
        time.sleep(0.05)
    t.join(2)  # flush the reader

    output = "".join(chunks).rstrip()
    code = -1 if timed_out else (p.returncode if p.returncode is not None else -1)
    logger.trace("TOOL", f"Shell finished in {int((time.monotonic() - t0) * 1000)} ms: "
                         f"{'TIMED OUT' if timed_out else 'exit ' + str(code)}, {len(output)} chars of output")
    return code, output, timed_out


def clip(s: str, max_len: int = 12000) -> str:
    if len(s) <= max_len:
        return s
    half = max_len // 2
    return s[:half] + f"\n… [{len(s) - max_len} characters omitted] …\n" + s[-half:]
