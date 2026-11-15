"""lin-sing as an MCP server (stdio, JSON-RPC 2.0, no extra packages): any MCP client can drive lin -
Claude Code / Claude Desktop, Trae, Cursor, ChatGPT (desktop / Codex), DeepSeek through an MCP-capable harness (OpenCode, Cline, ...).

Register it as a stdio server:   command = python   args = ["<repo>/pipeline/mcp_server.py"]
Tools:
  linsing_check(audio)                         length check (<= 10 min; warns above 5 min)
  linsing_make(audio, lyrics?, lang?, name?)   the whole analysis + automatic choreography -> songs/<name>/
  linsing_get_choreo(folder)                   the editable dope sheet (moves with times) and the list of moves
  linsing_set_choreo(folder, sheet)            replace the dope sheet and re-bake body + camera
  linsing_render(folder)                       render with Unity -> mp4 (needs Unity 6, LINSING_UNITY)
"""
import os, sys, json, subprocess
HERE = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, HERE)
def _python():
    """the repository's own environment (install.bat / install.sh make .venv) when there is one: the MCP client may start this server with
    whatever `python` is on its PATH, which need not have the pipeline's packages"""
    root = os.path.dirname(HERE)
    for p in (os.path.join(root, ".venv", "Scripts", "python.exe"), os.path.join(root, ".venv", "bin", "python")):
        if os.path.exists(p): return p
    return sys.executable
CLI = [_python(), "-X", "utf8", os.path.join(HERE, "linsing.py")]

TOOLS = [
    dict(name="linsing_check", description="Check an audio file's length for lin-sing (max 10 minutes; over 5 minutes it works but may make the computer stutter).",
         inputSchema=dict(type="object", properties=dict(audio=dict(type="string", description="path to the audio file")), required=["audio"])),
    dict(name="linsing_make", description="Make lin sing a song: separates the voice, finds the lyrics and every syllable, builds the face track, analyses the music and writes an automatic choreography and camera. Takes minutes. Returns the song folder.",
         inputSchema=dict(type="object", properties=dict(audio=dict(type="string"), lyrics=dict(type="string", description="lyrics file the user gave (.txt one line per line, 'original | translation' for a caption translation; or .lrc). Shown as captions."),
                                                        lang=dict(type="string", enum=["ja", "zh", "en"]), name=dict(type="string"),
                                                        mood=dict(type="string", enum=["neutral", "sad", "bright"], description="her faces and the stage colours")), required=["audio"])),
    dict(name="linsing_get_choreo", description="Read the dope sheet of a song folder: a list of {t, end, move, level} and the moves that exist.",
         inputSchema=dict(type="object", properties=dict(folder=dict(type="string")), required=["folder"])),
    dict(name="linsing_set_choreo", description="Replace the dope sheet (list of {t, end, move, level}) of a song folder and re-bake body, eyes and camera.",
         inputSchema=dict(type="object", properties=dict(folder=dict(type="string"), sheet=dict(type="array", items=dict(type="object"))), required=["folder", "sheet"])),
    dict(name="linsing_render", description="Render the song folder with Unity into an mp4 with the lyric captions (the Unity editor opens and closes by itself). With t_from / t_to only that part, at 720p, as a quick preview.",
         inputSchema=dict(type="object", properties=dict(folder=dict(type="string"), t_from=dict(type="number"), t_to=dict(type="number")), required=["folder"])),
]


def call(name, a):
    if name == "linsing_check": cmd = CLI + ["check", a["audio"]]
    elif name == "linsing_make":
        cmd = CLI + ["make", a["audio"]] + (["--lyrics", a["lyrics"]] if a.get("lyrics") else []) + (["--lang", a["lang"]] if a.get("lang") else []) + (["--name", a["name"]] if a.get("name") else []) + (["--mood", a["mood"]] if a.get("mood") else [])
    elif name == "linsing_get_choreo":
        return open(os.path.join(a["folder"], "choreo.json"), encoding="utf-8").read()
    elif name == "linsing_set_choreo":
        p = os.path.join(a["folder"], "choreo.json"); d = json.load(open(p, encoding="utf-8"))
        bad = [s.get("move") for s in a["sheet"] if s.get("move") not in d["moves"]]
        if bad: return "unknown moves: %s; allowed: %s" % (bad, d["moves"])
        d["sheet"] = sorted(a["sheet"], key=lambda s: float(s["t"])); json.dump(d, open(p, "w", encoding="utf-8"), indent=1, ensure_ascii=False)
        cmd = CLI + ["bake", a["folder"]]
    elif name == "linsing_render": cmd = CLI + ["render", a["folder"]] + (["--from", str(a["t_from"])] if a.get("t_from") is not None else []) + (["--to", str(a["t_to"])] if a.get("t_to") is not None else [])
    else: raise ValueError("unknown tool " + name)
    r = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="replace")
    out = (r.stdout + ("\n" + r.stderr if r.returncode else ""))[-6000:]
    return ("OK\n" if r.returncode == 0 else "FAILED\n") + out


def main():
    # MCP messages are UTF-8. A client that starts this with a plain `python` on Windows leaves the console code page (cp1252, GBK on
    # Chinese Windows) in place, and the first Chinese character in a reply would crash the server - so both pipes are set to UTF-8 here.
    for stream in (sys.stdin, sys.stdout):
        try: stream.reconfigure(encoding="utf-8")
        except Exception: pass
    for line in sys.stdin:
        try: msg = json.loads(line)
        except Exception: continue
        mid, method = msg.get("id"), msg.get("method")
        if method == "initialize":
            res = dict(protocolVersion=msg.get("params", {}).get("protocolVersion", "2024-11-05"), capabilities=dict(tools={}), serverInfo=dict(name="lin-sing", version="0.1.0"))
        elif method == "tools/list": res = dict(tools=TOOLS)
        elif method == "tools/call":
            p = msg.get("params", {})
            try: res = dict(content=[dict(type="text", text=call(p.get("name"), p.get("arguments") or {}))])
            except Exception as e: res = dict(content=[dict(type="text", text="error: %s" % e)], isError=True)
        elif mid is None: continue                                   # notifications
        else: res = {}
        sys.stdout.write(json.dumps(dict(jsonrpc="2.0", id=mid, result=res), ensure_ascii=False) + "\n"); sys.stdout.flush()


if __name__ == "__main__":
    main()
