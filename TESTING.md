# SystemBanner – test checklist

Record the result of each test before release. This checklist lists what to test; it is not evidence that testing has been done.

| | |
|---|---|
| **Version / build** | |
| **Tested by** | |
| **Date** | |
| **Machines (OS, displays, DPI)** | |
| **Workstation / domain controller** | |

Before testing, confirm the classification colours against the current PSPF release.

## Classification display

| # | Test | Expected result | Result / notes |
|---|---|---|---|
| 1 | No policy set | "Classification is not configured." Left- and right-click on the bar do nothing, and nothing behind the bar reacts. | |
| 2 | Configure classification banner: each level, UNOFFICIAL to TOP SECRET | Correct text and colour on every display. Hover makes no change. Updates within about 1 s of `gpupdate /force`, with no flicker. | |
| 3 | Enforce PROTECTED, then set the policy to Not Configured (`gpupdate /force` each time) | Banner still shows PROTECTED. The menu unlocks with PROTECTED ticked. Remove classification returns the banner to "not configured". | |
| 4 | Set banner position: Top only ↔ Top and bottom | Bars added or removed correctly. No gap above the taskbar after repeated classification changes. | |
| 5 | Leftover values present: `Simple`, `Text`, `0`–`5`, `TopAndBottom` (original) and `SimpleAU`, `Debug`, `OverrideLevel` (pre-release builds) | Ignored. The banner is unaffected and the menu is not locked. | |

## Full-screen handling

| # | Test | Expected result | Result / notes |
|---|---|---|---|
| 6 | Hide banner policy **off**, full-screen video | The bottom bar moves to the screen edge over the video, and returns afterwards. | |
| 7 | Hide banner policy **on**, full-screen app on a secondary display while working on the main display | The secondary display's bars hide and the main display's bars stay. The bars reappear when the app closes. | |
| 8 | Hide banner policy on, **maximised** (not full-screen) window | Bars do **not** hide. | |
| 9 | Taskbar thumbnail previews (click a taskbar button with several windows) | Bottom bar stays visible. | |

## Right-click menu

| # | Test | Expected result | Result / notes |
|---|---|---|---|
| 10 | Groups policy Not Configured, standard user | Right-click does nothing. No desktop menu and no focus change. | |
| 11 | Groups policy Not Configured, local admin | Menu shows. Choosing a level gives a UAC prompt in front. Approve: applies. Cancel: nothing changes. No stray taskbar button. Focus returns to the previous window. | |
| 11a | Fresh install, no policy ever applied: set a classification from the menu, both as a normal admin (UAC) and as an admin with a full token (built-in Administrator, domain controller, or UAC off) | Applies first time, with no "registry key does not exist" error. | |
| 12 | Groups policy Disabled | No menu for anyone, including admins. | |
| 13 | Groups policy Enabled with a non-admin group; ACL script **not** run | Menu shows. Choosing a level gives a clear "does not exist / access denied" message. | |
| 14 | As 13, ACL script run for that group | Each level applies within about 1 s, and Remove classification works. Event 1903 is logged, and `OverrideSetBy`/`OverrideSetAt` are written. | |
| 15 | Classification enforced by Group Policy | Menu shows "Managed by Group Policy" at the top, with the options greyed out. | |
| 16 | Standard user not in an allowed group: `reg add HKLM\SOFTWARE\SystemBanner /v ClassificationOverride /t REG_DWORD /d 1 /f` | **Access denied.** | |
| 17 | Domain controller: Domain Admin signed in | Menu shows (default group). GPO linked to the Domain Controllers OU applies. | |

## Lifecycle and robustness

| # | Test | Expected result | Result / notes |
|---|---|---|---|
| 18 | Run `SystemBanner.exe` a second time | No extra bars. | |
| 19 | Uninstall the MSI while SystemBanner is running (interactive and `/qn`) | Bars disappear, with no empty reserved space. No process left running. The install log has no "Failed to shutdown all applications". | |
| 20 | `RemoveSystemBanner.bat` while running | As 19. | |
| 21 | Upgrade the MSI over the previous version | Old version replaced, a single entry in Installed apps, and the banner works after sign-in. | |
| 22 | Multiple displays: connect, disconnect, change resolution and DPI scaling | Bars follow the displays, with no gaps or duplicates. | |
| 23 | Taskbar auto-hide, or taskbar on another edge | Bars reposition correctly. | |
| 24 | Two users signed in (fast user switching or RDP) | Each session has its own banner. | |
| 25 | Installer screens | AVS logo shown and not overlapping text. Licence information page shown. No install-folder page. | |
| 25a | After install, Task Scheduler › Task Scheduler Library | **SystemBanner** task present: "At log on of any user", runs as Users, no time limit. After uninstall it is removed (also with `RemoveSystemBanner.bat`). | |
| 25b | Sign out and in (and on a domain controller) | Banner appears within a second or two of the desktop appearing. A single process in that session (`Get-Process SystemBanner`). | |
| 25c | Restart Explorer (Task Manager › Windows Explorer › Restart) while SystemBanner runs | Within about 2 s the bars reserve their space again, and maximised windows don't cover them. | |
| 26 | Fresh install on a clean machine (no SystemBanner keys) | All defaults in the README registry reference are present: `Classification`=0, `BannerPosition`=0, `FullscreenHide`=0, `OverrideEnabled`=1, `OverrideGroups`=`S-1-5-32-544`, `DebuggingEnabled`=0, `ClassificationOverride`=0. The banner shows "not configured" and admins get the menu. | |
| 27 | Upgrade or reinstall with existing settings (e.g. `BannerPosition`=1 by GPO, `ClassificationOverride`=4 from the menu) | Existing values are **unchanged** after install. Defaults only fill in missing values. | |
| 28 | `InstallSystemBanner.bat` on a clean machine | Same defaults as test 26. | |
