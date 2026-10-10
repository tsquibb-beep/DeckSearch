using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Debug;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardLibrary;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.ScreenContext;

namespace DeckSearch;

/// <summary>
/// The search box on one Deck screen, deck card picker (upgrade, remove, transform, enchant) or
/// combat pile (draw, discard, exhaust), and the filter it applies. A plain C# object rather than a Godot node subclass: the Godot
/// source generators do not run over mod assemblies, so a subclass would never get its
/// callbacks. It lives exactly as long as its screen.
/// </summary>
internal sealed class SearchBox
{
    private static readonly ConditionalWeakTable<Control, SearchBox> Boxes = new();

    private static readonly MethodInfo? DisplayCardsMethod = AccessTools.Method(typeof(NDeckViewScreen), "DisplayCards");

    private static readonly FieldInfo? PickerCardsField = AccessTools.Field(typeof(NCardGridSelectionScreen), "_cards");

    private static readonly MethodInfo? PileContentsChangedMethod = AccessTools.Method(typeof(NCardPileScreen), "OnPileContentsChanged");

    private static readonly FieldInfo? HighlightedCardsField = AccessTools.Field(typeof(NCardGrid), "_highlightedCards");

    private static readonly FieldInfo? ScrollingEnabledField = AccessTools.Field(typeof(NCardGrid), "_scrollingEnabled");

    private static readonly FieldInfo? TargetDragField = AccessTools.Field(typeof(NCardGrid), "_targetDrag");

    private static readonly PropertyInfo? ScrollLimitTopProperty = AccessTools.Property(typeof(NCardGrid), "ScrollLimitTop");

    private const string SortButtonScene = "res://scenes/screens/deck_view_screen/deck_view_sort_button.tscn";

    private const string SortBarTexture = "res://images/ui/color_tab_bar.png";

    /// <summary>How far the Deck screen pushes its cards down to make room for the sort bar.</summary>
    private const int SortBarGridOffset = 100;

    private static readonly StringName SaturationParam = "s";

    private static readonly StringName ValueParam = "v";

    /// <summary>The sort buttons' resting and hovered shader brightness (NCardViewSortButton).</summary>
    private const float RestShade = 0.8f;

    private const float LitShade = 1f;

    /// <summary>The most recently opened box, for the console command.</summary>
    private static WeakReference<SearchBox>? _latest;

    private readonly Control _screen;

    private readonly NCardGrid? _grid;

    private enum ScreenKind
    {
        Deck,
        Picker,
        Pile,
    }

    private readonly ScreenKind _kind;

    /// <summary>
    /// Pickers and piles have no sort bar of their own, so the box builds one and runs it: it keeps
    /// the sort order and has the grid redrawn with it.
    /// </summary>
    private bool OwnsSortBar => _kind != ScreenKind.Deck;

    private IReadOnlyList<CardModel> _pickerCards = Array.Empty<CardModel>();

    /// <summary>The Deck screen's starting order: as obtained, then type, cost, name.</summary>
    private readonly List<SortingOrders> _sorting = new()
    {
        SortingOrders.Ascending,
        SortingOrders.TypeAscending,
        SortingOrders.CostAscending,
        SortingOrders.AlphabetAscending,
    };

    private readonly Dictionary<CardModel, FuzzyMatcher.Target> _targets = new(ReferenceEqualityComparer.Instance);

    private Control? _root;

    private LineEdit? _input;

    private Label? _emptyLabel;

    /// <summary>The row holding the game's sort buttons, when the box sits in it.</summary>
    private HBoxContainer? _sortRow;

    private ShaderMaterial? _hsv;

    private Tween? _shadeTween;

    private bool _hovered;

    /// <summary>The magnifying-glass icon, fixed at the right end of the box.</summary>
    private TextureRect? _icon;

    private static Texture2D? _iconTexture;

    /// <summary>A × drawn as its own node: shown while there is text to clear.</summary>
    private Label? _clearButton;

    private const float IconSize = 30;

    private const float IconPad = 10;

    /// <summary>Lifts the icon off dead centre so it lines up with the text.</summary>
    private const float IconRaise = 2;

    /// <summary>Width of the × and its font size.</summary>
    private const float ClearSize = 26;

    private const int ClearFontSize = 34;

    /// <summary>Lifts the × glyph, which sits low in the font, level with the text and icon.</summary>
    private const float ClearRaise = 2;

    /// <summary>Space between the × and the icon.</summary>
    private const float ClearGap = 6;

    private const string Placeholder = "Search...";

    private FuzzyMatcher.Query _query = FuzzyMatcher.Query.Empty;

    private int _version;

    private int _shown;

    private int _total;

    private readonly List<(StringName Signal, Callable Callable)> _controllerSignals = new();

    private SearchBox(Control screen, ScreenKind kind)
    {
        _screen = screen;
        _kind = kind;
        _grid = screen.GetNodeOrNull<NCardGrid>("CardGrid");
    }

    public static SearchBox? For(Control screen) => Boxes.TryGetValue(screen, out SearchBox? box) ? box : null;

    public static SearchBox? Latest => _latest != null && _latest.TryGetTarget(out SearchBox? box) && GodotObject.IsInstanceValid(box._screen) ? box : null;

    public static void Attach(NDeckViewScreen screen) => Attach(screen, ScreenKind.Deck);

    public static void Attach(NCardGridSelectionScreen screen)
    {
        if (SearchConfig.Current.PickScreens)
        {
            Attach(screen, ScreenKind.Picker);
        }
    }

    public static void Attach(NCardPileScreen screen)
    {
        if (SearchConfig.Current.PileScreens)
        {
            Attach(screen, ScreenKind.Pile);
        }
    }

    private static void Attach(Control screen, ScreenKind kind)
    {
        if (!SearchConfig.Current.Enabled)
        {
            return;
        }

        var box = new SearchBox(screen, kind);
        if (kind != ScreenKind.Deck && box._grid == null)
        {
            Log.Warn($"[DeckSearch] {screen.GetType().Name} has no card grid (game layout changed?), no search box.");
            return;
        }

        if (kind == ScreenKind.Picker)
        {
            if (PickerCardsField?.GetValue(screen) is not IReadOnlyList<CardModel> cards)
            {
                Log.Warn($"[DeckSearch] {screen.GetType().Name} has no card list (game changed?), no search box.");
                return;
            }

            box._pickerCards = cards;
        }
        else if (kind == ScreenKind.Pile && PileContentsChangedMethod == null)
        {
            Log.Warn("[DeckSearch] NCardPileScreen.OnPileContentsChanged is gone (game changed?), no search box.");
            return;
        }

        Boxes.AddOrUpdate(screen, box);
        _latest = new WeakReference<SearchBox>(box);
        box.Build();
    }

    // ---------------------------------------------------------------- filtering

    public IReadOnlyList<CardModel> Filter(IReadOnlyList<CardModel> cards)
    {
        _total = cards.Count;
        if (_query.IsEmpty)
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

    /// <summary>
    /// The sort order the grid should use: ours where we run the sort bar. A combat pile redraws
    /// itself whenever its cards change, always asking for pile order, so this keeps the
    /// player's choice through a draw or a discard.
    /// </summary>
    public List<SortingOrders> SortingFor(List<SortingOrders> requested) => OwnsSortBar ? _sorting : requested;

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

        return new FuzzyMatcher.Target(card.Title, description, Tags(card), MarkedTags(card));
    }

    /// <summary>Tags found only with '#': "#upgraded" (or "#up") for upgraded cards.</summary>
    private static IEnumerable<string> MarkedTags(CardModel card) =>
        card.IsUpgraded ? new[] { "Upgraded" } : Array.Empty<string>();

    /// <summary>
    /// The card's type and rarity, both as the game shows them in the player's language and as the
    /// English names, so "attack" works everywhere. The Card Library's own search matches the
    /// English rarity names the same way.
    /// </summary>
    private static IEnumerable<string> Tags(CardModel card)
    {
        var tags = new List<string> { card.Type.ToString(), card.Rarity.ToString() };
        TryAdd(tags, () => card.Type.ToLocString().GetFormattedText());
        TryAdd(tags, () => card.Rarity.ToLocString().GetFormattedText());
        return tags;
    }

    private static void TryAdd(List<string> tags, Func<string> localized)
    {
        try
        {
            // A missing translation can come back as its key ("CARD_RARITY.TOKEN"), which would
            // make "card" match every card.
            string text = localized();
            if (!string.IsNullOrWhiteSpace(text) && !text.Contains('_') && !text.Contains('.'))
            {
                tags.Add(text);
            }
        }
        catch (Exception)
        {
            // Not every rarity has a display name (None, Token...); the English name still counts.
        }
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
        FuzzyMatcher.Query query = FuzzyMatcher.Query.Parse(text);
        if (query.Key == _query.Key)
        {
            return;
        }

        _query = query;
        Redraw();
    }

    /// <summary>
    /// Re-runs the Deck screen's own DisplayCards or the pile screen's OnPileContentsChanged, or on
    /// a picker hands the grid its cards again. All end in our SetCards prefix, which applies the
    /// filter (and on a pile, our sort order).
    /// </summary>
    private void Redraw()
    {
        try
        {
            switch (_kind)
            {
                case ScreenKind.Deck:
                    DisplayCardsMethod?.Invoke(_screen, null);
                    break;

                case ScreenKind.Pile:
                    // The game's own redraw, so the draw pile keeps its rarity order and never
                    // shows the order the cards will be drawn in.
                    PileContentsChangedMethod?.Invoke(_screen, null);
                    break;

                case ScreenKind.Picker when _grid != null && GodotObject.IsInstanceValid(_grid):
                    _grid.SetCards(_pickerCards, PileType.None, _sorting);
                    RestoreHighlights();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[DeckSearch] Could not redraw the cards: {ex}");
        }
    }

    /// <summary>
    /// The grid rebuilds its card nodes on every SetCards but only re-applies the glow on cards
    /// already picked when it scrolls, so a re-filter would leave picked cards looking unpicked.
    /// </summary>
    private void RestoreHighlights()
    {
        if (_grid == null || HighlightedCardsField?.GetValue(_grid) is not List<CardModel> picked)
        {
            return;
        }

        foreach (CardModel card in picked)
        {
            _grid.GetCardNode(card)?.CardHighlight.AnimShow();
        }
    }

    // ---------------------------------------------------------------- UI

    private void Build()
    {
        SearchConfig config = SearchConfig.Current;

        _input = new LineEdit
        {
            Name = "Input",
        };
        _input.TextChanged += OnQueryChanged;
        _input.TextSubmitted += _ => _input.ReleaseFocus();
        _input.GuiInput += OnInputGuiInput;

        // The sort buttons live in an HBoxContainer inside the grid's scroll area, so joining that
        // row puts the box on the bar, lets the row lay it out, and scrolls it with the cards.
        // Pickers and piles have no bar, so we build the Deck screen's one in the same place.
        Control? sorter = OwnsSortBar ? BuildSortBar() : _screen.GetNodeOrNull<Control>("%ObtainedSorter");
        _sortRow = sorter?.GetParent() as HBoxContainer;
        TextStyle style = TextStyle.From(sorter);

        if (sorter != null && _sortRow != null)
        {
            _root = CreateThemedBox(sorter, _input, config, style);
            FitSortRow(_sortRow, config);
            _sortRow.AddChildSafely(_root);
        }
        else
        {
            Log.Warn("[DeckSearch] Sort button row not found (game layout changed?), placing a plain box instead.");
            _input.CustomMinimumSize = new Vector2(config.SearchWidth, 48);
            _root = _input;
            _screen.AddChildSafely(_root);
        }

        _root.Name = "DeckSearch";
        style.ApplyTo(_input);
        _input.TextChanged += _ => ApplyFocusState();
        _input.FocusEntered += ApplyFocusState;
        _input.FocusExited += ApplyFocusState;
        ApplyFocusState();

        _emptyLabel = new Label
        {
            Name = "DeckSearchEmpty",
            Text = "No cards match",
            HorizontalAlignment = HorizontalAlignment.Center,
            Visible = false,
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        style.ApplyTo(_emptyLabel, fontSize: 32);

        _screen.AddChildSafely(_emptyLabel);
        _screen.AddChildSafely(CreateShortcutButton(config));

        WatchController();
        _screen.TreeExiting += Detach;

        if (OwnsSortBar && _grid != null)
        {
            // Push the cards down below the new bar, as the Deck screen does.
            if (_sortRow != null)
            {
                _grid.YOffset = SortBarGridOffset;
            }

            Redraw();
        }

        Callable.From(() =>
        {
            Place();
            // Not on a combat pile: its open key also closes it, and the box would swallow that.
            if (config.FocusOnOpen && _kind != ScreenKind.Pile && _root.Visible)
            {
                _input.GrabFocus();
            }
        }).CallDeferred();
    }

    /// <summary>
    /// The Deck screen's sort bar, rebuilt on a picker or pile from the same parts: the tab-bar strip tinted
    /// with the character's card-frame material, and the game's own sort-button scene. Sizes and
    /// placement copy SortingOptions in deck_view_screen.tscn. Returns the first sort button, or
    /// null if the parts could not be found (the box then falls back to a plain one).
    /// </summary>
    private Control? BuildSortBar()
    {
        if (_grid?.GetNodeOrNull<Control>("%ScrollContainer") is not { } scroll)
        {
            Log.Warn($"[DeckSearch] {_screen.GetType().Name} grid has no %ScrollContainer, no sort bar.");
            return null;
        }

        PackedScene? buttonScene = ResourceLoader.Load<PackedScene>(SortButtonScene);
        if (buttonScene == null)
        {
            Log.Warn($"[DeckSearch] {SortButtonScene} not found, no sort bar.");
            return null;
        }

        var bar = new Control
        {
            Name = "DeckSearchSortBar",
            CustomMinimumSize = new Vector2(200, 60),
        };
        bar.SetAnchorAndOffset(Side.Left, 0, 56);
        bar.SetAnchorAndOffset(Side.Top, 0, 92);
        bar.SetAnchorAndOffset(Side.Right, 1, -33);
        bar.SetAnchorAndOffset(Side.Bottom, 0, 152);

        ShaderMaterial? hue = CharacterHue();
        var background = new TextureRect
        {
            Name = "SortingBg",
            Texture = ResourceLoader.Load<Texture2D>(SortBarTexture),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Material = hue,
        };
        bar.AddChild(background);
        background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        var row = new HBoxContainer
        {
            Name = "HBoxContainer",
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        row.AddThemeConstantOverride("separation", 50);
        bar.AddChild(row);

        // Bottom before top: Godot never lets the top anchor pass the bottom one, so setting the
        // top to 0.5 first snaps it back to 0 and the row comes out 72px tall instead of 42.
        row.SetAnchorAndOffset(Side.Right, 1, 0);
        row.SetAnchorAndOffset(Side.Bottom, 0.5f, 21);
        row.SetAnchorAndOffset(Side.Left, 0, 0);
        row.SetAnchorAndOffset(Side.Top, 0.5f, -21);

        var sorts = new (string Label, SortingOrders Ascending, SortingOrders Descending)[]
        {
            ("SORT_OBTAINED", SortingOrders.Ascending, SortingOrders.Descending),
            ("SORT_TYPE", SortingOrders.TypeAscending, SortingOrders.TypeDescending),
            ("SORT_COST", SortingOrders.CostAscending, SortingOrders.CostDescending),
            ("SORT_ALPHABET", SortingOrders.AlphabetAscending, SortingOrders.AlphabetDescending),
        };

        var buttons = new List<NCardViewSortButton>();
        foreach (var _ in sorts)
        {
            var button = buttonScene.Instantiate<NCardViewSortButton>();
            button.CustomMinimumSize = new Vector2(250, 42);
            row.AddChild(button);
            buttons.Add(button);
        }

        // Behind the cards, as on the Deck screen, so a hovered card in the top row draws over it.
        scroll.AddChild(bar);
        scroll.MoveChild(bar, 0);

        // The buttons only find their own nodes once _Ready has run, which adding the bar did.
        for (int i = 0; i < buttons.Count; i++)
        {
            NCardViewSortButton button = buttons[i];
            (string label, SortingOrders ascending, SortingOrders descending) = sorts[i];
            button.SetLabel(new LocString("gameplay_ui", label).GetRawText());
            if (hue != null)
            {
                button.SetHue(hue);
            }

            button.Connect(NClickableControl.SignalName.Released, Callable.From<NButton>(_ => OnSort(button, ascending, descending)));
        }

        return buttons[0];
    }

    /// <summary>Same rule as the Deck screen: the last sort clicked leads, the others break ties.</summary>
    private void OnSort(NCardViewSortButton button, SortingOrders ascending, SortingOrders descending)
    {
        _sorting.Remove(ascending);
        _sorting.Remove(descending);
        _sorting.Insert(0, button.IsDescending ? descending : ascending);
        Redraw();
    }

    /// <summary>
    /// The material the Deck screen tints its bar with: the owner's card-frame colour. The game
    /// never opens an empty pile, so there is always a card to ask.
    /// </summary>
    private ShaderMaterial? CharacterHue()
    {
        try
        {
            IEnumerable<CardModel> cards = _screen is NCardPileScreen pile ? pile.Pile.Cards : _pickerCards;
            return cards.FirstOrDefault()?.Owner?.Character.CardPool.FrameMaterial as ShaderMaterial;
        }
        catch (Exception ex)
        {
            Log.Warn($"[DeckSearch] No character colour for the sort bar: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// A sort button's background, copied with its own copy of the hue shader. The screen has
    /// already tinted the buttons for the character (NDeckViewScreen._Ready calls SetHue before
    /// our postfix), so the copy arrives in the right colour for whoever is being played.
    /// </summary>
    private Control CreateThemedBox(Control sorter, LineEdit input, SearchConfig config, TextStyle style)
    {
        float height = sorter.CustomMinimumSize.Y > 0 ? sorter.CustomMinimumSize.Y : 42;
        var box = new Control
        {
            CustomMinimumSize = new Vector2(config.SearchWidth, height),
            MouseFilter = Control.MouseFilterEnum.Pass,
        };

        if (sorter.GetNodeOrNull<TextureRect>("%ButtonImage") is { } image)
        {
            var background = (TextureRect)image.Duplicate();
            background.Name = "Background";
            background.MouseFilter = Control.MouseFilterEnum.Ignore;
            background.Scale = Vector2.One;
            if (image.Material is ShaderMaterial material)
            {
                _hsv = (ShaderMaterial)material.Duplicate();
                _hsv.SetShaderParameter(SaturationParam, RestShade);
                _hsv.SetShaderParameter(ValueParam, RestShade);
                background.Material = _hsv;
            }

            box.AddChild(background);
            background.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        }
        else
        {
            Log.Warn("[DeckSearch] Sort button has no %ButtonImage, the search box will be unstyled.");
        }

        box.AddChild(input);
        input.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);

        // Brighten on hover and while typing, as the sort buttons do on hover.
        input.MouseEntered += () => SetHovered(true);
        input.MouseExited += () => SetHovered(false);
        input.FocusEntered += UpdateShade;
        input.FocusExited += UpdateShade;

        _icon = CreateIcon();
        if (_icon != null)
        {
            // Under the input, so clicks on the icon still land in the text box.
            box.AddChild(_icon);
            box.MoveChild(_icon, input.GetIndex());
        }

        _clearButton = CreateClearButton(style);
        if (_clearButton != null)
        {
            // Above the input, so it gets the click rather than the text box.
            box.AddChild(_clearButton);
        }

        box.Resized += ApplyFocusState;
        return box;
    }

    /// <summary>
    /// Narrows the sort buttons to what their labels need (or sortButtonWidth, whichever is
    /// wider) and tightens the gaps, to make room for the box. Measuring the labels keeps longer
    /// translations from spilling out of their buttons.
    /// </summary>
    private static void FitSortRow(HBoxContainer row, SearchConfig config)
    {
        row.AddThemeConstantOverride("separation", config.SortButtonSpacing);
        foreach (Control button in row.GetChildren().OfType<NCardViewSortButton>())
        {
            // The label row starts 8px in (deck_view_sort_button.tscn); leave room on both sides.
            float content = button.GetNodeOrNull<Control>("HBoxContainer")?.GetCombinedMinimumSize().X ?? 0;
            float width = Math.Max(config.SortButtonWidth, content + 24);
            button.CustomMinimumSize = new Vector2(width, button.CustomMinimumSize.Y);

            // The button scales its image from a pivot set for the original 256px width.
            if (button.GetNodeOrNull<Control>("%ButtonImage") is { } image)
            {
                image.PivotOffset = new Vector2(width / 2, image.PivotOffset.Y);
            }
        }
    }

    /// <summary>
    /// The icon of Withering Presence (the Aeonglass boss power), which reads as a magnifying
    /// glass. Taken from the power model, so a game update that moves the file still finds it.
    /// </summary>
    private static TextureRect? CreateIcon()
    {
        try
        {
            if (_iconTexture == null)
            {
                PowerModel power = ModelDb.Power<WitheringPresencePower>();
                Texture2D? source = ResourceLoader.Load<Texture2D>(power.ResolvedBigIconPath) ?? power.Icon;
                if (source == null)
                {
                    return null;
                }

                // Drawn straight from the big icon, the GPU squeezes it about 8x with no mipmaps,
                // which looks harsh. Pre-shrinking at twice the display size with a good filter,
                // plus mipmaps, softens it and still leaves detail for higher resolutions.
                _iconTexture = Resample(source, (int)IconSize * 2, mipmaps: true) ?? source;
            }

            return new TextureRect
            {
                Name = "Icon",
                Texture = _iconTexture,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps,
                MouseFilter = Control.MouseFilterEnum.Ignore,
                Size = new Vector2(IconSize, IconSize),
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"[DeckSearch] No search icon ({ex.Message}), the box will have none.");
            return null;
        }
    }

    /// <summary>
    /// The × is a glyph in the box's own font: the game's red (StsColors.red, its [red] text colour)
    /// with the same soft dark outline as the gold text, so it reads as part of the theme.
    ///
    /// Two dead ends first. LineEdit's built-in clear button has no icon in the game theme, and
    /// copying the game's back_button_x sprite into that slot came out blank, because reading pixels
    /// back out of the packed atlas fails. Drawing that sprite as a TextureRect worked but looked
    /// sharp and out of place.
    /// </summary>
    private Label? CreateClearButton(TextStyle style)
    {
        try
        {
            var button = new Label
            {
                Name = "Clear",
                Text = "×",
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MouseFilter = Control.MouseFilterEnum.Stop,
                MouseDefaultCursorShape = Control.CursorShape.PointingHand,
                Visible = false,
            };
            style.ApplyTo(button, ClearFontSize);
            button.AddThemeColorOverride("font_color", StsColors.red);
            button.GuiInput += OnClearGuiInput;
            button.MouseEntered += () => button.Scale = new Vector2(1.15f, 1.15f);
            button.MouseExited += () => button.Scale = Vector2.One;
            return button;
        }
        catch (Exception ex)
        {
            Log.Warn($"[DeckSearch] Could not create the clear button: {ex.Message}");
            return null;
        }
    }

    private void OnClearGuiInput(InputEvent inputEvent)
    {
        if (inputEvent is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            return;
        }

        ClearQuery();
        _input?.GrabFocus();
        _clearButton?.AcceptEvent();
    }

    /// <summary>A copy of the texture scaled to fit size x size (keeping its shape), or null.</summary>
    private static Texture2D? Resample(Texture2D source, int size, bool mipmaps)
    {
        Image? image = source.GetImage();
        if (image == null || image.IsEmpty())
        {
            return null;
        }

        if (image.IsCompressed())
        {
            image.Decompress();
        }

        float scale = (float)size / Math.Max(image.GetWidth(), image.GetHeight());
        image.Resize(
            Math.Max(1, (int)Math.Round(image.GetWidth() * scale)),
            Math.Max(1, (int)Math.Round(image.GetHeight() * scale)),
            Image.Interpolation.Lanczos);
        if (mipmaps)
        {
            image.GenerateMipmaps();
        }

        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// The icon stays put at the right end, nudged up a touch to sit level with the text. The
    /// right margin keeps the text and the clear button clear of it, so the × lands just left of
    /// the icon. The "Search..." prompt shows only while the box is idle and empty.
    /// </summary>
    private void ApplyFocusState()
    {
        if (_input == null)
        {
            return;
        }

        bool focused = _input.HasFocus();
        _input.PlaceholderText = focused ? "" : Placeholder;

        if (_root == null || _root == _input)
        {
            SetInputMargins(16, 8);
            return;
        }

        // Right to left: the icon, then the × while there is text, then the text itself.
        float right = _root.Size.X - IconPad;
        if (_icon != null)
        {
            right -= IconSize;
            _icon.Position = new Vector2(right, ((_root.Size.Y - IconSize) / 2) - IconRaise);
            right -= ClearGap;
        }

        bool showClear = _clearButton != null && _input.Text.Length > 0;
        if (_clearButton != null)
        {
            _clearButton.Visible = showClear;
            // Full height with the glyph centred by the label, then lifted a touch.
            // Centred on its slot even if the outlined glyph needs more than ClearSize.
            float width = Math.Max(ClearSize, _clearButton.GetCombinedMinimumSize().X);
            _clearButton.Position = new Vector2(right - (ClearSize / 2) - (width / 2), -ClearRaise);
            _clearButton.Size = new Vector2(width, _root.Size.Y);
            _clearButton.PivotOffset = _clearButton.Size / 2;
        }

        if (showClear)
        {
            right -= ClearSize + ClearGap;
        }

        SetInputMargins(16, Math.Max(8, _root.Size.X - right));
    }

    private void SetInputMargins(float left, float right)
    {
        if (_input == null)
        {
            return;
        }

        // No box of its own: the copied button image is the background.
        foreach (string name in new[] { "normal", "focus", "read_only" })
        {
            _input.AddThemeStyleboxOverride(name, new StyleBoxEmpty { ContentMarginLeft = left, ContentMarginRight = right });
        }
    }

    private void SetHovered(bool hovered)
    {
        _hovered = hovered;
        UpdateShade();
    }

    private void UpdateShade()
    {
        if (_hsv == null || _root == null || _input == null || !_root.IsInsideTree())
        {
            return;
        }

        float target = _hovered || _input.HasFocus() ? LitShade : RestShade;
        _shadeTween?.Kill();
        _shadeTween = _root.CreateTween().SetParallel();
        _shadeTween.TweenProperty(_hsv, "shader_parameter/s", target, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
        _shadeTween.TweenProperty(_hsv, "shader_parameter/v", target, 0.5).SetEase(Tween.EaseType.Out).SetTrans(Tween.TransitionType.Expo);
    }

    /// <summary>The sort buttons' label look: gold Kreon with a soft black outline.</summary>
    private readonly record struct TextStyle(Font? Font, int FontSize, Color Color, Color Outline, int OutlineSize)
    {
        // Fallbacks copied from deck_view_sort_button.tscn, used only if the label is missing.
        private static readonly TextStyle Default = new(null, 22, new Color(0.937f, 0.784f, 0.318f), new Color(0, 0, 0, 0.5f), 12);

        public static TextStyle From(Control? sorter)
        {
            if (sorter?.GetNodeOrNull<Control>("%Label") is not { } label)
            {
                return Default;
            }

            return new TextStyle(
                label.GetThemeFont("font"),
                label.GetThemeFontSize("font_size"),
                label.GetThemeColor("font_color"),
                label.GetThemeColor("font_outline_color"),
                label.GetThemeConstant("outline_size"));
        }

        public void ApplyTo(LineEdit input)
        {
            ApplyCommon(input, FontSize);
            input.AddThemeColorOverride("caret_color", Color);
            input.AddThemeColorOverride("selection_color", new Color(Color, 0.35f));
            input.AddThemeColorOverride("font_selected_color", Colors.White);
            // Opaque on purpose: a faded prompt looked off. It is hidden on focus instead.
            input.AddThemeColorOverride("font_placeholder_color", Color);
        }

        public void ApplyTo(Label label, int fontSize) => ApplyCommon(label, fontSize);

        private void ApplyCommon(Control control, int fontSize)
        {
            if (Font != null)
            {
                control.AddThemeFontOverride("font", Font);
            }

            control.AddThemeFontSizeOverride("font_size", fontSize);
            control.AddThemeColorOverride("font_color", Color);
            control.AddThemeColorOverride("font_outline_color", Outline);
            control.AddThemeConstantOverride("outline_size", OutlineSize);
        }
    }

    /// <summary>
    /// Esc clears a query, then a second Esc leaves the box so the game's own Esc (close screen)
    /// works again. The game ignores its hotkeys while a text box is being edited. With
    /// type-to-search one Esc does both, since typing on gets straight back into the box.
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
            if (SearchConfig.Current.TypeToSearch)
            {
                _input.ReleaseFocus();
            }
        }
        else
        {
            _input.ReleaseFocus();
        }

        _input.AcceptEvent();
    }

    private void ClearQuery()
    {
        if (_input != null)
        {
            // Setting Text in code does not raise TextChanged, so pass the change on ourselves.
            _input.Clear();
            OnQueryChanged("");
            ApplyFocusState();
        }
    }

    /// <summary>
    /// Every open screen has a shortcut button, and the first to see the key swallows it, so
    /// whichever button fires hands the key to the box on the screen in front: the Deck screen
    /// can be open over a picker. A card being inspected is in front of both, so nothing happens.
    /// </summary>
    private static void FocusCurrentBox()
    {
        foreach (KeyValuePair<Control, SearchBox> entry in Boxes)
        {
            if (entry.Value.IsCurrentScreen())
            {
                entry.Value.FocusBox();
                return;
            }
        }
    }

    private bool IsCurrentScreen()
    {
        return GodotObject.IsInstanceValid(_screen)
            && _screen.IsInsideTree()
            && _screen is IScreenContext screen
            && ActiveScreenContext.Instance.IsCurrent(screen);
    }

    private void FocusBox()
    {
        if (!CanTakeFocus() || _input == null)
        {
            return;
        }

        ScrollToTop();
        _input.GrabFocus();
        _input.SelectAll();
    }

    /// <summary>Hidden under a controller, or a picker's confirm view covering the cards: no.</summary>
    private bool CanTakeFocus()
    {
        if (_input == null || _root == null || !_root.IsVisibleInTree())
        {
            return false;
        }

        // A picker stops its grid scrolling while its confirm view covers the cards.
        return _grid == null || ScrollingEnabledField?.GetValue(_grid) is not false;
    }

    /// <summary>
    /// Type-to-search: a printable key the box did not get, because it is not focused, starts a
    /// new search in the box on the screen in front. Returns false to leave the key to the game:
    /// no box in front, a shortcut chord, space/Enter/Esc/arrows, or a dev-console key.
    /// </summary>
    public static bool TryTypeInto(InputEvent inputEvent)
    {
        if (!SearchConfig.Current.TypeToSearch
            || inputEvent is not InputEventKey { Pressed: true, Echo: false } key
            || key.Unicode <= ' '
            || IsConsoleKey(key))
        {
            return false;
        }

        // Ctrl+C and friends are shortcuts, but AltGr (which Windows reports as Ctrl+Alt) types.
        if ((key.CtrlPressed || key.MetaPressed) && !key.AltPressed)
        {
            return false;
        }

        foreach (KeyValuePair<Control, SearchBox> entry in Boxes)
        {
            if (entry.Value.IsCurrentScreen())
            {
                return entry.Value.StartTyping(char.ConvertFromUtf32((int)key.Unicode));
            }
        }

        return false;
    }

    /// <summary>The keys NDevConsole._Input opens and closes the console with, and the console itself.</summary>
    private static bool IsConsoleKey(InputEventKey key)
    {
        if (key.Keycode is Key.Quoteleft or Key.Apostrophe or Key.Asterisk or Key.Asciicircum
            || (key.ShiftPressed && key.Keycode == Key.Key8))
        {
            return true;
        }

        try
        {
            return NDevConsole.Instance.Visible;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Replaces any earlier search with the typed character, as Tom chose.</summary>
    private bool StartTyping(string text)
    {
        if (!CanTakeFocus() || _input == null || _input.HasFocus())
        {
            return false;
        }

        ScrollToTop();
        _input.GrabFocus();
        // Setting Text in code does not raise TextChanged, so pass the change on ourselves.
        _input.Text = text;
        _input.CaretColumn = _input.Text.Length;
        OnQueryChanged(_input.Text);
        ApplyFocusState();
        return true;
    }

    /// <summary>
    /// The box scrolls away with the cards, and the grid scrolls by moving its content rather
    /// than through a ScrollContainer, so focus alone never brings it back into view. Setting
    /// the grid's scroll target lets its own easing glide back up to the bar.
    /// </summary>
    private void ScrollToTop()
    {
        if (_grid == null || TargetDragField == null || ScrollLimitTopProperty?.GetValue(_grid) is not float top)
        {
            return;
        }

        TargetDragField.SetValue(_grid, top);
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
        button.Pressed += FocusCurrentBox;
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
    /// In the sort row, the row does the layout; this only shrinks the box if the row would
    /// overflow the bar, and puts the "no matches" label under the bar. Without the row (the
    /// fallback), the box goes to the top right of the screen.
    /// </summary>
    private void Place()
    {
        if (_root == null || !GodotObject.IsInstanceValid(_screen) || !_screen.IsInsideTree())
        {
            return;
        }

        Transform2D toLocal = _screen.GetGlobalTransform().AffineInverse();
        Vector2 screenSize = _screen.Size;
        float barBottom = 160;

        if (_sortRow != null && _sortRow.GetParent() is Control bar)
        {
            float overflow = _sortRow.GetCombinedMinimumSize().X - (bar.Size.X - 40);
            if (overflow > 0)
            {
                _root.CustomMinimumSize = new Vector2(Math.Max(160, _root.CustomMinimumSize.X - overflow), _root.CustomMinimumSize.Y);
            }

            barBottom = (toLocal * bar.GetGlobalRect().End).Y;
        }
        else
        {
            Vector2 size = _root.GetCombinedMinimumSize();
            _root.Position = new Vector2(screenSize.X - size.X - 220, 110);
            _root.Size = size;
            barBottom = _root.Position.Y + size.Y;
        }

        if (_emptyLabel != null)
        {
            _emptyLabel.Position = new Vector2(0, barBottom + 120);
            _emptyLabel.Size = new Vector2(screenSize.X, 60);
        }
    }

    private void UpdateEmptyLabel()
    {
        if (_emptyLabel == null || !GodotObject.IsInstanceValid(_emptyLabel))
        {
            return;
        }

        _emptyLabel.Visible = !_query.IsEmpty && _shown == 0 && _total > 0;
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

    // ---------------------------------------------------------------- console

    public string Diagnostics()
    {
        var text = new StringBuilder();
        text.AppendLine($"Box: {(_sortRow != null ? "in the sort row" : "fallback, on the screen")} at {_root?.GetGlobalRect()} visible={_root?.Visible} themed={_hsv != null}");
        if (_sortRow != null)
        {
            text.AppendLine($"Sort row: {_sortRow.GetGlobalRect()} separation={_sortRow.GetThemeConstant("separation")}");
            foreach (Node child in _sortRow.GetChildren())
            {
                string rect = child is Control control ? $" {control.GetGlobalRect()} min={control.CustomMinimumSize}" : "";
                text.AppendLine($"  {child.Name}{rect}");
            }
        }

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
