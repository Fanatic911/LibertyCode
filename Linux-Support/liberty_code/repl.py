"""The interactive loop: slash commands, projects, settings, model / provider switching."""
from __future__ import annotations

import os
import shutil
import signal
import stat
import sys
import threading
from typing import Callable, Optional

from rich import box
from rich.markup import escape
from rich.padding import Padding
from rich.table import Table

from . import crypto, line_editor, loc, logger, notifier, paths, prompts, providers, settings as settings_mod, shell, system_prompt, ui
from .agent import Agent
from .models import CancelToken, Cancelled, PermissionMode
from .theme import Sym, Theme
from .workspace import Workspace

COMMANDS = ["/help", "/models", "/project", "/leave", "/cd", "/clear", "/undo", "/settings",
            "/prompt", "/provider", "/log", "/exit", "/mode", "/key", "/language"]


def _same_path(a: str, b: str) -> bool:
    return os.path.normcase(os.path.realpath(a)).rstrip("\\/") == os.path.normcase(os.path.realpath(b)).rstrip("\\/")


def _on_off(v: bool) -> str:
    return ui.M(Theme.GREEN, "true") if v else ui.dim("false")


def _parse_bool(v: Optional[str]) -> Optional[bool]:
    v = (v or "").strip().lower()
    if v in ("true", "on", "yes", "1", "oui"):
        return True
    if v in ("false", "off", "no", "0", "non"):
        return False
    return None


class Repl:
    def __init__(self, settings):
        self._s = settings
        self._api_key = ""
        self._agent: Agent = None  # type: ignore[assignment]
        self._turn: Optional[CancelToken] = None
        self._in_hub = False            # no project open: we are on /project
        self._scan_stop: Optional[threading.Event] = None
        self._scan_thread: Optional[threading.Thread] = None

    # ── Main loop ─────────────────────────────────────────────────────────────
    def run(self) -> None:
        self._api_key = crypto.load_api_key(self._s.Provider) or ""
        line_editor.load_history()

        self._new_session()
        self._log_session_info()
        self._verify_startup()

        while True:
            ui.status_line(self._s, self._agent.perm.mode, self._agent.ws.root, self._agent.context_tokens, self._in_hub)
            raw = line_editor.read_input(ui.prompt_fragments(), COMMANDS)
            if raw is None:
                break
            raw = raw.strip()
            if not raw:
                continue
            logger.trace("USER", "Input: " + logger.clip(raw, 500))
            try:
                if not self._handle(raw):
                    break
            except (Cancelled, KeyboardInterrupt):
                pass
            except Exception as ex:  # noqa: BLE001
                logger.exception("Unexpected error", ex)
                ui.error(str(ex))
            ui.blank()
        ui.line(f"  {ui.dim('Bye.')}")

    def _new_session(self, banner: bool = True) -> None:
        # No workspace (or the Projects folder itself) = the /project hub: nothing is opened yet.
        self._in_hub = (not self._s.Workspace.strip() or not os.path.isdir(self._s.Workspace)
                        or _same_path(self._s.Workspace, paths.PROJECTS_DIR))
        if self._in_hub:
            self._s.Workspace = ""
        root = paths.PROJECTS_DIR if self._in_hub else self._s.Workspace
        os.makedirs(root, exist_ok=True)
        self._agent = Agent(self._s, self._api_key, Workspace(root))
        if banner:
            ui.banner(self._s, self._agent.perm.mode, self._agent.ws.root, self._in_hub)
            if self._in_hub:
                self._show_projects()

    # ── Input handling ────────────────────────────────────────────────────────
    def _handle(self, raw: str) -> bool:
        if raw.lower() in ("exit", "quit"):
            return False
        raw = self._normalize_bare_command(raw)
        if self._in_hub and not raw.startswith("/"):
            raw = "/" + raw   # outside a project, everything typed is a command
        if raw.startswith("/"):
            return self._command(raw)
        if raw.startswith("!"):
            self._run_shell(raw[1:].strip())
            return True
        if not self._api_key.strip():
            ui.warn("No API key. Use /provider set api-key.")
            return True
        self._ask_agent(raw)
        return True

    @staticmethod
    def _normalize_bare_command(raw: str) -> str:
        """Lets the user type app commands without the leading slash ('project new Matcha', 'help', 'undo').
        Only unambiguous forms are accepted, so normal requests are never hijacked."""
        if raw.startswith("/") or raw.startswith("!"):
            return raw
        w = raw.split()
        if not w:
            return raw
        cmd = w[0].lower()
        second = w[1].lower() if len(w) > 1 else ""
        n = len(w)
        if cmd in ("help", "clear", "undo", "provider", "leave", "log", "key"):
            is_cmd = n == 1
        elif cmd == "settings":
            is_cmd = (n == 1
                      or (n in (2, 3) and second in ("notification", "notifications", "autoselect", "debug", "warn",
                                                     "mode", "security", "language"))
                      or (n >= 2 and second == "set"))
        elif cmd == "project":
            is_cmd = (n == 1
                      or (second in ("new", "open") and n >= 3)
                      or (second in ("delete", "remove") and n >= 2)
                      or (second == "list" and n == 2))
        elif cmd in ("models", "model"):
            is_cmd = n <= 2
        elif cmd == "cd":
            is_cmd = n >= 2
        elif cmd == "prompt":
            is_cmd = n == 1 or (n == 2 and second == "edit")
        else:
            is_cmd = False
        return "/" + raw if is_cmd else raw

    def _ask_agent(self, text: str) -> None:
        cancel = CancelToken()
        self._turn = cancel
        old = None
        try:
            old = signal.signal(signal.SIGINT, lambda *_: cancel.cancel())
        except ValueError:
            pass  # not the main thread
        try:
            with ui.EscWatcher(cancel):
                self._agent.run_turn(text, cancel)
        finally:
            self._turn = None
            if old is not None:
                try:
                    signal.signal(signal.SIGINT, old)
                except Exception:
                    pass

        if self._s.Notifications and not cancel.cancelled:
            project = os.path.basename(self._agent.ws.root.rstrip("\\/"))
            notifier.send("LibertyCode · " + project, self._agent.final_reply or "Stopped: your input is needed.")

    # ── Slash commands ────────────────────────────────────────────────────────
    def _command(self, raw: str) -> bool:
        parts = raw.split(None, 1)
        cmd = parts[0].lower()
        arg = parts[1].strip() if len(parts) > 1 else ""

        if cmd in ("/exit", "/quit"):
            return False
        elif cmd == "/help":
            self._show_help()
        elif cmd == "/clear":
            self._new_session()
        elif cmd in ("/models", "/model"):
            self._models(arg)
        elif cmd == "/mode":
            self._set_security(arg)
        elif cmd == "/key":
            self._models("key " + arg if arg else "key")
        elif cmd == "/language":
            self._settings_command("set language " + arg if arg else "")
        elif cmd == "/project":
            self._project_command(arg)
        elif cmd == "/leave":
            self._leave_project()
        elif cmd == "/cd":
            self._change_dir(arg)
        elif cmd == "/undo":
            self._undo_last()
        elif cmd == "/settings":
            self._settings_command(arg)
        elif cmd == "/prompt":
            if arg.lower() == "edit":
                system_prompt.edit()
            else:
                system_prompt.view()
        elif cmd == "/provider":
            self._change_provider(arg)
        elif cmd == "/log":
            lines = logger.tail()
            if not lines:
                ui.hint("The log is empty.")
            for l in lines:
                ui.line(f"  {ui.dim(l)}")
        else:
            ui.warn(f"Unknown command {cmd}. Type /help.")
        return True

    def _show_help(self) -> None:
        table = Table(box=box.ROUNDED if Sym.UNICODE else box.SQUARE, border_style=Theme.ORANGE, expand=False)
        table.add_column(ui.dim(loc.T("Command")), no_wrap=True)
        table.add_column(ui.dim(loc.T("Description")))
        rows = [
            ("/models [name | key [api-key]]", "Change model (searchable list) or API key, anywhere"),
            ("/settings set Security Strict/Mid/Off", "Permission level"),
            ("/undo", "Revert the file changes of the last request"),
            ("/project [list|new|open|delete]", "List, create, open or delete a project"),
            ("/leave", "Leave the project and go back to /project"),
            ("/cd <path>", "Use another folder as workspace"),
            ("/clear", "New conversation"),
            ("/provider [set <name> | set api-key <key>]", "List providers / switch provider / set its API key"),
            ("/settings [key value]", "Show settings, or set a value"),
            ("/prompt [edit]", "Extra instructions added to the system prompt"),
            ("/log", "Show the log"),
            ("/exit", "Quit"),
        ]
        ui.blank()
        for c, d in rows:
            table.add_row(ui.M(Theme.ORANGE, c), ui.dim(loc.T(d)))
        ui.console.print(Padding(table, (0, 0, 0, 2)))

    def _show_settings(self) -> None:
        g = Table.grid(padding=(0, 3, 0, 0))
        g.add_column(no_wrap=True)
        g.add_column()
        mode = self._agent.perm.mode
        g.add_row(ui.dim(loc.T("Provider:")), escape(providers.label(self._s.Provider)))
        g.add_row(ui.dim(loc.T("Model:")), ui.violet(self._s.Model))
        g.add_row(ui.dim(loc.T("Security:")), ui.M(ui.mode_color(mode), ui.security_name(mode)) + ui.dim("  (" + ui.security_hint(mode) + ")"))
        g.add_row(ui.dim(loc.T("Language:")), escape(loc.lang_name(self._s.Language)))
        g.add_row(ui.dim(loc.T("Workspace:")), ui.M(Theme.ORANGE, ui.HUB_LABEL) if self._in_hub else escape(ui.short(self._agent.ws.root)))
        g.add_row(ui.dim(loc.T("Autoselect:")), _on_off(self._s.AutoSelect))
        g.add_row(ui.dim(loc.T("Notification:")), _on_off(self._s.Notifications))
        g.add_row(ui.dim(loc.T("Debug:")), _on_off(logger.debug_enabled()))
        g.add_row(ui.dim(loc.T("Data:")), escape(ui.short(paths.DATA_DIR)))
        ui.blank()
        ui.console.print(Padding(g, (0, 0, 0, 2)))

    def _settings_command(self, arg: str) -> None:
        """/settings  ·  /settings set <key> <value>  ·  /settings set Security Strict/Mid/Off"""
        if not arg:
            self._show_settings()
            return
        p = arg.split()
        key_idx = 1 if p and p[0].lower() == "set" else 0
        if key_idx >= len(p):
            self._show_settings()
            return

        key = p[key_idx].lower()
        raw_val = p[key_idx + 1] if len(p) > key_idx + 1 else ""
        bool_val = _parse_bool(raw_val) if raw_val else None

        if key in ("notification", "notifications"):
            self._s.Notifications = bool_val if bool_val is not None else not self._s.Notifications
            settings_mod.save(self._s)
            ui.ok(f"Notification: {'true' if self._s.Notifications else 'false'}")
            if self._s.Notifications:
                notifier.send("LibertyCode", "Notifications are on.")
        elif key == "autoselect":
            self._s.AutoSelect = bool_val if bool_val is not None else not self._s.AutoSelect
            settings_mod.save(self._s)
            ui.ok(f"AutoSelect: {'true' if self._s.AutoSelect else 'false'}")
            ui.hint("LibertyCode will reopen where you left off." if self._s.AutoSelect else "LibertyCode will start on /project.")
        elif key == "debug":
            self._set_debug(bool_val if bool_val is not None else not logger.debug_enabled())
        elif key == "language":
            if len(raw_val) < 2:
                ui.warn("Usage: /settings set language fr")
                return
            self._s.Language = raw_val.lower()
            loc.lang = self._s.Language
            settings_mod.save(self._s)
            ui.ok(f"Language: {loc.lang_name(self._s.Language)}")
        elif key in ("security", "mode", "warn"):
            self._set_security(raw_val)
        else:
            ui.warn("Usage: /settings set <name> <value>  (debug true/false)")

    # ── Startup checks / models ───────────────────────────────────────────────
    def _verify_startup(self) -> None:
        """Checks the API key and the current model against the provider at startup (never blocks when offline)."""
        if not self._api_key.strip() or not self._s.Model.strip():
            return

        if self._s.Provider == "nvidia":
            # Test only the selected model now; the full scan of the others goes on in the background.
            with ui.Spinner("Checking API key and models"):
                st = providers.test_model("nvidia", self._api_key, self._s.Model)
            self._start_background_scan()
            if st == 401:
                ui.warn(f"{providers.label(self._s.Provider)} rejected your API key. Use /provider set api-key to enter a valid one.")
                return
            if providers.is_unusable(st):
                providers.mark_broken(self._api_key, self._s.Model)
                ui.warn(loc.T("This model is not usable with your key. Pick another one."))
                self._model("")
            elif st != 0:
                providers.mark_working(self._api_key, self._s.Model)
            return

        with ui.Spinner("Checking API key and models"):
            r = providers.get_models(self._s.Provider, self._api_key, timeout_sec=8)

        if r.auth_rejected:
            ui.warn(f"{providers.label(self._s.Provider)} rejected your API key. Use /provider set api-key to enter a valid one.")
            return
        if r.error is not None:
            ui.hint("Could not check the model list: " + r.error)
            return
        line_editor.model_names = list(r.models)
        if not r.verified or self._s.Model.lower() in (m.lower() for m in r.models):
            return

        ui.warn(f"The model '{self._s.Model}' does not exist on {providers.label(self._s.Provider)} (any more). Pick one from the real list:")
        self._model("", r)

    @staticmethod
    def _show_models_error(r: providers.ModelsResult) -> None:
        ui.error(r.error or "Could not fetch the models.")
        if r.auth_rejected:
            ui.hint("Use /provider set api-key to update the API key.")

    def _set_model(self, model: str) -> None:
        self._s.Model = model
        settings_mod.save(self._s)
        self._agent.reconfigure()
        logger.info(f"Model set to {model}")
        ui.ok(f"Model set to {model}")

    def _model_usable(self, model: str) -> bool:
        """NVIDIA lists models that its hosted API does not serve: test the chosen one before using it."""
        if self._s.Provider != "nvidia" or providers.is_known_working(self._api_key, model):
            return True
        with ui.Spinner(loc.T("Testing the model")):
            st = providers.test_model(self._s.Provider, self._api_key, model)
        if not providers.is_unusable(st):
            return True
        providers.mark_broken(self._api_key, model)
        ui.warn(loc.T("This model is not usable with your key. Pick another one."))
        return False

    def _start_background_scan(self, models: Optional[list] = None) -> None:
        """Silent and slow: tests the NVIDIA models one by one in the background (the current model is tested first)."""
        if self._scan_stop is not None:
            self._scan_stop.set()
        stop = self._scan_stop = threading.Event()
        key = self._api_key

        def work() -> None:
            ms = models if models is not None else providers.get_models("nvidia", key, timeout_sec=20).models
            providers.scan_slowly(key, ms, stop)

        self._scan_thread = threading.Thread(target=work, daemon=True)
        self._scan_thread.start()

    def _stop_scan(self) -> None:
        if self._scan_stop is not None:
            self._scan_stop.set()

    def _filter_nvidia(self, r: providers.ModelsResult) -> providers.ModelsResult:
        """NVIDIA lists models its hosted API does not serve: hide the ones already known broken, scan the rest
        in the background."""
        ok = [m for m in r.models if not providers.is_known_broken(self._api_key, m)]
        pending = sum(1 for m in ok if not providers.is_known_working(self._api_key, m))
        if pending > 0:
            running = (self._scan_thread is not None and self._scan_thread.is_alive()
                       and self._scan_stop is not None and not self._scan_stop.is_set())
            if not running:
                self._start_background_scan(r.models)
            ui.hint(f"{pending} models are still being checked in the background.")
        return r if not ok else providers.ModelsResult(models=ok, status=r.status, verified=True)

    def _models(self, arg: str) -> None:
        """/models  ·  /models <name>  ·  /models key [api-key]. Works anywhere, no project needed."""
        key = arg.lower() == "key" or arg.lower().startswith("key ")
        if not arg and self._s.Provider != "ollama":
            key = prompts.choose("Models", [False, True], lambda k: "Change API key" if k else "Change model")
        if not key:
            self._model(arg)
            return
        if self._s.Provider == "ollama":
            ui.hint("Ollama needs no API key.")
            return
        self._set_key(arg[4:].strip() if len(arg) > 3 else "")

    def _model(self, arg: str, preloaded: Optional[providers.ModelsResult] = None) -> None:
        if preloaded is not None:
            r = preloaded
        else:
            with ui.Spinner("Fetching models"):
                r = providers.get_models(self._s.Provider, self._api_key)

        if r.error is not None:
            self._show_models_error(r)
            # Typed name + provider unreachable (not a key problem): accept it, but say it is unchecked.
            if arg and not r.auth_rejected:
                ui.warn("The name could not be checked.")
                self._set_model(arg)
            return
        if not r.verified:
            ui.hint("This provider has no model list: showing the built-in one (not verified).")
        if self._s.Provider == "nvidia" and r.ok:
            r = self._filter_nvidia(r)
        line_editor.model_names = list(r.models)

        if arg:
            exact = next((m for m in r.models if m.lower() == arg.lower()), None)
            if exact is None and r.verified:
                near = [m for m in r.models if arg.lower() in m.lower()][:8]
                if len(near) == 1:
                    exact = near[0]   # autocomplete: a single partial match is the model
                else:
                    ui.warn(f"'{arg}' is not a model available on {providers.label(self._s.Provider)}.")
                    ui.hint("Did you mean: " + ", ".join(near) if near else "Type /models to see the real list.")
                    return
            if not self._model_usable(exact or arg):
                return
            self._set_model(exact or arg)
            return

        while True:
            shown = [m for m in r.models if not providers.is_known_broken(self._api_key, m)] or r.models
            pick = prompts.choose(f"Model ({len(shown)} available)", shown,
                                  lambda m: m + "  (current)" if m == self._s.Model else m, 14, search=True)
            pick = pick.replace("  (current)", "")
            if not self._model_usable(pick):
                continue
            self._set_model(pick)
            break

    # ── Debug mode ────────────────────────────────────────────────────────────
    def _log_session_info(self) -> None:
        if not logger.debug_enabled():
            return
        logger.trace("DEBUG", f"Settings: provider={self._s.Provider}, model={self._s.Model}, "
                              f"security={ui.security_name(self._s.Mode)}, language={self._s.Language}, key={logger.mask(self._api_key)}")
        logger.trace("DEBUG", f"Workspace: {'(none - /project hub)' if self._in_hub else self._agent.ws.root}")

    def _set_debug(self, on: bool) -> None:
        self._s.Debug = on
        settings_mod.save(self._s)
        if on:
            opened = logger.start_debug()
            self._log_session_info()
            if opened:
                ui.ok("Debug mode on: live window opened")
            else:
                ui.warn("Debug mode on, but the window could not be opened.")
                ui.hint("Follow the log yourself: " + paths.DEBUG_FILE)
        else:
            logger.stop_debug()
            ui.ok("Debug mode off")

    def _set_security(self, arg: str) -> None:
        table = {"strict": PermissionMode.ASK, "ask": PermissionMode.ASK, "mid": PermissionMode.EDITS,
                 "edits": PermissionMode.EDITS, "off": PermissionMode.AUTO, "auto": PermissionMode.AUTO}
        m = table.get(arg.lower())
        if m is None:
            if arg:
                ui.warn("Security values: Strict, Mid, Off.  Usage: /settings set Security Strict/Mid/Off")
                return
            m = prompts.choose("Security", [PermissionMode.ASK, PermissionMode.EDITS, PermissionMode.AUTO],
                               lambda x: ui.security_name(x) + "  —  " + ui.security_hint(x))
        self._s.Mode = m
        self._agent.perm.mode = m
        settings_mod.save(self._s)
        ui.ok(f"Security: {ui.security_name(m)}")

    def _undo_last(self) -> None:
        report = self._agent.undo.undo_last()
        if not report:
            ui.hint("Nothing to undo.")
            return
        for path, action in report:
            color = Theme.RED if action.startswith("failed") else Theme.GREEN
            ui.line(f"  {ui.M(color, Sym.UNDO)} {ui.dim(action)} {escape(self._agent.ws.rel(path))}")

    # ── API key / provider ────────────────────────────────────────────────────
    def _set_key(self, inline: str = "", pick_model: bool = True) -> None:
        if inline:
            line_editor.secret_issue = None
            key: Optional[str] = inline
        else:
            key = line_editor.read([("", "  "), ("bold", "New API key"), ("", " "), ("fg:" + Theme.ORANGE, "›"), ("", " ")], secret=True)
        if not key or not key.strip():
            ui.hint("Cancelled.")
            return
        if line_editor.secret_issue == "long":
            ui.warn("Key too long. Paste only the API key.")
            return
        if line_editor.secret_issue == "invalid":
            ui.warn("Invalid key. Use only the characters of the key (no spaces, accents or symbols).")
            return
        key = crypto.clean_key(key)

        with ui.Spinner("Checking API key"):
            r = providers.get_models(self._s.Provider, key, probe_key=True)

        if r.auth_rejected:
            ui.error(f"{providers.label(self._s.Provider)} rejected this key. Nothing was saved.")
            return
        if r.error is not None:
            ui.warn(f"Could not check the key ({r.error}). Saved anyway.")

        crypto.save_api_key(key, self._s.Provider)
        self._api_key = key
        self._agent.reconfigure(self._api_key)
        if self._s.Provider == "nvidia":
            self._start_background_scan()
        logger.info("API key updated: " + logger.mask(key))
        ui.ok("API key valid and saved (encrypted)" if r.error is None else "API key saved (encrypted)")

        if pick_model and r.verified and self._s.Model.lower() not in (m.lower() for m in r.models):
            ui.warn(f"The model '{self._s.Model}' is not available with this key. Pick one:")
            self._model("", r)

    def _change_provider(self, arg: str = "") -> None:
        parts = arg.split(None, 1)
        if not parts or parts[0].lower() != "set":
            try:
                pick = prompts.choose("AI provider", list(providers.ALL.keys()),
                                      lambda k: providers.ALL[k].label + ("  (current)" if k == self._s.Provider else ""))
            except Cancelled:
                return
            self._switch_provider(pick)
            return

        rest = parts[1].strip() if len(parts) > 1 else ""
        if rest.lower() == "api-key" or rest.lower().startswith("api-key "):
            if self._s.Provider == "ollama":
                ui.hint("Ollama needs no API key.")
                return
            self._set_key(rest[8:].strip() if len(rest) > 7 else "")
            return

        provider = next((k for k in providers.ALL if k.lower() == rest.lower()), None) \
            or next((k for k, p in providers.ALL.items() if p.label.lower() == rest.lower()), None)
        if provider is None:
            ui.warn("Usage: /provider set <name>" if not rest else f"Unknown provider '{rest}'. Type /provider to see the list.")
            return
        self._switch_provider(provider)

    def _switch_provider(self, provider: str) -> None:
        if provider == self._s.Provider:
            ui.hint(f"Already using {providers.label(provider)}.")
            return

        prev_provider, prev_model, prev_key = self._s.Provider, self._s.Model, self._api_key
        self._s.Provider = provider
        self._stop_scan()
        saved = "ollama" if provider == "ollama" else crypto.load_api_key(provider)
        try:
            if saved is not None and provider != "ollama":
                use_old = prompts.choose("An old API key was found. Use it?", [True, False], lambda y: loc.T("Yes" if y else "No"))
                if not use_old:
                    saved = None
            if saved is not None:
                self._api_key = saved
                self._agent.reconfigure(self._api_key)
            else:
                self._set_key(pick_model=False)
                saved = crypto.load_api_key(provider)
                if saved is None:
                    raise Cancelled()
            self._model("")
        except Cancelled:
            self._s.Provider, self._s.Model, self._api_key = prev_provider, prev_model, prev_key
            self._agent.reconfigure(self._api_key)
            settings_mod.save(self._s)
            ui.hint(f"Cancelled: still on {providers.label(prev_provider)}.")
            return
        settings_mod.save(self._s)
        logger.info(f"Provider set to {provider}")
        self._new_session()

    # ── Workspace / projects ──────────────────────────────────────────────────
    def _switch_workspace(self, path: str) -> None:
        self._s.Workspace = os.path.abspath(path)
        settings_mod.save(self._s)
        self._new_session()

    def _change_dir(self, arg: str) -> None:
        if not arg:
            ui.hint("Usage: /cd <path>")
            return
        arg = arg.strip('"')
        if arg.startswith("~"):
            arg = os.path.expanduser("~") + arg[1:]
        full = os.path.abspath(arg if os.path.isabs(arg) else os.path.join(self._agent.ws.root, arg))
        if not os.path.isdir(full):
            ui.warn("Folder not found: " + full)
            return
        self._switch_workspace(full)

    @staticmethod
    def _list_projects() -> list:
        if not os.path.isdir(paths.PROJECTS_DIR):
            return []
        return sorted((d for d in os.listdir(paths.PROJECTS_DIR) if os.path.isdir(os.path.join(paths.PROJECTS_DIR, d))), key=str.lower)

    def _is_current(self, name: str) -> bool:
        return not self._in_hub and _same_path(os.path.join(paths.PROJECTS_DIR, name), self._agent.ws.root)

    def _show_projects(self) -> None:
        """Framed table of the projects: used on startup and by /project list."""
        import datetime
        dirs = self._list_projects()
        ui.blank()
        if not dirs:
            ui.hint("No projects yet.")
            return
        table = Table(box=box.ROUNDED if Sym.UNICODE else box.SQUARE, border_style=Theme.ORANGE, expand=False)
        table.add_column(ui.dim(loc.T("Project")), no_wrap=True)
        table.add_column(ui.dim(loc.T("Created")), no_wrap=True)
        table.add_column(ui.dim(loc.T("Modified")), no_wrap=True)
        table.add_column(ui.dim(loc.T("Files")), no_wrap=True, justify="right")
        for d in dirs:
            p = os.path.join(paths.PROJECTS_DIR, d)
            st = os.stat(p)
            created = getattr(st, "st_birthtime", st.st_ctime)
            label = f"{ui.M(Theme.ORANGE, Sym.POINTER)} {ui.bold(d)}" if self._is_current(d) else f" {escape(d)}"
            files = 0
            try:
                for root, dnames, fnames in os.walk(p):
                    if ".git" in dnames:
                        dnames.remove(".git")
                    files += len(fnames)
            except OSError:
                pass
            table.add_row(label,
                          ui.dim(datetime.datetime.fromtimestamp(created).strftime("%Y-%m-%d")),
                          ui.dim(datetime.datetime.fromtimestamp(st.st_mtime).strftime("%Y-%m-%d %H:%M")),
                          ui.dim(str(files)))
        ui.console.print(table)

    def _create_project(self, name: str) -> None:
        if any(c in name for c in '<>:"/\\|?*') or any(ord(c) < 32 for c in name):
            ui.warn("That name has invalid characters.")
            return
        path = os.path.join(paths.PROJECTS_DIR, name)
        if os.path.isdir(path):
            ui.warn(f"Project '{name}' already exists.")
            return
        os.makedirs(path)
        with open(os.path.join(path, "README.md"), "w", encoding="utf-8") as f:
            f.write(f"# {name}\n\nCreated with LibertyCode.\n")
        logger.info("Project created: " + name)
        self._switch_workspace(path)

    def _leave_project(self) -> None:
        if self._in_hub:
            ui.hint("You are already on /project.")
            return
        self._s.Workspace = ""
        settings_mod.save(self._s)
        self._new_session()

    def _delete_project(self, name: str) -> None:
        dirs = self._list_projects()
        if not dirs:
            ui.hint("No projects to delete.")
            return
        if not name:
            name = prompts.choose("Delete which project?", dirs, lambda d: d)
        else:
            match = next((d for d in dirs if d.lower() == name.lower()), None)
            if match is None:
                ui.warn(f"Project '{name}' not found. Try /project list.")
                return
            name = match

        delete = prompts.choose(f"Delete '{name}' and everything inside it? This cannot be undone.", [True, False],
                                lambda v: loc.T("Yes, delete it" if v else "No, keep it"))
        if not delete:
            ui.hint("Cancelled.")
            return

        path = os.path.join(paths.PROJECTS_DIR, name)
        was_current = not self._in_hub and _same_path(path, self._agent.ws.root)
        if was_current:
            # Step out of the project before deleting it.
            self._s.Workspace = ""
            settings_mod.save(self._s)
            self._new_session(banner=False)

        try:
            self._force_delete(path)
            logger.info("Project deleted: " + name)
        except Exception as ex:  # noqa: BLE001
            ui.error(f"Could not delete '{name}': {ex}")
            if was_current:
                self._new_session()
            return

        if was_current:
            self._new_session()
        ui.ok(f"Project '{name}' deleted")

    @staticmethod
    def _force_delete(path: str) -> None:
        """Deletes a folder even when it holds read-only files (typical of .git on Windows)."""
        isjunction = getattr(os.path, "isjunction", lambda p: False)
        if os.path.islink(path) or isjunction(path):
            os.rmdir(path) if os.name == "nt" else os.unlink(path)   # a link: remove the link only
            return

        def handler(func, p, *_):
            if os.name == "nt":
                os.chmod(p, stat.S_IWRITE)
            else:
                # On POSIX, removing an entry needs write+search rights on its *parent* folder.
                for target in (os.path.dirname(p), p):
                    try:
                        os.chmod(target, stat.S_IRWXU)
                    except OSError:
                        pass
            func(p)

        if sys.version_info >= (3, 12):
            shutil.rmtree(path, onexc=handler)
        else:
            shutil.rmtree(path, onerror=handler)

    def _project_command(self, arg: str) -> None:
        parts = arg.split(None, 1)
        sub = parts[0].lower() if parts else "list"
        name = parts[1].strip() if len(parts) > 1 else ""

        if sub == "list":
            self._show_projects()
        elif sub == "new":
            if not name:
                ui.hint("Usage: /project new <name>")
                return
            self._create_project(name)
        elif sub == "open":
            if not name:
                ui.hint("Usage: /project open <name>")
                return
            path = os.path.join(paths.PROJECTS_DIR, name)
            if not os.path.isdir(path):
                ui.warn(f"Project '{name}' not found. Try /project list.")
                return
            self._switch_workspace(path)
        elif sub in ("delete", "remove"):
            self._delete_project(name)
        else:
            ui.hint("Usage: /project [list | new <name> | open <name> | delete <name>]")

    # ── !command ──────────────────────────────────────────────────────────────
    def _run_shell(self, command: str) -> None:
        if not command:
            ui.hint("Usage: !<command>")
            return
        ui.tool_header("Run", command)

        cancel = CancelToken()
        self._turn = cancel
        old = None
        try:
            old = signal.signal(signal.SIGINT, lambda *_: cancel.cancel())
        except ValueError:
            pass
        try:
            with ui.EscWatcher(cancel):
                try:
                    with ui.Spinner("Running"):
                        code, output, timed_out = shell.run(command, self._agent.ws.root, 300, cancel)
                except Cancelled:
                    ui.tool_line(ui.dim(loc.T("Interrupted")))
                    return
            out_lines = [l.rstrip("\r") for l in output.split("\n") if l.rstrip("\r")]
            keep = 3 if code == 0 else 8
            if len(out_lines) > keep:
                ui.line(f"     {ui.dim(loc.T(f'… +{len(out_lines) - keep} lines'))}")
            for l in out_lines[-keep:]:
                room = ui.width() - 7
                ui.line(f"     {ui.dim(l[:max(1, room - 1)] + '…' if len(l) > room else l)}")
            if timed_out:
                ui.tool_line(ui.M(Theme.RED, "Timed out"))
            elif code == 0:
                ui.tool_line(f"{ui.M(Theme.GREEN, Sym.CHECK)} {ui.dim('exit 0')}")
            else:
                ui.tool_line(f"{ui.M(Theme.RED, Sym.CROSS)} {ui.M(Theme.RED, 'exit ' + str(code))}")
        finally:
            self._turn = None
            if old is not None:
                try:
                    signal.signal(signal.SIGINT, old)
                except Exception:
                    pass
