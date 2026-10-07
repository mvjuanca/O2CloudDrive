namespace O2CloudDrive.Auth;

public interface IAuthService
{
    O2Session GetCurrentSession();
    bool HasStoredSession();
    bool HasValidatedSession { get; }
    O2Session? EnsureAuthenticated(bool allowInteractive, bool forceInteractive = false);
    StoredSessionValidationResult ValidateStoredSession(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return EnsureAuthenticated(allowInteractive: false) is { IsAuthenticated: true }
            ? new StoredSessionValidationResult(StoredSessionValidationStatus.Valid, "validated", SessionValidationClassification.Accepted)
            : new StoredSessionValidationResult(StoredSessionValidationStatus.Missing, "no_stored_session", SessionValidationClassification.MissingSession);
    }
    void Logout();
}

public enum StoredSessionValidationStatus
{
    Missing,
    Valid,
    Rejected,
    Unavailable,
}

public sealed record StoredSessionValidationResult(
    StoredSessionValidationStatus Status,
    string Condition,
    SessionValidationClassification Classification,
    O2SessionValidationDiagnostic? Diagnostic = null);

public sealed class AuthService : IAuthService
{
    private const string ValidationKeyVariable = "O2CLOUD_VALIDATIONKEY";
    private readonly ISessionStore _sessionStore;
    private readonly IInteractiveLoginService _loginService;
    private readonly O2SessionValidator _validator;
    private O2Session? _currentSession;
    private StoredSessionValidationResult? _lastTerminalStoredValidation;

    public AuthService(
        ISessionStore sessionStore,
        IInteractiveLoginService loginService,
        O2SessionValidator validator)
    {
        _sessionStore = sessionStore;
        _loginService = loginService;
        _validator = validator;
    }

    public O2Session GetCurrentSession()
    {
        return _currentSession ?? TryEnvironmentSession() ?? _sessionStore.TryRead() ?? new O2Session();
    }

    public bool HasStoredSession()
    {
        return _currentSession is { IsAuthenticated: true } ||
               TryEnvironmentSession() is { IsAuthenticated: true } ||
               _sessionStore.TryRead() is { IsAuthenticated: true };
    }

    public bool HasValidatedSession => _currentSession is { IsAuthenticated: true };

    public StoredSessionValidationResult ValidateStoredSession(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_lastTerminalStoredValidation is not null)
        {
            return _lastTerminalStoredValidation;
        }

        var storedSession = _sessionStore.TryRead();
        if (storedSession is null)
        {
            _currentSession = null;
            return _lastTerminalStoredValidation = new StoredSessionValidationResult(
                StoredSessionValidationStatus.Missing, "no_stored_session", SessionValidationClassification.MissingSession);
        }

        var diagnostic = _validator.ValidateDetailed(storedSession, cancellationToken, storedSession: true);
        if (diagnostic.Classification == SessionValidationClassification.Accepted)
        {
            _currentSession = storedSession;
            return _lastTerminalStoredValidation = new StoredSessionValidationResult(
                StoredSessionValidationStatus.Valid, diagnostic.Condition, diagnostic.Classification, diagnostic);
        }

        _currentSession = null;
        var isRejected = diagnostic.Classification is
            SessionValidationClassification.Unauthorized401 or
            SessionValidationClassification.Forbidden403 or
            SessionValidationClassification.MissingValidationKey or
            SessionValidationClassification.MissingDeviceId;
        var status = isRejected ? StoredSessionValidationStatus.Rejected : StoredSessionValidationStatus.Unavailable;
        var result = new StoredSessionValidationResult(status, diagnostic.Condition, diagnostic.Classification, diagnostic);

        // Keep the stored credential for diagnosis. Do not repeat a terminal rejection in this process.
        if (diagnostic.Classification is not (SessionValidationClassification.TransientNetworkFailure or SessionValidationClassification.Timeout))
        {
            _lastTerminalStoredValidation = result;
        }

        return result;
    }

    public O2Session? EnsureAuthenticated(bool allowInteractive, bool forceInteractive = false)
    {
        if (!forceInteractive)
        {
            var environmentSession = TryEnvironmentSession();
            if (environmentSession is not null)
            {
                var environmentDiagnostic = _validator.ValidateDetailed(environmentSession);
                if (environmentDiagnostic.IsValid)
                {
                    _currentSession = environmentSession;
                    return _currentSession;
                }

                if (environmentDiagnostic.Classification is SessionValidationClassification.Unauthorized401 or
                    SessionValidationClassification.Forbidden403 or SessionValidationClassification.MissingValidationKey or
                    SessionValidationClassification.MissingDeviceId)
                {
                    ClearEnvironmentSession();
                }
                else
                {
                    return null;
                }
            }

            var storedResult = ValidateStoredSession();
            if (storedResult.Status == StoredSessionValidationStatus.Valid)
            {
                return _currentSession;
            }

            if (storedResult.Status == StoredSessionValidationStatus.Unavailable)
            {
                return null;
            }
        }
        else
        {
            _currentSession = null;
            ClearEnvironmentSession();
        }

        if (!allowInteractive)
        {
            return null;
        }

        var capturedSession = _loginService.Login(_validator);
        if (capturedSession is not { IsAuthenticated: true })
        {
            return null;
        }

        _currentSession = capturedSession;
        _sessionStore.Save(capturedSession!);
        _lastTerminalStoredValidation = null;
        return _currentSession;
    }

    public void Logout()
    {
        _currentSession = null;
        _sessionStore.Delete();
        _lastTerminalStoredValidation = new StoredSessionValidationResult(
            StoredSessionValidationStatus.Missing, "explicit_logout", SessionValidationClassification.MissingSession);
        ClearEnvironmentSession();
        _loginService.ClearSessionCache();
    }

    private static O2Session? TryEnvironmentSession()
    {
        var validationKey = Environment.GetEnvironmentVariable(ValidationKeyVariable);
        if (string.IsNullOrWhiteSpace(validationKey))
        {
            return null;
        }

        return new O2Session
        {
            ValidationKey = validationKey.Trim(),
        };
    }

    private static void ClearEnvironmentSession()
    {
        Environment.SetEnvironmentVariable(ValidationKeyVariable, null, EnvironmentVariableTarget.Process);
        TryClearEnvironmentVariable(EnvironmentVariableTarget.User);
        TryClearEnvironmentVariable(EnvironmentVariableTarget.Machine);
    }

    private static void TryClearEnvironmentVariable(EnvironmentVariableTarget target)
    {
        try
        {
            Environment.SetEnvironmentVariable(ValidationKeyVariable, null, target);
        }
        catch
        {
            // Machine-level variables can require elevation; process and user cleanup still remove local reuse.
        }
    }
}
