using System.Net.Http;

namespace O2CloudDrive.Auth;

/// <summary>Applies the captured O2 device identity to an authenticated SAPI request.</summary>
public static class O2SessionRequestIdentity
{
    public const string HeaderName = "X-Deviceid";

    public static void ApplyDeviceIdHeader(HttpRequestMessage request, O2Session session)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(session);

        if (!session.IsAuthenticated)
        {
            throw new InvalidOperationException("No se puede enviar una petición SAPI sin una sesión autenticada.");
        }

        if (string.IsNullOrWhiteSpace(session.DeviceId))
        {
            throw new InvalidOperationException("La sesión O2 necesita recapturar X-Deviceid desde su página autenticada.");
        }

        request.Headers.Remove(HeaderName);
        if (!request.Headers.TryAddWithoutValidation(HeaderName, session.DeviceId))
        {
            throw new InvalidOperationException("No se pudo aplicar la identidad de dispositivo de la sesión O2.");
        }
    }
}
