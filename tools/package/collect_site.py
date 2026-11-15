"""Copy the packages lin-sing was tested with - and everything they depend on - from THIS Python's environment into the portable Python
of the all-in-one package. Same versions as tested, no download.

usage: python collect_site.py <portable python's Lib/site-packages>
"""
import sys, os, re, shutil
from importlib import metadata
try: from packaging.requirements import Requirement
except ImportError: from pip._vendor.packaging.requirements import Requirement

ROOTS = ["torch", "torchaudio", "demucs", "faster-whisper", "librosa", "soundfile", "scipy", "numpy", "pykakasi", "pypinyin"]
norm = lambda n: re.sub(r"[-_.]+", "-", n).lower()


def main():
    dst = os.path.abspath(sys.argv[1]); os.makedirs(dst, exist_ok=True)
    dists = {}
    for d in metadata.distributions():
        dists.setdefault(norm(d.metadata["Name"]), d)          # first on sys.path wins, as at import time
    need, todo, missing = {}, [norm(r) for r in ROOTS], set()
    while todo:
        n = todo.pop()
        if n in need: continue
        if n not in dists: missing.add(n); continue
        d = dists[n]; need[n] = d
        for req in d.requires or []:
            r = Requirement(req)
            if r.marker and not r.marker.evaluate({"extra": ""}): continue           # optional extras / other platforms
            todo.append(norm(r.name))
    files = size = 0
    for n, d in sorted(need.items()):
        for f in d.files or []:
            parts = f.parts
            if parts[0] == ".." or "__pycache__" in parts: continue                 # console scripts / caches are not needed
            src = d.locate_file(f)
            if not os.path.isfile(src): continue
            out = os.path.join(dst, *parts); os.makedirs(os.path.dirname(out), exist_ok=True)
            shutil.copy2(src, out); files += 1; size += os.path.getsize(src)
    print(f"{len(need)} packages, {files} files, {size / 2**30:.2f} GB -> {dst}")
    print("packages:", " ".join(f"{n}=={d.version}" for n, d in sorted(need.items())))
    if missing: print("not installed (skipped):", " ".join(sorted(missing)))


if __name__ == "__main__":
    main()
