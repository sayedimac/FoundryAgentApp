using System.Diagnostics;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;

namespace AgentApp.PlaywrightTests;

/// <summary>
/// Base class that starts the AgentApp dev server before each test fixture
/// and provides a headed Chromium browser so tests are visible on screen.
/// </summary>
public class PlaywrightTestBase : PageTest
{
    private Process? _serverProcess;
    private bool _serverStartedByUs;
    protected string BaseUrl { get; private set; } = null!;

    private const int Port = 5199; // dedicated test port

    /// <summary>
    /// Run tests in headed (graphical) Chromium with slow-mo so you can watch.
    /// Override via .runsettings or HEADED env var.
    /// </summary>
    public override BrowserNewContextOptions ContextOptions()
    {
        return new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
            IgnoreHTTPSErrors = true
        };
    }

    [OneTimeSetUp]
    public async Task GlobalSetup()
    {
        BaseUrl = $"http://localhost:{Port}";

        // Check if the server is already running (e.g. started manually)
        using var httpClient = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        try
        {
            var probe = await httpClient.GetAsync("/");
            if (probe.IsSuccessStatusCode)
            {
                TestContext.Out.WriteLine($"Server already running at {BaseUrl}");
                return;
            }
        }
        catch
        {
            // Not running — we'll start it
        }

        var projectPath = Path.GetFullPath(
            Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "AgentApp.csproj"));

        _serverProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"run --project \"{projectPath}\" --no-launch-profile --urls {BaseUrl}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                Environment =
                {
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                    ["ASPNETCORE_URLS"] = BaseUrl
                }
            }
        };

        _serverProcess.Start();
        _serverStartedByUs = true;

        // Wait for the server to be ready (up to 30 seconds)
        var timeout = TimeSpan.FromSeconds(30);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                var response = await httpClient.GetAsync("/");
                if (response.IsSuccessStatusCode)
                {
                    TestContext.Out.WriteLine($"Server ready at {BaseUrl} after {sw.Elapsed.TotalSeconds:F1}s");
                    return;
                }
            }
            catch
            {
                // Server not ready yet
            }
            await Task.Delay(500);
        }

        throw new Exception($"Server did not start within {timeout.TotalSeconds}s at {BaseUrl}");
    }

    [OneTimeTearDown]
    public void GlobalTeardown()
    {
        if (_serverStartedByUs && _serverProcess is { HasExited: false })
        {
            _serverProcess.Kill(entireProcessTree: true);
            _serverProcess.WaitForExit(5000);
        }
        _serverProcess?.Dispose();
    }
}
