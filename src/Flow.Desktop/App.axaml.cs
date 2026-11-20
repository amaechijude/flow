using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Flow.Application.Queries;
using Flow.Desktop.Services;
using Flow.Desktop.ViewModels;
using Flow.Desktop.Views;
using Flow.Infrastructure.Sqlite;

namespace Flow.Desktop;

public partial class App : global::Avalonia.Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var dbPath = AppPaths.DatabasePath();
            var factory = new SqliteConnectionFactory(dbPath);
            var repo = new UsageRepository(factory);
            var localUsage = new LocalUsageQueryClient(repo);
            var dbus = new DbusDaemonClient();
            IUsageQueryClient usageClient = new HybridUsageQueryClient(dbus, localUsage);
            IDaemonStatusClient statusClient = new HybridDaemonStatusClient(dbus);

            var window = new MainWindow();
            var picker = new StorageProviderExportFilePicker { TopLevel = window };
            window.DataContext = new MainViewModel(statusClient, usageClient, picker, dbus);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
