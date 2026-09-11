using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FlashCardsApp;

public partial class MainPage : ContentPage
{
    private readonly Random _random = new();
    private FlashCard? _currentCard;
    private bool _showingBack;
    private bool _isMaximized;
    private double _imageScale = 1;
    private string? _generatedFilePath;

    public ObservableCollection<FlashCard> Cards { get; } = new();
    public ObservableCollection<FlashCard> DiscardedCards { get; } = new();

    public MainPage()
    {
        InitializeComponent();
        BindingContext = this;
        UpdateDiscardActions();
        RefreshFlashArea();
    }

    private FlashCard? CurrentCard => _currentCard;

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
            StatusLabel.Text = $"Merged {imported.Count} cards";
        });
    }

    private async void OnGenerateDataClicked(object? sender, EventArgs e)
    {
        if (!Cards.Any())
        {
            await DisplayAlert("Generate data", "Add or load at least one card first.", "OK");
            return;
        }

        await RunBusyAsync(async () =>
        {
            ValidateCards(Cards);
            var generateConfirmed = await DisplayAlert(
                "Generate data",
                $"Validated {Cards.Count} cards. Generate the JSON file now?",
                "Yes",
                "No");
            if (!generateConfirmed)
                return;

            _generatedFilePath = Path.Combine(FileSystem.Current.AppDataDirectory, $"flashcards-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            var payload = new FlashCardDocument { Cards = Cards.ToList() };
            await File.WriteAllTextAsync(_generatedFilePath, JsonSerializer.Serialize(payload, JsonOptions));
            GeneratedPathLabel.Text = _generatedFilePath;
            LocateButton.IsEnabled = true;
            StatusLabel.Text = "Data generated and validated";
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
        if (CurrentCard is null)
            return;

        var angle = _showingBack ? -90 : 90;
        await FlashImage.RotateYTo(angle, 140, Easing.CubicInOut);
        _showingBack = !_showingBack;
        RefreshFlashArea();
        await FlashImage.RotateYTo(0, 140, Easing.CubicInOut);
    }

    private void OnZoomInClicked(object? sender, EventArgs e) => SetZoom(Math.Min(_imageScale + .1, 2));
    private void OnZoomOutClicked(object? sender, EventArgs e) => SetZoom(Math.Max(_imageScale - .1, .6));
    private void OnResetZoomClicked(object? sender, EventArgs e) => SetZoom(1);

    private void SetZoom(double scale)
    {
        _imageScale = scale;
        FlashImage.Scale = scale;
        ResetZoomButton.IsEnabled = Math.Abs(scale - 1) > .001;
    }

    private void OnRollClicked(object? sender, EventArgs e)
    {
        var available = Cards.Except(DiscardedCards).ToList();
        if (available.Count == 0)
            return;

        _currentCard = available[_random.Next(available.Count)];
        _showingBack = false;
        RefreshFlashArea();
    }

    private async void OnFlashClicked(object? sender, EventArgs e)
    {
        var available = Cards.Except(DiscardedCards).ToList();
        if (available.Count == 0)
            return;

        if (CurrentCard is null || DiscardedCards.Contains(CurrentCard))
        {
            _currentCard = available[_random.Next(available.Count)];
            _showingBack = false;
            RefreshFlashArea();
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
    }

    private async Task MoveCurrentToDiscardAsync()
    {
        if (CurrentCard is null)
            return;

        if (!DiscardedCards.Contains(CurrentCard))
            DiscardedCards.Insert(0, CurrentCard);

        var available = Cards.Except(DiscardedCards).ToList();
        _currentCard = available.Count == 0 ? null : available[_random.Next(available.Count)];
        _showingBack = false;
        RefreshFlashArea();
        UpdateDiscardActions();
        await Task.CompletedTask;
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
        CardCountLabel.Text = $"{Cards.Except(DiscardedCards).Count()} cards on hand";
        DiscardCountLabel.Text = $"{DiscardedCards.Count} discarded";
    }

    private void OnReturnClicked(object? sender, EventArgs e)
    {
        foreach (var card in DiscardCollection.SelectedItems.Cast<FlashCard>().ToList())
            DiscardedCards.Remove(card);

        DiscardCollection.SelectedItems = new ObservableCollection<object>();
        _currentCard ??= Cards.Except(DiscardedCards).FirstOrDefault();
        RefreshFlashArea();
        UpdateDiscardActions();
    }

    private async void OnDeleteClicked(object? sender, EventArgs e)
    {
        var selectedCards = DiscardCollection.SelectedItems.Cast<FlashCard>().ToList();
        if (selectedCards.Count == 0)
        {
            await DisplayAlert("Delete", "Select at least one discarded card before deleting.", "OK");
            return;
        }

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
    }

    private void OnMaximizeClicked(object? sender, EventArgs e)
    {
        _isMaximized = !_isMaximized;
        ApplyMaximizeState();
    }

    private async void OnMinimizeClicked(object? sender, EventArgs e)
    {
        _isMaximized = false;
        for (var i = 0; i < Workspace.RowDefinitions.Count; i++)
            Workspace.RowDefinitions[i].Height = i == 3 ? new GridLength(500) : GridLength.Auto;

        MaximizeButton.IsVisible = true;
        MinimizeButton.IsVisible = false;
        Workspace.InvalidateMeasure();
        await PageScroll.ScrollToAsync(0, 0, false);
    }

    private void ApplyMaximizeState()
    {
        for (var i = 0; i < Workspace.RowDefinitions.Count; i++)
            Workspace.RowDefinitions[i].Height = _isMaximized && i != 3 ? new GridLength(0) : GridLength.Auto;
        Workspace.RowDefinitions[3].Height = _isMaximized ? new GridLength(1, GridUnitType.Star) : new GridLength(500);
        MaximizeButton.IsVisible = !_isMaximized;
        MinimizeButton.IsVisible = _isMaximized;
    }

    private async void OnLocateClicked(object? sender, EventArgs e)
    {
        if (_generatedFilePath is null)
            return;

        try
        {
#if WINDOWS
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_generatedFilePath}\"") { UseShellExecute = true });
#else
            var folder = Path.GetDirectoryName(_generatedFilePath);
            if (folder is not null)
                await Launcher.Default.OpenAsync(new Uri($"file://{folder.Replace("\\", "/")}"));
#endif
        }
        catch
        {
            await Share.Default.RequestAsync(new ShareFileRequest("Generated flashcards", new ShareFile(_generatedFilePath)));
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