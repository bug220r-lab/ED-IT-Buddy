"""Local transport fixture only: actual archive engine, synthetic media inference."""
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from media_worker.core import Engine, canonical
from test_media_worker import Tools, Encoder

state, archive = sys.argv[1:3]
tools = Tools()
def runner(engine, job, name, args, **kwargs):
    if name == "ffprobe":
        tools.durations[str(Path(args[-1]))] = 1
    return tools(engine, job, name, args, **kwargs)

engine = Engine(state, {"expected_host": "", "archive_root": archive, "model_revision": "synthetic"}, runner=runner, encoder=Encoder())
if '--fail-after-receipt' in sys.argv[3:]:
    actual_register = engine.register_root
    fail_once = [True]
    def register(params):
        if fail_once[0]:
            fail_once[0] = False
            from media_worker.core import BuddyError
            raise BuddyError('injected_failure', 'Injected failure after receipt persistence')
        return actual_register(params)
    engine.register_root = register
sys.stdin.reconfigure(encoding="utf-8")
sys.stdout.reconfigure(encoding="utf-8", newline="\n")
for line in sys.stdin:
    request = json.loads(line)
    try:
        if request["method"] == "get_job":
            engine.run_next()
        result = engine.handle(request["method"], request.get("params", {}))
        print(canonical({"id": request["id"], "result": result}), flush=True)
    except Exception as e:
        print(canonical({"id": request["id"], "error": str(e)}), flush=True)
