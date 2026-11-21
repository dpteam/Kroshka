using System.Diagnostics;
using System.Text;
using DPT.Kroshka.Shared;

namespace DPT.Kroshka.Linux;

/// <summary>
/// Opens the chat in a browser window that looks like an application.
///
/// Why a browser rather than a native GTK/WebKit window: a WebKitGTK binding
/// means shipping against libwebkit2gtk, whose soname and API differ between
/// distributions (4.0 / 4.1 / 6.0), and it cannot be tested from a Windows build
/// machine at all. Chromium's --app mode gives a chromeless window with its own
/// WM_CLASS, which is what actually makes it feel like an app - and it works on
/// every distribution that has a browser.
///
/// The trade-off is honest: no system tray icon, and Firefox has no app mode, so
/// there it opens a normal window. Both are documented in README.
/// </summary>
public static class BrowserLauncher
{
    /// <summary>
    /// Chromium-family browsers, best first. Order matters: Chromium and Chrome
    /// give the true app window, Edge and Brave inherit it, and everything else
    /// falls back to a normal browser window.
    /// </summary>
    private static readonly string[] ChromiumCandidates =
    {
        "chromium",
        "chromium-browser",
        "google-chrome",
        "google-chrome-stable",
        "brave-browser",
        "microsoft-edge",
        "microsoft-edge-stable",
        "vivaldi",
        "vivaldi-stable"
    };

    private static readonly string[] FirefoxCandidates =
    {
        "firefox",
        "firefox-esr"
    };

    public enum Kind
    {
        None,
        Chromium,
        Firefox
    }

    /// <summary>What was launched, for logging and for the tray-less status line.</summary>
    public sealed class Result
    {
        public Kind Kind { get; init; }
        public string Executable { get; init; } = "";
        public string Detail { get; init; } = "";
        public bool Started => Kind != Kind.None;
    }

    /// <summary>
    /// Resolves an executable name against PATH. Kept separate so the ordering
    /// logic below can be tested without a browser installed.
    /// </summary>
    public static string Which(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        // An absolute path is trusted as-is; the caller asked for it by name.
        if (Path.IsPathRooted(name))
            return File.Exists(name) ? name : null;

        string path = Environment.GetEnvironmentVariable("PATH") ?? "";

        // Path.PathSeparator is ':' on Linux and ';' on Windows. Hardcoding ':'
        // would make the self-test pass on Linux and silently find nothing on a
        // Windows build machine, which is exactly where it runs.
        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// Picks the browser to use.
    ///
    ///   ""/auto/default   best available: Chromium family first, then Firefox
    ///   chromium/chrome/… that family, with the requested binary preferred
    ///   firefox           Firefox
    ///   anything else     treated as a literal executable name; if it is not on
    ///                     PATH, fall back to auto and say so in the log
    ///
    /// The last rule matters: `browser=vivaldi` should use Vivaldi when it is
    /// installed, and still open a chat window (with a warning) when it is not.
    /// Refusing to start over a browser name would be the wrong trade.
    /// </summary>
    public static (Kind kind, string executable) Resolve(string preference)
    {
        preference = (preference ?? "").Trim().ToLowerInvariant();

        if (preference is "" or "auto" or "default")
            return Auto();

        if (preference is "chromium" or "chrome" or "edge" or "brave")
            return SearchChromium(preference);

        if (preference is "firefox")
            return SearchFirefox(preference);

        // A name we know: search its family, but try the requested binary first so
        // an explicit choice is honoured rather than approximated.
        if (ChromiumCandidates.Contains(preference)) return SearchChromium(preference);
        if (FirefoxCandidates.Contains(preference)) return SearchFirefox(preference);

        // An unknown value: maybe it is a browser we do not list.
        string literal = Which(preference);
        if (literal != null)
        {
            bool firefox = preference.Contains("firefox", StringComparison.Ordinal);
            return (firefox ? Kind.Firefox : Kind.Chromium, literal);
        }

        Log.Warn("BROWSER", $"'{preference}' not found in PATH, falling back to auto");
        return Auto();
    }

    private static (Kind kind, string executable) Auto()
    {
        foreach (string name in ChromiumCandidates)
        {
            string found = Which(name);
            if (found != null) return (Kind.Chromium, found);
        }

        foreach (string name in FirefoxCandidates)
        {
            string found = Which(name);
            if (found != null) return (Kind.Firefox, found);
        }

        return (Kind.None, null);
    }

    private static (Kind kind, string executable) SearchChromium(string preferred)
    {
        // The preferred name first, then the rest of the family.
        foreach (string name in new[] { preferred }.Concat(ChromiumCandidates).Distinct())
        {
            string found = Which(name);
            if (found != null) return (Kind.Chromium, found);
        }

        return (Kind.None, null);
    }

    private static (Kind kind, string executable) SearchFirefox(string preferred)
    {
        foreach (string name in new[] { preferred }.Concat(FirefoxCandidates).Distinct())
        {
            string found = Which(name);
            if (found != null) return (Kind.Firefox, found);
        }

        return (Kind.None, null);
    }

    /// <summary>
    /// Opens <paramref name="url"/> in an app-like window.
    /// </summary>
    /// <param name="profileDir">
    /// A dedicated Chromium profile. Using the user's own profile makes a second
    /// launch attach to the running browser as a tab instead of opening a window,
    /// which silently breaks "click the icon to see the chat".
    /// </param>
    public static Result Open(string url, LinuxClientConfig config, string profileDir)
    {
        (Kind kind, string executable) = Resolve(config.Browser);

        if (kind == Kind.None)
        {
            Log.Warn("BROWSER", "no supported browser found in PATH");
            return new Result { Kind = Kind.None, Detail = "no browser" };
        }

        try
        {
            if (kind == Kind.Chromium)
            {
                List<string> args = new()
                {
                    "--app=" + url,
                    "--class=Kroshka",
                    "--user-data-dir=" + profileDir,
                    "--no-first-run",
                    "--no-default-browser-check",
                    "--disable-features=Translate"
                };

                (int width, int height) = ParseSize(config.WindowSize);
                if (width > 0 && height > 0)
                    args.Add($"--window-size={width},{height}");

                StartDetached(executable, args);
                Log.Info("BROWSER", $"chromium app window: {executable}");
                return new Result { Kind = Kind.Chromium, Executable = executable, Detail = url };
            }

            // Firefox has no --app; a new window is the closest honest equivalent.
            StartDetached(executable, new List<string> { "--new-window", url });
            Log.Info("BROWSER", $"firefox window: {executable}");
            return new Result { Kind = Kind.Firefox, Executable = executable, Detail = url };
        }
        catch (Exception ex)
        {
            Log.Warn("BROWSER", "cannot start " + executable + ": " + ex.Message);
            return new Result { Kind = Kind.None, Detail = ex.Message };
        }
    }

    /// <summary>
    /// Launches without inheriting our stdio, so the browser does not hold the
    /// terminal open and the launcher can exit cleanly when it is a one-shot.
    /// </summary>
    private static void StartDetached(string executable, List<string> args)
    {
        ProcessStartInfo info = new()
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };

        foreach (string arg in args) info.ArgumentList.Add(arg);

        Process process = Process.Start(info);

        // Drain the pipes so a chatty browser cannot fill its buffer and block.
        try
        {
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch { }
    }

    /// <summary>Parses "1200x820" into a pair; anything unparseable yields zeros.</summary>
    public static (int width, int height) ParseSize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (0, 0);

        string[] parts = text.ToLowerInvariant().Split('x');
        if (parts.Length != 2) return (0, 0);

        if (!int.TryParse(parts[0].Trim(), out int width)) return (0, 0);
        if (!int.TryParse(parts[1].Trim(), out int height)) return (0, 0);

        // Guard against a typo turning into a 1x1 window nobody can use.
        if (width < 480 || height < 400 || width > 8000 || height > 8000) return (0, 0);

        return (width, height);
    }

    /// <summary>Prints the URL as a fallback when nothing could be launched.</summary>
    public static string FallbackHint(string url)
    {
        StringBuilder text = new();
        text.AppendLine();
        text.AppendLine("  Откройте вручную:");
        text.AppendLine("    " + url);
        text.AppendLine();
        text.AppendLine("  С этого компьютера, или с телефона в той же сети.");
        return text.ToString();
    }
}
