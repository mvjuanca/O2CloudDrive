using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using O2CloudDrive.Ui;

namespace O2CloudDrive.Auth;

public sealed class O2LoginForm : Form
{
    private static readonly Regex ValidationKeyRegex = new(
        "validationkey[^A-Za-z0-9._-]+([A-Za-z0-9._-]{8,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Uri _loginUri;
    private readonly O2SessionValidator _validator;
    private readonly string? _userDataFolder;
    private readonly Action<string>? _diagnosticLog;
    private readonly AuthSessionLog? _authSessionLog;
    private readonly WebView2 _webView = new() { Dock = DockStyle.Fill };
    private readonly Label _statusLabel = new()
    {
        Dock = DockStyle.Fill,
        AutoEllipsis = true,
        Text = "Cargando login oficial de O2 Cloud...",
    };

    private readonly System.Windows.Forms.Timer _captureTimer = new() { Interval = 2000 };
    private readonly HashSet<string> _seenUrls = new(StringComparer.OrdinalIgnoreCase);
    private bool _capturing;
    private bool _cookieMetadataLogged;
    private bool _webLoginAdvanceInFlight;
    private bool _webLoginAdvanceStarted;
    private string? _lastValidationKey;
    private string? _lastDeviceId;
    private IReadOnlyCollection<WebViewCookieMetadata> _lastCookieMetadata = Array.Empty<WebViewCookieMetadata>();

    public O2LoginForm(
        Uri loginUri,
        O2SessionValidator validator,
        string? userDataFolder = null,
        Action<string>? diagnosticLog = null,
        AuthSessionLog? authSessionLog = null)
    {
        _loginUri = loginUri;
        _validator = validator;
        _userDataFolder = userDataFolder;
        _diagnosticLog = diagnosticLog;
        _authSessionLog = authSessionLog;

        Text = "O2 Cloud - Login";
        Width = 1100;
        Height = 760;
        MinimizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.Load();

        var topPanel = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            ColumnCount = 2,
            Padding = new Padding(8, 8, 8, 4),
        };
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        topPanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var cancelButton = new Button
        {
            Text = "Cancelar",
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(8, 0, 0, 0),
        };
        cancelButton.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        topPanel.Controls.Add(_statusLabel, 0, 0);
        topPanel.Controls.Add(cancelButton, 1, 0);

        Controls.Add(_webView);
        Controls.Add(topPanel);

        _captureTimer.Tick += async (_, _) =>
        {
            await AcceptCookieConsentAsync();
            await TryAdvanceToWebLoginAsync();
            await CaptureAndValidateAsync();
        };
        Shown += async (_, _) => await InitializeWebViewAsync();
        FormClosed += (_, _) =>
        {
            _captureTimer.Stop();
            _webView.Dispose();
        };
    }

    public O2Session? CapturedSession { get; private set; }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var userDataFolder = _userDataFolder ?? O2WebViewProfile.UserDataFolder;
            var profileExisted = Directory.Exists(userDataFolder);
            Directory.CreateDirectory(userDataFolder);
            _authSessionLog?.LogWebViewProfileState(userDataFolder, profileExisted);

            var environmentOptions = new CoreWebView2EnvironmentOptions
            {
                EnableTrackingPrevention = false,
            };
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, environmentOptions);
            await _webView.EnsureCoreWebView2Async(environment);

            _webView.CoreWebView2.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.None;
            _webView.CoreWebView2.Profile.IsPasswordAutosaveEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(LoginBootstrapScript);
            _webView.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var popupUri) &&
                    O2LoginFlowPolicy.IsO2OwnedHttpsUri(popupUri) &&
                    !O2LoginFlowPolicy.IsAppStoreUri(popupUri))
                {
                    DiagnosticLog($"POPUP route={O2LoginFlowPolicy.SanitizeUri(args.Uri)} routed_in_current_view=true");
                    _webView.CoreWebView2.Navigate(args.Uri);
                }
                else
                {
                    DiagnosticLog($"POPUP_BLOCKED route={O2LoginFlowPolicy.SanitizeUri(args.Uri)}");
                }
            };
            _webView.CoreWebView2.NavigationStarting += (_, args) =>
            {
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var navigationUri) &&
                    O2LoginFlowPolicy.IsAppStoreUri(navigationUri))
                {
                    args.Cancel = true;
                    _statusLabel.Text = "Se mantiene el acceso web de O2 Cloud; no se abrirá una tienda de aplicaciones.";
                    DiagnosticLog($"APP_STORE_NAVIGATION_BLOCKED route={O2LoginFlowPolicy.SanitizeUri(args.Uri)}");
                    return;
                }

                TrackUrl(args.Uri);
                DiagnosticLog($"NAV_START redirected={args.IsRedirected} route={O2LoginFlowPolicy.SanitizeUri(args.Uri)}");
                _statusLabel.Text = "Login oficial de O2 Cloud cargando...";
                if (TryExtractValidationKey(args.Uri, out var validationKey))
                {
                    _lastValidationKey = validationKey;
                    BeginInvoke(async () => await CaptureAndValidateAsync(validationKey));
                }
            };
            _webView.CoreWebView2.WebResourceResponseReceived += (_, args) =>
            {
                if (args.Response.StatusCode is >= 200 and < 300 &&
                    Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var apiUri) &&
                    apiUri.Scheme == Uri.UriSchemeHttps &&
                    apiUri.IdnHost.Equals("cloud.o2online.es", StringComparison.OrdinalIgnoreCase) &&
                    apiUri.AbsolutePath.StartsWith("/sapi/", StringComparison.Ordinal) &&
                    args.Request.Headers.Contains("X-Deviceid"))
                {
                    _lastDeviceId = args.Request.Headers.GetHeader("X-Deviceid");
                    if (TryExtractValidationKey(args.Request.Uri, out var apiValidationKey))
                    {
                        _lastValidationKey = apiValidationKey;
                    }
                }

                if (!string.Equals(args.Request.Method, "GET", StringComparison.OrdinalIgnoreCase) ||
                    !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var responseUri) ||
                    !IsLoginHost(responseUri))
                {
                    return;
                }

                DiagnosticLog($"HTTP_RESPONSE status={args.Response.StatusCode} route={O2LoginFlowPolicy.SanitizeUri(args.Request.Uri)}");
            };
            _webView.CoreWebView2.NavigationCompleted += async (_, args) =>
            {
                DiagnosticLog($"NAV_COMPLETE success={args.IsSuccess} error={args.WebErrorStatus}");
                await LogSafePageSnapshotAsync();
                _statusLabel.Text = "Esperando telefono, contrasena y codigo SMS de O2 Cloud...";
                await AcceptCookieConsentAsync();
                await TryAdvanceToWebLoginAsync();
                await CaptureAndValidateAsync();
                if (CapturedSession is null)
                {
                    _statusLabel.Text = "Introduce telefono, contrasena y SMS. La app capturara la sesion cuando O2 la valide.";
                }
            };
            _webView.CoreWebView2.DOMContentLoaded += async (_, _) =>
            {
                await LogSafePageSnapshotAsync();
                await AcceptCookieConsentAsync();
                await TryAdvanceToWebLoginAsync();
                await CaptureAndValidateAsync();
            };

            _captureTimer.Start();
            _statusLabel.Text = "Abriendo login oficial de O2 Cloud...";
            _webView.CoreWebView2.Navigate(_loginUri.ToString());
        }
        catch (Exception ex)
        {
            _statusLabel.Text = "No se pudo inicializar WebView2.";
            MessageBox.Show(this, ex.Message, "O2 Cloud Login", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private async Task AcceptCookieConsentAsync()
    {
        if (_webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var result = await _webView.ExecuteScriptAsync(CookieConsentScript);
            if (string.Equals(result, "true", StringComparison.OrdinalIgnoreCase))
            {
                DiagnosticLog("COOKIE_CONSENT_ACCEPTED_BY_SCOPED_COOKIE_CONTROL");
            }
        }
        catch
        {
            // The login page can navigate while the script is running.
        }
    }

    private async Task TryAdvanceToWebLoginAsync()
    {
        if (_webLoginAdvanceStarted || _webLoginAdvanceInFlight || _webView.CoreWebView2 is null ||
            !Uri.TryCreate(_webView.CoreWebView2.Source, UriKind.Absolute, out var currentUri))
        {
            return;
        }

        _webLoginAdvanceInFlight = true;
        try
        {
            var raw = await _webView.ExecuteScriptAsync(LandingProbeScript);
            using var state = JsonDocument.Parse(raw);
            var root = state.RootElement;
            var headings = ReadStringArray(root, "headings");
            var buttons = ReadStringArray(root, "buttons");
            var consentVisible = root.TryGetProperty("cookieConsentVisible", out var consent) && consent.ValueKind == JsonValueKind.True;

            if (!O2LoginFlowPolicy.ShouldAdvanceToWebLogin(currentUri, headings, buttons, consentVisible))
            {
                return;
            }

            var result = await _webView.ExecuteScriptAsync(ClickWebAccessButtonScript);
            if (string.Equals(result.Trim('"'), "clicked", StringComparison.Ordinal))
            {
                _webLoginAdvanceStarted = true;
                DiagnosticLog("WEB_ACCESS_CTA_CLICKED; dynamic O2 navigation retained; no fixed SSO URL");
            }
        }
        catch
        {
            // The page can navigate while its public login landing is being inspected.
        }
        finally
        {
            _webLoginAdvanceInFlight = false;
        }
    }

    private async Task LogSafePageSnapshotAsync()
    {
        if (_diagnosticLog is null || _webView.CoreWebView2 is null)
        {
            return;
        }

        try
        {
            var raw = await _webView.ExecuteScriptAsync(PageDiagnosticScript);
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var flags = root.GetProperty("flags");
            bool Flag(string name) => flags.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
            var snapshot = new
            {
                route = O2LoginFlowPolicy.SanitizeUri(GetJsonString(root, "route")),
                title = GetJsonString(root, "title") ?? string.Empty,
                userAgent = GetJsonString(root, "userAgent") ?? string.Empty,
                viewport = GetJsonString(root, "viewport") ?? string.Empty,
                webAccess = Flag("webAccess"),
                phonePrompt = Flag("phonePrompt"),
                smsPrompt = Flag("smsPrompt"),
                appPromo = Flag("appPromo"),
                qr = Flag("qr"),
                emailPassword = Flag("emailPassword"),
                cookieNotice = Flag("cookieNotice"),
                inputTypes = ReadStringArray(root, "inputTypes"),
            };

            DiagnosticLog("PAGE " + JsonSerializer.Serialize(snapshot));
        }
        catch
        {
            // Diagnostics must not turn a page transition into a login failure.
        }
    }

    private static string[] ReadStringArray(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? string.Empty)
            .ToArray();
    }

    private static string? GetJsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsLoginHost(Uri uri) =>
        string.Equals(uri.IdnHost, "cloud.o2online.es", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(uri.IdnHost, "t3.o2online.es", StringComparison.OrdinalIgnoreCase);

    private void DiagnosticLog(string message)
    {
        try
        {
            _diagnosticLog?.Invoke(message);
        }
        catch
        {
            // Diagnostics must never interrupt authentication.
        }
    }

    private async Task CaptureAndValidateAsync(string? forcedValidationKey = null)
    {
        if (_capturing || _webView.CoreWebView2 is null)
        {
            return;
        }

        _capturing = true;
        try
        {
            var session = await CaptureSessionAsync(forcedValidationKey);
            if (session is not { IsAuthenticated: true })
            {
                return;
            }

            _statusLabel.Text = "Sesion detectada. Validando con O2 Cloud...";
            if (!_cookieMetadataLogged)
            {
                _authSessionLog?.LogWebViewCookieMetadata(_lastCookieMetadata, session.Cookies.Count);
                _cookieMetadataLogged = true;
            }

            var validation = await Task.Run(() => _validator.ValidateDetailed(session));
            if (validation.Classification != SessionValidationClassification.Accepted)
            {
                _statusLabel.Text = "Sesion detectada, esperando a que O2 termine de validarla...";
                return;
            }

            CapturedSession = session;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch
        {
            _statusLabel.Text = "Esperando a que O2 Cloud complete el login...";
        }
        finally
        {
            _capturing = false;
        }
    }

    private async Task<O2Session?> CaptureSessionAsync(string? forcedValidationKey)
    {
        var state = await ReadBrowserStateAsync();
        var serializedState = state?.GetRawText() ?? string.Empty;
        var validationKey = FirstNonEmpty(
            forcedValidationKey,
            _lastValidationKey,
            ReadString(state, "validationKey"),
            ExtractValidationKey(serializedState));

        if (string.IsNullOrWhiteSpace(validationKey))
        {
            return null;
        }

        var cookies = await ReadCookiesAsync(ReadString(state, "cookie"));
        var userAgent = ReadString(state, "userAgent") ?? string.Empty;

        return new O2Session
        {
            ValidationKey = validationKey,
            Cookies = cookies,
            UserAgent = userAgent,
            DeviceId = _lastDeviceId ?? string.Empty,
        };
    }

    private async Task<JsonElement?> ReadBrowserStateAsync()
    {
        var raw = await _webView.ExecuteScriptAsync(CaptureScript);
        if (string.IsNullOrWhiteSpace(raw) || raw == "null")
        {
            return null;
        }

        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private async Task<Dictionary<string, string>> ReadCookiesAsync(string? documentCookie)
    {
        var cookies = ParseCookieHeader(documentCookie);
        var metadata = new List<WebViewCookieMetadata>();
        var webViewCookies = await _webView.CoreWebView2.CookieManager.GetCookiesAsync("https://cloud.o2online.es/");
        foreach (var cookie in webViewCookies)
        {
            if (!cookie.Domain.Contains("o2online.es", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(cookie.Name))
            {
                DateTimeOffset? expiresUtc = null;
                if (!cookie.IsSession && cookie.Expires != DateTime.MinValue)
                {
                    var expires = cookie.Expires.Kind switch
                    {
                        DateTimeKind.Utc => cookie.Expires,
                        DateTimeKind.Local => cookie.Expires.ToUniversalTime(),
                        _ => DateTime.SpecifyKind(cookie.Expires, DateTimeKind.Utc),
                    };
                    expiresUtc = new DateTimeOffset(expires);
                }

                metadata.Add(new WebViewCookieMetadata(
                    cookie.Name,
                    cookie.Domain,
                    cookie.Path,
                    cookie.IsHttpOnly,
                    cookie.IsSecure,
                    cookie.IsSession,
                    expiresUtc));

                if (!string.IsNullOrWhiteSpace(cookie.Value))
                {
                    cookies[cookie.Name] = cookie.Value;
                }
            }
        }

        _lastCookieMetadata = metadata;
        return cookies;
    }

    private static Dictionary<string, string> ParseCookieHeader(string? cookieHeader)
    {
        var cookies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(cookieHeader))
        {
            return cookies;
        }

        foreach (var part in cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var index = part.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var name = part[..index].Trim();
            var value = part[(index + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(value))
            {
                cookies[name] = value;
            }
        }

        return cookies;
    }

    private void TrackUrl(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
        {
            return;
        }

        _seenUrls.Add(uri);
        if (TryExtractValidationKey(uri, out var validationKey))
        {
            _lastValidationKey = validationKey;
        }
    }

    private static bool TryExtractValidationKey(string text, out string validationKey)
    {
        validationKey = ExtractValidationKey(text) ?? string.Empty;
        return !string.IsNullOrWhiteSpace(validationKey);
    }

    private static string? ExtractValidationKey(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = ValidationKeyRegex.Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ReadString(JsonElement? element, string name)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return element.Value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
    }

    private const string CaptureScript = """
        (() => {
          const readStorage = (storage) => {
            const out = {};
            try {
              for (let i = 0; i < storage.length; i++) {
                const key = storage.key(i);
                out[key] = storage.getItem(key);
              }
            } catch (_) {}
            return out;
          };
          const resources = [];
          try {
            for (const entry of performance.getEntriesByType('resource')) {
              if (entry && entry.name) resources.push(entry.name);
            }
          } catch (_) {}
          const payload = {
            url: location.href,
            cookie: document.cookie || '',
            userAgent: navigator.userAgent || '',
            localStorage: readStorage(localStorage),
            sessionStorage: readStorage(sessionStorage),
            seenUrls: Array.isArray(window.__o2CloudDriveSeenUrls) ? window.__o2CloudDriveSeenUrls.slice(-100) : [],
            resources: resources.slice(-100)
          };
          const text = JSON.stringify(payload);
          const match = text.match(/validationkey[^A-Za-z0-9._-]+([A-Za-z0-9._-]{8,})/i);
          payload.validationKey = match ? match[1] : '';
          return payload;
        })();
        """;

    private const string LoginBootstrapScript = """
        (() => {
          if (window.__o2CloudDriveLoginPatchInstalled) return;
          window.__o2CloudDriveLoginPatchInstalled = true;
          window.__o2CloudDriveSeenUrls = window.__o2CloudDriveSeenUrls || [];
          const remember = (value) => {
            try {
              const url = typeof value === 'string'
                ? value
                : value && value.url
                  ? value.url
                  : '';
              if (url && !window.__o2CloudDriveSeenUrls.includes(url)) {
                window.__o2CloudDriveSeenUrls.push(url);
                if (window.__o2CloudDriveSeenUrls.length > 200) {
                  window.__o2CloudDriveSeenUrls.shift();
                }
              }
            } catch (_) {}
          };
          const originalFetch = window.fetch;
          if (originalFetch) {
            window.fetch = function(input, init) {
              remember(input);
              return originalFetch.apply(this, arguments);
            };
          }
          try {
            const originalOpen = XMLHttpRequest.prototype.open;
            XMLHttpRequest.prototype.open = function(method, url) {
              remember(url);
              return originalOpen.apply(this, arguments);
            };
          } catch (_) {}
          const acceptCookies = () => {
            try {
              const clean = (value) => String(value || '')
                .normalize('NFD')
                .replace(/[\u0300-\u036f]/g, '')
                .replace(/\s+/g, ' ')
                .trim()
                .toLowerCase();
              const visible = (el) => {
                try {
                  const rect = el.getBoundingClientRect();
                  const style = getComputedStyle(el);
                  return rect.width > 0 && rect.height > 0 &&
                    style.visibility !== 'hidden' &&
                    style.display !== 'none' &&
                    style.opacity !== '0';
                } catch (_) {
                  return false;
                }
              };
              const inCookieConsent = (el) => {
                for (let node = el, depth = 0; node && depth < 7; node = node.parentElement, depth++) {
                  const marker = `${node.id || ''} ${typeof node.className === 'string' ? node.className : ''} ${node.getAttribute?.('role') || ''}`;
                  const text = clean(node.innerText || node.textContent || '');
                  if (/cookie|consent|onetrust|cookiebot/i.test(marker) ||
                      (text.includes('utiliza cookies y tecnologias similares') && text.includes('configurar cookies'))) return true;
                }
                return false;
              };
              const selectors = [
                '#onetrust-accept-btn-handler',
                '#CybotCookiebotDialogBodyLevelButtonLevelOptinAllowAll',
                '[data-testid="uc-accept-all-button"]',
                '[data-testid="cookie-accept-all"]'
              ];
              for (const selector of selectors) {
                for (const el of Array.from(document.querySelectorAll(selector))) {
                  if (visible(el) && inCookieConsent(el)) {
                    el.click();
                    return true;
                  }
                }
              }
              const controls = Array.from(document.querySelectorAll('button,a,input[type=button],input[type=submit],[role=button]'));
              for (const el of controls) {
                const text = clean(el.value || el.innerText || el.textContent || el.getAttribute('aria-label') || el.getAttribute('title') || '');
                if (visible(el) && text === 'aceptar todas las cookies' && inCookieConsent(el)) {
                  el.click();
                  return true;
                }
              }
            } catch (_) {}
            return false;
          };
          window.__o2CloudDriveAcceptCookies = acceptCookies;
          if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', acceptCookies);
          } else {
            acceptCookies();
          }
          setInterval(acceptCookies, 700);
        })();
        """;

    private const string CookieConsentScript = """
        (() => {
          try {
            if (window.__o2CloudDriveAcceptCookies) return window.__o2CloudDriveAcceptCookies();
          } catch (_) {}
          return false;
        })();
        """;

    private const string LandingProbeScript = """
        (() => {
          const visible = (el) => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el); return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none' && s.opacity !== '0'; };
          const headings = Array.from(document.querySelectorAll('h1,h2,[role="heading"]')).filter(visible).map(el => el.innerText || el.textContent || '');
          const buttons = Array.from(document.querySelectorAll('button,[role="button"]')).filter(visible).map(el => el.innerText || el.textContent || el.getAttribute('aria-label') || '');
          const cookieConsentVisible = Array.from(document.querySelectorAll('[role="dialog"],#onetrust-banner-sdk,#CybotCookiebotDialog,[class*="cookie" i],[id*="cookie" i]')).some(el => visible(el) && /utiliza cookies/i.test(el.innerText || el.textContent || ''));
          return { headings, buttons, cookieConsentVisible };
        })();
        """;

    private const string ClickWebAccessButtonScript = """
        (() => {
          const clean = (value) => String(value || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/\s+/g, ' ').trim().toLowerCase();
          const visible = (el) => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el); return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none' && s.opacity !== '0'; };
          const heading = Array.from(document.querySelectorAll('h1,h2,[role="heading"]')).some(el => visible(el) && clean(el.innerText || el.textContent).includes('tus contenidos siempre a salvo'));
          const cookie = Array.from(document.querySelectorAll('[role="dialog"],#onetrust-banner-sdk,#CybotCookiebotDialog,[class*="cookie" i],[id*="cookie" i]')).some(el => visible(el) && /utiliza cookies/i.test(el.innerText || el.textContent || ''));
          const buttons = Array.from(document.querySelectorAll('button,[role="button"]')).filter(el => visible(el) && clean(el.innerText || el.textContent || el.getAttribute('aria-label')) === 'acceder');
          if (location.origin === 'https://cloud.o2online.es' && location.pathname.replace(/\/+$/, '') === '/login' && heading && !cookie && buttons.length === 1) {
            buttons[0].click();
            return 'clicked';
          }
          return 'not-eligible';
        })();
        """;

    private const string PageDiagnosticScript = """
        (() => {
          const text = document.body?.innerText || '';
          const clean = (value) => String(value || '').normalize('NFD').replace(/[\u0300-\u036f]/g, '').toLowerCase();
          const flags = {
            webAccess: Array.from(document.querySelectorAll('button,[role="button"]')).some(el => clean(el.innerText || el.textContent || el.getAttribute('aria-label') || '').trim() === 'acceder'),
            phonePrompt: /introduce tu numero de movil/i.test(clean(text)),
            smsPrompt: /te enviaremos un codigo por sms/i.test(clean(text)),
            appPromo: /obtener la app/i.test(clean(text)),
            qr: /\bqr\b/i.test(text) || Array.from(document.images).some(el => /qr/i.test(el.alt || '')),
            emailPassword: /inicia sesion con email y contrasena/i.test(clean(text)),
            cookieNotice: /utiliza cookies y tecnologias similares/i.test(clean(text))
          };
          const hashRoute = location.hash ? location.hash.split('?')[0] : '';
          return {
            route: location.origin + location.pathname + hashRoute + (location.search || location.hash.includes('?') ? '?[redacted]' : ''),
            title: document.title || '',
            userAgent: navigator.userAgent || '',
            viewport: `${innerWidth}x${innerHeight}`,
            flags,
            inputTypes: Array.from(document.querySelectorAll('input')).map(el => el.type || 'text')
          };
        })();
        """;
}
