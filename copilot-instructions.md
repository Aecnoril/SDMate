# AGENTS.md

SDMate is a Windows WPF app that helps Servicedesk (SD) technicians offboard users and track deletion reminders, using the technician's own admin account. Everything runs on the technician's device; there is no server.

## Source of truth

- Requirements live in `docs/SPEC.md`. Requirement IDs look like `REQ-OFF-003`.
- Build in the order given under "Build order" in the spec. One slice at a time, each slice working end to end before the next.
- If the spec is ambiguous or lists the point under "Open questions", stop and ask. Do not guess. Do not edit the spec yourself.
- Out of scope for v1, never implement: mailbox sharing, Entra actions, Intune actions, TOPdesk, Onboarding and AD Change Request logic (stub tabs only).

## Stack

- .NET 11, WPF, C# with nullable reference types and implicit usings enabled.
- MVVM with `CommunityToolkit.Mvvm` and `Microsoft.Extensions.DependencyInjection`.
- Active Directory: native C# (`System.DirectoryServices`, `System.DirectoryServices.AccountManagement`). No RSAT dependency.
- Exchange Online: `ExchangeOnlineManagement` through `powershell.exe` (Windows PowerShell 5.1) as an out-of-process child process. Results as JSON. Any error-stream output or non-zero exit code is a failure.
- Tests: xUnit.

## Layout and dependency rules

| Project | Contains | May reference |
| --- | --- | --- |
| `src/SDMate.App` | Views, view models, navigation, DI composition root | Core, Infrastructure |
| `src/SDMate.Core` | Models, interfaces, workflow and step engine | nothing UI- or AD-specific |
| `src/SDMate.Infrastructure` | AD service, PowerShell runner, Exchange service, file stores | Core |
| `tests/SDMate.Core.Tests` | Unit tests for Core, using fakes | Core |

- `SDMate.Core` MUST NOT reference WPF, `System.DirectoryServices` or PowerShell.
- Views contain no logic. Code-behind stays empty apart from `InitializeComponent`.
- Workflow steps depend only on Core interfaces such as `IDirectoryService` and `IMailboxService`.
- Extend existing code instead of replacing it. In particular, extend `UserSearchInputClassifier` and keep its tests passing.

## Commands

- Build: `dotnet build SDMate.sln`
- Test: `dotnet test SDMate.sln`
- Run all of both before saying a task is done.

## Conventions

- File-scoped namespaces. One public type per file, file named after the type.
- Async all the way for anything touching AD, Exchange or disk. Pass `CancellationToken`.
- Test names: `Method_Condition_ExpectedResult`, as in the existing tests.
- Every Core behaviour and every workflow step gets a unit test. Name the `REQ-*` ID in a comment above the test or in the commit message.
- Commits are small and name the slice and requirement IDs. Never commit `bin/` or `obj/`. Never force-push.
- Document well: Every function, field, class etc. should have a proper description.

## Hard rules

- NEVER write passwords or tokens to disk, config, logs or the audit log. Credentials stay in memory only.
- NEVER hardcode a domain, OU, group name, path or URL. Read them from the config file described in the spec.
- NEVER run a destructive action (disable, convert mailbox, remove from group, move, delete) without the confirmation the spec requires.
- NEVER swallow an exception. Failures go through the error dialog and the audit log.
- NEVER develop or test against real users. Use a test OU with dummy accounts, and use fakes in unit tests.
- Every workflow step MUST be idempotent and write an audit event.
- Address AD users by `objectGUID`, never by name. Escape all LDAP filter input.
