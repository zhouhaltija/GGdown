"""runner 共享基座：事件发射、配置展开、输入解析、日志桥接。"""
import json
import logging
import re
import sys

PROTOCOL = 1
RUNNER_VERSION = "1.0.0"


class AuthError(Exception):
    """Cookie 无效或登录态失效。"""


def emit(ev, **kw):
    kw["ev"] = ev
    sys.stdout.write(json.dumps(kw, ensure_ascii=False))
    sys.stdout.write("\n")
    sys.stdout.flush()


def emit_hello():
    try:
        from gallery_dl import version as gdl_version
        ver = gdl_version.__version__
    except Exception:
        ver = None
    emit("hello", protocol=PROTOCOL, runner=RUNNER_VERSION, gallery_dl=ver)


def walk_config(d, base=()):
    """把嵌套 dict 展开为 (path_tuple, value) 序列，供 gallery_dl.config.set 使用。"""
    for k, v in d.items():
        path = base + (k,)
        if isinstance(v, dict):
            yield from walk_config(v, path)
        else:
            yield path, v


def apply_options(options):
    from gallery_dl import config
    for path, value in walk_config(options or {}):
        # gallery-dl 1.32.x 的 config.set 签名为 set(path, key, value)
        config.set(path[:-1], path[-1], value)


def parse_screen_name(value):
    """接受 裸用户名 / @user / x.com/user / twitter.com/user(/任意后缀)。"""
    v = (value or "").strip()
    if not v:
        raise ValueError("输入为空")
    m = re.search(r"(?:https?://)?(?:www\.|mobile\.)?(?:twitter|x)\.com/@?([A-Za-z0-9_]{1,15})", v)
    if m:
        return m.group(1)
    m = re.fullmatch(r"@?([A-Za-z0-9_]{1,15})", v)
    if m:
        return m.group(1)
    raise ValueError(f"无法识别用户名或链接: {value!r}")


class EventHandler(logging.Handler):
    """gallery-dl 日志 → log 事件（默认 WARNING 及以上）。"""

    def __init__(self, level=logging.WARNING):
        super().__init__(level=level)

    def emit(self, record):
        if record.exc_info:
            msg = f"{record.getMessage()}: {record.exc_info[0].__name__}"
        else:
            msg = record.getMessage()
        emit("log", level=record.levelname.lower(), msg=msg)
