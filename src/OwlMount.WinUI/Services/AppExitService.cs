namespace OwlMount.WinUI.Services;

public interface IAppExitService
{
    void SetExitAction(Action exitAction);
    void Exit();
}

public sealed class AppExitService : IAppExitService
{
    private Action? _exitAction;

    public void SetExitAction(Action exitAction) => _exitAction = exitAction;

    public void Exit() => _exitAction?.Invoke();
}
