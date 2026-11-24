"""Packs one last-hit lab recording for upload: its pictures as uncompressed tar shards of about SHARD GiB (members keep
their path REC/NNNNNN.jpg), the trace, sync file, log, frames.jsonl and labels.jsonl as they are, and manifest.json with
every file's bytes and SHA-256 (written last). Layout: OUT/runs/REC/.

    python pack.py ROOT REC OUT [--shard 1.5]
"""
import argparse, hashlib, json, os, shutil, tarfile


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 22), b""):
            h.update(block)
    return h.hexdigest()


def main():
    p = argparse.ArgumentParser()
    p.add_argument("root"); p.add_argument("rec"); p.add_argument("out"); p.add_argument("--shard", type=float, default=1.5)
    a = p.parse_args()
    src, dst = os.path.join(a.root, a.rec), os.path.join(a.out, "runs", a.rec)
    os.makedirs(dst)
    files = []
    for name in (a.rec + ".jsonl", a.rec + ".sync.jsonl", a.rec + ".log"):
        shutil.copyfile(os.path.join(a.root, name), os.path.join(dst, name)); files.append(name)
    for name in ("frames.jsonl", "labels.jsonl"):
        shutil.copyfile(os.path.join(src, name), os.path.join(dst, name)); files.append(name)
    pictures = sorted(f for f in os.listdir(src) if f.endswith(".jpg"))
    limit, shard, size, tar = a.shard * (1 << 30), 0, 0, None
    for picture in pictures:
        path = os.path.join(src, picture)
        if tar is None or size + os.path.getsize(path) > limit:
            if tar: tar.close()
            name = f"pictures-{shard:03d}.tar"
            tar, size, shard = tarfile.open(os.path.join(dst, name), "w", format=tarfile.PAX_FORMAT), 0, shard + 1
            files.append(name)
        tar.add(path, arcname=f"{a.rec}/{picture}")
        size += os.path.getsize(path) + 1024
    if tar: tar.close()
    manifest = {"recording": a.rec, "pictures": len(pictures), "first": pictures[0] if pictures else None, "last": pictures[-1] if pictures else None,
                "clock": "frames.jsonl 'at' is the capture time in QPC seconds; labels.jsonl 't' is the server time of each picture "
                         "(per-picture offset from REC.sync.jsonl, which pairs server frame times 't' with their arrival 'q' on the same clock)",
                "files": [{"name": f, "bytes": os.path.getsize(os.path.join(dst, f)), "sha256": sha256(os.path.join(dst, f))} for f in files]}
    with open(os.path.join(dst, "manifest.json"), "w") as f:
        json.dump(manifest, f, indent=1)
    print(json.dumps({"recording": a.rec, "pictures": len(pictures), "files": len(files), "bytes": sum(x["bytes"] for x in manifest["files"])}))


if __name__ == "__main__":
    main()
