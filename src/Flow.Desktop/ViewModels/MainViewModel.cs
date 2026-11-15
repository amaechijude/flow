namespace Flow.Desktop.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public OverviewViewModel Overview { get; }

    public MainViewModel(OverviewViewModel overview)
    {
        Overview = overview;
    }
}
