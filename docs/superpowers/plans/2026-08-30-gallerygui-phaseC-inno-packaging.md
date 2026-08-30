# GalleryGUI Phase C（缩减版：仅 Inno 打包）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans. Steps use checkbox (`- [ ]`) syntax.

**Goal:** 产出一个 Inno Setup 安装包（GalleryGUI 应用 + 引擎捆绑），本机可构建、装完即用。

**范围裁定（用户指示 2026-08-30）：** 本阶段**只做** Inno 打包；跳过 GitHub Actions CI、应用内检查更新、gallery-dl 热更新、任务栏进度、resw 本地化、E2E archive 回归防护、通知队列、时区语义修复——全部转入后续阶段待办（见文末清单）。引擎组装（Python embeddable + gallery-dl + runner）属于"打包"的必要组成，包含在内；GPL 合规只做静态文件（许可证随包），不做关于页检查更新等动态功能。

**Spec:** `docs/superpowers/specs/2026-08-29-gallerygui-design.md`（§2.3 部署形态、§10 GPL）

## Global Constraints

- 安装目标 `{localappdata}\Programs\GalleryGUI`（每用户，无管理员）；`PrivilegesRequired=lowest`。
- 引擎布局必须匹配 Phase A 代码：`engine\python\python.exe`、`engine\runner.py`、`engine\sites\`、`engine\site-packages\`（`RunnerEngineOptions.GalleryDlPath` 注册值即 `EngineDir\site-packages`，AppPaths.PythonExe 即 `EngineDir\python\python.exe`）。
- gallery-dl 用 PyPI 固定版本 `gallery-dl==1.32.10`（与仓库 vendored 源码一致，runner 已在该版本验证），脚本参数可覆盖。
- 首启播种：安装器把 engine 直接装到安装目录；应用**不再需要** appdata 播种——但 Phase A 代码的播种逻辑尚未实现（B1 DEBUG 覆盖、RELEASE 无覆盖），故安装版引擎必须放安装目录且 `%LOCALAPPDATA%\GalleryGUI\engine` 不存在时……**定稿**：安装目录含引擎；AppPaths 指向 appdata 引擎。冲突！修正：本阶段在 `App.OnLaunched`（RELEASE 也生效）补"播种"：若 `paths.PythonExe` 不存在且安装目录存在 `engine\`（用 `AppContext.BaseDirectory\engine` 探测），复制安装目录 engine → appdata engine（首启约 60MB 一次性复制）。这保持 Phase A 代码不动且引擎可热更新（Phase C 后续）。
- GPL：随包附 `gallery-dl/LICENSE`（GPL-3.0 全文）+ `THIRD-PARTY.md`（组件清单与源码链接）+ 应用 `LICENSE`。
- 产物：`dist\GalleryGUI-Setup-<version>.exe`；`dist/` 已 gitignore。
- 构建条件（已探测）：网络可用、Python 3.14 可用、Inno 未装（`winget install -e --id JRSoftware.InnoSetup`，装后 ISCC 位于 `C:\Program Files (x86)\Inno Setup 6\ISCC.exe`）。

## Tasks（单实现者一次派发完成全部；单审查者复核）

### Task C1: 构建脚本与安装器

**Files:**
- Create: `scripts/build-engine.ps1`、`scripts/build.ps1`、`installer/gallerygui.iss`、`THIRD-PARTY.md`、`LICENSE`（应用，GPL-3.0 全文）
- Modify: `src/GalleryGUI.App/App.xaml.cs`（RELEASE 播种逻辑，见上）
- 产物（不入库）：`dist/`

**build-engine.ps1 要点**：参数 `-Version 1.32.10`；下载 `https://www.python.org/ftp/python/3.13.7/python-3.13.7-embed-amd64.zip` → 解压 `dist\engine\python\`；用系统 pip `pip install --target dist\engine\site-packages "gallery-dl==<Version>"`；补丁 `python313._pth`：追加 `..\site-packages` 行并取消 `import site` 注释；拷贝 `engine\runner.py`、`engine\sites\` → `dist\engine\`；写 `dist\engine\engine.json`（版本戳）。校验：`dist\engine\python\python.exe -c "import gallery_dl"` 成功 + `runner.py hello` 输出 protocol 1。
**build.ps1 要点**：调 build-engine → `dotnet publish src/GalleryGUI.App -c Release -r win-x64 --self-contained true -o dist\app` → 拷贝 LICENSE/THIRD-PARTY → `dist\`。
**gallerygui.iss 要点**：`PrivilegesRequired=lowest`、`DefaultDirName={localappdata}\Programs\GalleryGUI`、`AppVersion` 从参数；Files：`dist\app\*` → `{app}`（recursive）、`dist\engine\*` → `{app}\engine`、licenses → `{app}\licenses`；图标/快捷方式：开始菜单 `GalleryGUI`；`Uninstall` 不删 `{localappdata}\GalleryGUI`（用户数据，卸载时提示保留）。

### Task C2: 播种逻辑（App.xaml.cs）

`ApplyRuntimeSeeding()`（非 DEBUG 也生效）：`if (!File.Exists(paths.PythonExe) && Directory.Exists(Path.Combine(AppContext.BaseDirectory, "engine")))` → 递归复制到 `paths.EngineDir`（幂等：目录已存在则跳过；复制失败记日志不崩）。在 OnLaunched 中 `paths.EnsureCreated()` 后调用（DEBUG 覆盖之前）。测试不可行（文件系统大对象），以构建 + 手测清单附录验证。

### 验收

1. `powershell -File scripts/build.ps1` 全流程成功，`dist\engine\python\python.exe runner.py hello`（在 dist\engine 下）输出 protocol 1
2. ISCC 编译出 `dist\GalleryGUI-Setup-*.exe`
3. `dotnet test src/GalleryGUI.sln` 仍 113/113（播种改动不破坏）
4. 手测清单追加"安装包验证"节（安装→启动→卸载保留数据）留人工执行
5. Commit：`feat(build): Inno 打包（引擎组装/发布/安装器/播种）+ GPL 资产`

## 后续阶段待办（用户裁定延后，勿丢）

CI、应用内检查更新、gallery-dl 热更新（落地前必须补 E2E archive 回归防护）、任务栏进度、resw 本地化、通知优先级队列、时区语义修复、来源保留词收紧、SortDesc=false 测试、B5 flaky 事件驱动根治、卸载时可选清理用户数据。
