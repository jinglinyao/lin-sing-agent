"""Run a command from the package as on a fresh computer: Windows' own environment variables stay, but nothing this development machine
added does - PATH holds only Windows' folders (no Python, no ffmpeg), no model caches, no proxies (this machine cannot reach the internet
without its proxy, so the run is also offline).

usage: python fresh_env_test.py <package folder> <log file> <command...>        (the command runs inside the package folder)
       e.g. ... runtime/python/python.exe -s -X utf8 pipeline/launcher.py
"""
import os, sys, subprocess

pkg, logf, cmd = sys.argv[1], sys.argv[2], sys.argv[3:]
win = os.environ.get("SystemRoot", r"C:\Windows")
env = {k: v for k, v in os.environ.items()
       if not k.upper().startswith(("PYTHON", "HF_", "TORCH", "LINSING", "CUDA", "CONDA", "VIRTUAL_ENV", "PIP_", "NUMBA"))
       and k.upper() not in ("HTTPS_PROXY", "HTTP_PROXY", "ALL_PROXY", "NO_PROXY", "https_proxy".upper(), "TRANSFORMERS_OFFLINE")}
env["PATH"] = ";".join([os.path.join(win, "System32"), win, os.path.join(win, "System32", "Wbem"), os.path.join(win, "System32", "WindowsPowerShell", "v1.0")])
env["BROWSER"] = "none"                                     # webbrowser.open does nothing during the test
with open(logf, "w", encoding="utf-8") as f:
    p = subprocess.Popen([os.path.join(pkg, cmd[0])] + cmd[1:], cwd=pkg, env=env, stdout=f, stderr=subprocess.STDOUT)
    print(p.pid, flush=True)
    if "--wait" in os.environ.get("FRESH_TEST", ""): p.wait()
