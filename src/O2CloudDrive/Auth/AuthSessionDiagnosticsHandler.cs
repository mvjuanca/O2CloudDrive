using System.Diagnostics;

namespace O2CloudDrive.Auth;

public sealed class AuthSessionDiagnosticsHandler : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<AuthenticatedRequestContext> RequestContextKey =
        new("O2CloudDrive.AuthenticatedRequestContext");
    public static readonly HttpRequestOptionsKey<SetCookieMetadataSummary> ResponseSetCookiesKey =
        new("O2CloudDrive.ResponseSetCookieMetadata");

    private readonly AuthSessionLog _log;

    public AuthSessionDiagnosticsHandler(HttpMessageHandler innerHandler, AuthSessionLog log)
    {
        InnerHandler = innerHandler ?? throw new ArgumentNullException(nameof(innerHandler));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = base.Send(request, cancellationToken);
            return ObserveResponse(request, response, Stopwatch.GetElapsedTime(started));
        }
        catch (Exception exception)
        {
            ObserveFailure(request, Stopwatch.GetElapsedTime(started), exception);
            throw;
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return ObserveResponse(request, response, Stopwatch.GetElapsedTime(started));
        }
        catch (Exception exception)
        {
            ObserveFailure(request, Stopwatch.GetElapsedTime(started), exception);
            throw;
        }
    }

    private HttpResponseMessage ObserveResponse(HttpRequestMessage request, HttpResponseMessage response, TimeSpan duration)
    {
        var setCookies = AuthSessionLog.CaptureSetCookies(response.Headers);
        request.Options.Set(ResponseSetCookiesKey, setCookies);

        if (request.Options.TryGetValue(RequestContextKey, out var context) && !context.IsSessionValidation)
        {
            _log.LogAuthenticatedResponse(context, request, response, duration, setCookies);
        }

        return response;
    }

    private void ObserveFailure(HttpRequestMessage request, TimeSpan duration, Exception exception)
    {
        if (request.Options.TryGetValue(RequestContextKey, out var context) && !context.IsSessionValidation)
        {
            _log.LogAuthenticatedFailure(context, request, duration, exception);
        }
    }
}
