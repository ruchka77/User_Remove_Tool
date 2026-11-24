using System.ComponentModel;
using System.Diagnostics;
using System.Net.WebSockets;
using Microsoft.Win32;
using UserRemoveTool.Core.Logging;

namespace UserRemoveTool.Core.ServiceDesk;

/// <summary>Starts Microsoft Edge, or reconnects to the window this tool started earlier.</summary>
internal sealed class EdgeBrowser(string configuredPath, string profileDirectory, IAppLogger log)
{
    public string FindExecutable()
    {
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe";
        var candidates = new[]
        {
            Environment.ExpandEnvironmentVariables(configuredPath ?? ""),
            Registry.CurrentUser.OpenSubKey(key)?.GetValue(null) as string,
            Registry.LocalMachine.OpenSubKey(key)?.GetValue(null) as string,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe")
        };
        return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
               ?? throw new UserFacingException("Microsoft Edge לא נמצא במחשב.");
    }

    /// <summary>Opens a page in the operator's regular Edge, without automation.</summary>
    public void OpenManually(string url) => Start([url]);

    public async Task<CdpClient> ConnectAsync(Uri serviceDesk, CancellationToken ct)
    {
        Directory.CreateDirectory(profileDirectory);
        var portFile = Path.Combine(profileDirectory, "DevToolsActivePort");
        if (await TryConnectAsync(portFile, ct).ConfigureAwait(false) is { } existing)
        {
            log.Info("Reusing the Edge window opened earlier");
            return existing;
        }
        try { File.Delete(portFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.Error("Cannot delete old DevToolsActivePort", ex); }

        // Edge allows automation only in a dedicated profile. Port 0 = random port, reachable from this PC only.
        Start([
            "--remote-debugging-port=0",
            $"--user-data-dir={profileDirectory}",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-popup-blocking",
            $"--auth-server-allowlist={serviceDesk.Host}",
            "--new-window",
            "about:blank"
        ]);

        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(200, ct).ConfigureAwait(false);
            if (await TryConnectAsync(portFile, ct).ConfigureAwait(false) is { } client) return client;
        }
        throw new UserFacingException("לא ניתן להתחבר ל-Edge. ייתכן שמדיניות הארגון (RemoteDebuggingAllowed) חוסמת אוטומציה.");
    }

    private static async Task<CdpClient?> TryConnectAsync(string portFile, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(portFile)) return null;
            // Two lines: port, and a path containing a random id of that specific browser instance.
            var lines = await File.ReadAllLinesAsync(portFile, ct).ConfigureAwait(false);
            if (lines.Length < 2 || !int.TryParse(lines[0], out var port)) return null;
            return await CdpClient.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{lines[1]}"), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WebSocketException ||
                                   (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    private void Start(string[] arguments)
    {
        var info = new ProcessStartInfo(FindExecutable()) { UseShellExecute = false };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        try { Process.Start(info)?.Dispose(); }
        catch (Win32Exception ex) { throw new UserFacingException($"הפעלת Edge נכשלה: {ex.Message}", ex); }
    }
}
