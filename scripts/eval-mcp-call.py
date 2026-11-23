"""Blind evaluation transport. Read one MCP tools/list or tools/call request from stdin.

Usage: python scripts/eval-mcp-call.py <evaluation-workspace>
Prints the server result and records the exchange beside the workspace.
"""
import json
import os
from pathlib import Path
import subprocess
import sys
from threading import Timer


def exchange(workspace, query):
    if query["method"] not in ("tools/list", "tools/call"):
        raise ValueError("Expected tools/list or tools/call")
    repo = Path(__file__).resolve().parents[1]
    dotnet = repo / ".dotnet" / ("dotnet.exe" if os.name == "nt" else "dotnet")
    cli = repo / "src/DocxGuard.Cli/bin/Release/net10.0/DocxGuard.Cli.dll"
    messages = [
        dict(id=1, method="initialize", params=dict(protocolVersion="2025-11-25",
             capabilities={}, clientInfo=dict(name="blind-evaluation", version="1"))),
        dict(method="notifications/initialized"),
        dict(id=2, method=query["method"], params=query.get("params", {})),
    ]
    wire = "".join(json.dumps({"jsonrpc": "2.0", **m}) + "\n" for m in messages)
    # Keep stdin open until the reply. Some hosts cancel requests on EOF.
    with subprocess.Popen([str(dotnet) if dotnet.exists() else "dotnet", str(cli),
                           "mcp", "--workspace", str(workspace)],
                          stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8",
                          env=dict(os.environ, DOCXGUARD_PYTHON=sys.executable)) as server:
        deadline = Timer(90, server.kill)
        deadline.start()
        try:
            for line in wire.splitlines():
                server.stdin.write(line + "\n")
                server.stdin.flush()
                message = json.loads(line)
                if "id" in message:
                    response = json.loads(server.stdout.readline())
                    if response.get("id") != message["id"] or "result" not in response:
                        raise RuntimeError(response)
            result = response["result"]
        finally:
            deadline.cancel()
            server.stdin.close()
            try:
                server.wait(timeout=5)
            except subprocess.TimeoutExpired:
                server.kill()
    with workspace.with_suffix(".jsonl").open("a", encoding="utf-8") as log:
        log.write(json.dumps(dict(query=query, result=result), ensure_ascii=False) + "\n")
    return result


if __name__ == "__main__":
    print(json.dumps(exchange(Path(sys.argv[1]).resolve(), json.load(sys.stdin)), ensure_ascii=False))
