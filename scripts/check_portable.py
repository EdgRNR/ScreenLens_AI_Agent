"""Verify a relocated portable bundle with no developer paths or user settings.

Starts the frontend only in its windowless installation-check mode. Exercises
packaged OCR and translation through the real framed process protocol, with a
local HTTP fixture (no real API key, no calls to external translation services).
"""
from __future__ import annotations

import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import threading
import time

ROOT = Path(__file__).resolve().parents[1]


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def frame(data):
    raw = data if isinstance(data, bytes) else json.dumps(data, ensure_ascii=False).encode("utf-8")
    return struct.pack(">I", len(raw)) + raw


class Worker:
    def __init__(self, command, env, cwd):
        self.process = subprocess.Popen([str(part) for part in command], env=env, cwd=cwd,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            creationflags=subprocess.CREATE_NO_WINDOW)

    def call(self, request, image=None):
        timer = threading.Timer(90, self.process.kill)
        timer.start()
        try:
            self.process.stdin.write(frame(request))
            if image is not None:
                self.process.stdin.write(frame(image))
            self.process.stdin.flush()
            events = []
            while True:
                header = self.process.stdout.read(4)
                if len(header) != 4:
                    raise RuntimeError("Worker exited: " + self.process.stderr.read().decode("utf-8", errors="replace"))
                length, = struct.unpack(">I", header)
                require(length <= 32 * 1024 * 1024, "Invalid response frame size")
                response = json.loads(self.process.stdout.read(length))
                if response.get("event") == "delta":
                    events.append(response["data"]["text"])
                else:
                    return response, events
        except (BrokenPipeError, OSError):
            self.process.wait(5)
            raise RuntimeError("Worker pipe failed: " + self.process.stderr.read().decode("utf-8", errors="replace"))
        finally:
            timer.cancel()

    def close(self):
        try:
            if self.process.poll() is None:
                self.process.stdin.write(frame({"op": "exit", "id": 0}))
                self.process.stdin.flush()
                self.process.wait(10)
        except (OSError, ValueError):
            pass
        finally:
            if self.process.poll() is None:
                self.process.kill()
                self.process.wait(5)
            for stream in (self.process.stdin, self.process.stdout, self.process.stderr):
                try:
                    stream.close()
                except (OSError, ValueError):
                    pass


class LocalApi(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def send_json(self, data):
        raw = json.dumps(data).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self):
        require(self.path == "/v1/models", "Wrong model listing endpoint")
        self.send_json({"data": [{"id": "portable-model"}, {"id": "portable-model"}]})

    def do_POST(self):
        require(self.path == "/v1/chat/completions", "Wrong translation endpoint")
        data = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        if not data.get("stream"):
            self.send_json({"choices": [{"message": {"content": "便携测试成功"}}]})
            return
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.end_headers()
        for text in ("便携", "测试", "成功"):
            self.wfile.write(("data: " + json.dumps({"choices": [{"delta": {"content": text}}]},
                ensure_ascii=False) + "\n\n").encode("utf-8"))
            self.wfile.flush()
            time.sleep(0.06)
        self.wfile.write(b"data: [DONE]\n\n")
        self.wfile.flush()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=Path)
    parser.add_argument("--work-dir", type=Path, required=True)
    args = parser.parse_args()
    work = args.work_dir.resolve()
    work.mkdir(parents=True, exist_ok=True)
    # Deliberately include spaces and non-ASCII characters; no repo-relative
    # paths, system dotnet or Python are available to these child processes.
    relocated = work / "解压验证 ScreenLens"
    shutil.copytree(args.bundle.resolve(), relocated)
    env = {key: value for key, value in os.environ.items() if not key.startswith("SCREENLENS_")
           and key not in ("PYTHONPATH", "PYTHONHOME", "DOTNET_ROOT", "DOTNET_ROOT_X64")}
    env.update(PATH=str(Path(os.environ["SystemRoot"]) / "System32"),
               APPDATA=str(work / "profile/roaming"), LOCALAPPDATA=str(work / "profile/local"),
               DOTNET_ROOT=str(work / "no-system-dotnet"), DOTNET_ROOT_X64=str(work / "no-system-dotnet"),
               DOTNET_MULTILEVEL_LOOKUP="0", NO_PROXY="127.0.0.1,localhost")
    for key in ("HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy"):
        env.pop(key, None)
    report = {}
    for exe in ("ScreenLensAgent.exe", "ScreenLensWorker.exe", "ScreenLens.WinUI.exe",
                "ocr/ScreenLens.Ocr.Worker.exe", "_internal/python313.dll", "resources.pri",
                "MainWindow.xbf", "Views/Capture/SelectionWindow.xbf",
                "Views/Capture/CaptureToastWindow.xbf", "Views/Result/ResultWindow.xbf"):
        if exe.endswith("python313.dll"):
            require(any((relocated / "_internal").glob("python3*.dll")), "Embedded Python runtime missing")
        else:
            require((relocated / exe).is_file(), f"Missing bundle entry: {exe}")
    for name in ("ScreenLens.WinUI.runtimeconfig.json", "ocr/ScreenLens.Ocr.Worker.runtimeconfig.json"):
        runtime = json.loads((relocated / name).read_text(encoding="utf-8"))["runtimeOptions"]
        require("framework" not in runtime and "frameworks" not in runtime,
                f"{name} depends on installed .NET")
    for exe, argument, output in (
        ("ScreenLensAgent.exe", "--check-install", work / "agent.json"),
        ("ScreenLens.WinUI.exe", "--check-install=", work / "frontend.json"),
    ):
        command = [str(relocated / exe), argument, str(output)] if not argument.endswith("=") else [
            str(relocated / exe), argument + str(output)]
        result = subprocess.run(command, cwd=work, env=env, stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL, creationflags=subprocess.CREATE_NO_WINDOW, timeout=60)
        require(output.is_file(), f"{exe} produced no installation report (exit={result.returncode})")
        info = json.loads(output.read_text(encoding="utf-8"))
        require(result.returncode == 0 and info.get("ok"), f"{exe} check failed: {info}")
        report[exe] = info
    agent = report["ScreenLensAgent.exe"]
    require(agent["frozen"], "Agent is not the bundled executable")
    for path in (agent["frontend"], *agent["ocr_command"], *agent["translation_command"]):
        require(Path(path).is_relative_to(relocated), f"Bundle resolved a developer dependency: {path}")
    require(not (work / "profile/roaming/ScreenLens/config.json").exists(), "Installation check wrote configuration")
    print("PASS: relocated frozen Agent, bundled paths, tray logo, self-contained WinUI and XAML startup", flush=True)

    worker = Worker([relocated / "ocr/ScreenLens.Ocr.Worker.exe", relocated / "ocr/models"], env, work)
    try:
        png = (ROOT / "tests/data/ocr_test.png").read_bytes()
        timings = []
        for request_id in (1, 2):
            started = time.perf_counter()
            response, _ = worker.call({"op": "ocr", "id": request_id}, png)
            timings.append(round((time.perf_counter() - started) * 1000))
            require(response.get("ok") and "ScreenLens" in response["data"]["text"],
                    f"Packaged OCR failed: {response}")
        response, _ = worker.call({"op": "ocr", "id": 3}, b"not a PNG")
        require(not response.get("ok") and response["error"]["code"] == "ocr_failed", "Bad image was not rejected")
        response, _ = worker.call({"op": "ocr", "id": 4}, png)
        require(response.get("ok"), "OCR did not recover after a bad image")
        report["ocr"] = {"cold_wall_ms": timings[0], "warm_wall_ms": timings[1], "reuse_and_recovery": True}
        print(f"PASS: native OCR, reuse, malformed image recovery; cold/warm={timings} ms", flush=True)
    finally:
        worker.close()

    server = ThreadingHTTPServer(("127.0.0.1", 0), LocalApi)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    worker = Worker([relocated / "ScreenLensWorker.exe"], env, work)
    try:
        api = {"base_url": f"http://127.0.0.1:{server.server_port}/v1",
               "api_key": "isolated-portable-test", "model": "portable-model"}
        response, _ = worker.call({"op": "models", "id": 1, "data": {"openai": api}})
        require(response.get("ok") and response["data"]["models"] == ["portable-model"], "Packaged model listing failed")
        response, chunks = worker.call({"op": "translate", "id": 2, "data": {
            "text": "Portable test", "target_language": "zh", "stream": True,
            "config": {"provider": "openai", "openai": api}}})
        require(response.get("ok") and response["data"]["text"] == "便携测试成功"
                and "".join(chunks) == "便携测试成功" and len(chunks) >= 2, "Packaged stream translation failed")
        response, _ = worker.call({"op": "test_translation", "id": 3, "data": {"openai": api}})
        require(response.get("ok"), "Packaged API connection test failed")
        response, _ = worker.call({"op": "translate", "id": 4, "data": {
            "text": "test", "config": {"provider": "none"}}})
        require(not response.get("ok"), "Disabled provider did not return an error")
        report["translation"] = {"models": True, "sse": True, "connection_test": True, "disabled_provider": True}
        print("PASS: frozen translation worker, model listing, SSE, connection test and error framing (local fixture)", flush=True)
    finally:
        worker.close()
        server.shutdown()
        server.server_close()
        thread.join(3)
    report["ok"] = True
    (work / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print("Portable checks passed; no real user configuration, startup registry or desktop windows modified.", flush=True)


if __name__ == "__main__":
    main()
