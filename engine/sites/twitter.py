"""X (Twitter) 站点命令实现，复用 gallery-dl 内部 API。"""
import requests

from sites import AuthError, parse_screen_name

SETTINGS_URL = "https://api.x.com/1.1/account/settings.json"


def make_api(cookies_path):
    from gallery_dl import config, extractor
    from gallery_dl.extractor.twitter import TwitterAPI
    config.set(("extractor", "twitter"), "cookies", cookies_path)
    # URL 中的 handle 无意义：user_following/user_by_screen_name 都显式传入用户名
    ext = extractor.find("https://x.com/__gallerygui__/following")
    # extractor.cookies 等属性在基类 initialize() 中才创建（idempotent），find() 不会调用
    ext.initialize()
    return TwitterAPI(ext), ext


def map_transformed(u):
    """gallery-dl _transform_user 结果 -> 协议 user 事件字段（纯函数，可测）。"""
    return {
        "rest_id": str(u["id"]),
        "screen_name": u.get("name"),
        "display_name": u.get("nick"),
        "avatar_url": u.get("profile_image"),
    }


def _raw_to_user(ext, raw):
    return map_transformed(ext._transform_user(raw))


def whoami(cookies_path):
    api, _ = make_api(cookies_path)
    try:
        resp = requests.get(SETTINGS_URL, headers=api.headers, timeout=30)
    except requests.RequestException as e:
        raise RuntimeError(f"网络请求失败: {e}") from e
    if resp.status_code in (401, 403):
        raise AuthError(f"cookie 无效或已过期（HTTP {resp.status_code}）")
    resp.raise_for_status()
    d = resp.json()
    return {"screen_name": d.get("screen_name"), "display_name": d.get("name")}


def list_following(cookies_path, screen_name):
    api, ext = make_api(cookies_path)
    for raw in api.user_following(screen_name):
        if "rest_id" not in raw:
            continue
        yield _raw_to_user(ext, raw)


def user_info(cookies_path, input_value):
    api, ext = make_api(cookies_path)
    screen = parse_screen_name(input_value)
    return _raw_to_user(ext, api.user_by_screen_name(screen))
