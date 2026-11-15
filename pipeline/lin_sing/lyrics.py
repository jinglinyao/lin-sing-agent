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
def split_trans(text):
    """"original | translation" (or a tab between them): the second part is shown under the first as a translation, never sung"""
    for sep in (" | ", "\t", "｜"):
        if sep in text: a, b = text.split(sep, 1); return a.strip(), b.strip()
    return text.strip(), ""


def parse_lyrics_file(path):
    """LRC ([mm:ss.xx] text) -> [{t, text, trans}] with times; plain text -> [{t: None, ...}].
    Bilingual LRC (the same time stamp twice: original, then translation) and "original | translation" lines are both understood."""
    rows = []
    for raw in open(path, encoding="utf-8-sig").read().splitlines():
        if raw.strip().startswith("#"): continue
        tags = re.findall(r"\[(\d+):(\d+(?:\.\d+)?)\]", raw); text = re.sub(r"\[[^\]]*\]", "", raw).strip()
        if not text: continue
        text, trans = split_trans(text)
        if tags:
            for mm, ss in tags:
                t = int(mm) * 60 + float(ss)
                if rows and rows[-1]["t"] is not None and abs(rows[-1]["t"] - t) < 0.02 and not rows[-1]["trans"]: rows[-1]["trans"] = text      # second line, same stamp
                else: rows.append(dict(t=t, text=text, trans=trans))
        else: rows.append(dict(t=None, text=text, trans=trans))
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
    return words, detected, [[round(a, 2), round(b, 2)] for a, b in regions]


END_JA = set("をがはにでてとのもへよねたださるいうくずぬんか")
ATTACH = set("をがはにでのもへとねよんっゃゅょぁぃぅぇぉーてたりるれろらしすさ")
def is_kanji(ch): return "一" <= ch <= "鿿" or ch in "々〆"
def boundary(before, after):
    """how good a place to break a line is between two heard words (Japanese / Chinese have no spaces to show where words end)"""
    a, b = before.strip()[-1:] or " ", after.strip()[:1] or " "
    score = 0.0
    if a in "、,，。.!?！？": score += 0.5
    if a in END_JA and b not in ATTACH: score += 0.25
    if b in ATTACH: score -= 0.35                          # "君|を": the particle belongs to the word before it
    if is_kanji(a) and is_kanji(b): score -= 0.3            # "場|所": inside a compound
    return score


def lines_from_words(words, gap=0.45, max_len=6.5):
    """Whisper's words grouped into sung lines: a new line after a pause or a full stop. A line that grows longer than max_len is cut at its
    longest inner pause (between two words, never inside one) - a caption broken in the middle of a word reads badly."""
    groups = []
    for w in words:
        if groups and (w[0] - groups[-1][-1][1] <= gap and not groups[-1][-1][2].rstrip().endswith(("。", ".", "!", "?", "！", "？"))): groups[-1].append(w)
        else: groups.append([w])
    def cut(g):
        if len(g) < 2 or g[-1][1] - g[0][0] <= max_len: return [g]
        ks = [k for k in range(1, len(g)) if g[k][0] - g[0][0] >= 1.2 and g[-1][1] - g[k][0] >= 1.2] or list(range(1, len(g)))
        k = max(ks, key=lambda k: (g[k][0] - g[k - 1][1]) + boundary(g[k - 1][2], g[k][2]) - 0.02 * abs(k - len(g) / 2))
        return cut(g[:k]) + cut(g[k:])
    lines = []
    for g in groups:
        for part in cut(g): lines.append(dict(t=part[0][0], end=part[-1][1], text="".join(x[2] for x in part).strip()))
    return [l for l in lines if l["text"]]


def global_match(a, b):
    """Edit-distance alignment of the lyrics' letters (a) against the heard letters (b), in order, with free unmatched text at both ends of b.
    Returns {index in a: index in b} for the letters that match. (difflib's longest-block matching is fooled by a repeated chorus line:
    it pairs the first one the user wrote with a later one that was heard, and squeezes everything between.)"""
    N, M = len(a), len(b)
    if not N or not M: return {}
    B = np.frombuffer(b.encode("utf-32-le"), np.uint32); A = np.frombuffer(a.encode("utf-32-le"), np.uint32)
    D = np.zeros((N + 1, M + 1), np.int32 if N * M < 4e7 else np.int16); idx = np.arange(M + 1)
    for i in range(1, N + 1):
        cost = (B != A[i - 1]).astype(np.int32)
        cand = np.minimum(D[i - 1, :-1] + cost, D[i - 1, 1:] + 1)
        row = np.concatenate([[i], cand]) - idx
        D[i] = np.minimum.accumulate(row) + idx
    i, j = N, int(np.argmin(D[N])); pos = {}
    while i > 0 and j > 0:
        c = 0 if A[i - 1] == B[j - 1] else 1
        if D[i, j] == D[i - 1, j - 1] + c:
            if c == 0: pos[i - 1] = j - 1
            i -= 1; j -= 1
        elif D[i, j] == D[i - 1, j] + 1: i -= 1
        else: j -= 1
    return pos


def times_for_plain_lines(rows, words, lang):
    """the user's plain lyrics get line times by matching their romanized letters against what Whisper heard"""
    def rom(s): return "".join(u[0] for u in to_units(s, lang))
    heard = ""; tmap = []
    for a, b, w in words:
        r = rom(w); heard += r; tmap += list(np.linspace(a, b, max(1, len(r)), endpoint=False))[:len(r)]
    mine = ""; starts = []
    for r in rows: starts.append(len(mine)); mine += rom(r["text"])
    pos = global_match(mine, heard)
    out = []
    for i, r in enumerate(rows):
        a, b = starts[i], starts[i + 1] if i + 1 < len(rows) else len(mine)
        hit = [pos[k] for k in range(a, b) if k in pos]
        ok = tmap and len(hit) >= max(1, (b - a) // 4)
        out.append(dict(t=tmap[min(hit)] if ok else None, end=(tmap[min(max(hit), len(tmap) - 1)] + 0.3) if ok else None, text=r["text"], trans=r.get("trans", ""), n=b - a))
    # a line Whisper did not hear (or heard wrong) is not dropped: it gets its time between its neighbours, by its length in letters
    known = [i for i, o in enumerate(out) if o["t"] is not None]
    for i, o in enumerate(out):
        if o["t"] is not None: continue
        p = max([k for k in known if k < i], default=None); q = min([k for k in known if k > i], default=None)
        if p is None or q is None: continue
        span = [out[k]["n"] for k in range(p + 1, q)]; t0 = out[p]["end"]; t1 = out[q]["t"]; before = sum(out[k]["n"] for k in range(p + 1, i))
        o["t"] = t0 + (t1 - t0) * before / max(1, sum(span)); o["end"] = t0 + (t1 - t0) * (before + o["n"]) / max(1, sum(span)); o["guessed"] = True
    out = [o for o in out if o["t"] is not None]
    for i in range(len(out) - 1): out[i]["end"] = min(out[i]["end"], out[i + 1]["t"])
    return out


# ------------------------------------------------------------------ forced alignment
def align(vocals_wav, lines, lang, log=print, max_window=30.0, regions=None):
    import soundfile as sf, torch, torchaudio
    from scipy.signal import resample_poly
    L = []
    for ln in lines:
        u = [x for x in to_units(ln["text"], lang) if x[0]]
        if u: L.append(dict(t=float(ln["t"]), end=float(ln["end"]), units=u, text=ln["text"], trans=ln.get("trans", "")))
    if not L: raise SystemExit("no singable text found in the lyrics")
    wins = None
    if regions:
        # the stretches where the voice really sounds: every line goes to the stretch around its rough time (in order), and each stretch is
        # aligned as one piece. The rough times may be wrong inside a stretch (a repeated chorus line matched in the wrong place) - the
        # aligner, which walks the text and the audio in step, puts them right.
        R = []
        for a, b in sorted([float(a), float(b)] for a, b in regions):            # continuous singing (split by the 28 s cap of the listener) is one window
            if R and a - R[-1][1] < 4.0 and b - R[-1][0] < 55.0: R[-1][1] = b
            else: R.append([a, b])
        idx = []
        for ln in L:
            m = 0.5 * (ln["t"] + ln["end"])
            idx.append(min(range(len(R)), key=lambda k: 0.0 if R[k][0] <= m <= R[k][1] else min(abs(m - R[k][0]), abs(m - R[k][1]))))
        for i in range(1, len(idx)): idx[i] = max(idx[i], idx[i - 1])
        groups, wins = [], []
        for ln, k in zip(L, idx):
            if wins and wins[-1][2] == k: groups[-1].append(ln)
            else: groups.append([ln]); wins.append([R[k][0], R[k][1], k])
    else:
        groups = [[L[0]]]                                   # lines close together are aligned together, up to max_window seconds
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
        a, b = (max(0.0, wins[gi][0] - 0.5), min(dur, wins[gi][1] + 0.5)) if wins else (max(0.0, g[0]["t"] - 1.0), min(dur, g[-1]["end"] + 1.0))
        try: res = run(a, b, [u for ln in g for u in ln["units"]])
        except Exception as ex:                             # more letters than audio frames, etc.: spread the units evenly instead
            log(f"  alignment failed for lines at {a:.1f} s ({ex.__class__.__name__}); using even spacing there"); res = []
            for ln in g:
                ts = np.linspace(ln["t"], ln["end"], len(ln["units"]) + 1)
                res += [[u[1], u[2], round(ts[k], 3), round(ts[k] + 0.03, 3), round(ts[k + 1], 3), 0.0] for k, u in enumerate(ln["units"])]
        k = 0
        for ln in g: per.append(res[k:k + len(ln["units"])]); k += len(ln["units"])
        log(f"  aligned group {gi + 1}/{len(groups)}  {a:6.1f} - {b:6.1f} s")
    # second pass. The aligner is unsure on shouted, doubled or heavily processed vocals and then squeezes lines together; the line times
    # (from the LRC, or matched against what Whisper heard) are a good guess there. A line that scored badly, or landed far from its time
    # (start more than 1 s off, or shorter than 40 % of its line), is aligned again in its own window; if that is still implausible, its
    # syllables are spread evenly over its line time.
    sc = lambda r: float(np.mean([m[5] for m in r]))
    def plausible(i, r):
        d0 = L[i]["end"] - L[i]["t"]
        return abs(r[0][2] - L[i]["t"]) <= 1.0 and (r[-1][4] - r[0][2]) >= 0.4 * min(d0, 0.25 * len(L[i]["units"]) + 0.6)
    fixed = spread = 0
    for i in range(len(per)):
        ok = plausible(i, per[i])
        if sc(per[i]) >= 0.35 and ok: continue
        a = max(0.0, L[i]["t"] - 0.45); b = min(dur, (L[i + 1]["t"] if i + 1 < len(L) else L[i]["end"]) + 0.35)
        try: r = run(a, b, L[i]["units"])
        except Exception: r = None
        if r is not None and plausible(i, r) and (not ok or sc(r) > sc(per[i]) + 0.05): per[i] = r; fixed += 1; continue
        if not ok:
            ts = np.linspace(L[i]["t"], L[i]["end"], len(L[i]["units"]) + 1)
            per[i] = [[u[1], u[2], round(ts[k], 3), round(ts[k] + 0.04, 3), round(ts[k + 1], 3), 0.0] for k, u in enumerate(L[i]["units"])]; spread += 1
    if spread: log(f"  {spread} lines the aligner could not place: their syllables were spread over the line time")
    if fixed: log(f"  {fixed} weak lines aligned again in their own windows")
    for i in range(len(per) - 1):                            # no unit runs into the next line
        lim = per[i + 1][0][2]
        for m in per[i]: m[4] = min(m[4], max(lim, m[3] + 0.04))
    morae = []; info = []
    for i, r in enumerate(per):
        i0 = len(morae); morae += r; info.append([i0, len(morae), r[0][2], r[-1][4], round(float(np.mean([m[5] for m in r])), 3), L[i]["text"], L[i]["trans"]])
    order = sorted(range(len(morae)), key=lambda k: morae[k][2])
    if order != list(range(len(morae))):                     # overlapping lines (repeated LRC times): keep time order for the face track
        morae = [morae[k] for k in order]
    for a_, b_ in zip(morae[:-1], morae[1:]): a_[4] = min(a_[4], max(b_[2], a_[3] + 0.04))
    return dict(morae=morae, lines=info, lang=lang, device=dev)
