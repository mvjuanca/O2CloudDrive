namespace O2CloudDrive.Auth;

public sealed class WebViewInteractiveLoginService : IInteractiveLoginService
{
    private readonly Uri _loginUri;
    private readonly AuthSessionLog? _authSessionLog;

    public WebViewInteractiveLoginService(string loginUrl, AuthSessionLog? authSessionLog = null)
    {
        _loginUri = new Uri(loginUrl, UriKind.Absolute);
        _authSessionLog = authSessionLog;
    }

    public O2Session? Login(O2SessionValidator validator)
    {
        using var form = new O2LoginForm(_loginUri, validator, authSessionLog: _authSessionLog);
        return form.ShowDialog() == DialogResult.OK ? form.CapturedSession : null;
    }

    public void ClearSessionCache()
    {
        O2WebViewProfile.Clear();
    }
}
