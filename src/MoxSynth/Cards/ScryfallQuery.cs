using System.Globalization;
using System.Text;
using Dapper;

namespace MoxSynth.Cards;

/// <summary>
/// Result of translating a Scryfall-syntax query into SQL for the AllPrintings database.
/// <see cref="WhereSql"/> is written against alias <c>f</c> (cards – any face) and <c>fs</c> (sets);
/// <see cref="OrderBy"/> against <c>c</c> (primary face) and <c>s</c> (sets).
/// </summary>
public sealed class ScryfallFilter
{
    public string? WhereSql { get; init; }
    public string? OrderBy { get; init; }
    public DynamicParameters Parameters { get; init; } = new();
    /// <summary>Human readable interpretation, e.g. "type line contains “creature” and colour ≥ RG".</summary>
    public string? Description { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>False when nothing usable was entered yet (e.g. only "t:" or an unknown keyword).</summary>
    public bool HasEffect => WhereSql is not null || OrderBy is not null;

    public static readonly ScryfallFilter Empty = new();
}

/// <summary>
/// Parser for a practical subset of Scryfall's search syntax (https://scryfall.com/docs/syntax):
/// implicit AND, <c>OR</c>, <c>-</c> negation, parentheses, quoted values and the operators
/// <c>: = != &lt; &lt;= &gt; &gt;=</c>. Unknown keywords are reported as warnings and ignored, like Scryfall does.
/// </summary>
public sealed class ScryfallQueryParser
{
    private static readonly Dictionary<string, string> ColorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["white"] = "W", ["blue"] = "U", ["black"] = "B", ["red"] = "R", ["green"] = "G",
        ["colorless"] = "", ["colourless"] = "",
        ["azorius"] = "WU", ["dimir"] = "UB", ["rakdos"] = "BR", ["gruul"] = "RG", ["selesnya"] = "GW",
        ["orzhov"] = "WB", ["izzet"] = "UR", ["golgari"] = "BG", ["boros"] = "RW", ["simic"] = "GU",
        ["bant"] = "GWU", ["esper"] = "WUB", ["grixis"] = "UBR", ["jund"] = "BRG", ["naya"] = "RGW",
        ["abzan"] = "WBG", ["jeskai"] = "URW", ["sultai"] = "BGU", ["mardu"] = "RWB", ["temur"] = "GUR",
        ["wubrg"] = "WUBRG", ["rainbow"] = "WUBRG",
    };

    private static readonly Dictionary<string, string> FormatAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["edh"] = "commander", ["cedh"] = "commander", ["pdh"] = "paupercommander", ["oldschool"] = "oldschool",
    };

    private const string RarityOrdinal =
        "CASE {0}.rarity WHEN 'common' THEN 0 WHEN 'uncommon' THEN 1 WHEN 'rare' THEN 2 WHEN 'special' THEN 2.5 " +
        "WHEN 'mythic' THEN 3 WHEN 'bonus' THEN 4 END";

    private static readonly Dictionary<string, (string Rarity, double Ordinal)> Rarities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["c"] = ("common", 0), ["common"] = ("common", 0),
        ["u"] = ("uncommon", 1), ["uncommon"] = ("uncommon", 1),
        ["r"] = ("rare", 2), ["rare"] = ("rare", 2),
        ["s"] = ("special", 2.5), ["special"] = ("special", 2.5),
        ["m"] = ("mythic", 3), ["mythic"] = ("mythic", 3),
        ["b"] = ("bonus", 4), ["bonus"] = ("bonus", 4),
    };

    private static readonly HashSet<string> Layouts = new(StringComparer.OrdinalIgnoreCase)
    {
        "split", "flip", "transform", "meld", "adventure", "saga", "class", "case", "leveler", "prototype",
        "mutate", "aftermath", "augment", "host", "planar", "scheme", "vanguard", "reversible_card", "prepare",
    };

    private readonly IReadOnlySet<string> _formats;
    private readonly string? _ownedFinishColumn;
    private readonly DynamicParameters _p = new();
    private readonly List<string> _warnings = [];
    private List<Token> _tokens = [];
    private int _pos;
    private int _paramIndex;
    private string? _orderBy;
    private string? _orderLabel;
    private bool _descending;

    private ScryfallQueryParser(IReadOnlySet<string> formats, string? ownedFinishColumn)
    {
        _formats = formats;
        _ownedFinishColumn = ownedFinishColumn;
    }

    /// <param name="formats">Valid format column names of the cardLegalities table (whitelist for f:/legal:).</param>
    /// <param name="ownedFinishColumn">
    /// When searching a collection: SQL column with the finish of the owned copy (e.g. <c>cc.finish</c>).
    /// is:foil / is:nonfoil / is:etched then mean "my copy is foil" instead of "this printing exists in foil".
    /// </param>
    public static ScryfallFilter Parse(string? input, IReadOnlySet<string> formats, string? ownedFinishColumn = null)
    {
        if (string.IsNullOrWhiteSpace(input)) return ScryfallFilter.Empty;
        return new ScryfallQueryParser(formats, ownedFinishColumn).Run(input);
    }

    private ScryfallFilter Run(string input)
    {
        _tokens = Tokenize(input);

        var parts = new List<Fragment>();
        while (_pos < _tokens.Count)
        {
            if (ParseOr() is { } f) parts.Add(f);
            if (_pos < _tokens.Count) _pos++; // skip a stray ')'
        }
        var root = Combine(parts, "AND", " and ");

        var description = root?.Description;
        if (_orderLabel is not null)
        {
            var order = $"sorted by {_orderLabel}{(_descending ? " (descending)" : "")}";
            description = description is null ? order : $"{description}, {order}";
        }

        return new ScryfallFilter
        {
            WhereSql = root?.Sql,
            OrderBy = _orderBy is null ? null : ApplyDirection(_orderBy, _descending),
            Parameters = _p,
            Description = description,
            Warnings = _warnings,
        };
    }

    // ------------------------------------------------------------------ grammar

    private Fragment? ParseOr()
    {
        var parts = new List<Fragment>();
        if (ParseAnd() is { } first) parts.Add(first);
        while (_pos < _tokens.Count && _tokens[_pos].Kind == TokenKind.Or)
        {
            _pos++;
            if (ParseAnd() is { } next) parts.Add(next);
        }
        return Combine(parts, "OR", " or ");
    }

    private Fragment? ParseAnd()
    {
        var parts = new List<Fragment>();
        while (_pos < _tokens.Count && _tokens[_pos].Kind is not (TokenKind.Or or TokenKind.RParen))
        {
            if (ParseUnary() is { } f) parts.Add(f);
        }
        return Combine(parts, "AND", " and ");
    }

    private Fragment? ParseUnary()
    {
        var token = _tokens[_pos++];
        switch (token.Kind)
        {
            case TokenKind.Not:
                if (_pos >= _tokens.Count || _tokens[_pos].Kind is TokenKind.Or or TokenKind.RParen) return null;
                var inner = ParseUnary();
                return inner is null ? null : new Fragment($"NOT ({inner.Sql})", $"not {inner.Description}");

            case TokenKind.LParen:
                var group = ParseOr();
                if (_pos < _tokens.Count && _tokens[_pos].Kind == TokenKind.RParen) _pos++;
                return group is null ? null : new Fragment($"({group.Sql})", $"({group.Description})");

            case TokenKind.Term:
                return Term(token);

            default:
                return null;
        }
    }

    private static Fragment? Combine(List<Fragment> parts, string sqlOp, string textOp) => parts.Count switch
    {
        0 => null,
        1 => parts[0],
        _ => new Fragment(
            string.Join($" {sqlOp} ", parts.Select(p => $"({p.Sql})")),
            string.Join(textOp, parts.Select(p => p.Description))),
    };

    // ------------------------------------------------------------------ terms

    private Fragment? Term(Token t)
    {
        var value = t.Value.Trim();
        if (value.Length == 0) return null; // still being typed, e.g. "t:"

        if (t.Key is null) return NameTerm(value, t.Exact);

        switch (t.Key)
        {
            case "name":
                return NameTerm(value, t.Exact);
            case "t" or "type":
                return TextTerm(t, "f.type", "type line");
            case "o" or "oracle" or "fo" or "fulloracle":
                return TextTerm(t, "f.text", "rules text");
            case "ft" or "flavor" or "flavour":
                return TextTerm(t, "f.flavorText", "flavour text");
            case "a" or "artist":
                return TextTerm(t, "f.artist", "artist");
            case "kw" or "keyword":
                return TextTerm(t, "f.keywords", "keywords");
            case "m" or "mana":
                return ManaTerm(t, value);
            case "c" or "color" or "colour":
                return ColorTerm(t, value, "f.colors", "colour", defaultOp: ">=");
            case "id" or "identity" or "ci" or "commander":
                return ColorTerm(t, value, "f.colorIdentity", "colour identity", defaultOp: "<=");
            case "mv" or "cmc" or "manavalue":
                return NumericTerm(t, value, "mana value");
            case "pow" or "power":
                return NumericTerm(t, value, "power");
            case "tou" or "toughness":
                return NumericTerm(t, value, "toughness");
            case "loy" or "loyalty":
                return NumericTerm(t, value, "loyalty");
            case "def" or "defense" or "defence":
                return NumericTerm(t, value, "defense");
            case "r" or "rarity":
                return RarityTerm(t, value);
            case "s" or "set" or "e" or "edition":
                return EqualsTerm(t, "f.setCode", value.ToUpperInvariant(), "set");
            case "st" or "settype":
                return EqualsTerm(t, "fs.type", value.ToLowerInvariant(), "set type");
            case "cn" or "number":
                return CollectorNumberTerm(t, value);
            case "is":
                return IsTerm(value, negate: false);
            case "not":
                return IsTerm(value, negate: true);
            case "f" or "format" or "legal":
                return LegalityTerm(value, "Legal", "legal in");
            case "banned":
                return LegalityTerm(value, "Banned", "banned in");
            case "restricted":
                return LegalityTerm(value, "Restricted", "restricted in");
            case "game":
                return GameTerm(value);
            case "year":
                return YearTerm(t, value);
            case "date":
                return DateTerm(t, value);
            case "lang" or "language":
                return EqualsTerm(t, "f.language", LanguageName(value), "language");
            case "border":
                return EqualsTerm(t, "f.borderColor", value.ToLowerInvariant(), "border");
            case "frame":
                return EqualsTerm(t, "f.frameVersion", value.ToLowerInvariant(), "frame");
            case "wm" or "watermark":
                return EqualsTerm(t, "f.watermark", value.ToLowerInvariant(), "watermark");
            case "order":
                SetOrder(value);
                return null;
            case "direction" or "dir":
                _descending = value.StartsWith("desc", StringComparison.OrdinalIgnoreCase);
                return null;
            case "unique" or "display" or "prefer" or "include":
                _warnings.Add($"“{t.Key}:” only affects how Scryfall displays results and is ignored here.");
                return null;
            default:
                _warnings.Add($"Unknown keyword “{t.Key}{t.Op}”, ignored.");
                return null;
        }
    }

    private Fragment NameTerm(string value, bool exact)
    {
        var p = Param(exact ? value : LikeContains(value));
        return exact
            ? new Fragment($"(f.name = {p} COLLATE NOCASE OR f.faceName = {p} COLLATE NOCASE)", $"name is “{value}”")
            : new Fragment(
                $@"(COALESCE(f.name, '') LIKE {p} ESCAPE '\' OR COALESCE(f.faceName, '') LIKE {p} ESCAPE '\' OR COALESCE(f.asciiName, '') LIKE {p} ESCAPE '\')",
                $"name contains “{value}”");
    }

    private Fragment? TextTerm(Token t, string column, string label)
    {
        if (t.Op is not (":" or "=" or "!="))
            return Invalid(t, "only : = != are supported for text");

        var p = Param(LikeContains(t.Value.Trim()));
        var sql = $@"COALESCE({column}, '') LIKE {p} ESCAPE '\'";
        return t.Op == "!="
            ? new Fragment($"NOT ({sql})", $"{label} doesn't contain “{t.Value.Trim()}”")
            : new Fragment(sql, $"{label} contains “{t.Value.Trim()}”");
    }

    private Fragment? ManaTerm(Token t, string value)
    {
        // "2gg" → "{2}{G}{G}"; "{2}{G}{G}" stays as is.
        var cost = value.Contains('{')
            ? value.ToUpperInvariant()
            : string.Concat(SplitManaShorthand(value).Select(s => "{" + s.ToUpperInvariant() + "}"));
        var p = Param(LikeContains(cost));
        var sql = $@"COALESCE(f.manaCost, '') LIKE {p} ESCAPE '\'";
        return t.Op == "!="
            ? new Fragment($"NOT ({sql})", $"mana cost doesn't contain {cost}")
            : new Fragment(sql, $"mana cost contains {cost}");
    }

    private static IEnumerable<string> SplitManaShorthand(string value)
    {
        var i = 0;
        while (i < value.Length)
        {
            if (char.IsAsciiDigit(value[i]))
            {
                var start = i;
                while (i < value.Length && char.IsAsciiDigit(value[i])) i++;
                yield return value[start..i];
            }
            else
            {
                yield return value[i++].ToString();
            }
        }
    }

    private Fragment? ColorTerm(Token t, string value, string column, string label, string defaultOp)
    {
        var col = $"COALESCE({column}, '')";
        var v = value.ToLowerInvariant();

        if (v is "m" or "multi" or "multicolor" or "multicolour")
        {
            var multi = $"{col} LIKE '%,%'";
            return t.Op == "!="
                ? new Fragment($"NOT ({multi})", $"{label} is not multicoloured")
                : new Fragment(multi, $"{label} is multicoloured");
        }

        string letters;
        if (ColorNames.TryGetValue(v, out var named))
        {
            letters = named;
        }
        else if (v == "c")
        {
            letters = "";
        }
        else if (v.All(ch => "wubrgc".Contains(ch)))
        {
            letters = new string(v.ToUpperInvariant().Where(ch => ch != 'C').Distinct().ToArray());
        }
        else
        {
            return Invalid(t, "use colour letters (wubrg, c) or names like azorius, jund, colorless, multicolor");
        }

        var op = t.Op is ":" ? defaultOp : t.Op;
        if (letters.Length == 0 && t.Op == ":") op = "="; // c:c = colourless

        string Has(char x) => $"{col} LIKE '%{x}%'"; // letters only come from the fixed WUBRG alphabet
        var inSet = letters.ToCharArray();
        var outSet = "WUBRG".Where(x => !letters.Contains(x)).ToArray();

        var allIn = inSet.Length == 0 ? "1=1" : string.Join(" AND ", inSet.Select(Has));
        var noneOut = outSet.Length == 0 ? "1=1" : string.Join(" AND ", outSet.Select(x => $"NOT ({Has(x)})"));
        var anyOut = outSet.Length == 0 ? "0=1" : string.Join(" OR ", outSet.Select(Has));
        var anyInMissing = inSet.Length == 0 ? "0=1" : string.Join(" OR ", inSet.Select(x => $"NOT ({Has(x)})"));

        var shown = letters.Length == 0 ? "colourless" : letters;
        return op switch
        {
            ">=" => new Fragment(allIn, $"{label} includes {shown}"),
            "=" => new Fragment($"({allIn}) AND ({noneOut})", $"{label} is exactly {shown}"),
            "<=" => new Fragment(noneOut, $"{label} is within {shown}"),
            ">" => new Fragment($"({allIn}) AND ({anyOut})", $"{label} includes {shown} and more"),
            "<" => new Fragment($"({noneOut}) AND ({anyInMissing})", $"{label} is a strict subset of {shown}"),
            "!=" => new Fragment($"NOT (({allIn}) AND ({noneOut}))", $"{label} is not exactly {shown}"),
            _ => Invalid(t, "unsupported operator"),
        };
    }

    private static string? StatColumn(string name, string alias = "f") => name.ToLowerInvariant() switch
    {
        "mv" or "cmc" or "manavalue" or "mana value" => $"{alias}.manaValue",
        "pow" or "power" => NumericText($"{alias}.power"),
        "tou" or "toughness" => NumericText($"{alias}.toughness"),
        "loy" or "loyalty" => NumericText($"{alias}.loyalty"),
        "def" or "defense" or "defence" => NumericText($"{alias}.defense"),
        _ => null,
    };

    private static string StatLabel(string name) => name.ToLowerInvariant() switch
    {
        "mv" or "cmc" or "manavalue" => "mana value",
        "pow" => "power",
        "tou" => "toughness",
        "loy" => "loyalty",
        "def" or "defence" => "defense",
        var other => other,
    };

    // Power/toughness/loyalty are TEXT ("*", "1+*", "X"); only values starting with a digit compare numerically.
    private static string NumericText(string column) =>
        $"(CASE WHEN {column} GLOB '[0-9]*' THEN CAST({column} AS REAL) END)";

    private Fragment? NumericTerm(Token t, string value, string label)
    {
        var column = StatColumn(label)!;
        var op = SqlComparison(t.Op);

        if (StatColumn(value) is { } other) // pow>tou
            return new Fragment($"{column} {op} {other}", $"{label} {Pretty(t.Op)} {StatLabel(value)}");

        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            return Invalid(t, "expected a number");

        return new Fragment($"{column} {op} {Param(number)}", $"{label} {Pretty(t.Op)} {value}");
    }

    private Fragment? RarityTerm(Token t, string value)
    {
        if (!Rarities.TryGetValue(value, out var rarity))
            return Invalid(t, "use common, uncommon, rare, mythic, special or bonus");

        if (t.Op is ":" or "=")
            return new Fragment($"f.rarity = {Param(rarity.Rarity)}", $"rarity is {rarity.Rarity}");
        if (t.Op is "!=")
            return new Fragment($"COALESCE(f.rarity, '') <> {Param(rarity.Rarity)}", $"rarity is not {rarity.Rarity}");

        return new Fragment(
            $"({string.Format(RarityOrdinal, "f")}) {t.Op} {Param(rarity.Ordinal)}",
            $"rarity {Pretty(t.Op)} {rarity.Rarity}");
    }

    private Fragment? EqualsTerm(Token t, string column, string value, string label)
    {
        return t.Op switch
        {
            ":" or "=" => new Fragment($"{column} = {Param(value)} COLLATE NOCASE", $"{label} is {value}"),
            "!=" => new Fragment($"COALESCE({column}, '') <> {Param(value)} COLLATE NOCASE", $"{label} is not {value}"),
            _ => Invalid(t, "only : = != are supported"),
        };
    }

    private Fragment? CollectorNumberTerm(Token t, string value)
    {
        if (t.Op is ":" or "=" or "!=")
        {
            var cmp = t.Op == "!=" ? "<>" : "=";
            return new Fragment($"f.number {cmp} {Param(value)} COLLATE NOCASE",
                $"collector number {(t.Op == "!=" ? "is not" : "is")} {value}");
        }

        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return Invalid(t, "expected a number");
        return new Fragment($"CAST(f.number AS INTEGER) {t.Op} {Param(n)}", $"collector number {Pretty(t.Op)} {n}");
    }

    private Fragment? IsTerm(string value, bool negate)
    {
        static string Flag(string column) => $"COALESCE(f.{column}, 0) = 1";
        static string Finish(string finish) => $"(', ' || COALESCE(f.finishes, '') || ',') LIKE '%, {finish},%'";
        static string TypeHas(params string[] types) =>
            "(" + string.Join(" OR ", types.Select(x => $"COALESCE(f.type, '') LIKE '%{x}%'")) + ")";

        var v = value.ToLowerInvariant();
        if (_ownedFinishColumn is not null && v is "foil" or "nonfoil" or "etched")
        {
            var owned = $"{_ownedFinishColumn} = '{v}'"; // v is one of three literals
            return negate
                ? new Fragment($"NOT ({owned})", $"your copy is not {v}")
                : new Fragment(owned, $"your copy is {v}");
        }

        string? sql = v switch
        {
            "foil" or "nonfoil" or "etched" => Finish(v),
            "promo" => Flag("isPromo"),
            "reprint" => Flag("isReprint"),
            "firstprint" or "firstprinting" => $"NOT ({Flag("isReprint")})",
            "reserved" => Flag("isReserved"),
            "fullart" or "full" => Flag("isFullArt"),
            "textless" => Flag("isTextless"),
            "funny" => Flag("isFunny"),
            "oversized" => Flag("isOversized"),
            "digital" => Flag("isOnlineOnly"),
            "gamechanger" => Flag("isGameChanger"),
            "timeshifted" => Flag("isTimeshifted"),
            "spotlight" => Flag("isStorySpotlight"),
            "alternative" => Flag("isAlternative"),
            "dfc" or "doublefaced" => "f.layout IN ('transform', 'modal_dfc', 'meld', 'reversible_card')",
            "mdfc" => "f.layout = 'modal_dfc'",
            "commander" => @"COALESCE(f.leadershipSkills, '') LIKE '%""commander"":true%'",
            "brawler" => @"COALESCE(f.leadershipSkills, '') LIKE '%""brawl"":true%'",
            "oathbreaker" => @"COALESCE(f.leadershipSkills, '') LIKE '%""oathbreaker"":true%'",
            "permanent" => TypeHas("Artifact", "Creature", "Enchantment", "Land", "Planeswalker", "Battle"),
            "spell" => $"NOT {TypeHas("Land")}",
            "historic" => TypeHas("Legendary", "Artifact", "Saga"),
            "vanilla" => $"{TypeHas("Creature")} AND COALESCE(f.text, '') = ''",
            "paper" or "arena" or "mtgo" => $"COALESCE(f.availability, '') LIKE '%{v}%'",
            _ when Layouts.Contains(v) => $"f.layout = '{v}'", // whitelisted literal
            _ => null,
        };

        if (sql is null)
        {
            _warnings.Add($"Unknown “{(negate ? "not" : "is")}:{value}”, ignored.");
            return null;
        }
        return negate ? new Fragment($"NOT ({sql})", $"is not {v}") : new Fragment(sql, $"is {v}");
    }

    private Fragment? LegalityTerm(string value, string status, string label)
    {
        var format = FormatAliases.TryGetValue(value, out var alias) ? alias : value.ToLowerInvariant();
        if (!_formats.Contains(format))
        {
            _warnings.Add($"Unknown format “{value}”. Known: {string.Join(", ", _formats.Order())}.");
            return null;
        }
        // format is whitelisted against the cardLegalities columns, so it is safe to use as identifier.
        return new Fragment(
            $"EXISTS (SELECT 1 FROM cardLegalities l WHERE l.uuid = f.uuid AND l.\"{format}\" = '{status}')",
            $"{label} {format}");
    }

    private Fragment? GameTerm(string value)
    {
        var v = value.ToLowerInvariant();
        if (v is not ("paper" or "arena" or "mtgo"))
        {
            _warnings.Add($"Unknown game “{value}” (use paper, arena or mtgo), ignored.");
            return null;
        }
        return new Fragment($"COALESCE(f.availability, '') LIKE '%{v}%'", $"available on {v}");
    }

    private Fragment? YearTerm(Token t, string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year))
            return Invalid(t, "expected a year");
        return new Fragment($"CAST(substr(fs.releaseDate, 1, 4) AS INTEGER) {SqlComparison(t.Op)} {Param(year)}",
            $"year {Pretty(t.Op)} {year}");
    }

    private Fragment? DateTerm(Token t, string value)
    {
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return Invalid(t, "expected a date like 2020-01-31");
        var iso = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return new Fragment($"fs.releaseDate {SqlComparison(t.Op)} {Param(iso)}", $"released {Pretty(t.Op)} {iso}");
    }

    private void SetOrder(string value)
    {
        (string Sql, string Label)? order = value.ToLowerInvariant() switch
        {
            "name" => ("c.name COLLATE NOCASE", "name"),
            "set" => ("c.setCode|CAST(c.number AS INTEGER)|c.number", "set"),
            "released" or "date" => ("s.releaseDate", "release date"),
            "cmc" or "mv" or "manavalue" => ("c.manaValue", "mana value"),
            "power" or "pow" => (StatColumn("power", "c")!, "power"),
            "toughness" or "tou" => (StatColumn("toughness", "c")!, "toughness"),
            "rarity" => (string.Format(RarityOrdinal, "c"), "rarity"),
            "cn" or "number" => ("CAST(c.number AS INTEGER)|c.number", "collector number"),
            "artist" => ("c.artist COLLATE NOCASE", "artist"),
            "edhrec" => ("COALESCE(c.edhrecRank, 999999)", "EDHREC rank"),
            "color" or "colour" => ("length(COALESCE(c.colors, ''))|c.colors", "colour"),
            _ => null,
        };
        if (order is null)
        {
            _warnings.Add($"Unknown sort order “{value}”, ignored.");
            return;
        }
        (_orderBy, _orderLabel) = order.Value;
    }

    private static string ApplyDirection(string orderBy, bool descending) =>
        string.Join(", ", orderBy.Split('|').Select(part => descending ? $"{part} DESC" : part));

    private Fragment? Invalid(Token t, string reason)
    {
        _warnings.Add($"“{t.Key}{t.Op}{t.Value}”: {reason}; ignored.");
        return null;
    }

    // ------------------------------------------------------------------ helpers

    private string Param(object value)
    {
        var name = $"sf{_paramIndex++}";
        _p.Add(name, value);
        return "@" + name;
    }

    private static string SqlComparison(string? op) => op switch
    {
        ":" or "=" => "=",
        "!=" => "<>",
        _ => op ?? "=",
    };

    private static string Pretty(string? op) => op switch
    {
        ":" or "=" => "=",
        "!=" => "≠",
        "<=" => "≤",
        ">=" => "≥",
        _ => op ?? "=",
    };

    private static string LanguageName(string value) => value.ToLowerInvariant() switch
    {
        "en" => "English", "de" => "German", "fr" => "French", "it" => "Italian", "es" => "Spanish",
        "pt" => "Portuguese (Brazil)", "ja" or "jp" => "Japanese", "ko" => "Korean", "ru" => "Russian",
        "zhs" => "Chinese Simplified", "zht" => "Chinese Traditional", "ph" => "Phyrexian",
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant()),
    };

    internal static string LikeContains(string value)
    {
        var sb = new StringBuilder("%");
        foreach (var ch in value)
        {
            if (ch is '%' or '_' or '\\') sb.Append('\\');
            sb.Append(ch);
        }
        return sb.Append('%').ToString();
    }

    // ------------------------------------------------------------------ tokenizer

    private enum TokenKind { Term, LParen, RParen, Or, Not }

    private sealed record Token(TokenKind Kind, string? Key = null, string? Op = null, string Value = "", bool Exact = false);

    private sealed record Fragment(string Sql, string Description);

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var ch = s[i];
            if (char.IsWhiteSpace(ch)) { i++; continue; }
            if (ch == '(') { tokens.Add(new Token(TokenKind.LParen)); i++; continue; }
            if (ch == ')') { tokens.Add(new Token(TokenKind.RParen)); i++; continue; }
            if (ch == '-' && i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1]))
            {
                tokens.Add(new Token(TokenKind.Not));
                i++;
                continue;
            }

            var exact = false;
            if (ch == '!' && i + 1 < s.Length && !char.IsWhiteSpace(s[i + 1]))
            {
                exact = true;
                i++;
            }

            if (i < s.Length && s[i] == '"')
            {
                tokens.Add(new Token(TokenKind.Term, Value: ReadQuoted(s, ref i), Exact: exact));
                continue;
            }

            // keyword + operator?
            var start = i;
            while (i < s.Length && char.IsAsciiLetter(s[i])) i++;
            var key = s[start..i];
            string? op = null;
            if (key.Length > 0 && i < s.Length)
            {
                if (s[i] is ':' or '=') { op = s[i].ToString(); i++; }
                else if (s[i] == '!' && i + 1 < s.Length && s[i + 1] == '=') { op = "!="; i += 2; }
                else if (s[i] is '<' or '>')
                {
                    op = s[i].ToString();
                    i++;
                    if (i < s.Length && s[i] == '=') { op += "="; i++; }
                }
            }

            if (op is not null)
            {
                var value = i < s.Length && s[i] == '"' ? ReadQuoted(s, ref i) : ReadBare(s, ref i);
                tokens.Add(new Token(TokenKind.Term, key.ToLowerInvariant(), op, value, exact));
                continue;
            }

            i = start;
            var word = ReadBare(s, ref i);
            if (word.Length == 0) { i++; continue; } // safety: always make progress
            if (!exact && word.Equals("or", StringComparison.OrdinalIgnoreCase)) tokens.Add(new Token(TokenKind.Or));
            else if (!exact && word.Equals("and", StringComparison.OrdinalIgnoreCase)) { /* implicit */ }
            else tokens.Add(new Token(TokenKind.Term, Value: word, Exact: exact));
        }
        return tokens;
    }

    private static string ReadQuoted(string s, ref int i)
    {
        i++; // opening quote
        var start = i;
        while (i < s.Length && s[i] != '"') i++;
        var value = s[start..i];
        if (i < s.Length) i++; // closing quote
        return value;
    }

    private static string ReadBare(string s, ref int i)
    {
        var start = i;
        while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('(' or ')')) i++;
        return s[start..i];
    }
}
