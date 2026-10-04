using O2CloudDrive.Auth;

var passed = 0;
var failed = 0;

void Test(string name, bool condition)
{
    if (condition)
    {
        passed++;
        Console.WriteLine("PASS " + name);
    }
    else
    {
        failed++;
        Console.WriteLine("FAIL " + name);
    }
}

var landing = new Uri("https://cloud.o2online.es/login");
var hero = new[] { "Tus contenidos siempre a salvo. Disfruta 10TB de espacio en la nube" };
var access = new[] { "Aceptar todas las cookies", "Acceder" };

Test("official landing with unique web CTA advances", O2LoginFlowPolicy.ShouldAdvanceToWebLogin(landing, hero, access, cookieConsentVisible: false));
Test("cookie consent blocks auto navigation", !O2LoginFlowPolicy.ShouldAdvanceToWebLogin(landing, hero, access, cookieConsentVisible: true));
Test("QR/app promotion alone is not selected", !O2LoginFlowPolicy.ShouldAdvanceToWebLogin(landing, new[] { "Tus contenidos siempre a salvo" }, new[] { "Obtener la App" }, false));
Test("phone form Acceder button is not auto-submitted", !O2LoginFlowPolicy.ShouldAdvanceToWebLogin(new Uri("https://t3.o2online.es/acceso/#/accessUserPassO2"), hero, new[] { "Acceder" }, false));
Test("landing marker is required", !O2LoginFlowPolicy.ShouldAdvanceToWebLogin(landing, new[] { "Otra página" }, new[] { "Acceder" }, false));
Test("one exact Acceder button is required", !O2LoginFlowPolicy.ShouldAdvanceToWebLogin(landing, hero, new[] { "Acceder", "Acceder" }, false));
Test("spoofed cloud hostname is rejected", !O2LoginFlowPolicy.ShouldAdvanceToWebLogin(new Uri("https://cloud.o2online.es.attacker.invalid/login"), hero, new[] { "Acceder" }, false));
Test("app store host is identified and blocked", O2LoginFlowPolicy.IsAppStoreUri(new Uri("https://play.google.com/store/apps/details?id=example")) && O2LoginFlowPolicy.IsAppStoreUri(new Uri("https://apps.apple.com/es/app/example")));
Test("O2 SSO popup stays in current WebView", O2LoginFlowPolicy.IsO2OwnedHttpsUri(new Uri("https://t3.o2online.es/acceso/")));
Test("external/store popup is not trusted", !O2LoginFlowPolicy.IsO2OwnedHttpsUri(new Uri("https://play.google.com/store/apps/details?id=example")));

var unsafeUrl = "https://t3.o2online.es/acceso/#/accessUserPassO2?state=state-secret&sessionData=session-secret";
var safeUrl = O2LoginFlowPolicy.SanitizeUri(unsafeUrl);
Test("diagnostic route redacts dynamic query and hash values", safeUrl.Contains("#/accessUserPassO2?[redacted]", StringComparison.Ordinal) && !safeUrl.Contains("state-secret", StringComparison.Ordinal) && !safeUrl.Contains("session-secret", StringComparison.Ordinal));

var runner = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "RealValidation.Program.cs"));
Test("human login runner has no fixed 90-second close", !runner.Contains("90000", StringComparison.Ordinal) && !runner.Contains("System.Windows.Forms.Timer", StringComparison.Ordinal));

Console.WriteLine($"Login flow tests: {passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
