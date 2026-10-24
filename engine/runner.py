#!/usr/bin/env python3
"""GGdown 引擎适配器。stdout 输出 JSONL 事件（协议 v1），诊断走 stderr。"""
import argparse
import json
import logging
import os
import re
import sys
import traceback
from contextlib import nullcontext

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from sites import AuthError, EventHandler, apply_proxy_from_env, classify_error, emit, emit_hello, load_site


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


def create_download_job(base, counters, extraction_errors):
    """在 gallery-dl 的最终文件失败钩子计数，重试日志不代表文件失败。"""
    failed_paths = set()

    class DownloadJob(base):
        def dispatch(self, messages):
            from gallery_dl import exception
            try:
                return super().dispatch(messages)
            except exception.ControlException:
                raise
            except Exception as error:
                extraction_errors[:] = [error]
                raise

        def initialize(self, *args, **kwargs):
            super().initialize(*args, **kwargs)
            self.hooks = dict(self.hooks)
            self.hooks["error"] = [self.file_failed, *self.hooks.get("error", ())]

        def file_failed(self, pathfmt):
            path = pathfmt.path
            key = os.path.normcase(os.path.abspath(path))
            if key in failed_paths:
                return
            failed_paths.add(key)
            counters["failed"] += 1
            match = re.match(r"^(\d+)_", os.path.basename(path))
            item_id = match.group(1) if match else None
            if item_id is None:
                for name in ("tweet_id", "id", "illust_id"):
                    if pathfmt.kwdict.get(name) is not None:
                        item_id = str(pathfmt.kwdict[name])
                        break
            emit("file-failed", path=path, item_id=item_id,
                 msg="文件下载失败，详情见引擎诊断", kind="download")

    return DownloadJob


def cmd_whoami(args):
    site = load_site(args.site)
    emit_hello()
    info = site.whoami(args.cookies)
    emit("account", screen_name=info["screen_name"], display_name=info.get("display_name"),
         rest_id=info.get("rest_id"))


def cmd_list_following(args):
    site = load_site(args.site)
    emit_hello()
    info = site.whoami(args.cookies)
    n = 0
    for user in site.list_following(args.cookies, info):
        emit("user", **user)
        n += 1
    emit("end", total=n)


def cmd_user_info(args):
    site = load_site(args.site)
    emit_hello()
    user = site.user_info(args.cookies, args.input)
    emit("user", **user)
    emit("end", total=1)


def cmd_download(args):
    if args.site == "douyin":
        site = load_site(args.site)
        emit_hello()
        with open(args.job, encoding="utf-8") as f:
            spec = json.load(f)
        site.download(args.cookies, spec, emit)
        return
    from gallery_dl import config, job, output
    from sites import apply_options
    emit_hello()
    with open(args.job, encoding="utf-8") as f:
        spec = json.load(f)
    if args.site == "twitter":
        from sites.twitter import validate_cookies
        validate_cookies(args.cookies)
    options = dict(spec.get("options", {}))
    from rate_limit import SharedRateLimiter, limit_gallery_downloads
    rate_config = options.pop("ggdown-rate-limit", None)
    apply_options(options)
    config.set(("extractor", args.site), "cookies", args.cookies)  # CLI 参数覆盖，双保险
    counters = {"started": 0, "done": 0, "skipped": 0, "failed": 0}
    # gallery-dl 的 logger（"gallery-dl" 及类别名如 "twitter"）均向 root 传播；
    # 按库使用时 setup_logging 不会被触发，root 上无冲突 handler
    handler = EventHandler(level=logging.WARNING)
    logging.getLogger().addHandler(handler)
    original_select = output.select
    output.select = lambda: EventOutput(counters)
    extraction_errors = []
    download_job = create_download_job(job.DownloadJob, counters, extraction_errors)
    try:
        with SharedRateLimiter(rate_config) as limiter, limit_gallery_downloads(limiter):
            for url in spec["urls"]:
                emit("url-start", url=url)
                status = download_job(url).run()
                # run() 返回位标记而非失败文件数；4 表示最终文件下载失败。
                # 其他提取/格式/网络错误不能在没有完整结果时报告成功。
                if status and (status & ~4 or counters["failed"] == 0):
                    if extraction_errors:
                        raise extraction_errors[-1]
                    raise RuntimeError(f"gallery-dl 未能完成提取（状态码 {status}）")
    finally:
        output.select = original_select
        logging.getLogger().removeHandler(handler)
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
    if args.cmd != "hello":
        apply_proxy_from_env()

    if args.cmd == "hello":
        emit_hello()
        return 0
    try:
        if args.site == "pixiv":
            from sites.pixiv import guard_login_errors
            login_guard = guard_login_errors()
        else:
            login_guard = nullcontext()
        with login_guard:
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
