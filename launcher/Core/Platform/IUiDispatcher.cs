namespace StellarAllegiance.Launcher.Platform;

// LauncherFlow is single-threaded by construction: every mutation happens on "the UI thread", whatever
// that is for the host (Avalonia's dispatcher in the app, an inline queue in tests, a plain lock in the
// headless self-test). Service callbacks arrive on arbitrary threads and come back through Post.
public interface IUiDispatcher
{
    void Post(Action action);
}
