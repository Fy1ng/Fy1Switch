#!/usr/bin/env python3
"""Linux smoke test for a self-contained validation build. Never connects to SSH."""
import argparse
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from datetime import datetime, timedelta, timezone


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("program", type=Path, help="Absolute Linux self-contained Gateway executable")
    options = parser.parse_args()
    program = options.program.resolve(strict=True)
    if os.name != "posix" or os.geteuid() == 0:
        raise SystemExit("Run this Linux smoke test as a non-root user.")
    with tempfile.TemporaryDirectory(prefix="fy1switch-wol-linux-") as temporary:
        root = Path(temporary)
        device_id = str(uuid.uuid4())
        configuration = {
            "publicUrl": "https://wol.example.invalid", "dataDirectory": str(root / "data"),
            "ssh": {"host": "10.254.253.2", "user": "root", "identityFile": str(root / "missing-key"),
                    "knownHostsFile": str(root / "missing-hosts"), "bindAddress": "10.254.253.1",
                    "wireGuardInterface": "woltestmissing", "executable": str(root / "NO-SSH-EXECUTABLE")},
            "devices": [{"deviceId": device_id, "name": "Linux fixture", "macAddress": "02:00:00:00:00:01", "interface": "br-test"}],
        }
        config = root / "config.json"
        config.write_text(json.dumps(configuration))
        config.chmod(0o600)
        registration = json.loads(subprocess.check_output([str(program), "admin", str(config), "code", device_id], text=True, cwd=root))
        with socket.socket() as port_probe:
            port_probe.bind(("127.0.0.1", 0))
            port = port_probe.getsockname()[1]
        base = f"http://127.0.0.1:{port}"
        # Ignore environment proxy settings for this exclusively loopback fixture.
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with (root / "server.log").open("w") as log:
            service = subprocess.Popen([str(program), "--FY1SWITCH_WOL_CONFIG", str(config), "--urls", base], stdout=log, stderr=log, cwd=root)
            try:
                def request(path, method="GET", body=None, token=None):
                    headers = {"Content-Type": "application/json"}
                    if token is not None:
                        headers["Authorization"] = "Bearer " + token
                    call = urllib.request.Request(base + path, data=json.dumps(body).encode() if body is not None else None,
                                                  headers=headers, method=method)
                    try:
                        response = opener.open(call, timeout=5)
                    except urllib.error.HTTPError as error:
                        response = error
                    with response:
                        content = response.read()
                        return response.status, json.loads(content) if content else None

                deadline = time.monotonic() + 30
                while True:
                    try:
                        assert request("/healthz")[0] == 200
                        break
                    except urllib.error.URLError:
                        if service.poll() is not None or time.monotonic() > deadline:
                            raise RuntimeError("Isolated Gateway failed to become ready. Startup log:\n" + (root / "server.log").read_text())
                        time.sleep(0.1)
                assert request("/v1/wol/devices")[0] == 401
                status, registered = request("/v1/wol/register", "POST", {
                    "version": 1, "registrationCode": registration["registrationCode"], "clientName": "Linux smoke"})
                assert status == 200
                token = registered["accessToken"]
                assert request("/v1/wol/devices", token=token)[1]["devices"][0]["deviceId"] == device_id
                now = datetime.now(timezone.utc)
                operation_id = str(uuid.uuid4())
                body = {"version": 1, "deviceId": device_id, "requestId": str(uuid.uuid4()), "operationId": operation_id,
                        "issuedAt": now.isoformat(), "expiresAt": (now + timedelta(seconds=30)).isoformat()}
                status, operation = request(f"/v1/wol/devices/{device_id}/wake", "POST", body, token)
                assert status == 200 and operation["status"] == "failed", operation
                assert operation["errorCode"] == "tunnel_unavailable", operation["errorCode"]
                assert request(f"/v1/wol/devices/{device_id}/operations/{operation_id}", token=token)[1] == operation
                assert request(f"/v1/wol/devices/{device_id}/wake", "POST", body, token)[1] == operation
                assert request("/v1/wol/clients/me", "DELETE", token=token)[0] == 204
                assert request("/v1/wol/devices", token=token)[0] == 401
                assert token not in (root / "server.log").read_text()
                assert token.encode() not in (root / "data/wol.db").read_bytes()
                print("PASS: Linux non-root startup, health, one-time enrollment, auth, safe SSH preflight failure, durable result/dedup and revocation. No SSH executable existed.")
            finally:
                service.terminate()
                try:
                    service.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    service.kill()
                    service.wait(timeout=5)


if __name__ == "__main__":
    main()
