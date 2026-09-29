import sqlite3
import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

from migrate_pixiv_downloads import migrate_pixiv_downloads


def create_db(path, file_paths, active=False):
    with sqlite3.connect(path) as db:
        db.execute("CREATE TABLE Jobs (Id INTEGER PRIMARY KEY, Status INTEGER NOT NULL)")
        db.execute("CREATE TABLE Files (Id INTEGER PRIMARY KEY, FilePath TEXT NOT NULL)")
        db.executemany("INSERT INTO Files (FilePath) VALUES (?)", [(str(p),) for p in file_paths])
        if active:
            db.execute("INSERT INTO Jobs (Status) VALUES (1)")


def test_migrates_existing_pixiv_author_files_and_history_paths(tmp_path):
    root = tmp_path / "downloads"
    artwork = root / "123 creator" / "artworks" / "1.jpg"
    novel = root / "123 creator" / "novels" / "2.txt"
    artwork.parent.mkdir(parents=True)
    novel.parent.mkdir(parents=True)
    artwork.write_bytes(b"art")
    novel.write_bytes(b"novel")
    unrelated = root / "pixiv" / "456 other" / "artworks" / "3.jpg"
    unrelated.parent.mkdir(parents=True)
    unrelated.write_bytes(b"other")
    db_path = tmp_path / "pixiv.db"
    create_db(db_path, [artwork, novel, unrelated])

    assert migrate_pixiv_downloads(root, db_path) == 2

    assert (root / "pixiv" / "123 creator" / "artworks" / "1.jpg").read_bytes() == b"art"
    assert (root / "pixiv" / "123 creator" / "novels" / "2.txt").read_bytes() == b"novel"
    assert unrelated.read_bytes() == b"other"
    assert not (root / "123 creator").exists()
    with sqlite3.connect(db_path) as db:
        paths = [Path(p) for (p,) in db.execute("SELECT FilePath FROM Files ORDER BY Id")]
    assert paths == [root / "pixiv" / "123 creator" / "artworks" / "1.jpg",
                     root / "pixiv" / "123 creator" / "novels" / "2.txt", unrelated]
    assert db_path.with_suffix(".db.pre-folder-migration.bak").exists()
    assert migrate_pixiv_downloads(root, db_path) == 0


def test_refuses_to_move_while_pixiv_job_is_active(tmp_path):
    root = tmp_path / "downloads"
    file = root / "123 creator" / "artworks" / "1.jpg"
    file.parent.mkdir(parents=True)
    file.write_bytes(b"art")
    db_path = tmp_path / "pixiv.db"
    create_db(db_path, [file], active=True)

    with pytest.raises(RuntimeError, match="仍有下载任务"):
        migrate_pixiv_downloads(root, db_path)

    assert file.exists()


def test_refuses_conflicting_destination_before_moving_anything(tmp_path):
    root = tmp_path / "downloads"
    file = root / "123 creator" / "artworks" / "1.jpg"
    file.parent.mkdir(parents=True)
    file.write_bytes(b"art")
    conflict = root / "pixiv" / "123 creator"
    conflict.mkdir(parents=True)
    db_path = tmp_path / "pixiv.db"
    create_db(db_path, [file])

    with pytest.raises(FileExistsError):
        migrate_pixiv_downloads(root, db_path)

    assert file.exists()
