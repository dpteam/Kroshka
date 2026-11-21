using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;

namespace DPT.Kroshka.Client;

/// <summary>
/// One place that knows how to get the application icon.
///
/// Why this exists: ApplicationIcon embeds the .ico into the exe, but the file
/// itself is not copied next to it. A loader that only reads assets/kroshka.ico
/// therefore finds nothing on a clean install and silently falls back to the
/// generic .NET icon - which is exactly what a blank title bar and an anonymous
/// tray icon look like.
///
/// Order of preference:
///   1. the .ico next to the exe (full multi-size set, best for 256px surfaces)
///   2. the icon embedded in the running exe (always present, 32px)
///   3. the assembly resource (in case the file was stripped)
///   4. the system application icon (last resort, never blank)
/// </summary>
internal static class IconLoader
{
    private const int SmallIcon = 16;
    private const int LargeIcon = 32;

    private static Icon _cached;

    /// <summary>The window and tray icon. Loaded once and reused.</summary>
    public static Icon Get()
    {
        return _cached ??= Load(LargeIcon);
    }

    /// <summary>
    /// A specific size, for surfaces that ask for one explicitly. Windows asks for
    /// 16px in the tray and title bar, 32px on the taskbar, 256px in large previews.
    /// </summary>
    public static Icon Get(int size)
    {
        return Load(size);
    }

    private static Icon Load(int size)
    {
        // 1. the shipped file, which carries every size
        string path = Path.Combine(AppContext.BaseDirectory, "assets", "kroshka.ico");
        if (File.Exists(path))
        {
            try
            {
                return new Icon(path, size, size);
            }
            catch (Exception ex)
            {
                DPT.Kroshka.Shared.Log.Warn("ICON", "cannot read " + path + ": " + ex.Message);
            }
        }

        // 2. whatever the compiler embedded into this exe
        try
        {
            string exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                Icon extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted != null) return extracted;
            }
        }
        catch (Exception ex)
        {
            DPT.Kroshka.Shared.Log.Warn("ICON", "cannot extract from exe: " + ex.Message);
        }

        // 3. a resource compiled into the assembly
        try
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            foreach (string name in assembly.GetManifestResourceNames())
            {
                if (!name.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) continue;

                using Stream stream = assembly.GetManifestResourceStream(name);
                if (stream != null) return new Icon(stream, size, size);
            }
        }
        catch { }

        // 4. never leave the window with no icon at all
        return SystemIcons.Application;
    }

    /// <summary>
    /// Applies the icon to a form, and asks the shell for the correct small size.
    /// WinForms uses one icon for both the title bar and Alt-Tab unless told
    /// otherwise, which is why a 32px icon can look soft at 16px.
    /// </summary>
    public static void Apply(Form form)
    {
        try
        {
            form.Icon = Get();
            form.Icon = Get(SmallIcon);
        }
        catch (Exception ex)
        {
            DPT.Kroshka.Shared.Log.Warn("ICON", "cannot apply to form: " + ex.Message);
        }
    }

    /// <summary>
    /// Builds a tray icon at the size the shell actually uses. Passing a 32px icon
    /// to NotifyIcon makes Windows downscale it badly; 16px is the native size.
    /// </summary>
    public static Icon ForTray()
    {
        try
        {
            return Get(SmallIcon);
        }
        catch
        {
            return Get();
        }
    }
}
