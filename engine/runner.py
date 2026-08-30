#!/usr/bin/env python3
"""GalleryGUI 引擎适配器。stdout 输出 JSONL 事件（协议 v1），诊断走 stderr。"""
import argparse
import json
import logging
import os
import re
import sys
import traceback

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from sites import AuthError, EventHandler, classify_error, emit, emit_hello


class EventOutput:
    """替代 gallery_dl.output 的下载事件发射器（接口同 output.NullOutput）。"""

    def __init__(self, counters):
        self.counters = counters

    def start(self, path):
        m = re.match(r"^(\d+)_", os.path.basename(path))
        self.counters["started"] += 1
        emit("file-start", path=path, item_id=m.group(1) if m else None)

    def skip(self, path):
        self.counters["skipped"] += 1
        emit("file-skip", path=path)

    def success(self, path):
        self.counters["done"] += 1
        # 审查 Important-3：file-done 必须带 size，否则落库 FileSize 为空、带宽统计/去重信息缺失；
        # 读取失败（文件被移动等）回退 null，C# JsonlParser 对 null size 安全
        try:
            size = os.path.getsize(path)
        except OSError:
            size = None
        emit("file-done", path=path, size=size)

    def progress(self, bytes_total, bytes_downloaded, bytes_per_second):
        pass


class CountingLogHandler(EventHandler):
    """ERROR 级 gallery-dl 日志计入 failed：output.NullOutput 接口无失败回调，
    文件失败只走日志通道，故在 log 桥接处补计。"""

    def __init__(self, counters):
        super().__init__(level=logging.WARNING)
        self.counters = counters

    def emit(self, record):
        if record.levelno >= logging.ERROR:
            self.counters["failed"] += 1
        super().emit(record)


def cmd_whoami(args):
    from sites import twitter
    emit_hello()
    info = twitter.whoami(args.cookies)
    emit("account", screen_name=info["screen_name"], display_name=info.get("display_name"))


def cmd_list_following(args):
    from sites import twitter
    emit_hello()
    me = twitter.whoami(args.cookies)["screen_name"]
    n = 0
    for user in twitter.list_following(args.cookies, me):
        emit("user", **user)
        n += 1
    emit("end", total=n)


def cmd_user_info(args):
    from sites import twitter
    emit_hello()
    user = twitter.user_info(args.cookies, args.input)
    emit("user", **user)
    emit("end", total=1)


def cmd_download(args):
    from gallery_dl import config, job, output
    from sites import apply_options
    emit_hello()
    with open(args.job, encoding="utf-8") as f:
        spec = json.load(f)
    apply_options(spec.get("options", {}))
    config.set(("extractor", args.site), "cookies", args.cookies)  # CLI 参数覆盖，双保险
    counters = {"started": 0, "done": 0, "skipped": 0, "failed": 0}
    output.select = lambda: EventOutput(counters)
    # gallery-dl 的 logger（"gallery-dl" 及类别名如 "twitter"）均向 root 传播；
    # 按库使用时 setup_logging 不会被触发，root 上无冲突 handler
    logging.getLogger().addHandler(CountingLogHandler(counters))
    for url in spec["urls"]:
        emit("url-start", url=url)
        job.DownloadJob(url).run()
    total = counters["done"] + counters["skipped"] + counters["failed"]
    emit("job-done", total=total, skipped=counters["skipped"], failed=counters["failed"])


def main(argv=None):
    p = argparse.ArgumentParser(prog="runner")
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("hello")
    for name in ("whoami", "list-following", "user-info", "download"):
        sp = sub.add_parser(name)
        sp.add_argument("--site", default="twitter")
        sp.add_argument("--cookies", required=True)
        if name == "user-info":
            sp.add_argument("--input", required=True)
        if name == "download":
            sp.add_argument("--job", required=True)
    args = p.parse_args(argv)

    if args.cmd == "hello":
        emit_hello()
        return 0
    try:
        {"whoami": cmd_whoami,
         "list-following": cmd_list_following,
         "user-info": cmd_user_info,
         "download": cmd_download}[args.cmd](args)
        return 0
    except AuthError as e:
        emit("fatal", msg=str(e), kind="auth")
        return 2
    except Exception as e:
        # 审查 Important-5：下载中途认证失效须带 kind="auth"，C# 侧据 fatal kind=auth 触发账号置 Invalid
        emit("fatal", msg=f"{type(e).__name__}: {e}", kind=classify_error(e))
        traceback.print_exc(file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
