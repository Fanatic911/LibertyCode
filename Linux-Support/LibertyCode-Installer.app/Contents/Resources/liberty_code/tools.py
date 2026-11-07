"""The tools the model can call. Each call is first turned into a Plan so it can be previewed and approved."""
from __future__ import annotations

import os
import re
import time
from typing import Optional

from . import diff as diffmod
from . import loc, shell, ui
from .models import CancelToken, Plan, ToolError, ToolKind, ToolOutput, ToolSpec
from .theme import Sym, Theme
from .workspace import UndoStack, Workspace

_IGNORED = {".git", ".vs", ".idea", ".vscode", "node_modules", "bin", "obj", "__pycache__", "dist", "target", ".venv", "venv"}
_IGNORED_LOWER = {n.lower() for n in _IGNORED}


def _spec(name: str, desc: str, props: dict, required: Optional[list] = None) -> ToolSpec:
    schema: dict = {"type": "object", "properties": props}
    if required:
        schema["required"] = required
    return ToolSpec(name, desc, schema)


SPECS = [
    _spec("read_file", "Read a text file. Returns numbered lines. Use offset/limit for large files.",
          {"path": {"type": "string", "description": "File path relative to the workspace"},
           "offset": {"type": "integer", "description": "First line to read (1-based)"},
           "limit": {"type": "integer", "description": "Max lines (default 2000)"}}, ["path"]),
    _spec("write_file", "Create a new file or fully overwrite an existing one. Prefer edit_file for changes to existing files.",
          {"path": {"type": "string"}, "content": {"type": "string", "description": "Complete file content"}},
          ["path", "content"]),
    _spec("edit_file", "Replace text in an existing file. old_string must match exactly (whitespace included) and be unique unless replace_all is true.",
          {"path": {"type": "string"}, "old_string": {"type": "string"}, "new_string": {"type": "string"},
           "replace_all": {"type": "boolean", "description": "Replace every occurrence"}},
          ["path", "old_string", "new_string"]),
    _spec("delete_file", "Delete a file.", {"path": {"type": "string"}}, ["path"]),
    _spec("list_dir", "List the files and folders of a directory (not recursive).",
          {"path": {"type": "string", "description": "Directory, default is the workspace root"}}),
    _spec("glob", "Find files by glob pattern, e.g. **/*.py or src/**/*.json.",
          {"pattern": {"type": "string"}}, ["pattern"]),
    _spec("grep", "Search file contents with a regular expression. Returns file:line: text.",
          {"pattern": {"type": "string", "description": "Regular expression"},
           "path": {"type": "string", "description": "File or directory to search (default: workspace)"},
           "glob": {"type": "string", "description": "Only files matching this glob, e.g. *.py"},
           "ignore_case": {"type": "boolean"}}, ["pattern"]),
    _spec("run_command", "Run a shell command in the workspace (build, test, git...). The user must approve it.",
          {"command": {"type": "string"},
           "timeout_seconds": {"type": "integer", "description": "Default 120, max 600"}}, ["command"]),
]


# ── Argument helpers ──────────────────────────────────────────────────────────
def _str(a: dict, key: str, required: bool = True) -> str:
    v = a.get(key)
    v = v if isinstance(v, str) else None
    if required and v is None:
        raise ToolError(f"Missing required argument '{key}'.")
    return v or ""


def _int(a: dict, key: str) -> Optional[int]:
    v = a.get(key)
    if isinstance(v, bool) or not isinstance(v, (int, float)):
        return None
    return int(v)


def _bool(a: dict, key: str) -> bool:
    return a.get(key) is True


def describe(tool: str, a: dict) -> tuple:
    def p(k: str) -> str:
        v = a.get(k)
        return v if isinstance(v, str) else ""
    return {
        "read_file": ("Read", p("path")),
        "write_file": ("Write", p("path")),
        "edit_file": ("Update", p("path")),
        "delete_file": ("Delete", p("path")),
        "list_dir": ("List", p("path") or "."),
        "glob": ("Glob", p("pattern")),
        "grep": ("Search", p("pattern")),
        "run_command": ("Run", " ".join(p("command").splitlines())),
    }.get(tool, (tool, ""))


def prepare(ws: Workspace, undo: UndoStack, tool: str, a: dict) -> Plan:
    if "__error" in a:
        raise ToolError(str(a.get("__error") or "Invalid arguments."))
    fn = {
        "read_file": lambda: _prepare_read(ws, a),
        "write_file": lambda: _prepare_write(ws, undo, a),
        "edit_file": lambda: _prepare_edit(ws, undo, a),
        "delete_file": lambda: _prepare_delete(ws, undo, a),
        "list_dir": lambda: _prepare_list(ws, a),
        "glob": lambda: _prepare_glob(ws, a),
        "grep": lambda: _prepare_grep(ws, a),
        "run_command": lambda: _prepare_run(ws, a),
    }.get(tool)
    if fn is None:
        raise ToolError(f"Unknown tool '{tool}'.")
    return fn()


def _instant(f) -> Plan:
    return Plan(kind=ToolKind.READ, run=lambda _ct: f())


# ── Text file helpers ─────────────────────────────────────────────────────────
def _read_text(path: str) -> tuple:
    """Reads a text file without ever corrupting accents: UTF-8 first, and if the bytes are not valid UTF-8
    (old Windows-1252 files) they are decoded as Latin-1, which round-trips byte for byte.
    Returns (text, bom, latin1)."""
    with open(path, "rb") as f:
        data = f.read()
    if b"\x00" in data[:8000]:
        raise ToolError("This looks like a binary file.")
    bom = data[:3] == b"\xef\xbb\xbf"
    try:
        return data[3 if bom else 0:].decode("utf-8"), bom, False
    except UnicodeDecodeError:
        return data.decode("latin-1"), False, True


def _write_text(path: str, text: str, bom: bool, latin1: bool = False) -> None:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    body = text.encode("latin-1") if latin1 else text.encode("utf-8")
    with open(path, "wb") as f:
        f.write((b"\xef\xbb\xbf" + body) if bom else body)


_FOLD = str.maketrans({
    "\u2010": "-", "\u2011": "-", "\u2012": "-", "\u2013": "-", "\u2014": "-", "\u2015": "-", "\u2212": "-",
    "\u2018": "'", "\u2019": "'", "\u201b": "'",
    "\u201c": '"', "\u201d": '"', "\u201f": '"',
    "\u00a0": " ", "\u2007": " ", "\u2009": " ", "\u202f": " ",
})


def _fold(s: str) -> str:
    """Maps typographic look-alikes (em/en dashes, curly quotes, non-breaking spaces) to plain ASCII, one char for
    one char, so indices stay valid. Models often type '-' where the file has an em dash (or the reverse)."""
    return s.translate(_FOLD)


def _match_eol(s: str, crlf: bool) -> str:
    s = s.replace("\r\n", "\n")
    return s.replace("\n", "\r\n") if crlf else s


def _stats(added: int, removed: int) -> str:
    return f"[{Theme.GREEN}]+{added}[/] [{Theme.RED}]−{removed}[/]"


def _plural(n: int, one: str, many: str) -> str:
    return loc.T((one if n == 1 else many).format(n))


# ── read_file ─────────────────────────────────────────────────────────────────
def _prepare_read(ws: Workspace, a: dict) -> Plan:
    full = ws.resolve(_str(a, "path"))
    offset = max(1, _int(a, "offset") or 1)
    limit = min(5000, max(1, _int(a, "limit") or 2000))

    def run() -> ToolOutput:
        if os.path.isdir(full):
            raise ToolError("That is a directory. Use list_dir.")
        if not os.path.isfile(full):
            raise ToolError(f"File not found: {ws.rel(full)}")
        if os.path.getsize(full) > 4_000_000:
            with open(full, "rb") as f:
                if b"\x00" in f.read(8000):
                    raise ToolError("This looks like a binary file.")
            with open(full, "r", encoding="utf-8", errors="replace") as f:
                all_lines = [l.rstrip("\r\n") for l in f]
        else:
            text = _read_text(full)[0]
            all_lines = re.split(r"\r\n|\r|\n", text)
            if all_lines and all_lines[-1] == "":
                all_lines.pop()
        out: list = []
        total = len(all_lines)
        shown = 0
        for n, line in enumerate(all_lines, 1):
            if n < offset or shown >= limit:
                continue
            l = line[:2000] + "…" if len(line) > 2000 else line
            out.append(f"{str(n).rjust(6)}\t{l}")
            shown += 1
        if total == 0:
            return ToolOutput(for_model="(empty file)", summary=ui.dim("Empty file"))
        if shown == 0:
            raise ToolError(f"Offset {offset} is past the end of the file ({total} lines).")
        if offset + shown - 1 < total:
            out.append(f"[file has {total} lines; showing {offset}-{offset + shown - 1}. Use offset/limit to read more.]")
        return ToolOutput(for_model="\n".join(out) + "\n",
                          summary=ui.dim(_plural(shown, "Read {0} line", "Read {0} lines")))

    return _instant(run)


# ── write_file ────────────────────────────────────────────────────────────────
def _prepare_write(ws: Workspace, undo: UndoStack, a: dict) -> Plan:
    full = ws.resolve(_str(a, "path"))
    content = _str(a, "content")
    rel = ws.rel(full)
    if os.path.isdir(full):
        raise ToolError("That path is a directory.")

    exists = os.path.isfile(full)
    old, bom, latin1 = "", False, False
    if exists:
        old, bom, latin1 = _read_text(full)
    crlf = "\r\n" in old
    final = _match_eol(content, crlf) if exists else content

    if exists and final == old:
        return _instant(lambda: ToolOutput(for_model="No changes: the file already has this content.",
                                           summary=ui.dim(loc.T("No changes"))))

    new_lines = diffmod.split_lines(final)

    def run(_ct: CancelToken) -> ToolOutput:
        undo.record(full)
        # a Latin-1 file stays Latin-1 unless the new text contains characters it cannot hold
        _write_text(full, final, bom, latin1 and all(ord(ch) <= 0xFF for ch in final))
        return ToolOutput(for_model=f"{'Updated' if exists else 'Created'} {rel}.")

    plan = Plan(kind=ToolKind.EDIT, question="Overwrite this file?" if exists else "Create this file?", run=run)
    if not exists:
        plan.headline = f"{ui.M(Theme.GREEN, 'Created')} {ui.dim(f'· {len(new_lines)} lines')}"
        plan.preview = diffmod.render_whole(new_lines, added=True)
    else:
        d = diffmod.compute(diffmod.split_lines(old), new_lines)
        ad, rm = diffmod.count(d)
        plan.headline = f"{ui.M(Theme.YELLOW, 'Rewrote')} {_stats(ad, rm)}"
        plan.preview = diffmod.render(d)
    return plan


# ── edit_file ─────────────────────────────────────────────────────────────────
def _prepare_edit(ws: Workspace, undo: UndoStack, a: dict) -> Plan:
    full = ws.resolve(_str(a, "path"))
    rel = ws.rel(full)
    old_str, new_str = _str(a, "old_string"), _str(a, "new_string")
    replace_all = _bool(a, "replace_all")

    if not os.path.isfile(full):
        raise ToolError(f"File not found: {rel}. Use write_file to create it.")
    if not old_str:
        raise ToolError("old_string is empty. Use write_file to create or overwrite a file.")
    if old_str == new_str:
        raise ToolError("old_string and new_string are identical.")

    text, bom, latin1 = _read_text(full)
    crlf = "\r\n" in text
    old_str = _match_eol(old_str, crlf)
    new_str = _match_eol(new_str, crlf)

    # Exact match first; if that fails, retry ignoring typographic look-alikes (em dash vs -, curly quotes...).
    hay, needle = text, old_str
    count = hay.count(needle)
    if count == 0:
        fh, fn = _fold(text), _fold(old_str)
        fc = fh.count(fn)
        if fc > 0:
            hay, needle, count = fh, fn, fc
    if count == 0:
        shown = text[:6000] + "\n… [truncated]" if len(text) > 6000 else text
        raise ToolError("old_string was not found. It must match the file exactly, including whitespace. Read the file again.",
                        f"Current content of {rel} (copy old_string from here, do not retype it):\n{shown}")
    if count > 1 and not replace_all:
        raise ToolError(f"old_string appears {count} times. Add more surrounding context to make it unique, or set replace_all.")

    if replace_all:
        parts: list = []
        last = 0
        while True:
            at = hay.find(needle, last)
            if at < 0:
                break
            parts.append(text[last:at])
            parts.append(new_str)
            last = at + len(needle)
        parts.append(text[last:])
        result = "".join(parts)
    else:
        at = hay.find(needle)
        result = text[:at] + new_str + text[at + len(needle):]

    d = diffmod.compute(diffmod.split_lines(text), diffmod.split_lines(result))
    ad, rm = diffmod.count(d)

    def run(_ct: CancelToken) -> ToolOutput:
        undo.record(full)
        _write_text(full, result, bom, latin1 and all(ord(ch) <= 0xFF for ch in result))
        return ToolOutput(for_model=f"Edited {rel} (+{ad} −{rm}).")

    places = ui.dim(f" · {count} places") if count > 1 else ""
    return Plan(kind=ToolKind.EDIT, question="Apply this edit?",
                headline=f"{ui.M(Theme.YELLOW, 'Updated')} {_stats(ad, rm)}{places}",
                preview=diffmod.render(d), run=run)


# ── delete_file ───────────────────────────────────────────────────────────────
def _prepare_delete(ws: Workspace, undo: UndoStack, a: dict) -> Plan:
    full = ws.resolve(_str(a, "path"))
    rel = ws.rel(full)
    if os.path.isdir(full):
        raise ToolError("That is a directory. delete_file only removes files.")
    if not os.path.isfile(full):
        raise ToolError(f"File not found: {rel}")

    try:
        lines = diffmod.split_lines(_read_text(full)[0])
    except ToolError:
        lines = []

    def run(_ct: CancelToken) -> ToolOutput:
        undo.record(full)
        os.remove(full)
        return ToolOutput(for_model=f"Deleted {rel}.")

    return Plan(kind=ToolKind.DELETE, question="Delete this file?",
                headline=f"{ui.M(Theme.RED, 'Deleted')} {ui.dim(f'· {len(lines)} lines')}",
                preview=diffmod.render_whole(lines, added=False, max_lines=8) if lines else None, run=run)


# ── list_dir ──────────────────────────────────────────────────────────────────
def _prepare_list(ws: Workspace, a: dict) -> Plan:
    p = _str(a, "path", required=False)
    full = ws.resolve(p if p else ".")

    def run() -> ToolOutput:
        if not os.path.isdir(full):
            raise ToolError(f"Directory not found: {ws.rel(full)}")
        entries = [e for e in os.listdir(full) if e.lower() not in _IGNORED_LOWER]
        entries.sort(key=lambda e: (not os.path.isdir(os.path.join(full, e)), e.lower()))
        out = [e + "/" if os.path.isdir(os.path.join(full, e)) else e for e in entries[:200]]
        if len(entries) > 200:
            out.append(f"… +{len(entries) - 200} more")
        return ToolOutput(for_model="(empty directory)" if not entries else "\n".join(out) + "\n",
                          summary=ui.dim(_plural(len(entries), "{0} item", "{0} items")))

    return _instant(run)


# ── glob / grep ───────────────────────────────────────────────────────────────
def _walk(d: str):
    try:
        entries = sorted(os.listdir(d), key=str.lower)
    except OSError:
        entries = []
    for e in entries:
        full = os.path.join(d, e)
        if os.path.isdir(full):
            if e.lower() in _IGNORED_LOWER:
                continue
            yield from _walk(full)
        else:
            yield full


def _glob_to_regex(glob: str) -> "re.Pattern":
    glob = glob.replace("\\", "/")
    sb = ["^"]
    if "/" not in glob:
        sb.append("(?:.*/)?")
    i = 0
    while i < len(glob):
        c = glob[i]
        if c == "*":
            if i + 1 < len(glob) and glob[i + 1] == "*":
                i += 1
                if i + 1 < len(glob) and glob[i + 1] == "/":
                    i += 1
                    sb.append("(?:.*/)?")
                else:
                    sb.append(".*")
            else:
                sb.append("[^/]*")
        elif c == "?":
            sb.append("[^/]")
        else:
            sb.append(re.escape(c))
        i += 1
    sb.append("$")
    return re.compile("".join(sb), re.IGNORECASE)


def _prepare_glob(ws: Workspace, a: dict) -> Plan:
    rx = _glob_to_regex(_str(a, "pattern"))

    def run() -> ToolOutput:
        hits = [r for r in (ws.rel(f).replace("\\", "/") for f in _walk(ws.root)) if rx.match(r)]
        body = "No files matched." if not hits else "\n".join(hits[:200]) + (f"\n… +{len(hits) - 200} more" if len(hits) > 200 else "")
        return ToolOutput(for_model=body, summary=ui.dim(_plural(len(hits), "{0} file", "{0} files")))

    return _instant(run)


def _prepare_grep(ws: Workspace, a: dict) -> Plan:
    pattern = _str(a, "pattern")
    p = _str(a, "path", required=False)
    start = ws.resolve(p if p else ".")
    glob_str = _str(a, "glob", required=False)
    try:
        rx = re.compile(pattern, (re.IGNORECASE if _bool(a, "ignore_case") else 0) | re.MULTILINE)
    except re.error as ex:
        raise ToolError("Invalid regular expression: " + str(ex))
    file_rx = _glob_to_regex(glob_str) if glob_str else None

    def run() -> ToolOutput:
        files = [start] if os.path.isfile(start) else _walk(start)
        out: list = []
        matches = 0
        for f in files:
            rel = ws.rel(f).replace("\\", "/")
            if file_rx is not None and not file_rx.match(rel):
                continue
            try:
                if os.path.getsize(f) > 1_000_000:
                    continue
                with open(f, "r", encoding="utf-8", errors="replace") as fh:
                    for n, line in enumerate(fh, 1):
                        line = line.rstrip("\r\n")
                        if n == 1 and "\0" in line:
                            break
                        if not rx.search(line):
                            continue
                        out.append(f"{rel}:{n}: {line[:300] + '…' if len(line) > 300 else line.strip()}")
                        matches += 1
                        if matches >= 100:
                            break
            except OSError:
                pass
            if matches >= 100:
                break
        return ToolOutput(for_model="No matches." if matches == 0 else "\n".join(out) + "\n" + ("[stopped at 100 matches]" if matches >= 100 else ""),
                          summary=ui.dim(_plural(matches, "{0} match", "{0} matches")))

    return _instant(run)


# ── run_command ───────────────────────────────────────────────────────────────
def _prepare_run(ws: Workspace, a: dict) -> Plan:
    command = _str(a, "command").strip()
    if not command:
        raise ToolError("Empty command.")
    outside = ws.outside_path(command)
    if outside:
        raise ToolError(f"Access denied: the command reaches '{outside}', outside the workspace.",
                        "Do not retry with another path or command. Tell the user you can only work inside the workspace.")
    timeout = min(600, max(5, _int(a, "timeout_seconds") or 120))

    def run(ct: CancelToken) -> ToolOutput:
        t0 = time.monotonic()
        code, output, timed_out = shell.run(command, ws.root, timeout, ct)
        elapsed = time.monotonic() - t0

        t = f" · {elapsed:.1f}".rstrip("0").rstrip(".") + "s" if elapsed >= 1 else ""
        res = ToolOutput(is_error=code != 0)
        if timed_out:
            res.summary = ui.M(Theme.RED, loc.T(f"Timed out after {timeout}s"))
        elif code == 0:
            res.summary = f"{ui.M(Theme.GREEN, Sym.CHECK)} {ui.dim('exit 0' + t)}"
        else:
            res.summary = f"{ui.M(Theme.RED, Sym.CROSS)} {ui.M(Theme.RED, 'exit ' + str(code))}{ui.dim(t)}"

        lines = [l.rstrip("\r") for l in output.split("\n") if l.rstrip("\r")]
        show = 3 if code == 0 else 8
        tail = lines[-show:] if len(lines) > show else lines
        details: list = []
        if len(lines) > show:
            details.append(f"     {ui.dim(loc.T(f'… +{len(lines) - show} lines'))}")
        for l in tail:
            l = l.replace("\t", "    ")
            room = max(20, ui.width() - 7)
            if len(l) > room:
                l = l[:room - 1] + "…"
            details.append(f"     {ui.dim(l)}")
        res.details = details
        res.for_model = (f"Command timed out after {timeout}s.\n" if timed_out else f"exit code: {code}\n") + shell.clip(output)
        return res

    return Plan(kind=ToolKind.COMMAND, question="Run this command?", key=command,
                dangerous=shell.is_dangerous(command), run=run)
