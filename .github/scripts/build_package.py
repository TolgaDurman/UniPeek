#!/usr/bin/env python3
"""
build_package.py — Creates a Unity .unitypackage from the repository contents.

Each file's GUID is read from its companion .meta file, which Unity generates
and tracks in source control. The resulting archive can be imported directly
into any Unity project via Assets > Import Package.

Usage:
  python3 build_package.py <repo_root> <output_file> [assets_prefix]

Arguments:
  repo_root      Path to the repository root (default: current directory)
  output_file    Destination .unitypackage path
  assets_prefix  Unity import path (default: Assets/Plugins/UniPeek)
"""

import io
import os
import re
import sys
import tarfile

# Directories to exclude from the package
SKIP_DIRS = {'.git', '.github', '.vscode', '.idea', '__pycache__'}

# Files to exclude from the package
SKIP_FILES = {'.gitignore', '.gitattributes', '.DS_Store', 'Thumbs.db'}

# File extensions to exclude
SKIP_EXTS = {'.py', '.pyc'}


def read_guid(meta_path: str) -> str | None:
    """Extract the GUID from a Unity .meta file."""
    try:
        with open(meta_path, 'r', encoding='utf-8') as f:
            for line in f:
                m = re.match(r'^guid:\s*([a-f0-9]+)', line)
                if m:
                    return m.group(1)
    except OSError:
        pass
    return None


def add_entry(
    tar: tarfile.TarFile,
    guid: str,
    asset_abs: str,
    meta_abs: str,
    unity_path: str,
    is_dir: bool,
) -> None:
    """Add one asset entry (pathname + asset.meta + optional asset) to the archive."""
    # pathname — Unity uses this to reconstruct the project folder hierarchy
    data = unity_path.encode('utf-8')
    info = tarfile.TarInfo(f'{guid}/pathname')
    info.size = len(data)
    tar.addfile(info, io.BytesIO(data))

    # asset.meta
    tar.add(meta_abs, arcname=f'{guid}/asset.meta')

    # asset bytes (directories have no asset file in the archive)
    if not is_dir:
        tar.add(asset_abs, arcname=f'{guid}/asset')


def build(repo_root: str, output_file: str, prefix: str) -> None:
    print(f'[UniPeek] Building {output_file}')
    print(f'          Source : {repo_root}')
    print(f'          Prefix : {prefix}')

    count = 0
    with tarfile.open(output_file, 'w:gz') as tar:
        for dirpath, dirnames, filenames in os.walk(repo_root):
            # Prune unwanted directories in-place so os.walk won't descend into them
            dirnames[:] = sorted(
                d for d in dirnames
                if d not in SKIP_DIRS and not d.startswith('.')
            )

            rel_dir = os.path.relpath(dirpath, repo_root).replace('\\', '/')
            if rel_dir == '.':
                rel_dir = ''

            # ── Directories ────────────────────────────────────────────────
            for d in dirnames:
                dir_abs  = os.path.join(dirpath, d)
                meta_abs = dir_abs + '.meta'
                if not os.path.exists(meta_abs):
                    continue
                guid = read_guid(meta_abs)
                if not guid:
                    continue
                sub = f'{rel_dir}/{d}' if rel_dir else d
                unity_path = f'{prefix}/{sub}'
                add_entry(tar, guid, dir_abs, meta_abs, unity_path, is_dir=True)
                count += 1

            # ── Files ──────────────────────────────────────────────────────
            for fname in sorted(filenames):
                if fname in SKIP_FILES or fname.endswith('.meta'):
                    continue
                _, ext = os.path.splitext(fname)
                if ext in SKIP_EXTS:
                    continue

                file_abs = os.path.join(dirpath, fname)
                meta_abs = file_abs + '.meta'
                if not os.path.exists(meta_abs):
                    continue
                guid = read_guid(meta_abs)
                if not guid:
                    continue

                rel_file = f'{rel_dir}/{fname}' if rel_dir else fname
                unity_path = f'{prefix}/{rel_file}'
                add_entry(tar, guid, file_abs, meta_abs, unity_path, is_dir=False)
                count += 1

    size_kb = os.path.getsize(output_file) // 1024
    print(f'          Done  : {count} entries, {size_kb} KB → {output_file}')


if __name__ == '__main__':
    if len(sys.argv) < 3:
        print(__doc__)
        sys.exit(1)

    root   = sys.argv[1]
    out    = sys.argv[2]
    prefix = sys.argv[3] if len(sys.argv) > 3 else 'Assets/Plugins/UniPeek'
    build(root, out, prefix)
