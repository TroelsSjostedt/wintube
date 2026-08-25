using Microsoft.Win32;

namespace WinTube.App;

/// Registers wintube:// for the current user, so links and tools can target the app.
/// HKCU only, idempotent — rewritten only when the stored command drifts (a moved exe).
/// Never registers for http/https: stealing browser links is not this app's place.
public static class ProtocolRegistration
{
    public static void EnsureRegistered()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            var command = $"\"{exe}\" \"%1\"";

            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\wintube");
            using var commandKey = key.CreateSubKey(@"shell\open\command");
            if (Equals(commandKey.GetValue(null), command)) return;
            key.SetValue(null, "URL:WinTube");
            key.SetValue("URL Protocol", "");
            using var icon = key.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{exe}\",0");
            commandKey.SetValue(null, command);
        }
        catch (Exception e) when (e is System.Security.SecurityException
            or UnauthorizedAccessException or IOException)
        {
            // A locked-down registry costs the protocol, not the app.
        }
    }
}
