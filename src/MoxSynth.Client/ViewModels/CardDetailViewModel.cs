using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoxSynth.Client.Services;
using MoxSynth.Contracts;

namespace MoxSynth.Client.ViewModels;

/// <summary>Card page: image, rules text, prices of all shops, your copies, add / remove copies, other printings.</summary>
public sealed partial class CardDetailViewModel(MoxApiClient api, AppSession session, INavigator nav, IDialogs dialogs)
    : ViewModelBase
{
    private string? _uuid;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Subtitle), nameof(HasBackImage), nameof(ShownImageUrl))]
    private CardDetailDto? _card;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownImageUrl))]
    private bool _showBack;

    [ObservableProperty]
    private string _ownedText = "";

    // Add form
    [ObservableProperty]
    private VocabularyOption? _selectedFinish;

    [ObservableProperty]
    private VocabularyOption _selectedCondition = CardVocabulary.Conditions[1]; // Near Mint

    [ObservableProperty]
    private int _quantity = 1;

    public ObservableCollection<CardFaceDto> Faces { get; } = [];
    public ObservableCollection<PriceRow> Prices { get; } = [];
    public ObservableCollection<CopyItem> Copies { get; } = [];
    public ObservableCollection<CardItem> OtherPrintings { get; } = [];
    public ObservableCollection<VocabularyOption> Finishes { get; } = [];
    public IReadOnlyList<VocabularyOption> Conditions => CardVocabulary.Conditions;

    public string Title => Card?.Name ?? "";
    public string Subtitle => Card is null ? "" :
        $"{Card.SetName} ({Card.SetCode}) · #{Card.Number} · {Format.Rarity(Card.Rarity)}" +
        (Card.ReleaseDate is { Length: >= 4 } d ? $" · {d[..4]}" : "");
    public bool HasBackImage => Card?.BackImageUrl is not null;
    public string? ShownImageUrl => ShowBack && Card?.BackImageUrl is { } back ? back : Card?.ImageUrl;
    public bool HasPrices => Prices.Count > 0;
    public bool HasCopies => Copies.Count > 0;
    public bool HasOtherPrintings => OtherPrintings.Count > 0;

    public override void ApplyParameters(IDictionary<string, object> parameters)
    {
        if (parameters.TryGetValue("uuid", out var uuid) && uuid is string id && id != _uuid)
        {
            _uuid = id;
            Card = null;
        }
    }

    public override Task OnAppearingAsync() => Card is null && _uuid is not null ? LoadAsync() : Task.CompletedTask;

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(showBusy: false);

    public Task LoadAsync(bool showBusy = true) => RunAsync(async () =>
    {
        var card = await api.GetCardAsync(_uuid!, session.Provider);
        Card = card;
        ShowBack = false;

        Faces.Clear();
        foreach (var f in card.Faces) Faces.Add(f);

        Prices.Clear();
        foreach (var p in card.Prices)
        {
            var label = session.Providers.FirstOrDefault(x => x.Id == p.Provider)?.Label ?? p.Provider;
            Prices.Add(new PriceRow(p, label, p.Provider == session.Provider));
        }

        Finishes.Clear();
        foreach (var f in card.Finishes)
            Finishes.Add(CardVocabulary.Finishes.FirstOrDefault(x => x.Value == f) ?? new VocabularyOption(f, f, f));
        SelectedFinish = Finishes.FirstOrDefault(f => f.Value == SelectedFinish?.Value) ?? Finishes.FirstOrDefault();

        OtherPrintings.Clear();
        foreach (var p in card.OtherPrintings) OtherPrintings.Add(new CardItem(p));

        SetCopies(card.Copies);
        OnPropertyChanged(nameof(HasPrices));
        OnPropertyChanged(nameof(HasOtherPrintings));
    }, showBusy);

    private void SetCopies(IReadOnlyList<CopyDto> copies)
    {
        Copies.Clear();
        foreach (var c in copies) Copies.Add(new CopyItem(c));
        var owned = copies.Count(c => c.RemovedAt is null);
        OwnedText = owned == 0 ? "You don't own this printing yet." : $"You own {owned} cop{(owned == 1 ? "y" : "ies")}.";
        OnPropertyChanged(nameof(HasCopies));
    }

    private async Task ReloadCopiesAsync()
    {
        var card = await api.GetCardAsync(_uuid!, session.Provider);
        SetCopies(card.Copies);
    }

    [RelayCommand]
    private void ToggleFace() => ShowBack = !ShowBack;

    [RelayCommand]
    private void ChangeQuantity(string delta) =>
        Quantity = Math.Clamp(Quantity + int.Parse(delta, System.Globalization.CultureInfo.InvariantCulture), 1, 100);

    [RelayCommand]
    private async Task AddAsync()
    {
        if (Card is null || SelectedFinish is null) return;
        await RunAsync(async () =>
        {
            await api.AddCardsAsync(new AddCardsDto(Card.Uuid, SelectedFinish.Value, SelectedCondition.Value, Quantity: Quantity));
            await dialogs.ToastAsync($"Added {Quantity}× {Card.Name} ({SelectedFinish.Label}, {SelectedCondition.Short})");
            Quantity = 1;
            await ReloadCopiesAsync();
        });
    }

    [RelayCommand]
    private async Task RemoveCopyAsync(CopyItem item)
    {
        var reason = await dialogs.ChooseAsync("Why is this copy leaving?", CardVocabulary.RemovalReasons);
        if (reason is null) return;
        await RunAsync(async () =>
        {
            await api.RemoveCopyAsync(item.Copy.Id, reason);
            await ReloadCopiesAsync();
        });
    }

    [RelayCommand]
    private async Task RestoreCopyAsync(CopyItem item)
    {
        if (!await dialogs.ConfirmAsync("Undo removal", "Put this copy back into your collection?", "Restore")) return;
        await RunAsync(async () =>
        {
            await api.RestoreCopyAsync(item.Copy.Id);
            await ReloadCopiesAsync();
        });
    }

    [RelayCommand]
    private Task OpenPrintingAsync(CardItem item) =>
        nav.GoToAsync(Routes.Card, new Dictionary<string, object> { ["uuid"] = item.Card.Uuid });
}
