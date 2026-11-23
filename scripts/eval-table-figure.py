"""Run MCP preview, stale-preview rejection, publish and overwrite rejection.

Usage: python scripts/eval-table-figure.py source.docx request.json figure.png [table-caption image-caption]
When captions are supplied, only rows are read from the request; identifiers are discovered via MCP.
"""
from hashlib import sha256
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
from tempfile import TemporaryDirectory
from threading import Timer


def evaluate(source, request, figure, table_caption=None, image_caption=None):
    root = Path(__file__).resolve().parents[1]
    dotnet = root / ".dotnet" / ("dotnet.exe" if os.name == "nt" else "dotnet")
    cli = root / "src/DocxGuard.Cli/bin/Release/net10.0/DocxGuard.Cli.dll"
    with TemporaryDirectory() as temp:
        workspace = Path(temp)
        paths = dict(sourcePath="input.docx", requestPath="request.json", figurePath="image.png")
        for path, target in zip((source, request, figure), paths.values()):
            shutil.copyfile(path, workspace / target)
        command = [str(dotnet) if dotnet.exists() else "dotnet", str(cli), "mcp", "--workspace", temp, "--allow-apply"]
        env = dict(os.environ, DOCXGUARD_PYTHON=sys.executable)
        with subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                              text=True, encoding="utf-8", env=env) as server:
            deadline = Timer(90, server.kill)
            deadline.start()
            sequence = 0

            def rpc(method, params):
                nonlocal sequence
                sequence += 1
                server.stdin.write(json.dumps(dict(jsonrpc="2.0", id=sequence, method=method, params=params)) + "\n")
                server.stdin.flush()
                return json.loads(server.stdout.readline())["result"]

            def call(name, **extra):
                return rpc("tools/call", dict(name="docxguard_table_figure_" + name, arguments=paths | extra))

            try:
                rpc("initialize", dict(protocolVersion="2025-11-25", capabilities={}, clientInfo=dict(name="eval", version="1")))
                server.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
                server.stdin.flush()
                if table_caption is not None:
                    candidates = []
                    while True:
                        reply = rpc("tools/call", dict(name="docxguard_table_figure_list",
                            arguments=dict(sourcePath=paths["sourcePath"], offset=len(candidates), limit=2)))
                        page = reply["structuredContent"]
                        assert not reply.get("isError"), reply
                        candidates.extend(page["items"])
                        if not page["hasMore"]:
                            break
                        assert page["items"]
                    assert len(candidates) == page["total"]
                    def target(kind, caption):
                        matches = [item for item in candidates if item["kind"] == kind
                                   and caption in (item["before"], item["after"])]
                        assert len(matches) == 1, (caption, matches)
                        return matches[0]
                    content = dict(sourceSha256=page["sourceSha256"], rows=json.loads(request.read_bytes())["rows"],
                                   table=target("table", table_caption)["table"],
                                   imageRelationship=target("image", image_caption)["imageRelationship"])
                    paths.pop("requestPath")
                    paths["requestJson"] = json.dumps(content)
                preview = call("preview")
                assert not preview.get("isError"), preview
                assert set(p.name for p in workspace.iterdir()) == {"input.docx", "request.json", "image.png"}
                report = preview["structuredContent"]["buildReport"]
                args = dict(previewSha256=report["outputSha256"], outputDirectory="result")
                request_path = workspace / "request.json"
                content = json.loads(paths["requestJson"]) if table_caption is not None else json.loads(request_path.read_bytes())
                content["rows"][0][0] += " updated"
                if table_caption is not None:
                    original_json = paths["requestJson"]
                    paths["requestJson"] = json.dumps(content)
                else:
                    request_path.write_text(json.dumps(content), encoding="utf-8")
                stale = call("apply", **args)
                assert stale.get("isError") and "differs from preview" in str(stale), stale
                assert not (workspace / "result").exists()
                shutil.copyfile(request, request_path)
                if table_caption is not None:
                    paths["requestJson"] = original_json
                applied = call("apply", **args)
                assert not applied.get("isError"), applied
                result = workspace / "result"
                assert {p.name for p in result.iterdir()} == {"output.docx", "build-report.json"}
                assert json.loads((result / "build-report.json").read_bytes()) == report
                assert sha256((result / "output.docx").read_bytes()).hexdigest() == args["previewSha256"]
                assert call("apply", **args).get("isError")
                print(json.dumps(report, ensure_ascii=False, indent=2))
            finally:
                server.stdin.close()
                try:
                    server.wait(timeout=10)
                finally:
                    deadline.cancel()
                    if server.poll() is None:
                        server.kill()


if __name__ == "__main__":
    if len(sys.argv) not in (4, 6):
        raise SystemExit(__doc__)
    evaluate(*map(Path, sys.argv[1:4]), *sys.argv[4:])
