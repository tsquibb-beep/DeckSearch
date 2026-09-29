using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace DeckSearch;

/// <summary>
/// Entry point. The loader finds this via <see cref="ModInitializerAttribute"/> and calls
/// <see cref="Init"/> once at startup.
/// </summary>
[ModInitializer(nameof(Init))]
internal static class DeckSearchMod
{
    private const string HarmonyId = "tomasapan.DeckSearch";

    private static void Init()
    {
        try
        {
            _ = SearchConfig.Current;
            new Harmony(HarmonyId).PatchAll(Assembly.GetExecutingAssembly());
            Log.Info("[DeckSearch] Initialised.");
        }
        catch (Exception ex)
        {
            Log.Error($"[DeckSearch] Failed to initialise: {ex}");
        }
    }
}
