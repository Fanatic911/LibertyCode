"""Small models sometimes print their tool calls as plain text (list_dir{}<tool_call|>, <tool_call>{json}</tool_call>...)
instead of using the API. This finds them, hides them from the screen and turns them into real calls."""
from __future__ import annotations

import json
import re
import uuid
from typing import Optional

from . import tools
from .models import ToolCall

Q = '<|"|>'
_MARKERS = ["<|tool_call>", "<tool_call>", "<tool_call|>", "</tool_call>", "<function=", "call:", Q]
_NAMES = "|".join(re.escape(t.name) for t in tools.SPECS)

_JSON_RX = re.compile(r"<\|?tool_call\|?>\s*(\{.*?\})\s*(?:</tool_call>|<tool_call\|>)", re.S)
_XML_RX = re.compile(r"<function=(\w+)>(.*?)</function>", re.S)
_PARAM_RX = re.compile(r"<parameter=(\w+)>\s*(.*?)\s*</parameter>", re.S)
_START_RX = re.compile(r"(?:<\|?tool_call\|?>\s*)?(?:call:\s*)?(?<![\w.])(" + _NAMES + r")\s*\{")
_TRAIL_RX = re.compile(r"\s*(?:</?\|?tool_call\|?>)?")
_STRAY_RX = re.compile(r'</?\|?tool_call\|?>|<\|"\|>')


def _known(name: Optional[str]) -> bool:
    return name is not None and any(t.name == name for t in tools.SPECS)


def _make(name: str, args: dict) -> ToolCall:
    return ToolCall(("call_" + uuid.uuid4().hex)[:20], name, args)


def extract(text: str) -> tuple:
    """Returns (text without the tool calls, the calls found in it)."""
    found: list = []   # (start, end, call)

    def free(s: int, e: int) -> bool:
        return not any(s < fe and e > fs for fs, fe, _ in found)

    for m in _JSON_RX.finditer(text):
        try:
            o = json.loads(m.group(1))
            name = o.get("name") if isinstance(o.get("name"), str) else None
            a = o.get("arguments")
            if a is None:
                a = o.get("parameters")
            args = a if isinstance(a, dict) else json.loads(a) if isinstance(a, str) else {}
            if not isinstance(args, dict):
                continue
            if _known(name):
                found.append((m.start(), m.end(), _make(name, args)))
        except Exception:
            pass

    for m in _XML_RX.finditer(text):
        if not _known(m.group(1)) or not free(m.start(), m.end()):
            continue
        args = {p.group(1): p.group(2) for p in _PARAM_RX.finditer(m.group(2))}
        found.append((m.start(), m.end(), _make(m.group(1), args)))

    for m in _START_RX.finditer(text):
        end, args = _parse_braces(text, m.end())
        if end < 0:
            continue
        end += len(_TRAIL_RX.match(text, end).group(0))
        if free(m.start(), end):
            found.append((m.start(), end, _make(m.group(1), args)))

    found.sort(key=lambda f: f[0])
    out: list = []
    pos = 0
    for s, e, _ in found:
        out.append(text[pos:s])
        pos = e
    out.append(text[pos:])
    return _STRAY_RX.sub("", "".join(out)).rstrip(), [f[2] for f in found]


def _parse_braces(t: str, i: int) -> tuple:
    """Parses  key:value,key:value}  (values as <|"|>text<|"|>, "text", number or bare word).
    Returns (index after '}', args), or (-1, {})."""
    args: dict = {}
    while True:
        while i < len(t) and (t[i].isspace() or t[i] == ","):
            i += 1
        if i >= len(t):
            return -1, {}
        if t[i] == "}":
            return i + 1, args

        colon = t.find(":", i)
        if colon < 0:
            return -1, {}
        key = t[i:colon].strip().strip("\"'")
        if not re.fullmatch(r"\w+", key):
            return -1, {}
        i = colon + 1
        while i < len(t) and t[i].isspace():
            i += 1
        if i >= len(t):
            return -1, {}

        if t.startswith(Q, i):
            e = t.find(Q, i + len(Q))
            if e < 0:
                return -1, {}
            args[key] = t[i + len(Q):e]
            i = e + len(Q)
        elif t[i] == '"':
            e = i + 1
            while e < len(t) and t[e] != '"':
                e += 2 if t[e] == "\\" else 1
            if e >= len(t):
                return -1, {}
            try:
                args[key] = json.loads(t[i:e + 1])
            except ValueError:
                return -1, {}
            i = e + 1
        else:
            e = i
            while e < len(t) and t[e] not in ",}":
                e += 1
            if e >= len(t):
                return -1, {}
            raw = t[i:e].strip()
            if raw.lower() in ("true", "false"):
                args[key] = raw.lower() == "true"
            else:
                try:
                    args[key] = int(raw)
                except ValueError:
                    args[key] = raw
            i = e


def find_start(text: str) -> int:
    """Index where a tool call starts in the text, or -1."""
    m = _START_RX.search(text)
    best = m.start() if m else -1
    for mk in _MARKERS:
        if mk in ("call:", "</tool_call>"):
            continue
        i = text.find(mk)
        if i >= 0 and (best < 0 or i < best):
            best = i
    return best


def held_tail(text: str) -> int:
    """How many trailing characters could still turn into a tool call and must be held back while streaming."""
    for i in range(max(0, len(text) - 24), len(text)):
        tail = text[i:]
        if any(mk.startswith(tail) for mk in _MARKERS):
            return len(text) - i
        word = tail.rstrip()
        at_word_start = i == 0 or not (text[i - 1].isalnum() or text[i - 1] == "_")
        if at_word_start and word and any(sp.name.startswith(word) for sp in tools.SPECS):
            return len(text) - i
    return 0


class LeakFilter:
    """Sits between the model stream and the screen: lets normal text through, holds back anything that looks
    like a tool call."""

    def __init__(self) -> None:
        self._all = ""
        self._shown = 0
        self._cut = -1

    def push(self, chunk: str) -> str:
        self._all += chunk
        t = self._all
        if self._cut < 0:
            frm = max(0, self._shown - 60)   # only the new part needs scanning
            c = find_start(t[frm:])
            if c >= 0:
                self._cut = frm + c
        limit = self._cut if self._cut >= 0 else len(t) - held_tail(t)
        if limit <= self._shown:
            return ""
        s = t[self._shown:limit]
        self._shown = limit
        return s

    def rest(self, clean: str) -> str:
        """What is still to be shown once the stream ended, given the text cleaned of tool calls."""
        return clean[self._shown:] if self._shown < len(clean) else ""
