"""Lyrics: where every line is sung, and every syllable inside it.

1. lines   - from the user's lyrics file (LRC with times, or plain text), or heard by Whisper (faster-whisper) when there is none.
             Plain text gets its times by matching it against what Whisper heard (character alignment, difflib).
2. units   - each line becomes singing units ("morae"): consonant + one vowel of a i u e o, the shape the mouth makes.
             ja: pykakasi readings -> kana morae      zh: pypinyin syllables -> initial + main vowel      en / latin: vowel groups
3. timing  - MMS_FA (torchaudio, Meta's multilingual CTC aligner) force-aligns the units on the vocal stem, line groups at a time
             (one window at most ~30 s: a 5-minute window would need >10 GB of GPU memory - this is the reason long songs are slow).

Output (morae.json): {"morae": [[consonant, vowel, onset, vowel_start, end, score], ...],
                      "lines": [[first unit, last unit + 1, t start, t end, score, text], ...], "lang": "ja"}
To support another language: write a romanizer that returns units [(letters, consonant, vowel, vowel_index)] (see units_*) and add it to UNITS.
"""
import os, re, json, difflib
import numpy as np

VOW = "aiueo"
# consonant -> one of the classes make_face.py knows (CCLASS): lips shut, f/v, rounded, y, sh, s, tongue
CONS_NORM = {"b": "b", "p": "p", "m": "m", "f": "f", "v": "v", "w": "w", "y": "y", "d": "d", "t": "t", "n": "n", "l": "r", "r": "r",
             "g": "g", "k": "k", "h": "h", "j": "j", "q": "ch", "x": "sh", "zh": "j", "ch": "ch", "sh": "sh", "z": "z", "c": "ts", "s": "s", "ts": "ts"}


# ------------------------------------------------------------------ romanizers: text -> [(letters, consonant, vowel, vowel index in letters)]
def units_ja(text):
    import pykakasi
    BASE = dict(zip("あいうえおかきくけこさしすせそたちつてとなにぬねのはひふへほまみむめもやゆよらりるれろわをんがぎぐげござじずぜぞだぢづでどばびぶべぼぱぴぷぺぽゔ",
        "a i u e o ka ki ku ke ko sa shi su se so ta chi tsu te to na ni nu ne no ha hi fu he ho ma mi mu me mo ya yu yo ra ri ru re ro wa o n ga gi gu ge go za ji zu ze zo da ji zu de do ba bi bu be bo pa pi pu pe po vu".split()))
    SMALL_Y = {"ゃ": "a", "ゅ": "u", "ょ": "o"}; SMALL_V = {"ぁ": "a", "ぃ": "i", "ぅ": "u", "ぇ": "e", "ぉ": "o"}
    hira = "".join(it["hira"] for it in pykakasi.kakasi().convert(text)); out = []
    for ch in hira:
        if ch in BASE: out.append(BASE[ch])
        elif ch in SMALL_Y and out: c = out[-1][:-1]; out[-1] = (c if c in ("sh", "ch", "j") else c + "y") + SMALL_Y[ch]
        elif ch in SMALL_V and out: p = out[-1]; c = p[:-1] if p[-1] in VOW else p; out[-1] = c + SMALL_V[ch]
        elif ch == "ー" and out and out[-1][-1] in VOW: out.append(out[-1][-1])
    if not out: return units_latin(text)
    res = []
    for m in out:
        vi = max((j for j, c in enumerate(m) if c in VOW), default=None)
        res.append((m, CONS_NORM.get(m[:vi], m[:1]) if vi else ("n" if m == "n" else ""), m[vi] if vi is not None else "u", vi if vi is not None else 0))
    return res


ZH_INITIALS = ("zh", "ch", "sh", "b", "p", "m", "f", "d", "t", "n", "l", "g", "k", "h", "j", "q", "x", "r", "z", "c", "s", "y", "w")
def zh_vowel(final):
    """the vowel the mouth makes for a pinyin final (the open, main one)"""
    f = final.replace("v", "u")
    for pat, v in (("ia", "a"), ("ua", "a"), ("ai", "a"), ("ao", "a"), ("an", "a"), ("a", "a"), ("ie", "e"), ("ue", "e"), ("ei", "e"), ("en", "e"), ("e", "e"),
                   ("ou", "o"), ("ong", "o"), ("uo", "o"), ("o", "o"), ("iu", "u"), ("ui", "i"), ("in", "i"), ("i", "i"), ("u", "u")):
        if pat in f: return v, f.index(pat) + (1 if pat in ("ia", "ua", "ie", "ue", "uo") else 0)
    return "e", 0
def units_zh(text):
    from pypinyin import lazy_pinyin, Style
    res = []
    for syl in lazy_pinyin(text, style=Style.NORMAL, errors="ignore"):
        syl = syl.lower().replace("ü", "v")
        if not re.fullmatch(r"[a-z]+", syl): continue
        if not re.search(r"[aeiouv]", syl): res += units_latin(syl); continue
        ini = next((i for i in ZH_INITIALS if syl.startswith(i)), ""); fin = syl[len(ini):]
        v, k = zh_vowel(fin); letters = syl.replace("v", "u")
        res.append((letters, CONS_NORM.get(ini, ""), v, len(ini) + k))
    return res


EN_V = {"a": "a", "e": "e", "i": "i", "o": "o", "u": "u", "y": "i"}
def units_latin(text):
    """English and other latin text: every vowel group is one sung syllable (rough, but the mouth opens on the right letters)"""
    res = []
    for word in re.findall(r"[a-z']+", text.lower()):
        word = word.replace("'", "")
        if not word: continue
        groups = [(m.start(), m.end()) for m in re.finditer(r"[aeiouy]+", word)]
        if len(groups) > 1 and word.endswith("e") and groups[-1] == (len(word) - 1, len(word)): groups = groups[:-1]      # silent final e
        if not groups: res.append((word, CONS_NORM.get(word[0], ""), "u", 0)); continue
        cuts = [0] + [g[1] for g in groups[:-1]] + [len(word)]
        for k, g in enumerate(groups):
            a, b = cuts[k], cuts[k + 1]; letters = word[a:b]; vi = g[0] - a; vg = word[g[0]:g[1]]
            v = "a" if vg in ("ay", "ai", "au", "aw") else "i" if vg in ("ee", "ea", "ie", "ey") else "u" if vg in ("oo", "ou", "ew") else EN_V[vg[0]]
            c0 = word[a:g[0]]; c = "sh" if c0.endswith("sh") else "ch" if c0.endswith("ch") else (c0[-1] if c0 else "")
            res.append((letters, CONS_NORM.get(c, ""), v, vi))
    return res


UNITS = {"ja": units_ja, "zh": units_zh, "en": units_latin}
def to_units(text, lang):
    return UNITS.get(lang, units_latin)(text)


# ------------------------------------------------------------------ lines
def parse_lyrics_file(path):
    """LRC ([mm:ss.xx] text) -> [{t, text}] with times; plain text -> [{t: None, text}]"""
    rows = []
    for raw in open(path, encoding="utf-8-sig").read().splitlines():
        tags = re.findall(r"\[(\d+):(\d+(?:\.\d+)?)\]", raw); text = re.sub(r"\[[^\]]*\]", "", raw).strip()
        if not text: continue
        if tags:
            for mm, ss in tags: rows.append(dict(t=int(mm) * 60 + float(ss), text=text))
        else: rows.append(dict(t=None, text=text))
    rows.sort(key=lambda r: (r["t"] is None, r["t"] or 0))
    return rows


HALLUCINATIONS = ("ご視聴ありがとうございました", "ご視聴ありがとう", "チャンネル登録", "字幕", "请不吝点赞", "订阅", "谢谢观看", "明镜与点点", "thanksforwatching", "subscribe", "amara.org")


def whisper_words(vocals_wav, lang=None, model_name="large-v3", log=print):
    """faster-whisper on the vocal stem: [(start, end, word)] and the detected language"""
    os.environ.setdefault("HF_HOME", os.path.join(os.path.expanduser("~"), ".cache", "huggingface"))
    try:                                                     # Windows: CUDA DLLs that come with torch
        import importlib.util
        lib = os.path.join(list(importlib.util.find_spec("torch").submodule_search_locations)[0], "lib")
        if os.name == "nt" and os.path.isdir(lib): os.add_dll_directory(lib); os.environ["PATH"] = lib + os.pathsep + os.environ["PATH"]
    except Exception: pass
    import soundfile as sf
    from scipy.signal import resample_poly
    from faster_whisper import WhisperModel
    import torch
    dev = "cuda" if torch.cuda.is_available() else "cpu"
    if dev == "cpu" and model_name == "large-v3": model_name = "small"; log("  no CUDA GPU: using the Whisper 'small' model on the CPU (slower, less accurate)")
    ct = "float16" if dev == "cuda" else "int8"
    try: m = WhisperModel(model_name, device=dev, compute_type=ct, local_files_only=True)        # cached: no network check (it can hang behind a proxy)
    except Exception: log(f"  downloading the Whisper model '{model_name}' (once)..."); m = WhisperModel(model_name, device=dev, compute_type=ct)
    x, sr = sf.read(vocals_wav)
    if x.ndim > 1: x = x.mean(1)
    x16 = resample_poly(x, 16000, sr).astype(np.float32)
    # Whisper run over a whole song skips verses (it "hears" a long stretch as music and jumps on). So: find every stretch where the vocal stem
    # actually sounds, and transcribe each one (at most 28 s) on its own.
    hop = 1600; e = np.array([np.sqrt(np.mean(x16[i:i + hop] ** 2)) for i in range(0, len(x16) - hop, hop)]); db = 20 * np.log10(e + 1e-7)
    on = db > np.percentile(db, 97) - 32; regions = []
    for i in np.flatnonzero(on):
        t = i * 0.1
        if regions and t - regions[-1][1] < 1.6 and t - regions[-1][0] < 28: regions[-1][1] = t + 0.1
        else: regions.append([t, t + 0.1])
    regions = [(max(0.0, a - 0.4), min(len(x16) / 16000, b + 0.4)) for a, b in regions if b - a > 0.5]
    words = []; detected = lang
    for a, b in regions:
        segs, info = m.transcribe(x16[int(a * 16000):int(b * 16000)], language=detected, vad_filter=False, word_timestamps=True, condition_on_previous_text=False)
        detected = detected or info.language
        for s in segs:                                       # Whisper's well-known phantom lines in quiet passages ("thanks for watching")
            if any(h in s.text.replace(" ", "").lower() for h in HALLUCINATIONS): continue
            words += [(a + float(w.start), a + float(w.end), w.word) for w in (s.words or []) if w.end > w.start]
    words.sort()
    log(f"  {len(regions)} sung stretches, {sum(b - a for a, b in regions):.0f} s of voice")
    return words, detected


def lines_from_words(words, gap=0.45, max_len=5.0):
    """Whisper's words grouped into sung lines: a new line after a pause, or when a line gets long"""
    lines = []
    for a, b, w in words:
        if lines and (a - lines[-1]["end"] > gap or b - lines[-1]["t"] > max_len or lines[-1]["text"].rstrip().endswith(("。", "、", ".", "!", "?", "！", "？"))):
            lines.append(dict(t=a, end=b, text=w.strip()))
        elif lines: lines[-1]["end"] = b; lines[-1]["text"] += w
        else: lines.append(dict(t=a, end=b, text=w.strip()))
    return [l for l in lines if l["text"].strip()]


def times_for_plain_lines(rows, words, lang):
    """the user's plain lyrics get line times by matching their romanized letters against what Whisper heard"""
    def rom(s): return "".join(u[0] for u in to_units(s, lang))
    heard = ""; tmap = []
    for a, b, w in words:
        r = rom(w); heard += r; tmap += list(np.linspace(a, b, max(1, len(r)), endpoint=False))[:len(r)]
    mine = ""; starts = []
    for r in rows: starts.append(len(mine)); mine += rom(r["text"])
    sm = difflib.SequenceMatcher(None, mine, heard, autojunk=False); pos = {}
    for blk in sm.get_matching_blocks():
        for k in range(blk.size): pos[blk.a + k] = blk.b + k
    out = []
    for i, r in enumerate(rows):
        a, b = starts[i], starts[i + 1] if i + 1 < len(rows) else len(mine)
        hit = [pos[k] for k in range(a, b) if k in pos]
        if len(hit) >= max(1, (b - a) // 4): out.append(dict(t=tmap[min(hit)], end=tmap[min(max(hit), len(tmap) - 1)] + 0.3, text=r["text"]))
    for i in range(len(out) - 1): out[i]["end"] = min(out[i]["end"], out[i + 1]["t"])
    return out


# ------------------------------------------------------------------ forced alignment
def align(vocals_wav, lines, lang, log=print, max_window=30.0):
    import soundfile as sf, torch, torchaudio
    from scipy.signal import resample_poly
    L = []
    for ln in lines:
        u = [x for x in to_units(ln["text"], lang) if x[0]]
        if u: L.append(dict(t=float(ln["t"]), end=float(ln["end"]), units=u, text=ln["text"]))
    if not L: raise SystemExit("no singable text found in the lyrics")
    groups = [[L[0]]]                                       # lines close together are aligned together, up to max_window seconds
    for a, b in zip(L[:-1], L[1:]):
        if b["t"] - a["end"] > 2.0 or b["end"] - groups[-1][0]["t"] > max_window: groups.append([b])
        else: groups[-1].append(b)
    x, sr = sf.read(vocals_wav)
    if x.ndim > 1: x = x.mean(1)
    x16 = resample_poly(x, 16000, sr).astype(np.float32); dur = len(x16) / 16000.0
    bundle = torchaudio.pipelines.MMS_FA; dev = "cuda" if torch.cuda.is_available() else "cpu"
    model = bundle.get_model(with_star=False).to(dev).eval(); tokenizer = bundle.get_tokenizer(); aligner = bundle.get_aligner()
    hop = 160; e = np.array([np.sqrt(np.mean(x16[i:i + 400] ** 2)) for i in range(0, len(x16) - 400, hop)]); db = 20 * np.log10(e + 1e-7)
    top = np.percentile(db[db > -60], 97) if (db > -60).any() else -20; voiced = db > top - 38

    def run(a, b, units):
        seg = torch.from_numpy(x16[int(a * 16000):int(b * 16000)])[None]
        with torch.inference_mode():
            emission, _ = model(seg.to(dev)); spans = aligner(emission[0], tokenizer([u[0] for u in units]))
        ratio = seg.size(1) / emission.size(1) / 16000.0; res = []
        for k, (letters, c, v, vi) in enumerate(units):
            sp = spans[k]; vi = min(vi, len(sp) - 1)
            t_on = a + sp[0].start * ratio; t_v = a + sp[vi].start * ratio
            nxt = a + spans[k + 1][0].start * ratio if k + 1 < len(spans) else b
            k0, k1 = int(t_v * 100), min(len(voiced), int(nxt * 100)); t_end = nxt; run_ = 0
            for q in range(k0 + 3, k1):
                run_ = run_ + 1 if not voiced[q] else 0
                if run_ >= 6: t_end = (q - 5) / 100.0; break
            res.append([c, v, round(t_on, 3), round(t_v, 3), round(max(t_end, t_v + 0.04), 3), round(float(np.mean([s.score for s in sp])), 3)])
        return res

    per = []
    for gi, g in enumerate(groups):
        a = max(0.0, g[0]["t"] - 1.0); b = min(dur, g[-1]["end"] + 1.0)
        try: res = run(a, b, [u for ln in g for u in ln["units"]])
        except Exception as ex:                             # more letters than audio frames, etc.: spread the units evenly instead
            log(f"  alignment failed for lines at {a:.1f} s ({ex.__class__.__name__}); using even spacing there"); res = []
            for ln in g:
                ts = np.linspace(ln["t"], ln["end"], len(ln["units"]) + 1)
                res += [[u[1], u[2], round(ts[k], 3), round(ts[k] + 0.03, 3), round(ts[k + 1], 3), 0.0] for k, u in enumerate(ln["units"])]
        k = 0
        for ln in g: per.append(res[k:k + len(ln["units"])]); k += len(ln["units"])
        log(f"  aligned group {gi + 1}/{len(groups)}  {a:6.1f} - {b:6.1f} s")
    # second pass: a line that scored badly is aligned again inside its own window (a wrong neighbour no longer pulls it)
    sc = lambda r: float(np.mean([m[5] for m in r]))
    fixed = 0
    for i in range(len(per)):
        if sc(per[i]) >= 0.35: continue
        a = max(0.0, L[i]["t"] - 0.45); b = min(dur, (L[i + 1]["t"] if i + 1 < len(L) else L[i]["end"]) + 0.35)
        try: r = run(a, b, L[i]["units"])
        except Exception: continue
        if sc(r) > sc(per[i]) + 0.05: per[i] = r; fixed += 1
    if fixed: log(f"  {fixed} weak lines aligned again in their own windows")
    for i in range(len(per) - 1):                            # no unit runs into the next line
        lim = per[i + 1][0][2]
        for m in per[i]: m[4] = min(m[4], max(lim, m[3] + 0.04))
    morae = []; info = []
    for i, r in enumerate(per):
        i0 = len(morae); morae += r; info.append([i0, len(morae), r[0][2], r[-1][4], round(float(np.mean([m[5] for m in r])), 3), L[i]["text"]])
    order = sorted(range(len(morae)), key=lambda k: morae[k][2])
    if order != list(range(len(morae))):                     # overlapping lines (repeated LRC times): keep time order for the face track
        morae = [morae[k] for k in order]
    for a_, b_ in zip(morae[:-1], morae[1:]): a_[4] = min(a_[4], max(b_[2], a_[3] + 0.04))
    return dict(morae=morae, lines=info, lang=lang, device=dev)
