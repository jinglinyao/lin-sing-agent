"""Lyric captions at the bottom of the screen, timed by the aligned syllables (morae.json): every line appears on its first sung syllable
and leaves after its last one (or when the next line comes). A translation (lyrics written "original | translation") goes on a smaller line
under it. Captions only fade in and out - no motion - so they read calmly over a moving camera.

Writes  subs.json  for Unity's live player (LyricOverlay draws it in the Game view)
        subs.ass   burnt into the rendered video by ffmpeg / libass (fonts in pipeline/fonts)
usage: python -m lin_sing.subtitles <song folder>
"""
import os, sys, json

FONT = "Noto Sans SC"         # pipeline/fonts/NotoSansSC (SIL OFL): simplified Chinese, kana and kanji, latin


def ass_time(t):
    t = max(0.0, t); h = int(t // 3600); m = int(t % 3600 // 60); s = t % 60
    return "%d:%02d:%05.2f" % (h, m, s)


def build(folder, log=print, lead=0.12, tail=0.45, min_dur=1.2):
    mo = json.load(open(os.path.join(folder, "morae.json"), encoding="utf-8"))
    dur = json.load(open(os.path.join(folder, "song.json"), encoding="utf-8"))["duration"]
    # in the order of the lyrics (not of the aligned times: where the aligner squeezed lines together the order still holds); every line is
    # shown at least 0.5 s, later ones move on if they must, and none is dropped
    rows = [[float(l[2]), float(l[3]), l[5] if len(l) > 5 else "", l[6] if len(l) > 6 else ""] for l in mo["lines"]]
    rows = [r for r in rows if r[2].strip()]
    subs = []; prev = -1.0
    for i, (a, b, text, trans) in enumerate(rows):
        nxt = max(rows[i + 1][0], a + 0.5) if i + 1 < len(rows) else dur
        t0 = max(0.0, a - lead, prev + 0.02); t1 = min(max(b + tail, t0 + min_dur), max(nxt - lead - 0.02, t0 + 0.5), dur)
        if t1 <= t0: continue
        subs.append(dict(t0=round(t0, 3), t1=round(t1, 3), text=text.strip(), trans=(trans or "").strip())); prev = t1
    json.dump(dict(font=FONT, lines=subs), open(os.path.join(folder, "subs.json"), "w", encoding="utf-8"), ensure_ascii=False, indent=0)
    head = f"""[Script Info]
ScriptType: v4.00+
PlayResX: 1920
PlayResY: 1080
WrapStyle: 0
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: Lyric,{FONT},58,&H00F6F4F2,&H000000FF,&H00241A14,&H78000000,-1,0,0,0,100,100,2,0,1,3.2,1.5,2,120,120,96,1
Style: Trans,{FONT},38,&H00D8DEE6,&H000000FF,&H00241A14,&H78000000,0,0,0,0,100,100,1,0,1,2.4,1.2,2,120,120,46,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
"""
    ev = []
    for s in subs:
        txt = s["text"].replace("{", "(").replace("}", ")")
        if s["trans"]:
            ev.append(f"Dialogue: 0,{ass_time(s['t0'])},{ass_time(s['t1'])},Lyric,,0,0,0,,{{\\fad(180,260)}}{txt}")
            ev.append(f"Dialogue: 0,{ass_time(s['t0'])},{ass_time(s['t1'])},Trans,,0,0,0,,{{\\fad(180,260)}}{s['trans'].replace('{', '(').replace('}', ')')}")
        else:
            ev.append(f"Dialogue: 0,{ass_time(s['t0'])},{ass_time(s['t1'])},Lyric,,0,0,52,,{{\\fad(180,260)}}{txt}")
    open(os.path.join(folder, "subs.ass"), "w", encoding="utf-8-sig").write(head + "\n".join(ev) + "\n")
    log(f"  captions: {len(subs)} lines" + (f", {sum(1 for s in subs if s['trans'])} with a translation" if any(s["trans"] for s in subs) else ""))
    return subs


if __name__ == "__main__":
    build(sys.argv[1])
