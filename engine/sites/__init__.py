"""runner 共享基座：事件发射、配置展开、输入解析、日志桥接。"""
import json
import logging
import os
import re
import sys

PROTOCOL = 1
RUNNER_VERSION = "1.0.0"


def configure_stdio():
    """Windows 默认 GBK，gallery-dl 用户名里的 ⋆ 等字符会 UnicodeEncodeError。"""
    os.environ.setdefault("PYTHONUTF8", "1")
    os.environ.setdefault("PYTHONIOENCODING", "utf-8")
    for stream in (sys.stdout, sys.stderr):
        reconf = getattr(stream, "reconfigure", None)
        if reconf is None:
            continue
        try:
            reconf(encoding="utf-8", errors="replace")
        except Exception:
            pass


configure_stdio()


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


def classify_error(e):
    """把未知异常归类为协议 fatal 事件的 kind（None=普通错误，"auth"=认证失效）。

    审查 Important-5：下载中途认证失效原本不带 kind="auth"，账号不会被置 Invalid。
    注意不能把 gallery_dl.exception.AuthorizationError 一律映射为 auth——它还涵盖账号被封/
    受保护内容等情形，误映射会把有效账号标成 Invalid；字符串匹配只针对认证失效特征，
    isinstance 保留（AuthenticationError 是认证失效的专用类型）。惰性 import 保证
    gallery-dl 缺失时 hello 命令仍可用。
    """
    try:
        from gallery_dl import exception as gdl_exc
        if isinstance(e, gdl_exc.AuthenticationError) or "Could not authenticate you" in str(e):
            return "auth"
    except Exception:
        pass
    return None


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


def apply_proxy_from_env():
    """把进程环境里的代理写入 gallery-dl，覆盖 extractor / downloader。

    C# 在启动 python 时设置 HTTP_PROXY/HTTPS_PROXY/ALL_PROXY；whoami 的裸
    requests.get 走 requests 默认 trust_env，gallery-dl 再显式 set 以免
    个别路径忽略环境变量。
    """
    import os
    url = os.environ.get("ALL_PROXY") or os.environ.get("HTTPS_PROXY") or os.environ.get("HTTP_PROXY")
    if not url:
        return
    from gallery_dl import config
    config.set(("extractor",), "proxy", url)
    config.set(("downloader",), "proxy", url)
    config.set(("downloader", "http"), "proxy", url)


def load_site(name):
    """按 --site 懒加载站点模块。未知站点抛 ValueError。"""
    key = (name or "").strip().lower()
    if key == "twitter":
        from sites import twitter
        return twitter
    if key == "pixiv":
        from sites import pixiv
        return pixiv
    if key == "douyin":
        from sites import douyin
        return douyin
    raise ValueError(f"unknown site: {name!r}")


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
