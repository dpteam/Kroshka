using System.Text;
using DPT.Kroshka.Server.Storage;
using DPT.Kroshka.Server.Web;
using DPT.Kroshka.Shared;

namespace DPT.Kroshka.Server;

/// <summary>
/// Owns the Kestrel hosts and the periodic maintenance loop. Two listeners: the
/// public one (SPA + /api + /ws) and the admin one, so the admin surface simply does
/// not exist on the public port.
/// </summary>
public sealed class App : IAsyncDisposable
{
    public static readonly DateTime StartedUtc = DateTime.UtcNow;
    public static KroshkaConfig CurrentConfig { get; private set; }

    /// <summary>
    /// Cache-busting token for asset URLs, derived from the newest write time in web/.
    /// Recomputed on demand (throttled) rather than once at startup: a developer who
    /// edits a script must not keep serving the old one under the same ?v= token.
    /// </summary>
    public static string BuildStamp
    {
        get
        {
            string root = CurrentConfig?.WebRootPath;
            if (string.IsNullOrEmpty(root)) return Protocol.Version;

            // One scan per second at most; the shell is not a hot path but a burst of
            // parallel requests should not each walk the tree.
            if (_stampRoot == root && (DateTime.UtcNow - _stampAt).TotalSeconds < 1)
                return _stamp;

            _stampRoot = root;
            _stampAt = DateTime.UtcNow;
            _stamp = ComputeBuildStamp(root);
            return _stamp;
        }
    }

    private static string _stamp = Protocol.Version;
    private static string _stampRoot;
    private static DateTime _stampAt = DateTime.MinValue;

    private readonly KroshkaConfig _cfg;
    private readonly KroshkaStore _store;
    private readonly BlobStore _blobs;
    private readonly WsHub _hub;
    private readonly Api _api;
    private readonly AdminApi _adminApi;

    private WebApplication _public;
    private WebApplication _admin;
    private CancellationTokenSource _cts;
    private Task _maintenance;

    public int PublicPort { get; private set; }
    public int AdminPort { get; private set; }

    public App(KroshkaConfig cfg)
    {
        _cfg = cfg;
        CurrentConfig = cfg;

        _store = new KroshkaStore(cfg);
        _blobs = new BlobStore(cfg);
        _hub = new WsHub(cfg, _store);
        _api = new Api(cfg, _store, _blobs, _hub);
        _adminApi = new AdminApi(cfg, _store, _blobs, _hub);
    }

    /// <summary>
    /// Newest write time in the web tree, encoded as a short token. Cheap enough to do
    /// once at startup and stable across restarts that change nothing.
    /// </summary>
    private static string ComputeBuildStamp(string webRoot)
    {
        try
        {
            if (!Directory.Exists(webRoot)) return Protocol.Version;

            long newest = 0;
            foreach (string file in Directory.EnumerateFiles(webRoot, "*", SearchOption.AllDirectories))
            {
                try
                {
                    long ticks = File.GetLastWriteTimeUtc(file).Ticks;
                    if (ticks > newest) newest = ticks;
                }
                catch { }
            }

            return newest == 0
                ? Protocol.Version
                : Protocol.Version + "-" + (newest % 100_000_000).ToString("x");
        }
        catch
        {
            return Protocol.Version;
        }
    }

    public KroshkaStore Store => _store;
    public WsHub Hub => _hub;

    // ================================================================== start

    public async Task<bool> StartAsync()
    {
        _store.Load();

        if (!TryStartPublic(out int port, out string error))
        {
            Log.Error("APP", "public listener failed: " + error);
            return false;
        }
        PublicPort = port;

        if (_cfg.AdminEnabled)
        {
            if (TryStartAdmin(out int adminPort, out string adminError))
                AdminPort = adminPort;
            else
                Log.Warn("APP", "admin board not started: " + adminError);
        }

        _cts = new CancellationTokenSource();
        _maintenance = Task.Run(() => MaintenanceLoopAsync(_cts.Token));

        Log.Info("APP", $"public  http://{_cfg.Bind}:{PublicPort}/");
        if (AdminPort > 0) Log.Info("APP", $"admin   http://{_cfg.AdminBind}:{AdminPort}/admin");

        foreach (string warning in _cfg.Warnings) Log.Warn("CFG", warning);

        await Task.CompletedTask;
        return true;
    }

    private bool TryStartPublic(out int port, out string error)
    {
        port = 0;
        error = "";

        foreach (int candidate in new[] { _cfg.Port }.Concat(_cfg.PortFallbacks))
        {
            try
            {
                _public = BuildHost(candidate, _cfg.Bind, admin: false);
                _public.StartAsync().GetAwaiter().GetResult();
                port = candidate;

                if (candidate != _cfg.Port)
                    Log.Warn("APP", $"port {_cfg.Port} busy, listening on {candidate} instead");

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { _public?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
                _public = null;
            }
        }

        return false;
    }

    private bool TryStartAdmin(out int port, out string error)
    {
        port = 0;
        error = "";

        foreach (int candidate in new[] { _cfg.AdminPort }.Concat(_cfg.AdminPortFallbacks))
        {
            try
            {
                _admin = BuildHost(candidate, _cfg.AdminBind, admin: true);
                _admin.StartAsync().GetAwaiter().GetResult();
                port = candidate;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                try { _admin?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
                _admin = null;
            }
        }

        return false;
    }

    private WebApplication BuildHost(int port, string bind, bool admin)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>(),
            ContentRootPath = _cfg.BaseDir,
            EnvironmentName = "Production"
        });

        builder.Logging.ClearProviders();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = (long)_cfg.MaxUploadMb * 1024 * 1024 + (8 * 1024 * 1024);
            options.Limits.MaxConcurrentConnections = 512;
            options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
            options.AddServerHeader = false;

            if (bind is "0.0.0.0" or "*" or "+")
            {
                options.ListenAnyIP(port);
            }
            else if (System.Net.IPAddress.TryParse(bind, out System.Net.IPAddress ip))
            {
                options.Listen(ip, port);
            }
            else
            {
                options.ListenAnyIP(port);
            }
        });

        WebApplication app = builder.Build();

        if (admin)
        {
            ConfigureAdminHost(app);
        }
        else
        {
            ConfigurePublicHost(app);
        }

        return app;
    }

    private void ConfigurePublicHost(WebApplication app)
    {
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        });

        // A browser client may live on a different origin than the server (a page
        // opened from a file, a reverse proxy, another port on the LAN). The chat
        // authenticates with a bearer token, not a cookie, so a wildcard origin is
        // safe here: without it, "connect to another server" simply cannot work in
        // a browser, and that is a documented feature.
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["Access-Control-Allow-Origin"] = "*";
            ctx.Response.Headers["Access-Control-Allow-Headers"] = "Authorization, Content-Type";
            ctx.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, PATCH, DELETE, OPTIONS";
            ctx.Response.Headers["Access-Control-Max-Age"] = "600";

            if (HttpMethods.IsOptions(ctx.Request.Method))
            {
                ctx.Response.StatusCode = 204;
                return;
            }

            await next();
        });

        // One line per request, never the body and never a secret.
        app.Use(async (ctx, next) =>
        {
            DateTime start = DateTime.UtcNow;
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                Log.Error("HTTP", $"{ctx.Request.Method} {ctx.Request.Path}: {ex}");
                if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 500;
            }
            finally
            {
                long ms = (long)(DateTime.UtcNow - start).TotalMilliseconds;
                Log.Api("HTTP", $"{ctx.Request.Method} {ctx.Request.Path}{ctx.Request.QueryString} -> {ctx.Response.StatusCode} {ms}ms");
            }
        });

        app.Use(BufferJsonBody);
        _api.Map(app);
        ServeSpa(app);
    }

    /// <summary>
    /// Kestrel refuses synchronous reads on the request body, and the handlers prefer a
    /// plain string over async plumbing. Buffering small JSON payloads here keeps both
    /// true: the body is read once, asynchronously, and handed to the handlers as text.
    ///
    /// Multipart uploads are deliberately skipped - they are large and streamed.
    /// </summary>
    private static async Task BufferJsonBody(HttpContext ctx, Func<Task> next)
    {
        string contentType = ctx.Request.ContentType ?? "";
        bool isJson = contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase);
        bool hasBody = ctx.Request.ContentLength is > 0 and < 4 * 1024 * 1024;

        if (isJson && hasBody)
        {
            try
            {
                ctx.Request.EnableBuffering();
                using StreamReader reader = new(ctx.Request.Body, System.Text.Encoding.UTF8, false, 4096, leaveOpen: true);
                string body = await reader.ReadToEndAsync();
                ctx.Items[BufferedBodyKey] = body;

                // Rewind so a handler that still calls DeserializeAsync sees the payload.
                ctx.Request.Body.Position = 0;
            }
            catch (Exception ex)
            {
                Log.Warn("HTTP", "cannot buffer body: " + ex.Message);
            }
        }

        await next();
    }

    internal const string BufferedBodyKey = "kroshka.rawBody";

    private void ConfigureAdminHost(WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            ctx.Response.Headers["X-Kroshka-Admin"] = "1";

            if (_cfg.AdminLocalOnly && !IsLocal(ctx))
            {
                ctx.Response.StatusCode = 403;
                await ctx.Response.WriteAsync("admin board is local only");
                return;
            }

            try
            {
                await next();
            }
            catch (Exception ex)
            {
                Log.Error("ADMIN", $"{ctx.Request.Method} {ctx.Request.Path}: {ex}");
                if (!ctx.Response.HasStarted) ctx.Response.StatusCode = 500;
            }
        });

        app.Use(BufferJsonBody);
        _adminApi.Map(app);

        // The admin SPA lives at /admin so the board can reuse the public assets.
        app.MapGet("/", ctx =>
        {
            ctx.Response.Redirect("/admin");
            return Task.CompletedTask;
        });

        ServeAdminSpa(app);
    }

    private static bool IsLocal(HttpContext ctx)
    {
        System.Net.IPAddress ip = ctx.Connection.RemoteIpAddress;
        if (ip == null) return true;
        if (System.Net.IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.ToString() is "0.0.0.0" or "::";
    }

    // ==================================================================== SPA

    private void ServeSpa(WebApplication app)
    {
        string root = _cfg.WebRootPath;

        // The shell is rewritten on every request so the ?v= token always matches the
        // files on disk. Assets themselves are then safe to cache for a long time.
        app.Use(async (ctx, next) =>
        {
            string path = ctx.Request.Path.Value ?? "/";
            bool isShell = path == "/" || path.Equals("/index.html", StringComparison.OrdinalIgnoreCase);

            if (!isShell || !HttpMethods.IsGet(ctx.Request.Method))
            {
                await next();
                return;
            }

            await ServeShellAsync(ctx, Path.Combine(root, "index.html"));
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(root),
            OnPrepareResponse = ctx =>
            {
                string path = ctx.File.Name;
                bool isHtml = path.EndsWith(".html", StringComparison.OrdinalIgnoreCase);

                // Versioned URLs may be cached hard; anything unversioned must revalidate.
                bool versioned = ctx.Context.Request.Query.ContainsKey("v");

                ctx.Context.Response.Headers.CacheControl = isHtml
                    ? "no-cache, no-store, must-revalidate"
                    : versioned
                        ? "public, max-age=604800, immutable"
                        : "no-cache";
            }
        });

        // Unknown paths fall back to index.html so deep links work, but /api/* must 404.
        app.MapFallback(async ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/ws"))
            {
                ctx.Response.StatusCode = 404;
                await ctx.Response.WriteAsJsonAsync(new ApiError("not-found", "Не найдено"));
                return;
            }

            await ServeShellAsync(ctx, Path.Combine(root, "index.html"));
        });
    }

    /// <summary>
    /// Sends an HTML shell with the build token substituted. Reading it per request is
    /// fine - the file is a few kilobytes and the OS keeps it in cache.
    /// </summary>
    private static async Task ServeShellAsync(HttpContext ctx, string path)
    {
        if (!File.Exists(path))
        {
            ctx.Response.StatusCode = 503;
            await ctx.Response.WriteAsync("web/index.html is missing. Run build.cmd");
            return;
        }

        string html;
        try
        {
            html = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            ctx.Response.StatusCode = 500;
            await ctx.Response.WriteAsync("cannot read shell: " + ex.Message);
            return;
        }

        html = html.Replace("__BUILD__", App.BuildStamp, StringComparison.Ordinal);

        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        await ctx.Response.WriteAsync(html);
    }

    private void ServeAdminSpa(WebApplication app)
    {
        string root = _cfg.WebRootPath;

        // One endpoint for the whole board. Mapping both "/admin" and "/admin/" would
        // register the same pattern twice and the router throws AmbiguousMatchException
        // before any middleware runs, which surfaces as a bare 500.
        app.MapGet("/admin/{*rest}", ctx => ServeAdminShell(ctx, root));

        // The board reuses the public asset tree, so mirror both mount points.
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.Combine(root, "assets")),
            RequestPath = "/assets",
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
                ctx.Context.Request.Query.ContainsKey("v") ? "public, max-age=604800, immutable" : "no-cache"
        });

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.Combine(root, "vendor")),
            RequestPath = "/vendor",
            OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl =
                ctx.Context.Request.Query.ContainsKey("v") ? "public, max-age=604800, immutable" : "no-cache"
        });
    }

    private static async Task ServeAdminShell(HttpContext ctx, string root)
    {
        await ServeShellAsync(ctx, Path.Combine(root, "admin.html"));
    }

    private static async Task ServeAdminFile(HttpContext ctx, string root, string relative)
    {
        string candidate = Path.GetFullPath(Path.Combine(root, relative));

        // Never let a crafted path escape the web root.
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
        {
            ctx.Response.StatusCode = 404;
            await ctx.Response.WriteAsync("not found");
            return;
        }

        ctx.Response.Headers.CacheControl = "no-cache";
        await ctx.Response.SendFileAsync(candidate);
    }

    // ============================================================ maintenance

    private async Task MaintenanceLoopAsync(CancellationToken token)
    {
        DateTime lastBackup = DateTime.UtcNow;
        DateTime lastSweep = DateTime.UtcNow;

        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1), token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                _hub.SweepTyping();

                DateTime now = DateTime.UtcNow;

                if (now - lastSweep > TimeSpan.FromMinutes(10))
                {
                    lastSweep = now;
                    int pruned = _store.PruneSessions();
                    _api.SweepRateLimits();
                    if (pruned > 0) Log.Info("MAINT", $"pruned {pruned} expired sessions");
                }

                if (_cfg.AutoBackupHours > 0 && now - lastBackup > TimeSpan.FromHours(_cfg.AutoBackupHours))
                {
                    lastBackup = now;
                    CreateBackup();
                }
            }
            catch (Exception ex)
            {
                // A maintenance failure must never take the server down.
                Log.Error("MAINT", ex);
            }
        }
    }

    public string CreateBackup()
    {
        try
        {
            Directory.CreateDirectory(_cfg.BackupDirPath);
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string target = Path.Combine(_cfg.BackupDirPath, stamp);
            Directory.CreateDirectory(target);

            _store.SaveAll();
            FileStore.CopyDirectory(_cfg.DataDirPath, target);

            string[] dirs = Directory.GetDirectories(_cfg.BackupDirPath);
            Array.Sort(dirs, StringComparer.Ordinal);
            int remove = dirs.Length - _cfg.KeepBackups;
            for (int i = 0; i < remove; i++)
            {
                try { Directory.Delete(dirs[i], true); } catch { }
            }

            Log.Info("MAINT", "backup " + stamp);
            return target;
        }
        catch (Exception ex)
        {
            Log.Error("MAINT", ex);
            return null;
        }
    }

    // ================================================================== stop

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { }

        try { if (_maintenance != null) await _maintenance; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warn("APP", "maintenance stop: " + ex.Message); }

        try { _store.SaveAll(); } catch (Exception ex) { Log.Warn("APP", "save on stop: " + ex.Message); }

        try { if (_public != null) await _public.StopAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception ex) { Log.Warn("APP", "public stop: " + ex.Message); }

        try { if (_admin != null) await _admin.StopAsync(TimeSpan.FromSeconds(3)); }
        catch (Exception ex) { Log.Warn("APP", "admin stop: " + ex.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        try { if (_public != null) await _public.DisposeAsync(); } catch { }
        try { if (_admin != null) await _admin.DisposeAsync(); } catch { }
        _cts?.Dispose();
    }

    public string StatusText()
    {
        return $"users={_store.UserCount} rooms={_store.AllRooms().Count} dms={_store.AllDms().Count} " +
               $"online={_hub.ConnectionCount} port={PublicPort}";
    }
}
