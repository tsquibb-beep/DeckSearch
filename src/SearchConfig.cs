using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace DeckSearch;

/// <summary>
/// Settings, read from DeckSearch.config.jsonc. Missing or malformed files fall back to these
/// defaults, so the mod never fails to load because of a bad config. `dsearch reload` re-reads it.
/// </summary>
internal sealed class SearchConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Also match card descriptions ("exhaust", "block"), ranked below name matches.</summary>
    [JsonPropertyName("searchDescriptions")]
    public bool SearchDescriptions { get; set; } = true;

    /// <summary>Cards scoring below this are hidden. Name hits score 100+, description hits 50.</summary>
    [JsonPropertyName("minScore")]
    public double MinScore { get; set; } = 30;

    /// <summary>Pause after the last keystroke before the grid re-lays itself out.</summary>
    [JsonPropertyName("debounceMs")]
    public int DebounceMs { get; set; } = 200;

    /// <summary>Put the cursor in the box as soon as the Deck screen opens.</summary>
    [JsonPropertyName("focusOnOpen")]
    public bool FocusOnOpen { get; set; } = false;

    /// <summary>Keys that jump to the search box, in Godot's key-name format ("Ctrl+F", "Slash").</summary>
    [JsonPropertyName("focusShortcuts")]
    public List<string> FocusShortcuts { get; set; } = new() { "Ctrl+F" };

    /// <summary>Width of the search box on the sort bar, in pixels.</summary>
    [JsonPropertyName("searchWidth")]
    public float SearchWidth { get; set; } = 340;

    /// <summary>
    /// The sort buttons are narrowed to this width (the game uses 250) to make room for the box.
    /// A button never goes narrower than its label needs.
    /// </summary>
    [JsonPropertyName("sortButtonWidth")]
    public float SortButtonWidth { get; set; } = 190;

    /// <summary>Gap between the sort buttons and the box (the game uses 50).</summary>
    [JsonPropertyName("sortButtonSpacing")]
    public int SortButtonSpacing { get; set; } = 30;

    private static SearchConfig? _current;

    public static SearchConfig Current => _current ??= Load();

    public static void Reload() => _current = Load();

    private const string FileName = "DeckSearch.config.jsonc";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Looks for the config next to the game's save data first, then beside the mod DLL.
    ///
    /// The save-data copy wins so that settings survive a Vortex update, which replaces the
    /// whole mod folder. The file is .jsonc rather than .json deliberately: the game scans
    /// mods/ recursively for *.json and tries to parse every one as a mod manifest.
    /// </summary>
    private static SearchConfig Load()
    {
        foreach (string path in CandidatePaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                SearchConfig? loaded = JsonSerializer.Deserialize<SearchConfig>(File.ReadAllText(path), _jsonOptions);
                if (loaded != null)
                {
                    Log.Info($"[DeckSearch] Loaded config from {path}.");
                    return loaded;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"[DeckSearch] Could not read {path} ({ex.Message}), trying the next location.");
            }
        }

        Log.Info("[DeckSearch] No config file found, using defaults.");
        return new SearchConfig();
    }

    private static IEnumerable<string> CandidatePaths()
    {
        string? userDir = null;
        try
        {
            userDir = OS.GetUserDataDir();
        }
        catch (Exception)
        {
            // Not fatal: fall back to the mod folder.
        }

        if (!string.IsNullOrEmpty(userDir))
        {
            yield return Path.Combine(userDir, FileName);
        }

        string? modDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (!string.IsNullOrEmpty(modDir))
        {
            yield return Path.Combine(modDir, FileName);
        }
    }
}
