"""Line diff (LCS) + rendering of the result as coloured markup lines."""
from __future__ import annotations

import re
from dataclasses import dataclass
from enum import Enum

from rich.markup import escape

from .theme import Sym, Theme

MAX_CELLS = 6_000_000
_INDENT = "     "


class DiffKind(Enum):
    SAME = 0
    ADD = 1
    DEL = 2


@dataclass
class DiffLine:
    kind: DiffKind
    old_no: int
    new_no: int
    text: str


def split_lines(text: str) -> list:
    text = text.replace("\r\n", "\n")
    if not text:
        return []
    lines = text.split("\n")
    return lines[:-1] if text.endswith("\n") else lines


def compute(a: list, b: list) -> list:
    pre = 0
    while pre < len(a) and pre < len(b) and a[pre] == b[pre]:
        pre += 1
    suf = 0
    while suf < len(a) - pre and suf < len(b) - pre and a[len(a) - 1 - suf] == b[len(b) - 1 - suf]:
        suf += 1

    n, m = len(a) - pre - suf, len(b) - pre - suf
    result: list = []
    for i in range(pre):
        result.append(DiffLine(DiffKind.SAME, i + 1, i + 1, a[i]))

    oi = ni = pre
    if n > 0 or m > 0:
        if (n + 1) * (m + 1) > MAX_CELLS:
            for i in range(n):
                oi += 1
                result.append(DiffLine(DiffKind.DEL, oi, 0, a[pre + i]))
            for j in range(m):
                ni += 1
                result.append(DiffLine(DiffKind.ADD, 0, ni, b[pre + j]))
        else:
            w = m + 1
            l = [0] * ((n + 1) * w)
            for i in range(n - 1, -1, -1):
                ai = a[pre + i]
                for j in range(m - 1, -1, -1):
                    if ai == b[pre + j]:
                        l[i * w + j] = l[(i + 1) * w + j + 1] + 1
                    else:
                        l[i * w + j] = max(l[(i + 1) * w + j], l[i * w + j + 1])
            x = y = 0
            while x < n and y < m:
                if a[pre + x] == b[pre + y]:
                    oi += 1; ni += 1
                    result.append(DiffLine(DiffKind.SAME, oi, ni, a[pre + x]))
                    x += 1; y += 1
                elif l[(x + 1) * w + y] >= l[x * w + y + 1]:
                    oi += 1
                    result.append(DiffLine(DiffKind.DEL, oi, 0, a[pre + x]))
                    x += 1
                else:
                    ni += 1
                    result.append(DiffLine(DiffKind.ADD, 0, ni, b[pre + y]))
                    y += 1
            while x < n:
                oi += 1
                result.append(DiffLine(DiffKind.DEL, oi, 0, a[pre + x])); x += 1
            while y < m:
                ni += 1
                result.append(DiffLine(DiffKind.ADD, 0, ni, b[pre + y])); y += 1

    for i in range(suf):
        oi += 1; ni += 1
        result.append(DiffLine(DiffKind.SAME, oi, ni, a[len(a) - suf + i]))
    return result


def count(d: list) -> tuple:
    return (sum(1 for x in d if x.kind == DiffKind.ADD), sum(1 for x in d if x.kind == DiffKind.DEL))


# ── Rendering ─────────────────────────────────────────────────────────────────
def _fit(s: str, max_len: int) -> str:
    s = s.replace("\t", "    ").replace("\r", "")
    s = "".join(c for c in s if c.isprintable())
    return s if len(s) <= max_len else s[:max(0, max_len - 1)] + "…"


def _row(kind: DiffKind, no: int, text: str, num_w: int, width: int) -> str:
    room = max(10, width - len(_INDENT) - num_w - 4)
    body = _fit(text, room)
    num = str(no).rjust(num_w) if no > 0 else " " * num_w
    if kind == DiffKind.ADD:
        raw = f" {num} + {body}".ljust(width - len(_INDENT))
        return f"{_INDENT}[{Theme.ADD_FG} on {Theme.ADD_BG}]{escape(raw)}[/]"
    if kind == DiffKind.DEL:
        raw = f" {num} - {body}".ljust(width - len(_INDENT))
        return f"{_INDENT}[{Theme.DEL_FG} on {Theme.DEL_BG}]{escape(raw)}[/]"
    return f"{_INDENT}[{Theme.DIM}]{escape(f' {num}   {body}')}[/]"


def render(diff: list, max_lines: int = 48, context: int = 3) -> list:
    """Renders changed hunks with 3 lines of context."""
    from . import ui
    width = ui.width()
    keep = [False] * len(diff)
    for i, d in enumerate(diff):
        if d.kind == DiffKind.SAME:
            continue
        for k in range(max(0, i - context), min(len(diff) - 1, i + context) + 1):
            keep[k] = True

    max_no = 1 if not diff else max(max(d.old_no, d.new_no) for d in diff)
    num_w = max(3, len(str(max_no)))

    lines: list = []
    shown = hidden = 0
    gap = False
    for i, d in enumerate(diff):
        if not keep[i]:
            gap = len(lines) > 0
            continue
        if shown >= max_lines:
            hidden += 1
            continue
        if gap:
            lines.append(f"{_INDENT}[{Theme.FAINT}] {' ' * (num_w - 1)}{Sym.GAP}[/]")
            gap = False
        lines.append(_row(d.kind, d.old_no if d.kind == DiffKind.DEL else d.new_no, d.text, num_w, width))
        shown += 1
    if hidden > 0:
        lines.append(f"{_INDENT}[{Theme.DIM}] … +{hidden} more lines[/]")
    return lines


def render_whole(content: list, added: bool, max_lines: int = 10) -> list:
    """Preview of a whole file (new file in green, deleted file in red)."""
    from . import ui
    width = ui.width()
    num_w = max(3, len(str(len(content))))
    lines = [_row(DiffKind.ADD if added else DiffKind.DEL, i + 1, content[i], num_w, width)
             for i in range(min(max_lines, len(content)))]
    if len(content) > max_lines:
        lines.append(f"{_INDENT}[{Theme.DIM}] … +{len(content) - max_lines} more lines[/]")
    return lines
