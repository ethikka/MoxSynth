using CommunityToolkit.Mvvm.ComponentModel;
using MoxSynth.Client.Services;

namespace MoxSynth.Client.ViewModels;

public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotBusy))]
    private bool _isBusy;

    /// <summary>Pull-to-refresh indicator.</summary>
    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _error;

    public bool IsNotBusy => !IsBusy;
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>Called by the page each time it appears.</summary>
    public virtual Task OnAppearingAsync() => Task.CompletedTask;

    /// <summary>Navigation parameters (see <see cref="INavigator.GoToAsync"/>).</summary>
    public virtual void ApplyParameters(IDictionary<string, object> parameters)
    {
    }

    /// <summary>Runs an API action: shows busy state, turns API errors into <see cref="Error"/>.</summary>
    protected async Task<bool> RunAsync(Func<Task> action, bool showBusy = true)
    {
        Error = null;
        if (showBusy) IsBusy = true;
        try
        {
            await action();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ApiException ex)
        {
            Error = ex.Message;
            return false;
        }
        finally
        {
            if (showBusy) IsBusy = false;
            IsRefreshing = false;
        }
    }
}
