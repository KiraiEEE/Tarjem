namespace Tarjem.Core.Language;

/// <summary>
/// Decides which language a piece of OCR output is in, for source-language auto-detection.
///
/// Auto-detect used to run every installed Windows OCR engine over the capture and keep whichever
/// produced the longest text. That is wrong in two ways. Within a script it is a coin toss: the
/// English, Polish and French engines read the same Latin text to almost the same length, so
/// English text came back labelled Polish or French about as often as English - and that label is
/// what gets sent to the translator as the source language, so a wrong one degrades the result.
/// Across scripts it is biased: one Chinese or Japanese character carries what takes a Latin
/// engine several letters of garbage to fill, so the longest reading of a CJK screen was often the
/// wrong one. It was also slow - one full OCR pass per installed language on every hotkey press.
///
/// So detection is split in two. <see cref="ReadingScore"/> judges an engine's reading by how much
/// of it is in that engine's own script, which is a reliable signal between scripts and lets the
/// app run one engine per script instead of one per language. <see cref="GuessWithinScript"/> then
/// tells the languages sharing that script apart from the text itself - letters only one of them
/// uses, and their most common function words.
/// </summary>
public static class LanguageGuesser
{
    public enum Script { Latin, Arabic, Cyrillic, Cjk, Hangul, Devanagari }

    /// <summary>The script a language code's OCR engine reads. Unknown codes are treated as Latin,
    /// which is what the bulk of the world's languages - and of this app's list - are written in.</summary>
    public static Script ScriptOf(string languageTag) => BaseTag(languageTag) switch
    {
        "ar" or "fa" or "ur" or "ps" => Script.Arabic,
        "ru" or "uk" or "bg" or "sr" or "be" or "kk" => Script.Cyrillic,
        "ja" or "zh" => Script.Cjk,
        "ko" => Script.Hangul,
        "hi" or "mr" or "ne" => Script.Devanagari,
        _ => Script.Latin,
    };

    /// <summary>Which script a single letter belongs to, or null for anything outside the ones above
    /// (digits, punctuation, symbols, scripts no engine here reads).</summary>
    public static Script? ScriptOfChar(char c) => c switch
    {
        >= 'A' and <= 'Z' or >= 'a' and <= 'z' => Script.Latin,
        >= 'À' and <= 'ɏ' when char.IsLetter(c) => Script.Latin,
        >= 'Ḁ' and <= 'ỿ' => Script.Latin,
        >= 'Ѐ' and <= 'ӿ' => Script.Cyrillic,
        >= '؀' and <= 'ۿ' when char.IsLetter(c) => Script.Arabic,
        >= 'ݐ' and <= 'ݿ' or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿' => Script.Arabic,
        >= 'ऀ' and <= 'ॿ' when char.IsLetter(c) => Script.Devanagari,
        >= '぀' and <= 'ヿ' or >= '㐀' and <= '䶿' or >= '一' and <= '鿿' => Script.Cjk,
        >= '豈' and <= '﫿' or >= 'ｦ' and <= 'ﾟ' => Script.Cjk,
        >= '가' and <= '힯' or >= 'ᄀ' and <= 'ᇿ' or >= '㄰' and <= '㆏' => Script.Hangul,
        _ => null,
    };

    /// <summary>
    /// How plausible <paramref name="text"/> is as the reading of the engine for
    /// <paramref name="languageTag"/>: every letter in that engine's script counts for it, every
    /// letter in another script counts against it. A wrong-script engine either reads little or
    /// reads it as the wrong alphabet, and both lose here. CJK and Hangul letters count for more
    /// because one of them holds what takes a Latin engine two or three letters to spell.
    /// </summary>
    public static double ReadingScore(string? text, string languageTag)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var own = ScriptOf(languageTag);
        double score = 0;
        foreach (var c in text)
        {
            if (ScriptOfChar(c) is not { } script) continue;
            var weight = Weight(script);
            score += script == own ? weight : -weight;
        }
        return score;
    }

    private static double Weight(Script script) => script switch
    {
        Script.Cjk => 2.5,
        Script.Hangul => 2,
        _ => 1,
    };

    /// <summary>
    /// Picks which of <paramref name="candidates"/> - languages that share a script - the text is
    /// written in. Returns <paramref name="fallback"/> when there is no real evidence either way (a
    /// lone word like "Start" could be half the Latin languages), and also when the fallback is
    /// tied for first, so an undecided call never moves away from the user's own source language.
    /// </summary>
    public static string GuessWithinScript(string? text, IReadOnlyList<string> candidates, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text) || candidates.Count < 2) return fallback;

        var tokens = Tokenize(text);
        var scored = candidates.Select(c => (Tag: c, Score: Evidence(text, tokens, c))).ToList();
        var best = scored.Max(s => s.Score);
        if (best < MinimumEvidence) return fallback;

        var leaders = scored.Where(s => s.Score >= best - TieMargin).Select(s => s.Tag).ToList();
        return leaders.Contains(fallback) ? fallback : leaders[0];
    }

    /// <summary>Half a point is one weak signal - a single common English word. Less than that is
    /// no evidence at all.</summary>
    private const double MinimumEvidence = 0.5;

    /// <summary>Scores this close together are a tie; a tie is not a reason to switch language.</summary>
    private const double TieMargin = 0.25;

    private static double Evidence(string text, IReadOnlyList<string> tokens, string languageTag)
    {
        var tag = languageTag.ToLowerInvariant();
        var profile = Profiles.GetValueOrDefault(tag) ?? Profiles.GetValueOrDefault(BaseTag(tag));
        if (profile == null) return 0;

        double score = 0;

        foreach (var c in text)
        {
            var lower = char.ToLowerInvariant(c);
            foreach (var (letters, weight) in profile.Letters)
            {
                if (letters.Contains(lower) || letters.Contains(c))
                    score += weight;
            }
        }

        foreach (var token in tokens)
        {
            if (profile.Words.Contains(token))
                score += 1;
        }

        switch (tag)
        {
            // English has no letters of its own to recognise it by, but it does have a word list.
            // Only common words count, and only for half a point: the list is noisy enough at the
            // short, rare end ("nie", "jest", "est" are all in it) that it can't be a strong signal.
            case "en":
                foreach (var token in tokens)
                {
                    if (token.Length >= 3 && !profile.Words.Contains(token))
                    {
                        var rank = EnglishWordList.Rank(token);
                        if (rank >= 0 && rank < 20000) score += 0.5;
                    }
                }
                break;

            // Text written entirely in Han characters is far more likely Chinese than Japanese,
            // which almost always mixes kana in. Without this, a Chinese line with none of the
            // simplified/traditional marker characters would have nothing to go on.
            case "zh-cn" or "zh-tw" or "zh":
                if (!text.Any(IsKana))
                    score += 0.3 * text.Count(c => ScriptOfChar(c) == Script.Cjk);
                break;
        }

        return score;
    }

    private static bool IsKana(char c) => c is >= '぀' and <= 'ヿ' or >= 'ｦ' and <= 'ﾟ';

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isWordChar = i < text.Length && char.IsLetter(text[i]);
            if (isWordChar && start < 0) start = i;
            else if (!isWordChar && start >= 0)
            {
                tokens.Add(text[start..i].ToLowerInvariant());
                start = -1;
            }
        }
        return tokens;
    }

    private static string BaseTag(string languageTag)
    {
        var dash = languageTag.IndexOf('-');
        return (dash > 0 ? languageTag[..dash] : languageTag).ToLowerInvariant();
    }

    /// <param name="Letters">Groups of letters, each with how strongly one occurrence points at
    /// this language. A letter only this language uses (Polish ł) is worth more than one it shares
    /// with a neighbour (ö, shared by German and Turkish).</param>
    /// <param name="Words">The language's most frequent function words, lower case. They are what
    /// separates languages with no letters of their own, and they survive OCR well because they
    /// are short and everywhere.</param>
    private sealed record Profile((string Letters, double Weight)[] Letters, HashSet<string> Words);

    private static Profile P(string words, params (string, double)[] letters) =>
        new(letters, new HashSet<string>(words.Split(' ', StringSplitOptions.RemoveEmptyEntries)));

    private static readonly Dictionary<string, Profile> Profiles = new()
    {
        // ── Latin ───────────────────────────────────────────────────────────────────────
        ["en"] = P("the and you your is are of that this with for not have was will can what it to be in on"),
        ["pl"] = P("nie się jest na w z do że to jak co ale po od czy tak dla jestem mnie już ten ta są być oraz",
            ("ąęłśźżńć", 3)),
        ["fr"] = P("le la les de des du et est un une pour pas que qui vous dans sur avec ce je tu il nous au aux",
            ("œ", 3), ("êûîïë", 2), ("èàùâôçé", 1)),
        ["es"] = P("el la los las de del que y en un una es por con para no se su lo más como pero yo",
            ("ñ¿¡", 3), ("áíóú", 1), ("é", 0.5)),
        ["de"] = P("der die das und ist nicht ein eine zu mit den von sie ich es auf für dem sich auch wir du",
            ("ßä", 3), ("öü", 1.5)),
        ["it"] = P("il la di che e è un per non una sono del della con gli le lo mi ti si ma ho",
            ("ìò", 2), ("àèù", 1)),
        ["pt"] = P("o a de que e do da em um para é com não uma os no na se mais você eu",
            ("ãõ", 3), ("çâêô", 1), ("áíú", 0.5)),
        ["nl"] = P("de het een en van is dat niet op te zijn met voor ik je wat er maar ook naar"),
        ["tr"] = P("ve bir bu da de için ile ne çok mi ama daha gibi olarak ben sen var yok",
            ("ğışİ", 3), ("çöü", 1)),

        // ── Arabic script ───────────────────────────────────────────────────────────────
        // Persian and Urdu add letters Arabic doesn't have (and swap in their own kaf and yeh);
        // Urdu adds a further set on top of Persian's.
        ["ar"] = P("في من على إلى أن هذا التي الذي لا ما هل عن مع كان هذه",
            ("ةىأإؤئ", 2), ("كي", 1)),
        ["fa"] = P("است این که را از به با در می برای من شما آن هم",
            ("پچژگ", 2), ("کی", 1.5)),
        ["ur"] = P("ہے کے کی میں اور کا سے نہیں یہ ہیں کو پر",
            ("ٹڈڑںےۓھہ", 3), ("پچژگکی", 1)),

        // ── CJK ─────────────────────────────────────────────────────────────────────────
        // Kana is Japanese and nothing else. Between the two Chinese scripts, these are some of
        // the most common characters whose simplified and traditional forms differ.
        ["ja"] = P("", ("ぁあぃいぅうぇえぉおかがきぎくぐけげこごさざしじすずせぜそぞただちぢっつづてでとどなにぬねのはばぱひびぴふぶぷへべぺほぼぽまみむめもゃやゅゆょよらりるれろゎわをんゔァアィイゥウェエォオカガキギクグケゲコゴサザシジスズセゼソゾタダチヂッツヅテデトドナニヌネノハバパヒビピフブプヘベペホボポマミムメモャヤュユョヨラリルレロヮワヲンヴー", 3)),
        ["zh-cn"] = P("", ("这们说个时来为会国对过还没发么学经问现开关见长车东门话书写听让认边钱应级题难习体历设战斗进击败选择继续载储档", 2)),
        ["zh-tw"] = P("", ("這們說個時來為會國對過還沒發麼學經問現開關見長車東門話書寫聽讓認邊錢應級題難習體歷設戰鬥進擊敗選擇繼續載儲檔", 2)),
    };
}
