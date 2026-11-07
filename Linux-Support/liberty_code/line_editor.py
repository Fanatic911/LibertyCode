"""Single-line input with history, cursor editing and Tab completion for slash commands (prompt_toolkit)."""
from __future__ import annotations

import sys
import time
from typing import Optional, Sequence

from prompt_toolkit import PromptSession
from prompt_toolkit.completion import Completer, Completion
from prompt_toolkit.history import InMemoryHistory
from prompt_toolkit.key_binding import KeyBindings

from . import paths, ui
from .theme import Theme

MAX_SECRET = 300
model_names: list = []              # model names of the current provider (Tab completion of '/models xxx')
secret_issue: Optional[str] = None     # set by read(secret=True): "invalid" | "long" | None
_last_ctrl_c = 0.0


class _ManualHistory(InMemoryHistory):
    """Entries are added by remember() only (a multi-line input is stored as one entry)."""

    def append_string(self, string: str) -> None:   # called by prompt_toolkit on accept: ignored
        pass

    def add(self, string: str) -> None:
        super().append_string(string)


_history = _ManualHistory()
_entries: list = []


def load_history() -> None:
    try:
        with open(paths.HISTORY_FILE, "r", encoding="utf-8") as f:
            for l in [x.rstrip("\n") for x in f.readlines()][-200:]:
                _entries.append(l)
                _history.add(l.replace(" ⏎ ", " "))
    except Exception:
        pass


def _remember(text: str) -> None:
    if not text.strip() or (_entries and _entries[-1] == text):
        return
    _entries.append(text)
    _history.add(text.replace(" ⏎ ", " "))
    try:
        with open(paths.HISTORY_FILE, "w", encoding="utf-8") as f:
            f.write("\n".join(_entries[-200:]) + "\n")
    except Exception:
        pass


class _SlashCompleter(Completer):
    def __init__(self, commands: Sequence[str]):
        self._commands = list(commands)

    def get_completions(self, document, complete_event):
        t = document.text_before_cursor
        if t.lower().startswith("/models "):
            part = t[8:].strip()
            names = [n for n in model_names if n.lower().startswith(part.lower())] \
                or [n for n in model_names if part.lower() in n.lower()]
            for n in names:
                yield Completion(n, start_position=-len(t[8:]))
            return
        if t.startswith("/") and " " not in t:
            for c in self._commands:
                if c.lower().startswith(t.lower()):
                    yield Completion(c, start_position=-len(t))


def _bindings() -> KeyBindings:
    kb = KeyBindings()

    @kb.add("c-c")
    def _(event):
        if event.current_buffer.text:
            event.current_buffer.reset()
        else:
            event.app.exit(exception=KeyboardInterrupt, style="class:aborting")

    @kb.add("escape", eager=True)
    def _(event):
        event.current_buffer.reset()

    return kb


def read(prompt: list, completions: Optional[Sequence[str]] = None, secret: bool = False,
         remember: bool = False) -> Optional[str]:
    """prompt: list of (style, text) fragments. Returns None on exit (Ctrl+C twice / Ctrl+D / end of input)."""
    global secret_issue, _last_ctrl_c
    if secret:
        secret_issue = None

    if not (sys.stdin.isatty() and sys.stdout.isatty()):
        sys.stdout.write("".join(t for _, t in prompt))
        sys.stdout.flush()
        l = sys.stdin.readline()
        if l == "":
            return None
        return l.rstrip("\r\n")

    session = PromptSession(
        history=None if secret else _history,
        key_bindings=_bindings(),
        completer=None if secret or not completions else _SlashCompleter(completions),
        complete_while_typing=False,
        is_password=secret,
        enable_history_search=False,
    )
    while True:
        try:
            text = session.prompt(prompt)
            break
        except KeyboardInterrupt:
            if time.monotonic() - _last_ctrl_c < 2:
                sys.stdout.write("\n")
                return None
            _last_ctrl_c = time.monotonic()
            ui.hint("press ctrl+c again to exit")
        except EOFError:
            sys.stdout.write("\n")
            return None

    if secret:
        t = text.strip()
        if len(t) > MAX_SECRET:
            secret_issue = "long"
        elif any(not ("!" <= c <= "~") for c in t):
            secret_issue = "invalid"
        return t
    if remember:
        _remember(text)
    return text


def read_input(prompt: list, completions: Sequence[str]) -> Optional[str]:
    """Reads a possibly multi-line input (a trailing backslash continues on the next line). None = exit."""
    parts: list = []
    cur = prompt
    while True:
        l = read(cur, completions)
        if l is None:
            return None
        if l.endswith("\\"):
            parts.append(l[:-1])
            cur = [("fg:" + Theme.FAINT, "·"), ("", " ")]
            continue
        parts.append(l)
        break
    result = "\n".join(parts)
    _remember(result.replace("\r\n", " ⏎ ").replace("\n", " ⏎ "))
    return result
