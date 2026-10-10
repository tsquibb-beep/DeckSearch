using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
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

/// <summary>Adds the sort bar and search box to the draw, discard and exhaust pile screens in combat.</summary>
[HarmonyPatch(typeof(NCardPileScreen), nameof(NCardPileScreen._Ready))]
internal static class PileReadyPatch
{
    private static void Postfix(NCardPileScreen __instance)
    {
        try
        {
            SearchBox.Attach(__instance);
        }
        catch (Exception ex)
        {
            Log.Error($"[DeckSearch] Could not add the search box to the {__instance.Pile?.Type} pile: {ex}");
        }
    }
}

/// <summary>
/// Type-to-search. A key the focused box did not take ends up here, where the game turns its
/// rebindable keys (D, A, S, X, M, E...) into hotkey actions. A printable key on a screen with a
/// box goes into the box instead, and the game never sees it.
/// </summary>
[HarmonyPatch(typeof(NInputManager), nameof(NInputManager._UnhandledKeyInput))]
internal static class TypeToSearchPatch
{
    private static bool Prefix(NInputManager __instance, InputEvent inputEvent)
    {
        try
        {
            if (SearchBox.TryTypeInto(inputEvent))
            {
                __instance.GetViewport()?.SetInputAsHandled();
                return false;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[DeckSearch] Type-to-search failed, passing the key to the game: {ex}");
        }

        return true;
    }
}

/// <summary>
/// Every route that redraws the Deck screen (opening, the sort buttons, the deck changing, and our
/// own re-filter) ends in <c>NCardGrid.SetCards</c>. Filtering its input here leaves the deck and
/// the screen's own <c>_cards</c> list untouched, and the grid still applies the chosen sort.
/// A picker's redraws are all ours and end here too, and so do a combat pile's, where the sort
/// order is swapped for the one chosen on our sort bar.
/// </summary>
[HarmonyPatch(typeof(NCardGrid), nameof(NCardGrid.SetCards))]
internal static class GridSetCardsPatch
{
    private static void Prefix(NCardGrid __instance, ref IReadOnlyList<CardModel> cardsToDisplay, ref List<SortingOrders> sortingPriority)
    {
        try
        {
            if (__instance.GetParent() is Control screen && SearchBox.For(screen) is { } box)
            {
                cardsToDisplay = box.Filter(cardsToDisplay);
                sortingPriority = box.SortingFor(sortingPriority);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[DeckSearch] Filtering failed, showing the whole deck: {ex}");
        }
    }
}

/// <summary>
/// A card may have been upgraded or enchanted, or in combat changed its cost or gained Replay,
/// so its cached text is stale.
/// </summary>
[HarmonyPatch]
internal static class ContentsChangedPatch
{
    private static IEnumerable<MethodBase> TargetMethods() => new[]
    {
        AccessTools.Method(typeof(NDeckViewScreen), "OnPileContentsChanged"),
        AccessTools.Method(typeof(NCardPileScreen), "OnPileContentsChanged"),
    }.Where(method => method != null);

    private static void Prefix(Control __instance)
    {
        SearchBox.For(__instance)?.InvalidateCache();
    }
}
