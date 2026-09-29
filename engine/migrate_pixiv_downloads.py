"""把旧版 Pixiv 作者目录移到 pixiv/ 下，并同步修正下载历史路径。"""

import argparse
import sqlite3
from collections import defaultdict
from pathlib import Path


def _plan(root: Path, db: sqlite3.Connection):
    active = db.execute("SELECT COUNT(*) FROM Jobs WHERE Status IN (0, 1)").fetchone()[0]
    if active:
        raise RuntimeError(f"Pixiv 仍有下载任务（{active} 个），请先等待任务结束")

    moves = defaultdict(list)
    for file_id, raw_path in db.execute("SELECT Id, FilePath FROM Files WHERE FilePath IS NOT NULL"):
        old = Path(raw_path).resolve()
        try:
            relative = old.relative_to(root)
        except ValueError:
            continue
        if len(relative.parts) < 3 or relative.parts[0].lower() == "pixiv":
            continue
        if relative.parts[1] not in {"artworks", "novels"}:
            continue
        source = root / relative.parts[0]
        target = root / "pixiv" / relative.parts[0]
        moves[(source, target)].append((file_id, old, root / "pixiv" / relative))

    target_root = (root / "pixiv").resolve()
    if not target_root.is_relative_to(root):
        raise ValueError("Pixiv 目标目录超出下载根目录")
    for (source, target), records in moves.items():
        if not source.is_dir() or not source.resolve().is_relative_to(root):
            raise FileNotFoundError(f"旧作者目录不存在或超出下载根目录：{source}")
        if target.exists():
            raise FileExistsError(f"目标作者目录已存在：{target}")
        if not target.resolve().is_relative_to(root):
            raise ValueError(f"目标作者目录超出下载根目录：{target}")
        for _, old, _ in records:
            if not old.is_file():
                raise FileNotFoundError(f"历史记录对应文件不存在：{old}")
    return moves


def migrate_pixiv_downloads(root: Path, db_path: Path) -> int:
    root = Path(root).resolve()
    db_path = Path(db_path).resolve()
    if not root.is_dir() or not db_path.is_file():
        raise FileNotFoundError("下载目录或 Pixiv 数据库不存在")

    with sqlite3.connect(db_path, timeout=10) as db:
        moves = _plan(root, db)
        if not moves:
            return 0

        backup_path = db_path.with_suffix(db_path.suffix + ".pre-folder-migration.bak")
        if backup_path.exists():
            raise FileExistsError(f"数据库备份已存在，请先核对：{backup_path}")
        with sqlite3.connect(backup_path) as backup:
            db.backup(backup)

        moved = []
        try:
            db.execute("BEGIN IMMEDIATE")
            # 获得写锁后重新确认，防止检查与移动之间有新任务启动。
            if db.execute("SELECT COUNT(*) FROM Jobs WHERE Status IN (0, 1)").fetchone()[0]:
                raise RuntimeError("Pixiv 有新的下载任务启动，迁移已停止")
            (root / "pixiv").mkdir(exist_ok=True)
            for source, target in moves:
                source.rename(target)
                moved.append((source, target))
            updates = [(str(new), file_id) for records in moves.values()
                       for file_id, _, new in records]
            cursor = db.executemany("UPDATE Files SET FilePath = ? WHERE Id = ?", updates)
            if cursor.rowcount != len(updates):
                raise RuntimeError("下载记录更新数量不符")
            db.commit()
            return len(updates)
        except Exception:
            db.rollback()
            for source, target in reversed(moved):
                target.rename(source)
            raise


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, required=True, help="软件下载目录")
    parser.add_argument("--database", type=Path, required=True, help="Pixiv 站点数据库")
    parser.add_argument("--apply", action="store_true", help="实际迁移；默认只检查")
    args = parser.parse_args()
    with sqlite3.connect(f"file:{args.database.resolve()}?mode=ro", uri=True) as con:
        plan = _plan(args.root.resolve(), con)
    print(f"待迁移 {len(plan)} 个作者目录、{sum(map(len, plan.values()))} 条文件记录")
    if args.apply:
        print(f"已更新 {migrate_pixiv_downloads(args.root, args.database)} 条文件记录")
