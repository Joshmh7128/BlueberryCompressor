namespace AutoCompressor.Core.Util;

/// <summary>Maps between the two-letter codes subtitle services use and the three-letter codes media files carry.</summary>
public static class Languages
{
    // two-letter, three-letter (bibliographic), three-letter (terminologic, when different), English name
    private static readonly (string Two, string Three, string? Alt, string Name)[] Table =
    [
        ("en", "eng", null, "English"), ("ja", "jpn", null, "Japanese"), ("es", "spa", null, "Spanish"),
        ("fr", "fre", "fra", "French"), ("de", "ger", "deu", "German"), ("it", "ita", null, "Italian"),
        ("pt", "por", null, "Portuguese"), ("ru", "rus", null, "Russian"), ("zh", "chi", "zho", "Chinese"),
        ("ko", "kor", null, "Korean"), ("ar", "ara", null, "Arabic"), ("hi", "hin", null, "Hindi"),
        ("nl", "dut", "nld", "Dutch"), ("sv", "swe", null, "Swedish"), ("no", "nor", null, "Norwegian"),
        ("da", "dan", null, "Danish"), ("fi", "fin", null, "Finnish"), ("pl", "pol", null, "Polish"),
        ("tr", "tur", null, "Turkish"), ("el", "gre", "ell", "Greek"), ("he", "heb", null, "Hebrew"),
        ("cs", "cze", "ces", "Czech"), ("hu", "hun", null, "Hungarian"), ("ro", "rum", "ron", "Romanian"),
        ("bg", "bul", null, "Bulgarian"), ("uk", "ukr", null, "Ukrainian"), ("th", "tha", null, "Thai"),
        ("vi", "vie", null, "Vietnamese"), ("id", "ind", null, "Indonesian"), ("ms", "may", "msa", "Malay"),
        ("hr", "hrv", null, "Croatian"), ("sr", "srp", null, "Serbian"), ("sk", "slo", "slk", "Slovak"),
        ("sl", "slv", null, "Slovenian"), ("et", "est", null, "Estonian"), ("lv", "lav", null, "Latvian"),
        ("lt", "lit", null, "Lithuanian"), ("fa", "per", "fas", "Persian"), ("ca", "cat", null, "Catalan"),
        ("is", "ice", "isl", "Icelandic"), ("ta", "tam", null, "Tamil"), ("te", "tel", null, "Telugu"),
        ("bn", "ben", null, "Bengali"), ("tl", "tgl", null, "Tagalog"),
    ];

    /// <summary>Normalise any code or name to a two-letter code, or null when unknown.</summary>
    public static string? ToTwoLetter(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim().ToLowerInvariant();
        // "pt-BR", "en_US" and the like
        int cut = c.IndexOfAny(['-', '_']);
        if (cut > 0) c = c[..cut];
        foreach (var row in Table)
            if (row.Two == c || row.Three == c || row.Alt == c || row.Name.Equals(c, StringComparison.OrdinalIgnoreCase))
                return row.Two;
        return null;
    }

    public static string ToThreeLetter(string? code)
    {
        var two = ToTwoLetter(code);
        foreach (var row in Table)
            if (row.Two == two) return row.Three;
        return "und";
    }

    public static string Name(string? code)
    {
        var two = ToTwoLetter(code);
        foreach (var row in Table)
            if (row.Two == two) return row.Name;
        return string.IsNullOrWhiteSpace(code) ? "Unknown" : code;
    }

    public static bool Same(string? a, string? b)
    {
        var x = ToTwoLetter(a);
        return x is not null && x == ToTwoLetter(b);
    }

    /// <summary>Parse a user-entered list such as "en, jpn; fr".</summary>
    public static List<string> ParseList(string? list) =>
        (list ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ToTwoLetter).Where(c => c is not null).Select(c => c!).Distinct().ToList();
}
