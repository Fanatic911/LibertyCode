"""AI providers: endpoints, live model listing, key checks."""
from __future__ import annotations

import json
import os
import re
import threading
import time
from dataclasses import dataclass, field
from typing import Callable, Optional
from urllib.parse import quote, urlparse

import httpx

from . import crypto, logger


@dataclass
class Provider:
    label: str
    base_url: str


@dataclass
class ModelsResult:
    models: list = field(default_factory=list)
    status: int = 0
    error: Optional[str] = None
    verified: bool = False

    @property
    def auth_rejected(self) -> bool:
        return self.status in (401, 403)

    @property
    def ok(self) -> bool:
        return self.error is None and len(self.models) > 0


ALL = {
    "openai":     Provider("OpenAI", "https://api.openai.com/v1"),
    "claude":     Provider("Anthropic", "https://api.anthropic.com/v1"),
    "gemini":     Provider("Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai"),
    "deepseek":   Provider("DeepSeek", "https://api.deepseek.com/v1"),
    "mistral":    Provider("Mistral", "https://api.mistral.ai/v1"),
    "ollama":     Provider("Ollama (Local)", "http://localhost:11434/v1"),
    "groq":       Provider("Groq", "https://api.groq.com/openai/v1"),
    "nvidia":     Provider("NVIDIA NIM", "https://integrate.api.nvidia.com/v1"),
    "github":     Provider("GitHub Models", "https://models.github.ai/inference"),
    "openrouter": Provider("OpenRouter", "https://openrouter.ai/api/v1"),
}

STATIC_MODELS = {
    "openai":     ["gpt-4o", "gpt-4o-mini"],
    "claude":     ["claude-opus-4-5", "claude-sonnet-4-5", "claude-haiku-4-5"],
    "gemini":     ["gemini-2.0-flash", "gemini-1.5-pro", "gemini-1.5-flash"],
    "deepseek":   ["deepseek-chat", "deepseek-reasoner"],
    "mistral":    ["mistral-large-latest", "mistral-small-latest", "codestral-latest"],
    "ollama":     ["llama3.2", "qwen2.5-coder", "mistral"],
    "groq":       ["llama-3.3-70b-versatile", "llama-3.1-8b-instant"],
    "github":     ["openai/gpt-4o", "openai/gpt-4o-mini"],
    "openrouter": ["openai/gpt-4o", "anthropic/claude-sonnet-4-5"],
    "nvidia":     ["meta/llama-3.3-70b-instruct"],
}

_UA = "LibertyCode/1.0"   # GitHub refuses requests without one


def base_url(provider: str) -> str:
    o = os.environ.get("LIBERTY_BASE_URL", "").strip()
    return o.rstrip("/") if o else ALL[provider].base_url


def label(provider: str) -> str:
    return ALL[provider].label if provider in ALL else provider


def short_label(provider: str) -> str:
    return label(provider).replace(" (Local)", "")


def _base_overridden() -> bool:
    return bool(os.environ.get("LIBERTY_BASE_URL", "").strip())


def _list_url(provider: str) -> str:
    if provider == "github" and not _base_overridden():
        return "https://models.github.ai/catalog/models"
    if provider == "gemini" and not _base_overridden():
        return "https://generativelanguage.googleapis.com/v1beta/models"
    return base_url(provider) + "/models"


_NOT_CHAT = ["embed", "whisper", "tts", "dall-e", "moderation", "davinci", "babbage", "audio", "realtime",
             "transcribe", "image", "rerank", "guard", "sora", "flux", "stable-diffusion", "stabilityai",
             "parakeet", "riva", "reward", "nemoguard"]

# NVIDIA's catalog also lists embedding, vision-only, speech, biology and safety models: none of them chat.
_NVIDIA_NOT_CHAT = ["retriev", "clip", "vila", "neva", "paligemma", "kosmos", "fuyu", "deplot", "parse", "safety",
                    "pii", "gliner", "cosmos", "genmol", "diffdock", "esm2", "molmim", "fourcastnet", "corrdiff",
                    "canary", "maxine", "bge-", "e5-", "arctic-embed", "synthetic", "translate", "topic-control",
                    "jailbreak", "studiovoice", "audio2face", "magpie", "usdcode", "streampetr", "bevformer",
                    "sparsedrive", "nv-embed", "nv-rerank", "vlm", "ocr"]

# Models that practically always answer on the hosted API; used to check that a key really works.
_NVIDIA_PROBE = ["meta/llama-3.3-70b-instruct", "meta/llama-3.1-8b-instruct", "nvidia/llama-3.1-nemotron-70b-instruct"]

_OPENAI_CHAT = re.compile(r"^(gpt-|chatgpt-|o\d)", re.I)
_OPENAI_NO = ["instruct", "codex", "search", "deep-research", "computer-use", "-pro", "-realtime", "-audio"]


def _is_chat_model(provider: str, id_: str, m: dict) -> bool:
    low = id_.lower()
    if any(n in low for n in _NOT_CHAT):
        return False
    if isinstance(m.get("active"), bool) and not m["active"]:
        return False
    o1 = m.get("supported_output_modalities")
    if isinstance(o1, list) and "text" not in o1:
        return False
    arch = m.get("architecture")
    o2 = arch.get("output_modalities") if isinstance(arch, dict) else None
    if isinstance(o2, list) and "text" not in o2:
        return False
    if provider == "nvidia" and any(n in low for n in _NVIDIA_NOT_CHAT):
        return False
    if provider == "openai":
        return bool(_OPENAI_CHAT.match(id_)) and not any(n in low for n in _OPENAI_NO)
    if provider == "gemini":
        return "gemini" in low and "embedding" not in low and "aqa" not in low
    return True


_http = httpx.Client(timeout=None, headers={"User-Agent": _UA})


def apply_auth(headers: dict, provider: str, api_key: str, url: str = "") -> None:
    api_key = crypto.clean_key(api_key)
    if provider == "claude":
        headers["x-api-key"] = api_key
        headers["anthropic-version"] = "2023-06-01"
    elif provider == "gemini":
        # the key travels as a query parameter for listing; for chat we use Bearer
        if "key=" not in urlparse(url).query:
            headers["Authorization"] = f"Bearer {api_key}"
    elif provider == "ollama":
        pass  # no auth
    elif api_key.strip():
        headers["Authorization"] = f"Bearer {api_key}"
    if provider == "github":
        headers["Accept"] = "application/vnd.github+json"
        headers["X-GitHub-Api-Version"] = "2022-11-28"


def _log_request(method: str, url: str, headers: dict) -> None:
    if not logger.debug_enabled():
        return
    lines = [f"  {k}: {logger.mask_header(k, v)}" for k, v in headers.items()]
    logger.trace("REQ", f"{method} {url}\n" + "\n".join(lines))


def _fail(status: int, error: str) -> ModelsResult:
    logger.trace("MODEL", f"Listing failed (HTTP {status}): {error}")
    return ModelsResult(status=status, error=error)


def _fallback(provider: str, status: int, body: str) -> ModelsResult:
    if provider in STATIC_MODELS:
        logger.warn(f"{label(provider)} has no model-listing endpoint (HTTP {status}): using the built-in list, not verified.")
        return ModelsResult(models=list(STATIC_MODELS[provider]), status=status, verified=False)
    return _fail(status, _describe_failure(provider, status, body))


def _error_text(body: str) -> str:
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
    return logger.clip(body.strip(), 240)


def _describe_failure(provider: str, status: int, body: str) -> str:
    if status in (401, 403):
        return f"{label(provider)} rejected the API key (HTTP {status})."
    if status == 429:
        return f"{label(provider)} rate limit reached (HTTP 429). Try again in a moment."
    return f"{label(provider)} answered HTTP {status}: {_error_text(body)}"


def _get(url: str, provider: str, api_key: str, timeout: float) -> tuple:
    """Returns (status, body, net_error)."""
    t0 = time.monotonic()
    try:
        headers: dict = {}
        apply_auth(headers, provider, api_key, url)
        _log_request("GET", url, headers)
        res = _http.get(url, headers=headers, timeout=timeout)
        body = res.text
        logger.trace("RES", f"HTTP {res.status_code} {res.reason_phrase} in {int((time.monotonic() - t0) * 1000)} ms "
                            f"({len(body)} chars)\n{logger.clip(body, 700)}")
        return res.status_code, body, None
    except httpx.TimeoutException:
        logger.trace("ERROR", f"GET {url}: timeout after {int((time.monotonic() - t0) * 1000)} ms")
        return 0, "", "The provider did not answer in time (network or timeout)."
    except httpx.HTTPError as ex:
        logger.trace("ERROR", f"GET {url}: {ex}")
        return 0, "", "Network error: " + str(ex)


def get_models(provider: str, api_key: str, timeout_sec: int = 20, probe_key: bool = False) -> ModelsResult:
    # Ollama: local server, no auth needed, just list what is pulled
    if provider == "ollama":
        return _get_ollama_models(timeout_sec)

    logger.trace("MODEL", f"Listing models: provider={provider}, key={logger.mask(api_key)}, timeout={timeout_sec}s")
    raw: list = []
    after: Optional[str] = None
    status = 0

    for page in range(20):
        url = _list_url(provider)
        if provider == "claude":
            url += "?limit=1000" + (f"&after_id={quote(after)}" if after else "")
        if provider == "gemini":
            url += "?key=" + quote(api_key)

        st, body, net_error = _get(url, provider, api_key, timeout_sec)
        status = st
        if net_error is not None:
            return _fail(0, net_error)
        if st in (404, 405) and page == 0:
            return _fallback(provider, st, body)
        if st < 200 or st >= 300:
            return _fail(st, _describe_failure(provider, st, body))

        try:
            root = json.loads(body)
        except Exception:
            return _fail(st, "The provider answered, but not with a model list.")

        if provider == "gemini":
            arr = root.get("models") if isinstance(root, dict) else None
        elif isinstance(root, list):
            arr = root
        else:
            arr = (root.get("data") or root.get("models")) if isinstance(root, dict) else None
        if not isinstance(arr, list):
            return _fail(st, "The provider answered, but not with a model list.")

        for item in arr:
            if not isinstance(item, dict):
                continue
            if provider == "gemini":
                id_ = str(item.get("name") or "").replace("models/", "")
            else:
                id_ = str(item.get("id") or item.get("name") or "")
            if id_ and _is_chat_model(provider, id_, item):
                raw.append(id_)

        if provider == "claude" and isinstance(root, dict) and root.get("has_more") is True and root.get("last_id"):
            after = str(root["last_id"])
            continue
        break

    models = list(dict.fromkeys(raw))
    if provider != "claude":
        models.sort(key=str.lower)
    logger.trace("MODEL", f"{len(models)} chat model(s) available for {provider}")
    if not models:
        return _fail(status, "The provider returned no usable chat model for this key.")

    if probe_key and api_key.strip():
        bad = _probe_key(provider, api_key, models[0], timeout_sec)
        if bad is not None:
            return bad
    return ModelsResult(models=models, status=status, verified=True)


def _get_ollama_models(timeout_sec: int) -> ModelsResult:
    url = base_url("ollama").replace("/v1", "") + "/api/tags"
    logger.trace("MODEL", f"Ollama: listing from {url}")
    try:
        res = _http.get(url, timeout=timeout_sec)
        if res.status_code >= 300:
            return _fallback("ollama", res.status_code, res.text)
        arr = res.json().get("models")
        if not isinstance(arr, list) or not arr:
            return _fallback("ollama", 200, "")
        models = sorted((str(m.get("name") or "") for m in arr if isinstance(m, dict) and m.get("name")), key=str.lower)
        return ModelsResult(models=models, status=200, verified=True)
    except Exception as ex:
        logger.trace("ERROR", "Ollama unreachable: " + str(ex))
        return _fallback("ollama", 0, str(ex))


# ── Real chat requests (tests) ────────────────────────────────────────────────
def _probe(provider: str, api_key: str, model: str) -> tuple:
    """Sends a 1-token chat request. Returns (status, retry_after_seconds). Status 0 = timeout / network error."""
    try:
        url = base_url(provider) + "/chat/completions"
        headers = {"Content-Type": "application/json", "Accept": "application/json"}
        apply_auth(headers, provider, api_key, url)
        _log_request("POST", url, headers)
        body = {"model": model, "max_tokens": 16, "stream": False, "messages": [{"role": "user", "content": "hi"}]}
        res = _http.post(url, headers=headers, content=json.dumps(body), timeout=10)   # no answer in 10 s = timeout
        logger.trace("RES", f"Model test {model}: HTTP {res.status_code}\n{logger.clip(res.text, 400)}")
        retry = None
        try:
            retry = float(res.headers.get("retry-after", ""))
        except ValueError:
            pass
        return res.status_code, retry
    except Exception as ex:
        logger.trace("ERROR", f"Model test {model}: {ex}")
        return 0, None


def test_model(provider: str, api_key: str, model: str) -> int:
    """Sends a 1-token chat request. Returns the HTTP status, or 0 when it timed out or the network failed."""
    return _probe(provider, api_key, model)[0]


_working: dict = {}
_working_lock = threading.Lock()


def _cache_key(api_key: str, model: str) -> str:
    return crypto.clean_key(api_key) + "|" + model


def mark_working(api_key: str, model: str) -> None:
    with _working_lock:
        _working[_cache_key(api_key, model)] = True


def mark_broken(api_key: str, model: str) -> None:
    with _working_lock:
        _working[_cache_key(api_key, model)] = False


def is_known_working(api_key: str, model: str) -> bool:
    with _working_lock:
        return _working.get(_cache_key(api_key, model)) is True


def is_known_broken(api_key: str, model: str) -> bool:
    with _working_lock:
        return _working.get(_cache_key(api_key, model)) is False


def _is_answering(status: int) -> bool:
    """The model answered (or is merely throttled / picky about parameters): it exists and the key can use it."""
    return 200 <= status < 300 or status in (400, 422, 429)


def scan_slowly(api_key: str, models: list, stop: threading.Event) -> None:
    """Background check of every model, one request at a time with a pause between them, so the provider never
    rate-limits us. Results land in the cache as they come. A throttled (429) or failing (5xx) model is retried
    after a pause, never marked broken."""
    gap, timeouts, done = 0.6, 0, 0
    try:
        if stop.wait(3):   # let the startup finish first
            return
        for m in models:
            if stop.is_set():
                return
            with _working_lock:
                if _cache_key(api_key, m) in _working:
                    continue
            st = 0
            for attempt in range(3):
                st, wait = _probe("nvidia", api_key, m)
                if stop.is_set():
                    return
                if st != 429 and st < 500:
                    break
                gap = min(gap * 2, 5.0)
                if stop.wait(min(30.0, max(1.0, wait if wait is not None else 5.0 * (attempt + 1)))):
                    return
            if st == 429 or st >= 500:
                continue                       # still throttled: unknown, tested when picked
            timeouts = timeouts + 1 if st == 0 else 0
            if timeouts >= 3:
                break                          # offline or provider down: stop instead of timing out 50 times
            if st == 0:
                continue                       # timeout: unknown, never marked broken
            with _working_lock:
                _working[_cache_key(api_key, m)] = _is_answering(st)
            done += 1
            gap = max(0.6, gap - 0.2)
            if stop.wait(gap):
                return
    finally:
        with _working_lock:
            ok = sum(1 for m in models if _working.get(_cache_key(api_key, m)) is True)
        logger.trace("MODEL", f"Background scan: {done} model(s) checked, {ok}/{len(models)} answer")


def is_unusable(status: int) -> bool:
    """True when the model is certainly unusable with this key (auth, not found, gone)."""
    return status in (401, 403, 404, 410)


def _probe_key(provider: str, api_key: str, sample_model: str, timeout_sec: int) -> Optional[ModelsResult]:
    if provider not in ("nvidia", "openrouter"):
        return None
    if provider == "nvidia":
        # /models is public on NVIDIA: it proves nothing about the key. Ask a model that is normally available.
        denied = 0
        for m in _NVIDIA_PROBE:
            st = test_model(provider, api_key, m)
            if st == 401:
                return _fail(401, _describe_failure(provider, 401, ""))
            if st == 403:
                denied += 1
                continue
            if 200 <= st < 300 or st in (400, 429):
                return None  # key accepted
        # 403 on every common model = the key's organisation lacks the "Public API Endpoints" permission.
        if denied == len(_NVIDIA_PROBE):
            return _fail(403, "NVIDIA refuses this key for every model, it has probably expired. "
                              "Create a new one on build.nvidia.com.")
        return None  # inconclusive: do not block
    t0 = time.monotonic()
    try:
        url = base_url(provider) + "/auth/key"
        headers: dict = {}
        apply_auth(headers, provider, api_key, url)
        _log_request("GET", url, headers)
        res = _http.get(url, headers=headers, timeout=timeout_sec)
        logger.trace("RES", f"Key check: HTTP {res.status_code} in {int((time.monotonic() - t0) * 1000)} ms\n"
                            f"{logger.clip(res.text, 500)}")
        if res.status_code == 401:
            return _fail(401, _describe_failure(provider, 401, res.text))
    except Exception as ex:
        logger.trace("ERROR", "Key check inconclusive: " + str(ex))
    return None
