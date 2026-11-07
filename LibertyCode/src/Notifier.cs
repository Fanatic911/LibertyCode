using System.Diagnostics;
using System.Text;

namespace LibertyCode;

/// <summary>Desktop notification when the AI has answered. Fire-and-forget: it must never block or crash the app.</summary>
internal static class Notifier
{
    // Title and body travel in environment variables, so no text is ever pasted into a script (no quoting issues).
    private const string WindowsScript = """
        [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] > $null
        $xml = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
        $text = $xml.GetElementsByTagName('text')
        $text.Item(0).AppendChild($xml.CreateTextNode($env:LC_TITLE)) > $null
        $text.Item(1).AppendChild($xml.CreateTextNode($env:LC_BODY)) > $null
        $toast = [Windows.UI.Notifications.ToastNotification]::new($xml)
        $appId = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe'
        [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier($appId).Show($toast)
        """;

    public static void Send(string title, string body)
    {
        try
        {
            body = Clean(body);
            var psi = new ProcessStartInfo
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.Environment["LC_TITLE"] = title;
            psi.Environment["LC_BODY"] = body;

            if (OperatingSystem.IsWindows())
            {
                psi.FileName = "powershell.exe";
                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-NonInteractive");
                psi.ArgumentList.Add("-WindowStyle");
                psi.ArgumentList.Add("Hidden");
                psi.ArgumentList.Add("-EncodedCommand");
                psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(WindowsScript)));
            }
            else if (OperatingSystem.IsMacOS())
            {
                psi.FileName = "osascript";
                psi.ArgumentList.Add("-e");
                psi.ArgumentList.Add("display notification (system attribute \"LC_BODY\") with title (system attribute \"LC_TITLE\")");
            }
            else
            {
                psi.FileName = "notify-send";
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add(title);
                psi.ArgumentList.Add(body);
            }

            var p = Process.Start(psi);
            if (p == null) return;
            _ = Task.Run(() =>
            {
                try { p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd(); p.WaitForExit(10000); }
                catch { }
                finally { p.Dispose(); }
            });
        }
        catch (Exception ex)
        {
            Logger.Warn("Notification failed: " + ex.Message);
            try { Console.Write('\a'); } catch { }   // fall back to the terminal bell
        }
    }

    private static string Clean(string s)
    {
        s = (s ?? "").Replace("`", "").Replace("**", "").Replace("#", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length == 0) return "Done.";
        return s.Length > 140 ? s[..139] + "…" : s;
    }
}
