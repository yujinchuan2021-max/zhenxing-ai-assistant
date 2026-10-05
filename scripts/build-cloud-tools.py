"""Build immutable portable tool packages from a verified previous candidate.

No tool is launched. A catalog entry is retained even when its payload cannot be
published as a portable executable. Existing upstream URLs never become package URLs.
"""
import argparse
import hashlib
import json
import re
import struct
import zipfile
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlsplit

# Explicit launch entry review for suites containing unrelated helper executables.
# A helper's PE architecture does not make it another architecture of the GUI tool.
REVIEWED_ENTRIES = {
    'tool-linx': ['LinX.exe'],
    'tool-gputest': ['GpuTest_GUI.exe'],
    'tool-furmark-win64': ['furmark.exe'],
    'tool-crystaldiskinfo': ['DiskInfo64S.exe'],
}


def sha(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(block)
    return value.hexdigest()


def normalized(text):
    return re.sub(r'[\s_\-+]', '', text).casefold()


def architecture(path):
    try:
        with path.open('rb') as stream:
            if stream.read(2) != b'MZ':
                return None
            stream.seek(60)
            offset = struct.unpack('<I', stream.read(4))[0]
            stream.seek(offset)
            if stream.read(4) != b'PE\x00\x00':
                return None
            machine = struct.unpack('<H', stream.read(2))[0]
            return {0x8664: 'x64', 0x14c: 'x86', 0xaa64: 'arm64'}.get(machine)
    except (OSError, struct.error):
        return None


def portable_entry(path):
    return path.suffix.casefold() == '.exe' and not re.search(
        r'(setup|install|unins|uninstall|redist|vc_redist|crash|updater|updatecheck)', path.name, re.I)


def homepage(item, directory):
    source = item.get('downloadUrl', '')
    if source.startswith('https://'):
        return source
    if source.startswith('gh:') and not source[3:].startswith('Tools/'):
        return 'https://github.com/' + source[3:]
    if directory:
        for file in sorted(directory.rglob('*')):
            if file.is_file() and file.suffix.casefold() in ('.url', '.bat', '.cmd', '.txt') and file.stat().st_size < 32768:
                for encoding in ('utf-8-sig', 'gb18030'):
                    try:
                        text = file.read_text(encoding=encoding)
                        break
                    except UnicodeError:
                        text = ''
                found = re.search(r'https://[^\s\x00"<>]+', text)
                if found:
                    value = found.group().rstrip("');,，。")
                    parsed = urlsplit(value)
                    if parsed.netloc and not parsed.username and not parsed.password:
                        return value
    return ''


def find_directory(root, item, directories):
    source = item.get('downloadUrl', '')
    if source.startswith(('gc:Tools/', 'gh:Tools/')):
        path = root.joinpath(*source[9:].split('/')).resolve()
        if root in path.parents and path.is_dir():
            return path
    key = normalized(item['match'])
    choices = [path for path in directories if key and key in normalized(path.name)]
    exact = [path for path in choices if key == normalized(path.name)]
    return exact[0] if len(exact) == 1 else choices[0] if len(choices) == 1 else None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--candidate', required=True)
    parser.add_argument('--metadata', required=True)
    parser.add_argument('--out', required=True)
    parser.add_argument('--revision', type=int, required=True)
    args = parser.parse_args()
    candidate = Path(args.candidate).resolve()
    root = (candidate / 'src' / 'Tools').resolve()
    out = Path(args.out).resolve()
    if candidate.parent != Path(r'D:\AI2') or not candidate.name.startswith('TubaWinUi3CE-cand6-'):
        raise ValueError('Input must be a dedicated verified candidate')
    if out.parent != Path(r'D:\AI2') or out.exists() or args.revision < 1:
        raise ValueError('Output must be a new D:\\AI2 folder; revision must be positive')
    lock = json.loads((candidate / 'app-content-lock.json').read_text(encoding='utf-8-sig'))['files']
    items = json.loads(Path(args.metadata).read_text(encoding='utf-8-sig'))['tools']
    directories = [d for c in root.iterdir() if c.is_dir() for d in c.iterdir() if d.is_dir()]
    out.mkdir()
    manifest = {'schemaVersion': 1, 'revision': args.revision,
        'publishedAt': datetime.now(timezone.utc).isoformat().replace('+00:00', 'Z'),
        'minClientVersion': '0.1.0.0', 'tools': []}
    package_reports = []
    ids = set()
    seen_directories = {}
    for item in items:
        if item.get('builtin'):
            continue
        name = item['match']
        slug = re.sub(r'[^a-z0-9]+', '-', name.lower()).strip('-')[:48]
        identity = 'tool-' + (slug or hashlib.sha256(name.encode()).hexdigest()[:16])
        if identity in ids:
            identity += '-' + hashlib.sha256(name.encode()).hexdigest()[:8]
        if identity in ids:
            raise ValueError('Duplicate tool identity: ' + name)
        ids.add(identity)
        directory = find_directory(root, item, directories)
        category = item.get('category') or (directory.parent.name if directory else '其他工具')
        source = item.get('downloadUrl', '')
        if not directory and source.startswith(('gc:Tools/', 'gh:Tools/')):
            category = source[9:].split('/')[0]
        tool = {'id': identity, 'name': name, 'category': category,
            'categories': list(dict.fromkeys([category, *item.get('categories', [])])),
            'description': item.get('description', ''), 'publisher': item.get('publisher', ''),
            'version': str(item.get('version') or 1), 'tags': item.get('tags', []),
            'homepage': homepage(item, directory),
            'legacyPath': directory.relative_to(root).as_posix() if directory else '',
            'order': item.get('order') or 0, 'packages': []}
        if directory and directory in seen_directories:
            previous = seen_directories[directory]
            previous['categories'] = list(dict.fromkeys([*previous['categories'], *tool['categories']]))
            previous['tags'] = list(dict.fromkeys([*previous['tags'], *tool['tags'], name]))
            continue
        if directory:
            seen_directories[directory] = tool
        if directory:
            entries = sorted((p for p in directory.rglob('*') if p.is_file() and portable_entry(p)),
                key=lambda p: (normalized(name) not in normalized(p.stem), len(p.relative_to(directory).parts), len(p.name), p.name))
            selected = {}
            for variant in item.get('archVariants', []):
                path = directory / variant.get('file', '__none__')
                if path.is_file() and portable_entry(path) and architecture(path):
                    selected.setdefault(architecture(path), path)
            for entry in entries:
                arch = architecture(entry)
                if arch:
                    selected.setdefault(arch, entry)
            if identity in REVIEWED_ENTRIES:
                selected = {}
                for relative in REVIEWED_ENTRIES[identity]:
                    entry = directory / relative
                    arch = architecture(entry)
                    if not entry.is_file() or not arch or not portable_entry(entry):
                        raise ValueError('Reviewed launch entry missing: ' + str(entry))
                    selected[arch] = entry
            if selected:
                files = sorted(p for p in directory.rglob('*') if p.is_file())
                hashes = {}
                for file in files:
                    if file.is_symlink() or file.stat().st_file_attributes & 0x400:
                        raise ValueError('Reparse file refused: ' + str(file))
                    rel = file.relative_to(root).as_posix()
                    actual = sha(file)
                    if lock.get('Tools/' + rel) != actual:
                        raise ValueError('Candidate tool content differs from lock: ' + rel)
                    hashes[file.relative_to(directory).as_posix()] = actual
                payload_id = hashlib.sha256(json.dumps(hashes, sort_keys=True).encode()).hexdigest()[:20]
                package_path = out / 'downloads' / 'tools' / identity / payload_id / 'portable.zip'
                package_path.parent.mkdir(parents=True)
                with zipfile.ZipFile(package_path, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
                    for file in files:
                        info = zipfile.ZipInfo(file.relative_to(directory).as_posix(), date_time=(2020, 1, 1, 0, 0, 0))
                        info.compress_type = zipfile.ZIP_DEFLATED
                        info.external_attr = 0o100644 << 16
                        archive.writestr(info, file.read_bytes())
                with zipfile.ZipFile(package_path) as archive:
                    if archive.testzip() is not None:
                        raise ValueError('Package CRC failed')
                package_sha = sha(package_path)
                for arch, entry in selected.items():
                    tool['packages'].append({'architecture': arch, 'url': 'https://zhenxingai.com/' + package_path.relative_to(out).as_posix(),
                        'sizeBytes': package_path.stat().st_size, 'sha256': package_sha,
                        'entryPoint': entry.relative_to(directory).as_posix(), 'kind': 'portable-zip'})
                package_reports.append({'id': identity, 'path': str(package_path), 'sha256': package_sha,
                    'sizeBytes': package_path.stat().st_size, 'files': len(files), 'entries': {a: p.relative_to(directory).as_posix() for a, p in selected.items()}})
        manifest['tools'].append(tool)
    (out / 'catalog.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    report = {'sourceCandidate': str(candidate), 'catalogEntries': len(manifest['tools']),
        'portablePackages': len(package_reports), 'manualEntries': sum(not t['packages'] for t in manifest['tools']),
        'packages': package_reports, 'sizeBytes': sum(p['sizeBytes'] for p in package_reports), 'toolsExecuted': False}
    (out / 'package-report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({k: v for k, v in report.items() if k != 'packages'}, ensure_ascii=False))


if __name__ == '__main__':
    main()
