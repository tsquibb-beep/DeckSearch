using System;
using System.Linq;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace DeckSearch;

/// <summary>
/// `dsearch diag` describes the open Deck screen and where the box went; `dsearch test <query>`
/// scores your deck against a query; `dsearch reload` re-reads the config.
///
/// The dev console discovers commands in loaded mods by reflection, so shipping this public
/// class with a parameterless constructor is enough to register it.
/// </summary>
public class SearchConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "dsearch";

    public override string Args => "[diag|test <query>|reload]";

    public override string Description => "DeckSearch: inspect the Deck screen search box or test a query.";

    /// <summary>Searching is local to this client — nothing to synchronise.</summary>
    public override bool IsNetworked => false;

    public override CmdResult Process(Player? issuingPlayer, string[] args)
    {
        switch (args.FirstOrDefault()?.ToLowerInvariant() ?? "diag")
        {
            case "diag":
                return SearchBox.Latest is { } box
                    ? new CmdResult(success: true, box.Diagnostics())
                    : new CmdResult(success: false, "Open the Deck screen first (it stays open behind the console).");

            case "test":
                return Test(issuingPlayer, string.Join(" ", args.Skip(1)));

            case "reload":
                SearchConfig.Reload();
                SearchBox.Latest?.Refresh();
                return new CmdResult(success: true, "Config reloaded.");

            default:
                return new CmdResult(success: false, "Use: dsearch diag, dsearch test <query>, dsearch reload.");
        }
    }

    private static CmdResult Test(Player? player, string query)
    {
        if (player == null)
        {
            return new CmdResult(success: false, "Start a run first.");
        }

        string normalized = FuzzyMatcher.Normalize(query);
        SearchConfig config = SearchConfig.Current;
        var scored = player.Deck.Cards
            .Select(card => (card.Title, Score: FuzzyMatcher.Score(normalized, SearchBox.BuildTarget(card), config.SearchDescriptions)))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Title, StringComparer.Ordinal)
            .ToList();

        int shown = scored.Count(x => x.Score >= config.MinScore);
        var lines = scored.Take(30).Select(x => $"{(x.Score >= config.MinScore ? "+" : "-")} {x.Score,5:0}  {x.Title}");
        return new CmdResult(success: true, $"'{normalized}': {shown}/{scored.Count} shown (minScore {config.MinScore})\n" + string.Join("\n", lines));
    }

    public override CompletionResult GetArgumentCompletions(Player? player, string[] args)
    {
        if (args.Length <= 1)
        {
            return CompleteArgument(new[] { "diag", "test", "reload" }, Array.Empty<string>(), args.FirstOrDefault() ?? "");
        }

        return new CompletionResult { Type = CompletionType.Argument, ArgumentContext = CmdName };
    }
}
