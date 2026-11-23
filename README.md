# SystemBanner

SystemBanner displays the security classification of a Windows system in a coloured bar across the top (and optionally bottom) of every screen.

This version is published by Applied Virtual Simulation Pty Ltd. It is a heavily customised version of [SystemBanner by Aaron Wawrzyniak](https://github.com/awawrzyniak10/SystemBanner), used under the MIT License (see `LICENSE`). See [Differences from the original SystemBanner](#differences-from-the-original-systembanner) below, and `TESTING.md` for the test checklist.

## About

SystemBanner shows the Australian Government security classifications, with colours based on the Protective Security Policy Framework (PSPF) Release 2026 Guidelines, section 9.2.3.2 "Colour-based Markings":

| Classification | Banner colour |
|---|---|
| UNOFFICIAL | White |
| OFFICIAL | White |
| OFFICIAL: Sensitive | Yellow |
| PROTECTED | Blue |
| SECRET | Pink/salmon |
| TOP SECRET | Red |

UNOFFICIAL and OFFICIAL have no PSPF-defined colour. All banner text is black. Only these predefined classifications can be displayed; there is no custom text or colour.

The banner appears as soon as a user signs in and reserves its own strip of each screen, so windows don't cover it. It adapts automatically when displays are added, removed or resized. It never changes on mouse hover and is never click-through. Policy and registry changes take effect within about a second.

SystemBanner is a C# .NET Framework 4.7.2 Windows Forms application, configured through Group Policy (or the equivalent registry values). The original SystemBanner was tested by its author on Windows 10/11 and Windows Server 2016–2025 (x64 and arm64); see `TESTING.md` for testing of this version.

## Installation

Install `SystemBannerSetup.msi` (double-click, or `msiexec /i SystemBannerSetup.msi /qn` for silent deployment, e.g. Intune), or run `InstallSystemBanner.bat` as an administrator.

The installer:

- copies `SystemBanner.exe` to `C:\Program Files\SystemBanner`. This location is fixed: a custom `TARGETDIR=` is refused and the installation is rolled back,
- copies the Group Policy templates to `C:\Windows\PolicyDefinitions` and `C:\Windows\PolicyDefinitions\en-US`,
- registers a scheduled task, **SystemBanner**, that starts SystemBanner at sign-in for every user. It typically starts within a second or two, ahead of normal startup programs, runs as the signed-in user with standard rights, and has no time limit. A startup (Run key) entry is also kept as a backup; only one copy ever runs per session,
- marks SystemBanner as High DPI aware (`HKLM\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers`),
- writes the default settings (see [Registry reference](#registry-reference)) to `HKLM\SOFTWARE\Policies\SystemBanner` and `HKLM\SOFTWARE\SystemBanner`, by running `SystemBanner.exe /install` (which also registers the scheduled task). Only **missing** values are written: an install or upgrade never overwrites settings already applied by Group Policy, Intune or the right-click menu. These values are left in place on removal.

SystemBanner starts at the next sign-in, or run `C:\Program Files\SystemBanner\SystemBanner.exe`. Only one instance runs per signed-in session. If Explorer restarts (or the taskbar loads after SystemBanner), the banners re-reserve their screen space automatically.

### Silent installation

Run from an **elevated** (Run as administrator) prompt. With `/qn` there is no UAC prompt, so a non-elevated silent install simply fails. Deployment tools such as Intune, SCCM or Action1 run as SYSTEM, which covers this.

> `msiexec` returns to the prompt immediately and carries on in the background, so use the commands below, which **wait** for the install to finish and report its exit code. Use the PowerShell commands in PowerShell and the Command Prompt commands in Command Prompt: they are not interchangeable (for example, `%TEMP%` and `start /wait` don't work in PowerShell).

**PowerShell**

```powershell
# Install (log written to C:\Temp\SystemBanner-install.log)
New-Item -ItemType Directory C:\Temp -Force | Out-Null
$p = Start-Process msiexec.exe -Wait -PassThru -ArgumentList '/i "C:\Path\To\SystemBannerSetup.msi" /qn /norestart /l*v "C:\Temp\SystemBanner-install.log"'
"Exit code: $($p.ExitCode)"

# Uninstall
$p = Start-Process msiexec.exe -Wait -PassThru -ArgumentList '/x "C:\Path\To\SystemBannerSetup.msi" /qn /norestart /l*v "C:\Temp\SystemBanner-uninstall.log"'
"Exit code: $($p.ExitCode)"
```

**Command Prompt**

```bat
:: Install (log written to C:\Temp\SystemBanner-install.log)
if not exist C:\Temp mkdir C:\Temp
start "" /wait msiexec /i "C:\Path\To\SystemBannerSetup.msi" /qn /norestart /l*v "C:\Temp\SystemBanner-install.log"
echo Exit code: %ERRORLEVEL%

:: Uninstall
start "" /wait msiexec /x "C:\Path\To\SystemBannerSetup.msi" /qn /norestart /l*v "C:\Temp\SystemBanner-uninstall.log"
echo Exit code: %ERRORLEVEL%
```

Replace `C:\Path\To\` with the MSI's actual location. Use the full path; relative paths such as `.\SystemBannerSetup.msi` are unreliable with `msiexec`. The log is optional: remove `/l*v "…"` if you don't need it.

- `/qn` = no user interface (`/qb` shows a progress bar only); `/norestart` = never restart automatically; `/l*v` = full log.
- Exit codes: **0** = success, **3010** = success, restart required (rare). Any other code is a failure; check the log.
- Do not set `TARGETDIR`. SystemBanner must be installed in `C:\Program Files\SystemBanner`; any other location makes the installation fail and roll back.
- Upgrades: install the newer MSI the same way. The old version is replaced and existing settings are kept. A running SystemBanner closes itself cleanly, and users see the banner again at their next sign-in.
- Intune: upload the MSI as a **line-of-business app**; Intune generates the install and uninstall commands.
- Uninstall without the MSI file: `msiexec /x {PRODUCT-CODE} /qn /norestart`. The product code changes with each version; find it in Settings › Apps or `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall`.

## Removal

Uninstall SystemBanner from Settings › Apps (or `msiexec /x`), or run `RemoveSystemBanner.bat` as an administrator.

Removal also deletes the **SystemBanner** scheduled task. A running SystemBanner closes itself cleanly during uninstall, upgrade or repair: it releases its reserved screen space before exiting, so no empty bars are left. Only a forced kill (Task Manager › End task, or `TASKKILL /F`) can leave empty reserved space behind; restart Windows Explorer or sign out to clear it.

## Administration

Settings are in **Computer Configuration › Administrative Templates › SystemBanner**:

| Policy | What it does |
|---|---|
| **Configure classification banner** | Enforces a classification. While Enabled, the right-click menu is locked. If it later becomes Not Configured or Disabled, the last enforced classification stays displayed until changed or removed. |
| **Set banner position** | Top only (default), or top and bottom. |
| **Hide banner on displays showing a full-screen application** | Off by default. When on, the banner hides on any display where a full-screen application is on top (each display is checked separately), and reappears when it closes. While hidden, that display shows no marking. |
| **Choose which groups can change the classification from the right-click menu** | Not Configured: Administrators. Enabled: only the listed groups. Disabled: nobody. |

**Active Directory:** copy `SystemBanner.admx` and `en-US\SystemBanner.adml` to the domain's Central Store (`\\<domain>\SYSVOL\<domain>\Policies\PolicyDefinitions`). Link the GPO to the **Domain Controllers** OU as well as workstation OUs if SystemBanner is installed on domain controllers.

**Intune:** import the ADMX/ADML as custom templates if available, or deploy the registry values in [Registry reference](#registry-reference) directly.

### Right-click menu

Authorised users can right-click the banner to set the classification on that device (for all users of the device). The menu lists the six classifications (current one ticked) and **Remove classification**, which sets the local classification back to 0 ("Classification is not configured."). When Group Policy is enforcing a classification, the menu shows "Managed by Group Policy - local changes disabled" and the options are greyed out. For everyone else, right-click does nothing.

- **Default (policy Not Configured):** members of `BUILTIN\Administrators` (`S-1-5-32-544`). On workstations this is the local Administrators group (by default including Domain Admins); on domain controllers it is the domain's Builtin\Administrators group (by default including Domain Admins, Enterprise Admins and the domain Administrator). Administrators get a UAC prompt when changing the classification.
- **Other groups:** list them in the policy (names or SIDs, separated by `;`; SIDs recommended) **and** grant them write access to `HKLM\SOFTWARE\SystemBanner` by running `ConfigurationScripts\Set-SystemBannerOverrideAcl.ps1 -Group <SID>` (elevated) on each device. Hiding the menu is not the security control; those registry permissions are.
- Group membership changes take effect after the user signs out and back in.
- Every change is logged to the Application event log (Event ID 1903) and recorded in the registry (who and when).

**Decommissioning or reclassifying a device:** use **Remove classification**, or set `HKLM\SOFTWARE\SystemBanner\ClassificationOverride` to 0, so a previous marking isn't left displayed.

## Registry reference

### Classification values

Classifications are stored as REG_DWORD numbers:

| Value | Classification | Banner colour |
|---|---|---|
| 0 | Not configured | White ("Classification is not configured.") |
| 1 | UNOFFICIAL | White |
| 2 | OFFICIAL | White |
| 3 | OFFICIAL: Sensitive | Yellow |
| 4 | PROTECTED | Blue |
| 5 | SECRET | Pink/salmon |
| 6 | TOP SECRET | Red |

Any value outside 1–6 is treated as not configured.

### Where the classification is stored

| Location | Type | Default | Set by | Meaning |
|---|---|---|---|---|
| `HKLM\SOFTWARE\Policies\SystemBanner\Classification` | REG_DWORD | 0 | Group Policy ("Configure classification banner") | **Enforced** classification. 1–6 takes precedence and locks the right-click menu; 0 = not enforced. |
| `HKLM\SOFTWARE\SystemBanner\ClassificationOverride` | REG_DWORD | 0 | Right-click menu, or Group Policy (last enforced level) | **Local** classification, used only when `Classification` is 0. 0 = not configured; "Remove classification" sets it to 0. |
| `HKLM\SOFTWARE\SystemBanner\OverrideSetBy` | REG_SZ | *(none)* | Same as above | Who set the local classification: a user name, or "Group Policy (last enforced)". |
| `HKLM\SOFTWARE\SystemBanner\OverrideSetAt` | REG_SZ | *(none)* | Right-click menu | When it was set (UTC). Blank when set by Group Policy. |

**Precedence:** `Classification` (1–6, enforced), then `ClassificationOverride` (1–6, local), then "Classification is not configured."

### Other policy values (`HKLM\SOFTWARE\Policies\SystemBanner`)

| Value | Type | Default | Meaning |
|---|---|---|---|
| `BannerPosition` | REG_DWORD | 0 | 0 = top only, 1 = top and bottom |
| `FullscreenHide` | REG_DWORD | 0 | 1 = hide banners on displays showing a full-screen application |
| `OverrideEnabled` | REG_DWORD | 1 | 1 = use `OverrideGroups`; 0 = nobody can use the menu |
| `OverrideGroups` | REG_SZ | `S-1-5-32-544` | Groups allowed to use the right-click menu, separated by `;` (names or SIDs). Default = BUILTIN\Administrators |
| `DebuggingEnabled` | REG_DWORD | 0 | 1 = diagnostic logging (Application event log, Event ID 1902, and `%LOCALAPPDATA%\SystemBanner\SystemBanner-debug.log`), plus on-banner debug labels. Test use only. |

Defaults are written at install time only where a value is missing. A missing value behaves exactly like its default. In gpedit/GPMC these policies still show as **Not Configured** until a GPO sets them; that is expected, because the defaults are not part of any GPO.

Values ignored by this version: `Simple`, `Text`, `0`–`5` and `TopAndBottom` (from the original SystemBanner), and `SimpleAU`, `Debug`, `FullscreenDim` and `HKLM\SOFTWARE\SystemBanner\OverrideLevel` (from pre-release builds of this version).

### Quick check

```powershell
Get-ItemProperty 'HKLM:\SOFTWARE\Policies\SystemBanner','HKLM:\SOFTWARE\SystemBanner' -ErrorAction SilentlyContinue
```

## Differences from the original SystemBanner

- **Classifications:** Australian PSPF classifications replace the United States presets. They use the registry value `Classification` (not the original `Simple`), so a mismatched program and policy show "not configured" rather than the wrong marking.
- **No custom text or colours:** the Custom policies were removed, and leftover custom registry values are ignored.
- **No hover fade; never click-through:** the original faded the banner on mouse hover and let clicks pass through to the desktop.
- **Full-screen handling:** optional per-display hiding replaces the original opacity dimming, and detection works on every display (the original only checked the active window).
- **Right-click menu:** new, group-gated, with Administrators as the default.
- **Last enforced classification is kept** when the policy is removed, instead of reverting to "not configured".
- **Group Policy templates:** renamed and simplified policies; classifications listed lowest to highest.
- **Instant updates:** changes apply within about a second (the original checked every 60 seconds), and classification changes update the bar in place without flicker.
- **Reliability fixes:** bottom-bar gaps after changes; the bar disappearing behind the taskbar during thumbnail previews; duplicate bars when started twice; empty bars left behind after uninstall; preset colour corruption and missed colour changes in the original code.
- **Installer:** AVS branding, licence information page, fixed install folder.

## Developer notes

**Building:** Visual Studio with the .NET desktop development workload (.NET Framework 4.7.2 targeting pack) and the **Microsoft Visual Studio Installer Projects** extension. Build `SystemBanner`, then `SystemBannerSetup`, in Release; the MSI is written to the repository root. Bump the setup project's Version (accepting the new ProductCode) for each release, and keep the UpgradeCode unchanged so new versions replace old ones.

**Design notes** (non-obvious decisions, kept for future maintainers):

- **Bar placement** uses the shell's `ABM_QUERYPOS` → `ABM_SETPOS` sequence and repositions on `ABN_POSCHANGED`. Using `Screen.WorkingArea` caused a one-bar-height gap after rebuilds, because it is stale while the previous bar is being removed.
- **Full-screen detection** walks top-level windows in z-order and, for each display, checks whether the top-most eligible window covers it. Shell surfaces (taskbar, desktop, thumbnails, Alt-Tab), tool windows, cloaked and minimised windows, and SystemBanner's own windows are ignored.
- **Clean shutdown:** Restart Manager (Windows Installer) and `TASKKILL` only send close requests to visible, unowned top-level windows. The banners are therefore unowned tool windows (`cp.Parent = IntPtr.Zero`), and a 1×1 off-screen listener window handles `WM_QUERYENDSESSION`/`WM_ENDSESSION`/`WM_CLOSE`. On any close request, every bar's space is released first, then the process exits from a background thread, with a 1-second forced-exit backup. `Microsoft.Win32.SystemEvents` is deliberately not used, because it can deadlock `Environment.Exit`.
- **Right-click menu** is a native Win32 popup (`TrackPopupMenuEx`); a WinForms `ContextMenuStrip` created a stray taskbar button from the never-activated banner.
- **UAC:** group membership also checks the user's elevated (linked) token. When a direct registry write is denied, SystemBanner relaunches itself elevated with `/setoverride N`; that instance re-checks policy, writes the value, logs it and exits.
- **Last enforced classification** is written by the ADMX `valueList` to `HKLM\SOFTWARE\SystemBanner` (outside `\Policies`, so it intentionally persists when the policy is removed).
- **Instant updates** use `RegNotifyChangeKeyValue` on `HKLM\SOFTWARE\Policies` (subtree) and `HKLM\SOFTWARE\SystemBanner`, with a 0.5-second settle delay; the 60-second poll remains as a backup.
- **Start-up:** a sign-in scheduled task (`SystemBanner.exe /install` registers it, `/uninstall` removes it; Users group principal, priority 4, `ExecutionTimeLimit` PT0S, `MultipleInstancesPolicy` Parallel), with the Run key kept as a fallback. The listener window also handles the `TaskbarCreated` broadcast and rebuilds the banners about 1 second later, so AppBar space is re-registered after Explorer restarts or when SystemBanner starts before the taskbar.
- **Single instance:** per-session mutex `Local\SystemBanner.SingleInstance`; the elevated `/setoverride` helper is exempt.

## Licence

See `LICENSE`. SystemBanner is based on the original SystemBanner by Aaron Wawrzyniak, used under the MIT License; the original copyright and licence notice must be kept with all copies.
