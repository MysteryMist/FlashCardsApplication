namespace FlashCardsApp;

public partial class DeckPage : ContentPage
{
    private readonly Func<FlashCard, Task> _onCardSelected;
    private bool _isHandlingCardTap;

    public IReadOnlyList<FlashCard> Cards { get; }

    public DeckPage(IEnumerable<FlashCard> cards, Func<FlashCard, Task> onCardSelected)
    {
        InitializeComponent();
        Cards = cards.ToList();
        _onCardSelected = onCardSelected;
        BindingContext = this;
    }

    private async void OnBackClicked(object? sender, EventArgs e) => await Navigation.PopModalAsync();

    private async void OnCardTapped(object? sender, TappedEventArgs e)
    {
        if (_isHandlingCardTap || e.Parameter is not FlashCard card)
            return;

        _isHandlingCardTap = true;
        await Navigation.PopModalAsync();
        await _onCardSelected(card);
    }
}
