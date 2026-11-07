"""Cross-platform raw key reading (used by the menus and the Esc watcher)."""
from __future__ import annotations

import os
import sys
from dataclasses import dataclass


@dataclass
class Key:
    name: str      # enter, esc, tab, backspace, up, down, left, right, home, end, pageup, pagedown, delete, ctrl_c, ctrl_d, char, other
    char: str = ""


if os.name == "nt":
    import msvcrt

    _SPECIAL = {"H": "up", "P": "down", "K": "left", "M": "right", "G": "home", "O": "end",
                "I": "pageup", "Q": "pagedown", "S": "delete"}

    def key_available() -> bool:
        return msvcrt.kbhit()

    def read_key() -> Key:
        ch = msvcrt.getwch()
        if ch in ("\x00", "\xe0"):
            return Key(_SPECIAL.get(msvcrt.getwch(), "other"))
        if ch in ("\r", "\n"): return Key("enter")
        if ch == "\x1b": return Key("esc")
        if ch == "\t": return Key("tab")
        if ch in ("\x08", "\x7f"): return Key("backspace")
        if ch == "\x03": return Key("ctrl_c")
        if ch == "\x04": return Key("ctrl_d")
        if ch < " ": return Key("other")
        return Key("char", ch)

    class RawTerminal:
        """No-op on Windows (msvcrt reads keys without echo)."""
        def __enter__(self): return self
        def __exit__(self, *a): return False

else:
    import select
    import termios
    import tty

    def _fd() -> int:
        return sys.stdin.fileno()

    def key_available() -> bool:
        try:
            return bool(select.select([_fd()], [], [], 0)[0])
        except Exception:
            return False

    def _read_byte(timeout=None):
        if timeout is not None and not select.select([_fd()], [], [], timeout)[0]:
            return b""
        return os.read(_fd(), 1)

    class RawTerminal:
        """Puts the terminal in cbreak mode (no echo, no line buffering) for its lifetime."""
        def __init__(self) -> None:
            self._old = None

        def __enter__(self):
            try:
                self._old = termios.tcgetattr(_fd())
                tty.setcbreak(_fd(), termios.TCSANOW)   # TCSAFLUSH (default) would drop pending keys
            except Exception:
                self._old = None
            return self

        def __exit__(self, *a):
            if self._old is not None:
                try:
                    termios.tcsetattr(_fd(), termios.TCSADRAIN, self._old)
                except Exception:
                    pass
            return False

    def read_key() -> Key:
        fd = _fd()
        try:
            old = termios.tcgetattr(fd)
        except Exception:
            old = None
        try:
            if old is not None:
                tty.setraw(fd, termios.TCSANOW)   # never flush: the key we were told about must survive
            b = os.read(fd, 1)
            if not b:
                return Key("ctrl_d")
            c = b[0]
            if c == 0x1b:
                nxt = _read_byte(0.03)
                if not nxt:
                    return Key("esc")
                if nxt in (b"[", b"O"):
                    seq = b""
                    while True:
                        x = _read_byte(0.03)
                        if not x:
                            break
                        seq += x
                        if x.isalpha() or x == b"~":
                            break
                    s = seq.decode("ascii", "ignore")
                    table = {"A": "up", "B": "down", "C": "right", "D": "left", "H": "home", "F": "end",
                             "3~": "delete", "5~": "pageup", "6~": "pagedown", "1~": "home", "4~": "end"}
                    # ctrl/shift/alt + arrow arrive as "1;5C" etc.: treat them as the plain arrow
                    name = table.get(s) or (table.get(s[-1:]) if len(s) > 1 and s[-1:] in "ABCDHF" else None)
                    return Key(name or "other")
                return Key("other")
            if c in (13, 10): return Key("enter")
            if c == 9: return Key("tab")
            if c in (8, 127): return Key("backspace")
            if c == 3: return Key("ctrl_c")
            if c == 4: return Key("ctrl_d")
            if c < 32: return Key("other")
            if c >= 0xC0:
                n = 1 if c < 0xE0 else 2 if c < 0xF0 else 3
                rest = b"".join(_read_byte(0.05) for _ in range(n))
                try:
                    return Key("char", (b + rest).decode("utf-8"))
                except UnicodeDecodeError:
                    return Key("other")
            return Key("char", chr(c))
        finally:
            if old is not None:
                try:
                    termios.tcsetattr(fd, termios.TCSADRAIN, old)
                except Exception:
                    pass
