# Third-Party Components

GGdown bundles the following third-party components. Their sources and
licenses are reproduced below / shipped alongside.

## gallery-dl

- Version: 1.32.10 (pinned; installed from PyPI into the bundled engine's
  `site-packages`)
- Source: https://github.com/mikf/gallery-dl
- License: GNU General Public License v2.0 (GPL-2.0). Full text shipped as
  `licenses/gallery-dl-LICENSE` in the installed application.
- Homepage: https://github.com/mikf/gallery-dl

## Python (embeddable distribution)

- Version: 3.13.7 (Windows embeddable package, amd64)
- Source: https://www.python.org/ftp/python/3.13.7/python-3.13.7-embed-amd64.zip
- License: Python Software Foundation License (PSF-2.0).
  https://docs.python.org/3/license.html

## DouK-Downloader / TikTokDownloader

- Version: 5.8, commit `473c90ff70c663cfb69310fff2b8d5192f200661`.
- Source: https://github.com/JoeanAmier/TikTokDownloader
- License: GNU General Public License v3.0 (GPL-3.0). Full text shipped as
  `licenses/TikTokDownloader-LICENSE`.
- The release contains the library's `src/` and `locale/` directories.
  Its `setup.py` builds a standalone application, so GGdown imports the
  source modules directly.

## Bundled Python packages

`engine/uv.lock` fixes the release dependency versions. The installer
includes each wheel's license file in
`engine/site-packages/<package>.dist-info/`. The package and license list
for this lock file is:

| License | Packages (version) |
| --- | --- |
| Apache-2.0 | aiofiles 25.1.0; requests 2.34.2 |
| BSD (see bundled texts) | colorama 0.4.6; emoji 2.16.0; PySocks 1.7.1; pyperclip 1.11.0 |
| BSD-2-Clause | Pygments 2.21.0 |
| BSD-3-Clause | click 8.5.0; idna 3.20; lxml 6.1.3; pycparser 3.0; starlette 1.7.0; uvicorn 0.54.0 |
| MIT | aiosqlite 0.22.1; annotated-doc 0.0.5; annotated-types 0.8.0; anyio 4.15.1; charset-normalizer 3.5.1; curl-cffi 0.16.3; et-xmlfile 2.0.0; fastapi 0.141.1; h11 0.16.0; javascript 1!1.2.6; markdown-it-py 4.2.0; mdurl 0.1.2; openpyxl 3.1.5; pydantic 2.13.5; pydantic-core 2.46.5; rich 15.0.0; typing-inspection 0.4.4; urllib3 2.8.0 |
| MIT-0 | cffi 2.1.1 |
| MPL-2.0 | certifi 2026.7.22 |
| PSF-2.0 | typing-extensions 4.16.0 |

The gallery-dl wheel also retains its license in `site-packages`; the
separate `licenses/gallery-dl-LICENSE` is copied from the same pinned wheel.

## Release build prerequisites

Install .NET 8 SDK, PowerShell 7, `uv`, `git`, and Inno Setup 6 or 7.
Run `pwsh scripts/build.ps1 -AppVersion 1.0.0`. The script automatically
fetches `https://github.com/JoeanAmier/TikTokDownloader` into
`third_party/TikTokDownloader` at commit
`473c90ff70c663cfb69310fff2b8d5192f200661`; an existing checkout must match
that commit and have no tracked modifications. The build uses uv's
Python 3.13.7 and `engine/uv.lock` to install hash-locked dependencies into
the bundled Python environment, and copies the wheel's gallery-dl license.
It includes all top-level engine Python modules, including `rate_limit.py`.
End users do not need uv or a separate Python installation.

Tags such as `v1.0.0` trigger `.github/workflows/release.yml`: tests,
Windows x64 publishing, installer compilation, SHA-256 generation, and
GitHub Release asset upload. See `README.md` for release commands.

## Simple Icons

- The X path used in XAML and the pixiv SVG in `src/GGdown.App/Assets/Brands/`
  are from https://simpleicons.org (CC0-1.0).

## GGdown

- License: GNU General Public License v3.0 (GPL-3.0). Full text shipped as
  `licenses/LICENSE` in the installed application and in the repository root.
