# Project and file structure

Three projects. `UserRemoveTool` (WPF) and `UserRemoveTool.Tests` both depend on `UserRemoveTool.Core`, which has no UI code.

## Root

| File | Purpose |
|---|---|
| `UserRemoveTool.sln` | Solution |
| `Directory.Build.props` | Target framework, language version and warnings for all projects |
| `Launch.bat` | Sets the console to UTF-8 and runs the pre-built console `UserRemoveTool.exe` (from `publish`, or next to itself) with the exe folder as working directory; never builds. Also copied into `publish` |
| `publish/` | Pre-built self-contained `win-x64` output (exe, runtime, `appsettings.json`, `Launch.bat`). Regenerate after code changes with `dotnet publish src\UserRemoveTool\UserRemoveTool.csproj -c Release -r win-x64 --self-contained true -p:PublishProfile=win-x64 -o publish` |
| `README.md` | Operator and administrator guide (Hebrew) |

## src/UserRemoveTool.Core: logic

| File | Purpose |
|---|---|
| `UserFacingException.cs` | Error whose Hebrew message may be shown to the operator |
| `Configuration/AppSettings.cs` | Typed `appsettings.json`, loading and validation |
| `Models/DirectoryUser.cs` | User, group membership and operation status |
| `Models/OffboardingRecord.cs` | Step 2 data, the content of the temporary JSON file |
| `Logging/Loggers.cs` | Application log (best effort) and audit log (a failed write stops the operation) |
| `ActiveDirectory/IDirectoryService.cs` | Directory operations used by the workflow |
| `ActiveDirectory/LdapDirectoryService.cs` | LDAP implementation: search, groups, move, disable, error translation |
| `ActiveDirectory/LdapText.cs` | Filter escaping and DN parsing |
| `Workflow/OffboardingWorkflow.cs` | Step order, safety checks, auditing, re-entrancy guard |
| `Cache/UserCache.cs` | Local DPAPI-encrypted cache of users from earlier searches (TTL, instant local search, background refresh) |
| `Workflow/RecordStore.cs` | Temporary JSON file |
| `Text/VisualText.cs` | Converts Hebrew lines to visual order for left-to-right consoles; Latin words, paths and attribute names keep their order |
| `Workflow/NationalIdValidator.cs` | Checks extensionAttribute1 as an Israeli ID number |
| `ServiceDesk/TicketAutomation.cs` | Ticket content; opens CA, opens a new ticket, fills and verifies fields |
| `ServiceDesk/EdgeBrowser.cs` | Finds, starts and reconnects to Edge |
| `ServiceDesk/CdpClient.cs` | Small DevTools Protocol client over WebSocket |
| `ServiceDesk/ca-ticket.js` | Runs in the CA page: find, fill, read. Contains no save or submit |

## src/UserRemoveTool: console application (WPF only for the search window)

| File | Purpose |
|---|---|
| `ConsoleOut.cs` | All console text output: UTF-8 setup, wrapping, visual-order conversion and right alignment (terminal only) |
| `Program.cs` | Entry point and the whole CLI workflow (`Cli`): prompts, per-step output, confirmations, retries |
| `appsettings.json` | Configuration |
| `app.manifest` | Runs without elevation |
| `Properties/PublishProfiles/win-x64.pubxml` | Self-contained publish profile |
| `Mvvm/Mvvm.cs` | `ObservableObject` and `RelayCommand` |
| `ViewModels/SearchPickerViewModel.cs` | Live search and selection logic for the pop-up |
| `Views/SearchWindow.xaml(.cs)` | The only window: RTL search box and results, blue Continue / red Cancel; `Pick()` runs it on its own STA thread |
## tests/UserRemoveTool.Tests

| File | Purpose |
|---|---|
| `CoreTests.cs` | LDAP text, configuration, ticket format, NationalId, workflow rules, audit file |
| `EdgeTests.cs` | Real Edge against a local imitation of CA, including proof that nothing is submitted |

## Flow

`SearchWindow.Pick` (window) -> then in the console: `OffboardingWorkflow.StartAsync` -> NationalId check -> `RemoveGroupsAsync` -> `MoveAndDisableAsync` -> `TicketAutomation.RunAsync`

The workflow itself refuses out-of-order calls, so a UI defect cannot skip a step.
