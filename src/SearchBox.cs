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
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
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

    private static readonly StringName SaturationParam = "s";

    private static readonly StringName ValueParam = "v";

    /// <summary>The sort buttons' resting and hovered shader brightness (NCardViewSortButton).</summary>
    private const float RestShade = 0.8f;

    private const float LitShade = 1f;

    /// <summary>The most recently opened box, for the console command.</summary>
    private static WeakReference<SearchBox>? _latest;

    private readonly NDeckViewScreen _screen;

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
    private const float ClearRaise = 3;

    /// <summary>Space between the × and the icon.</summary>
    private const float ClearGap = 6;

    private const string Placeholder = "Search...";

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

        _input = new LineEdit
        {
            Name = "Input",
        };
        _input.TextChanged += OnQueryChanged;
        _input.TextSubmitted += _ => _input.ReleaseFocus();
        _input.GuiInput += OnInputGuiInput;

        // The sort buttons live in an HBoxContainer inside the grid's scroll area, so joining that
        // row puts the box on the bar, lets the row lay it out, and scrolls it with the cards.
        Control? sorter = _screen.GetNodeOrNull<Control>("%ObtainedSorter");
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
        if (_input != null)
        {
            // Setting Text in code does not raise TextChanged, so pass the change on ourselves.
            _input.Clear();
            OnQueryChanged("");
            ApplyFocusState();
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
