"""Verify deterministic native checkpoint rehydration across processes and local owner loss.

Uses the standalone service, temporary SQLite storage and pure string workflows.
No Studio, model credentials, MCP calls, external effects or automatic failover.
"""
import argparse
import hashlib
import json
from pathlib import Path
import selectors
import signal
import sqlite3
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parent.parent


def check(dotnet):
    command = [dotnet, str(ROOT / "samples/WorkflowService/bin/Release/net10.0/WorkflowService.dll")]
    artifacts = ROOT / "samples/WorkflowService/artifacts"
    with tempfile.TemporaryDirectory(prefix="vaw-checkpoint-") as temporary:
        database = Path(temporary) / "runs.sqlite3"
        child = subprocess.Popen(command + ["checkpoint", str(artifacts / "pure.deployment.json"), str(database), "abc", "2", "--hold"],
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            with selectors.DefaultSelector() as selector:
                selector.register(child.stdout, selectors.EVENT_READ)
                assert selector.select(timeout=25), "Checkpoint host did not acknowledge a saved boundary"
                saved = json.loads(child.stdout.readline())
            assert saved["reconciliation"]["canResume"] is True
            assert saved["reconciliation"]["lastSequence"] == saved["sequence"]
            assert saved["status"] == "suspended" and saved["nodes"] == ["input", "step-1"], saved
            recover = command + ["recover", str(database), saved["runId"], saved["checkpointId"]]
            denied = subprocess.run(recover, capture_output=True, text=True, timeout=20)
            assert denied.returncode == 1 and "InvalidOperationException" in denied.stderr
            with sqlite3.connect(database) as connection:
                assert connection.execute("SELECT sequence,status FROM runs").fetchone() == (saved["sequence"], "suspended")
            # Kill the owner instead of disposing it: the kernel must release the local lock.
            child.send_signal(signal.SIGKILL)
            child.communicate(timeout=10)
            completed = subprocess.run(recover, capture_output=True, text=True, timeout=25)
            assert completed.returncode == 0, completed.stderr
            result = json.loads(completed.stdout)
            assert result["runId"] == saved["runId"] and result["revisionId"] == saved["revisionId"]
            assert result["contentHash"] == saved["contentHash"] and result["status"] == "completed"
            assert result["reconciliation"]["canResume"] is False
            assert result["output"] == "CBA" and result["nodes"] == ["input", "step-1", "step-2", "output"], result
            with sqlite3.connect(database) as connection:
                events = [json.loads(row[0]) for row in connection.execute("SELECT event_json FROM run_events ORDER BY sequence")]
                envelope = json.loads(connection.execute("SELECT checkpoint_json FROM run_checkpoints").fetchone()[0])
            assert [e["sequence"] for e in events] == list(range(1, len(events) + 1))
            assert sum(e["kind"] == "run.recovered" for e in events) == 1
            assert envelope["payloadHash"] == hashlib.sha256(envelope["native"]["payload"].encode()).hexdigest()
            stale = subprocess.run(recover, capture_output=True, text=True, timeout=20)
            assert stale.returncode == 1
            with sqlite3.connect(database) as connection:
                assert connection.execute("SELECT sequence FROM runs").fetchone()[0] == len(events)
            denied_db = Path(temporary) / "external.sqlite3"
            external = subprocess.run(command + ["checkpoint", str(artifacts / "mcp.deployment.json"), str(denied_db), "abc", "1"],
                                      capture_output=True, text=True, timeout=20)
            assert external.returncode == 1 and not denied_db.exists(), "External workflow was admitted"
        finally:
            if child.poll() is None:
                child.kill()
                child.communicate(timeout=10)
    print("Native deterministic checkpoint, atomic evidence, live-owner exclusion, forced process loss, explicit same-run rehydration without duplicate nodes, stale recovery and external-node rejection passed.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    check(parser.parse_args().dotnet)
