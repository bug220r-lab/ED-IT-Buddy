"""Scheduled-task entry point. It retains jobs independently of SSH clients."""
from __future__ import annotations

import argparse
import json
import sys
import threading
import time
import traceback
from pathlib import Path

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from media_worker.core import Engine, atomic_json, worker_lock


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--state", required=True)
    parser.add_argument("--config", required=True)
    parser.add_argument("--once", action="store_true", help="Run one bounded job unit for diagnostics.")
    parser.add_argument("--preflight", action="store_true", help="Exercise CUDA in this exact launch context.")
    args = parser.parse_args()
    config = json.loads(Path(args.config).read_text("utf-8-sig"))
    engine = Engine(args.state, config)
    try:
        run(engine, args)
    except Exception as error:
        atomic_json(engine.state / "last-worker-error.json", {"observed_at": time.time(), "phase": "preflight" if args.preflight else "worker", "code": getattr(error, "code", "worker_start_failed"), "message": str(error)[:3000], "traceback": traceback.format_exc()[-16000:]})
        raise


def run(engine, args):
    with worker_lock(engine.state):
        if args.preflight:
            from media_worker.vision import preflight
            result = preflight(engine)
            atomic_json(engine.state / "gpu-preflight.json", result)
            if sys.stdout is not None:
                print(json.dumps(result), flush=True)
            return
        engine.recover()
        stop = threading.Event()
        def pulse():
            while not stop.wait(3):
                try:
                    engine.heartbeat()
                except Exception:
                    pass  # Work writes still fail explicitly; a failed pulse expires naturally.
        engine.heartbeat()
        thread = threading.Thread(target=pulse, daemon=True)
        thread.start()
        try:
            while True:
                worked = engine.run_next()
                if args.once:
                    break
                if not worked:
                    time.sleep(.5)
        finally:
            stop.set()
            thread.join(timeout=5)


if __name__ == "__main__":
    main()
