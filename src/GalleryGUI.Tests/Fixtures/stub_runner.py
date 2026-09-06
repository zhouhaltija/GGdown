#!/usr/bin/env python3
"""测试桩：按 runner 协议输出事件。"""
import json
import sys
import time


def emit(ev, **kw):
    kw["ev"] = ev
    sys.stdout.write(json.dumps(kw) + "\n")
    sys.stdout.flush()


def main():
    emit("hello", protocol=1, runner="stub-1.0.0", gallery_dl="stub-0.0.1")
    args = sys.argv[1:]
    cmd = args[0]
    if cmd == "hello":
        return 0
    if cmd == "whoami":
        if any("bad" in a for a in args):
            emit("fatal", msg="cookie 无效", kind="auth")
            return 2
        site = "twitter"
        if "--site" in args:
            site = args[args.index("--site") + 1]
        if site == "pixiv":
            emit("account", screen_name="pixiv_user", display_name="Pixiv User", rest_id="12345")
        else:
            emit("account", screen_name="stub_user", display_name="Stub User", rest_id="99")
        return 0
    if cmd == "list-following":
        emit("user", rest_id="1", screen_name="alice", display_name="Alice",
             avatar_url="https://x/a.png")
        emit("user", rest_id="2", screen_name="bob", display_name="Bob",
             avatar_url="https://x/b.png")
        emit("end", total=2)
        return 0
    if cmd == "user-info":
        emit("user", rest_id="42", screen_name="carol", display_name="Carol",
             avatar_url="https://x/c.png")
        emit("end", total=1)
        return 0
    if cmd == "download":
        job_path = args[args.index("--job") + 1]
        with open(job_path, encoding="utf-8") as f:
            spec = json.load(f)
        opts = spec.get("options") or {}
        if opts.get("fail"):
            emit("fatal", msg="boom")
            return 1
        if opts.get("crash"):
            sys.exit(3)  # 审查 Important-1：不发 fatal 直接非零退出，模拟 runner 被杀
        if opts.get("hang"):
            time.sleep(60)
        for url in spec["urls"]:
            emit("url-start", url=url)
            emit("file-start", path="D:/x/1_a_1.jpg", item_id="1")
            emit("file-done", path="D:/x/1_a_1.jpg", size=100)
            emit("file-skip", path="D:/x/2_b_1.jpg")
        emit("job-done", total=2, skipped=1, failed=0)
        return 0
    return 1


if __name__ == "__main__":
    sys.exit(main())
