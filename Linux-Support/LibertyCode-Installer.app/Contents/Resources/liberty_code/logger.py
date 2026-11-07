"""Two outputs:
 - libertycode.log : short, always on (INFO / WARN / ERROR), one line per entry.
 - debug.log       : only in Debug mode, everything (requests, responses, AI stream, tool steps, stack traces).
                     A separate console window (debug_viewer) follows this file in real time.
"""
from __future__ import annotations

import datetime as _dt
import threading
import traceback
from typing import Optional

from . import paths

CLOSE_MARKER = "@@CLOSE@@"   # written on its own line to tell the debug window to close itself

_gate = threading.RLock()
_dbg = None                  # open file object while debug mode is on
_stream_open = False         # an AI stream line is open (no trailing newline yet)


def debug_enabled() -> bool:
    return _dbg is not None


def _now(fmt: str) -> str:
    return _dt.datetime.now().strftime(fmt)


# ── Normal log ────────────────────────────────────────────────────────────────
def log(level: str, message: str) -> None:
    try:
        with _gate:
            line = f"[{_now('%Y-%m-%d %H:%M:%S')}] [{level}] {' '.join(message.splitlines())}"
            with open(paths.LOG_FILE, "a", encoding="utf-8") as f:
                f.write(line + "\n")
    except Exception:
        pass  # logging must never crash the app
    trace(level, message)


def info(msg: str) -> None: log("INFO", msg)
def warn(msg: str) -> None: log("WARN", msg)
def error(msg: str) -> None: log("ERROR", msg)


def exception(context: str, ex: BaseException) -> None:
    """Error with the full exception (type, message, stack) in the debug window."""
    error(f"{context}: {type(ex).__name__}: {ex}")
    trace("STACK", "".join(traceback.format_exception(type(ex), ex, ex.__traceback__)).rstrip())


def tail(lines: int = 20) -> list:
    try:
        with open(paths.LOG_FILE, "r", encoding="utf-8", errors="replace") as f:
            return [l.rstrip("\n") for l in f.readlines()][-lines:]
    except Exception:
        return []


# ── Debug mode ────────────────────────────────────────────────────────────────
def start_debug() -> bool:
    """Turns debug on (if needed) and makes sure the live window is open. False if no window could be opened."""
    global _dbg, _stream_open
    from . import debug_viewer
    with _gate:
        if _dbg is None:
            try:
                import os
                os.makedirs(paths.DATA_DIR, exist_ok=True)
                _dbg = open(paths.DEBUG_FILE, "w", encoding="utf-8", newline="\n", buffering=1)
            except Exception:
                _dbg = None
                return False
            _stream_open = False
            import platform
            import sys
            from . import __version__
            _write(_line("DEBUG", f"Debug mode ON - LibertyCode {__version__} | {platform.platform()} | Python {sys.version.split()[0]}"))
            _write(_line("DEBUG", f"Data folder: {paths.DATA_DIR}"))
    return debug_viewer.launch(paths.DEBUG_FILE)


def stop_debug() -> None:
    global _dbg
    with _gate:
        if _dbg is None:
            return
        _close_stream_line()
        _write(_line("DEBUG", "Debug mode OFF"))
        _write(CLOSE_MARKER + "\n")
        try:
            _dbg.close()
        except Exception:
            pass
        _dbg = None


def shutdown() -> None:
    with _gate:
        if _dbg is None:
            return
        _close_stream_line()
        _write(_line("INFO", "LibertyCode is exiting"))


def trace(level: str, message: str) -> None:
    """Debug-only entry. Multi-line messages are indented under their first line."""
    if _dbg is None:
        return
    with _gate:
        if _dbg is None:
            return
        _close_stream_line()
        _write(_line(level, message))


def debug(message: str) -> None:
    trace("DEBUG", message)


def stream_chunk(text: str) -> None:
    """Starts an open line (no newline) that stream_chunk fills in real time."""
    global _stream_open
    if _dbg is None or not text:
        return
    with _gate:
        if _dbg is None:
            return
        if not _stream_open:
            _write(f"{_now('%H:%M:%S.%f')[:-3]} [AI   ] ")
            _stream_open = True
        _write(text.replace("\r\n", "\n").replace("\r", "\n"))


def stream_end() -> None:
    if _dbg is None:
        return
    with _gate:
        _close_stream_line()


def _close_stream_line() -> None:
    global _stream_open
    if not _stream_open:
        return
    _write("\n")
    _stream_open = False


def _line(level: str, message: str) -> str:
    parts = message.replace("\r\n", "\n").replace("\r", "\n").split("\n")
    out = [f"{_now('%H:%M:%S.%f')[:-3]} [{level:<5}] {parts[0]}\n"]
    for p in parts[1:]:
        out.append("             | " + p + "\n")
    return "".join(out)


def _write(s: str) -> None:
    try:
        if _dbg is not None:
            _dbg.write(s)
            _dbg.flush()
    except Exception:
        pass


# ── Helpers for callers ───────────────────────────────────────────────────────
def mask(key: Optional[str]) -> str:
    """'sk-abcd...wxyz': enough to recognise a key, never enough to use it."""
    if not key:
        return "(none)"
    return "****" if len(key) <= 10 else key[:4] + "..." + key[-4:]


def mask_header(name: str, value: str) -> str:
    if name.lower() == "authorization":
        i = value.find(" ")
        return value[:i + 1] + mask(value[i + 1:]) if i > 0 else mask(value)
    return mask(value) if name.lower() == "x-api-key" else value


def clip(s: Optional[str], max_len: int) -> str:
    if not s:
        return ""
    return s if len(s) <= max_len else s[:max_len] + f" ... (+{len(s) - max_len} chars)"
