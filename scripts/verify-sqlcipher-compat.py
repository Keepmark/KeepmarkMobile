#!/usr/bin/env python3
"""Check SQLCipher 4 old/new read+write compatibility on a synthetic encrypted DB."""
import argparse
import ctypes
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[1]
KEY = '0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef'


def probe(library, database, mode):
    native = ctypes.CDLL(str(library))
    native.sqlite3_open.argtypes = [ctypes.c_char_p, ctypes.POINTER(ctypes.c_void_p)]
    native.sqlite3_exec.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.POINTER(ctypes.c_char_p)]
    native.sqlite3_prepare_v2.argtypes = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int, ctypes.POINTER(ctypes.c_void_p), ctypes.c_void_p]
    native.sqlite3_step.argtypes = [ctypes.c_void_p]
    native.sqlite3_column_text.argtypes = [ctypes.c_void_p, ctypes.c_int]
    native.sqlite3_column_text.restype = ctypes.c_char_p
    native.sqlite3_finalize.argtypes = [ctypes.c_void_p]
    native.sqlite3_close.argtypes = [ctypes.c_void_p]
    db = ctypes.c_void_p()
    assert native.sqlite3_open(str(database).encode(), ctypes.byref(db)) == 0
    try:
        def execute(sql):
            error = ctypes.c_char_p()
            result = native.sqlite3_exec(db, sql.encode(), None, None, ctypes.byref(error))
            if result != 0:
                raise RuntimeError(error.value.decode() if error.value else f'SQLite error {result}')

        def scalar(sql):
            statement = ctypes.c_void_p()
            assert native.sqlite3_prepare_v2(db, sql.encode(), -1, ctypes.byref(statement), None) == 0
            try:
                assert native.sqlite3_step(statement) == 100
                value = native.sqlite3_column_text(statement, 0)
                return value.decode() if value else None
            finally:
                native.sqlite3_finalize(statement)

        execute(f'PRAGMA key = "x\'{KEY}\'"')
        if mode == 'new-write':
            execute("INSERT INTO Items(NodeId,Title,Url,IsFolder,SortOrder) VALUES(3,'Forward migration','https://forward.example.test',0,2)")
            assert scalar('SELECT COUNT(*) FROM Items') == '3'
        elif mode == 'old-write':
            assert scalar("SELECT COUNT(*) FROM ItemsFts WHERE ItemsFts MATCH 'Forward'") == '1'
            execute("INSERT INTO Items(NodeId,Title,Url,IsFolder,SortOrder) VALUES(4,'Rollback write','https://rollback.example.test',0,3)")
        else:
            assert scalar('SELECT COUNT(*) FROM Items') == '4'
            assert scalar("SELECT COUNT(*) FROM ItemsFts WHERE ItemsFts MATCH 'Rollback'") == '1'
        print(f'{mode}: SQLCipher {scalar("PRAGMA cipher_version")}, SQLite {scalar("SELECT sqlite_version()")}: OK')
    finally:
        native.sqlite3_close(db)


if __name__ == '__main__':
    # Isolated processes avoid ELF symbol interposition between old/new engines.
    if len(sys.argv) == 5 and sys.argv[1] == '--probe':
        probe(Path(sys.argv[2]), Path(sys.argv[3]), sys.argv[4])
    else:
        parser = argparse.ArgumentParser(description=__doc__)
        parser.add_argument('--old', type=Path, required=True, help='legacy libe_sqlcipher.so 4.5.2 (test only)')
        args = parser.parse_args()
        current = ROOT / 'artifacts/native/linux-x64/libsqlcipher.so'
        with tempfile.TemporaryDirectory(prefix='keepmark-native-compat-') as temp:
            db = Path(temp) / 'compat.db'
            shutil.copyfile(ROOT / 'tests/Keepmark.Core.Tests/TestData/sqlcipher-2.1.10.db', db)
            for library, mode in [(current, 'new-write'), (args.old.resolve(), 'old-write'), (current, 'new-read')]:
                subprocess.run([sys.executable, __file__, '--probe', str(library), str(db), mode], check=True)
