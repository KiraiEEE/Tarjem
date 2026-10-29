namespace Tarjem.Core.Translation;

/// <summary>
/// The prompts every AI provider shares, in one place so Gemini, Groq and Cerebras cannot drift
/// into translating the same sentence to different standards.
///
/// The governing instruction is transcreation, not translation: produce the sentence a native
/// writer would have written to say this, not a word-shaped mapping of the original. Literal
/// output is the default failure mode of every one of these models - it reads as machine
/// translation precisely because it preserves the source language's word order and idiom - so
/// the instruction to abandon the original's structure has to be explicit and repeated.
/// </summary>
public static class TranslationPrompts
{
    /// <summary>
    /// Sampling temperature for calls that write text for a reader - block and sentence
    /// translation. Every call used to run at 0.2-0.3, which is right for facts and wrong here: a
    /// low temperature makes the model take the single most probable continuation at each step,
    /// and for a translation the most probable continuation is the word-for-word mapping of the
    /// source. The instructions above ask for a rewrite; this is what lets the model produce one.
    /// </summary>
    public const double RewriteTemperature = 0.6;

    /// <summary>Temperature for calls answering a question of fact (encyclopedia, dictionary
    /// fields) where the most probable answer is exactly the one wanted.</summary>
    public const double FactualTemperature = 0.2;

    /// <summary>The standing role every AI translation call opens with.</summary>
    public static string SystemInstruction(string targetLanguageName) =>
        $@"You are a native {targetLanguageName} writer producing text for {targetLanguageName} readers.

You do not translate word by word. You read the whole meaning, then write it again from scratch in {targetLanguageName}, the way someone who thinks in {targetLanguageName} would have said it. Rearrange word order, change grammatical structure, and replace idioms with the natural {targetLanguageName} equivalent whenever that reads better - matching the original's structure is never a goal.

Match the register and tone of the original: casual stays casual, formal stays formal, and game or story dialogue keeps its character and energy.

Use only native {targetLanguageName} script and spelling. Never transliterate, never leave source-language words in, never add notes, alternatives, parentheses or explanations.";

    /// <summary>
    /// Region (Alt+W) translation: an arbitrary block of text lifted off the screen.
    ///
    /// This is the call most likely to come back literal, and it's worth understanding why. The
    /// model is handed a bare fragment with no question attached - a menu item, a line of dialogue,
    /// a sign, half a paragraph - and with nothing to anchor it, the safest-looking output is a
    /// word-shaped mapping of the input. The instructions below spend their length on the two
    /// things that actually pull it away from that: making it work out what *kind* of text this is
    /// before writing anything, and naming the specific literalisms (calqued idioms, English word
    /// order, translated UI verbs) that are the recognizable signature of machine translation.
    /// </summary>
    public static string TextTranslationRequest(string text, string sourceLanguageName, string targetLanguageName) =>
        $@"Rewrite this {sourceLanguageName} text in natural {targetLanguageName}.

It was captured off a screen, so before you write anything, work out what kind of text it is - game or film dialogue, a menu or button, a heading, an error message, prose, a caption - and write what that kind of text would say in {targetLanguageName}. A menu item is a menu item in {targetLanguageName} too, phrased the way {targetLanguageName} software phrases it, not a translated English sentence.

Write it as though it had been authored in {targetLanguageName} in the first place:
- Say what the original means, not what its words are. If a natural {targetLanguageName} phrasing shares no words with the original, that phrasing is the right answer.
- Idioms, slang and set phrases become their {targetLanguageName} equivalents. Never translate one literally - a calqued idiom is the clearest sign of machine translation there is.
- Use {targetLanguageName} word order and sentence structure throughout. Do not carry the original's clause order, punctuation habits or connectives across.
- Keep proper nouns, character names, brands and on-screen key names as they are, unless {targetLanguageName} has an established form for them.
- Match the register exactly: casual stays casual, formal stays formal, and dialogue keeps its character's voice and energy.
- Keep it about as long as the original. It is drawn back over the space the text came from, so an explanatory expansion will not fit.

Each line below is one complete unit - a sentence or paragraph, a menu entry, a list item, a line of dialogue. Lines that merely wrapped on screen have already been joined, so never split a line and never merge two. Return the same number of lines in the same order, because the result is drawn back over the layout it came from - but within each line, restructure freely. The text was read by OCR and may contain recognition errors: infer what was meant and translate that, rather than reproducing an obvious typo. If a fragment is cut off mid-sentence, translate the fragment as a fragment; do not invent an ending for it.

Return only the {targetLanguageName} text. No quotes, no commentary, no alternatives, no notes, nothing before or after it.

Text:
{text}";

    /// <summary>Word (Alt+Q) translation for providers that answer in plain text rather than
    /// structured JSON. Two bare lines are far more reliable from smaller models than a schema.</summary>
    public static string WordTranslationRequest(string word, string sentence, string sourceLanguageName, string targetLanguageName)
    {
        var hasSentence = !string.IsNullOrWhiteSpace(sentence);

        return $@"{sourceLanguageName} word: ""{word}""
{(hasSentence
    ? $@"It appears in this sentence: ""{sentence}""
Translate the word in the sense it carries in that sentence - not its most common dictionary sense, if the two differ."
    : "No sentence was given, so use the word's most common sense.")}

Answer in exactly {(hasSentence ? "two lines" : "one line")}, with no labels and nothing else:
Line 1: the word in {targetLanguageName}
{(hasSentence ? $"Line 2: the whole sentence rewritten in natural {targetLanguageName}" : "")}";
    }

    /// <summary>The standing role for the "what is this?" (Alt+E) lookup. Deliberately not the
    /// translation instruction above: this call is answering a question of fact, and the one thing
    /// it must never do is write fluently about something it doesn't actually know.</summary>
    public static string EncyclopediaSystemInstruction(string sourceLanguageName) =>
        $@"You identify proper nouns - companies, products, games, places, people, works - and say what they are, in {sourceLanguageName}.

Accuracy outranks helpfulness here, without exception. You are the fallback for names that no encyclopedia had an entry for, so the honest answer is very often that you do not know, and saying so is a correct and expected result. Never guess from the shape of a word, never blend several things that share a name into one description, and never describe something you are not confident actually exists.

Return only the JSON object described, with no markdown fences and no commentary.";

    /// <summary>
    /// The "what is this?" (Alt+E) lookup for an AI provider - the fallback for names Wikipedia and
    /// Wikidata both came back empty on.
    ///
    /// The <c>known</c> flag is the whole design. Asked "what is X", every one of these models will
    /// produce a confident paragraph about something, which for an unrecognized name is worse than
    /// the "nothing found" it replaced: a wrong answer in this card is indistinguishable from a
    /// right one. Giving the model an explicit, blessed way to decline is what makes it usable at
    /// all.
    /// </summary>
    public static string EncyclopediaRequest(string term, string sourceLanguageName) =>
        $@"What is ""{term}""?

Return a single JSON object with exactly these fields:
- ""known"": true only if you are genuinely confident you know what this specific name refers to. If it could be several unrelated things, if you would be guessing from how the word looks, or if you simply have not heard of it, return false.
- ""title"": the name written properly, with its real capitalization and spelling
- ""kind"": a short noun phrase saying what category of thing it is, in {sourceLanguageName} - e.g. ""American technology company"", ""role-playing video game"", ""British physicist"". No sentence, no article, at most six words.
- ""summary"": one to three plain sentences in {sourceLanguageName} saying what it is and what it is known for. No marketing language, no lists, no markdown.

If ""known"" is false, set ""title"" to the name as given and leave ""kind"" and ""summary"" as empty strings. Do not apologise or explain in the summary field - an empty string is the answer.";

    /// <summary>Word (Alt+Q) lookup for providers that support a response schema, which gets both
    /// the dictionary entry and the translation from a single request.</summary>
    public static string DictionaryEntryRequest(string word, string fullSentence, string sourceLanguageName, string targetLanguageName)
    {
        var hasSentence = !string.IsNullOrEmpty(fullSentence);

        return $@"You are a precise {sourceLanguageName} dictionary and a native {targetLanguageName} writer at the same time. Never leave a field blank, null, or a placeholder - if you are unsure, give your single best answer instead of omitting it.

Word to analyze (in {sourceLanguageName}): ""{word}""
{(hasSentence
    ? $@"Sentence it appears in: ""{fullSentence}""
Use this sentence to pick the word's exact sense, part of speech, and grammatical form here - do not default to the word's most common dictionary sense if the context calls for a different one."
    : "No sentence was given - use the word's single most common sense.")}

Return a single JSON object with exactly these fields:
- ""word"": the word/phrase exactly as it should be written in {sourceLanguageName}, respecting that language's normal capitalization rules (not necessarily lowercase - e.g. German nouns are capitalized)
- ""phonetic"": IPA phonetic transcription, e.g. ""/ˈmiːnɪŋ/""
- ""partOfSpeech"": one of noun, verb, adjective, adverb, pronoun, preposition, conjunction, interjection, determiner - matching how it is used above
- ""cefrLevel"": one of A1, A2, B1, B2, C1, C2 - the word's difficulty level for a learner of {sourceLanguageName}
- ""definition"": a clear, self-contained definition in 1-2 sentences, written in {sourceLanguageName}, in the sense that matches the context above - do not define an unrelated sense of the word
- ""synonyms"": array of 3-5 {sourceLanguageName} synonyms, same part of speech and sense as ""definition""
- ""arabicWord"": the natural {targetLanguageName} word a native speaker would actually use for this, in the same sense as ""definition"". Native {targetLanguageName} script only - no transliteration, no {sourceLanguageName}, no parenthetical notes. (Despite the field's name, use {targetLanguageName}, not Arabic specifically - the name is just this field's fixed key.)
- ""arabicTranslation"": identical to ""arabicWord""
{(hasSentence
    ? $@"- ""fullSentenceTranslation"": the sentence above rewritten in natural, fluent {targetLanguageName} - as a native writer would say it, not as a word-by-word mapping. Rearrange the structure freely and swap idioms for their {targetLanguageName} equivalents whenever it reads better. Within this string, wrap ONLY the exact word or phrase that carries the meaning of ""{word}"" in double asterisks, e.g. **word**. If {targetLanguageName} grammar attaches a prefix or suffix to that word (an article, preposition, case ending, etc.), include it inside the asterisks too. Wrap exactly one occurrence and nothing else in the sentence."
    : "")}

Return ONLY the raw JSON object. No markdown code fences, no commentary, nothing before or after the JSON.";
    }
}
