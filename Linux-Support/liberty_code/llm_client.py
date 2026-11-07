"""Streaming chat client with tool calling. Speaks the OpenAI-compatible and the Anthropic protocols."""
from __future__ import annotations

import json
import time
import uuid
from typing import Callable, Optional

import httpx

from . import logger, providers
from .models import (CancelToken, Cancelled, LlmException, LlmResult, Msg, ToolCall, ToolSpec)

_http = httpx.Client(
    timeout=httpx.Timeout(connect=20, read=120, write=60, pool=60),
    headers={"User-Agent": "LibertyCode/1.0"},
)


def stream(s, api_key: str, system: str, history: list, tools: Optional[list],
           on_text: Callable[[str], None], cancel: CancelToken) -> LlmResult:
    claude = s.Provider == "claude"
    base = providers.base_url(s.Provider)
    state = {"got_text": False, "no_temp": False}

    def text(t: str) -> None:
        state["got_text"] = True
        on_text(t)
        logger.stream_chunk(t)

    attempt = 0
    while True:
        try:
            if claude:
                url, headers, body = _build_claude(base, s.Model, api_key, system, history, tools)
            else:
                url, headers, body = _build_openai(base, s, api_key, system, history, tools, not state["no_temp"])
            return _send(url, headers, body, claude, text, cancel, attempt)
        except LlmException as ex:
            if (ex.status == 400 and not state["no_temp"] and not claude and s.Provider != "openai"
                    and "temperature" in str(ex).lower()):
                state["no_temp"] = True
                logger.warn("This model refuses a custom temperature: retrying with the default one.")
            elif (attempt < 2 and not state["got_text"] and ex.status in (0, 429, 500, 502, 503, 529)
                  and not cancel.cancelled):
                logger.warn(f"Retrying after: {ex} (attempt {attempt + 1}/3)")
                if cancel.wait(2 if attempt == 0 else 5):
                    raise Cancelled()
            else:
                raise
            attempt += 1


def _send(url: str, headers: dict, body: bytes, claude: bool, text: Callable[[str], None],
          cancel: CancelToken, attempt: int) -> LlmResult:
    if logger.debug_enabled():
        lines = "\n".join(f"  {k}: {logger.mask_header(k, v)}" for k, v in headers.items())
        sbody = body.decode("utf-8", errors="replace")
        logger.trace("REQ", f"POST {url} (attempt {attempt + 1})\n{lines}\n  body ({len(sbody)} chars): {logger.clip(sbody, 6000)}")
    t0 = time.monotonic()
    try:
        with _http.stream("POST", url, headers=headers, content=body) as res:
            unregister = cancel.on_cancel(res.close)
            try:
                logger.trace("RES", f"HTTP {res.status_code} {res.reason_phrase} - headers after "
                                    f"{int((time.monotonic() - t0) * 1000)} ms{_interesting_headers(res)}")
                if res.status_code >= 300:
                    err_body = res.read().decode("utf-8", errors="replace")
                    logger.trace("RES", "Error body: " + logger.clip(err_body, 4000))
                    raise LlmException(res.status_code, _extract_error(err_body))
                try:
                    r = _parse_claude(res, text, cancel) if claude else _parse_openai(res, text, cancel)
                finally:
                    logger.stream_end()
                logger.trace("RES", f"Stream finished in {int((time.monotonic() - t0) * 1000)} ms: {len(r.text)} chars of text, "
                                    f"{len(r.calls)} tool call(s), tokens in={r.in_tokens} out={r.out_tokens}")
                return r
            finally:
                unregister()
    except LlmException:
        raise
    except Cancelled:
        raise
    except httpx.TimeoutException:
        if cancel.cancelled:
            raise Cancelled()
        raise LlmException(0, "The provider took too long to answer.")
    except (httpx.HTTPError, httpx.StreamError, OSError) as ex:
        if cancel.cancelled:
            raise Cancelled()
        raise LlmException(0, "Network error: " + str(ex))


def _interesting_headers(res: httpx.Response) -> str:
    if not logger.debug_enabled():
        return ""
    keep = [f"{k}={v}" for k, v in res.headers.items()
            if "request-id" in k.lower() or k.lower() == "retry-after" or "ratelimit" in k.lower()]
    return "\n  " + "\n  ".join(keep) if keep else ""


def _extract_error(body: str) -> str:
    try:
        o = json.loads(body)
        if isinstance(o, dict):
            err = o.get("error")
            m = (err.get("message") if isinstance(err, dict) else None) or o.get("message") \
                or (err if isinstance(err, str) else None)
            if m and str(m).strip():
                return str(m)
    except Exception:
        pass
    body = body.strip()
    return body[:240] + "…" if len(body) > 240 else body


def _dump(o: dict) -> bytes:
    return json.dumps(o, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


# ── Request builders ──────────────────────────────────────────────────────────
def _build_openai(base: str, s, key: str, system: str, history: list, tools: Optional[list],
                  use_temperature: bool = True) -> tuple:
    msgs: list = [{"role": "system", "content": system}]
    for m in history:
        if m.role == "user":
            msgs.append({"role": "user", "content": m.text})
        elif m.role == "assistant":
            o: dict = {"role": "assistant", "content": m.text if m.text else None}
            if m.calls:
                o["tool_calls"] = [{
                    "id": c.id, "type": "function",
                    "function": {"name": c.name, "arguments": json.dumps(c.args, ensure_ascii=False, separators=(",", ":"))},
                } for c in m.calls]
            msgs.append(o)
        elif m.role == "tool":
            msgs.append({"role": "tool", "tool_call_id": m.call_id, "content": m.text if m.text else "(no output)"})

    body: dict = {"model": s.Model, "stream": True, "messages": msgs}
    if s.Provider == "openai":
        body["max_completion_tokens"] = 8192
    else:
        body["max_tokens"] = 8192
    # Low temperature = far fewer random tool calls from small open models. OpenAI is left alone: some models reject it.
    if s.Provider != "openai" and use_temperature:
        body["temperature"] = 0.2
    if s.Provider in ("openai", "groq", "openrouter"):
        body["stream_options"] = {"include_usage": True}
    if tools:
        body["tools"] = [{"type": "function",
                          "function": {"name": t.name, "description": t.description, "parameters": t.schema}}
                         for t in tools]

    url = f"{base}/chat/completions"
    headers = {"Content-Type": "application/json", "Accept": "text/event-stream"}
    providers.apply_auth(headers, s.Provider, key, url)
    return url, headers, _dump(body)


def _build_claude(base: str, model: str, key: str, system: str, history: list, tools: Optional[list]) -> tuple:
    msgs: list = []
    last_user: Optional[dict] = None

    def add_user(block: dict) -> None:
        nonlocal last_user
        if last_user is None:
            last_user = {"role": "user", "content": []}
            msgs.append(last_user)
        last_user["content"].append(block)

    for m in history:
        if m.role == "user":
            add_user({"type": "text", "text": m.text})
        elif m.role == "tool":
            add_user({"type": "tool_result", "tool_use_id": m.call_id,
                      "content": m.text if m.text else "(no output)", "is_error": m.is_error})
        elif m.role == "assistant":
            last_user = None
            blocks: list = []
            if m.text.strip():
                blocks.append({"type": "text", "text": m.text})
            for c in m.calls:
                blocks.append({"type": "tool_use", "id": c.id, "name": c.name, "input": c.args})
            if not blocks:
                blocks.append({"type": "text", "text": "(empty)"})
            msgs.append({"role": "assistant", "content": blocks})

    body: dict = {"model": model, "max_tokens": 8192, "stream": True, "system": system, "messages": msgs}
    if tools:
        body["tools"] = [{"name": t.name, "description": t.description, "input_schema": t.schema} for t in tools]

    url = f"{base}/messages"
    headers = {"Content-Type": "application/json", "x-api-key": key, "anthropic-version": "2023-06-01"}
    return url, headers, _dump(body)


# ── SSE ───────────────────────────────────────────────────────────────────────
def _sse(res: httpx.Response, cancel: CancelToken):
    ev = ""
    data: list = []
    try:
        for ln in res.iter_lines():
            if cancel.cancelled:
                raise Cancelled()
            if ln == "":
                if data:
                    yield ev, "\n".join(data)
                ev = ""
                data = []
                continue
            if ln.startswith(":"):
                continue
            if ln.startswith("event:"):
                ev = ln[6:].strip()
            elif ln.startswith("data:"):
                data.append(ln[5:].lstrip())
        if data:
            yield ev, "\n".join(data)
    except httpx.ReadTimeout:
        if cancel.cancelled:
            raise Cancelled()
        raise LlmException(0, "The model stopped responding (timeout).")
    except (httpx.StreamError, httpx.ReadError, httpx.RemoteProtocolError, OSError) as ex:
        if cancel.cancelled:
            raise Cancelled()
        raise LlmException(0, "Connection lost: " + str(ex))


def _try_parse(data: str) -> Optional[dict]:
    try:
        o = json.loads(data)
        return o if isinstance(o, dict) else None
    except Exception:
        return None


def _parse_args(raw: str) -> dict:
    if not raw.strip():
        return {}
    try:
        o = json.loads(raw)
        if isinstance(o, dict):
            return o
    except Exception:
        pass
    return {"__error": "The tool arguments were not valid JSON."}


# ── OpenAI-compatible stream ──────────────────────────────────────────────────
def _parse_openai(res: httpx.Response, on_text: Callable[[str], None], cancel: CancelToken) -> LlmResult:
    r = LlmResult()
    text: list = []
    calls: dict = {}   # index -> {"id","name","args"}
    nxt = last = 0

    for _, data in _sse(res, cancel):
        if data == "[DONE]":
            break
        o = _try_parse(data)
        if o is None:
            continue
        if isinstance(o.get("error"), dict):
            raise LlmException(0, str(o["error"].get("message") or "Provider error"))

        u = o.get("usage")
        if isinstance(u, dict):
            r.in_tokens = int(u.get("prompt_tokens") or r.in_tokens)
            r.out_tokens = int(u.get("completion_tokens") or r.out_tokens)

        choices = o.get("choices")
        if not isinstance(choices, list) or not choices or not isinstance(choices[0], dict):
            continue
        delta = choices[0].get("delta")
        if not isinstance(delta, dict):
            continue

        content = delta.get("content")
        if isinstance(content, str) and content:
            text.append(content)
            on_text(content)

        tcs = delta.get("tool_calls")
        if isinstance(tcs, list):
            for tc in tcs:
                if not isinstance(tc, dict):
                    continue
                id_ = tc.get("id")
                idx = tc.get("index")
                if not isinstance(idx, int):
                    idx = nxt if id_ else last
                pc = calls.setdefault(idx, {"id": "", "name": "", "args": []})
                nxt = max(nxt, idx + 1)
                last = idx
                if id_:
                    pc["id"] = id_
                fn = tc.get("function")
                if isinstance(fn, dict):
                    if fn.get("name"):
                        pc["name"] = fn["name"]
                    if isinstance(fn.get("arguments"), str):
                        pc["args"].append(fn["arguments"])

    r.text = "".join(text)
    for idx in sorted(calls):
        pc = calls[idx]
        if not pc["name"]:
            continue
        cid = pc["id"] or ("call_" + uuid.uuid4().hex)[:20]
        r.calls.append(ToolCall(cid, pc["name"], _parse_args("".join(pc["args"]))))
    return r


# ── Anthropic stream ──────────────────────────────────────────────────────────
def _parse_claude(res: httpx.Response, on_text: Callable[[str], None], cancel: CancelToken) -> LlmResult:
    r = LlmResult()
    text: list = []
    blocks: dict = {}

    for ev, data in _sse(res, cancel):
        o = _try_parse(data)
        if o is None:
            continue
        typ = o.get("type") or ev
        if typ == "message_start":
            u = (o.get("message") or {}).get("usage") or {}
            r.in_tokens = (int(u.get("input_tokens") or 0) + int(u.get("cache_read_input_tokens") or 0)
                           + int(u.get("cache_creation_input_tokens") or 0))
        elif typ == "content_block_start":
            idx = int(o.get("index") or 0)
            cb = o.get("content_block") or {}
            blocks[idx] = {"type": cb.get("type") or "", "id": cb.get("id") or "", "name": cb.get("name") or "", "json": []}
        elif typ == "content_block_delta":
            idx = int(o.get("index") or 0)
            d = o.get("delta") or {}
            dt = d.get("type") or ""
            if dt == "text_delta":
                t = d.get("text") or ""
                if t:
                    text.append(t)
                    on_text(t)
            elif dt == "input_json_delta" and idx in blocks:
                blocks[idx]["json"].append(d.get("partial_json") or "")
        elif typ == "message_delta":
            r.out_tokens = int((o.get("usage") or {}).get("output_tokens") or r.out_tokens)
        elif typ == "error":
            raise LlmException(0, str((o.get("error") or {}).get("message") or "Provider error"))

    r.text = "".join(text)
    for idx in sorted(blocks):
        b = blocks[idx]
        if b["type"] == "tool_use":
            r.calls.append(ToolCall(b["id"], b["name"], _parse_args("".join(b["json"]))))
    return r
