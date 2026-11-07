"""Arrow-key selection (with optional type-to-filter). Falls back to a numbered list when not interactive."""
from __future__ import annotations

import sys
from typing import Callable, Sequence, TypeVar

from rich.markup import escape

from . import keys, loc, ui
from .models import Cancelled
from .theme import Sym, Theme

T = TypeVar("T")


def choose(title: str, items: Sequence[T], label: Callable[[T], str] = str, page_size: int = 12,
           search: bool = False) -> T:
    if not ui.interactive():
        ui.line(f"  {ui.bold(title)}")
        for i, it in enumerate(items):
            ui.line(f"    {ui.dim(str(i + 1) + '.')} {escape(label(it))}")
        while True:
            sys.stdout.write("  > ")
            sys.stdout.flush()
            l = sys.stdin.readline()
            if l == "":
                raise Cancelled()
            l = l.strip()
            if l.isdigit() and 1 <= int(l) <= len(items):
                return items[int(l) - 1]
    return _choose_box(title, items, label, page_size, search)


def _choose_box(title: str, items: Sequence[T], label: Callable[[T], str], page_size: int, search: bool) -> T:
    title = loc.T(title)
    labels = [loc.T(label(i)) for i in items]
    u = Sym.UNICODE
    tl, tr, bl, br = ("╭", "╮", "╰", "╯") if u else ("+", "+", "+", "+")
    h, v, ml, mr = ("─", "│", "├", "┤") if u else ("-", "|", "+", "+")

    max_w = max(30, ui.console.width - 4)
    inner = min(max_w, max(34, max(max(len(l) for l in labels) + 6, len(title) + 6)))
    rows = min(page_size, len(items))

    flt = ""
    idx = list(range(len(items)))
    sel = scroll = 0

    def fit(t: str, w: int) -> str:
        return t.ljust(w) if len(t) <= w else t[:max(0, w - 1)] + "…"

    def B(c: str) -> str:
        return f"[{Theme.ORANGE}]{c}[/]"

    def refilter() -> None:
        nonlocal idx, sel, scroll
        idx = [i for i in range(len(items)) if not flt or flt.lower() in labels[i].lower()]
        sel = scroll = 0

    def build() -> list:
        nonlocal scroll
        out = []
        t = " " + fit(title, inner - 4).rstrip() + " "
        out.append(B(tl + h) + f"[bold]{escape(t)}[/]" + B(h * max(0, inner - 1 - len(t)) + tr))
        if sel < scroll:
            scroll = sel
        if sel >= scroll + rows:
            scroll = sel - rows + 1
        for r in range(rows):
            k = scroll + r
            if k < len(idx):
                text = fit(labels[idx[k]], inner - 4)
                body = (f"[{Theme.ORANGE}]{escape(Sym.POINTER + ' ' + text)}[/] " if k == sel
                        else f"  {escape(text)} ")
            else:
                body = " " * (inner - 1)
            out.append(B(v) + " " + body + B(v))
        out.append(B(ml + h * inner + mr))
        if search:
            hint = loc.T("filter: {0}").replace("{0}", flt) if flt else loc.T("type to filter")
        else:
            hint = loc.T("↑↓ choose")
        hint += "  ·  " + loc.T("Enter") + "  ·  Esc"
        out.append(B(v) + " " + f"[{Theme.DIM}]{escape(fit(hint, inner - 2))}[/]" + " " + B(v))
        out.append(B(bl + h * inner + br))
        return out

    total = rows + 4   # top + rows + separator + hint + bottom
    first = True

    def draw() -> None:
        nonlocal first
        if not first:
            sys.stdout.write(f"\x1b[{total}A")
        for l in build():
            sys.stdout.write("\r\x1b[2K")
            sys.stdout.flush()
            ui.console.print(l, crop=True, overflow="crop", no_wrap=True)
        first = False

    def clear() -> None:
        sys.stdout.write(f"\x1b[{total}A\r\x1b[J")
        sys.stdout.flush()

    sys.stdout.write("\x1b[?25l")
    try:
        while True:
            draw()
            k = keys.read_key()
            if k.name == "ctrl_c":
                clear()
                raise Cancelled()
            if k.name == "up" and idx:
                sel = (sel - 1) % len(idx)
            elif k.name == "down" and idx:
                sel = (sel + 1) % len(idx)
            elif k.name == "pageup":
                sel = max(0, sel - rows)
            elif k.name == "pagedown":
                sel = min(max(0, len(idx) - 1), sel + rows)
            elif k.name == "home":
                sel = 0
            elif k.name == "end":
                sel = max(0, len(idx) - 1)
            elif k.name == "enter":
                if not idx:
                    continue
                clear()
                return items[idx[sel]]
            elif k.name == "esc":
                if flt:
                    flt = ""
                    refilter()
                    continue
                clear()
                raise Cancelled()
            elif k.name == "backspace":
                if search and flt:
                    flt = flt[:-1]
                    refilter()
            elif k.name == "char" and search:
                flt += k.char
                refilter()
    finally:
        sys.stdout.write("\x1b[?25h")
        sys.stdout.flush()
