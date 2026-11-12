# SDMate Requirements Spec

Nov 12, 2026 · @Koen

## Purpose and scope

SDMate is a Windows desktop app (WPF) that lets Servicedesk (SD) technicians offboard users and track deletion reminders from one GUI, using their own admin account.

- `REQ-SCOPE-001` The app MUST run entirely on the technician's device. There is no server, web app or shared backend.
- `REQ-SCOPE-002` The app MUST act only with the technician's admin account permissions, nothing more.
- `REQ-SCOPE-003` The left-hand navigation MUST show an Overview home page plus five tabs: Offboarding, Deletion Reminders, Audit Log, Onboarding, AD Change Request.
- `REQ-SCOPE-004` v1 implements: admin sign-in, user lookup helper, offboarding with mail forwarding, deletion reminders, audit log, error handling.

**Out of scope for v1. Do not implement:**

- Onboarding and AD Change Request: stub tab only, showing a "coming later" message.
- Mailbox sharing (delegate access). Forwarding only.
- Entra and Intune actions.
- TOPdesk actions and TOPdesk authentication.

**Glossary**

- SD: Servicedesk. Technician: an SD employee using SDMate.
- Admin account: the technician's separate privileged account, used for AD and Exchange actions.
- Offboarded user: a user for whom all offboarding steps finished.

## Tech stack and architecture

SDMate is a single locally run WPF app on .NET 11, with a thin UI layer on top of code that calls AD and Exchange directly.

| Item | Decision |
| --- | --- |
| Runtime | .NET 11, Windows 10/11 x64 |
| UI | WPF, MVVM with `CommunityToolkit.Mvvm`, `Microsoft.Extensions.DependencyInjection` |
| Active Directory | Native C# via `System.DirectoryServices` and `System.DirectoryServices.AccountManagement`, using the admin credentials. No RSAT dependency. |
| Exchange Online | `ExchangeOnlineManagement` module, run in Windows PowerShell 5.1 |
| Microsoft.Graph | Allowed if a step needs it. No v1 step requires it. |
| Packaging | One self-contained publish output the technician can copy and run |

- `REQ-ARCH-001` Windows PowerShell 5.1 cannot be hosted in-process by .NET 11. The app MUST run `powershell.exe` (5.1) as a child process with `-NoProfile` and `$ErrorActionPreference = 'Stop'`, and return results as JSON. Any error-stream output or non-zero exit code MUST count as a failure.
- `REQ-ARCH-002` The app SHOULD keep one long-lived PowerShell child process per app session, so the Exchange Online connection (and MFA) is established once. Use a simple request/response protocol over stdin/stdout.
- `REQ-ARCH-003` At startup the app MUST check that `powershell.exe` exists, the `ExchangeOnlineManagement` module is installed and a domain controller is reachable, and show a clear message for each failure.
- `REQ-ARCH-004` Workflow steps MUST depend only on interfaces in the Core project, so they can be unit-tested with fakes and run in dry-run mode.

Recommended layout (collapse into one project with the same folders if you prefer one project):

| Project | Responsibility |
| --- | --- |
| `SDMate.App` | WPF views, view models, navigation, dialogs, DI composition root |
| `SDMate.Core` | Models, interfaces, workflow and step engine. No WPF or AD references. |
| `SDMate.Infrastructure` | AD service, PowerShell runner and Exchange service, file-based stores |
| `SDMate.Tests` | Unit tests for Core using fakes |

Core interfaces: `IUserResolver`, `IDirectoryService`, `IMailboxService`, `IAuditLogWriter`, `IReminderStore`, `ISettingsProvider`, `IUserPrompt` (callbacks the workflow uses to ask the technician something).

## Configuration

`REQ-CFG-001` All environment-specific values MUST come from one JSON config file at `%AppData%\SDMate\config.json`; nothing below may be hardcoded in code.

| Key | Meaning | Default |
| --- | --- | --- |
| `upnSuffix` | Domain appended to admin usernames without one (for example `walraven.com`) | none, required |
| `dataFolder` | Folder for audit logs and deletion reminders. Local path or OneDrive folder. | `%AppData%\SDMate\data` |
| `fallbackFolder` | Used when `dataFolder` is unreachable. Also receives a backup copy of reminders. | `%AppData%\SDMate\fallback` |
| `offboardedOuDn` | Distinguished name of the OU `Users who left the company` under `Shared Mailboxes` | none, required |
| `licenceGroups` | AD groups removed during offboarding | `lic_M365 E3 Users`, `lic_M365 F3 Users`, `O365_MDM_Users` |
| `protectedGroups` | Members of these groups cannot be offboarded | `Domain Admins`, `Enterprise Admins` |
| `reminderDaysWithForward` | Default deletion reminder delay when a forward is set | `90` |
| `reminderDaysWithoutForward` | Default deletion reminder delay without a forward | `3` |
| `reminderCheckMinutes` | How often the app checks for due reminders while open | `15` |
| `displayNameDateFormat` | Date suffix appended to the display name | `yyyyMMdd` |

- `REQ-CFG-002` On first run, or when a required key is missing, the app MUST show a settings dialog and refuse to run offboarding until the config is valid.
- `REQ-CFG-003` The config file MUST NOT contain passwords, tokens or secrets.

## Admin sign-in

The app runs under the technician's normal account, which has no admin rights, so it must obtain the admin account before any AD or Exchange action.

- `REQ-AUTH-001` On launch the app MUST show a sign-in screen. No tab is usable until sign-in succeeds.
- `REQ-AUTH-002` The technician enters the admin username with or without the domain. If there is no `@`, append `upnSuffix`. If the domain after `@` differs from `upnSuffix`, reject with a clear message. The result MUST match `^[\w.%+-]+@[\w.-]+\.[a-zA-Z]{2,}$`.
- `REQ-AUTH-003` The password is entered in a `PasswordBox`, kept only as a `SecureString` in memory, and used for AD binds. Sign-in MUST verify it with an AD bind.
- `REQ-AUTH-004` At sign-in the app MUST connect to Exchange Online with `Connect-ExchangeOnline -UserPrincipalName <admin UPN>`. The module handles interactive and MFA sign-in.
- `REQ-AUTH-005` The password MUST NOT be written to disk, the config, any log or the audit log. Tokens cached in memory by Microsoft libraries are acceptable. The app MUST NOT add its own persistent credential or token cache.
- `REQ-AUTH-006` Sign-out and closing the app MUST clear the credentials from memory and disconnect Exchange Online.
- `REQ-AUTH-007` If credentials or a token become invalid mid-run, treat it as an error (see error handling), offer re-sign-in, then offer Retry.

## User lookup helper

One shared helper, `IUserResolver`, turns whatever the technician types into exactly one AD user. It is used for the offboarding list and for the forward address.

- `REQ-LOOKUP-001` The helper MUST accept an email address, UPN, `sAMAccountName`, or first and last name (including `Last, First`).
- `REQ-LOOKUP-002` Input detection, in order:
  1. Contains `@`: match `mail`, `userPrincipalName` or `proxyAddresses` (`smtp:` values).
  2. Contains a space or comma: match `givenName` + `sn`, `displayName` or `name`.
  3. Otherwise: match `sAMAccountName`.
- `REQ-LOOKUP-003` All LDAP filters MUST escape user input (RFC 4515). Names like `O'Brien` and `(`, `*` characters MUST NOT break or alter the query.
- `REQ-LOOKUP-004` Searches MUST be case- and accent-insensitive and ignore contacts and computer objects.
- `REQ-LOOKUP-005` No match: tell the technician and let them retry. Several matches: show a chooser with email, job title and office location, and return only the user the technician picks.
- `REQ-LOOKUP-006` The helper returns a user object containing `objectGUID`, `userPrincipalName`, `displayName`, `mail`, `title`, `physicalDeliveryOfficeName`, `distinguishedName` and enabled state. All later steps use `objectGUID` to address the user.

* `REQ-LOOKUP-007` `UserSearchInputClassifier` MUST be extended, not replaced. It MUST accept `Last, First`, SHOULD accept names with initials such as `J. Smith`, MUST keep the existing `UserSearchInputKind` values and MUST keep all current tests passing. A UPN is classified as an email address and matched against `userPrincipalName` as well as `mail`.

## Offboarding

The Offboarding tab collects users, gathers every input up front, shows one summary for confirmation, then runs the ordered steps per user without further prompts except errors.

**UI and inputs**

- `REQ-OFF-001` The tab MUST show a user list (UPN, job title, office location), a details panel (current user, current step, progress bar) and prompt dialogs.
- `REQ-OFF-002` The technician adds users through the lookup helper. The list is cumulative, and entries can be removed before the run starts.
- `REQ-OFF-003` Per user the technician chooses whether mail forwarding is requested. If yes, the technician enters a forward address through the lookup helper.
- `REQ-OFF-004` The forward target MUST be an enabled AD user with a mailbox, inside the company (same domain), and not the user being offboarded.
- `REQ-OFF-005` The input phase MUST also collect, per user: the deletion reminder date (defaults from config) and, when the user has direct reports, whether to clear their manager field.
- `REQ-OFF-006` Before the run the app shows a summary of all users and chosen options, and the technician MUST confirm. A single invalid key press MUST never count as confirmation.

**Safety checks**

- `REQ-OFF-007` The app MUST refuse to offboard the signed-in admin account, the technician's own regular account, members of `protectedGroups`, and accounts already in the offboarded OU. An already disabled account triggers a warning the technician can accept.
- `REQ-OFF-008` The app SHOULD offer a preview (dry-run) toggle that runs all steps, logs what would change and changes nothing.

**Steps.** Users are processed one at a time in list order. Every step MUST be idempotent, write an audit event, and use the `objectGUID` to address the user.

| # | Step | Applies to | Skippable | Rule |
| --- | --- | --- | --- | --- |
| 1 | Snapshot | All | No | Record before-values in the audit log: display name, OU, group names, manager, direct reports, mailbox type. |
| 2 | Disable AD account | All | No | Cuts access first. |
| 3 | Set mail forward | Forward only | Yes | Exchange: `ForwardingSmtpAddress` set to the forward address, `DeliverToMailboxAndForward` false. Read back to verify. |
| 4 | Convert to shared mailbox | Forward only | Yes | Exchange: set type to Shared, then verify `RecipientTypeDetails` is `SharedMailbox`. |
| 5 | Update display name | All | Yes | Append a space and the date (`displayNameDateFormat`) to the user's name, unless already suffixed. Set `msExchHideFromAddressLists` to true. |
| 6 | Clear address fields | All | Yes | Clear `streetAddress`, `postOfficeBox`, `l`, `st`, `postalCode`, `c`. |
| 7 | Clear manager on direct reports | Only if chosen in the input phase | Yes | Clear `manager` on each direct report. |
| 8 | Remove licence and MDM groups | All | Yes | Remove membership of every group in `licenceGroups`. BLOCKED while step 4 is pending, failed or skipped for a forward user. Log other group memberships without removing them. |
| 9 | Move to offboarded OU | All | Yes | Move to `offboardedOuDn`. |
| 10 | Create deletion reminder | All | No | Store the reminder (see deletion reminders). |
| 11 | Write summary event | All | No | One audit event with the overall result. |

Order rationale: disable first to cut access; Exchange steps before licence removal, because removing the licence before the conversion is verified can lose the mailbox; the OU move comes last among AD changes.

## Deletion reminders

Offboarded users must be deleted from AD later, so the Deletion Reminders tab lists every pending user and prompts the technician when the date is reached.

**Storage**

- `REQ-DEL-001` Reminders live in `dataFolder`, the same location as the audit log, as one JSON file per reminder at `reminders/<objectGUID>.json`. Every technician sharing the folder sees the same list, and OneDrive never has to merge two edits of one file.
- `REQ-DEL-002` If `dataFolder` is unreachable the app MUST use `fallbackFolder`. Every save MUST also write a backup copy under `fallbackFolder`.

**Reminder fields**

| Field | Type | Note |
| --- | --- | --- |
| `objectGuid` | GUID | Identifies the user. Never match by name. |
| `userPrincipalName`, `displayName` | text | For display only |
| `offboardedOn` | date | Date offboarding finished |
| `offboardedBy` | text | Admin UPN of the technician |
| `reminderDate` | date | Must be in the future when set |
| `hasForward`, `forwardAddress` | bool, text | `forwardAddress` empty when no forward |
| `status` | `Pending` or `Deleted` |  |
| `history` | list | Entries of date, technician, action and new date (for postponements) |

**Behaviour**

- `REQ-DEL-003` The default date is `reminderDaysWithForward` days after offboarding for users with a forward, otherwise `reminderDaysWithoutForward`. The technician can change it in the input phase.
- `REQ-DEL-004` The tab lists Pending reminders with a "Review now" button per row that opens the prompt early. Deleted entries stay stored and are hidden by default.
- `REQ-DEL-005` The app MUST check for due reminders (`reminderDate` today or earlier, status `Pending`) at launch and every `reminderCheckMinutes` while open, and queue one prompt per due user.
- `REQ-DEL-006` The prompt MUST offer three options:
  1. **Delete from AD.** Re-verify that the stored `objectGuid` still exists, is disabled and is in the offboarded OU. Ask for explicit confirmation showing the UPN, then delete, mark `Deleted` and write an audit event.
  2. **I deleted it.** Look up the `objectGuid` in AD. If the user still exists, warn and do not mark it done. If it is gone, mark `Deleted`.
  3. **Postpone.** The technician enters a new future date. Append to `history`.

## Audit log

Every technician action is written to that technician's own XML log file, and the Audit Log tab lets any technician read their own and others' logs.

- `REQ-LOG-001` One file per technician at `<dataFolder>/logs/<admin sAMAccountName>.xml`. Use `fallbackFolder` when `dataFolder` is unreachable.
- `REQ-LOG-002` Structure: root `AuditLog` with the technician UPN, and one `Event` per action. Each event has `id` (GUID), `timestamp` (UTC, ISO 8601), `action`, `result` (`Success`, `Failure`, `Skipped` or `DryRun`), a `Target` (`objectGuid`, `userPrincipalName`), `Details` (key/value pairs, including before and after values) and, on failure, an `Error` (message and exception type).
- `REQ-LOG-003` Writes MUST be atomic: write a temp file in the same folder, then replace the log. Only the owning technician writes a log file, so OneDrive has no conflicting edits.
- `REQ-LOG-004` The app MUST log every step attempt (including retries and skips) and every reminder action (create, delete, postpone). Passwords and tokens MUST NEVER be logged.
- `REQ-LOG-005` If the log cannot be written to either folder, the app MUST stop the current action with an error and offer Retry or Exit.
- `REQ-LOG-006` The Audit Log tab loads the signed-in technician's log on open and has a selector listing every `.xml` file in the logs folder (read-only). The table shows time, technician, action, target and result, with filters for date range, target text and result. Selecting a row shows its full details.

Example event:

```xml
<AuditLog technician="tech-adm@example.com">
  <Event id="3f2a9c1e-5b7d-4e8a-9c21-7d4e5f6a8b90" timestamp="2026-11-12T09:15:00Z"
         action="Offboarding.DisableAccount" result="Success">
    <Target objectGuid="7d4e5f6a-8b90-4c21-9e8a-3f2a9c1e5b7d" userPrincipalName="user@example.com" />
    <Details>
      <Item key="enabledBefore" value="true" />
    </Details>
  </Event>
</AuditLog>
```

## Error handling and safety

When a step fails, the app stops, explains what went wrong, and lets the technician decide; it never continues silently.

- `REQ-ERR-001` On any step failure the app MUST stop and show a dialog with the user, the step, what failed and why (message and exception type).
- `REQ-ERR-002` The dialog offers three choices:
  1. **Retry** the step.
  2. **Skip** the step. Offered only when the step is marked skippable, and never when a later step depends on it (for example step 8 after a failed step 4).
  3. **Exit** the action: stop the current user and the rest of the batch. Unprocessed users stay in the list.
- `REQ-ERR-003` Every failure, retry, skip and exit MUST be written to the audit log.
- `REQ-ERR-004` PowerShell calls MUST fail on any error-stream output or non-zero exit code, not only on terminating errors. Cmdlets run with `-ErrorAction Stop`.
- `REQ-ERR-005` Errors that point to an expired or invalid sign-in follow `REQ-AUTH-007`.
- `REQ-ERR-006` The app MUST NOT swallow exceptions. Unexpected exceptions are shown with the same dialog and logged.
- `REQ-ERR-007` No destructive action (disable, convert, remove from group, move, delete) runs without the summary confirmation in `REQ-OFF-006` or the confirmation in `REQ-DEL-006`.

## Repository state

The repository `Aecnoril/SDMate` has one commit with a skeleton GUI and one tested Core class, and no domain logic yet; build on it instead of scaffolding over it.

| Area | What exists | Action |
| --- | --- | --- |
| Solution layout | `src/SDMate.App` (WPF, `net11.0-windows`), `src/SDMate.Core` (`net11.0`), `tests/SDMate.Core.Tests` (xUnit). No `.sln`, no Infrastructure project. | Add a `.sln` and `src/SDMate.Infrastructure` (`net11.0-windows`). Keep existing names and target frameworks. |
| Build output | 170 `bin/` and `obj/` files are committed, and there is no `.gitignore`. | Add a standard .NET `.gitignore` and remove the files from the index with `git rm -r --cached`. |
| UI shell | `MainWindow` is a static mock: sidebar with Overview, Offboarding and Onboarding (all disabled), an "Admin sign-in required" badge, an empty reminders panel and a disabled Start offboarding button. Code-behind is empty. | Move to MVVM with view models and navigation. Keep the visual styling (brushes, nav and primary button styles). |
| Tabs | Deletion Reminders, Audit Log and AD Change Request are missing from the sidebar. | Add them. Overview stays as the home page next to the five tabs in `REQ-SCOPE-003`. |
| Text that contradicts this spec | "Reminder storage: user profile", "Audit events are stored under the current Windows user profile", "Guided, single-user workflow". | Show the configured data folder (`REQ-LOG`, `REQ-DEL`) and describe a multi-user list (`REQ-OFF-002`). |
| Lookup | `UserSearchInputClassifier` in Core classifies email, full name or a single token (treated as `sAMAccountName`), with xUnit tests. It rejects `Last, First` and names with initials such as `J. Smith`. | Keep and extend it (`REQ-LOOKUP-007`). Existing tests MUST keep passing. |
| Not present | DI, config, sign-in, AD or Exchange code, README, `AGENTS.md`. | Per the build order. |

- [ ] Keep Overview as a home page next to a separate Deletion Reminders tab, or fold the reminders into Overview?

## Build order

Build in seven vertical slices; each slice must run end to end before the next one starts. If the SDMate repository already contains code, extend it instead of scaffolding over it.

1. **Shell.** Solution file, the missing Infrastructure project and DI added to the existing skeleton (see Repository state), navigation with Overview plus five tabs (Onboarding and AD Change Request as stubs), config loading and settings dialog, startup prerequisite checks (`REQ-CFG`, `REQ-ARCH-003`). Done when the app starts, tabs navigate and a missing config key opens the settings dialog.
2. **Sign-in.** `REQ-AUTH-001` to `007`. Done when a valid admin account passes the AD bind, Exchange Online connects, and username normalisation has unit tests.
3. **Storage and audit log.** File stores with fallback, backup and atomic writes, plus the Audit Log tab (`REQ-LOG`). Done when events are written and read back, and pointing `dataFolder` at a missing path switches to the fallback.
4. **User lookup helper.** `REQ-LOOKUP`. Done when input detection and LDAP escaping have unit tests (including `O'Brien`) and the chooser dialog works.
5. **Offboarding, AD steps.** Step engine, error dialog, dry-run, steps 1, 2, 5, 6, 7, 9, 10 and 11 (`REQ-OFF`, `REQ-ERR`). Done when a run against a test OU completes and every step is logged.
6. **Offboarding, Exchange steps.** Steps 3, 4 and the step 8 gating. Done when conversion is verified before licence groups are removed, and a failed conversion blocks step 8.
7. **Deletion Reminders.** `REQ-DEL`. Done when due reminders prompt on launch, all three options work and "I deleted it" is verified against AD.

Testing rule: development and manual tests MUST use a test OU with dummy accounts, never real users. Core logic MUST have unit tests using fakes for the interfaces listed under architecture.

## Assumptions and open questions

The spec rests on nine assumptions I made where the draft was silent; change any that are wrong, and four questions remain open.

**Assumptions**

1. Every offboarded user moves to `offboardedOuDn`, with or without a forward. The example script asked Y/N for this.
2. "Disable account" is step 2 because it cuts access first. The rest of the step order is my choice, as you allowed.
3. "Exit" in the error dialog stops the current user and the whole batch.
4. The password is never persisted. Token caching done in memory by Microsoft libraries is fine.
5. Reminders are visible to all technicians sharing the folder, one file per reminder. The audit log stays XML as in the draft.
6. A log write failure stops the current action.
7. v1 does not use Microsoft.Graph.
8. `protectedGroups` defaults to `Domain Admins` and `Enterprise Admins`.
9. "Forwarding" means the mailbox-level `ForwardingSmtpAddress`, not an inbox rule.

**Open questions**

- [ ] PowerShell 5.1 can only be called out-of-process from .NET 11 (`REQ-ARCH-001`). Confirm this, or choose PowerShell 7 hosted in-process instead.
- [ ] Is Exchange hybrid? The example script writes `msExchHideFromAddressLists` directly in AD, which hints at on-premises Exchange attributes. If so, the mailbox type change may have to happen differently.
- [ ] Is the offboarded OU inside Entra Connect sync scope? If not, the move removes the cloud object and its mailbox.
- [ ] Should the app warn when a forwarded mailbox exceeds 50 GB or has a hold, since a shared mailbox then needs a licence?
