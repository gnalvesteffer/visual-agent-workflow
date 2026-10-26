"""Exercise the standalone service consumer without HTTP, Studio or browser access.

Runs deterministic fixtures and creates disposable local journals. Requires a
Release build of samples/WorkflowService. No live provider credentials are used.
"""
import argparse
import hashlib
import json
import os
import sqlite3
from pathlib import Path
import signal
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parent.parent
SAMPLE = ROOT / "samples/WorkflowService"


def check(dotnet):
    command = [dotnet, str(SAMPLE / "bin/Release/net10.0/WorkflowService.dll")]
    artifacts = SAMPLE / "artifacts"
    with tempfile.TemporaryDirectory(prefix="vaw-service-") as temporary:
        work = Path(temporary)
        counter = 0

        def execute(mode, deployment, grant=False, expected=0, error_type=None, store="file"):
            nonlocal counter
            counter += 1
            journal = work / str(counter)
            args = command + [mode, str(deployment), str(journal), "abc"]
            if grant:
                args.append("--grant-demo-tool")
            result = subprocess.run(args, capture_output=True, text=True, timeout=40, env={**os.environ, "WorkflowService__RunStore":store})
            assert result.returncode == expected, (args, result.returncode, result.stdout, result.stderr)
            if error_type:
                assert f"{error_type}." in result.stderr, result.stderr
            records = [json.loads(line) for line in result.stdout.splitlines() if line.startswith("{")]
            return records, journal

        def verify(mode, kind, grant=False, store="file"):
            manifest = json.loads((artifacts / f"{kind}.deployment.json").read_text())
            records, journal = execute(mode, artifacts / f"{kind}.deployment.json", grant, store=store)
            result = records[-1]
            assert result["status"] == "completed"
            assert result["output"] == ("CBA" if kind == "pure" else "cba")
            assert result["revisionId"] == manifest["revisionId"]
            assert result["contentHash"] == manifest["definitionHash"]
            if mode == "queued":
                assert records[0]["status"] == "paused"
                assert records[0]["runId"] == result["runId"]
                assert result["events"].count("run.resumed") == 1
            assert result["events"].count("node.started") == (4 if kind == "pure" else 3)
            assert result["events"].count("tool.completed") == (1 if kind == "mcp" else 0)
            if store == "sqlite":
                with sqlite3.connect(journal / "runs.sqlite3") as connection:
                    events = [json.loads(row[0]) for row in connection.execute("SELECT event_json FROM run_events ORDER BY sequence")]
                    sequence, status = connection.execute("SELECT sequence,status FROM runs").fetchone()
                    assert sequence == len(events) and status == "completed"
            else:
                events = json.loads(next(journal.glob("*.snapshot.json")).read_text())["events"]
            assert [event["sequence"] for event in events] == list(range(1, len(events) + 1))
            assert all(event["revisionId"] == manifest["revisionId"] for event in events)
            assert not list(journal.rglob("*.db")), "Execution should not create a catalog"

        for store in ("file", "sqlite"):
            for kind in ("pure", "mcp"):
                for mode in ("direct", "queued"):
                    verify(mode, kind, grant=kind == "mcp", store=store)
        records, journal = execute("direct", artifacts / "mcp.deployment.json", expected=1, error_type="ToolAccessDeniedException")
        assert not records and not journal.exists(), "Denied deployment admitted execution"

        def deployment_variant(name, change_package=None, change_manifest=None):
            package = json.loads((artifacts / "mcp.package.json").read_text())
            manifest = json.loads((artifacts / "mcp.deployment.json").read_text())
            if change_package:
                change_package(package)
                package["definitionHash"] = hashlib.sha256(json.dumps(package["definition"], indent=2).encode()).hexdigest()
                manifest["definitionHash"] = package["definitionHash"]
            package_path = work / f"{name}.package.json"
            package_path.write_text(json.dumps(package, indent=2))
            manifest["packageFile"] = package_path.name
            if change_manifest:
                change_manifest(manifest)
            path = work / f"{name}.deployment.json"
            path.write_text(json.dumps(manifest))
            return path

        def missing_tool(package):
            package["definition"]["nodes"]["transform"]["tool"]["bindingId"] = "unregistered.reverse"
            next(r for r in package["requirements"] if r["kind"] == "mcp-tool")["id"] = "unregistered.reverse"

        def drift(package):
            package["definition"]["nodes"]["transform"]["tool"]["contractHash"] = "0" * 64
            next(r for r in package["requirements"] if r["kind"] == "mcp-tool")["contractHash"] = "0" * 64

        def missing_provider(package):
            package["definition"]["schemaVersion"] = "1.2"
            package["definition"]["nodes"]["transform"] = {"type":"core.agent","typeVersion":"1.0.0", "agent":{
                "providerId":"absent","instructions":"fixture","tools":[]}}
            package["requirements"] = [r for r in package["requirements"] if r["kind"] != "mcp-tool" and r["id"] != "core.mcp-tool"]
            package["requirements"] += [{"kind":"node","id":"core.agent","version":"1.0.0"},
                                         {"kind":"agent-provider","id":"absent","version":""}]

        for name, change, error_type in (("missing-tool", missing_tool, "InvalidOperationException"), ("drift", drift, "ToolContractMismatchException"), ("missing-provider", missing_provider, "InvalidOperationException")):
            path = deployment_variant(name, change_package=change)
            records, journal = execute("queued", path, grant=True, expected=1, error_type=error_type)
            assert not records and not journal.exists(), f"{name} admitted execution"
        path = deployment_variant("other-workflow", change_package=lambda p: p["definition"].update(workflowId="other-workflow"))
        records, journal = execute("queued", path, grant=True, expected=1, error_type="ToolAccessDeniedException")
        assert not records and not journal.exists()
        path = deployment_variant("wrong-pin", change_manifest=lambda m: m.update(definitionHash="0" * 64))
        records, journal = execute("direct", path, grant=True, expected=1, error_type="ArgumentException")
        assert not records and not journal.exists()
        path = deployment_variant("invalid-package")
        package_path = work / "invalid-package.package.json"
        package_path.write_text('{"formatVersion":"unsupported"}')
        records, journal = execute("queued", path, grant=True, expected=1)
        assert not records and not journal.exists()

        # A host stop, not an HTTP/browser request, owns cancellation of a parked continuation.
        journal = work / "shutdown"
        process = subprocess.Popen(command + ["paused", str(artifacts / "pure.deployment.json"), str(journal), "abc"],
                                   stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        try:
            import selectors
            selector = selectors.DefaultSelector()
            selector.register(process.stdout, selectors.EVENT_READ)
            assert selector.select(timeout=15), "Paused host did not acknowledge admission"
            accepted = json.loads(process.stdout.readline())
            selector.close()
            assert accepted["status"] == "paused"
            process.send_signal(signal.SIGTERM)
            stdout, stderr = process.communicate(timeout=15)
            assert process.returncode == 2, (stdout, stderr, process.returncode)
            result = json.loads(stdout.strip())
            assert result["runId"] == accepted["runId"]
            assert result["status"] in ("cancelled", "interrupted")
            assert "node.started" not in result["events"]
        finally:
            if process.poll() is None:
                process.kill()
                process.communicate()
        dependencies = json.loads((SAMPLE / "bin/Release/net10.0/WorkflowService.deps.json").read_text())["libraries"]
        assert not any("Blazor" in name or "Studio" in name for name in dependencies)
    print("File/SQLite headless direct/queued execution, pinned artifacts, real MCP, denied/missing/drifted dependencies, no catalog/UI dependency, event sequences and host-stop cancellation passed.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    check(parser.parse_args().dotnet)
