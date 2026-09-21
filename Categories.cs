namespace SimpleWallpaper;

/// <summary>
/// The subjects the wallpaper API offers, in the order the picker and every request use them.
/// Only these keys are ever sent to the API; the picked ones are remembered in state.json.
/// </summary>
internal static class Categories
{
    /// <summary>风景 first, then the remaining subjects in the order the API documents them.</summary>
    public static readonly string[] All =
    {
        "nature", "anime", "game", "animal", "city", "abstract", "space", "car", "girl", "sport",
    };

    /// <summary>What a fresh installation uses: landscape only.</summary>
    public const string Default = "nature";

    /// <summary>
    /// Canonical selection: known keys only, in the order above, never empty. Used for every request
    /// and for comparing two selections, so a hand-edited state.json cannot produce a bad request.
    /// </summary>
    public static List<string> Normalize(IEnumerable<string>? keys)
    {
        var wanted = new HashSet<string>(keys ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var picked = All.Where(wanted.Contains).ToList();
        return picked.Count > 0 ? picked : new List<string> { Default };
    }

    /// <summary>Stable text form of a selection, for "did the selection change" comparisons.</summary>
    public static string Key(IEnumerable<string>? keys) => string.Join(",", Normalize(keys));
}
