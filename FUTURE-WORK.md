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

The clearest remaining gap for a vocabulary tool: the popup renders `/ˈlaɪbrəri/` and there is no
way to hear it. `Windows.Media.SpeechSynthesis` is already available with no package to add, works
offline, and covers most of the 18 languages — a speaker button beside the phonetic and beside the
translated word. Deliberately deferred once; it should be the next feature.

## Ideas not yet started

- A "words you keep looking up" view — repeated lookups are the ones worth learning. Needs a
  lookup counter on `HistoryEntry` instead of the current append-only list.
- Filter history by CEFR level.
- Arabic diacritics (tashkeel) toggle for learners.
- OCR correction for languages other than English — the current corrector and its frequency list
  are English-only, so non-English sources get the raw OCR token.
- Golden-image tests for the OCR half: `WordMatcher` is now covered against hand-written
  fixtures, but nothing exercises real capture → recognition. A handful of checked-in screenshots
  (game, browser, PDF, dark UI, small font) would pin the part that is still untested.
