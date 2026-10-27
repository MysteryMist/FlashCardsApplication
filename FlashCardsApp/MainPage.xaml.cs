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
    private readonly UpdateService _updateService = new(
        "https://api.github.com/repos/MysteryMist/FlashCardsApplication/releases/latest",
        "https://github.com/MysteryMist/FlashCardsApplication/releases");
    private CancellationTokenSource? _updateCheckCancellation;
    private bool _updateCheckInProgress;
    private bool _updateAvailable;
    private string? _updateReleaseUrl;
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
    private double _imageAspectRatio = 4d / 3d;
    private double? _normalImageFrameWidth;
    private double? _normalImageFrameHeight;
    private double _panStartTranslationX;
    private double _panStartTranslationY;
    private double _pinchStartScale = 1;
    private CancellationTokenSource? _wheelResetCancellation;
#if ANDROID
    private CancellationTokenSource? _androidPanHoldCancellation;
    private bool _androidPanActive;
    private bool _androidPinchActive;
    private DateTime _androidSuppressTapUntil;
#endif
#if WINDOWS
    private Microsoft.UI.Xaml.FrameworkElement? _windowsNativeView;
    private KeyEventHandler? _windowsKeyHandler;
    private Microsoft.UI.Xaml.FrameworkElement? _windowsImageView;
    private PointerEventHandler? _windowsPointerWheelHandler;
#endif
    private const double NormalFlashAreaHeight = 560;
    private const double AndroidMaximizedFlashAreaHeight = 560;
#if ANDROID
    private const double MaximizedBottomReserve = 96;
    private const double MaximizedScrollExtension = 0;
    private const double PanSensitivity = 1.15;
    private const int AndroidPanHoldDurationMilliseconds = 1500;
#else
    private const double MaximizedBottomReserve = 96;
    private const double MaximizedScrollExtension = 0;
    private const double PanSensitivity = 1;
#endif

    private static string SavedStatePath => Path.Combine(FileSystem.Current.AppDataDirectory, "flashcards-state.json");
    private static string UserDataSetsDirectory => Path.Combine(FileSystem.Current.AppDataDirectory, "DataSets");
    private static string HiddenBuiltInDataSetsPath => Path.Combine(FileSystem.Current.AppDataDirectory, "hidden-built-in-datasets.json");

    public ObservableCollection<FlashCard> Cards { get; } = new();
    public ObservableCollection<FlashCard> DiscardedCards { get; } = new();
    public string AppVersionText => $"Version {AppInfo.Current.VersionString}{(string.IsNullOrWhiteSpace(AppInfo.Current.BuildString) ? string.Empty : $" (Build {AppInfo.Current.BuildString})")}";

    public MainPage()
    {
        InitializeComponent();
        HandlerChanged += OnMainPageHandlerChanged;
        if (FindByName("CopyPathButton") is Button copyPathButton)
            copyPathButton.Clicked += OnCopyPathClicked;
        BindingContext = this;
        ApplyImageLayout();
        UpdateDiscardActions();
        RefreshFlashArea();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        AttachWindowsKeyboard();
        AttachWindowsPointerWheel();
        if (_stateLoaded)
            return;

        _stateLoaded = true;
        await LoadSavedStateAsync();
        _ = Task.Run(CheckForUpdateInBackgroundAsync);
    }

    private FlashCard? CurrentCard => _currentCard;

    protected override void OnDisappearing()
    {
#if WINDOWS
        DetachWindowsKeyboard();
        DetachWindowsPointerWheel();
#endif
        _wheelResetCancellation?.Cancel();
#if ANDROID
        _androidPanHoldCancellation?.Cancel();
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

    private void OnMainPageHandlerChanged(object? sender, EventArgs e)
    {
        AttachWindowsKeyboard();
        AttachWindowsPointerWheel();
    }

    private void ApplyImageLayout()
    {
        var isDesktop = DeviceInfo.Current.Platform == DevicePlatform.WinUI || DeviceInfo.Current.Platform == DevicePlatform.MacCatalyst;
        var availableWidth = FlashCardBorder.Width > 0
            ? Math.Max(200, FlashCardBorder.Width - 132)
            : 520;
        var availableHeight = FlashCardBorder.Height > 0
            ? Math.Max(180, FlashCardBorder.Height - 170)
            : 390;
        var normalWidth = _normalImageFrameWidth ?? (isDesktop ? 420 : 300);
        var normalHeight = _normalImageFrameHeight ?? (isDesktop ? 320 : 225);
        var maximizedWidth = Math.Min(availableWidth, normalWidth);
        var maximizedHeight = Math.Min(availableHeight, normalHeight);
        var frameWidth = maximizedWidth;
        var frameHeight = frameWidth / _imageAspectRatio;
        if (frameHeight > maximizedHeight)
        {
            frameHeight = maximizedHeight;
            frameWidth = frameHeight * _imageAspectRatio;
        }

        FlashImageFrame.HorizontalOptions = LayoutOptions.Center;
        FlashImageFrame.VerticalOptions = LayoutOptions.Center;
        FlashImageFrame.WidthRequest = _isMaximized ? frameWidth : -1;
        FlashImageFrame.HeightRequest = _isMaximized ? frameHeight : -1;
        FlashImage.WidthRequest = _isMaximized ? -1 : isDesktop ? 420 : -1;
        FlashImage.HeightRequest = _isMaximized ? -1 : isDesktop ? 320 : -1;
    }

    private void AttachWindowsPointerWheel()
    {
#if WINDOWS
        if (_windowsImageView is not null || FlashImage.Handler?.PlatformView is not Microsoft.UI.Xaml.FrameworkElement imageView)
            return;

        _windowsImageView = imageView;
        _windowsPointerWheelHandler = OnWindowsPointerWheelChanged;
        _windowsImageView.PointerWheelChanged += _windowsPointerWheelHandler;
#endif
    }

    private void DetachWindowsPointerWheel()
    {
#if WINDOWS
        if (_windowsImageView is not null && _windowsPointerWheelHandler is not null)
            _windowsImageView.PointerWheelChanged -= _windowsPointerWheelHandler;

        _windowsImageView = null;
        _windowsPointerWheelHandler = null;
#endif
    }

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
            ToggleAppWindowFullScreen();
        }
        else if (e.Key == VirtualKey.Escape && IsAppWindowFullScreen())
        {
            e.Handled = true;
            ToggleAppWindowFullScreen();
        }
    }
#endif

    private static bool IsAppWindowFullScreen()
    {
#if WINDOWS
        var window = Application.Current?.Windows.FirstOrDefault();
        if (window is null)
            return false;

        var platformWindow = window.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (platformWindow is null)
            return false;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(platformWindow);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        return appWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
#else
        return false;
#endif
    }

    private static void ToggleAppWindowFullScreen()
    {
#if WINDOWS
        var window = Application.Current?.Windows.FirstOrDefault();
        if (window is null)
            return;

        var platformWindow = window.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
        if (platformWindow is null)
            return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(platformWindow);
        var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        var isFullScreen = appWindow.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen;
        appWindow.SetPresenter(isFullScreen
            ? Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped
            : Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
#endif
    }

    private async void OnAddImageClicked(object? sender, EventArgs e)
    {
        var action = await DisplayActionSheet("Add cards", "Cancel", null, "Add one card", "Add front/back batches");
        if (action == "Add one card")
            await AddSingleImageAsync();
        else if (action == "Add front/back batches")
            await AddImageBatchAsync();
    }

    private async void OnViewDeckClicked(object? sender, EventArgs e)
    {
        if (Cards.Count == 0)
        {
            await DisplayAlert("View deck", "Add or load cards before opening the deck.", "OK");
            return;
        }

        await Navigation.PushModalAsync(new DeckPage(Cards, OnDeckCardSelected));
    }

    private async Task OnDeckCardSelected(FlashCard card)
    {
        DiscardedCards.Remove(card);
        _currentCard = card;
        _showingBack = false;
        _imageScale = 1;
        RefreshFlashArea();
        StatusLabel.Text = "Selected card from deck";
        await SaveStateAsync();
        await PageScroll.ScrollToAsync(FlashCardBorder, ScrollToPosition.Center, false);
        await JiggleImageAsync();
    }

    private async Task AddSingleImageAsync()
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
        ValidateCards(new[] { card });
        var replaceExisting = Cards.Count > 0;
        if (replaceExisting && !await ConfirmImageDataReplacementAsync(1))
            return;

        Cards.Clear();
        DiscardedCards.Clear();
        Cards.Add(card);
        _currentCard = card;
        _showingBack = false;
        RefreshFlashArea();
        await SaveStateAsync();
        await JiggleImageAsync();
        StatusLabel.Text = replaceExisting
            ? "Replaced current data with 1 card"
            : string.IsNullOrWhiteSpace(draft.BackPath) ? "Added (no back image)" : "Card added";
    }

    private async Task AddImageBatchAsync()
    {
        var frontFiles = await PickMultipleImageFilesAsync("Select the front images");
        if (frontFiles is null || frontFiles.Count == 0)
            return;

        var backFiles = await PickMultipleImageFilesAsync("Select the matching back images");
        if (backFiles is null || backFiles.Count == 0)
            return;

        frontFiles = frontFiles.OrderBy(file => file.FileName, NaturalFileNameComparer.Instance).ToList();
        backFiles = backFiles.OrderBy(file => file.FileName, NaturalFileNameComparer.Instance).ToList();
        if (frontFiles.Count != backFiles.Count)
        {
            await DisplayAlert("Add image batch", $"The front batch has {frontFiles.Count} images and the back batch has {backFiles.Count}. Select the same number of each side.", "OK");
            return;
        }

        var replaceExisting = Cards.Count > 0;
        if (replaceExisting && !await ConfirmImageDataReplacementAsync(frontFiles.Count))
            return;

        await RunBusyAsync(async () =>
        {
            var addedCards = new List<FlashCard>(frontFiles.Count);
            for (var i = 0; i < frontFiles.Count; i++)
            {
                var front = frontFiles[i];
                var back = backFiles[i];
                var name = Path.GetFileNameWithoutExtension(front.FileName);
                addedCards.Add(new FlashCard
                {
                    Name = name,
                    FrontImage = await CopyToAppDataAsync(front),
                    BackImage = await CopyToAppDataAsync(back),
                    BackName = Path.GetFileNameWithoutExtension(back.FileName)
                });
            }

            ValidateCards(addedCards);
            Cards.Clear();
            DiscardedCards.Clear();
            foreach (var card in addedCards)
                Cards.Add(card);

            _currentCard = addedCards[0];
            _showingBack = false;
            RefreshFlashArea();
            await SaveStateAsync();
            StatusLabel.Text = replaceExisting
                ? $"Replaced current data with {addedCards.Count} paired cards"
                : $"Added {addedCards.Count} paired cards";
        });
    }

    private async Task<bool> ConfirmImageDataReplacementAsync(int incomingCardCount)
    {
        var cardText = incomingCardCount == 1 ? "1 card" : $"{incomingCardCount} cards";
        return await DisplayAlert(
            "Overwrite current data?",
            $"Adding these images will replace all {Cards.Count} cards currently loaded with {cardText}. Continue?",
            "Overwrite",
            "Cancel");
    }

    private async Task<List<FileResult>?> PickMultipleImageFilesAsync(string title)
    {
        try
        {
            var files = await FilePicker.Default.PickMultipleAsync(new PickOptions
            {
                PickerTitle = title,
                FileTypes = FilePickerFileType.Images
            });
            var selectedFiles = files?.ToList();
            if (selectedFiles is not null && selectedFiles.Any(file => !IsImageFile(file.FileName)))
            {
                await DisplayAlert("File selection", "Please select only image files.", "OK");
                return null;
            }

            return selectedFiles;
        }
        catch (Exception ex)
        {
            await DisplayAlert("File selection", ex.Message, "OK");
            return null;
        }
    }

    private async void OnLoadDataClicked(object? sender, EventArgs e)
    {
        var source = await DisplayActionSheet("Load data", "Cancel", null, "From device", "Built-in or saved dataset");
        if (source == "Built-in or saved dataset")
        {
            await LoadBuiltInDataSetAsync();
            return;
        }

        if (source != "From device")
            return;

        var file = await PickFileAsync("Select a flashcard package or JSON file", "data");
        if (file is not null)
            await LoadDataAsync(() => ReadCardsAsync(file));
    }

    private async Task LoadDataAsync(Func<Task<List<FlashCard>>> readCards)
    {
        await RunBusyAsync(async () =>
        {
            var imported = await readCards();
            if (imported.Count == 0)
                throw new InvalidDataException("The selected data does not contain any valid cards.");

            ValidateCards(imported);
            if (Cards.Count > 0)
            {
                ValidateCards(Cards);
                var overwriteConfirmed = await DisplayAlert(
                    "Overwrite current data?",
                    $"Validated {imported.Count} incoming cards. Loading them will replace all {Cards.Count} current cards. Continue?",
                    "Overwrite",
                    "Cancel");
                if (!overwriteConfirmed)
                    return;
            }

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

    private async Task LoadBuiltInDataSetAsync(bool increment = false)
    {
        var operation = increment ? "Increment" : "Load";
        while (true)
        {
            List<DataSetChoice>? choices = null;
            await RunBusyAsync(async () => choices = await GetDataSetChoicesAsync());
            if (choices is null)
                return;
            if (choices.Count == 0)
            {
                await DisplayAlert($"{operation} data", "No built-in or saved datasets are available.", "OK");
                return;
            }

            var selectedName = await DisplayActionSheet($"Select a dataset to {operation.ToLowerInvariant()}", "Cancel", null, choices.Select(choice => choice.DisplayName).ToArray());
            var selectedChoice = choices.FirstOrDefault(choice => choice.DisplayName == selectedName);
            if (selectedChoice is null)
                return;

            var selectedAction = await DisplayActionSheet(
                $"Dataset: {selectedChoice.Name}",
                "Cancel",
                "Delete",
                operation);
            if (selectedAction == "Delete")
            {
                var confirmationMessage = selectedChoice.IsPackaged
                    ? $"Remove '{selectedChoice.Name}' from the dataset chooser? The built-in package remains installed."
                    : $"Delete '{selectedChoice.Name}' from this app? This cannot be undone.";
                var confirmed = await DisplayAlert(
                    selectedChoice.IsPackaged ? "Hide built-in dataset" : "Delete saved dataset",
                    confirmationMessage,
                    selectedChoice.IsPackaged ? "Hide" : "Delete",
                    "Cancel");
                if (confirmed)
                {
                    try
                    {
                        if (selectedChoice.IsPackaged)
                            await HideBuiltInDataSetAsync(selectedChoice);
                        else
                            DeleteSavedDataSet(selectedChoice);
                    }
                    catch (Exception ex)
                    {
                        await DisplayAlert("Remove dataset", ex.Message, "OK");
                    }
                }

                continue;
            }

            if (selectedAction != operation)
                return;

            if (increment)
                await IncrementDataAsync(() => ReadDataSetAsync(selectedChoice));
            else
                await LoadDataAsync(() => ReadDataSetAsync(selectedChoice));
            return;
        }
    }

    private static async Task<Stream> OpenAppPackageOrLocalFileAsync(string relativePath)
    {
        try
        {
            return await FileSystem.OpenAppPackageFileAsync(relativePath);
        }
        catch (FileNotFoundException) { }

        var pathCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar)),
            Path.Combine(AppContext.BaseDirectory, "DataSets", Path.GetFileName(relativePath)),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Resources", "DataSets", Path.GetFileName(relativePath))
        };

        foreach (var candidate in pathCandidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(candidate))
                return File.OpenRead(candidate);
        }

        throw new FileNotFoundException($"The app package file '{relativePath}' could not be found.", relativePath);
    }

    private static async Task<List<DataSetChoice>> GetDataSetChoicesAsync()
    {
        await using var catalogStream = await OpenAppPackageOrLocalFileAsync("DataSets/datasets.json");
        var catalog = await JsonSerializer.DeserializeAsync<List<BuiltInDataSet>>(catalogStream, JsonOptions)
            ?? throw new InvalidDataException("The built-in dataset catalog is invalid.");
        var hiddenBuiltInFiles = await ReadHiddenBuiltInDataSetsAsync();
        var packagedChoices = new List<DataSetChoice>();
        foreach (var dataset in catalog)
        {
            if (string.IsNullOrWhiteSpace(dataset.Name) ||
                string.IsNullOrWhiteSpace(dataset.File) ||
                Path.GetFileName(dataset.File) != dataset.File ||
                !dataset.File.EndsWith(".flashcards", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The built-in dataset catalog contains an invalid entry.");

            if (!hiddenBuiltInFiles.Contains(dataset.File))
                packagedChoices.Add(new DataSetChoice { Name = dataset.Name, File = dataset.File, IsPackaged = true });
        }

        Directory.CreateDirectory(UserDataSetsDirectory);
        var savedChoices = Directory.EnumerateFiles(UserDataSetsDirectory)
            .Where(file => Path.GetExtension(file).Equals(".flashcards", StringComparison.OrdinalIgnoreCase))
            .Select(file => new
            {
                Choice = new DataSetChoice
                {
                    Name = Path.GetFileNameWithoutExtension(file),
                    File = Path.GetFileName(file),
                    IsPackaged = false
                },
                LastWriteTime = File.GetLastWriteTime(file)
            })
            .OrderByDescending(item => item.LastWriteTime)
            .ThenBy(item => item.Choice.Name, NaturalFileNameComparer.Instance)
            .Select(item => item.Choice);

        var choices = savedChoices
            .Concat(packagedChoices.OrderByDescending(choice => choice.Name, NaturalFileNameComparer.Instance))
            .ToList();
        return choices;
    }

    private static async Task<HashSet<string>> ReadHiddenBuiltInDataSetsAsync()
    {
        if (!File.Exists(HiddenBuiltInDataSetsPath))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var stream = File.OpenRead(HiddenBuiltInDataSetsPath);
        var hiddenFiles = await JsonSerializer.DeserializeAsync<List<string>>(stream, JsonOptions) ?? new List<string>();
        return hiddenFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task HideBuiltInDataSetAsync(DataSetChoice choice)
    {
        if (!choice.IsPackaged || Path.GetFileName(choice.File) != choice.File ||
            !choice.File.EndsWith(".flashcards", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only a built-in dataset can be hidden this way.");

        var hiddenFiles = await ReadHiddenBuiltInDataSetsAsync();
        hiddenFiles.Add(choice.File);
        var temporaryPath = $"{HiddenBuiltInDataSetsPath}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
                await JsonSerializer.SerializeAsync(stream, hiddenFiles, JsonOptions);
            File.Move(temporaryPath, HiddenBuiltInDataSetsPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void DeleteSavedDataSet(DataSetChoice choice)
    {
        if (choice.IsPackaged || Path.GetFileName(choice.File) != choice.File ||
            !choice.File.EndsWith(".flashcards", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only app-saved datasets can be deleted.");

        var dataSetsDirectory = Path.GetFullPath(UserDataSetsDirectory);
        var targetPath = Path.GetFullPath(Path.Combine(dataSetsDirectory, choice.File));
        var directoryPrefix = dataSetsDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? dataSetsDirectory
            : dataSetsDirectory + Path.DirectorySeparatorChar;
        if (!targetPath.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The saved dataset path is invalid.");

        File.Delete(targetPath);
    }

    private static async Task<List<FlashCard>> ReadDataSetAsync(DataSetChoice choice)
    {
        if (choice.IsPackaged)
        {
            await using var packagedStream = await OpenAppPackageOrLocalFileAsync($"DataSets/{choice.File}");
            return await ReadPackageCardsAsync(packagedStream);
        }

        if (Path.GetFileName(choice.File) != choice.File ||
            !choice.File.EndsWith(".flashcards", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The saved dataset has an invalid package filename.");

        await using var savedStream = File.OpenRead(Path.Combine(UserDataSetsDirectory, choice.File));
        return await ReadPackageCardsAsync(savedStream);
    }

    private async void OnIncrementDataClicked(object? sender, EventArgs e)
    {
        var source = await DisplayActionSheet("Increment data", "Cancel", null, "From device", "Built-in or saved dataset");
        if (source == "Built-in or saved dataset")
        {
            await LoadBuiltInDataSetAsync(increment: true);
            return;
        }

        if (source != "From device")
            return;

        var file = await PickFileAsync("Select a flashcard package or JSON file", "data");
        if (file is null)
            return;

        await IncrementDataAsync(() => ReadCardsAsync(file));
    }

    private async Task IncrementDataAsync(Func<Task<List<FlashCard>>> readCards)
    {
        await RunBusyAsync(async () =>
        {
            var imported = await readCards();
            if (imported.Count == 0)
                throw new InvalidDataException("The selected data does not contain any valid cards.");

            ValidateCards(imported);
            if (Cards.Count > 0)
                ValidateCards(Cards);

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

            var mergedCards = Cards.Concat(newCards).ToList();
            ValidateCards(mergedCards);
            if (Cards.Count > 0)
            {
                var incrementConfirmed = await DisplayAlert(
                    "Add to current data?",
                    $"Validated {imported.Count} incoming cards. {newCards.Count} new cards will be added to the current {Cards.Count} cards. Continue?",
                    "Add",
                    "Cancel");
                if (!incrementConfirmed)
                    return;
            }

            foreach (var card in newCards)
                Cards.Add(card);

            _currentCard ??= Cards.FirstOrDefault();
            RefreshFlashArea();
            await SaveStateAsync();
            StatusLabel.Text = $"Added {newCards.Count} cards";
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

    private async void OnSaveInAppClicked(object? sender, EventArgs e)
    {
        if (Cards.Count == 0)
        {
            await DisplayAlert("Save dataset", "Add or load at least one card first.", "OK");
            return;
        }

        var enteredName = await DisplayPromptAsync(
            "Save dataset",
            "Enter a name for this dataset.",
            accept: "Continue",
            cancel: "Cancel",
            placeholder: "My flashcards",
            maxLength: 80);
        if (string.IsNullOrWhiteSpace(enteredName))
            return;

        var datasetName = SanitizeDataSetName(enteredName);
        if (string.IsNullOrWhiteSpace(datasetName))
        {
            await DisplayAlert("Save dataset", "Enter a valid dataset name.", "OK");
            return;
        }

        var targetPath = Path.Combine(UserDataSetsDirectory, $"{datasetName}.flashcards");
        if (File.Exists(targetPath) &&
            !await DisplayAlert("Replace dataset", $"A saved dataset named '{datasetName}' already exists. Replace it?", "Replace", "Cancel"))
            return;

        await RunBusyAsync(async () =>
        {
            ValidateCards(Cards);
            Directory.CreateDirectory(UserDataSetsDirectory);
            string? packagePath = null;
            var temporaryPath = Path.Combine(UserDataSetsDirectory, $"{Guid.NewGuid():N}.tmp");
            try
            {
                packagePath = await CreatePackageAsync(Cards);
                await using (var source = File.OpenRead(packagePath))
                await using (var destination = File.Create(temporaryPath))
                    await source.CopyToAsync(destination);

                File.Move(temporaryPath, targetPath, true);
                StatusLabel.Text = $"Saved dataset: {datasetName}";
            }
            finally
            {
                if (packagePath is not null && File.Exists(packagePath))
                    File.Delete(packagePath);
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
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
#if ANDROID
        if (DateTime.UtcNow < _androidSuppressTapUntil)
        {
            _androidSuppressTapUntil = DateTime.MinValue;
            return;
        }
#endif
        await AnimateFlipAsync();
    }

    private void OnFlashImagePanUpdated(object? sender, PanUpdatedEventArgs e)
    {
        switch (e.StatusType)
        {
            case GestureStatus.Started:
                _panStartTranslationX = FlashImageFrame.TranslationX;
                _panStartTranslationY = FlashImageFrame.TranslationY;
#if ANDROID
                _androidSuppressTapUntil = DateTime.MinValue;
                _androidPanActive = false;
                _androidPanHoldCancellation?.Cancel();
                _androidPanHoldCancellation = new CancellationTokenSource();
                _ = ActivateAndroidPanAsync(_androidPanHoldCancellation.Token);
#endif
                break;
            case GestureStatus.Running:
#if ANDROID
                if (!_androidPanActive || _androidPinchActive)
                    break;

                var parent = FlashImageFrame.Parent as VisualElement;
                var maxTranslationX = parent is null
                    ? 0
                    : Math.Max(0, (parent.Width - (FlashImageFrame.Width * FlashImageFrame.Scale)) / 2);
                var maxTranslationY = parent is null
                    ? 0
                    : Math.Max(0, (parent.Height - (FlashImageFrame.Height * FlashImageFrame.Scale)) / 2);
                var targetTranslationX = _panStartTranslationX + (e.TotalX * PanSensitivity);
                var targetTranslationY = _panStartTranslationY + (e.TotalY * PanSensitivity);
                FlashImageFrame.TranslationX = Math.Clamp(targetTranslationX, -maxTranslationX, maxTranslationX);
                FlashImageFrame.TranslationY = Math.Clamp(targetTranslationY, -maxTranslationY, maxTranslationY);
#else
                FlashImageFrame.TranslationX = _panStartTranslationX + (e.TotalX * PanSensitivity);
                FlashImageFrame.TranslationY = _panStartTranslationY + (e.TotalY * PanSensitivity);
#endif
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
#if ANDROID
                _androidPanHoldCancellation?.Cancel();
                _androidPanHoldCancellation = null;
                var wasAndroidPanActive = _androidPanActive;
                _androidPanActive = false;
                _ = ResetAndroidPanAsync(wasAndroidPanActive);
#else
                _ = FlashImageFrame.TranslateTo(0, 0, 250, Easing.SinOut);
#endif
                break;
        }
    }

#if ANDROID
    private async Task ActivateAndroidPanAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(AndroidPanHoldDurationMilliseconds, cancellationToken);
            if (cancellationToken.IsCancellationRequested || _androidPinchActive)
                return;

            _androidPanActive = true;
            _androidSuppressTapUntil = DateTime.UtcNow.AddMilliseconds(500);
            await FlashImageFrame.ScaleTo(Math.Min(_imageScale * 1.05, 2), 100, Easing.SinOut);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ResetAndroidPanAsync(bool wasActive)
    {
        await FlashImageFrame.TranslateTo(0, 0, 250, Easing.CubicOut);
        if (wasActive)
        {
            await FlashImageFrame.ScaleTo(1, 250, Easing.CubicOut);
            SetZoom(1);
        }
    }
#endif

    private void OnFlashImagePinchUpdated(object? sender, PinchGestureUpdatedEventArgs e)
    {
        switch (e.Status)
        {
            case GestureStatus.Started:
#if ANDROID
                _androidPinchActive = true;
                _androidSuppressTapUntil = DateTime.UtcNow.AddMilliseconds(500);
                _androidPanHoldCancellation?.Cancel();
                _androidPanHoldCancellation = null;
                _androidPanActive = false;
                SetZoom(_imageScale);
#endif
                _pinchStartScale = _imageScale;
                break;
            case GestureStatus.Running:
#if ANDROID
                var pinchDelta = e.Scale - 1;
                if (Math.Abs(pinchDelta) < .01)
                    break;

                var pinchDirection = Math.Sign(pinchDelta);
                SetZoom(Math.Clamp(_pinchStartScale + (Math.Abs(pinchDelta) * pinchDirection), .6, 2));
#else
                SetZoom(Math.Clamp(_pinchStartScale * e.Scale, .6, 2));
#endif
                break;
            case GestureStatus.Completed:
            case GestureStatus.Canceled:
#if ANDROID
                _androidPinchActive = false;
                _ = ResetAndroidPinchAsync();
#else
                SetZoom(_imageScale);
#endif
                break;
        }
    }

#if ANDROID
    private async Task ResetAndroidPinchAsync()
    {
        await FlashImageFrame.ScaleTo(1, 250, Easing.CubicOut);
        SetZoom(1);
    }
#endif

#if WINDOWS
    private void OnWindowsPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(VirtualKeyModifiers.Control))
            return;

        var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
        if (delta == 0)
            return;

        e.Handled = true;
        SetZoom(Math.Clamp(_imageScale + (delta > 0 ? .1 : -.1), .6, 2));
        _wheelResetCancellation?.Cancel();
        _wheelResetCancellation = new CancellationTokenSource();
        _ = ResetWheelZoomAsync(_wheelResetCancellation.Token);
    }

    private async Task ResetWheelZoomAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(350, cancellationToken);
            await AnimateGestureScaleBackAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }
#endif

    private async Task AnimateGestureScaleBackAsync()
    {
        await FlashImageFrame.ScaleTo(1, 250, Easing.SinInOut);
        SetZoom(1);
    }

    private Task JiggleImageAsync()
    {
        var completion = new TaskCompletionSource<bool>();
        var animation = new Animation();
        animation.Add(0, .25, new Animation(value => FlashImageFrame.Scale = value, 1, 1.08, Easing.SinInOut));
        animation.Add(.25, .5, new Animation(value => FlashImageFrame.Scale = value, 1.08, 1, Easing.SinInOut));
        animation.Add(0, .25, new Animation(value => FlashImageFrame.Rotation = value, 0, 4, Easing.SinInOut));
        animation.Add(.25, .5, new Animation(value => FlashImageFrame.Rotation = value, 4, -4, Easing.SinInOut));
        animation.Add(.5, .75, new Animation(value => FlashImageFrame.Rotation = value, -4, 2, Easing.SinInOut));
        animation.Add(.75, 1, new Animation(value => FlashImageFrame.Rotation = value, 2, 0, Easing.SinInOut));
        animation.Commit(FlashImageFrame, "ImageJiggle", 16, 500, Easing.SinInOut, (_, finished) => completion.TrySetResult(finished));
        return completion.Task;
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
        FlashImageFrame.Scale = scale;
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
            DiscardedCards.Insert(0, card);
        }

        ScrollDiscardCollectionToStart();
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
            DiscardedCards.Insert(0, CurrentCard);
            ScrollDiscardCollectionToStart();
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

        var angle = _showingBack ? -180 : 180;
        await AnimateFlipHalfAsync(0, angle, true);
        _showingBack = !_showingBack;
        RefreshFlashArea();
        await AnimateFlipHalfAsync(angle, angle * 2, false);
        FlashImageFrame.RotationY = 0;
    }

    private async Task AnimateCardChangeAsync(FlashCard card)
    {
        await AnimateFlipHalfAsync(0, 180, true);
        _currentCard = card;
        _showingBack = false;
        RefreshFlashArea();
        await AnimateFlipHalfAsync(180, 360, false);
        FlashImageFrame.RotationY = 0;
    }

    private Task AnimateFlipHalfAsync(double startAngle, double endAngle, bool narrow)
    {
        var completion = new TaskCompletionSource<bool>();
        var animation = new Animation(value =>
        {
            FlashImageFrame.RotationY = startAngle + (endAngle - startAngle) * value;
            FlashImageFrame.ScaleX = narrow ? 1 - (.96 * value) : .04 + (.96 * value);
        }, 0, 1, Easing.SinInOut);
        animation.Commit(FlashImageFrame, "FlashCardFlip", 16, 220, Easing.SinInOut, (_, finished) =>
        {
            if (finished && !narrow)
                FlashImageFrame.ScaleX = 1;
            completion.TrySetResult(finished);
        });
        return completion.Task;
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
        ViewDeckButton.IsEnabled = canUseOtherActions && Cards.Count > 0;
        SaveInAppButton.IsEnabled = canUseOtherActions;
        LoadDataButton.IsEnabled = canUseOtherActions;
        GenerateDataButton.IsEnabled = canUseOtherActions;
        IncrementDataButton.IsEnabled = canUseOtherActions;
        EditButton.IsEnabled = canUseOtherActions;
        ClearAreaButton.IsEnabled = canUseOtherActions;
        ZoomInButton.IsEnabled = canUseOtherActions;
        ZoomOutButton.IsEnabled = canUseOtherActions;
        ResetZoomButton.IsEnabled = canUseOtherActions && Math.Abs(_imageScale - 1) > .001;
        AndroidMaximizedZoomInButton.IsEnabled = ZoomInButton.IsEnabled;
        AndroidMaximizedZoomOutButton.IsEnabled = ZoomOutButton.IsEnabled;
        AndroidMaximizedResetZoomButton.IsEnabled = ResetZoomButton.IsEnabled;
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
            ViewDeckButton,
            SaveInAppButton,
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
        AndroidMaximizedMinimizeButton.BackgroundColor = green;
        AndroidMaximizedResetZoomButton.BackgroundColor = AndroidMaximizedResetZoomButton.IsEnabled && !isRunning ? green : gray;
        AndroidMaximizedZoomInButton.BackgroundColor = AndroidMaximizedZoomInButton.IsEnabled && !isRunning ? green : gray;
        AndroidMaximizedZoomOutButton.BackgroundColor = AndroidMaximizedZoomOutButton.IsEnabled && !isRunning ? green : gray;
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

    private void ScrollDiscardCollectionToStart()
    {
        if (DiscardedCards.Count > 0)
            DiscardCollection.ScrollTo(0, position: ScrollToPosition.Start, animate: true);
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
        SetZoom(1);
        CaptureNormalImageFrameSize();
        _isMaximized = !_isMaximized;
        ApplyMaximizeState();
    }

    private void CaptureNormalImageFrameSize()
    {
        if (FlashImageFrame.Width > 0)
            _normalImageFrameWidth = FlashImageFrame.Width;
        if (FlashImageFrame.Height > 0)
            _normalImageFrameHeight = FlashImageFrame.Height;
    }

    private void OnPageScrollSizeChanged(object? sender, EventArgs e)
    {
        if (_isMaximized)
            ApplyMaximizeState();
    }

    private async void OnMinimizeClicked(object? sender, EventArgs e)
    {
        SetZoom(1);
        _isMaximized = false;
        RestoreAndroidMaximizedControlsToRoot();
        AndroidMaximizedFlashAreaHeader.IsVisible = false;
        AndroidMaximizedImageControlsBorder.IsVisible = false;
        RestoreTopControlPanelToWorkspace();
        PageScroll.IsVisible = true;
        FlashCardBorder.HeightRequest = -1;
        Workspace.HeightRequest = -1;
        Grid.SetRow(FlashAreaHeader, 2);
        Grid.SetRow(FlashCardBorder, 3);
        for (var i = 0; i < Workspace.RowDefinitions.Count; i++)
            Workspace.RowDefinitions[i].Height = i == 3 ? new GridLength(NormalFlashAreaHeight) : GridLength.Auto;

        Workspace.Padding = new Thickness(12);
        TopControlPanel.IsVisible = true;
        DataManagementPanel.IsVisible = true;
        FlashAreaHeader.IsVisible = true;
        DiscardSection.IsVisible = true;
        GeneratedFileRow.IsVisible = true;
        FlashCardControls.IsVisible = true;
        PageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Default;
        MaximizeButton.IsVisible = true;
        MinimizeButton.IsVisible = false;
        ApplyImageLayout();
        FlashImageFrame.TranslationX = 0;
        FlashImageFrame.TranslationY = 0;
        Workspace.InvalidateMeasure();
        await PageScroll.ScrollToAsync(0, 0, false);
    }

    private void ApplyMaximizeState()
    {
        var isAndroid = DeviceInfo.Current.Platform == DevicePlatform.Android;
        var useAndroidMaximizedHeader = _isMaximized && isAndroid;
        if (useAndroidMaximizedHeader)
        {
            RestoreTopControlPanelToWorkspace();
            MoveAndroidMaximizedControlsToWorkspace();
        }
        else
        {
            RestoreAndroidMaximizedControlsToRoot();
        }

        if (_isMaximized && !useAndroidMaximizedHeader)
            MoveTopControlPanelToRoot();
        else if (!_isMaximized)
            RestoreTopControlPanelToWorkspace();

        PageScroll.IsVisible = true;
        var flashAreaHeight = useAndroidMaximizedHeader
            ? AndroidMaximizedFlashAreaHeight
            : Math.Max(
                PageScroll.Height - Workspace.Padding.VerticalThickness - MaximizedBottomReserve - 48,
                NormalFlashAreaHeight);
        Workspace.Padding = _isMaximized ? new Thickness(12, 0, 12, 12) : new Thickness(12);
        var maximizedHeight = flashAreaHeight + (useAndroidMaximizedHeader ? 0 : MaximizedScrollExtension);
        Workspace.HeightRequest = useAndroidMaximizedHeader
            ? -1
            : _isMaximized ? maximizedHeight : -1;
        Grid.SetRow(FlashAreaHeader, _isMaximized && !useAndroidMaximizedHeader ? 0 : 2);
        Grid.SetRow(FlashCardBorder, useAndroidMaximizedHeader ? 3 : _isMaximized ? 1 : 3);
        for (var i = 0; i < Workspace.RowDefinitions.Count; i++)
        {
            Workspace.RowDefinitions[i].Height = useAndroidMaximizedHeader
                ? i switch
                {
                    0 or 1 or 2 => GridLength.Auto,
                    3 => new GridLength(AndroidMaximizedFlashAreaHeight),
                    _ => new GridLength(0)
                }
                : _isMaximized ? new GridLength(0) : i == 3 ? new GridLength(NormalFlashAreaHeight) : GridLength.Auto;
        }
        if (_isMaximized && !useAndroidMaximizedHeader)
            Workspace.RowDefinitions[1].Height = new GridLength(maximizedHeight);
        FlashCardBorder.HeightRequest = useAndroidMaximizedHeader ? AndroidMaximizedFlashAreaHeight : -1;
        TopControlPanel.IsVisible = true;
        DataManagementPanel.IsVisible = !_isMaximized;
        AndroidMaximizedFlashAreaHeader.IsVisible = useAndroidMaximizedHeader;
        AndroidMaximizedImageControlsBorder.IsVisible = useAndroidMaximizedHeader;
        FlashAreaHeader.IsVisible = !useAndroidMaximizedHeader;
        FlashCardControls.IsVisible = !useAndroidMaximizedHeader;
        DiscardSection.IsVisible = !_isMaximized;
        GeneratedFileRow.IsVisible = !_isMaximized;
        PageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Default;
        MaximizeButton.IsVisible = !_isMaximized;
        MinimizeButton.IsVisible = _isMaximized;
        ApplyImageLayout();
        Workspace.InvalidateMeasure();
        if (_isMaximized)
            _ = PageScroll.ScrollToAsync(0, 0, false);
    }

    private void MoveTopControlPanelToRoot()
    {
        if (Workspace.Children.Contains(TopControlPanel))
            Workspace.Children.Remove(TopControlPanel);

        if (!RootLayout.Children.Contains(TopControlPanel))
            RootLayout.Children.Add(TopControlPanel);

        Grid.SetRow(TopControlPanel, 0);
    }

    private void MoveAndroidMaximizedControlsToWorkspace()
    {
        if (RootLayout.Children.Contains(AndroidMaximizedFlashAreaHeader))
            RootLayout.Children.Remove(AndroidMaximizedFlashAreaHeader);
        if (!Workspace.Children.Contains(AndroidMaximizedFlashAreaHeader))
            Workspace.Children.Add(AndroidMaximizedFlashAreaHeader);
        Grid.SetRow(AndroidMaximizedFlashAreaHeader, 1);

        if (RootLayout.Children.Contains(AndroidMaximizedImageControlsBorder))
            RootLayout.Children.Remove(AndroidMaximizedImageControlsBorder);
        if (!Workspace.Children.Contains(AndroidMaximizedImageControlsBorder))
            Workspace.Children.Add(AndroidMaximizedImageControlsBorder);
        Grid.SetRow(AndroidMaximizedImageControlsBorder, 2);
    }

    private void RestoreAndroidMaximizedControlsToRoot()
    {
        if (Workspace.Children.Contains(AndroidMaximizedFlashAreaHeader))
            Workspace.Children.Remove(AndroidMaximizedFlashAreaHeader);
        if (!RootLayout.Children.Contains(AndroidMaximizedFlashAreaHeader))
            RootLayout.Children.Add(AndroidMaximizedFlashAreaHeader);
        Grid.SetRow(AndroidMaximizedFlashAreaHeader, 1);

        if (Workspace.Children.Contains(AndroidMaximizedImageControlsBorder))
            Workspace.Children.Remove(AndroidMaximizedImageControlsBorder);
        if (!RootLayout.Children.Contains(AndroidMaximizedImageControlsBorder))
            RootLayout.Children.Add(AndroidMaximizedImageControlsBorder);
        Grid.SetRow(AndroidMaximizedImageControlsBorder, 2);
    }

    private void RestoreTopControlPanelToWorkspace()
    {
        if (RootLayout.Children.Contains(TopControlPanel))
            RootLayout.Children.Remove(TopControlPanel);

        if (!Workspace.Children.Contains(TopControlPanel))
            Workspace.Children.Add(TopControlPanel);

        Grid.SetRow(TopControlPanel, 0);
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

    private async void OnCheckForUpdateClicked(object? sender, EventArgs e)
    {
        await CheckForUpdateAsync(forceUserPrompt: true);
    }

    private async Task CheckForUpdateInBackgroundAsync()
    {
        await CheckForUpdateAsync(forceUserPrompt: false, suppressStatus: true);
    }

    private async Task CheckForUpdateAsync(bool forceUserPrompt, bool suppressStatus = false)
    {
        if (_updateCheckInProgress)
            return;

        _updateCheckInProgress = true;
        _updateCheckCancellation?.Cancel();
        _updateCheckCancellation = new CancellationTokenSource();

        try
        {
            var status = await _updateService.CheckForUpdateAsync(_updateCheckCancellation.Token);
            if (_updateCheckCancellation.IsCancellationRequested)
                return;

            _updateAvailable = status.UpdateAvailable;
            _updateReleaseUrl = status.ReleaseUrl;
            UpdateUpdateIndicator();

            if (suppressStatus)
                return;

            if (status.UpdateAvailable)
            {
                var message = $"A new version ({status.LatestVersionText}) is available. Open the release page to download it?";
                var shouldOpen = await DisplayAlert("Update available", message, "Open", "Later");
                if (shouldOpen && !string.IsNullOrWhiteSpace(_updateReleaseUrl))
                    await Microsoft.Maui.ApplicationModel.Launcher.Default.OpenAsync(new Uri(_updateReleaseUrl));
                return;
            }

            if (forceUserPrompt)
                await DisplayAlert("Update check", "You are already on the latest version.", "OK");
        }
        catch (Exception)
        {
            if (forceUserPrompt)
                await DisplayAlert("Update check", "No update information could be fetched right now. Please try again later.", "OK");
        }
        finally
        {
            _updateCheckInProgress = false;
            _updateCheckCancellation?.Dispose();
            _updateCheckCancellation = null;
        }
    }

    private void UpdateUpdateIndicator()
    {
        if (UpdateAvailableDot is null)
            return;

        UpdateAvailableDot.IsVisible = _updateAvailable;
    }

    private async void OnCopyPathClicked(object sender, EventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_generatedFilePath) || !File.Exists(_generatedFilePath))
        {
            await DisplayAlert("Copy path", "Path File not available or not generated yet", "OK");
            return;
        }

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
        UpdateImageAspectRatio(_showingBack && hasBack ? card.BackImage : card.FrontImage);
        FlashImageFrame.Scale = _imageScale;
        UpdateDiscardActions();
    }

    private void UpdateImageAspectRatio(string? imagePath)
    {
        if (TryGetImageAspectRatio(imagePath, out var aspectRatio))
            _imageAspectRatio = aspectRatio;
        else
            _imageAspectRatio = 4d / 3d;

        if (_isMaximized)
            ApplyImageLayout();
    }

    private static bool TryGetImageAspectRatio(string? imagePath, out double aspectRatio)
    {
        aspectRatio = 0;
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
            return false;

        try
        {
            using var stream = File.OpenRead(imagePath);
            Span<byte> header = stackalloc byte[24];
            if (stream.Read(header) >= 24 &&
                header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
            {
                var width = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                var height = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                return SetAspectRatio(width, height, out aspectRatio);
            }

            stream.Position = 0;
            if (stream.ReadByte() != 0xFF || stream.ReadByte() != 0xD8)
                return false;

            while (stream.Position < stream.Length)
            {
                if (stream.ReadByte() != 0xFF)
                    return false;

                var marker = stream.ReadByte();
                while (marker == 0xFF)
                    marker = stream.ReadByte();

                if (marker is >= 0xC0 and <= 0xC3 or >= 0xC5 and <= 0xC7 or >= 0xC9 and <= 0xCB or >= 0xCD and <= 0xCF)
                {
                    var length = (stream.ReadByte() << 8) | stream.ReadByte();
                    if (length < 7)
                        return false;

                    stream.ReadByte();
                    var height = (stream.ReadByte() << 8) | stream.ReadByte();
                    var width = (stream.ReadByte() << 8) | stream.ReadByte();
                    return SetAspectRatio(width, height, out aspectRatio);
                }

                var segmentLength = (stream.ReadByte() << 8) | stream.ReadByte();
                if (segmentLength < 2 || stream.Position + segmentLength - 2 > stream.Length)
                    return false;
                stream.Position += segmentLength - 2;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static bool SetAspectRatio(int width, int height, out double aspectRatio)
    {
        aspectRatio = height > 0 && width > 0 ? (double)width / height : 0;
        return aspectRatio > 0 && double.IsFinite(aspectRatio);
    }

    private static string GetBackName(FlashCard card) =>
        string.IsNullOrWhiteSpace(card.BackName)
            ? Path.GetFileNameWithoutExtension(card.BackImage)
            : card.BackName;

    private static string SanitizeDataSetName(string name)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var safeName = new string(name.Trim()
            .Select(character => char.IsControl(character) || character is '/' or '\\' || invalidCharacters.Contains(character) ? '-' : character)
            .ToArray())
            .Trim(' ', '.');
        return safeName.EndsWith(".flashcards", StringComparison.OrdinalIgnoreCase)
            ? safeName[..^".flashcards".Length].Trim(' ', '.')
            : safeName;
    }

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
            var isData = fileType.Equals("data", StringComparison.OrdinalIgnoreCase);
            var pickerType = isData ?
                new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                {
                    [DevicePlatform.Android] = new[] { "application/json", "application/zip", "application/octet-stream" },
                    [DevicePlatform.iOS] = new[] { "public.json", "public.archive" },
                    [DevicePlatform.MacCatalyst] = new[] { "public.json", "public.archive" },
                    [DevicePlatform.WinUI] = new[] { ".json", ".flashcards", ".zip" }
                }) : isJson ?
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
            if (!isJson && !isPackage && !isData && file is not null && !IsImageFile(file.FileName))
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
        {
            await using var packageStream = await file.OpenReadAsync();
            return await ReadPackageCardsAsync(packageStream);
        }

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

    private static async Task<List<FlashCard>> ReadPackageCardsAsync(Stream source)
    {
        var temporaryPath = Path.Combine(FileSystem.Current.CacheDirectory, $"{Guid.NewGuid():N}.flashcards");
        try
        {
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
    private string _name = string.Empty;

    [JsonPropertyName("name")]
    public string Name
    {
        get => _name;
        set => _name = RemoveGeneratedIndexPrefix(value ?? string.Empty);
    }

    [JsonPropertyName("frontImage")]
    public string FrontImage { get; set; } = string.Empty;

    [JsonPropertyName("backImage")]
    public string BackImage { get; set; } = string.Empty;

    [JsonPropertyName("backName")]
    public string BackName { get; set; } = string.Empty;

    private static string RemoveGeneratedIndexPrefix(string name)
    {
        var separatorIndex = name.IndexOf(" - ", StringComparison.Ordinal);
        if (separatorIndex <= 0)
            return name;

        for (var index = 0; index < separatorIndex; index++)
        {
            if (name[index] is < '0' or > '9')
                return name;
        }

        return name[(separatorIndex + 3)..];
    }
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

internal sealed class BuiltInDataSet
{
    public string Name { get; set; } = string.Empty;
    public string File { get; set; } = string.Empty;
}

internal sealed class DataSetChoice
{
    public string Name { get; init; } = string.Empty;
    public string File { get; init; } = string.Empty;
    public bool IsPackaged { get; init; }
    public string DisplayName => $"{Name} ({(IsPackaged ? "Built-in" : "Saved")})";
}

internal sealed class NaturalFileNameComparer : IComparer<string>
{
    public static NaturalFileNameComparer Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
            return 0;
        if (left is null)
            return -1;
        if (right is null)
            return 1;

        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            if (char.IsDigit(left[leftIndex]) && char.IsDigit(right[rightIndex]))
            {
                var leftEnd = leftIndex;
                var rightEnd = rightIndex;
                while (leftEnd < left.Length && char.IsDigit(left[leftEnd]))
                    leftEnd++;
                while (rightEnd < right.Length && char.IsDigit(right[rightEnd]))
                    rightEnd++;

                var leftSignificant = leftIndex;
                var rightSignificant = rightIndex;
                while (leftSignificant < leftEnd && left[leftSignificant] == '0')
                    leftSignificant++;
                while (rightSignificant < rightEnd && right[rightSignificant] == '0')
                    rightSignificant++;

                var leftSignificantLength = leftEnd - leftSignificant;
                var rightSignificantLength = rightEnd - rightSignificant;
                var numberComparison = leftSignificantLength.CompareTo(rightSignificantLength);
                if (numberComparison != 0)
                    return numberComparison;

                for (var i = 0; i < leftSignificantLength; i++)
                {
                    numberComparison = left[leftSignificant + i].CompareTo(right[rightSignificant + i]);
                    if (numberComparison != 0)
                        return numberComparison;
                }

                var digitRunComparison = (leftEnd - leftIndex).CompareTo(rightEnd - rightIndex);
                if (digitRunComparison != 0)
                    return digitRunComparison;

                leftIndex = leftEnd;
                rightIndex = rightEnd;
                continue;
            }

            var characterComparison = char.ToUpperInvariant(left[leftIndex]).CompareTo(char.ToUpperInvariant(right[rightIndex]));
            if (characterComparison != 0)
                return characterComparison;
            leftIndex++;
            rightIndex++;
        }

        var lengthComparison = (left.Length - leftIndex).CompareTo(right.Length - rightIndex);
        return lengthComparison != 0 ? lengthComparison : StringComparer.Ordinal.Compare(left, right);
    }
}

internal sealed class FlashCardsState
{
    public List<FlashCard> Cards { get; set; } = new();
    public List<int> DiscardedCardIndices { get; set; } = new();
    public int? CurrentCardIndex { get; set; }
    public bool ShowingBack { get; set; }
}