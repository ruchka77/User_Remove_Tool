# Project and file structure

Three projects. `UserRemoveTool` (WPF) and `UserRemoveTool.Tests` both depend on `UserRemoveTool.Core`, which has no UI code.

## Root

| File | Purpose |
|---|---|
| `UserRemoveTool.sln` | Solution |
| `Directory.Build.props` | Target framework, language version and warnings for all projects |
| `Launch.bat` | Only starts the pre-built `UserRemoveTool.exe` (from `publish`, or next to itself) with the exe folder as working directory; never builds. Also copied into `publish` |
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
| `Workflow/RecordStore.cs` | Temporary JSON file |
| `ServiceDesk/TicketAutomation.cs` | Ticket content; opens CA, opens a new ticket, fills and verifies fields |
| `ServiceDesk/EdgeBrowser.cs` | Finds, starts and reconnects to Edge |
| `ServiceDesk/CdpClient.cs` | Small DevTools Protocol client over WebSocket |
| `ServiceDesk/ca-ticket.js` | Runs in the CA page: find, fill, read. Contains no save or submit |

## src/UserRemoveTool: WPF application (MVVM)

| File | Purpose |
|---|---|
| `App.xaml`, `App.xaml.cs` | Shared styles; creates all services |
| `appsettings.json` | Configuration |
| `app.manifest` | Runs without elevation |
| `Properties/PublishProfiles/win-x64.pubxml` | Self-contained publish profile |
| `Mvvm/Mvvm.cs` | `ObservableObject` and commands |
| `ViewModels/MainViewModel.cs` | Navigation, overall progress, shared busy gate |
| `ViewModels/SearchViewModel.cs` | Steps 1-2 |
| `ViewModels/GroupsViewModel.cs` | Step 3 |
| `ViewModels/MoveViewModel.cs` | Step 4 |
| `ViewModels/TicketViewModel.cs` | Steps 5-6 |
| `Views/*.xaml` | One RTL view per view model, plus the main and message windows |
| `Views/Views.xaml.cs` | Code-behind for all views (focus, closing) and the dialog service |

## tests/UserRemoveTool.Tests

| File | Purpose |
|---|---|
| `CoreTests.cs` | LDAP text, configuration, ticket format, workflow rules, audit file |
| `EdgeTests.cs` | Real Edge against a local imitation of CA, including proof that nothing is submitted |

## Flow

`SearchViewModel` -> `OffboardingWorkflow.StartAsync` -> `GroupsViewModel` -> `RemoveGroupsAsync` -> `MoveViewModel` -> `MoveAndDisableAsync` -> `TicketViewModel` -> `TicketAutomation.RunAsync`

The workflow itself refuses out-of-order calls, so a UI defect cannot skip a step.
