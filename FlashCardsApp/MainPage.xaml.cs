using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Maui.Storage;
#if WINDOWS
using Microsoft.UI.Xaml.Input;
using Windows.System;
#endif

namespace FlashCardsApp;

public partial class MainPage : ContentPage
{
    private readonly Random _random = new();
    private FlashCard? _currentCard;
    private bool _showingBack;
    private bool _isMaximized;
    private double _imageScale = 1;
    private string? _generatedFilePath;
    private CancellationTokenSource? _autoRollCancellation;
    private TaskCompletionSource<bool>? _autoRollResumeSignal;
    private bool _autoRollPaused;
    private double? _autoRollSeconds;
    private bool _shuffleEnabled;
    private bool _keyboardEnabled;
    private bool _stateLoaded;
#if WINDOWS
    private Microsoft.UI.Xaml.FrameworkElement? _windowsNativeView;
    private KeyEventHandler? _windowsKeyHandler;
#endif
    private const double NormalFlashAreaHeight = 400;
#if ANDROID
    private const double MaximizedBottomReserve = 96;
    private const double MaximizedScrollExtension = 0;
#else
    private const double MaximizedBottomReserve = 96;
    private const double MaximizedScrollExtension = 0;
#endif

    private static string SavedStatePath => Path.Combine(FileSystem.Current.AppDataDirectory, "flashcards-state.json");

    public ObservableCollection<FlashCard> Cards { get; } = new();
    public ObservableCollection<FlashCard> DiscardedCards { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        HandlerChanged += OnMainPageHandlerChanged;
        if (FindByName("CopyPathButton") is Button copyPathButton)
            copyPathButton.Clicked += OnCopyPathClicked;
        BindingContext = this;
        UpdateDiscardActions();
        RefreshFlashArea();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        AttachWindowsKeyboard();
        if (_stateLoaded)
            return;

        _stateLoaded = true;
        await LoadSavedStateAsync();
    }

    private FlashCard? CurrentCard => _currentCard;

    protected override void OnDisappearing()
    {
#if WINDOWS
        DetachWindowsKeyboard();
#endif
        StopAutoRoll();
        base.OnDisappearing();
    }

    private void AttachWindowsKeyboard()
    {
#if WINDOWS
        if (!_keyboardEnabled || _windowsNativeView is not null || Handler?.PlatformView is not Microsoft.UI.Xaml.FrameworkElement nativeView)
            return;

        _windowsNativeView = nativeView;
        _windowsKeyHandler = OnWindowsKeyDown;
        _windowsNativeView.AddHandler(Microsoft.UI.Xaml.UIElement.PreviewKeyDownEvent, _windowsKeyHandler, true);
        FlashImage.Focus();
#endif
    }

    private void DetachWindowsKeyboard()
    {
#if WINDOWS
        if (_windowsNativeView is not null)
        {
            if (_windowsKeyHandler is not null)
                _windowsNativeView.RemoveHandler(Microsoft.UI.Xaml.UIElement.PreviewKeyDownEvent, _windowsKeyHandler);
            _windowsNativeView = null;
            _windowsKeyHandler = null;
        }
#endif
    }

    private void OnMainPageHandlerChanged(object? sender, EventArgs e) => AttachWindowsKeyboard();

#if WINDOWS
    private void OnWindowsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_keyboardEnabled)
            return;

        if (e.KeyStatus.WasKeyDown)
            return;

        var direction = e.Key switch
        {
            VirtualKey.Left => -1,
            VirtualKey.Right => 1,
            _ => 0
        };

        if (direction != 0)
        {
            e.Handled = true;
            HandleKeyboardNavigation(direction);
            return;
        }

        if (e.Key == VirtualKey.Space)
        {
            e.Handled = true;
            HandleKeyboardFlip();
        }

        else if (e.Key == VirtualKey.F11)
        {
            e.Handled = true;
            HandleKeyboardMaximize();
        }
        else if (e.Key == VirtualKey.Escape && _isMaximized)
        {
            e.Handled = true;
            HandleKeyboardMinimize();
        }
    }
#endif

    private async void OnAddImageClicked(object? sender, EventArgs e)
    {
        var file = await PickFileAsync("Select the front image");
        if (file is null)
            return;

        var draft = new NewCardDraft
        {
            FrontPath = await CopyToAppDataAsync(file),
            FrontName = Path.GetFileNameWithoutExtension(file.FileName)
        };

        var frontAction = await ConfigureFrontImageAsync(draft);
        if (!frontAction)
            return;

        var card = new FlashCard { Name = draft.FrontName, FrontImage = draft.FrontPath, BackImage = draft.BackPath, BackName = draft.BackName };
        Cards.Add(card);
        _currentCard = card;
        _showingBack = false;
        RefreshFlashArea();
        await SaveStateAsync();
        StatusLabel.Text = string.IsNullOrWhiteSpace(draft.BackPath) ? "Added (no back image)" : "Card added";
    }

    private async void OnLoadDataClicked(object? sender, EventArgs e)
    {
        var file = await PickFileAsync("Select a flashcard package or JSON file", ".flashcards");
        if (file is null)
            return;

        await RunBusyAsync(async () =>
        {
            var imported = await ReadCardsAsync(file);
            if (imported.Count == 0)
                throw new InvalidDataException("The JSON file does not contain any valid cards.");

            var loadConfirmed = await DisplayAlert(
                "Load data",
                $"Validated {imported.Count} cards. Loading will replace the current cards. Continue?",
                "Yes",
                "No");
            if (!loadConfirmed)
                return;

            Cards.Clear();
            DiscardedCards.Clear();
            foreach (var card in imported)
                Cards.Add(card);

            _currentCard = Cards[0];
            _showingBack = false;
            RefreshFlashArea();
            await SaveStateAsync();
            StatusLabel.Text = $"Loaded {Cards.Count} cards";
        });
    }

    private async void OnIncrementDataClicked(object? sender, EventArgs e)
    {
        var file = await PickFileAsync("Select a flashcard package or JSON file", ".flashcards");
        if (file is null)
            return;

        await RunBusyAsync(async () =>
        {
            var imported = await ReadCardsAsync(file);
            var existingNames = Cards
                .Select(card => card.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newCards = imported
                .Where(card => existingNames.Add(card.Name))
                .ToList();
            if (newCards.Count == 0)
            {
                await DisplayAlert("Increment data", "Validation found no new cards to add.", "OK");
                return;
            }

            var incrementConfirmed = await DisplayAlert(
                "Increment data",
                $"Validated {imported.Count} cards. {newCards.Count} new cards will be added. Continue?",
                "Yes",
                "No");
            if (!incrementConfirmed)
                return;

            foreach (var card in newCards)
                Cards.Add(card);

            _currentCard ??= Cards.FirstOrDefault();
            RefreshFlashArea();
            await SaveStateAsync();
            StatusLabel.Text = $"Merged {imported.Count} cards";
        });
    }

    private async void OnGenerateDataClicked(object? sender, EventArgs e)
    {
        if (!Cards.Any())
        {
            await DisplayAlert("Export JSON", "Add or load at least one card first.", "OK");
            return;
        }

        await RunBusyAsync(async () =>
        {
            ValidateCards(Cards);
            var generateConfirmed = await DisplayAlert(
                "Export JSON",
                $"Validated {Cards.Count} cards. Save the JSON file now?",
                "Yes",
                "No");
            if (!generateConfirmed)
                return;

            var packagePath = await CreatePackageAsync(Cards);
            FileSaverResult saveResult;
            try
            {
                await using var packageStream = File.OpenRead(packagePath);
                var fileName = $"flashcards-{DateTime.Now:yyyyMMdd-HHmmss}.flashcards";
                saveResult = await FileSaver.Default.SaveAsync(fileName, packageStream);
            }
            finally
            {
                File.Delete(packagePath);
            }

            if (!saveResult.IsSuccessful)
            {
                if (saveResult.Exception is not null)
                    throw saveResult.Exception;

                StatusLabel.Text = "Export canceled";
                return;
            }

            _generatedFilePath = saveResult.FilePath;
            GeneratedPathLabel.Text = _generatedFilePath;
            var hasGeneratedPath = !string.IsNullOrWhiteSpace(_generatedFilePath);
            LocateButton.IsEnabled = hasGeneratedPath;
            StatusLabel.Text = "JSON exported successfully";
        });
    }

    private async void OnEditClicked(object? sender, EventArgs e)
    {
        if (CurrentCard is null)
        {
            await DisplayAlert("Edit", "There is no current card to edit.", "OK");
            return;
        }
        var action = await DisplayActionSheet(
            "Edit card",
            "Cancel",
            null,
            "Change Front Side",
            "Change/Add Back Side",
            "Edit Name");

        switch (action)
        {
            case "Change Front Side":
            {
                var file = await PickFileAsync("Select a replacement front image");
                if (file is not null)
                    CurrentCard.FrontImage = await CopyToAppDataAsync(file);
                break;
            }
            case "Change/Add Back Side":
            {
                var file = await PickFileAsync("Select a replacement back image");
                if (file is not null)
                {
                    CurrentCard.BackImage = await CopyToAppDataAsync(file);
                    if (string.IsNullOrWhiteSpace(CurrentCard.BackName))
                        CurrentCard.BackName = Path.GetFileNameWithoutExtension(file.FileName);
                }
                break;
            }
            case "Edit Name":
            {
                var name = await DisplayPromptAsync("Front image name", "Name", initialValue: CurrentCard.Name);
                if (!string.IsNullOrWhiteSpace(name))
                    CurrentCard.Name = name.Trim();

                if (!string.IsNullOrWhiteSpace(CurrentCard.BackImage))
                {
                    var backName = await DisplayPromptAsync("Back image name", "Name", initialValue: GetBackName(CurrentCard));
                    if (!string.IsNullOrWhiteSpace(backName))
                        CurrentCard.BackName = backName.Trim();
                }
                break;
            }
        }

        RefreshFlashArea();
        await SaveStateAsync();
    }

    private async Task<bool> ConfigureFrontImageAsync(NewCardDraft draft)
    {
        while (true)
        {
            var action = await DisplayActionSheet(
                "Front image added",
                "Cancel",
                null,
                "Change Front Side",
                "Add Back Side",
                "Edit Name",
                "Proceed anyway");

            switch (action)
            {
                case "Change Front Side":
                {
                    var file = await PickFileAsync("Select the front image");
                    if (file is not null)
                    {
                        draft.FrontPath = await CopyToAppDataAsync(file);
                        draft.FrontName = Path.GetFileNameWithoutExtension(file.FileName);
                    }
                    break;
                }
                case "Add Back Side":
                {
                    var file = await PickFileAsync("Select the back image");
                    if (file is not null)
                    {
                        draft.BackPath = await CopyToAppDataAsync(file);
                        draft.BackName = Path.GetFileNameWithoutExtension(file.FileName);
                        await ConfigureBackImageAsync(draft);
                    }
                    break;
                }
                case "Edit Name":
                {
                    var name = await DisplayPromptAsync("Front image name", "Name", initialValue: draft.FrontName);
                    if (!string.IsNullOrWhiteSpace(name))
                        draft.FrontName = name.Trim();
                    break;
                }
                case "Proceed anyway":
                    return true;
                default:
                    return false;
            }
        }
    }

    private async Task ConfigureBackImageAsync(NewCardDraft draft)
    {
        while (true)
        {
            var action = await DisplayActionSheet(
                "Back image added",
                "Cancel",
                null,
                "Change Back Side",
                "Edit Name",
                "Proceed anyway");

            switch (action)
            {
                case "Change Back Side":
                {
                    var file = await PickFileAsync("Select the back image");
                    if (file is not null)
                    {
                        draft.BackPath = await CopyToAppDataAsync(file);
                        draft.BackName = Path.GetFileNameWithoutExtension(file.FileName);
                    }
                    break;
                }
                case "Edit Name":
                {
                    var name = await DisplayPromptAsync("Back image name", "Name", initialValue: draft.BackName);
                    if (!string.IsNullOrWhiteSpace(name))
                        draft.BackName = name.Trim();
                    break;
                }
                case "Proceed anyway":
                case null:
                    return;
                default:
                    return;
            }
        }
    }

    private void OnClearClicked(object? sender, EventArgs e)
    {
        _currentCard = null;
        _showingBack = false;
        RefreshFlashArea();
        StatusLabel.Text = "Flash area cleared";
    }

    private async void OnFlashImageTapped(object? sender, TappedEventArgs e)
    {
        await AnimateFlipAsync();
    }

    private void OnZoomInClicked(object? sender, EventArgs e) => SetZoom(Math.Min(_imageScale + .1, 2));
    private void OnZoomOutClicked(object? sender, EventArgs e) => SetZoom(Math.Max(_imageScale - .1, .6));
    private void OnResetZoomClicked(object? sender, EventArgs e) => SetZoom(1);

    private void OnShuffleToggled(object? sender, ToggledEventArgs e)
    {
        _shuffleEnabled = e.Value;
        StatusLabel.Text = _shuffleEnabled ? "Shuffle enabled" : "Shuffle disabled";
    }

    private void OnKeyboardToggled(object? sender, ToggledEventArgs e)
    {
        _keyboardEnabled = e.Value;
#if WINDOWS
        if (_keyboardEnabled)
            AttachWindowsKeyboard();
        else
            DetachWindowsKeyboard();
#endif
        StatusLabel.Text = _keyboardEnabled ? "Keyboard controls enabled" : "Keyboard controls disabled";
    }

    internal bool KeyboardShortcutsEnabled => _keyboardEnabled;

    internal void HandleKeyboardFlip() => _ = AnimateFlipAsync();

    internal void HandleKeyboardMaximize() => OnMaximizeClicked(this, EventArgs.Empty);

    internal void HandleKeyboardMinimize()
    {
        if (_isMaximized)
            OnMinimizeClicked(this, EventArgs.Empty);
    }

    private async void OnPreviousClicked(object? sender, EventArgs e) => await NavigateCardAsync(-1);
    private async void OnNextClicked(object? sender, EventArgs e) => await NavigateCardAsync(1);

    internal void HandleKeyboardNavigation(int direction)
    {
        if (direction is -1 or 1)
            _ = NavigateCardAsync(direction);
    }

    private async Task NavigateCardAsync(int direction)
    {
        var available = Cards.Except(DiscardedCards).ToList();
        if (available.Count == 0)
        {
            await ShowNoCardsWarningAsync();
            return;
        }

        if (_shuffleEnabled)
        {
            await AnimateCardChangeAsync(available[_random.Next(available.Count)]);
            return;
        }

        var currentIndex = CurrentCard is null ? -1 : available.IndexOf(CurrentCard);
        var nextIndex = currentIndex < 0
            ? direction > 0 ? 0 : available.Count - 1
            : (currentIndex + direction + available.Count) % available.Count;
        await AnimateCardChangeAsync(available[nextIndex]);
    }

    private void SetZoom(double scale)
    {
        _imageScale = scale;
        FlashImage.Scale = scale;
        ResetZoomButton.IsEnabled = Math.Abs(scale - 1) > .001;
        UpdateAutoRollActions();
    }

    private async void OnRollClicked(object? sender, EventArgs e)
    {
        await RollToRandomCardAsync();
    }

    private async void OnFlashClicked(object? sender, EventArgs e)
    {
        var available = Cards.Except(DiscardedCards).ToList();
        if (available.Count == 0)
        {
            await ShowNoCardsWarningAsync();
            return;
        }

        if (CurrentCard is null || DiscardedCards.Contains(CurrentCard))
        {
            await RollToRandomCardAsync();
            return;
        }

        await MoveCurrentToDiscardAsync();
    }

    private async void OnFlashAllClicked(object? sender, EventArgs e)
    {
        var remaining = Cards.Except(DiscardedCards).ToList();
        if (remaining.Count == 0)
        {
            await DisplayAlert("Flash all", "There are no cards on hand to flash.", "OK");
            return;
        }

        var incompleteCard = remaining.FirstOrDefault(card =>
            string.IsNullOrWhiteSpace(card.Name) ||
            string.IsNullOrWhiteSpace(card.FrontImage) ||
            string.IsNullOrWhiteSpace(card.BackImage));
        if (incompleteCard is not null)
        {
            StatusLabel.Text = "Flash All validation failed";
            await DisplayAlert("Flash all", "Every card must have a name, front image, and back image before using Flash All.", "OK");
            return;
        }

        var confirmed = await DisplayAlert(
            "Flash all",
            $"Move all {remaining.Count} cards to the discard area?",
            "Yes",
            "No");
        if (!confirmed)
            return;

        foreach (var card in remaining)
        {
            _currentCard = card;
            RefreshFlashArea();
            await Task.Delay(120);
            DiscardedCards.Add(card);
        }

        ScrollDiscardCollectionToEnd();
        _currentCard = Cards.Except(DiscardedCards).FirstOrDefault();
        RefreshFlashArea();
        UpdateDiscardActions();
        await SaveStateAsync();
        await ShowNoCardsWarningAsync();
    }

    private async Task MoveCurrentToDiscardAsync()
    {
        if (CurrentCard is null)
            return;

        if (!DiscardedCards.Contains(CurrentCard))
        {
            DiscardedCards.Add(CurrentCard);
            ScrollDiscardCollectionToEnd();
        }

        await SaveStateAsync();

        var available = Cards.Except(DiscardedCards).ToList();
        if (available.Count == 0)
        {
            _currentCard = null;
            _showingBack = false;
            RefreshFlashArea();
            await SaveStateAsync();
            await ShowNoCardsWarningAsync();
            return;
        }

        await NavigateCardAsync(1);
        await SaveStateAsync();
    }

    private async Task AnimateFlipAsync()
    {
        if (CurrentCard is null)
            return;

        var angle = _showingBack ? -90 : 90;
        await FlashImage.RotateYTo(angle, 180, Easing.CubicInOut);
        _showingBack = !_showingBack;
        RefreshFlashArea();
        await FlashImage.RotateYTo(0, 180, Easing.CubicInOut);
    }

    private async Task AnimateCardChangeAsync(FlashCard card)
    {
        await FlashImage.RotateYTo(90, 180, Easing.CubicInOut);
        _currentCard = card;
        _showingBack = false;
        RefreshFlashArea();
        await FlashImage.RotateYTo(0, 180, Easing.CubicInOut);
    }

    private async Task RollToRandomCardAsync()
    {
        var available = Cards.Except(DiscardedCards).ToList();
        if (available.Count == 0)
        {
            await ShowNoCardsWarningAsync();
            return;
        }

        if (_shuffleEnabled)
        {
            await AnimateCardChangeAsync(available[_random.Next(available.Count)]);
            return;
        }

        var currentIndex = CurrentCard is null ? -1 : available.IndexOf(CurrentCard);
        var nextIndex = currentIndex < 0 ? 0 : (currentIndex + 1) % available.Count;
        await AnimateCardChangeAsync(available[nextIndex]);
    }

    private async void OnAutoRollClicked(object? sender, EventArgs e)
    {
        if (!Cards.Except(DiscardedCards).Any())
        {
            await ShowNoCardsWarningAsync();
            return;
        }

        var input = await DisplayPromptAsync(
            "Auto Roll",
            "How many seconds should each card cycle take?",
            initialValue: _autoRollSeconds?.ToString(CultureInfo.CurrentCulture) ?? "2");
        if (input is null)
            return;

        if (!TryParsePositiveSeconds(input, out var seconds))
        {
            await DisplayAlert("Auto Roll", "Enter a positive number of seconds.", "OK");
            return;
        }

        StopAutoRoll();
        _autoRollSeconds = seconds;
        _autoRollPaused = false;
        _autoRollCancellation = new CancellationTokenSource();
        UpdateAutoRollActions();

        await RollToRandomCardAsync();

        _ = RunAutoRollAsync(_autoRollCancellation.Token);
        StatusLabel.Text = $"Auto Roll running ({seconds:0.##} seconds)";
    }

    private void OnStopClicked(object? sender, EventArgs e)
    {
        StopAutoRoll();
        StatusLabel.Text = "Auto Roll stopped";
    }

    private void OnPauseClicked(object? sender, EventArgs e)
    {
        if (_autoRollCancellation is null)
            return;

        _autoRollPaused = !_autoRollPaused;
        if (!_autoRollPaused)
            _autoRollResumeSignal?.TrySetResult(true);

        PauseButton.Text = _autoRollPaused ? "Play" : "Pause";
        StatusLabel.Text = _autoRollPaused ? "Auto Roll paused" : "Auto Roll running";
    }

    private async Task RunAutoRollAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(_autoRollSeconds!.Value / 2), cancellationToken);
                await WaitForAutoRollResumeAsync(cancellationToken);
                await AnimateFlipAsync();

                await Task.Delay(TimeSpan.FromSeconds(_autoRollSeconds.Value / 2), cancellationToken);
                await WaitForAutoRollResumeAsync(cancellationToken);
                await RollToRandomCardAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task WaitForAutoRollResumeAsync(CancellationToken cancellationToken)
    {
        while (_autoRollPaused)
        {
            _autoRollResumeSignal ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _autoRollResumeSignal.Task.WaitAsync(cancellationToken);
        }
    }

    private void StopAutoRoll()
    {
        _autoRollPaused = false;
        _autoRollResumeSignal?.TrySetResult(true);
        _autoRollCancellation?.Cancel();
        _autoRollCancellation?.Dispose();
        _autoRollCancellation = null;
        _autoRollResumeSignal = null;
        _autoRollSeconds = null;
        UpdateAutoRollActions();
    }

    private void UpdateAutoRollActions()
    {
        var isRunning = _autoRollCancellation is not null;
        var cardsOnHand = Cards.Except(DiscardedCards).Count();
        var hasDiscardedCards = DiscardedCards.Count > 0;
        var hasSelection = DiscardCollection.SelectedItems?.Count > 0;
        var canUseOtherActions = !isRunning;

        AutoRollButton.IsEnabled = canUseOtherActions && cardsOnHand > 0;
        StopButton.IsEnabled = isRunning;
        PauseButton.IsEnabled = isRunning;
        PauseButton.Text = _autoRollPaused ? "Play" : "Pause";

        AddImageButton.IsEnabled = canUseOtherActions;
        LoadDataButton.IsEnabled = canUseOtherActions;
        GenerateDataButton.IsEnabled = canUseOtherActions;
        IncrementDataButton.IsEnabled = canUseOtherActions;
        EditButton.IsEnabled = canUseOtherActions;
        ClearAreaButton.IsEnabled = canUseOtherActions;
        ZoomInButton.IsEnabled = canUseOtherActions;
        ZoomOutButton.IsEnabled = canUseOtherActions;
        ResetZoomButton.IsEnabled = canUseOtherActions && Math.Abs(_imageScale - 1) > .001;
        RollButton.IsEnabled = canUseOtherActions && cardsOnHand > 0;
        FlashButton.IsEnabled = canUseOtherActions && cardsOnHand > 0;
        FlashAllButton.IsEnabled = canUseOtherActions && cardsOnHand > 0;
        SelectAllButton.IsEnabled = canUseOtherActions && hasDiscardedCards;
        ReturnButton.IsEnabled = canUseOtherActions && hasDiscardedCards && hasSelection;
        DeleteButton.IsEnabled = canUseOtherActions && hasDiscardedCards;
        LocateButton.IsEnabled = canUseOtherActions && _generatedFilePath is not null;

        UpdateAutoRollButtonColors(isRunning, hasDiscardedCards);
    }

    private void UpdateAutoRollButtonColors(bool isRunning, bool hasDiscardedCards)
    {
        var green = (Color)Application.Current!.Resources["Primary"];
        var gray = (Color)Application.Current.Resources["Gray600"];
        var inactiveColor = isRunning ? gray : green;

        foreach (var button in new[]
        {
            AutoRollButton,
            AddImageButton,
            LoadDataButton,
            GenerateDataButton,
            IncrementDataButton,
            EditButton,
            ClearAreaButton,
            ZoomInButton,
            ZoomOutButton,
            ResetZoomButton,
            RollButton,
            FlashButton,
            FlashAllButton,
            SelectAllButton,
            ReturnButton,
            LocateButton,
            PreviousButton,
            NextButton
        })
        {
            button.BackgroundColor = inactiveColor;
        }

        StopButton.BackgroundColor = isRunning ? green : gray;
        PauseButton.BackgroundColor = isRunning ? green : gray;
        MaximizeButton.BackgroundColor = green;
        MinimizeButton.BackgroundColor = green;
        ResetZoomButton.BackgroundColor = ResetZoomButton.IsEnabled && !isRunning ? green : gray;
        SelectAllButton.BackgroundColor = SelectAllButton.IsEnabled && !isRunning ? green : gray;
        ReturnButton.BackgroundColor = ReturnButton.IsEnabled && !isRunning ? green : gray;
        DeleteButton.BackgroundColor = isRunning
            ? gray
            : hasDiscardedCards
                ? Color.FromArgb("#B71C1C")
                : gray;
    }

    private static bool TryParsePositiveSeconds(string input, out double seconds)
    {
        return (double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out seconds) ||
                double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds)) &&
               double.IsFinite(seconds) && seconds > 0;
    }

    private void OnDiscardSelectionChanged(object? sender, SelectionChangedEventArgs e) => UpdateDiscardActions();

    private void OnSelectAllClicked(object? sender, EventArgs e)
    {
        var allSelected = DiscardedCards.Count > 0 &&
                          DiscardCollection.SelectedItems?.Count == DiscardedCards.Count;
        DiscardCollection.SelectedItems = allSelected
            ? new ObservableCollection<object>()
            : new ObservableCollection<object>(DiscardedCards.Cast<object>());
        UpdateDiscardActions();
    }

    private void ScrollDiscardCollectionToEnd()
    {
        if (DiscardedCards.Count > 0)
            DiscardCollection.ScrollTo(DiscardedCards.Count - 1, position: ScrollToPosition.End, animate: true);
    }

    private void UpdateDiscardActions()
    {
        var hasSelection = DiscardCollection.SelectedItems?.Count > 0;
        var hasDiscardedCards = DiscardedCards.Count > 0;
        SelectAllButton.IsEnabled = hasDiscardedCards;
        ReturnButton.IsEnabled = hasDiscardedCards && hasSelection;
        DeleteButton.IsEnabled = hasDiscardedCards;
        DeleteButton.BackgroundColor = hasDiscardedCards
            ? Color.FromArgb("#B71C1C")
            : (Color)Application.Current!.Resources["Gray600"];
        var cardsOnHand = Cards.Except(DiscardedCards).Count();
        RollButton.IsEnabled = cardsOnHand > 0;
        FlashButton.IsEnabled = cardsOnHand > 0;
        FlashAllButton.IsEnabled = cardsOnHand > 0;
        CardCountLabel.Text = $"{cardsOnHand} cards on hand";
        DiscardCountLabel.Text = $"{DiscardedCards.Count} discarded";
        UpdateAutoRollActions();
    }

    private Task ShowNoCardsWarningAsync() =>
        DisplayAlert("No cards available", "There are no more cards to flash or roll.", "OK");

    private async void OnReturnClicked(object? sender, EventArgs e)
    {
        foreach (var card in DiscardCollection.SelectedItems.Cast<FlashCard>().ToList())
            DiscardedCards.Remove(card);

        DiscardCollection.SelectedItems = new ObservableCollection<object>();
        _currentCard ??= Cards.Except(DiscardedCards).FirstOrDefault();
        RefreshFlashArea();
        UpdateDiscardActions();
        await SaveStateAsync();
    }

    private async void OnDeleteClicked(object? sender, EventArgs e)
    {
        var selectedCards = DiscardCollection.SelectedItems.Cast<FlashCard>().ToList();
        if (selectedCards.Count == 0)
        {
            await DisplayAlert("Delete", "Select at least one discarded card before deleting.", "OK");
            return;
        }

        var confirmed = await DisplayAlert(
            "Delete selected images",
            "Are you sure you want to delete the selected images?",
            "Delete",
            "Cancel");
        if (!confirmed)
            return;

        foreach (var card in selectedCards)
        {
            DiscardedCards.Remove(card);
            Cards.Remove(card);
        }

        DiscardCollection.SelectedItems = new ObservableCollection<object>();
        if (!Cards.Contains(_currentCard))
            _currentCard = Cards.Except(DiscardedCards).FirstOrDefault();
        RefreshFlashArea();
        UpdateDiscardActions();
        await SaveStateAsync();
    }

    private void OnMaximizeClicked(object? sender, EventArgs e)
    {
        _isMaximized = !_isMaximized;
        ApplyMaximizeState();
    }

    private void OnPageScrollSizeChanged(object? sender, EventArgs e)
    {
        if (_isMaximized)
            ApplyMaximizeState();
    }

    private async void OnMinimizeClicked(object? sender, EventArgs e)
    {
        _isMaximized = false;
        Workspace.HeightRequest = -1;
        Grid.SetRow(FlashCardBorder, 3);
        for (var i = 0; i < Workspace.RowDefinitions.Count; i++)
            Workspace.RowDefinitions[i].Height = i == 3 ? new GridLength(NormalFlashAreaHeight) : GridLength.Auto;

        Workspace.Padding = new Thickness(12);
        TopControlPanel.IsVisible = true;
        DataManagementPanel.IsVisible = true;
        FlashAreaHeader.IsVisible = true;
        DiscardSection.IsVisible = true;
        GeneratedFileRow.IsVisible = true;
        PageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Default;
        MaximizeButton.IsVisible = true;
        MinimizeButton.IsVisible = false;
        Workspace.InvalidateMeasure();
        await PageScroll.ScrollToAsync(0, 0, false);
    }

    private void ApplyMaximizeState()
    {
        var flashAreaHeight = Math.Max(
            PageScroll.Height - Workspace.Padding.VerticalThickness - MaximizedBottomReserve,
            NormalFlashAreaHeight);
        for (var i = 0; i < Workspace.RowDefinitions.Count; i++)
            Workspace.RowDefinitions[i].Height = _isMaximized && i != 3 ? new GridLength(0) : GridLength.Auto;
        Workspace.Padding = _isMaximized ? new Thickness(12, 0, 12, 12) : new Thickness(12);
        var maximizedHeight = flashAreaHeight + MaximizedScrollExtension;
        Workspace.HeightRequest = _isMaximized ? maximizedHeight : -1;
        Grid.SetRow(FlashCardBorder, _isMaximized ? 0 : 3);
        Workspace.RowDefinitions[0].Height = _isMaximized ? new GridLength(maximizedHeight) : GridLength.Auto;
        Workspace.RowDefinitions[3].Height = _isMaximized ? new GridLength(0) : new GridLength(NormalFlashAreaHeight);
        TopControlPanel.IsVisible = !_isMaximized;
        DataManagementPanel.IsVisible = !_isMaximized;
        FlashAreaHeader.IsVisible = !_isMaximized;
        DiscardSection.IsVisible = !_isMaximized;
        GeneratedFileRow.IsVisible = !_isMaximized;
        PageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Default;
        MaximizeButton.IsVisible = !_isMaximized;
        MinimizeButton.IsVisible = _isMaximized;
        Workspace.InvalidateMeasure();
        if (_isMaximized)
            _ = PageScroll.ScrollToAsync(0, 0, false);
    }

    private async void OnLocateClicked(object? sender, EventArgs e)
    {
        var generatedPath = _generatedFilePath;
        if (string.IsNullOrWhiteSpace(generatedPath))
            return;

        if (!File.Exists(generatedPath))
        {
            await DisplayAlert("Generated file", "The generated file is no longer available.", "OK");
            return;
        }

        try
        {
#if WINDOWS
#pragma warning disable CA1416
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{generatedPath}\"") { UseShellExecute = true });
#pragma warning restore CA1416
#elif ANDROID
            var opened = await Launcher.Default.OpenAsync(
                new OpenFileRequest("Open generated flashcards", new ReadOnlyFile(generatedPath)));
            if (!opened)
                await ShareGeneratedFileAsync(generatedPath);
#else
            var folder = Path.GetDirectoryName(generatedPath);
            if (folder is not null)
                await Launcher.Default.OpenAsync(new Uri($"file://{folder.Replace("\\", "/")}"));
#endif
        }
        catch (Exception ex)
        {
            try
            {
                await ShareGeneratedFileAsync(generatedPath);
            }
            catch (Exception shareException)
            {
                await DisplayAlert("Generated file", $"Unable to open or share the file: {shareException.Message}", "OK");
            }
        }
    }

    private static Task ShareGeneratedFileAsync(string filePath) =>
        Share.Default.RequestAsync(new ShareFileRequest("Generated flashcards", new ShareFile(filePath)));

    private async void OnCopyPathClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_generatedFilePath))
            return;

        try
        {
            await Clipboard.Default.SetTextAsync(_generatedFilePath);
            StatusLabel.Text = "File path copied to clipboard";
        }
        catch (Exception ex)
        {
            await DisplayAlert("Copy path", $"Unable to copy the file path: {ex.Message}", "OK");
        }
    }

    private void RefreshFlashArea()
    {
        var card = CurrentCard;
        if (card is null)
        {
            CardNameLabel.Text = "No cards loaded";
            FlashImage.Source = "dotnet_bot.png";
            FlashCardBorder.Stroke = (Color)Application.Current!.Resources["Primary"];
            UpdateDiscardActions();
            return;
        }

        var hasBack = !string.IsNullOrWhiteSpace(card.BackImage);
        FlashCardBorder.Stroke = hasBack ? (Color)Application.Current!.Resources["Primary"] : Colors.Red;
        CardNameLabel.Text = _showingBack ? $"{GetBackName(card)} (back)" : card.Name;
        FlashImage.Source = _showingBack && hasBack ? card.BackImage : card.FrontImage;
        FlashImage.Scale = _imageScale;
        UpdateDiscardActions();
    }

    private static string GetBackName(FlashCard card) =>
        string.IsNullOrWhiteSpace(card.BackName)
            ? Path.GetFileNameWithoutExtension(card.BackImage)
            : card.BackName;

    private static void ValidateCards(IEnumerable<FlashCard> cards)
    {
        foreach (var card in cards)
        {
            if (string.IsNullOrWhiteSpace(card.Name) || string.IsNullOrWhiteSpace(card.FrontImage))
                throw new InvalidDataException("Every card requires a name and front image.");
        }
    }

    private async Task<FileResult?> PickFileAsync(string title, string fileType = "image/*")
    {
        try
        {
            var isJson = fileType.Equals(".json", StringComparison.OrdinalIgnoreCase);
            var isPackage = fileType.Equals(".flashcards", StringComparison.OrdinalIgnoreCase);
            var pickerType = isJson ?
                new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.Android] = new[] { "application/json" },
                    [DevicePlatform.iOS] = new[] { "public.json" },
                    [DevicePlatform.MacCatalyst] = new[] { "public.json" },
                    [DevicePlatform.WinUI] = new[] { ".json" }
                }) : isPackage ?
                new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.Android] = new[] { "application/zip", "application/octet-stream" },
                    [DevicePlatform.iOS] = new[] { "public.archive" },
                    [DevicePlatform.MacCatalyst] = new[] { "public.archive" },
                    [DevicePlatform.WinUI] = new[] { ".flashcards", ".zip" }
                }) : FilePickerFileType.Images;
            var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = title, FileTypes = pickerType });
            if (!isJson && !isPackage && file is not null && !IsImageFile(file.FileName))
            {
                await DisplayAlert("File selection", "Please select an image file.", "OK");
                return null;
            }

            return file;
        }
        catch (Exception ex)
        {
            await DisplayAlert("File selection", ex.Message, "OK");
            return null;
        }
    }

    private static async Task<string> CopyToAppDataAsync(FileResult file)
    {
        var extension = Path.GetExtension(file.FileName);
        var path = Path.Combine(FileSystem.Current.AppDataDirectory, $"{Guid.NewGuid():N}{extension}");
        await using var source = await file.OpenReadAsync();
        await using var destination = File.Create(path);
        await source.CopyToAsync(destination);
        return path;
    }

    private static async Task<string> CreatePackageAsync(IEnumerable<FlashCard> cards)
    {
        var packagePath = Path.Combine(FileSystem.Current.CacheDirectory, $"{Guid.NewGuid():N}.flashcards");
        var packageCards = new List<FlashCardPackageCard>();

        await using (var packageStream = File.Create(packagePath))
        using (var archive = new ZipArchive(packageStream, ZipArchiveMode.Create))
        {
            var index = 0;
            foreach (var card in cards)
            {
                var frontEntry = $"images/{index}-front{Path.GetExtension(card.FrontImage)}";
                await AddFileToArchiveAsync(archive, card.FrontImage, frontEntry);

                var backEntry = string.Empty;
                if (!string.IsNullOrWhiteSpace(card.BackImage))
                {
                    backEntry = $"images/{index}-back{Path.GetExtension(card.BackImage)}";
                    await AddFileToArchiveAsync(archive, card.BackImage, backEntry);
                }

                packageCards.Add(new FlashCardPackageCard
                {
                    Name = card.Name,
                    FrontImage = frontEntry,
                    BackImage = backEntry,
                    BackName = card.BackName
                });
                index++;
            }

            var manifest = new FlashCardPackageDocument { Cards = packageCards };
            var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
            await using var manifestStream = manifestEntry.Open();
            await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions);
        }

        return packagePath;
    }

    private static async Task AddFileToArchiveAsync(ZipArchive archive, string filePath, string entryName)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Fastest);
        await using var source = File.OpenRead(filePath);
        await using var destination = entry.Open();
        await source.CopyToAsync(destination);
    }

    private static async Task<List<FlashCard>> ReadCardsAsync(FileResult file)
    {
        if (IsPackageFile(file.FileName))
            return await ReadPackageCardsAsync(file);

        await using var stream = await file.OpenReadAsync();
        var document = await JsonSerializer.DeserializeAsync<FlashCardDocument>(stream, JsonOptions);
        var cards = new List<FlashCard>();
        foreach (var transferCard in document?.Cards ?? new List<FlashCardTransfer>())
        {
            cards.Add(new FlashCard
            {
                Name = transferCard.Name,
                FrontImage = await MaterializeImageAsync(transferCard.FrontImageData, transferCard.FrontImage),
                BackImage = await MaterializeImageAsync(transferCard.BackImageData, transferCard.BackImage),
                BackName = transferCard.BackName
            });
        }

        ValidateCards(cards);
        return cards;
    }

    private static async Task<List<FlashCard>> ReadPackageCardsAsync(FileResult file)
    {
        var temporaryPath = Path.Combine(FileSystem.Current.CacheDirectory, $"{Guid.NewGuid():N}.flashcards");
        try
        {
            await using (var source = await file.OpenReadAsync())
            await using (var destination = File.Create(temporaryPath))
                await source.CopyToAsync(destination);

            using var archive = ZipFile.OpenRead(temporaryPath);
            var manifestEntry = archive.GetEntry("manifest.json")
                ?? throw new InvalidDataException("The flashcard package does not contain a manifest.");

            await using var manifestStream = manifestEntry.Open();
            var manifest = await JsonSerializer.DeserializeAsync<FlashCardPackageDocument>(manifestStream, JsonOptions)
                ?? throw new InvalidDataException("The flashcard package manifest is invalid.");

            var cards = new List<FlashCard>();
            foreach (var packageCard in manifest.Cards)
            {
                cards.Add(new FlashCard
                {
                    Name = packageCard.Name,
                    FrontImage = await ExtractImageAsync(archive, packageCard.FrontImage),
                    BackImage = await ExtractImageAsync(archive, packageCard.BackImage),
                    BackName = packageCard.BackName
                });
            }

            ValidateCards(cards);
            return cards;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static async Task<string> ExtractImageAsync(ZipArchive archive, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
            return string.Empty;

        if (Path.IsPathRooted(entryName) || entryName.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("The flashcard package contains an invalid image path.");

        var entry = archive.GetEntry(entryName)
            ?? throw new InvalidDataException($"The flashcard package is missing image '{entryName}'.");
        var extension = Path.GetExtension(entryName);
        var imagePath = Path.Combine(FileSystem.Current.AppDataDirectory, $"{Guid.NewGuid():N}{extension}");

        await using var source = entry.Open();
        await using var destination = File.Create(imagePath);
        await source.CopyToAsync(destination);
        return imagePath;
    }

    private static bool IsPackageFile(string fileName) =>
        string.Equals(Path.GetExtension(fileName), ".flashcards", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> MaterializeImageAsync(string imageData, string originalPath)
    {
        if (string.IsNullOrWhiteSpace(imageData))
            return originalPath;

        byte[] imageBytes;
        try
        {
            imageBytes = Convert.FromBase64String(imageData);
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("The JSON contains invalid image data.", ex);
        }

        var extension = Path.GetExtension(originalPath);
        if (string.IsNullOrWhiteSpace(extension))
            extension = ".img";

        var imagePath = Path.Combine(FileSystem.Current.AppDataDirectory, $"{Guid.NewGuid():N}{extension}");
        await File.WriteAllBytesAsync(imagePath, imageBytes);
        return imagePath;
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        try
        {
            BusyIndicator.IsVisible = BusyIndicator.IsRunning = true;
            await action();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Validation failed";
            await DisplayAlert("Operation failed", ex.Message, "OK");
        }
        finally
        {
            BusyIndicator.IsVisible = BusyIndicator.IsRunning = false;
        }
    }

    private async Task LoadSavedStateAsync()
    {
        if (!File.Exists(SavedStatePath))
            return;

        try
        {
            await using var stream = File.OpenRead(SavedStatePath);
            var state = await JsonSerializer.DeserializeAsync<FlashCardsState>(stream, JsonOptions);
            if (state is null || state.Cards.Count == 0)
                return;

            Cards.Clear();
            DiscardedCards.Clear();
            foreach (var card in state.Cards)
                Cards.Add(card);

            foreach (var index in state.DiscardedCardIndices.Distinct().Where(index => index >= 0 && index < Cards.Count))
                DiscardedCards.Add(Cards[index]);

            _currentCard = state.CurrentCardIndex is int currentIndex && currentIndex >= 0 && currentIndex < Cards.Count
                ? Cards[currentIndex]
                : Cards.Except(DiscardedCards).FirstOrDefault();
            _showingBack = state.ShowingBack && _currentCard is not null;
            RefreshFlashArea();
            StatusLabel.Text = $"Restored {Cards.Count} cards";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to restore saved flashcards: {ex}");
            StatusLabel.Text = "Saved data could not be restored";
        }
    }

    private async Task SaveStateAsync()
    {
        try
        {
            var state = new FlashCardsState
            {
                Cards = Cards.ToList(),
                DiscardedCardIndices = DiscardedCards
                    .Select(card => Cards.IndexOf(card))
                    .Where(index => index >= 0)
                    .ToList(),
                CurrentCardIndex = _currentCard is null ? null : Cards.IndexOf(_currentCard),
                ShowingBack = _showingBack
            };

            var temporaryPath = $"{SavedStatePath}.tmp";
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions);

            File.Move(temporaryPath, SavedStatePath, true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to save flashcards: {ex}");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static bool IsImageFile(string fileName) =>
        new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".heic", ".heif" }
            .Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);
}

internal sealed class NewCardDraft
{
    public string FrontPath { get; set; } = string.Empty;
    public string FrontName { get; set; } = string.Empty;
    public string BackPath { get; set; } = string.Empty;
    public string BackName { get; set; } = string.Empty;
}

public sealed class FlashCard
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("frontImage")]
    public string FrontImage { get; set; } = string.Empty;

    [JsonPropertyName("backImage")]
    public string BackImage { get; set; } = string.Empty;

    [JsonPropertyName("backName")]
    public string BackName { get; set; } = string.Empty;
}

public sealed class FlashCardDocument
{
    [JsonPropertyName("cards")]
    public List<FlashCardTransfer> Cards { get; set; } = new();
}

public sealed class FlashCardPackageDocument
{
    [JsonPropertyName("cards")]
    public List<FlashCardPackageCard> Cards { get; set; } = new();
}

public sealed class FlashCardPackageCard
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("frontImage")]
    public string FrontImage { get; set; } = string.Empty;

    [JsonPropertyName("backImage")]
    public string BackImage { get; set; } = string.Empty;

    [JsonPropertyName("backName")]
    public string BackName { get; set; } = string.Empty;
}

public sealed class FlashCardTransfer
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("frontImage")]
    public string FrontImage { get; set; } = string.Empty;

    [JsonPropertyName("frontImageData")]
    public string FrontImageData { get; set; } = string.Empty;

    [JsonPropertyName("backImage")]
    public string BackImage { get; set; } = string.Empty;

    [JsonPropertyName("backImageData")]
    public string BackImageData { get; set; } = string.Empty;

    [JsonPropertyName("backName")]
    public string BackName { get; set; } = string.Empty;
}

internal sealed class FlashCardsState
{
    public List<FlashCard> Cards { get; set; } = new();
    public List<int> DiscardedCardIndices { get; set; } = new();
    public int? CurrentCardIndex { get; set; }
    public bool ShowingBack { get; set; }
}