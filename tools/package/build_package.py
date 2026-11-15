"""Assemble the all-in-one Windows / NVIDIA package of lin-sing (offline: Python, packages, models, ffmpeg, the player, the code).

  python tools/package/build_package.py <out folder>            e.g. E:/lin-sing-dist/pkg/lin-sing
      --hf <HF_HOME>  --torch <TORCH_HOME>                         where this computer keeps the three models

Steps that are already done in <out> are kept (the portable Python with its packages takes the longest):
  runtime/python   python-build-standalone 3.14 (extract it there first) + tools/package/collect_site.py
  runtime/ffmpeg   ffmpeg.exe / ffprobe.exe of this computer (a full build, with libass and NVENC) + its licence
  runtime/vc_redist.x64.exe   Microsoft Visual C++ runtime installer (Microsoft allows passing it on)
  models/          Whisper large-v3 (faster-whisper), HTDemucs, MMS_FA - the Hugging Face / torch caches, offline
  player/          LinSing.exe (Unity menu LinSing / Build Standalone Player)
  pipeline/ docs/ songs/ and the papers (README, licences, security, known issues), 启动 lin-sing.bat, 先看我.txt
"""
import os, sys, shutil, argparse, glob, json, subprocess, urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
PKG_README = "先看我.txt"


def copytree(src, dst, ignore=()):
    shutil.copytree(src, dst, dirs_exist_ok=True, ignore=shutil.ignore_patterns("__pycache__", "*.pyc", *ignore))


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("out"); ap.add_argument("--hf", default=os.environ.get("HF_HOME", "")); ap.add_argument("--torch", default=os.environ.get("TORCH_HOME", ""))
    a = ap.parse_args(); O = os.path.abspath(a.out); os.makedirs(O, exist_ok=True); J = lambda *p: os.path.join(O, *p)
    log = lambda *x: print(*x, flush=True)
    # 1. portable Python and its packages
    py = J("runtime", "python", "python.exe")
    if not os.path.exists(py): sys.exit("extract python-build-standalone (cpython-3.14.x-x86_64-pc-windows-msvc-install_only) to runtime/python first")
    if not os.path.isdir(J("runtime", "python", "Lib", "site-packages", "torch")):
        subprocess.run([sys.executable, os.path.join(REPO, "tools", "package", "collect_site.py"), J("runtime", "python", "Lib", "site-packages")], check=True)
    tp = J("runtime", "python", "Lib", "site-packages", "torch")                 # headers and link libraries: only for compiling, never at run time
    shutil.rmtree(os.path.join(tp, "include"), ignore_errors=True)
    for f in glob.glob(os.path.join(tp, "lib", "*.lib")): os.remove(f)
    log("[ok] runtime/python")
    # 2. ffmpeg
    ff = shutil.which("ffmpeg"); os.makedirs(J("runtime", "ffmpeg"), exist_ok=True)
    for exe in ("ffmpeg.exe", "ffprobe.exe"): shutil.copy2(os.path.join(os.path.dirname(ff), exe), J("runtime", "ffmpeg", exe))
    for lic in glob.glob(os.path.join(os.path.dirname(os.path.dirname(ff)), "LICENSE*")) + glob.glob(os.path.join(os.path.dirname(os.path.dirname(ff)), "README*")):
        shutil.copy2(lic, J("runtime", "ffmpeg", os.path.basename(lic)))
    log("[ok] runtime/ffmpeg")
    # 3. Visual C++ runtime installer
    vc = J("runtime", "vc_redist.x64.exe")
    if not os.path.exists(vc): urllib.request.urlretrieve("https://aka.ms/vs/17/release/vc_redist.x64.exe", vc)
    log("[ok] runtime/vc_redist.x64.exe")
    # 4. models
    for repo in ("models--Systran--faster-whisper-large-v3", "models--adefossez--HTDemucs"):
        copytree(os.path.join(a.hf, "hub", repo), J("models", "hf", "hub", repo))
    os.makedirs(J("models", "torch", "hub", "checkpoints"), exist_ok=True)
    mms = os.path.join(a.torch, "hub", "checkpoints", "model.pt")
    if not os.path.exists(J("models", "torch", "hub", "checkpoints", "model.pt")): shutil.copy2(mms, J("models", "torch", "hub", "checkpoints", "model.pt"))
    log("[ok] models")
    # 5. the player
    if os.path.isdir(J("player")): shutil.rmtree(J("player"))
    copytree(os.path.join(REPO, "player"), J("player"), ignore=("*_DoNotShip",))
    log("[ok] player")
    # 6. the code and the papers (the Unity project stays in the repository: users do not need it)
    if os.path.isdir(J("pipeline")): shutil.rmtree(J("pipeline"))
    copytree(os.path.join(REPO, "pipeline"), J("pipeline"), ignore=("config.json",))
    copytree(os.path.join(REPO, "docs"), J("docs"), ignore=("guide",))
    for f in ("README.md", "LICENSE", "ASSETS.md", "SECURITY.md", "AGENTS.md", "CLAUDE.md", ".mcp.json"):
        shutil.copy2(os.path.join(REPO, f), J(f))
    for d in (".kimi-code", ".agents", ".trae", ".cursor", ".github"): copytree(os.path.join(REPO, d), J(d))
    os.makedirs(J("songs"), exist_ok=True); os.makedirs(J("cache"), exist_ok=True)
    shutil.copy2(os.path.join(REPO, "songs", "README.md"), J("songs", "README.md"))
    for f in ("启动 lin-sing.bat", PKG_README, "THIRD_PARTY_NOTICES.txt", "更新说明.txt", "整合包测试清单.txt"):
        src = os.path.join(REPO, "tools", "package", "files", f)
        if os.path.exists(src): shutil.copy2(src, J(f))
    log("[ok] code, docs, launcher")
    size = sum(os.path.getsize(os.path.join(r, f)) for r, _, fs in os.walk(O) for f in fs)
    log(f"package: {O}  ({size / 2**30:.2f} GB)")


if __name__ == "__main__":
    main()
