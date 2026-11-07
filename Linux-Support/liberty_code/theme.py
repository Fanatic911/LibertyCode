"""Colour palette (hex, used inside rich markup) and terminal symbols."""
from __future__ import annotations

import os


class Theme:
    ORANGE = "#FF9F1C"
    VIOLET = "#B388FF"
    GREEN = "#4ADE80"
    RED = "#F87171"
    YELLOW = "#FBBF24"
    CYAN = "#67E8F9"
    DIM = "#E6EDF3"
    FAINT = "#4B5563"
    CODE = "#C4B5FD"

    ADD_FG = "#D1FAE5"
    ADD_BG = "#12361F"
    DEL_FG = "#FEE2E2"
    DEL_BG = "#3F1518"


def _detect_unicode() -> bool:
    """Legacy Windows consoles (conhost + Consolas) lack glyphs for some symbols: fall back to ASCII.
    Force a mode with LIBERTY_ASCII=1 or LIBERTY_UNICODE=1."""
    if os.environ.get("LIBERTY_ASCII") == "1":
        return False
    if os.environ.get("LIBERTY_UNICODE") == "1":
        return True
    if os.name != "nt":
        return os.environ.get("TERM", "") not in ("linux", "dumb")
    return any(os.environ.get(k) for k in ("WT_SESSION", "TERM_PROGRAM", "ConEmuPID"))


class Sym:
    UNICODE = _detect_unicode()
    POINTER = "›" if UNICODE else ">"
    BRANCH = "⎿" if UNICODE else "└"
    DIAMOND = "◆" if UNICODE else "♦"
    CHECK = "✓" if UNICODE else "√"
    CROSS = "✗" if UNICODE else "x"
    UNDO = "↶" if UNICODE else "<"
    GAP = "⋯" if UNICODE else ".."
    QUOTE = "▎" if UNICODE else "|"
    BOX_TOP = "╭─" if UNICODE else "┌─"
    BOX_BOT = "╰─" if UNICODE else "└─"
    FRAMES = (["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"] if UNICODE else ["|", "/", "-", "\\"])
