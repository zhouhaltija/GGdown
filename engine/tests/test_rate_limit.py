"""验证多个引擎进程共享的总下载限速。"""
import io
import json
import sqlite3
import subprocess
import sys
import time
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))


def make_config(tmp_path, rate):
    settings = tmp_path / "settings.db"
    with sqlite3.connect(settings) as db:
        db.execute("CREATE TABLE Settings (Key TEXT PRIMARY KEY, Value TEXT)")
        db.execute("INSERT INTO Settings VALUES (?, ?)", ("download.rateLimitBytesPerSecond", json.dumps(rate)))
    return {"settings-db": str(settings), "state-db": str(tmp_path / "rate.db")}


def set_rate(config, rate):
    with sqlite3.connect(config["settings-db"]) as db:
        db.execute("UPDATE Settings SET Value = ?", (json.dumps(rate),))


def test_unlimited_does_not_sleep_or_create_scheduler(tmp_path, monkeypatch):
    from rate_limit import SharedRateLimiter

    config = make_config(tmp_path, 0)
    monkeypatch.setattr(time, "sleep", lambda _: pytest.fail("不限速时不应等待"))
    SharedRateLimiter(config).consume(1048576)
    assert not Path(config["state-db"]).exists()


def test_two_limiter_instances_share_one_total_budget(tmp_path, monkeypatch):
    from rate_limit import SharedRateLimiter

    config = make_config(tmp_path, 1024)
    now = [100.0]
    monkeypatch.setattr(time, "monotonic", lambda: now[0])
    monkeypatch.setattr(time, "sleep", lambda duration: now.__setitem__(0, now[0] + duration))
    first, second = SharedRateLimiter(config), SharedRateLimiter(config)
    first.consume(1024)
    second.consume(1024)
    assert now[0] == pytest.approx(102.0)


def test_waiting_download_observes_disabling_rate_limit(tmp_path, monkeypatch):
    from rate_limit import SharedRateLimiter

    config = make_config(tmp_path, 1024)
    now = [100.0]
    monkeypatch.setattr(time, "monotonic", lambda: now[0])

    def sleep(duration):
        now[0] += duration
        set_rate(config, 0)

    monkeypatch.setattr(time, "sleep", sleep)
    SharedRateLimiter(config).consume(1048576)
    assert now[0] <= 100.2


def test_rate_change_resets_old_reservations(tmp_path, monkeypatch):
    from rate_limit import SharedRateLimiter

    config = make_config(tmp_path, 1024)
    now = [100.0]
    monkeypatch.setattr(time, "monotonic", lambda: now[0])
    monkeypatch.setattr(time, "sleep", lambda duration: now.__setitem__(0, now[0] + duration))
    limiter = SharedRateLimiter(config)
    limiter.consume(1024)
    set_rate(config, 2048)
    limiter.consume(2048)
    assert now[0] == pytest.approx(102.0)


def test_two_processes_cannot_each_use_the_full_limit(tmp_path):
    config = make_config(tmp_path, 65536)
    script = (
        "import sys,json;sys.path.insert(0,sys.argv[1]);"
        "from rate_limit import SharedRateLimiter;"
        "SharedRateLimiter(json.loads(sys.argv[2])).consume(32768)"
    )
    args = [sys.executable, "-c", script, str(Path(__file__).resolve().parents[1]), json.dumps(config)]
    start = time.monotonic()
    processes = [subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE) for _ in range(2)]
    try:
        for process in processes:
            stdout, stderr = process.communicate(timeout=10)
            assert process.returncode == 0, stderr.decode(errors="replace")
        assert time.monotonic() - start >= 0.95
    finally:
        for process in processes:
            if process.poll() is None:
                process.kill()
                process.wait()


def test_gallery_receiver_uses_shared_limit_and_restores_library(tmp_path, monkeypatch):
    from rate_limit import SharedRateLimiter, limit_gallery_downloads
    from gallery_dl.downloader.http import HttpDownloader

    config = make_config(tmp_path, 1024)
    now = [100.0]
    monkeypatch.setattr(time, "monotonic", lambda: now[0])
    monkeypatch.setattr(time, "sleep", lambda duration: now.__setitem__(0, now[0] + duration))
    original = HttpDownloader.receive
    receiver = object.__new__(HttpDownloader)
    target = io.BytesIO()
    with limit_gallery_downloads(SharedRateLimiter(config)):
        receiver.receive(target, iter([b"a" * 1024, b"b" * 1024]), 2048, 0)
    assert target.getvalue() == b"a" * 1024 + b"b" * 1024
    assert now[0] == pytest.approx(102.0)
    assert HttpDownloader.receive is original


def test_gallery_and_douyin_network_transfers_share_total_limit(tmp_path):
    config = make_config(tmp_path, 65536)
    payload = b"\x00\x00\x00\x18ftypmp42" + b"a" * (65536 - 12)

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            self.send_response(200)
            self.send_header("Content-Type", "video/mp4")
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

        def log_message(self, *args):
            pass

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    url = f"http://127.0.0.1:{server.server_port}/video.mp4"
    engine_dir = Path(__file__).resolve().parents[1]
    cookies = tmp_path / "cookies.txt"
    cookies.write_text("# Netscape HTTP Cookie File\n", encoding="utf-8")
    job = tmp_path / "job.json"
    job.write_text(json.dumps({
        "urls": [url], "options": {
            "ggdown-rate-limit": config, "base-directory": str(tmp_path / "gallery"),
            "extractor": {"generic": {"enabled": True}},
            "downloader": {"http": {"validate": False}},
        },
    }), encoding="utf-8")
    douyin_target = tmp_path / "douyin.mp4"
    script = (
        "import asyncio,json,sys;from pathlib import Path;sys.path.insert(0,sys.argv[1]);"
        "from sites.douyin import DouyinClient;from rate_limit import SharedRateLimiter;"
        "from curl_cffi.requests import AsyncSession;"
        "exec('async def run():\\n async with AsyncSession() as s:\\n  with SharedRateLimiter(json.loads(sys.argv[2])) as limiter:\\n   client=DouyinClient(s, \\\"\\\");client.rate_limiter=limiter\\n   await client.stream_file(sys.argv[3],Path(sys.argv[4]))');"
        "asyncio.run(run())"
    )
    start = time.monotonic()
    commands = [
        [sys.executable, str(engine_dir / "runner.py"), "download", "--site", "twitter", "--cookies", str(cookies), "--job", str(job)],
        [sys.executable, "-c", script, str(engine_dir), json.dumps(config), url, str(douyin_target)],
    ]
    processes = [subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE) for args in commands]
    try:
        for process in processes:
            stdout, stderr = process.communicate(timeout=15)
            assert process.returncode == 0, stderr.decode(errors="replace")
        assert time.monotonic() - start >= 1.9
        assert douyin_target.read_bytes() == payload
        gallery_files = list((tmp_path / "gallery").rglob("*.mp4"))
        assert len(gallery_files) == 1
        assert gallery_files[0].read_bytes() == payload
    finally:
        for process in processes:
            if process.poll() is None:
                process.kill()
                process.wait()
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)
