using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Assets;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Screens;

namespace DeckSearch;

/// <summary>
/// The search box on one Deck screen, and the filter it applies. A plain C# object rather than a
/// Godot node subclass: the Godot source generators do not run over mod assemblies, so a subclass
/// would never get its callbacks. It lives exactly as long as its screen.
/// </summary>
internal sealed class SearchBox
{
    private static readonly ConditionalWeakTable<NDeckViewScreen, SearchBox> Boxes = new();

    private static readonly MethodInfo? DisplayCardsMethod = AccessTools.Method(typeof(NDeckViewScreen), "DisplayCards");

    private static PackedScene? _gameBarScene;

    private static bool _lookedForGameBar;

    private static string _gameBarInfo = "not looked up yet";

    /// <summary>The most recently opened box, for the console command.</summary>
    private static WeakReference<SearchBox>? _latest;

    private readonly NDeckViewScreen _screen;

    private readonly Dictionary<CardModel, FuzzyMatcher.Target> _targets = new(ReferenceEqualityComparer.Instance);

    private Control? _root;

    private LineEdit? _input;

    private Label? _emptyLabel;

    private string _query = "";

    private int _version;

    private int _shown;

    private int _total;

    private readonly List<(StringName Signal, Callable Callable)> _controllerSignals = new();

    private SearchBox(NDeckViewScreen screen)
    {
        _screen = screen;
    }

    public static SearchBox? For(NDeckViewScreen screen) => Boxes.TryGetValue(screen, out SearchBox? box) ? box : null;

    public static SearchBox? Latest => _latest != null && _latest.TryGetTarget(out SearchBox? box) && GodotObject.IsInstanceValid(box._screen) ? box : null;

    public static void Attach(NDeckViewScreen screen)
    {
        if (!SearchConfig.Current.Enabled)
        {
            return;
        }

        var box = new SearchBox(screen);
        Boxes.AddOrUpdate(screen, box);
        _latest = new WeakReference<SearchBox>(box);
        box.Build();
    }

    // ---------------------------------------------------------------- filtering

    public IReadOnlyList<CardModel> Filter(IReadOnlyList<CardModel> cards)
    {
        _total = cards.Count;
        if (_query.Length == 0)
        {
            _shown = cards.Count;
            UpdateEmptyLabel();
            return cards;
        }

        double minScore = SearchConfig.Current.MinScore;
        List<CardModel> matches = cards.Where(c => Score(c) >= minScore).ToList();
        _shown = matches.Count;
        UpdateEmptyLabel();
        return matches;
    }

    public void InvalidateCache() => _targets.Clear();

    private double Score(CardModel card)
    {
        if (!_targets.TryGetValue(card, out FuzzyMatcher.Target? target))
        {
            target = BuildTarget(card);
            _targets[card] = target;
        }

        return FuzzyMatcher.Score(_query, target, SearchConfig.Current.SearchDescriptions);
    }

    /// <summary>Same text the Card Library searches: the title plus the description minus markup.</summary>
    public static FuzzyMatcher.Target BuildTarget(CardModel card)
    {
        string description = "";
        try
        {
            description = NSearchBar.RemoveHtmlTags(card.GetDescriptionForPile(PileType.Deck).StripBbCode());
        }
        catch (Exception ex)
        {
            Log.Warn($"[DeckSearch] No description for {card.Id}: {ex.Message}");
        }

        return new FuzzyMatcher.Target(card.Title, description);
    }

    private void OnQueryChanged(string text)
    {
        int version = ++_version;
        int delayMs = SearchConfig.Current.DebounceMs;
        SceneTree? tree = _screen.IsInsideTree() ? _screen.GetTree() : null;
        if (delayMs <= 0 || tree == null)
        {
            Apply(text);
            return;
        }

        // Relaying out the grid animates every card, so wait for a pause in typing, as the
        // Card Library does.
        tree.CreateTimer(delayMs / 1000.0, processAlways: true, ignoreTimeScale: true).Timeout += () =>
        {
            if (version == _version && GodotObject.IsInstanceValid(_screen))
            {
                Apply(text);
            }
        };
    }

    private void Apply(string text)
    {
        string query = FuzzyMatcher.Normalize(text);
        if (query == _query)
        {
            return;
        }

        _query = query;
        Redraw();
    }

    /// <summary>Re-runs the screen's own DisplayCards, which ends in our SetCards prefix.</summary>
    private void Redraw()
    {
        try
        {
            DisplayCardsMethod?.Invoke(_screen, null);
        }
        catch (Exception ex)
        {
            Log.Error($"[DeckSearch] Could not redraw the deck: {ex}");
        }
    }

    // ---------------------------------------------------------------- UI

    private void Build()
    {
        SearchConfig config = SearchConfig.Current;

        _root = config.UseGameSearchBar ? CreateGameSearchBar() : null;
        if (_root is NSearchBar bar)
        {
            _input = bar.GetNodeOrNull<LineEdit>("TextArea");
            // The clear button sets the text directly and only raises QueryChanged, not TextChanged.
            bar.Connect(NSearchBar.SignalName.QueryChanged, Callable.From<string>(OnQueryChanged));
        }

        if (_input == null)
        {
            _root?.QueueFree();
            _input = new LineEdit
            {
                Name = "DeckSearchInput",
                PlaceholderText = "Search deck…",
                ClearButtonEnabled = true,
                CustomMinimumSize = new Vector2(360, 56),
            };
            _input.AddThemeFontSizeOverride("font_size", 28);
            _input.TextChanged += OnQueryChanged;
            _root = _input;
        }

        // Either the game bar with its TextArea, or the plain box, which is its own root.
        _root!.Name = "DeckSearch";
        _input.TextSubmitted += _ => _input.ReleaseFocus();
        _input.GuiInput += OnInputGuiInput;

        _emptyLabel = new Label
        {
            Name = "DeckSearchEmpty",
            Text = "No cards match",
            HorizontalAlignment = HorizontalAlignment.Center,
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        _emptyLabel.AddThemeFontSizeOverride("font_size", 36);

        _screen.AddChildSafely(_root);
        _screen.AddChildSafely(_emptyLabel);
        _screen.AddChildSafely(CreateShortcutButton(config));

        WatchController();
        _screen.TreeExiting += Detach;

        Callable.From(() =>
        {
            Place();
            if (config.FocusOnOpen && _root.Visible)
            {
                _input.GrabFocus();
            }
        }).CallDeferred();
    }

    /// <summary>
    /// Esc clears a query, then a second Esc leaves the box so the game's own Esc (close screen)
    /// works again. The game ignores its hotkeys while a text box is being edited.
    /// </summary>
    private void OnInputGuiInput(InputEvent inputEvent)
    {
        if (_input == null || inputEvent is not InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            return;
        }

        if (_input.Text.Length > 0)
        {
            ClearQuery();
        }
        else
        {
            _input.ReleaseFocus();
        }

        _input.AcceptEvent();
    }

    private void ClearQuery()
    {
        if (_root is NSearchBar bar)
        {
            bar.ClearText();
        }
        else if (_input != null)
        {
            _input.Clear();
            OnQueryChanged("");
        }
    }

    private void FocusBox()
    {
        if (_input == null || _root == null || !_root.IsVisibleInTree())
        {
            return;
        }

        // Do not grab focus from underneath the card inspect view.
        if (NGame.Instance?.GetInspectCardScreen() is { Visible: true })
        {
            return;
        }

        _input.GrabFocus();
        _input.SelectAll();
    }

    /// <summary>
    /// A Button's Shortcut is the one way to catch a key from a mod without subclassing a node.
    /// It stays in the tree and "visible" so shortcuts fire, but draws nothing and takes no input.
    /// </summary>
    private Button CreateShortcutButton(SearchConfig config)
    {
        var shortcut = new Shortcut();
        var events = new Godot.Collections.Array();
        foreach (string text in config.FocusShortcuts)
        {
            if (ParseKey(text) is { } key)
            {
                events.Add(key);
            }
            else
            {
                Log.Warn($"[DeckSearch] Unknown focus shortcut '{text}'.");
            }
        }

        shortcut.Events = events;
        var button = new Button
        {
            Name = "DeckSearchShortcut",
            Flat = true,
            FocusMode = Control.FocusModeEnum.None,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Modulate = new Color(1, 1, 1, 0),
            Size = Vector2.Zero,
            Shortcut = shortcut,
            ShortcutFeedback = false,
            ShortcutInTooltip = false,
        };
        button.Pressed += FocusBox;
        return button;
    }

    /// <summary>"Ctrl+F", "Shift+Alt+S", "Slash" — Godot key names, with modifiers joined by '+'.</summary>
    private static InputEventKey? ParseKey(string text)
    {
        string[] parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return null;
        }

        Key key = OS.FindKeycodeFromString(parts[^1]);
        if (key == Key.None)
        {
            return null;
        }

        var inputEvent = new InputEventKey { Keycode = key };
        foreach (string modifier in parts[..^1])
        {
            switch (modifier.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    inputEvent.CtrlPressed = true;
                    break;
                case "shift":
                    inputEvent.ShiftPressed = true;
                    break;
                case "alt":
                    inputEvent.AltPressed = true;
                    break;
                case "meta":
                case "cmd":
                    inputEvent.MetaPressed = true;
                    break;
                default:
                    return null;
            }
        }

        return inputEvent;
    }

    /// <summary>
    /// Beside the sort buttons, measured from the game's own nodes. If there is no room to the
    /// right it drops underneath them instead.
    /// </summary>
    private void Place()
    {
        if (_root == null || !GodotObject.IsInstanceValid(_screen) || !_screen.IsInsideTree())
        {
            return;
        }

        SearchConfig config = SearchConfig.Current;
        Vector2 size = _root.Size;
        if (config.Width > 0)
        {
            size.X = config.Width;
        }

        size.X = Math.Max(size.X, 300);
        size.Y = Math.Max(size.Y, 48);

        Transform2D toLocal = _screen.GetGlobalTransform().AffineInverse();
        Vector2 screenSize = _screen.Size;
        Vector2 position = new(screenSize.X - size.X - 40, 20);

        Control? sortBg = _screen.GetNodeOrNull<Control>("%SortingBg");
        if (sortBg != null)
        {
            Rect2 sortRect = sortBg.GetGlobalRect();
            Vector2 topLeft = toLocal * sortRect.Position;
            Vector2 bottomRight = toLocal * sortRect.End;
            float middle = (topLeft.Y + bottomRight.Y) / 2;
            position = bottomRight.X + 24 + size.X <= screenSize.X
                ? new Vector2(bottomRight.X + 24, middle - (size.Y / 2))
                : new Vector2(topLeft.X, bottomRight.Y + 12);
        }

        _root.Position = position + new Vector2(config.OffsetX, config.OffsetY);
        _root.Size = size;

        if (_emptyLabel != null)
        {
            _emptyLabel.Position = new Vector2(0, position.Y + size.Y + 160);
            _emptyLabel.Size = new Vector2(screenSize.X, 60);
        }
    }

    private void UpdateEmptyLabel()
    {
        if (_emptyLabel == null || !GodotObject.IsInstanceValid(_emptyLabel))
        {
            return;
        }

        _emptyLabel.Visible = _query.Length > 0 && _shown == 0 && _total > 0;
    }

    /// <summary>
    /// Controller play hides the box, as the game hides its upgrades tickbox: it cannot be reached
    /// with a pad, and a hidden query would leave cards filtered with no way to see why.
    /// </summary>
    private void WatchController()
    {
        NControllerManager? manager = NControllerManager.Instance;
        if (manager == null)
        {
            return;
        }

        Callable callable = Callable.From(OnControllerStateChanged);
        foreach (StringName signal in new[] { NControllerManager.SignalName.ControllerDetected, NControllerManager.SignalName.MouseDetected })
        {
            manager.Connect(signal, callable);
            _controllerSignals.Add((signal, callable));
        }

        OnControllerStateChanged();
    }

    private void OnControllerStateChanged()
    {
        if (_root == null || !GodotObject.IsInstanceValid(_root))
        {
            return;
        }

        bool usingController = NControllerManager.Instance?.IsUsingController ?? false;
        _root.Visible = !usingController;
        if (usingController && _input != null && _input.Text.Length > 0)
        {
            ClearQuery();
        }
    }

    private void Detach()
    {
        NControllerManager? manager = NControllerManager.Instance;
        if (manager != null && GodotObject.IsInstanceValid(manager))
        {
            foreach ((StringName signal, Callable callable) in _controllerSignals)
            {
                if (manager.IsConnected(signal, callable))
                {
                    manager.Disconnect(signal, callable);
                }
            }
        }

        _controllerSignals.Clear();
    }

    // ---------------------------------------------------------------- game search bar

    /// <summary>
    /// The Card Library's search bar, if it is its own sub-scene. Read from the library scene's
    /// packed state without instancing the library itself.
    /// </summary>
    private static Control? CreateGameSearchBar()
    {
        if (!_lookedForGameBar)
        {
            _lookedForGameBar = true;
            _gameBarScene = FindGameSearchBarScene(out _gameBarInfo);
            Log.Info($"[DeckSearch] Game search bar: {_gameBarInfo}");
        }

        if (_gameBarScene == null)
        {
            return null;
        }

        try
        {
            return _gameBarScene.Instantiate<Control>();
        }
        catch (Exception ex)
        {
            Log.Warn($"[DeckSearch] Could not instance the game search bar, using a plain box: {ex.Message}");
            _gameBarScene = null;
            _gameBarInfo = $"instancing failed: {ex.Message}";
            return null;
        }
    }

    private static PackedScene? FindGameSearchBarScene(out string info)
    {
        try
        {
            string path = SceneHelper.GetScenePath("screens/card_library/card_library");
            PackedScene? library = PreloadManager.Cache.ContainsKey(path)
                ? PreloadManager.Cache.GetScene(path)
                : ResourceLoader.Load<PackedScene>(path);
            if (library == null)
            {
                info = $"could not load {path}";
                return null;
            }

            SceneState state = library.GetState();
            for (int i = 0; i < state.GetNodeCount(); i++)
            {
                if (state.GetNodeName(i) != "SearchBar")
                {
                    continue;
                }

                PackedScene? instance = state.GetNodeInstance(i);
                info = instance != null
                    ? $"using {instance.ResourcePath}"
                    : "SearchBar is built inline in the library scene, using a plain box";
                return instance;
            }

            info = "no SearchBar node in the library scene, using a plain box";
            return null;
        }
        catch (Exception ex)
        {
            info = $"lookup failed ({ex.Message}), using a plain box";
            return null;
        }
    }

    // ---------------------------------------------------------------- console

    public string Diagnostics()
    {
        var text = new StringBuilder();
        text.AppendLine($"Game search bar: {_gameBarInfo}");
        text.AppendLine($"Box: {_root?.GetType().Name} at {_root?.Position} size {_root?.Size} visible={_root?.Visible}");
        text.AppendLine($"Query: '{_query}' showing {_shown}/{_total}");
        text.AppendLine($"Screen: {_screen.GetGlobalRect()}");
        foreach (Node child in _screen.GetChildren())
        {
            string rect = child is Control control ? $" {control.GetGlobalRect()}" : "";
            text.AppendLine($"  {child.Name} ({child.GetType().Name}){rect}");
        }

        if (_screen.GetNodeOrNull<Control>("%SortingBg") is { } sortBg)
        {
            text.AppendLine($"SortingBg: {sortBg.GetGlobalRect()} under {sortBg.GetParent()?.Name}");
        }

        return text.ToString();
    }

    public void Refresh()
    {
        InvalidateCache();
        Redraw();
        Place();
    }
}
