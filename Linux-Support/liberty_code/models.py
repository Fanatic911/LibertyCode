"""Shared data types."""
from __future__ import annotations

import threading
from dataclasses import dataclass, field
from enum import Enum
from typing import Any, Callable, Optional


class PermissionMode(str, Enum):
    """How much the agent may do without asking."""
    ASK = "Ask"        # ask before every edit / delete / command
    EDITS = "Edits"    # file edits applied directly, deletes and commands still ask
    AUTO = "Auto"      # nothing asks (dangerous commands still do)


class Decision(Enum):
    YES = 1
    ALWAYS = 2
    NO = 3


@dataclass
class ToolCall:
    id: str
    name: str
    args: dict


@dataclass
class ToolSpec:
    name: str
    description: str
    schema: dict


@dataclass
class Msg:
    """Provider-neutral chat message. role: user | assistant | tool."""
    role: str = "user"
    text: str = ""
    calls: list = field(default_factory=list)
    call_id: Optional[str] = None
    is_error: bool = False


@dataclass
class LlmResult:
    text: str = ""
    calls: list = field(default_factory=list)
    in_tokens: int = 0
    out_tokens: int = 0


class LlmException(Exception):
    def __init__(self, status: int, message: str):
        super().__init__(message)
        self.status = status


class ToolError(Exception):
    """model_hint: extra context sent to the model only (never printed)."""

    def __init__(self, message: str, model_hint: Optional[str] = None):
        super().__init__(message)
        self.model_hint = model_hint


class Cancelled(Exception):
    """The user interrupted the operation (Esc / Ctrl+C)."""


class CancelToken:
    """Thread-safe cancellation flag with callbacks (used to close sockets / kill processes)."""

    def __init__(self) -> None:
        self._ev = threading.Event()
        self._cbs: list[Callable[[], Any]] = []
        self._lock = threading.Lock()

    @property
    def cancelled(self) -> bool:
        return self._ev.is_set()

    def cancel(self) -> None:
        with self._lock:
            if self._ev.is_set():
                return
            self._ev.set()
            cbs = list(self._cbs)
        for cb in cbs:
            try:
                cb()
            except Exception:
                pass

    def on_cancel(self, cb: Callable[[], Any]) -> Callable[[], None]:
        """Registers cb; returns a function that unregisters it."""
        call_now = False
        with self._lock:
            if self._ev.is_set():
                call_now = True
            else:
                self._cbs.append(cb)
        if call_now:
            try:
                cb()
            except Exception:
                pass

        def unregister() -> None:
            with self._lock:
                if cb in self._cbs:
                    self._cbs.remove(cb)

        return unregister

    def raise_if_cancelled(self) -> None:
        if self._ev.is_set():
            raise Cancelled()

    def wait(self, seconds: float) -> bool:
        """Sleeps up to `seconds`; returns True if cancelled meanwhile."""
        return self._ev.wait(seconds)


@dataclass
class ToolOutput:
    for_model: str = ""
    summary: str = ""                       # markup shown under the tool header
    details: Optional[list] = None          # extra markup lines
    is_error: bool = False


class ToolKind(Enum):
    READ = 1
    EDIT = 2
    DELETE = 3
    COMMAND = 4


@dataclass
class Plan:
    """What a tool call is about to do. Built first so it can be previewed and approved before running."""
    run: Callable[[CancelToken], ToolOutput]
    kind: ToolKind = ToolKind.READ
    question: str = ""
    key: str = ""
    dangerous: bool = False
    headline: Optional[str] = None          # markup, shown before the preview
    preview: Optional[list] = None          # markup lines (diff)
