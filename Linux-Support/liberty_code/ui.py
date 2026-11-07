"""All terminal rendering. The palette lives in theme.py."""
from __future__ import annotations

import os
import re
import sys
import threading
import time
from typing import Iterable, Optional

from rich import box
from rich.console import Console
from rich.markup import escape
from rich.panel import Panel
from rich.table import Table

from . import loc, logger
from .models import CancelToken, PermissionMode
from .theme import Sym, Theme

def enable_vt() -> None:
    """Turns on ANSI escape processing on Windows 10+ consoles (must happen before rich creates its Console)."""
    if os.name != "nt":
        return
    try:
        import ctypes
        k32 = ctypes.windll.kernel32
        for std in (-11, -12):
            h = k32.GetStdHandle(std)
            mode = ctypes.c_uint32()
            if k32.GetConsoleMode(h, ctypes.byref(mode)):
                k32.SetConsoleMode(h, mode.value | 0x0004)
    except Exception:
        pass


enable_vt()
# legacy_windows=False: rich would otherwise use the old Win32 colour API on conhost, which mixes badly with our
# ANSI cursor handling (menus, spinner) and cannot clear the screen properly.
console = Console(highlight=False, emoji=False, legacy_windows=False if os.name == "nt" else None)
GATE = threading.RLock()
HUB_LABEL = "/project"


# ── Console init ──────────────────────────────────────────────────────────────
def init() -> None:
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
        except Exception:
            pass
    enable_vt()
    set_title()


def clear_screen() -> None:
    """Really erases the screen (and scrollback where possible), cursor to the top-left corner."""
    if not sys.stdout.isatty():
        return
    try:
        sys.stdout.flush()
        if os.name == "nt":
            os.system("cls")
        sys.stdout.write("\x1b[0m\x1b[2J\x1b[3J\x1b[H")
        sys.stdout.flush()
    except Exception:
        pass


def set_title(title: str = "LibertyCode") -> None:
    try:
        if os.name == "nt":
            import ctypes
            ctypes.windll.kernel32.SetConsoleTitleW(title)
        elif sys.stdout.isatty():
            sys.stdout.write(f"\x1b]0;{title}\x07")
            sys.stdout.flush()
    except Exception:
        pass


def width() -> int:
    try:
        return max(40, min(140, console.width - 1))
    except Exception:
        return 80


def interactive() -> bool:
    try:
        return sys.stdin.isatty() and sys.stdout.isatty()
    except Exception:
        return False


# ── Markup helpers ────────────────────────────────────────────────────────────
def M(color: str, text: str) -> str:
    return f"[{color}]{escape(text)}[/]"


def dim(text: str) -> str: return M(Theme.DIM, text)
def bold(text: str) -> str: return f"[bold]{escape(text)}[/]"
def violet(text: str) -> str: return M(Theme.VIOLET, text)


def line(markup: str = "") -> None:
    console.print(markup)


def blank() -> None:
    console.print()


def ok(msg: str) -> None:
    line(f"  {M(Theme.GREEN, Sym.CHECK)} {escape(loc.T(msg))}")


def warn(msg: str) -> None:
    line(f"  {M(Theme.YELLOW, '!')} {M(Theme.YELLOW, loc.T(msg))}")
    logger.warn(msg)


def error(msg: str) -> None:
    line(f"  {M(Theme.RED, Sym.CROSS)} {M(Theme.RED, loc.T(msg))}")
    logger.error(msg)


def hint(msg: str) -> None:
    line(f"  {dim(loc.T(msg))}")


def short(path: str) -> str:
    home = os.path.expanduser("~").rstrip("\\/")
    if home and home != "~":
        a, b = os.path.normcase(path), os.path.normcase(home)
        if a == b or a.startswith(b + os.sep) or (os.altsep and a.startswith(b + os.altsep)):
            return "~" + path[len(home):]
    return path


def security_name(m: PermissionMode) -> str:
    return {PermissionMode.ASK: "Strict", PermissionMode.EDITS: "Mid"}.get(m, "Off")


def security_hint(m: PermissionMode) -> str:
    return loc.T({PermissionMode.ASK: "asks before edits and commands",
                  PermissionMode.EDITS: "applies edits, asks before commands"}
                 .get(m, "never asks (dangerous commands still ask)"))


def mode_color(m: PermissionMode) -> str:
    return {PermissionMode.ASK: Theme.GREEN, PermissionMode.EDITS: Theme.YELLOW}.get(m, Theme.RED)


# ── Banner ────────────────────────────────────────────────────────────────────
def banner(s, mode: PermissionMode, workspace: str, hub: bool = False) -> None:
    from . import providers
    clear_screen()
    set_title()
    grid = Table.grid(padding=(0, 2, 0, 0))
    grid.add_column(no_wrap=True)
    grid.add_column()
    grid.add_row(dim(loc.T("Model:")), f"{violet(s.Model)} {dim('· ' + providers.short_label(s.Provider))}")
    grid.add_row(dim(loc.T("Current directory:")), M(Theme.ORANGE, HUB_LABEL) if hub else escape(short(workspace)))
    grid.add_row(dim(loc.T("Security:")), M(mode_color(mode), security_name(mode)))
    panel = Panel(grid, box=box.ROUNDED if Sym.UNICODE else box.SQUARE, border_style=Theme.ORANGE,
                  title=f" [bold {Theme.ORANGE}]{Sym.DIAMOND} Liberty[/][bold {Theme.VIOLET}]Code[/] ",
                  title_align="left", padding=(0, 2, 0, 1), expand=False)
    blank()
    console.print(panel)
    line(f" {dim(loc.T('/help for commands  ·  esc to interrupt  ·  ctrl+c twice to exit'))}")
    blank()


_LOGO = [
    r"$$\       $$\ $$\                            $$\                      $$$$$$\                  $$\           ",
    r"$$ |      \__|$$ |                           $$ |                    $$  __$$\                 $$ |          ",
    r"$$ |      $$\ $$$$$$$\   $$$$$$\   $$$$$$\ $$$$$$\   $$\   $$\       $$ /  \__| $$$$$$\   $$$$$$$ | $$$$$$\  ",
    r"$$ |      $$ |$$  __$$\ $$  __$$\ $$  __$$\\_$$  _|  $$ |  $$ |      $$ |      $$  __$$\ $$  __$$ |$$  __$$\ ",
    r"$$ |      $$ |$$ |  $$ |$$$$$$$$ |$$ |  \__| $$ |    $$ |  $$ |      $$ |      $$ /  $$ |$$ /  $$ |$$$$$$$$ |",
    r"$$ |      $$ |$$ |  $$ |$$   ____|$$ |       $$ |$$\ $$ |  $$ |      $$ |  $$\ $$ |  $$ |$$ |  $$ |$$   ____|",
    r"$$$$$$$$\ $$ |$$$$$$$  |\$$$$$$$\ $$ |       \$$$$  |\$$$$$$$ |      \$$$$$$  |\$$$$$$  |\$$$$$$$ |\$$$$$$$\ ",
    r"\________|\__|\_______/  \_______|\__|        \____/  \____$$ |       \______/  \______/  \_______| \_______|",
    r"                                                     $$\   $$ |                                              ",
    r"                                                     \$$$$$$  |                                              ",
    r"                                                      \______/                                               ",
]


def big_logo() -> None:
    """The original ASCII logo, shown on first run when the terminal is wide enough."""
    if width() < 112:
        return
    blank()
    split = 63
    for l in _LOGO:
        left, right = (l[:split], l[split:]) if len(l) > split else (l, "")
        line(f"  [{Theme.ORANGE}]{escape(left)}[/][{Theme.VIOLET}]{escape(right)}[/]")
    blank()


# ── Prompt + status ───────────────────────────────────────────────────────────
def format_tokens(n: int) -> str:
    return f"{n / 1000.0:.1f}".rstrip("0").rstrip(".") + "k" if n >= 1000 else str(n)


def status_line(s, mode: PermissionMode, workspace: str, ctx_tokens: int, hub: bool = False) -> None:
    folder = HUB_LABEL if hub else (os.path.basename(workspace.rstrip("\\/")) or workspace)
    info = f"{folder} · {s.Model} · {security_name(mode)}"
    if ctx_tokens > 0:
        info += f" · {format_tokens(ctx_tokens)} ctx"
    bar = max(4, width() - len(info) - 3)
    line(f"[{Theme.FAINT}]{'─' * bar}[/] {dim(info)}")


def prompt_fragments() -> list:
    return [("bold fg:" + Theme.ORANGE, Sym.POINTER), ("", " ")]


PROMPT_LENGTH = 2


# ── Tool rendering ────────────────────────────────────────────────────────────
def tool_header(name: str, arg: str) -> None:
    room = max(10, width() - len(name) - 8)
    if len(arg) > room:
        arg = arg[:room - 1] + "…"
    line(f"{M(Theme.ORANGE, Sym.DIAMOND)} [bold]{escape(name)}[/]{dim('(')}{escape(arg)}{dim(')')}")


def tool_line(markup: str) -> None:
    """A result line under a tool header, markup already prepared."""
    line(f"  {dim(Sym.BRANCH)}  {markup}")


def tool_error(plain: str) -> None:
    tool_line(f"{M(Theme.RED, Sym.CROSS)} {M(Theme.RED, plain)}")


def lines(markup_lines: Iterable[str]) -> None:
    for l in markup_lines:
        line(l)


# ── Spinner ───────────────────────────────────────────────────────────────────
class Spinner:
    def __init__(self, label: str):
        self._label = loc.T(label)
        self._t0 = time.monotonic()
        self._stop = threading.Event()
        self._stopped = False
        self._live = sys.stdout.isatty()
        self._thread: Optional[threading.Thread] = None
        if self._live:
            sys.stdout.write("\x1b[?25l")
            sys.stdout.flush()
            self._thread = threading.Thread(target=self._run, daemon=True)
            self._thread.start()

    def set(self, label: str) -> None:
        self._label = label

    def _run(self) -> None:
        frames = Sym.FRAMES
        i = 0
        while not self._stop.is_set():
            with GATE:
                if self._stopped:
                    return
                secs = time.monotonic() - self._t0
                sys.stdout.write(f"\r\x1b[2K\x1b[38;2;255;159;28m{frames[i % len(frames)]}\x1b[0m "
                                 f"\x1b[38;2;139;148;158m{self._label}… {secs:.0f}s · {loc.T('esc to interrupt')}\x1b[0m")
                sys.stdout.flush()
            i += 1
            self._stop.wait(0.09)

    def stop(self) -> None:
        with GATE:
            if self._stopped:
                return
            self._stopped = True
        self._stop.set()
        if self._thread:
            self._thread.join(0.3)
        if self._live:
            sys.stdout.write("\r\x1b[2K\x1b[?25h")
            sys.stdout.flush()

    def __enter__(self) -> "Spinner":
        return self

    def __exit__(self, *a) -> bool:
        self.stop()
        return False


# ── Esc / interrupt watcher ───────────────────────────────────────────────────
class EscWatcher:
    """While active, pressing Esc (or Ctrl+C) cancels the token. Pause()/Resume() around prompts."""
    _current: "Optional[EscWatcher]" = None

    def __init__(self, target: CancelToken):
        from . import keys
        self._keys = keys
        self._target = target
        self._gate = threading.Lock()
        self._stop = threading.Event()
        self._paused = False
        self._raw = None
        self._thread: Optional[threading.Thread] = None
        EscWatcher._current = self
        if not sys.stdin.isatty():
            return
        self._raw = keys.RawTerminal()
        self._raw.__enter__()
        self._thread = threading.Thread(target=self._run, daemon=True)
        self._thread.start()

    def _run(self) -> None:
        while not self._stop.is_set():
            with self._gate:
                try:
                    if not self._paused and self._keys.key_available():
                        k = self._keys.read_key()
                        if k.name in ("esc", "ctrl_c"):
                            self._target.cancel()
                except Exception:
                    pass
            self._stop.wait(0.04)

    @staticmethod
    def pause() -> None:
        c = EscWatcher._current
        if c:
            with c._gate:
                c._paused = True

    @staticmethod
    def resume() -> None:
        c = EscWatcher._current
        if c:
            with c._gate:
                c._paused = False

    def close(self) -> None:
        self._stop.set()
        if self._thread:
            self._thread.join(0.3)
        if self._raw is not None:
            self._raw.__exit__(None, None, None)
        if EscWatcher._current is self:
            EscWatcher._current = None

    def __enter__(self) -> "EscWatcher":
        return self

    def __exit__(self, *a) -> bool:
        self.close()
        return False


# ── Streaming markdown renderer ───────────────────────────────────────────────
_INLINE_RX = re.compile(r"`([^`]+)`|\*\*([^*]+)\*\*")


class MarkdownStream:
    def __init__(self) -> None:
        self._buf = ""
        self._in_code = False
        self._started = False
        self._last_blank = False

    @property
    def has_output(self) -> bool:
        return self._started

    def feed(self, delta: str) -> None:
        self._buf += delta
        while True:
            i = self._buf.find("\n")
            if i < 0:
                break
            self._render_line(self._buf[:i].rstrip("\r"))
            self._buf = self._buf[i + 1:]

    def flush(self) -> None:
        if self._buf:
            self._render_line(self._buf.rstrip("\r"))
        self._buf = ""
        if self._in_code:
            line(f"  {M(Theme.FAINT, Sym.BOX_BOT)}")
            self._in_code = False

    def _prefix(self) -> str:
        if self._started:
            return "  "
        self._started = True
        return f"{M(Theme.ORANGE, Sym.DIAMOND)} "

    @staticmethod
    def _inline(t: str) -> str:
        out = []
        pos = 0
        for m in _INLINE_RX.finditer(t):
            out.append(escape(t[pos:m.start()]))
            if m.group(1) is not None:
                out.append(f"[{Theme.CODE}]{escape(m.group(1))}[/]")
            else:
                out.append(f"[bold]{escape(m.group(2))}[/]")
            pos = m.end()
        out.append(escape(t[pos:]))
        return "".join(out)

    def _render_line(self, text: str) -> None:
        trimmed = text.lstrip()

        if trimmed.startswith("```"):
            if not self._in_code:
                self._in_code = True
                lang = trimmed.strip("`").strip()
                line(f"{self._prefix()}{M(Theme.FAINT, Sym.BOX_TOP)}{' ' + dim(lang) if lang else ''}")
            else:
                self._in_code = False
                line(f"  {M(Theme.FAINT, Sym.BOX_BOT)}")
            self._last_blank = False
            return

        if self._in_code:
            line(f"  {M(Theme.FAINT, '│')} [{Theme.CODE}]{escape(text.replace(chr(9), '    '))}[/]")
            return

        if not trimmed:
            if self._started and not self._last_blank:
                blank()
            self._last_blank = True
            return
        self._last_blank = False

        hm = re.match(r"^#{1,6}\s+(.*)$", trimmed)
        if hm:
            line(f"{self._prefix()}[bold {Theme.ORANGE}]{escape(hm.group(1))}[/]")
            return

        lm = re.match(r"^(\s*)[-*]\s+(.*)$", text)
        if lm:
            line(f"{self._prefix()}{lm.group(1)}{M(Theme.DIM, '•')} {self._inline(lm.group(2))}")
            return

        if trimmed.startswith("> "):
            line(f"{self._prefix()}{M(Theme.FAINT, Sym.QUOTE)} {dim(trimmed[2:])}")
            return

        line(f"{self._prefix()}{text[:len(text) - len(trimmed)]}{self._inline(trimmed)}")
