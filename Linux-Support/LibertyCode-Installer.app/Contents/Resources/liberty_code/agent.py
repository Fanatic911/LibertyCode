"""The agent loop: stream the model, run its tool calls, feed results back, repeat."""
from __future__ import annotations

import json
from typing import Optional

from . import llm_client, loc, logger, system_prompt, text_tool_calls, tools, ui
from .models import (CancelToken, Cancelled, Decision, LlmException, Msg, PermissionMode, Plan, ToolCall,
                     ToolError, ToolKind, ToolOutput)
from .permissions import Permissions
from .theme import Sym, Theme
from .workspace import UndoStack, Workspace

MAX_STEPS = 30
MAX_MALFORMED_RETRIES = 2


def _is_malformed_tool_call(msg: str) -> bool:
    m = msg.lower()
    return "failed to call a function" in m or "tool_use_failed" in m or "failed_generation" in m


def _mentions_tools(msg: str) -> bool:
    m = msg.lower()
    return "tool" in m or "function" in m


class Agent:
    def __init__(self, s, api_key: str, ws: Workspace):
        self._s = s
        self._api_key = api_key
        self._history: list = []
        self._tools_off = False
        self.ws = ws
        self.perm = Permissions(s.Mode)
        self.undo = UndoStack()
        self.context_tokens = 0
        # The model's final answer of the last turn; None when the turn was interrupted or stopped early.
        self.final_reply: Optional[str] = None

    def reconfigure(self, api_key: Optional[str] = None) -> None:
        """Call after the model or the API key changed: keeps the conversation, re-enables tools."""
        if api_key is not None:
            self._api_key = api_key
        self._tools_off = False

    def run_turn(self, user_input: str, cancel: CancelToken) -> None:
        self.undo.begin_turn()
        self.final_reply = None
        logger.trace("USER", f"Turn start - workspace={self.ws.root}, security={ui.security_name(self.perm.mode)}\n"
                             f"{logger.clip(user_input, 1500)}")
        user_msg = Msg(role="user", text=user_input)
        self._history.append(user_msg)
        progressed = False       # True once the model answered at least once this turn
        error_streak = 0         # consecutive failed tool calls: small models can spiral, so we cut it short
        seen: dict = {}          # identical calls repeated in this turn
        malformed = 0            # the provider rejected a badly formed tool call (small models do this)

        def drop_user() -> None:
            if not progressed and user_msg in self._history:
                self._history.remove(user_msg)

        step = 0
        while step < MAX_STEPS:
            self._trim_context()
            system = system_prompt.build(self._s, self.ws, not self._tools_off)
            logger.trace("STEP", f"Step {step + 1}/{MAX_STEPS}: calling {self._s.Provider}/{self._s.Model} - "
                                 f"{len(self._history)} message(s) in history, system prompt {len(system)} chars, "
                                 f"tools {'OFF' if self._tools_off else 'on'}, last known context {self.context_tokens} tokens")
            md = ui.MarkdownStream()
            leak = text_tool_calls.LeakFilter()
            spinner = ui.Spinner("Thinking")
            first = [True]

            def on_text(t: str) -> None:
                visible = leak.push(t)
                if not visible:
                    return
                if first[0]:
                    spinner.stop()
                    ui.blank()
                    first[0] = False
                md.feed(visible)

            try:
                result = llm_client.stream(self._s, self._api_key, system, self._history,
                                           None if self._tools_off else tools.SPECS, on_text, cancel)
            except Cancelled:
                spinner.stop(); md.flush()
                ui.tool_line(ui.dim(loc.T("Interrupted")))
                logger.trace("STEP", "Turn interrupted by the user (Esc / Ctrl+C) while waiting for the model")
                drop_user()
                return
            except LlmException as ex:
                spinner.stop(); md.flush()
                if ex.status == 400 and _is_malformed_tool_call(str(ex)) and malformed < MAX_MALFORMED_RETRIES:
                    # Not "tools unsupported": the model just produced an invalid call. Try again instead of giving up.
                    malformed += 1
                    logger.warn(f"Malformed tool call, retrying ({malformed}/{MAX_MALFORMED_RETRIES}): {ex}")
                    continue
                if (ex.status == 400 and not self._tools_off and not _is_malformed_tool_call(str(ex))
                        and _mentions_tools(str(ex))):
                    self._tools_off = True
                    logger.trace("STEP", "Provider refused the tools: switching to chat only for this model")
                    ui.warn("This model doesn't support tools: chat only. Pick another with /models for file editing.")
                    continue
                self._show_error(ex)
                drop_user()
                return
            finally:
                spinner.stop()
            # Some models print tool calls as text: hide them and run them as real calls.
            clean, text_calls = text_tool_calls.extract(result.text)
            rest = leak.rest(clean)
            if rest:
                if first[0]:
                    ui.blank()
                    first[0] = False
                md.feed(rest)
            md.flush()
            if not result.calls:
                result.calls.extend(text_calls)
            result.text = clean
            progressed = True

            logger.trace("STEP", f"Model answered: {len(result.text)} chars, {len(result.calls)} tool call(s), "
                                 f"tokens in={result.in_tokens} out={result.out_tokens}")
            if result.in_tokens > 0:
                self.context_tokens = result.in_tokens
            self._history.append(Msg(role="assistant", text=result.text, calls=result.calls))
            if not result.calls:
                self.final_reply = result.text
                logger.trace("STEP", f"Turn finished: final answer ({len(result.text)} chars), {step + 1} step(s)")
                return

            for i, call in enumerate(result.calls):
                try:
                    output = self._execute(call, cancel)
                except Cancelled:
                    ui.tool_line(ui.dim(loc.T("Interrupted")))
                    logger.trace("STEP", f"Turn interrupted during tool '{call.name}'")
                    for c in result.calls[i:]:
                        self._history.append(Msg(role="tool", call_id=c.id, text="Cancelled by the user.", is_error=True))
                    return
                key = call.name + json.dumps(call.args, ensure_ascii=False, separators=(",", ":"), sort_keys=True)
                seen[key] = seen.get(key, 0) + 1
                error_streak = error_streak + 1 if output.is_error else 0

                for_model = output.for_model
                if seen[key] >= (2 if output.is_error else 3):
                    for_model += (f"\n[This exact call was already made {seen[key]} times this turn. Do not repeat it: "
                                  f"change approach, or stop and tell the user what is blocking you.]")
                self._history.append(Msg(role="tool", call_id=call.id, text=for_model, is_error=output.is_error))

            if error_streak >= 5:
                ui.warn("Several tool calls failed in a row, so I stopped. Tell me what to do next, or try /models with a stronger model.")
                logger.trace("STEP", "Turn stopped: 5 failed tool calls in a row")
                return
            step += 1

        logger.trace("STEP", f"Turn stopped: reached the {MAX_STEPS}-step limit")
        ui.warn(f"Stopped after {MAX_STEPS} steps. Send a message to continue.")

    # ── Tool execution ────────────────────────────────────────────────────────
    def _execute(self, call: ToolCall, cancel: CancelToken) -> ToolOutput:
        name, arg = tools.describe(call.name, call.args)
        logger.trace("TOOL", f"Model requests tool '{call.name}' (id {call.id})\n"
                             f"{logger.clip(json.dumps(call.args, ensure_ascii=False), 1500)}")
        ui.blank()
        ui.tool_header(name, arg)

        try:
            plan = tools.prepare(self.ws, self.undo, call.name, call.args)
        except ToolError as e:
            ui.tool_error(str(e))
            logger.trace("TOOL", f"'{call.name}' rejected before running: {e}")
            return ToolOutput(for_model="Error: " + str(e) + ("\n" + e.model_hint if e.model_hint else ""), is_error=True)
        except Exception as e:
            ui.tool_error(str(e))
            logger.exception(f"Tool '{call.name}' failed to prepare", e)
            return ToolOutput(for_model="Error: " + str(e), is_error=True)

        if plan.headline:
            ui.tool_line(plan.headline)
        if plan.preview:
            ui.lines(plan.preview)

        needs_ask = self.perm.needs(plan)
        logger.trace("TOOL", f"Prepared '{call.name}': kind={plan.kind.name}, dangerous={plan.dangerous}, "
                             f"asks the user={needs_ask} (security {ui.security_name(self.perm.mode)})")
        if needs_ask:
            decision, note = self.perm.ask(plan)
            logger.trace("TOOL", f"User decision: {decision.name}{' - ' + note if note else ''}")
            if decision == Decision.NO:
                ui.tool_line(f"{ui.M(Theme.RED, Sym.CROSS)} {ui.dim(loc.T('Declined'))}{ui.dim(' · ' + note) if note else ''}")
                return ToolOutput(is_error=True, for_model="The user declined this action." +
                                  (f" Their instruction: {note}" if note else " Do not retry it; ask what they want instead."))
            if decision == Decision.ALWAYS:
                self.perm.remember(plan)

        spin = ui.Spinner("Running") if plan.kind == ToolKind.COMMAND else None
        try:
            try:
                o = plan.run(cancel)
            except Cancelled:
                raise
            except ToolError as e:
                o = ToolOutput(is_error=True, for_model="Error: " + str(e), summary=ui.M(Theme.RED, Sym.CROSS + " " + str(e)))
            except Exception as e:
                logger.exception(f"Tool '{call.name}' crashed", e)
                o = ToolOutput(is_error=True, for_model="Error: " + str(e), summary=ui.M(Theme.RED, Sym.CROSS + " " + str(e)))
        finally:
            if spin:
                spin.stop()

        if o.summary:
            ui.tool_line(o.summary)
        if o.details:
            ui.lines(o.details)
        logger.info(f"{call.name} {arg}")
        logger.trace("TOOL", f"Result of '{call.name}': {'ERROR' if o.is_error else 'ok'}\n{logger.clip(o.for_model, 2000)}")
        return o

    @staticmethod
    def _show_error(ex: LlmException) -> None:
        logger.error(f"HTTP {ex.status}: {ex}")
        if ex.status in (401, 403):
            msg = "Authentication failed. Check your API key with /provider set api-key."
        elif ex.status == 404:
            msg = "Model not found, or no access to it. Pick another with /models."
        elif ex.status == 400 and _is_malformed_tool_call(str(ex)):
            msg = "The model keeps producing invalid tool calls. Try again, rephrase, or pick a stronger model with /models."
        elif ex.status == 429:
            msg = "Rate limit reached. Wait a moment and try again."
        else:
            msg = str(ex)
        ui.blank()
        ui.error(msg)
        if ex.status not in (401, 403, 404, 429) and ex.status != 0:
            ui.hint(f"HTTP {ex.status}")

    def _trim_context(self) -> None:
        """Keeps the context bounded by shrinking old tool outputs."""
        total = sum(len(m.text) for m in self._history)
        if total < 320_000:
            return
        i = 0
        while i < len(self._history) - 8 and total > 240_000:
            m = self._history[i]
            i += 1
            if m.role != "tool" or len(m.text) < 600:
                continue
            total -= len(m.text)
            m.text = "[old tool output removed to save context]"
            total += len(m.text)
