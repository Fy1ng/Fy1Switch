#!/usr/bin/env python3
"""Smoke-test a published Linux Relay on loopback, without a Windows peer or hardware."""
import argparse
import base64
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("program", type=Path)
    program = parser.parse_args().program.resolve(strict=True)
    if os.name != "posix" or os.geteuid() == 0:
        raise SystemExit("Run the isolated Linux Relay test as a non-root user.")
    with tempfile.TemporaryDirectory(prefix="fy1switch-relay-linux-") as temporary:
        root = Path(temporary)
        device = str(uuid.uuid4())
        token = base64.b64encode(secrets.token_bytes(32)).decode()
        config = root / "config.json"
        config.write_text(json.dumps({"devices": [{"deviceId": device, "windowsToken": base64.b64encode(secrets.token_bytes(32)).decode(), "controllerToken": token}]}))
        config.chmod(0o600)
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            port = listener.getsockname()[1]
        origin = f"http://127.0.0.1:{port}"
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with (root / "server.log").open("w") as log:
            process = subprocess.Popen([str(program), "--FY1SWITCH_RELAY_CONFIG", str(config), "--urls", origin], cwd=root, stdout=log, stderr=log)
            try:
                def post(command, credential=token):
                    payload = {"version": 1, "deviceId": device, "requestId": str(uuid.uuid4()),
                               "expiresAt": (datetime.now(timezone.utc) + timedelta(seconds=30)).isoformat(), **command}
                    request = urllib.request.Request(origin + f"/v1/devices/{device}/control", data=json.dumps(payload).encode(),
                                                     headers={"Authorization": "Bearer " + credential, "X-Device-Token": "isolated-fixture",
                                                              "Content-Type": "application/json"}, method="POST")
                    try:
                        response = opener.open(request, timeout=5)
                    except urllib.error.HTTPError as error:
                        response = error
                    with response:
                        body = response.read()
                        return response.status, json.loads(body) if body else None

                deadline = time.monotonic() + 30
                while True:
                    try:
                        assert post({"command": "status"}, "invalid")[0] == 401
                        break
                    except urllib.error.URLError:
                        if process.poll() is not None or time.monotonic() > deadline:
                            raise RuntimeError("Isolated Relay did not start.\n" + (root / "server.log").read_text())
                        time.sleep(0.1)
                target = str(uuid.uuid4())
                for command in [
                    {"command": "status"},
                    {"command": "controls", "targetId": target},
                    {"command": "set-control", "targetId": target, "operationId": str(uuid.uuid4()), "control": "Brightness", "value": 42},
                    {"command": "control-operation", "targetId": target, "operationId": str(uuid.uuid4())},
                ]:
                    status, result = post(command)
                    assert status == 503 and result["errorCode"] == "device_offline", (command["command"], status, result)
                assert post({"command": "set-control", "targetId": target, "operationId": str(uuid.uuid4()), "control": "Brightness", "value": -1})[0] == 400
                assert post({"command": "shutdown"})[0] == 400
                assert token not in (root / "server.log").read_text()
                print("PASS: non-root Linux Relay startup, auth, original status, all three parameter commands, invalid values and offline behavior; no Windows peer or hardware used.")
            finally:
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=5)


if __name__ == "__main__":
    main()
