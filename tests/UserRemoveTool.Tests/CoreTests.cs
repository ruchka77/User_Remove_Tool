using UserRemoveTool.Core;
using UserRemoveTool.Core.ActiveDirectory;
using UserRemoveTool.Core.Configuration;
using UserRemoveTool.Core.Logging;
using UserRemoveTool.Core.Models;
using UserRemoveTool.Core.ServiceDesk;
using UserRemoveTool.Core.Text;
using UserRemoveTool.Core.Workflow;
using Xunit;

namespace UserRemoveTool.Tests;

public class LdapTextTests
{
    [Fact]
    public void Dn_parsing_honours_escaped_commas()
    {
        const string dn = @"CN=Cohen\, Dana,OU=IT,DC=moia,DC=gov,DC=il";
        Assert.Equal(@"CN=Cohen\, Dana", LdapText.GetRdn(dn));
        Assert.Equal("OU=IT,DC=moia,DC=gov,DC=il", LdapText.GetParent(dn));
        Assert.Equal("moia.gov.il", LdapText.GetDnsDomain(dn));
    }

    [Fact]
    public void Filter_escapes_injection() =>
        Assert.Contains(@"(sn=*a\29\28objectClass=\2a\29*)", LdapText.BuildUserFilter("a)(objectClass=*)"));

    [Fact]
    public void Filter_is_users_only_and_does_not_exclude_disabled_accounts()
    {
        var filter = LdapText.BuildUserFilter("moshe");
        Assert.StartsWith("(&(objectCategory=person)(objectClass=user)", filter);
        Assert.DoesNotContain("userAccountControl", filter);
    }

    [Fact]
    public void Filter_matches_contains_on_all_ten_attributes()
    {
        var filter = LdapText.BuildUserFilter("משה");
        foreach (var attribute in new[] { "displayName", "cn", "name", "givenName", "sn", "sAMAccountName", "userPrincipalName", "mail", "description", "extensionAttribute1" })
            Assert.Contains($"({attribute}=*משה*)", filter);
        Assert.Equal(filter.Count(c => c == '('), filter.Count(c => c == ')'));
    }

    [Fact]
    public void Every_word_must_match_some_attribute()
    {
        var filter = LdapText.BuildUserFilter("  משה   כהן ");
        Assert.Equal(1, filter.Split("(displayName=*משה*)").Length - 1);
        Assert.Contains("(givenName=*כהן*)", filter);
        Assert.Matches(@"^\(&\(objectCategory=person\)\(objectClass=user\)\(\|.*משה.*\)\(\|.*כהן.*\)\)$", filter);
    }
}
public class ConfigurationTests
{
    [Fact]
    public void Shipped_settings_are_valid()
    {
        var s = AppSettings.Load(Path.Combine(AppContext.BaseDirectory, "appsettings.json"));
        Assert.Equal("moia.gov.il", s.ActiveDirectory.DomainController);
        Assert.Equal("בכר, ברוך", s.ServiceDesk.Assignee);
        Assert.Equal(7, s.ServiceDesk.Fields.Count);
    }

    [Fact]
    public void Bad_settings_are_rejected()
    {
        var s = new AppSettings();
        s.ActiveDirectory.TargetOu = "nonsense";
        Assert.Contains("TargetOu", Assert.Throws<UserFacingException>(s.Validate).Message);
    }
}

public class NationalIdTests
{
    [Theory]
    [InlineData("000000018", NationalIdStatus.Valid)]
    [InlineData("18", NationalIdStatus.Valid)]
    [InlineData(" 000-000-018 ", NationalIdStatus.Valid)]
    [InlineData("000000019", NationalIdStatus.Invalid)]
    [InlineData("12345678901", NationalIdStatus.Invalid)]
    [InlineData("abc", NationalIdStatus.Invalid)]
    [InlineData("", NationalIdStatus.Missing)]
    [InlineData(null, NationalIdStatus.Missing)]
    public void Israeli_id_check_digit(string? value, NationalIdStatus expected) =>
        Assert.Equal(expected, NationalIdValidator.Check(value));

    [Fact]
    public async Task National_id_is_saved_to_the_json_file()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var record = new OffboardingRecord { CorrelationId = "x", NationalId = "000000018", SamAccountName = "a" };
        var store = new RecordStore(dir);
        var path = store.Save(record);
        Assert.Contains("\"nationalId\": \"000000018\"", File.ReadAllText(path));
        Assert.Equal("000000018", store.Load(path).NationalId);
        await Task.CompletedTask;
        Directory.Delete(dir, true);
    }
}

public class VisualTextTests
{
    [Theory]
    [InlineData("שלום", "םולש")]
    [InlineData("שם משתמש: danac", "danac :שמתשמ םש")]
    [InlineData("דנה (danac)", "(danac) הנד")]
    [InlineData("שלב 3 מתוך 6", "6 ךותמ 3 בלש")]
    [InlineData("OU=IT,OU=Users,DC=moia", "OU=IT,OU=Users,DC=moia")]
    public void Hebrew_is_laid_out_right_to_left(string logical, string visual) =>
        Assert.Equal(visual, VisualText.ToVisual(logical));

    [Fact]
    public void Path_keeps_its_reading_order_next_to_hebrew()
    {
        const string path = @"\\fs01\home$\danac";
        var label = "תיקיית בית: ";
        var expected = path + new string(label.Reverse().ToArray());
        Assert.Equal(expected, VisualText.ToVisual(label + path));
    }

    [Fact]
    public void Wrap_breaks_before_reordering()
    {
        var lines = VisualText.Wrap("אחד שניים שלושה ארבעה חמישה", 12).ToList();
        Assert.All(lines, l => Assert.True(l.Length <= 12));
        Assert.Equal("אחד שניים שלושה ארבעה חמישה", string.Join(" ", lines));
    }
}

public class SummaryTests
{
    [Fact]
    public void Format_is_exact() => Assert.Equal("Email:\na@b.c\n\nHome Folder:\n\\\\fs\\h$\\a\n\nUsername:\nabc",
        TicketContent.BuildSummary(new OffboardingRecord { Mail = "a@b.c", HomeDirectory = @"\\fs\h$\a", SamAccountName = "abc" }));
}

internal sealed class NullLogger : IAppLogger
{
    public void Info(string message) { }
    public void Error(string message, Exception? exception = null) { }
}

public sealed class WorkflowTests : IDisposable
{
    private sealed class FakeAudit : IAuditLogger
    {
        public readonly List<(string Action, AuditOutcome Outcome)> Entries = new();
        public bool Broken;
        public void Write(string c, string action, AuditOutcome outcome, string a, string d, string detail = "")
        {
            if (Broken) throw new UserFacingException("audit");
            Entries.Add((action, outcome));
        }
    }

    private sealed class FakeDirectory : IDirectoryService
    {
        public DirectoryUser User = new() { ObjectGuid = Guid.NewGuid(), DistinguishedName = "CN=a,OU=b,DC=c", SamAccountName = "a", Email = "a@b", DisplayName = "דנה", Sid = "S-1-5-21-1-2-3-1001" };
        public List<GroupMembership> Groups = [new("CN=G1", "G1", false, false), new("CN=G2", "G2", false, false), new("CN=Domain Users", "Domain Users", true, true)];
        public HashSet<string> Locked = new();
        public List<string> Calls = new();
        public bool FailMove, FailDisable, NoTargetOu;
        public TaskCompletionSource? Hold;

        public Task<IReadOnlyList<DirectoryUser>> SearchAsync(string term, CancellationToken ct) => throw new NotSupportedException();
        public Task<DirectoryUser> GetUserAsync(Guid g) => Task.FromResult(User);
        public Task EnsureTargetOuAsync() => NoTargetOu ? throw new UserFacingException("ou") : Task.CompletedTask;
        public Task<IReadOnlyList<GroupMembership>> GetGroupsAsync(Guid g) => Task.FromResult<IReadOnlyList<GroupMembership>>(Groups.ToList());
        public async Task RemoveFromGroupAsync(Guid g, GroupMembership group)
        {
            if (Hold is not null) await Hold.Task;
            Calls.Add("remove " + group.Name);
            if (Locked.Contains(group.Name)) throw new UserFacingException("denied");
            Groups.Remove(group);
        }
        public Task MoveToTargetOuAsync(Guid g) { Calls.Add("move"); return FailMove ? throw new UserFacingException("x") : Task.CompletedTask; }
        public Task DisableAsync(Guid g) { Calls.Add("disable"); return FailDisable ? throw new UserFacingException("x") : Task.CompletedTask; }
    }

    private readonly string _temp = Directory.CreateTempSubdirectory().FullName;
    private readonly FakeDirectory _ad = new();
    private readonly FakeAudit _audit = new();
    private readonly OffboardingWorkflow _flow;

    public WorkflowTests() => _flow = new(_ad, _audit, new NullLogger(), new RecordStore(_temp), "S-1-5-21-1-2-3-9999");
    public void Dispose() => Directory.Delete(_temp, true);

    private Task Start() => _flow.StartAsync(_ad.User.ObjectGuid);

    [Fact]
    public async Task Happy_path_runs_in_order_and_uses_the_json_file()
    {
        await Start();
        Assert.Contains("\"mail\": \"a@b\"", File.ReadAllText(_flow.RecordPath!));
        Assert.Contains("דנה", File.ReadAllText(_flow.RecordPath!));
        Assert.Throws<UserFacingException>(() => _flow.LoadRecordForTicket());
        Assert.True(await _flow.RemoveGroupsAsync(_ => { }));
        await _flow.MoveAndDisableAsync(_ => { });
        Assert.Equal(["remove G1", "remove G2", "move", "disable"], _ad.Calls);
        Assert.Equal("a@b", _flow.LoadRecordForTicket().Mail);
        Assert.Equal("Domain Users", Assert.Single(_ad.Groups).Name);
        var path = _flow.RecordPath!;
        _flow.Reset();
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task One_failed_group_does_not_stop_the_rest_but_blocks_the_move()
    {
        _ad.Locked.Add("G1");
        await Start();
        var final = new Dictionary<string, OperationStatus>();
        Assert.False(await _flow.RemoveGroupsAsync(r => final[r.Group.Name] = r.Status));
        Assert.Equal(OperationStatus.Failed, final["G1"]);
        Assert.Equal(OperationStatus.Success, final["G2"]);
        Assert.Equal(OperationStatus.Kept, final["Domain Users"]);
        await Assert.ThrowsAsync<UserFacingException>(() => _flow.MoveAndDisableAsync(_ => { }));
        Assert.DoesNotContain("move", _ad.Calls);
        _ad.Locked.Clear();
        Assert.True(await _flow.RemoveGroupsAsync(_ => { }));
    }

    [Fact]
    public async Task Failed_move_never_disables()
    {
        _ad.FailMove = true;
        await Start();
        await _flow.RemoveGroupsAsync(_ => { });
        await Assert.ThrowsAsync<UserFacingException>(() => _flow.MoveAndDisableAsync(_ => { }));
        Assert.DoesNotContain("disable", _ad.Calls);
        Assert.False(_flow.CanOpenTicket);
        Assert.Contains(("MoveUser", AuditOutcome.Failure), _audit.Entries);
    }

    [Fact]
    public async Task Failed_disable_can_be_retried_without_moving_again()
    {
        _ad.FailDisable = true;
        await Start();
        await _flow.RemoveGroupsAsync(_ => { });
        await Assert.ThrowsAsync<UserFacingException>(() => _flow.MoveAndDisableAsync(_ => { }));
        Assert.False(_flow.CanOpenTicket);
        _ad.FailDisable = false;
        await _flow.MoveAndDisableAsync(_ => { });
        Assert.Single(_ad.Calls, c => c == "move");
        Assert.True(_flow.CanOpenTicket);
    }

    [Fact]
    public async Task No_change_is_made_when_the_audit_log_is_unavailable()
    {
        await Start();
        _audit.Broken = true;
        await Assert.ThrowsAsync<UserFacingException>(() => _flow.RemoveGroupsAsync(_ => { }));
        Assert.Empty(_ad.Calls);
    }

    [Fact]
    public async Task Second_action_is_rejected_while_one_is_running()
    {
        await Start();
        _ad.Hold = new();
        var first = _flow.RemoveGroupsAsync(_ => { });
        await Assert.ThrowsAsync<UserFacingException>(() => _flow.RemoveGroupsAsync(_ => { }));
        _ad.Hold.SetResult();
        Assert.True(await first);
    }

    [Fact]
    public async Task Unsafe_targets_are_refused()
    {
        var normal = _ad.User;
        _ad.User = normal with { Sid = "S-1-5-21-1-2-3-9999" };
        await Assert.ThrowsAsync<UserFacingException>(Start);
        _ad.User = normal with { IsCriticalSystemObject = true };
        await Assert.ThrowsAsync<UserFacingException>(Start);
        _ad.User = normal;
        _ad.NoTargetOu = true;
        await Assert.ThrowsAsync<UserFacingException>(Start);
    }

    [Fact]
    public void Audit_file_is_readable_json_lines()
    {
        var audit = new AuditLogger(_temp);
        audit.Write("id", "MoveUser", AuditOutcome.Success, "a", "CN=דנה");
        audit.Write("id", "DisableUser", AuditOutcome.Failure, "a", "CN=דנה");
        var lines = File.ReadAllLines(Directory.GetFiles(_temp, "audit-*.jsonl").Single());
        Assert.Equal(2, lines.Length);
        Assert.Contains("CN=דנה", lines[0]);
        Assert.Contains("\"Success\"", lines[0]);
    }
}
