using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DeckSearch;

/// <summary>
/// Scores a search query against a card's name and description. Pure string logic with no game
/// or Godot dependency, so it can be exercised outside the game.
///
/// A card matches when every query word is found somewhere (AND). Each word is tried as, in
/// order of strength: a substring of the name, a tag (card type or rarity, whole word only), a
/// typo of a name word or tag, a word in the description, a typo of a description word. As a
/// fallback the whole query may also match the name as a subsequence ("pstr" → "Perfected
/// Strike"). The best of the two scores wins. A word written with '#' ("#attack") is checked
/// against the tags alone; see <see cref="Query"/>.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>
    /// A card's searchable text, normalised once so a keystroke only costs the comparisons.
    /// </summary>
    public sealed class Target
    {
        public Target(string name, string description, IEnumerable<string>? tags = null, IEnumerable<string>? markedTags = null)
        {
            Name = Normalize(name);
            NameWords = SplitWords(Name);
            Description = Normalize(description);
            DescriptionWords = SplitWords(Description);
            TagWords = (tags ?? Array.Empty<string>()).SelectMany(t => SplitWords(Normalize(t))).Distinct().ToArray();
            MarkedTagWords = TagWords
                .Concat((markedTags ?? Array.Empty<string>()).SelectMany(t => SplitWords(Normalize(t))))
                .Distinct()
                .ToArray();
        }

        public string Name { get; }

        public string[] NameWords { get; }

        public string Description { get; }

        public string[] DescriptionWords { get; }

        /// <summary>Card type and rarity ("attack", "rare"), in the game's language and English.</summary>
        public string[] TagWords { get; }

        /// <summary>
        /// What a '#' word may match: the tags plus ones that only make sense asked for outright
        /// ("upgraded"), so a bare "upgrade" still finds cards that upgrade, not every upgraded card.
        /// </summary>
        public string[] MarkedTagWords { get; }
    }

    /// <summary>
    /// Lower-cases, strips accents, and turns every run of non-alphanumerics (including the "+" of
    /// an upgraded card) into a single space, so "Strike+" and "strike" compare equal.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        string decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        bool pendingSpace = false;
        foreach (char raw in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            // FormD is a no-op under invariant globalization, so fold Latin accents by table too.
            char c = raw >= LatinFoldStart && raw < LatinFoldStart + LatinFold.Length
                ? LatinFold[raw - LatinFoldStart]
                : raw;

            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    private const char LatinFoldStart = 'À';

    /// <summary>Base letters for U+00C0-U+017F (Latin-1 Supplement letters and Latin Extended-A).</summary>
    private const string LatinFold =
        "aaaaaaaceeeeiiiidnooooo ouuuuytsaaaaaaaceeeeiiiidnooooo ouuuuyty" +
        "aaaaaaccccccccddddeeeeeeeeeegggggggghhhhiiiiiiiiiiiijjkkkllllllllll" +
        "nnnnnnnnnoooooooorrrrrrssssssssttttttuuuuuuuuuuuuwwyyyzzzzzzs";

    /// <summary>
    /// What the player typed, split into the words to search for and the words marked with '#',
    /// which match the card's type or rarity only, never its name or text: "#attack" finds Attacks
    /// but not a Skill that says "attack".
    /// </summary>
    public sealed class Query
    {
        public const char TagMark = '#';

        public static readonly Query Empty = new("", Array.Empty<string>());

        private Query(string text, string[] tagWords)
        {
            Text = text;
            TagWords = tagWords;
            Key = string.Join(' ', TagWords.Select(t => TagMark + t).Prepend(Text).Where(w => w.Length > 0));
        }

        /// <summary>The normalised words to look for in names, tags and card text.</summary>
        public string Text { get; }

        /// <summary>The normalised words that must match a tag.</summary>
        public string[] TagWords { get; }

        /// <summary>Equal for queries that search alike, so "#attack " does not redraw again.</summary>
        public string Key { get; }

        public bool IsEmpty => Text.Length == 0 && TagWords.Length == 0;

        /// <summary>
        /// Everything after a '#' up to the next space is a tag word, so "#rare #attack",
        /// "#attack#rare" and "strike #attack" all work. A lone '#' while typing asks for nothing.
        /// </summary>
        public static Query Parse(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return Empty;
            }

            var text = new List<string>();
            var tags = new List<string>();
            foreach (string token in raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = token.Split(TagMark);
                text.Add(Normalize(parts[0]));
                tags.AddRange(parts.Skip(1).SelectMany(part => SplitWords(Normalize(part))));
            }

            return new Query(
                string.Join(' ', text.Where(t => t.Length > 0)),
                tags.Distinct().ToArray());
        }

        public override string ToString() => Key;
    }

    /// <summary>
    /// Returns 0 for no match, otherwise a score where higher is better (roughly 0-120).
    /// An empty query matches everything.
    /// </summary>
    public static double Score(string normalizedQuery, Target target, bool includeDescription)
    {
        if (normalizedQuery.Length == 0)
        {
            return 100;
        }

        return Math.Max(
            WordsScore(normalizedQuery, target, includeDescription),
            SubsequenceScore(normalizedQuery, target.Name));
    }

    /// <summary>
    /// Every '#' word must match a tag and the rest must match as usual; the score is the
    /// average over all the words.
    /// </summary>
    public static double Score(Query query, Target target, bool includeDescription)
    {
        if (query.TagWords.Length == 0)
        {
            return Score(query.Text, target, includeDescription);
        }

        double total = 0;
        foreach (string word in query.TagWords)
        {
            double best = TagOnlyScore(word, target);
            if (best <= 0)
            {
                return 0;
            }

            total += best;
        }

        int textWords = 0;
        if (query.Text.Length > 0)
        {
            double text = Score(query.Text, target, includeDescription);
            if (text <= 0)
            {
                return 0;
            }

            textWords = SplitWords(query.Text).Length;
            total += text * textWords;
        }

        return total / (query.TagWords.Length + textWords);
    }

    /// <summary>
    /// The player asked for a tag outright, so unlike a bare word a prefix counts too: "#att"
    /// already shows the Attacks while the word is still being typed.
    /// </summary>
    private static double TagOnlyScore(string word, Target target)
    {
        string[] tags = target.MarkedTagWords;
        if (tags.Contains(word))
        {
            return 110;
        }

        if (tags.Any(w => w.StartsWith(word, StringComparison.Ordinal)))
        {
            return 100;
        }

        return TypoScore(word, tags, 75, wholeWordOnly: true);
    }

    private static double WordsScore(string query, Target target, bool includeDescription)
    {
        string[] words = SplitWords(query);
        double total = 0;
        foreach (string word in words)
        {
            double best = WordScore(word, target, includeDescription);
            if (best <= 0)
            {
                return 0;
            }

            total += best;
        }

        return total / words.Length;
    }

    private static double WordScore(string word, Target target, bool includeDescription)
    {
        // One or two letters inside a word ("a" in "bash") is noise; only count them at a word start.
        bool shortWord = word.Length < 3;
        if (target.NameWords.Any(w => w.StartsWith(word, StringComparison.Ordinal)))
        {
            return 120;
        }

        if (!shortWord && target.Name.Contains(word, StringComparison.Ordinal))
        {
            return 100;
        }

        // Tags match whole words only: as prefixes, "com" would pull in every Common card and
        // "pow" every Power while the player is still typing a name.
        if (target.TagWords.Contains(word))
        {
            return 110;
        }

        double best = Math.Max(
            TypoScore(word, target.NameWords, 75),
            TypoScore(word, target.TagWords, 75, wholeWordOnly: true));

        if (includeDescription && !shortWord)
        {
            // Prefix of a description word only: a bare substring would let "a" or "at" match
            // nearly every card through words like "damage" and "attack".
            if (target.DescriptionWords.Any(w => w.StartsWith(word, StringComparison.Ordinal)))
            {
                best = Math.Max(best, 50);
            }
            else
            {
                best = Math.Max(best, TypoScore(word, target.DescriptionWords, 35));
            }
        }

        return best;
    }

    /// <summary>
    /// Compares the query word against each target word, and against the target word's prefix
    /// of the same length so a half-typed word with a typo ("defel") still finds "deflect".
    /// </summary>
    private static double TypoScore(string word, string[] targetWords, double baseScore, bool wholeWordOnly = false)
    {
        int allowed = AllowedEdits(word.Length);
        if (allowed == 0)
        {
            return 0;
        }

        int bestDistance = int.MaxValue;
        foreach (string candidate in targetWords)
        {
            int distance = Distance(word, candidate);
            if (!wholeWordOnly && candidate.Length > word.Length)
            {
                distance = Math.Min(distance, Distance(word, candidate.Substring(0, word.Length)));
            }

            bestDistance = Math.Min(bestDistance, distance);
        }

        return bestDistance <= allowed ? baseScore - (15 * bestDistance) : 0;
    }

    /// <summary>Short words get no typo allowance: "cat" → "bat" is a different word, not a typo.</summary>
    public static int AllowedEdits(int length) => length switch
    {
        < 4 => 0,
        < 8 => 1,
        _ => 2,
    };

    /// <summary>
    /// fzf-style: every query letter appears in the name in order. The match must begin at the
    /// start of a word, and rewards letters that land on word starts or run consecutively, so
    /// "pstr" scores well against "perfected strike" while scattered hits fall under the bar.
    /// </summary>
    private static double SubsequenceScore(string query, string name)
    {
        string compact = query.Replace(" ", "");
        if (compact.Length < 2 || name.Length == 0)
        {
            return 0;
        }

        double best = 0;
        for (int start = 0; start < name.Length; start++)
        {
            bool wordStart = start == 0 || name[start - 1] == ' ';
            if (!wordStart || name[start] != compact[0])
            {
                continue;
            }

            best = Math.Max(best, SubsequenceFrom(compact, name, start));
        }

        return best;
    }

    private static double SubsequenceFrom(string compact, string name, int start)
    {
        double score = 10;
        int previous = start;
        int q = 1;
        for (int i = start + 1; i < name.Length && q < compact.Length; i++)
        {
            if (name[i] != compact[q])
            {
                continue;
            }

            bool wordStart = name[i - 1] == ' ';
            if (i == previous + 1)
            {
                score += 8;
            }
            else if (wordStart)
            {
                score += 6;
            }
            else
            {
                score -= Math.Min(i - previous - 1, 6);
            }

            previous = i;
            q++;
        }

        if (q < compact.Length)
        {
            return 0;
        }

        // Scale to the per-letter maximum so short and long queries are judged alike.
        double perLetterMax = 8.0;
        double quality = (score - 10) / (perLetterMax * (compact.Length - 1));
        return quality < 0.5 ? 0 : 40 + (40 * quality);
    }

    /// <summary>Damerau-Levenshtein (optimal string alignment): a swapped pair counts as one edit.</summary>
    public static int Distance(string a, string b)
    {
        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }

    private static string[] SplitWords(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
