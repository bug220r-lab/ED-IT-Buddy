"""Bounded JSON-lines bridge. Does not start workers, models, or GUI applications."""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

if __package__ in (None, ""):
    sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

from media_worker.core import BuddyError, Engine, canonical


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--state", required=True)
    parser.add_argument("--config", required=True)
    args = parser.parse_args()
    config = json.loads(Path(args.config).read_text("utf-8-sig"))
    engine = Engine(args.state, config)
    # UTF-8 JSON protocol even under legacy Windows console code pages.
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8", newline="\n")
    while True:
        line = sys.stdin.buffer.readline(1024 * 1024 + 1)
        if not line:
            break
        request_id = None
        try:
            if len(line) > 1024 * 1024 or not line.endswith(b"\n"):
                raise BuddyError("invalid_request", "Protocol line exceeds its bound or is incomplete.")
            request = json.loads(line.decode("utf-8-sig"))
            if not isinstance(request, dict) or set(request) - {"id", "method", "params"}:
                raise BuddyError("invalid_request", "Invalid protocol request.")
            request_id = request.get("id")
            if not isinstance(request_id, (str, int)):
                raise BuddyError("invalid_request", "A request correlation ID is required.")
            result = engine.handle(request.get("method"), request.get("params", {}))
            response = {"id": request_id, "result": result, "error": None}
        except Exception as error:
            response = {"id": request_id, "result": None, "error": {"code": getattr(error, "code", "worker_error"), "message": str(error)[:3000]}}
        sys.stdout.write(canonical(response) + "\n")
        sys.stdout.flush()


if __name__ == "__main__":
    main()
