using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace DeckSearch;

/// <summary>Adds the search box once the Deck screen has built itself.</summary>
[HarmonyPatch(typeof(NDeckViewScreen), nameof(NDeckViewScreen._Ready))]
internal static class DeckViewReadyPatch
{
    private static void Postfix(NDeckViewScreen __instance)
    {
        try
        {
            SearchBox.Attach(__instance);
        }
        catch (Exception ex)
        {
            Log.Error($"[DeckSearch] Could not add the search box: {ex}");
        }
    }
}

/// <summary>
/// Every route that redraws the Deck screen (opening, the sort buttons, the deck changing, and our
/// own re-filter) ends in <c>NCardGrid.SetCards</c>. Filtering its input here leaves the deck and
/// the screen's own <c>_cards</c> list untouched, and the grid still applies the chosen sort.
/// </summary>
[HarmonyPatch(typeof(NCardGrid), nameof(NCardGrid.SetCards))]
internal static class GridSetCardsPatch
{
    private static void Prefix(NCardGrid __instance, ref IReadOnlyList<CardModel> cardsToDisplay)
    {
        try
        {
            if (__instance.GetParent() is NDeckViewScreen screen && SearchBox.For(screen) is { } box)
            {
                cardsToDisplay = box.Filter(cardsToDisplay);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[DeckSearch] Filtering failed, showing the whole deck: {ex}");
        }
    }
}

/// <summary>A card may have been upgraded or enchanted, so its cached text is stale.</summary>
[HarmonyPatch(typeof(NDeckViewScreen), "OnPileContentsChanged")]
internal static class DeckContentsChangedPatch
{
    private static void Prefix(NDeckViewScreen __instance)
    {
        SearchBox.For(__instance)?.InvalidateCache();
    }
}
