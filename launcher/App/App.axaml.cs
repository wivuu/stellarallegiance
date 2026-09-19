using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using StellarAllegiance.Launcher.Flow;
using StellarAllegiance.Launcher.Lobby;
using StellarAllegiance.Launcher.Platform;
using StellarAllegiance.Launcher.Views;

namespace StellarAllegiance.Launcher;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The launcher outlives its window: it hides while the game runs and exits when the FLOW says so.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var services = Program.Services;
            var host = new AvaloniaHost(desktop, services.Log);
            Window window;

            if (services.Args.Showcase)
            {
                window = new ShowcaseWindow();
                window.Closed += (_, _) => desktop.Shutdown(0);
            }
            else if (services.Args.Fake is { } scenario)
            {
                // A ready-made LauncherView, no flow behind it: for screenshots and design review.
                var main = new MainWindow(services, flow: null, host, lobby: null);
                main.Apply(FakeViews.Get(scenario) ?? FakeViews.Get("uptodate")!);
                main.Closed += (_, _) => desktop.Shutdown(0);
                window = main;
            }
            else
            {
                var lobby = new LobbyStatusClient(
                    LobbyAddress.Resolve(services.Args.GameArgs, Environment.GetEnvironmentVariable("PUBLIC_LOBBY"))
                );
                var flow = new LauncherFlow(
                    services.FlowOptions,
                    services.Updates,
                    services.Games,
                    services.Settings,
                    host,
                    new AvaloniaDispatcher(),
                    services.Log
                );
                var main = new MainWindow(services, flow, host, lobby);
                main.Opened += (_, _) => flow.Start();
                // A second launcher start rings the doorbell: focus the game if one is running, else show us.
                Program.Instance.Activated += () => Dispatcher.UIThread.Post(flow.OnSecondInstance);
                window = main;
            }

            host.Window = window;
            desktop.MainWindow = window;
            // Two seconds of a visible, responsive window = this renderer works: clear the boot sentinel
            // (see RenderPolicy) so the next start does not fall back to software rendering.
            window.Opened += (_, _) =>
                DispatcherTimer.RunOnce(() => RenderSetup.ClearSentinel(services.Paths.Root), TimeSpan.FromSeconds(2));
            if (services.Args.Shot is { } png)
                Screenshot.CaptureThenExit(window, png, desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
