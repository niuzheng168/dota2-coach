"""Offline x64 Windows folder build. No pip, downloads, venv copying or app-package edits."""
import argparse
import base64
import hashlib
from importlib.metadata import distribution
import io
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import sys
import tempfile
import zipfile

HERE = Path(__file__).resolve().parent
WORK = HERE.parents[2] / ".tmp"
# SHA256 over the sorted path + SHA256 of every redistributed wheel member, from the audited venv.
WHEELS = {
    "gem-dota": ("0.14.0", "d0827380221013b197753aa55470e686e0750f900aad636cadeef73c0bb69277"),
    "numpy": ("2.5.4", "7cb565cc3589ec62007a9cd2a3b0853e17d1bc39a46605bcf889d85e764f7501"),
    "cramjam": ("2.14.0", "08467855d7c83691888beccfb0b4b215f002511eca3f03b603f1e1c53de3c9c0"),
    "python-snappy": ("0.7.3", "e61fd9f0ca6173c4e795cfaaf43bcc2a37cc5c8a61ba532c547e8a6033252e88"),
    "protobuf": ("7.36.2", "eb5a735e287d6308c4a9688105bb1ea666ec169e51844b613d27909c4a410bd3"),
    "zstandard": ("0.25.0", "50504df999b5f8743fd31393e964636844c389fa8509b100fcae087316982fba"),
}
NODE = "5c976096e04e5c2c1f091938926234cc9fbebfe9787ddd149351b3b0ecc707b5"
LAUNCHER = "81a618f21cb87db9076134e70388b6e9cb7c2106739011b6a51772d22cae06b7"


def digest(p):
    with p.open("rb") as f:
        return hashlib.file_digest(f, "sha256").hexdigest()


def checked(p, sha):
    if digest(p) != sha:
        raise ValueError(f"Not the tested binary: {p.name}")
    return p


def source_text(source, dest):
    # A Windows checkout may apply core.autocrlf. The portable runtime is always UTF-8/LF.
    dest.write_bytes(source.read_text(encoding="utf-8").encode("utf-8"))


def zip_member(z, name, data):
    # Fixed timestamps/order. Staging the same inputs twice must yield identical file hashes.
    entry = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
    z.writestr(entry, data, compress_type=zipfile.ZIP_DEFLATED)


def wheel_payload(name, dest):
    version, expected = WHEELS[name]
    dist = distribution(name)
    if dist.version != version:
        raise ValueError(f"Expected {name}=={version}")
    tree = hashlib.sha256()
    for entry in sorted(dist.files, key=str):
        path = PurePosixPath(str(entry).replace("\\", "/"))
        if (not entry.hash or entry.hash.mode != "sha256" or path.is_absolute()
                or set(path.parts) & {"..", "__pycache__", "tests", "test"}
                or path.name in ("INSTALLER", "REQUESTED", "direct_url.json") or path.suffix in (".pyc", ".pyi")):
            continue
        data = entry.locate().read_bytes()
        sha = hashlib.sha256(data)
        if base64.urlsafe_b64encode(sha.digest()).rstrip(b"=").decode() != entry.hash.value:
            raise ValueError(f"Installed wheel file differs from RECORD: {path}")
        tree.update(f"{path}\t{sha.hexdigest()}\n".encode())
        output = dest / path
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_bytes(data)
    if tree.hexdigest() != expected:
        raise ValueError(f"Redistributed {name} payload differs from audited wheel")


def build(stage, node):
    if os.name != "nt" or sys.maxsize < 2**32 or sys.version_info[:3] != (3, 13, 13):
        raise ValueError("Build with the audited Windows x64 CPython 3.13.13 venv")
    base = Path(sys.base_prefix)
    checked(base / "python.exe", "dc7ecf75280678175b4f931ce05f1ef9c10d48984399ca7de6beee69d71bcb1b")
    checked(base / "python313.dll", "6964e590037c41d631fd1215f05af154f7fa68105993c6f573a20689b473f1ac")
    runtime = stage / "runtime"
    runtime.mkdir(parents=True)
    for name in ("python.exe", "python3.dll", "python313.dll", "vcruntime140.dll", "vcruntime140_1.dll", "LICENSE.txt"):
        shutil.copyfile(base / name, runtime / name)
    dlls = runtime / "DLLs"
    dlls.mkdir()
    for path in sorted((base / "DLLs").iterdir()):
        if (path.suffix in (".dll", ".pyd") and not path.name.startswith(("_test", "_ctypes_test", "tcl", "tk", "_tk"))
                and path.is_file()):
            shutil.copyfile(path, dlls / path.name)
    with zipfile.ZipFile(runtime / "stdlib.zip", "x") as z:
        for path in sorted((base / "Lib").rglob("*.py")):
            rel = path.relative_to(base / "Lib")
            if not set(rel.parts) & {"site-packages", "test", "tests", "idlelib", "ensurepip", "venv", "tkinter", "turtledemo"}:
                zip_member(z, rel.as_posix(), path.read_bytes())
    (runtime / "python313._pth").write_bytes(b"stdlib.zip\nDLLs\npackages\n")  # isolated; no site, registry or PATH imports
    for name in WHEELS:
        wheel_payload(name, runtime / "packages")
    distlib = distribution("pip").locate_file("pip/_vendor/distlib")
    licenses = stage / "licenses"
    licenses.mkdir()
    for path in sorted((HERE / "licenses").glob("*.txt")):
        source_text(path, licenses / path.name)
    shutil.copyfile(distlib / "LICENSE.txt", licenses / "distlib.txt")
    (stage / "node").mkdir()
    shutil.copyfile(checked(node, NODE), stage / "node" / "node.exe")  # 24.20.0 win-x64; download only
    shutil.copyfile(node.with_name("LICENSE"), licenses / "node.txt")
    scripts = stage / "scripts" / "replays"
    (scripts / "postgame").mkdir(parents=True)
    (scripts / "package.json").write_bytes(b'{"type":"module"}\n')
    for name in ("download.ts", "decompress.py"):
        source_text(HERE.parent / name, scripts / name)
    for name in ("cli.py", "worker.py", "timeline.py", "verify.py", "winlimits.py", "common.py", "acquire.mts"):
        source_text(HERE / name, scripts / "postgame" / name)
    # distlib's relative-shebang Windows launcher: a direct .exe, no shell or system Python.
    bootstrap = io.BytesIO()
    with zipfile.ZipFile(bootstrap, "w") as z:
        zip_member(z, "__main__.py", b"import runpy,sys\nfrom pathlib import Path\n"
                   b"p=Path(sys.argv[0]).resolve().parent/'scripts/replays/postgame/cli.py'\n"
                   b"runpy.run_path(str(p),run_name='__main__')\n")
    (stage / "PostgameReplay.exe").write_bytes(checked(distlib / "t64.exe", LAUNCHER).read_bytes()
        + b'#!"<launcher_dir>\\runtime\\python.exe" -IB\n' + bootstrap.getvalue())
    # Exercise the launcher and all runtime parser dependencies with no global site-packages.
    clean = {key: os.environ[key] for key in ("SystemRoot", "WINDIR", "TEMP", "TMP") if key in os.environ}
    subprocess.run([stage / "PostgameReplay.exe", "--help"], cwd=WORK, env=clean, check=True, stdout=subprocess.DEVNULL)
    subprocess.run([runtime / "python.exe", "-IB", "-c", "from gem.parser import ReplayParser;import zstandard,snappy,bz2"],
                   cwd=WORK, env=clean, check=True)
    manifest = {p.relative_to(stage).as_posix(): digest(p) for p in sorted(stage.rglob("*")) if p.is_file()}
    (stage / "sha256.json").write_bytes((json.dumps(manifest, indent=2, sort_keys=True) + "\n").encode())


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--output", type=Path, default=WORK / "postgame-replay-v1")
    ap.add_argument("--node", type=Path, default=shutil.which("node"))
    args = ap.parse_args()
    dest = args.output.resolve()
    if not dest.is_relative_to(WORK.resolve()) or dest == WORK.resolve() or dest.exists():
        ap.error("Choose a NEW directory below .tmp; never builds into a WPF package")
    if args.node is None:
        ap.error("Pass --node pointing to the audited Node 24.20.0 win-x64 binary")
    dest.parent.mkdir(exist_ok=True, parents=True)
    with tempfile.TemporaryDirectory(prefix=".postgame-build-", dir=dest.parent) as tmp:
        stage = Path(tmp) / "portable cli"  # deliberately exercise a space in the path
        build(stage, Path(args.node).resolve())
        stage.rename(dest)
    print(dest / "PostgameReplay.exe")
