"""Builds the system prompt sent to the model."""
from __future__ import annotations

import datetime
import os
import platform
import sys

from . import loc, paths, shell, ui
from .workspace import Workspace


def build(s, ws: Workspace, tools: bool) -> str:
    sb: list = []
    add = sb.append
    add("You are LibertyCode, a coding agent in the user's terminal. Help with writing, fixing and refactoring code.")
    add("")
    if tools:
        add("# Working method")
        add("- Use tools to act. Never claim you did something without using a tool.")
        add("- Do exactly what the user asked, nothing more. Do not create extra, placeholder or empty files, and do not delete a file you just created.")
        add("- Read a file before editing it. Prefer edit_file for small changes; use write_file for new files or full rewrites.")
        add("- Write each file once, complete and working. No TODO placeholders, no second rewrite of a file you just wrote unless something is actually wrong.")
        add("- If edit_file fails, copy old_string from the file content you are given. Never retype it from memory and never retry the same call.")
        add("- To start a new project, prefer the official generator through run_command (dotnet new console, npm create, cargo new, python -m venv...) over writing project files by hand.")
        add("- Keep exactly one program entry point per project.")
        add("- After changing code, verify it when a build or test command exists (dotnet build, npm test, pytest...), and fix what you broke.")
        add("- If the user refuses an action, do not retry it.")
        add("- Stay inside the workspace. Never read, edit or delete anything outside it (other drives, parent folders, home): refuse in one sentence, even if the user gives an absolute path.")
        add("- Call tools only through the tool-calling interface. Never write a tool call as text (no name{...}, no <tool_call> tags).")
        add("- Work step by step: one tool call at a time unless the calls are independent.")
        add("")
    add("# Style")
    add("- Be direct. No preamble, no restating the request, no summaries.")
    add("- If the request is a single word, an app command or otherwise unclear, ask ONE short question instead of inventing work. "
        "If it clearly asks you to build something, build the smallest version that does what was asked and say what you created.")
    add("- Use markdown sparingly (fenced code with a language tag).")
    add("- When done, say what changed in one sentence.")
    if s.Language.strip() and s.Language != "en":
        name = loc.english_lang_name(s.Language)
        add(f"- ALWAYS reply in {name} ({s.Language}), including explanations and summaries. Keep code, file names and commands unchanged.")
    add("")
    add("# Environment")
    add(f"- Workspace: {ws.root}")
    if os.name == "nt":
        add("- OS: Windows. Commands run with cmd.exe: use Windows syntax (dir, type, &&).")
    elif sys.platform == "darwin":
        add(f"- OS: macOS. Commands run with {shell.unix_shell_name()}. Tools are BSD flavoured "
            "(e.g. sed -i needs a suffix argument: sed -i '' ...). `python` may be missing: use python3.")
    else:
        add(f"- OS: {platform.system()}. Commands run with {shell.unix_shell_name()}. `python` may be missing: use python3.")
    add(f"- Date: {datetime.date.today():%Y-%m-%d}")

    try:
        skip = {"node_modules/", "bin/", "obj/", ".vs/"}
        top = []
        for e in os.listdir(ws.root):
            n = e + "/" if os.path.isdir(os.path.join(ws.root, e)) else e
            if n.startswith(".git") or n in skip:
                continue
            top.append(n)
        top = sorted(top, key=str.lower)[:40]
        add("- The workspace is empty." if not top else "- Top level: " + ", ".join(top))
    except OSError:
        pass

    memory = os.path.join(ws.root, "LIBERTY.md")
    if os.path.isfile(memory):
        try:
            with open(memory, "r", encoding="utf-8", errors="replace") as f:
                text = f.read()
            if len(text) > 8000:
                text = text[:8000] + "\n…"
            add("")
            add("# Project notes (LIBERTY.md)")
            add(text.strip())
        except OSError:
            pass

    extra = user_extra()
    if extra:
        add("")
        add("# Extra instructions from the user")
        add(extra)
    return "\n".join(sb) + "\n"


def user_extra() -> str:
    try:
        if not os.path.isfile(paths.PROMPT_FILE):
            return ""
        with open(paths.PROMPT_FILE, "r", encoding="utf-8") as f:
            lines = [l.rstrip("\n") for l in f if not l.lstrip().startswith("#")]
        return "\n".join(lines).strip()
    except OSError:
        return ""


def view() -> None:
    extra = user_extra()
    ui.line(f"  {ui.dim('Built-in instructions are fixed. Extra instructions (' + ui.short(paths.PROMPT_FILE) + '):')}")
    if not extra:
        ui.line(f"  {ui.dim('(none) — use /prompt edit')}")
    else:
        for l in extra.split("\n"):
            ui.line(f"  {ui.violet(l.rstrip(chr(13)))}")


def edit() -> None:
    """Edits the extra instructions inside the terminal: lines are appended; empty line saves; 'clear' erases all."""
    from . import line_editor
    from .theme import Theme
    view()
    ui.hint("Type lines to add. Empty line = save.  \"clear\" = erase all.")
    lines: list = []
    while True:
        l = line_editor.read([("", "  "), ("fg:" + Theme.ORANGE, "+"), ("", " ")])
        if l is None or not l.strip():
            break
        if l.strip().lower() == "clear":
            try:
                open(paths.PROMPT_FILE, "w", encoding="utf-8").close()
            except OSError:
                pass
            ui.ok("Extra instructions cleared")
            return
        lines.append(l)
    if not lines:
        return
    try:
        old = ""
        if os.path.isfile(paths.PROMPT_FILE):
            with open(paths.PROMPT_FILE, "r", encoding="utf-8") as f:
                old = f.read().rstrip()
        with open(paths.PROMPT_FILE, "w", encoding="utf-8") as f:
            f.write((old + "\n" if old else "") + "\n".join(lines) + "\n")
        ui.ok("Saved. Applies to the next message.")
    except OSError as ex:
        ui.warn("Could not save: " + str(ex))
