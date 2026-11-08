# Future work

Things deliberately not built yet, with enough research recorded that picking one up doesn't
mean starting the investigation over.

## Offline mode

Removed in 0.4. The bundled dictionaries were 1,494 English definitions and 859 Arabic pairs —
too thin to be a dictionary, and `en_ar.json` had in fact been failing to deserialize on every
single startup since it was added, so the Arabic half had never once loaded. Tarjem is now an
online app that keeps a 47k-word English frequency list purely to make OCR correction and CEFR
levels work without a request.

If offline comes back, it should come back properly:

**Dictionary — the easy half.** [Kaikki.org](https://kaikki.org/) publishes machine-readable
Wiktionary extracts per language, same data the current keyless sources serve over HTTP. English
is ~1 GB raw, but filtered to headword + first 2 senses + part of speech it compresses to roughly
25-40 MB. That is far too large to bundle in an app that installs in seconds, so it wants to be
an **optional download from Settings**, per language, with the same "only what you use" rule the
provider clients follow.

**Translation — the hard half.** Real offline NMT means shipping a model:

| Option | Size | Verdict |
|---|---|---|
| OPUS-MT (Helsinki-NLP), one pair | ~75 MB ONNX | Per language pair. Quality well below Google's. |
| NLLB-200 distilled 600M | ~600 MB | 200 languages, needs quantizing, slow on CPU |
| Small local LLM via llama.cpp | 1 GB+ | Best quality, needs a GPU to be usable |

None of these is close to the current experience: a 200 ms API call beats a 2 s CPU inference
that translates worse. Only worth revisiting for a specific, stated use case — a user who is
genuinely offline for long stretches — and even then as an optional download, never bundled.

## PaddleOCR

Researched, designed, deliberately deferred.

Windows OCR is the right default: zero bytes, zero install, and fast. Its weakness is CJK, where
PaddleOCR is meaningfully more accurate. But integrating it means ONNX Runtime plus detection and
recognition models (~8 MB), plus the image pre/post-processing pipeline PaddleOCR expects
(DB text detection, angle classification, CRNN decoding) — several hundred lines of code whose
failure modes are entirely different from the current engine's, on a code path where a wrong
answer is worse than no answer.

The shape it should take, when it's taken:

1. Extract `IOcrEngine` from `OcrService` (`RecognizeTextAsync`, `IsLanguageAvailable`).
2. `WindowsOcrEngine` — what exists today, always the default.
3. `PaddleOcrEngine` — downloaded on demand from Settings into `%LOCALAPPDATA%\Tarjem\ocr\`,
   never bundled, never in the installer.
4. Offered only when the source language is Chinese, Japanese or Korean, since that is the only
   place the accuracy difference justifies the download.

Tesseract was evaluated and rejected: it is *less* accurate than Windows OCR on screen text,
which is anti-aliased UI rendering rather than the scanned documents Tesseract is tuned for.

## Gemini vision OCR

Sending the captured bitmap to Gemini instead of running OCR locally would be the most accurate
option by a wide margin and needs no download at all. Not enabled because it turns every lookup
into a model request — several hundred milliseconds and a chunk of the daily quota, on a shared
key, for a hotkey people press constantly.

Worth reconsidering as a **fallback only**: when local OCR returns nothing, or when
`OcrSpellCorrector` can't resolve the token, fall back to vision for that one lookup. That keeps
the common path free and fast while fixing exactly the cases that are currently unfixable.

## Per-language settings

Considered and rejected. The symptom that suggested it — switching the source to Japanese and
back to English leaving the dictionary stuck on Gemini — was a bug, not a missing feature. Adding
per-language preference sets would have doubled the settings surface to paper over it.

It took two passes to actually fix. The first stopped `MainWindow.xaml.cs` writing the forced
display value through to storage, but left the picker disabled and displaying "Locked to Gemini",
on the stated grounds that the other dictionaries only understand English. That claim was false:
`ProviderCatalog` gives FreeDictionaryAPI and Wiktionary 17 languages each, the lookup path never
forced Gemini, and the failover chain was already choosing correctly. The UI was disabling a
setting that worked. It now stays enabled and the note lists which sources cover the selected
language, generated from the catalog so it can't drift out of step with the chain again.

## Pronunciation audio

Shipped in 0.6 for the headword (`Services/PronunciationService.cs`). Still open: a second
speaker beside the *translated* word, for people learning the target language rather than the
source. The service already takes any language code, so it is a button and a click handler.

## Region overlay: matching the font weight

Tried in 0.7 and taken out. The in-place overlay matches each line's colours and size, and it
would be nice to keep bold labels bold. Two signals were measured on Segoe UI rendered at 12-23 px
and read back through Windows OCR:

| Signal | Regular | Semibold |
|---|---|---|
| Share of the box that differs from the background | 0.18 - 0.36 | 0.35 - 0.41 |
| Mean horizontal stroke run / font size | 0.123 - 0.139 | 0.147 - 0.156 |

The first mostly measures anti-aliasing and how tightly the box fits; the second is the right
quantity but its margin is under a pixel at UI sizes. Either would guess wrong often enough to look
worse than drawing everything in the regular weight. Worth revisiting only for large text (24 px and
up), where stroke widths differ by a whole pixel or more.

## Chinese word segmentation without a Chinese Windows language

Alt+Q regroups Japanese characters into words with `Windows.Data.Text.WordsSegmenter`. For Chinese
that class only has a real segmenter when a Chinese language is installed in Windows; otherwise it
resolves to "und" and splits every character apart, so Tarjem skips it and looks up the single
character (still translated in its sentence). A small bundled dictionary with forward maximum
matching (~2 MB for the 100k most common words) would close the gap.

## Small on-device models

Considered in 0.8 for speed and offline use, and set aside for the same reason as offline mode
above: every option costs far more than it returns for this app. A local translation model is
75-600 MB and slower on CPU than a network round trip; PaddleOCR needs ONNX Runtime (~20 MB) and
is only better than Windows OCR for CJK. The speed work went into the existing pipeline instead -
one-pass OCR preprocessing (362 ms to 119 ms per read), warm connections, and showing the popup as
soon as the first source answers.

## Google rate limits

The keyless Google endpoint answers 429 in bursts - measured in 0.8, the same request from two
clients at the same moment got 429 on one and 200 on the other, and a configuration that got 429
got 200 seconds later. It is not something a client setting fixes. Tarjem reconnects and retries
once, then backs the source off and answers from the next one. If it becomes common, the official
Cloud Translation API (keyed, 500k characters a month free) is the stable replacement.

## Ideas not yet started

- Arabic diacritics (tashkeel) toggle for learners.
- OCR correction for languages other than English — the current corrector and its frequency list
  are English-only, so non-English sources get the raw OCR token.
- Golden-image tests for the OCR half: `WordMatcher` is now covered against hand-written
  fixtures, but nothing exercises real capture → recognition. A handful of checked-in screenshots
  (game, browser, PDF, dark UI, small font) would pin the part that is still untested.
