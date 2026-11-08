using MoxSynth.Client.ViewModels;

namespace MoxSynth.Mobile.Pages;

/// <summary>Page base: view model as BindingContext, forwards navigation parameters and "appearing".</summary>
public abstract class ViewModelPage : ContentPage, IQueryAttributable
{
    protected ViewModelPage(ViewModelBase viewModel)
    {
        BindingContext = viewModel;
    }

    protected ViewModelBase ViewModel => (ViewModelBase)BindingContext;

    public void ApplyQueryAttributes(IDictionary<string, object> query) => ViewModel.ApplyParameters(query);

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await ViewModel.OnAppearingAsync();
        }
        catch (Exception ex)
        {
            ViewModel.Error = ex.Message;
        }
    }

    /// <summary>The item a row's button / gesture belongs to.</summary>
    protected static T? ItemOf<T>(object sender) where T : class => (sender as BindableObject)?.BindingContext as T;
}
