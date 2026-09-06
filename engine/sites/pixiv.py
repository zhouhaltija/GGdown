"""Pixiv 站点命令实现，复用 gallery-dl PixivAppAPI。"""
import os
import re

from sites import AuthError

_USER_ID_RE = re.compile(
    r"(?:https?://)?(?:www\.|touch\.)?ph?ixiv\.net/"
    r"(?:(?:en/)?users/|member\.php\?id=)(\d+)",
    re.IGNORECASE,
)


def refresh_token_path(cookies_path):
    return os.path.join(os.path.dirname(os.path.abspath(cookies_path)), "refresh-token.txt")


def read_refresh_token(cookies_path):
    path = refresh_token_path(cookies_path)
    if not os.path.isfile(path):
        return None
    with open(path, encoding="utf-8") as f:
        token = f.read().strip()
    return token or None


def parse_user_id(value):
    v = (value or "").strip()
    if not v:
        raise ValueError("输入为空")
    if re.fullmatch(r"\d+", v):
        return v
    m = _USER_ID_RE.search(v)
    if m:
        return m.group(1)
    raise ValueError(f"无法识别 Pixiv 用户 ID 或链接: {value!r}")


def map_user(u):
    urls = u.get("profile_image_urls") or {}
    account = u.get("account")
    rest_id = str(u["id"])
    return {
        "rest_id": rest_id,
        "screen_name": account or rest_id,
        "display_name": u.get("name"),
        "avatar_url": urls.get("medium") or urls.get("px_170x170"),
    }


def make_api(cookies_path):
    from gallery_dl import config, extractor
    from gallery_dl.extractor.pixiv import PixivAppAPI
    token = read_refresh_token(cookies_path)
    config.set(("extractor", "pixiv"), "cookies", cookies_path)
    config.set(("extractor", "pixiv-novel"), "cookies", cookies_path)
    if token:
        config.set(("extractor", "pixiv"), "refresh-token", token)
        config.set(("extractor", "pixiv-novel"), "refresh-token", token)
    ext = extractor.find("https://www.pixiv.net/users/1/artworks")
    ext.initialize()
    return PixivAppAPI(ext), ext


def whoami(cookies_path):
    if not read_refresh_token(cookies_path):
        raise AuthError("缺少 refresh-token（请运行 gallery-dl oauth:pixiv 后填入导入对话框）")
    api, ext = make_api(cookies_path)
    jar = ext.cookies
    domain = ext.cookies_domain
    if not jar.get("PHPSESSID", domain=domain) and not jar.get("PHPSESSID"):
        raise AuthError("cookie 无效：缺少 PHPSESSID（请从已登录的 pixiv.net 导出 Netscape cookies.txt）")
    try:
        api.login()
    except AuthError:
        raise
    except Exception as e:
        text = str(e)
        if "refresh" in text.lower() or "authenticate" in text.lower() or type(e).__name__ == "AuthenticationError":
            raise AuthError(f"Pixiv 认证失败: {e}") from e
        raise
    user = api.user
    if not user or not user.get("id"):
        raise AuthError("cookie 或 refresh-token 无效")
    return {
        "rest_id": str(user["id"]),
        "screen_name": user.get("account") or str(user["id"]),
        "display_name": user.get("name"),
    }


def list_following(cookies_path, me):
    user_id = me["rest_id"] if isinstance(me, dict) else me
    api, _ext = make_api(cookies_path)
    seen = set()
    for restrict in ("public", "private"):
        for preview in api.user_following(user_id, restrict):
            raw = preview.get("user") if isinstance(preview, dict) else None
            if not raw:
                continue
            mapped = map_user(raw)
            if mapped["rest_id"] in seen:
                continue
            seen.add(mapped["rest_id"])
            yield mapped


def user_info(cookies_path, input_value):
    user_id = parse_user_id(input_value)
    api, _ext = make_api(cookies_path)
    detail = api.user_detail(user_id)
    raw = detail.get("user", detail) if isinstance(detail, dict) else detail
    return map_user(raw)
