#!/usr/bin/env python3
"""
wake Python tracer — records execution of a target program using sys.monitoring
and writes JSONL events to a file.

Usage:
    python python_tracer.py --output trace.jsonl --filter-prefix /path/to/target \\
        -- target.py [target-args...]

Events written: call, return, exception, output.
Requires Python 3.12+.
"""
import argparse
import inspect
import json
import os
import runpy
import sys
import traceback
import types
from typing import Any

# ---- Safe repr ------------------------------------------------------------

def safe_repr(value: Any, max_len: int = 200) -> str:
    """Best-effort repr. Never raise."""
    try:
        r = repr(value)
    except Exception as e:
        return f"<repr-error: {type(e).__name__}>"
    if len(r) > max_len:
        r = r[:max_len] + f"... (+{len(r) - max_len} chars)"
    return r


def snapshot_locals(frame, max_vars: int = 50) -> dict[str, str]:
    """Capture a best-effort snapshot of frame locals."""
    try:
        loc = frame.f_locals
    except Exception:
        return {}
    result: dict[str, str] = {}
    for i, (name, value) in enumerate(loc.items()):
        if i >= max_vars:
            result["..."] = f"(+{len(loc) - max_vars} more)"
            break
        if name.startswith("__") and name not in ("__name__", "__file__"):
            continue
        result[name] = safe_repr(value)
    return result


# Types to exclude from globals snapshot (they're typically modules/functions
# imported at top of file, uninteresting for bug analysis).
_GLOBAL_EXCLUDE_TYPES = (types.ModuleType, types.FunctionType, types.BuiltinFunctionType,
                        type,  # classes
                        types.MethodType)


def snapshot_globals(frame, max_vars: int = 30) -> dict[str, str]:
    """
    Capture a filtered snapshot of module globals.

    Skips: dunder names, imports (modules), function/class definitions, callables.
    Keeps: values (ints, strings, lists, dicts, custom instances).
    """
    try:
        g = frame.f_globals
    except Exception:
        return {}
    result: dict[str, str] = {}
    count = 0
    for name, value in g.items():
        if name.startswith("_"):
            continue
        if isinstance(value, _GLOBAL_EXCLUDE_TYPES):
            continue
        if callable(value) and not inspect.isroutine(value) is False:
            # Skip any remaining callables (e.g., C functions bound to names)
            pass
        if count >= max_vars:
            result["..."] = f"(+{len(g) - count} more)"
            break
        result[name] = safe_repr(value)
        count += 1
    return result


def snapshot_watch(frame, watch_names: list[str]) -> dict[str, str]:
    """
    Return a snapshot of specific named variables. Searches locals first,
    then globals, then nothing. Supports dotted paths like "obj.attr.sub".
    """
    if not watch_names:
        return {}
    result: dict[str, str] = {}
    try:
        local_ns = frame.f_locals
        global_ns = frame.f_globals
    except Exception:
        return {}

    for expr in watch_names:
        try:
            parts = expr.split(".")
            root = parts[0]
            if root in local_ns:
                val = local_ns[root]
            elif root in global_ns:
                val = global_ns[root]
            else:
                result[expr] = "<not found>"
                continue
            for attr in parts[1:]:
                val = getattr(val, attr, "<attr missing>")
                if val == "<attr missing>":
                    break
            result[expr] = safe_repr(val)
        except Exception as e:
            result[expr] = f"<eval-error: {type(e).__name__}>"
    return result


# ---- State ---------------------------------------------------------------

class Tracer:
    def __init__(self, output_path: str, filter_prefix: str, events: set[str],
                 include_globals: bool = False, watch_names: list[str] | None = None):
        self.fp = open(output_path, "w", encoding="utf-8", buffering=1)
        self.filter_prefix = os.path.normcase(os.path.abspath(filter_prefix))
        self.events = events
        self.seq = 0
        self.depth = 0
        self.include_globals = include_globals
        self.watch_names = watch_names or []

    def should_trace(self, filename: str) -> bool:
        if not filename:
            return False
        # Reject pseudo-filenames (frozen modules, eval/exec strings, etc.)
        # os.path.abspath("<frozen runpy>") would incorrectly resolve against cwd.
        if filename.startswith("<") or filename.startswith("["):
            return False
        try:
            abs_name = os.path.normcase(os.path.abspath(filename))
        except Exception:
            return False
        if not abs_name.startswith(self.filter_prefix):
            return False
        # Never trace the tracer itself
        if abs_name.endswith(os.path.normcase("python_tracer.py")):
            return False
        return True

    def emit(self, **kwargs) -> None:
        self.seq += 1
        event = {"seq": self.seq, **kwargs}
        self.fp.write(json.dumps(event, ensure_ascii=False) + "\n")

    def close(self) -> None:
        try:
            self.fp.flush()
            self.fp.close()
        except Exception:
            pass


# ---- sys.monitoring callbacks -------------------------------------------

TRACER: Tracer | None = None
TOOL_ID = sys.monitoring.DEBUGGER_ID


def on_py_start(code, offset):
    if TRACER is None:
        return
    if not TRACER.should_trace(code.co_filename):
        return sys.monitoring.DISABLE
    if "call" not in TRACER.events:
        return
    frame = sys._getframe(1)
    TRACER.depth += 1
    event: dict[str, Any] = dict(
        type="call",
        file=code.co_filename,
        line=frame.f_lineno,
        function=code.co_qualname,
        depth=TRACER.depth,
        locals=snapshot_locals(frame),
    )
    if TRACER.include_globals:
        event["globals"] = snapshot_globals(frame)
    if TRACER.watch_names:
        event["watch"] = snapshot_watch(frame, TRACER.watch_names)
    TRACER.emit(**event)


def on_py_return(code, offset, retval):
    if TRACER is None:
        return
    if not TRACER.should_trace(code.co_filename):
        return sys.monitoring.DISABLE
    if "return" in TRACER.events:
        frame = sys._getframe(1)
        TRACER.emit(
            type="return",
            file=code.co_filename,
            line=frame.f_lineno,
            function=code.co_qualname,
            depth=TRACER.depth,
            value=safe_repr(retval),
        )
    TRACER.depth = max(0, TRACER.depth - 1)


def on_raise(code, offset, exception):
    # RAISE is a non-local event — sys.monitoring.DISABLE is not supported here.
    if TRACER is None:
        return
    if not TRACER.should_trace(code.co_filename):
        return
    if "exception" not in TRACER.events:
        return
    frame = sys._getframe(1)
    event: dict[str, Any] = dict(
        type="exception",
        file=code.co_filename,
        line=frame.f_lineno,
        function=code.co_qualname,
        depth=TRACER.depth,
        exception=type(exception).__name__,
        message=safe_repr(exception),
        locals=snapshot_locals(frame),
    )
    # Always include globals and watch on exceptions — diagnostic value is high
    event["globals"] = snapshot_globals(frame)
    if TRACER.watch_names:
        event["watch"] = snapshot_watch(frame, TRACER.watch_names)
    TRACER.emit(**event)


def on_py_unwind(code, offset, exception):
    if TRACER is None:
        return
    if TRACER.should_trace(code.co_filename):
        TRACER.depth = max(0, TRACER.depth - 1)


# ---- Main ----------------------------------------------------------------

def main() -> int:
    if sys.version_info < (3, 12):
        print("wake python_tracer requires Python 3.12+", file=sys.stderr)
        return 2

    parser = argparse.ArgumentParser()
    parser.add_argument("--output", required=True, help="Path to write JSONL events")
    parser.add_argument("--filter-prefix", required=True,
                        help="Only trace files under this directory")
    parser.add_argument("--events", default="call,return,exception",
                        help="Comma-separated event types to capture")
    parser.add_argument("--include-globals", action="store_true",
                        help="Include filtered module globals in every call event")
    parser.add_argument("--watch", default="",
                        help="Comma-separated variable names (or dotted paths) to "
                             "snapshot into every event's 'watch' field")
    parser.add_argument("target", help="Target Python program to run")
    parser.add_argument("target_args", nargs=argparse.REMAINDER,
                        help="Arguments passed to the target program")
    args = parser.parse_args()

    events = set(e.strip() for e in args.events.split(",") if e.strip())
    watch_names = [w.strip() for w in args.watch.split(",") if w.strip()]

    global TRACER
    TRACER = Tracer(args.output, args.filter_prefix, events,
                    include_globals=args.include_globals,
                    watch_names=watch_names)
    TRACER.emit(type="trace_start",
                target=args.target,
                python_version=sys.version,
                events=sorted(events))

    # Register callbacks
    try:
        sys.monitoring.use_tool_id(TOOL_ID, "wake")
    except ValueError:
        pass  # already claimed by us in a previous run in same interpreter
    sys.monitoring.set_events(
        TOOL_ID,
        sys.monitoring.events.PY_START
        | sys.monitoring.events.PY_RETURN
        | sys.monitoring.events.RAISE
        | sys.monitoring.events.PY_UNWIND,
    )
    sys.monitoring.register_callback(TOOL_ID, sys.monitoring.events.PY_START, on_py_start)
    sys.monitoring.register_callback(TOOL_ID, sys.monitoring.events.PY_RETURN, on_py_return)
    sys.monitoring.register_callback(TOOL_ID, sys.monitoring.events.RAISE, on_raise)
    sys.monitoring.register_callback(TOOL_ID, sys.monitoring.events.PY_UNWIND, on_py_unwind)

    # Run target
    # Pretend to be the target program for sys.argv
    sys.argv = [args.target] + args.target_args

    exit_code = 0
    try:
        runpy.run_path(args.target, run_name="__main__")
    except SystemExit as e:
        exit_code = e.code if isinstance(e.code, int) else 1
    except BaseException as e:
        TRACER.emit(type="fatal",
                    exception=type(e).__name__,
                    message=safe_repr(e),
                    traceback=traceback.format_exc())
        exit_code = 1
    finally:
        sys.monitoring.set_events(TOOL_ID, 0)
        TRACER.emit(type="trace_end", exit_code=exit_code)
        TRACER.close()

    return exit_code


if __name__ == "__main__":
    sys.exit(main())
