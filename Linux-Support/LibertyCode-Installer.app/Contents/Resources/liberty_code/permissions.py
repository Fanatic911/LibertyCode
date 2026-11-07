"""Decides when to ask, and shows the inline approval menu."""
from __future__ import annotations

import sys
from typing import Optional

from rich.markup import escape

from . import keys, line_editor, loc, shell, ui
from .models import Decision, PermissionMode, Plan, ToolKind
from .theme import Sym, Theme


class Permissions:
    def __init__(self, mode: PermissionMode):
        self.mode = mode
        self._allowed_commands: set = set()

    def needs(self, p: Plan) -> bool:
        if p.kind == ToolKind.READ:
            return False
        if p.kind == ToolKind.EDIT:
            return self.mode == PermissionMode.ASK
        if p.kind == ToolKind.DELETE:
            return self.mode != PermissionMode.AUTO
        if p.kind == ToolKind.COMMAND:
            return p.dangerous or (self.mode != PermissionMode.AUTO
                                   and p.key not in self._allowed_commands
                                   and not shell.is_safe_read_only(p.key))
        return True

    def remember(self, p: Plan) -> None:
        if p.kind == ToolKind.EDIT:
            self.mode = PermissionMode.EDITS
        elif p.kind == ToolKind.COMMAND and not p.dangerous:
            self._allowed_commands.add(p.key)

    def ask(self, p: Plan) -> tuple:
        """Returns (Decision, note). note = what the user wants the model to do instead (when declined)."""
        options = [("Yes", Decision.YES)]
        if not p.dangerous and p.kind == ToolKind.EDIT:
            options.append(("Yes, and don't ask again for edits this session", Decision.ALWAYS))
        if not p.dangerous and p.kind == ToolKind.COMMAND:
            options.append(("Yes, and don't ask again for this exact command", Decision.ALWAYS))
        options.append(("No", Decision.NO))

        question = "This command looks destructive. Run it anyway?" if p.dangerous else p.question
        q_color = Theme.RED if p.dangerous else Theme.VIOLET
        has_always = any(d == Decision.ALWAYS for _, d in options)

        if not ui.interactive():
            ui.line(f"  {ui.M(q_color, loc.T(question))} {ui.dim('[y]es / [a]lways / [n]o')}")
            a = (sys.stdin.readline() or "").strip().lower()
            if a in ("y", "yes"):
                return Decision.YES, None
            if a in ("a", "always") and has_always:
                return Decision.ALWAYS, None
            return Decision.NO, None

        ui.EscWatcher.pause()
        sel = 0
        result = Decision.NO
        try:
            sys.stdout.write("\x1b[?25l")
            ui.line(f"  [bold {q_color}]{escape(loc.T(question))}[/]")
            self._draw(options, sel, first=True)
            while True:
                k = keys.read_key()
                if k.name == "up" or (k.name == "char" and k.char in "kK"):
                    sel = (sel - 1) % len(options)
                elif k.name in ("down", "tab") or (k.name == "char" and k.char in "jJ"):
                    sel = (sel + 1) % len(options)
                elif k.name == "enter":
                    result = options[sel][1]
                    break
                elif k.name == "char" and k.char in "yY":
                    result = Decision.YES
                    break
                elif k.name == "char" and k.char in "aA" and has_always:
                    result = Decision.ALWAYS
                    break
                elif (k.name == "char" and k.char in "nN") or k.name in ("esc", "ctrl_c"):
                    result = Decision.NO
                    break
                elif k.name == "char" and k.char.isdigit() and 1 <= int(k.char) <= len(options):
                    result = options[int(k.char) - 1][1]
                    break
                else:
                    continue
                self._draw(options, sel, first=False)
            # collapse the menu (question + options)
            sys.stdout.write(f"\x1b[{len(options) + 1}A\r\x1b[J")
        finally:
            sys.stdout.write("\x1b[?25h")
            sys.stdout.flush()
            ui.EscWatcher.resume()

        if result != Decision.NO:
            return result, None

        # The Esc watcher stays paused while typing: otherwise it would eat keystrokes.
        ui.EscWatcher.pause()
        try:
            note = line_editor.read([("fg:" + Theme.DIM, f"  tell the model what to do instead (enter to skip) {Sym.POINTER} ")])
        finally:
            ui.EscWatcher.resume()
        return Decision.NO, (note.strip() if note and note.strip() else None)

    @staticmethod
    def _draw(options: list, sel: int, first: bool) -> None:
        if not first:
            sys.stdout.write(f"\x1b[{len(options)}A")
        for i, (label, _) in enumerate(options):
            sys.stdout.write("\r\x1b[2K")
            sys.stdout.flush()
            text = escape(f"{i + 1}. {loc.T(label)}")
            ui.line(f"  [bold {Theme.ORANGE}]{Sym.POINTER}[/] [bold]{text}[/]" if i == sel
                    else f"    [{Theme.DIM}]{text}[/]")
