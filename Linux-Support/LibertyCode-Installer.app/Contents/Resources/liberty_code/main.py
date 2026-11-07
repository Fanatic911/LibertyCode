"""Entry point: python -m liberty_code [folder] [--debug]"""
from __future__ import annotations

import atexit
import os
import sys
import threading

from . import __version__


def main(argv=None) -> None:
    args = list(sys.argv[1:] if argv is None else argv)

    # Internal: the debug window is this same program started with --debug-viewer (see debug_viewer.py).
    if len(args) >= 2 and args[0] == "--debug-viewer":
        parent_pid = 0
        for i in range(2, len(args) - 1):
            if args[i] == "--pid":
                try:
                    parent_pid = int(args[i + 1])
                except ValueError:
                    pass
        from . import debug_viewer
        debug_viewer.run(args[1], parent_pid)
        return

    if any(a in ("--version", "-V") for a in args):
        print(f"LibertyCode {__version__}")
        return
    if any(a in ("--help", "-h") for a in args):
        print("Usage: python -m liberty_code [folder] [--debug]\n"
              "  folder   use this folder as workspace (e.g. '.')\n"
              "  --debug  open the live debug window")
        return

    from . import crypto, debug_viewer, logger, paths, setup_wizard, ui
    from . import settings as settings_mod
    from .models import Cancelled
    from .providers import ALL
    from .repl import Repl

    ui.init()
    paths.ensure_directories()

    def _unhandled(exc_type, exc, tb):
        if issubclass(exc_type, KeyboardInterrupt):
            return
        logger.exception("Unhandled exception", exc)
        sys.__excepthook__(exc_type, exc, tb)

    sys.excepthook = _unhandled
    if hasattr(threading, "excepthook"):
        threading.excepthook = lambda a: logger.exception("Unhandled thread exception", a.exc_value)
    atexit.register(lambda: (logger.shutdown(), debug_viewer.close_window()))

    s = settings_mod.load()

    # `--debug` (or LIBERTY_DEBUG=1, or /settings set debug true) opens the live debug window.
    debug_flag = "--debug" in args or os.environ.get("LIBERTY_DEBUG", "").lower() in ("1", "true")
    args = [a for a in args if a != "--debug"]
    if s.Debug or debug_flag:
        logger.start_debug()

    # `python -m liberty_code .` or `... <folder>` opens that folder as the workspace.
    if args and os.path.isdir(args[0]):
        s.Workspace = os.path.abspath(args[0])
        settings_mod.save(s)
    elif not s.AutoSelect:
        # Default: start on /project. (In memory only; the last folder is kept for when AutoSelect is turned on.)
        s.Workspace = ""

    crypto.migrate_legacy(s.Provider)
    needs_setup = (not s.Provider.strip() or not s.Model.strip() or s.Provider not in ALL
                   or crypto.load_api_key(s.Provider) is None)

    try:
        if needs_setup:
            s = setup_wizard.run(s)
        Repl(s).run()
    except (Cancelled, KeyboardInterrupt, EOFError):
        pass   # setup cancelled with ctrl+c / ctrl+d
