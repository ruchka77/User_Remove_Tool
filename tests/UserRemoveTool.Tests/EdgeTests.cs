using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UserRemoveTool.Core.Configuration;
using UserRemoveTool.Core.Models;
using UserRemoveTool.Core.ServiceDesk;
using Xunit;

namespace UserRemoveTool.Tests;

/// <summary>
/// Drives a real Edge against a small local imitation of CA (framesets, popup that depends on its opener,
/// caption-above-field tables). Skipped with: dotnet test --filter Category!=Edge
/// </summary>
[Trait("Category", "Edge")]
public sealed class EdgeTests : IAsyncLifetime
{
    private readonly HttpListener _server = new();
    private readonly string _profile = Directory.CreateTempSubdirectory("urt-edge").FullName;
    private readonly List<string> _posts = new();
    private string _base = "";

    public Task InitializeAsync()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _base = $"http://localhost:{port}";
        _server.Prefixes.Add(_base + "/");
        _server.Start();
        _ = ServeAsync();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _server.Close();
        // Close only the browser that uses this test's private profile.
        var file = Path.Combine(_profile, "DevToolsActivePort");
        if (File.Exists(file))
        {
            var lines = File.ReadAllLines(file);
            try
            {
                using var cdp = await CdpClient.ConnectAsync(new Uri($"ws://127.0.0.1:{lines[0]}{lines[1]}"), default);
                await cdp.SendAsync("Browser.close");
            }
            catch (Exception) { /* already closed */ }
        }
        for (var i = 0; i < 20; i++)
        {
            try { Directory.Delete(_profile, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(300); }
        }
    }

    private bool _popupWorks = true, _textarea = true, _loginFirst;

    private async Task ServeAsync()
    {
        while (_server.IsListening)
        {
            HttpListenerContext c;
            try { c = await _server.GetContextAsync(); } catch (Exception) { return; }
            var path = c.Request.Url!.PathAndQuery;
            if (c.Request.HttpMethod == "POST") lock (_posts) _posts.Add(path);
            string body;
            if (path.Contains("OP=CREATE_NEW")) body = path.Contains("SID=777") ? Form() : "no session";
            else if (path.Contains("popup_frames"))
                body = _popupWorks
                    ? "<html><script>document.write('<frameset rows=\"30,*\"><frame src=\"/CAisd/blank\"><frame src=\"'+(window.opener?'/CAisd/form':'/CAisd/blank')+'\"></frameset>')</script></html>"
                    : "<html><body>empty</body></html>";
            else if (path.Contains("/form")) body = Form();
            else if (path.Contains("/blank")) body = "<html><body>blank</body></html>";
            else if (path.Contains("/menu")) body = "<html><body><a href='#'>file</a><script>var cfgSID=777;</script></body></html>";
            else if (_loginFirst) { _loginFirst = false; body = "<html><body><input type=password><script>setTimeout(()=>location.reload(),1500)</script></body></html>"; }
            else body = "<html><frameset cols='50%,50%'><frame src='/CAisd/menu'><frame src='/CAisd/blank'></frameset></html>";
            var bytes = Encoding.UTF8.GetBytes(body);
            try
            {
                c.Response.ContentType = "text/html; charset=utf-8";
                c.Response.OutputStream.Write(bytes);
                c.Response.Close();
            }
            catch (HttpListenerException) { }
        }
    }

    private string Form() => $$"""
        <!doctype html><html dir=rtl><body><form method=post action="/CAisd/save" name=main_form>
        <table><tr><th>לקוח *</th><th>קטגוריה</th><th>סטטוס</th></tr>
        <tr><td><input type=hidden name="SET.customer"><input name="KEY.customer"></td>
        <td><input name="KEY.category" onchange="setTimeout(function(){document.getElementsByName('KEY.group')[0].value='other'},200)"></td>
        <td><select name="SET.status"><option>פתוח<option>בהמתנה</select></td></tr></table>
        <table><tr><th>מקור הקריאה:</th><th>צוות מטפל</th><th>גורם מטפל</th></tr>
        <tr><td><select name="SET.zsrc"><option>טלפון<option>פורטל לפתיחת קריאות</select></td>
        <td><input name="KEY.group" value="x"></td><td><input name="KEY.assignee"></td></tr></table>
        {{(_textarea ? "<textarea name='SET.summary'></textarea>" : "<input name='SET.summary' maxlength=240>")}}
        <button type=submit>שמור</button></form></body></html>
        """;

    private async Task<IReadOnlyList<TicketFieldResult>> Run()
    {
        var s = AppSettings.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json")).ServiceDesk;
        s.Url = _base + "/CAisd/pdmweb.exe";
        s.NewTicketUrl = _base + "/CAisd/html/popup_frames.html?POPUP_URLIX=0+popuptype=1";
        s.FormTimeoutSeconds = 5;
        s.LoginTimeoutSeconds = 30;
        var record = new OffboardingRecord { DisplayName = "דנה \"</script>' כהן", Mail = "d@moia.gov.il", HomeDirectory = @"\\fs\home$\dana", SamAccountName = "danac" };
        var fields = TicketContent.Build(record, s);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var results = await new TicketAutomation(s, _profile, new NullLogger()).RunAsync(fields, _ => { }, cts.Token);
        await Task.Delay(800);
        lock (_posts) Assert.Empty(_posts); // nothing was ever submitted
        return results;
    }

    private static void AssertAllFilled(IReadOnlyList<TicketFieldResult> results) =>
        Assert.True(results.All(r => r.Filled), string.Join("; ", results.Select(r => $"{r.Field.Key}: {r.Message}")));

    [Fact]
    public async Task Fills_all_fields_after_login_without_submitting()
    {
        _loginFirst = true;
        AssertAllFilled(await Run());
        AssertAllFilled(await Run()); // reuses the open browser
    }

    [Fact]
    public async Task Falls_back_to_session_url_and_flattens_single_line_summary()
    {
        _popupWorks = false;
        _textarea = false;
        var results = await Run();
        AssertAllFilled(results);
        Assert.Contains("שורה אחת", results.Single(r => r.Field.Key == "Summary").Message);
    }
}
