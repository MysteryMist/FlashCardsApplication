using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Maui.Storage;

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
    private bool _stateLoaded;
    private const double NormalFlashAreaHeight = 400;
    private const double MaximizedBottomReserve = 96;

    private static string SavedStatePath => Path.Combine(FileSystem.Current.AppDataDirectory, "flashcards-state.json");

    public ObservableCollection<FlashCard> Cards { get; } = new();
    public ObservableCollection<FlashCard> DiscardedCards { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        BindingContext = this;
        UpdateDiscardActions();
        RefreshFlashArea();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_stateLoaded)
            return;

        _stateLoaded = true;
        await LoadSavedStateAsync();
    }

    private FlashCard? CurrentCard => _currentCard;

    protected override void OnDisappearing()
    {
        StopAutoRoll();
        base.OnDisappearing();
    }

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
        var file = await PickFileAsync("Select a flashcard JSON file", ".json");
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
        var file = await PickFileAsync("Select JSON cards to merge", ".json");
        if (file is null)
            return;

        await RunBusyAsync(async () =>
        {
            var imported = await ReadCardsAsync(file);
            var newCards = imported
                .Where(card => !Cards.Any(existing => existing.Name.Equals(card.Name, StringComparison.OrdinalIgnoreCase)))
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

            var payload = new FlashCardDocument { Cards = Cards.ToList() };
            var json = JsonSerializer.Serialize(payload, JsonOptions);
            await using var jsonStream = new MemoryStream(Encoding.UTF8.GetBytes(json));
            var fileName = $"flashcards-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            var saveResult = await FileSaver.Default.SaveAsync(fileName, jsonStream);

            if (!saveResult.IsSuccessful)
            {
                if (saveResult.Exception is not null)
                    throw saveResult.Exception;

                StatusLabel.Text = "Export canceled";
                return;
            }

            _generatedFilePath = saveResult.FilePath;
            GeneratedPathLabel.Text = _generatedFilePath;
            LocateButton.IsEnabled = !string.IsNullOrWhiteSpace(_generatedFilePath);
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

    private async void OnPreviousClicked(object? sender, EventArgs e) => await NavigateCardAsync(-1);
    private async void OnNextClicked(object? sender, EventArgs e) => await NavigateCardAsync(1);

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
            DiscardedCards.Insert(0, card);
        }

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
            DiscardedCards.Insert(0, CurrentCard);

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
        DiscardCollection.SelectedItems = new ObservableCollection<object>(DiscardedCards.Cast<object>());
        UpdateDiscardActions();
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
        for (var i = 0; i < Workspace.RowDefinitions.Count; i++)
            Workspace.RowDefinitions[i].Height = i == 3 ? new GridLength(NormalFlashAreaHeight) : GridLength.Auto;

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
        Workspace.HeightRequest = _isMaximized ? flashAreaHeight : -1;
        Workspace.RowDefinitions[3].Height = _isMaximized ? new GridLength(flashAreaHeight) : new GridLength(NormalFlashAreaHeight);
        MaximizeButton.IsVisible = !_isMaximized;
        MinimizeButton.IsVisible = _isMaximized;
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
            var pickerType = isJson ?
                new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.Android] = new[] { "application/json" },
                    [DevicePlatform.iOS] = new[] { "public.json" },
                    [DevicePlatform.MacCatalyst] = new[] { "public.json" },
                    [DevicePlatform.WinUI] = new[] { ".json" }
                }) : FilePickerFileType.Images;
            var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = title, FileTypes = pickerType });
            if (!isJson && file is not null && !IsImageFile(file.FileName))
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

    private static async Task<List<FlashCard>> ReadCardsAsync(FileResult file)
    {
        await using var stream = await file.OpenReadAsync();
        var document = await JsonSerializer.DeserializeAsync<FlashCardDocument>(stream, JsonOptions);
        var cards = document?.Cards ?? new List<FlashCard>();
        ValidateCards(cards);
        return cards;
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
    public List<FlashCard> Cards { get; set; } = new();
}

internal sealed class FlashCardsState
{
    public List<FlashCard> Cards { get; set; } = new();
    public List<int> DiscardedCardIndices { get; set; } = new();
    public int? CurrentCardIndex { get; set; }
    public bool ShowingBack { get; set; }
}