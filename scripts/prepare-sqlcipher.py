#!/usr/bin/env python3
"""Prepare pinned SQLCipher Community natives; no DB conversion or device access."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import tarfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]
NATIVE = ROOT / 'native/sqlcipher'
SOURCES = json.loads((NATIVE / 'sources.json').read_text())
CACHE = ROOT / 'artifacts/native/cache'


def download(spec, name):
    CACHE.mkdir(parents=True, exist_ok=True)
    path = CACHE / name
    if not path.exists():
        pending = path.with_suffix('.download')
        with urllib.request.urlopen(spec['url'], timeout=60) as response, pending.open('wb') as output:
            shutil.copyfileobj(response, output)
        pending.replace(path)
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    if digest != spec['sha256']:
        raise SystemExit(f'Hash mismatch for {path}; refusing to use it.')
    return path


def android():
    archive = download(SOURCES['android'], 'sqlcipher-android.aar')
    with zipfile.ZipFile(archive) as source:
        for abi, expected in SOURCES['android']['libraries'].items():
            data = source.read(f'jni/{abi}/libsqlcipher.so')
            if hashlib.sha256(data).hexdigest() != expected:
                raise SystemExit(f'Unexpected native binary for {abi}')
            path = NATIVE / 'android' / abi / 'libsqlcipher.so'
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
            print(f'Verified official Android {abi}: {path}')


def linux():
    if platform.system() != 'Linux' or platform.machine() not in ('x86_64', 'amd64'):
        raise SystemExit('The development native build currently supports Linux x64 only.')
    for tool in ('gcc', 'make', 'tclsh', 'pkg-config'):
        if shutil.which(tool) is None:
            raise SystemExit(f'Missing {tool}; install the native build prerequisites first.')
    subprocess.run(['pkg-config', '--exists', 'libcrypto'], check=True)
    archive = download(SOURCES['source'], 'sqlcipher-source.tar.gz')
    source_root = CACHE / f'sqlcipher-{SOURCES["engineVersion"]}'
    if not source_root.exists():
        with tarfile.open(archive) as source:
            source.extractall(CACHE, filter='data')
    build = ROOT / 'artifacts/native/build-sqlcipher'
    build.mkdir(parents=True, exist_ok=True)
    env = dict(os.environ)
    env['CFLAGS'] = '-O2 -fPIC -DSQLITE_HAS_CODEC -DSQLITE_EXTRA_INIT=sqlcipher_extra_init -DSQLITE_EXTRA_SHUTDOWN=sqlcipher_extra_shutdown'
    env['LDFLAGS'] = '-lcrypto -Wl,-z,relro,-z,now'
    subprocess.run([str(source_root / 'configure'), '--disable-tcl', '--disable-static',
                    '--disable-readline', '--with-tempstore=yes', '--fts5', '--dll-basename=libsqlcipher'],
                   cwd=build, env=env, check=True)
    subprocess.run(['make', '-j2', 'so', 'LDFLAGS.rpath='], cwd=build, env=env, check=True)
    target = ROOT / 'artifacts/native/linux-x64/libsqlcipher.so'
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(build / 'libsqlcipher.so', target)
    print(f'Built development SQLCipher {SOURCES["engineVersion"]}: {target}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--android', action='store_true', help='refresh checked-in Android binaries from the pinned official AAR')
    parser.add_argument('--linux', action='store_true', help='build the Linux x64 test/benchmark native using system OpenSSL')
    args = parser.parse_args()
    if not (args.android or args.linux):
        parser.error('choose --android and/or --linux')
    if args.android:
        android()
    if args.linux:
        linux()
