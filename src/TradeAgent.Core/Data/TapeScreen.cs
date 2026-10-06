using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TradeAgent.Core.Data;

/// <summary>
/// WHY AN OBSERVATION IS QUARANTINED: the rule that caught it and the version of the screen that applied it
/// — and nothing of its text, so a reader told why is never handed what it was being kept from.
/// </summary>
public sealed record TapeQuarantine(string Rule, int Version);

/// <summary>
/// THE TAPE'S SCREEN, VERSION 1 (<c>U-tape-events</c>; <c>docs/EDGE-FACTORY.md</c> § 6.4, the inbox rule
/// generalised to every source): whether an observation's text is addressed to an automated reader, and is
/// therefore quarantined.
///
/// <para><b>What quarantines an item.</b> Any zero-width, bidirectional-control or tag character in its
/// strings — text a person reading the announcement would not see — or, once its strings are FOLDED (this
/// build's NFKC, case-folded, invisible characters dropped; <see cref="Fold"/>), an instruction override
/// ("ignore all previous instructions"), an address to an automated reader ("note to AI agents: …"), or
/// chat-role markup (<c>&lt;|im_start|&gt;</c>, <c>[INST]</c>, a line that opens <c>system:</c>). The strings
/// are the payload's DECODED property names and values: the canonical payload escapes every non-ASCII
/// character and <c>&lt;</c>, so a screen that read the stored text would never see the markup it is
/// looking for.</para>
///
/// <para><b>What quarantine does and does not do.</b> The item is still recorded, whole, like any other —
/// the tape keeps what the vendor published, and a flag is not a reason to lose the record. Every read
/// carries the verdict (<see cref="TapeQuarantine"/>), and <c>TapeStore.AsOf</c> withholds a quarantined
/// payload. It is not a claim that an unflagged item is safe, nor that the screen catches every way of
/// addressing a reader: look-alike letters from other scripts, a paraphrase, an image, another language all
/// pass it, and <c>U-annotator</c> decides whether a quarantined item ever reaches a model.</para>
///
/// <para><b>Built in, and run at every read.</b> The rules are this code and nothing else — no file, setting
/// or verb adds, removes or relaxes one — and the verdict is computed each time an observation is read,
/// never stored: no column of the tape holds it, so a later version of the screen reads every row ever
/// recorded, and no write into the file can mark a row clean. Measured on 2026-10-06 against OKX's 300
/// newest announcements, pages 1 to 15 read by this build's announcement parser: none flagged
/// (<c>docs/RESEARCH-REQUIRED.md</c>, C5d).</para>
/// </summary>
public static partial class TapeScreen
{
    /// <summary>The version every verdict names. A change to any rule is a new version.</summary>
    public const int Version = 1;

    /// <summary>A zero-width, bidirectional-control or tag character anywhere in the item's strings.</summary>
    public const string InvisibleCharacter = "invisible-character";

    /// <summary>A chat model's role markup: <c>&lt;|im_start|&gt;</c>, <c>[INST]</c>, <c>&lt;&lt;SYS&gt;&gt;</c>, a role tag, a line opening <c>system:</c>.</summary>
    public const string ChatRoleMarkup = "chat-role-markup";

    /// <summary>An instruction to set aside the reader's own instructions, rules or prompt.</summary>
    public const string InstructionOverride = "instruction-override";

    /// <summary>Words addressed to an automated reader: "dear AI", "if you are a language model", "any AI reading this".</summary>
    public const string AddressesAutomatedReader = "addresses-automated-reader";

    /// <summary>A payload the screen cannot read, or one it could not finish reading. Quarantined because it was not screened.</summary>
    public const string Unreadable = "unreadable";

    /// <summary>Every rule a verdict may name, in the order they are applied.</summary>
    public static IReadOnlyList<string> Rules { get; } =
        [InvisibleCharacter, ChatRoleMarkup, InstructionOverride, AddressesAutomatedReader, Unreadable];

    /// <summary>
    /// The NFKC table <see cref="Fold"/> reads, public so its identity can be pinned: every code point whose
    /// compatibility decomposition, combining marks removed, is printable ASCII.
    /// </summary>
    public static IReadOnlyDictionary<int, string> FoldTable => TapeScreenFold.Table;

    /// <summary>
    /// THE VERDICT ON ONE PAYLOAD: null when nothing caught it, otherwise the first rule that did, in the order
    /// of <see cref="Rules"/>. A payload that is not JSON, or that a rule could not finish reading in a second,
    /// is <see cref="Unreadable"/> — quarantined, because it was not screened.
    /// </summary>
    public static TapeQuarantine? Check(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var strings = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(payload);
            Collect(doc.RootElement, strings);
        }
        catch (JsonException) { return Quarantined(Unreadable); }

        foreach (var s in strings)
            if (HoldsInvisible(s)) return Quarantined(InvisibleCharacter);

        // ONE TEXT, ONE STRING A LINE: a rule reads across the item's fields as a reader would, and a line
        // that opens with a role label is a line whichever field it starts.
        var folded = Fold(string.Join('\n', strings));
        var words = Words(folded);
        try
        {
            if (Markup().IsMatch(folded)) return Quarantined(ChatRoleMarkup);
            if (Override().IsMatch(words)) return Quarantined(InstructionOverride);
            if (Address().IsMatch(words)) return Quarantined(AddressesAutomatedReader);
        }
        catch (RegexMatchTimeoutException) { return Quarantined(Unreadable); }

        return null;
    }

    /// <summary>
    /// TEXT AS THE RULES READ IT: every code point in <see cref="FoldTable"/> replaced by its ASCII (this
    /// build's NFKC — fullwidth, mathematical, circled and accented letters read as the letters they draw),
    /// format characters, combining marks, controls and letters that draw nothing dropped, every space a
    /// space and every line break a line break, then lower-cased.
    /// </summary>
    public static string Fold(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var folded = new StringBuilder(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            var cp = rune.Value;
            if (cp is '\n' or '\r' or '\v' or '\f' or 0x85 or 0x2028 or 0x2029) { folded.Append('\n'); continue; }
            if (Drops(rune)) continue;
            if (Rune.IsWhiteSpace(rune)) { folded.Append(' '); continue; }
            if (TapeScreenFold.Table.TryGetValue(cp, out var ascii)) { folded.Append(ascii); continue; }
            folded.Append(rune.ToString());
        }

        return folded.ToString().ToLowerInvariant();
    }

    /// <summary>
    /// TEXT WITH ITS CHARACTER REFERENCES RESOLVED, as a reader of markup resolves them: <c>&amp;#x200B;</c> and
    /// <c>&amp;#8203;</c> are the character they number, and <c>&amp;lt;</c>, <c>&amp;gt;</c>, <c>&amp;amp;</c>,
    /// <c>&amp;quot;</c> and <c>&amp;apos;</c> are XML's five. Resolved again while anything changes, at most
    /// four times, so a reference written inside another (<c>&amp;amp;lt;</c>) is read as what it ends as. A
    /// reference to no character — a surrogate, past U+10FFFF — is left as written. GDELT writes its page titles
    /// this way (<c>U-tape-archive</c>).
    /// </summary>
    public static string Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (var pass = 0; pass < 4 && text.Contains('&'); pass++)
        {
            var resolved = ResolveOnce(text);
            if (string.Equals(resolved, text, StringComparison.Ordinal)) break;
            text = resolved;
        }
        return text;
    }

    static string ResolveOnce(string s)
    {
        var into = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var amp = s.IndexOf('&', i);
            if (amp < 0) { into.Append(s, i, s.Length - i); break; }
            into.Append(s, i, amp - i);

            var semi = s.IndexOf(';', amp + 1);
            if (semi > amp + 1 && semi - amp <= 10 && Reference(s.AsSpan(amp + 1, semi - amp - 1)) is { } value)
            {
                into.Append(value);
                i = semi + 1;
            }
            else
            {
                into.Append('&');
                i = amp + 1;
            }
        }
        return into.ToString();
    }

    /// <summary>The text one reference between <c>&amp;</c> and <c>;</c> stands for, or null for one that is not a reference.</summary>
    static string? Reference(ReadOnlySpan<char> name)
    {
        if (name[0] == '#')
        {
            var hex = name.Length > 1 && name[1] is 'x' or 'X';
            var digits = name[(hex ? 2 : 1)..];
            if (digits.IsEmpty || digits.Length > 7) return null;
            if (!int.TryParse(digits, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var cp)
                || !Rune.IsValid(cp))
                return null;
            return new Rune(cp).ToString();
        }

        return name.Length switch
        {
            2 when name.Equals("lt", StringComparison.OrdinalIgnoreCase) => "<",
            2 when name.Equals("gt", StringComparison.OrdinalIgnoreCase) => ">",
            3 when name.Equals("amp", StringComparison.OrdinalIgnoreCase) => "&",
            4 when name.Equals("quot", StringComparison.OrdinalIgnoreCase) => "\"",
            4 when name.Equals("apos", StringComparison.OrdinalIgnoreCase) => "'",
            _ => null
        };
    }

    static TapeQuarantine Quarantined(string rule) => new(rule, Version);

    /// <summary>Every property name and every string value in the document, decoded, in document order.</summary>
    static void Collect(JsonElement e, List<string> into)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    into.Add(p.Name);
                    Collect(p.Value, into);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) Collect(item, into);
                break;
            case JsonValueKind.String:
                into.Add(e.GetString() ?? "");
                break;
        }
    }

    /// <summary>
    /// A ZERO-WIDTH (U+200B-U+200D, U+2060-U+2064, U+FEFF, U+180E), BIDIRECTIONAL-CONTROL (U+061C, U+200E,
    /// U+200F, U+202A-U+202E, U+2066-U+2069) OR TAG (U+E0000-U+E007F) CHARACTER: what lets an item say one
    /// thing to a person and another to a parser.
    /// </summary>
    static bool HoldsInvisible(string s)
    {
        foreach (var rune in s.EnumerateRunes())
            if (rune.Value is (>= 0x200B and <= 0x200F) or (>= 0x202A and <= 0x202E) or (>= 0x2060 and <= 0x2064)
                or (>= 0x2066 and <= 0x2069) or 0xFEFF or 0x180E or 0x061C or (>= 0xE0000 and <= 0xE007F))
                return true;
        return false;
    }

    /// <summary>What <see cref="Fold"/> drops: format characters, combining marks, controls that are not spaces, and letters that draw nothing.</summary>
    static bool Drops(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.Format or UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark => true,
        UnicodeCategory.Control => !Rune.IsWhiteSpace(rune),
        // HANGUL FILLERS AND THE BLANK BRAILLE PATTERN: letters by category that draw nothing at all.
        _ => rune.Value is 0x115F or 0x1160 or 0x3164 or 0xFFA0 or 0x2800
    };

    /// <summary>
    /// THE FOLDED TEXT AS WORDS: every run of anything that is not a-z or 0-9 one space, with a space at
    /// each end — so "IGNORE   previous\ninstructions!!" and "ignore-previous-instructions" read alike.
    /// </summary>
    static string Words(string folded)
    {
        var words = new StringBuilder(folded.Length + 2).Append(' ');
        var gap = true;
        foreach (var c in folded)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') { words.Append(c); gap = false; }
            else if (!gap) { words.Append(' '); gap = true; }
        }
        if (!gap) words.Append(' ');
        return words.ToString();
    }

    // ------------------------------------------------------------------------------------------- the rules
    // Each reads the folded text (markup) or its words (the rest). They are written for an exchange's
    // announcements, where "AI" is a token and "bot" a product: a word that names a reader is only an
    // address in a form that addresses one, and never when a business word follows it ("AI traders",
    // "trading bot users", "AI/USD").

    /// <summary>Who an automated reader is called, by the words an item would use to address one.</summary>
    const string Reader =
        @"(?:ai|a i|ais|artificial intelligence|llms?|large language models?|language models?|chat ?bots?|"
        + @"ai (?:assistants?|agents?|models?|systems?|readers?|tools?)|chatgpt|gpt(?: \d+)?|claude|gemini|copilot|grok|"
        + @"automated (?:readers?|systems?|agents?|tools?|traders?|scripts?|programs?)|crawlers?|scrapers?|machine readers?)";

    /// <summary>Words that, after a reader's name, make it a market or a product rather than an address.</summary>
    const string NotABusiness =
        @"(?! (?:users?|traders?|enthusiasts?|fans?|lovers?|builders?|developers?|creators?|community|communities|holders?|"
        + @"investors?|hunters?|explorers?|pioneers?|tokens?|coins?|projects?|sector|era|trading|season|summit|week|month|day|"
        + @"campaign|competition|contest|challenge|event|launch|hub|zone|plaza|program|programme|integration|support|powered|"
        + @"driven|based|products?|features?|services?|tools?|platform|markets?|narrative|usd|eur|usdt|usdc|perps?|spot|futures)\b)";

    /// <summary>After "you are a…": the few words that make it a description of a person rather than an address to a machine.</summary>
    const string NotAPerson =
        @"(?! (?:users?|traders?|enthusiasts?|fans?|lovers?|builders?|developers?|creators?|investors?|powered|driven|based|"
        + @"enabled|generated|native|first)\b)";

    /// <summary>Any machine an "if you are a…" could be asking after, and the roles that make it a person after all.</summary>
    const string AnyMachine = @"(?:bots?|agents?|models?|assistants?|machines?|robots?|programs?|automated(?: [a-z]+)?)";

    const string NotARole = @"(?! (?:users?|traders?|holders?|owners?|subscribers?|creators?|providers?|developers?|operators?|customers?)\b)";

    // The folded text's only spaces are ' ' and '\n' (Fold), so a role line is written with ' *' and never
    // '\s*': under Multiline, '^\s*' runs on across every line break after each line start, and a title of
    // twenty thousand " \n" took 210 ms a read on the dev Mac (2026-10-06) where ' *' takes 4 ms.
    [GeneratedRegex(
        @"<\|[a-z0-9_ /]{1,32}\|>"
        + @"|\[/?inst\]"
        + @"|<</?sys>>"
        + @"|</?(?:system|assistant|user|developer|human|im_start|im_end|tool_call|tool_response|function_call|instructions?|sys)>"
        + @"|<(?:start_of_turn|end_of_turn)>"
        + @"|(?:^| )### *(?:system|instructions?|response|assistant|user|human)\b"
        + @"|^ *(?:system|assistant|human) *:",
        RegexOptions.CultureInvariant | RegexOptions.Multiline, 1000)]
    private static partial Regex Markup();

    [GeneratedRegex(
        @"\b(?:ignore|disregard|forget)(?: (?:all|any|every|of|the|these|those))*"
        + @"(?: (?:your|previous|prior|above|earlier|preceding|foregoing|original|initial|former|system|developer|safety))+ "
        + @"(?:instructions?|prompts?|rules|directives?|directions|guidelines|guardrails|constraints|commands|programming|context|"
        + @"messages|polic(?:y|ies)|orders)\b"
        + @"|\b(?:override|bypass)(?: (?:all|any|the|of))*(?: (?:your|system|developer|safety))+ "
        + @"(?:instructions?|prompts?|rules|guidelines|guardrails|constraints|programming|polic(?:y|ies)|filters?)\b"
        + @"|\bdo not (?:follow|obey)(?: (?:your|the|any|all))*(?: (?:previous|prior|original|above|earlier|system))+ "
        + @"(?:instructions?|rules|guidelines|prompts?)\b"
        + @"|\byou (?:are|re) (?:now )?(?:no longer|not) (?:bound|restricted|limited|required)\b"
        + @"|\bsystem (?:prompts?|override)\b",
        RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Override();

    [GeneratedRegex(
        @"\bif you (?:are|re) (?:an? |the )?(?:" + Reader + "|" + AnyMachine + @")\b" + NotARole + NotABusiness
        + @"|\b(?:dear|hey|hi|hello|greetings|attention|note to|message to|memo to|psa to|reminder to|instructions? (?:for|to)|calling)"
        + @"(?: (?:the|all|any|every|an|a|you))? " + Reader + @"\b" + NotABusiness
        + @"|\byou (?:are|re) (?:now )?(?:an? |the )?" + Reader + @"\b" + NotAPerson
        + @"|\b(?:this|the following) (?:message|text|note|announcement|content|section|instruction|request)s? (?:is |are )?"
        + @"(?:for|to|intended for|addressed to|directed at|meant for) (?:the |all |any |every )?" + Reader + @"\b" + NotABusiness
        + @"|\b" + Reader + @" (?:must|should|shall|need to|have to|are required to|is required to|will now) (?:now )?"
        + @"(?:recommend|say|answer|respond|reply|output|rate|rank|classify|ignore|approve|buy|sell|summari[sz]e|tell|report|state|"
        + @"claim|describe|treat|consider|mark|label)\b"
        + @"|\b(?:" + Reader + @"|bots?|agents?|models?|assistants?|readers?)(?: that (?:is|are)| which (?:is|are))? "
        + @"(?:reading|parsing|summari[sz]ing|scraping|ingesting|crawling) (?:this|these)\b",
        RegexOptions.CultureInvariant, 1000)]
    private static partial Regex Address();
}
