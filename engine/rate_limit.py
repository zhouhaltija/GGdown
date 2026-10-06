"""多个下载引擎共享的限速额度。"""
from contextlib import contextmanager
import json
from pathlib import Path
import sqlite3
import time

SETTING_KEY = "download.rateLimitBytesPerSecond"


class SharedRateLimiter:
    def __init__(self, config):
        self.config = config
        self._state = None
        self._settings = None
        if config:
            uri = Path(config["settings-db"]).resolve().as_uri() + "?mode=ro"
            self._settings = sqlite3.connect(uri, uri=True, timeout=5)

    def __enter__(self):
        return self

    def __exit__(self, *args):
        if self._state is not None:
            self._state.close()
        if self._settings is not None:
            self._settings.close()

    def _rate(self):
        if self._settings is None:
            return 0
        row = self._settings.execute("SELECT Value FROM Settings WHERE Key = ?", (SETTING_KEY,)).fetchone()
        return max(0, min(1073741824, int(json.loads(row[0]) or 0))) if row else 0

    def _open_state(self):
        path = Path(self.config["state-db"])
        path.parent.mkdir(parents=True, exist_ok=True)
        deadline = time.monotonic() + 5
        while True:
            db = sqlite3.connect(path, timeout=0.1, isolation_level=None)
            try:
                db.execute("PRAGMA journal_mode=WAL")
                db.execute("PRAGMA synchronous=NORMAL")
                db.execute("CREATE TABLE IF NOT EXISTS Pace (Id INTEGER PRIMARY KEY, Rate INTEGER, Next REAL, Seen REAL)")
                # 初始化全部成功后才发布连接，失败重试不会留下半初始化状态。
                db.execute("PRAGMA busy_timeout=5000")
                self._state = db
                return
            except BaseException as error:
                db.close()
                code = getattr(error, "sqlite_errorcode", 0)
                remaining = deadline - time.monotonic()
                # 切换 WAL 时并不总会调用 SQLite 的 busy handler，需显式处理启动竞争。
                if (isinstance(error, sqlite3.OperationalError)
                        and (code & 0xFF) in (sqlite3.SQLITE_BUSY, sqlite3.SQLITE_LOCKED)
                        and remaining > 0):
                    time.sleep(min(0.05, remaining))
                    continue
                raise

    def _reserve(self, size, rate):
        if self._state is None:
            self._open_state()
        db = self._state
        db.execute("BEGIN IMMEDIATE")
        try:
            now = time.monotonic()
            row = db.execute("SELECT Rate, Next, Seen FROM Pace WHERE Id = 1").fetchone()
            # 修改速度或系统重启后重新计时；每次只预留约 0.1 秒，异常退出不会留下长时间占额。
            start = max(now, row[1]) if row and row[0] == rate and row[2] <= now else now
            deadline = start + size / rate
            db.execute("INSERT OR REPLACE INTO Pace VALUES (1, ?, ?, ?)", (rate, deadline, now))
            db.execute("COMMIT")
            return deadline
        except BaseException:
            db.execute("ROLLBACK")
            raise

    def consume(self, size):
        while size > 0:
            rate = self._rate()
            if rate == 0:
                return
            amount = min(size, max(1, rate // 10))
            deadline = self._reserve(amount, rate)
            while True:
                # 等待过程中持续读取设置，让正在下载的任务也能响应关闭或调整限速。
                if self._rate() != rate:
                    break
                delay = deadline - time.monotonic()
                if delay <= 0:
                    size -= amount
                    break
                time.sleep(min(delay, 0.1))


@contextmanager
def limit_gallery_downloads(limiter):
    if not limiter.config:
        yield
        return
    from gallery_dl.downloader.http import HttpDownloader

    originals = {name: getattr(HttpDownloader, name) for name in ("receive", "_receive_rate")}

    def wrap(receive):
        def paced_receive(self, fp, content, bytes_total, bytes_start):
            def chunks():
                for data in content:
                    limiter.consume(len(data))
                    yield data
            return receive(self, fp, chunks(), bytes_total, bytes_start)
        return paced_receive

    try:
        for name, receive in originals.items():
            setattr(HttpDownloader, name, wrap(receive))
        yield
    finally:
        for name, receive in originals.items():
            setattr(HttpDownloader, name, receive)
