"""First-run wizard: provider -> key -> model -> permission mode."""
from __future__ import annotations

import time

from . import crypto, line_editor, loc, logger, prompts, providers, settings as settings_mod, ui
from .models import Cancelled, PermissionMode
from .theme import Theme


def run(s, first_run: bool = True):
    """Updates and returns the given settings. Raises Cancelled if the user aborts."""
    ui.clear_screen()
    if first_run:
        ui.big_logo()
        ui.line(f"  [bold {Theme.ORANGE}]Welcome to Liberty[/][bold {Theme.VIOLET}]Code[/]")
    else:
        ui.line(f"  [bold {Theme.ORANGE}]Change provider[/]")
    ui.blank()

    # 1 - provider (Ollama needs no API key)
    keys = list(providers.ALL.keys())
    provider = prompts.choose("AI provider", keys, lambda k: providers.ALL[k].label)

    # 2 - API key (Ollama is local: no key needed)
    key = ""
    if provider == "ollama":
        crypto.save_api_key("ollama", "ollama")
        key = "ollama"
        with ui.Spinner("Connecting to Ollama"):
            res = providers.get_models(provider, key)
        if not res.ok:
            ui.warn(res.error or "Ollama did not respond. Make sure it is running (ollama serve).")
            res = providers.ModelsResult(models=list(providers.STATIC_MODELS["ollama"]), verified=False)
    else:
        prompt = [("", "  "), ("bold", "API key"), ("", " "), ("fg:" + Theme.ORANGE, "›"), ("", " ")]
        while True:
            typed = line_editor.read(prompt, secret=True)
            if typed is None:
                raise Cancelled()
            if not typed.strip():
                ui.warn("The key cannot be empty.")
                continue
            if line_editor.secret_issue == "long":
                ui.warn("Key too long. Paste only the API key.")
                continue
            if line_editor.secret_issue == "invalid":
                ui.warn("Invalid key. Use only the characters of the key (no spaces, accents or symbols).")
                continue
            key = crypto.clean_key(typed)

            with ui.Spinner("Checking the key and fetching the models"):
                res = providers.get_models(provider, key, probe_key=True)

            if res.ok:
                break
            if res.auth_rejected:
                ui.warn(res.error if res.status == 403 and res.error else f"{providers.label(provider)} rejected this key. Check it and try again.")
                continue

            ui.warn(res.error or "Could not fetch the models.")
            nxt = prompts.choose("What now?", ["retry", "other"],
                                 lambda a: "Try again with the same key" if a == "retry" else "Enter another key")
            if nxt == "retry":
                with ui.Spinner("Checking the key and fetching the models"):
                    res = providers.get_models(provider, key, probe_key=True)
                if res.ok:
                    break
                ui.warn(res.error or "Still no answer.")
        crypto.save_api_key(key, provider)
    logger.info(f"API key accepted by {provider}: {logger.mask(key)}")
    if not res.verified:
        ui.hint("This provider has no model list: showing the built-in one (not verified).")

    # 3 - model (only models the provider really offers)
    model = prompts.choose(f"Model ({len(res.models)} available)", res.models, lambda m: m, page_size=14, search=True)

    # 4 - permissions
    modes = [PermissionMode.ASK, PermissionMode.EDITS, PermissionMode.AUTO]
    mode = prompts.choose("Security", modes, lambda m: ui.security_name(m) + "  —  " + ui.security_hint(m))

    s.Provider = provider
    s.Model = model
    s.Mode = mode
    if first_run:
        s.AutoSelect = False
        s.Notifications = False
        s.Debug = False
    settings_mod.save(s)
    logger.info(f"Setup completed: {provider}/{model}")

    if first_run:
        # Key accepted: wipe the terminal, thank the user, then the main screen appears.
        ui.clear_screen()
        time.sleep(0.2)
        ui.blank()
        ui.line(f"  {ui.violet(loc.T('Thanks for using LibertyCode :)'))}")
        time.sleep(0.5)
    else:
        ui.blank()
        ui.ok("Ready")
    return s
