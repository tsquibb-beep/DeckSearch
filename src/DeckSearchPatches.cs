using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

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
/// Adds the sort bar and search box to the pickers that choose cards from the deck. Upgrade covers
/// the campfire Smith, events and relics like Pomander; the plain one covers removing cards.
/// Each overrides _Ready, so each is patched.
/// </summary>
[HarmonyPatch]
internal static class PickerReadyPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => new[]
    {
        typeof(NDeckUpgradeSelectScreen),
        typeof(NDeckCardSelectScreen),
        typeof(NDeckTransformSelectScreen),
        typeof(NDeckEnchantSelectScreen),
    }.Select(type => (MethodBase)AccessTools.Method(type, "_Ready"));

    private static void Postfix(NCardGridSelectionScreen __instance)
    {
        try
        {
            SearchBox.Attach(__instance);
        }
        catch (Exception ex)
        {
            Log.Error($"[DeckSearch] Could not add the search box to {__instance.GetType().Name}: {ex}");
        }
    }
}

/// <summary>
/// Every route that redraws the Deck screen (opening, the sort buttons, the deck changing, and our
/// own re-filter) ends in <c>NCardGrid.SetCards</c>. Filtering its input here leaves the deck and
/// the screen's own <c>_cards</c> list untouched, and the grid still applies the chosen sort.
/// A picker's redraws are all ours and end here too.
/// </summary>
[HarmonyPatch(typeof(NCardGrid), nameof(NCardGrid.SetCards))]
internal static class GridSetCardsPatch
{
    private static void Prefix(NCardGrid __instance, ref IReadOnlyList<CardModel> cardsToDisplay)
    {
        try
        {
            if (__instance.GetParent() is Control screen && SearchBox.For(screen) is { } box)
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
