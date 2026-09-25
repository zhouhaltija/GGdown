"""X (Twitter) 站点命令实现，复用 gallery-dl 内部 API。"""
from urllib.parse import unquote

from sites import AuthError, parse_screen_name


def make_api(cookies_path):
    from gallery_dl import config, extractor
    from gallery_dl.extractor.twitter import TwitterAPI
    config.set(("extractor", "twitter"), "cookies", cookies_path)
    # URL 中的 handle 无意义：user_following/user_by_screen_name 都显式传入用户名
    ext = extractor.find("https://x.com/__ggdown__/following")
    # extractor.cookies 等属性在基类 initialize() 中才创建（idempotent），find() 不会调用
    ext.initialize()
    return TwitterAPI(ext), ext


def map_transformed(u):
    """gallery-dl _transform_user 结果 -> 协议 user 事件字段（纯函数，可测）。"""
    banner = u.get("profile_banner") or None
    return {
        "rest_id": str(u["id"]),
        "screen_name": u.get("name"),
        "display_name": u.get("nick"),
        "avatar_url": u.get("profile_image"),
        "banner_url": banner if banner else None,
        "bio": u.get("description"),
        "followers_count": u.get("followers_count"),
        "media_count": u.get("media_count"),
    }


def _raw_to_user(ext, raw):
    return map_transformed(ext._transform_user(raw))


def _network_error(exc):
    text = f"{type(exc).__name__}: {exc}"
    if "Missing dependencies for SOCKS" in text or "InvalidSchema" in text:
        return "缺少 SOCKS 依赖。开发环境请执行: python -m pip install PySocks"
    if "ProxyError" in text or "Unable to connect to proxy" in text or "SSLEOF" in text:
        return (
            "代理连不上。本机 v2rayN/Clash：SOCKS 口（常见 10808）选 socks5h，"
            "HTTP 口（常见 10809/7890）选 http，不要选 https。"
        )
    return f"网络请求失败: {exc}"


def parse_twid(value):
    """Netscape twid cookie → rest_id。接受 u=123 / u%3D123 / \"u=123\"。"""
    if not value:
        return None
    s = unquote(str(value)).strip().strip('"')
    if s.startswith("u="):
        rest_id = s[2:].strip()
        return rest_id or None
    return None


def whoami(cookies_path):
    """用 twid + GraphQL UserByRestId 识别当前登录账号。

    X 已下线 v1.1 account/settings.json（404），不能再走 REST settings。
    """
    api, ext = make_api(cookies_path)
    jar = ext.cookies
    domain = ext.cookies_domain
    if not jar.get("auth_token", domain=domain):
        raise AuthError("cookie 无效：缺少 auth_token（请从已登录的 x.com 导出 Netscape cookies.txt）")
    rest_id = parse_twid(jar.get("twid", domain=domain) or jar.get("twid"))
    if not rest_id:
        raise AuthError("cookie 无效：缺少 twid")
    try:
        raw = api.user_by_rest_id(rest_id)
    except Exception as e:
        raise RuntimeError(_network_error(e)) from e
    if not raw or raw.get("__typename") == "UserUnavailable":
        raise AuthError("cookie 无效或账号不可用")
    u = ext._transform_user(raw)
    if not u.get("name"):
        raise AuthError("cookie 无效或已过期")
    return {"screen_name": u["name"], "display_name": u.get("nick"), "rest_id": rest_id}


def list_following(cookies_path, me):
    screen_name = me["screen_name"] if isinstance(me, dict) else me
    api, ext = make_api(cookies_path)
    for raw in api.user_following(screen_name):
        if "rest_id" not in raw:
            continue
        yield _raw_to_user(ext, raw)


def user_info(cookies_path, input_value):
    api, ext = make_api(cookies_path)
    screen = parse_screen_name(input_value)
    return _raw_to_user(ext, api.user_by_screen_name(screen))
