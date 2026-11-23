//Copyright (c) 2026, Aaron Wawrzyniak. MIT License Applies.
//Modified by Applied Virtual Simulation Pty Ltd (heavily customised from the original).
//Modified fork: Australian (PSPF) presets, no hover opacity, group-gated local override.
using System;
using System.Security.Principal;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SystemBanner
{
    internal static class Program
    {
        //--------------------------------------------------------------------
        // Registry locations
        //--------------------------------------------------------------------
        // Policy key (written by Group Policy / Intune). Always wins.
        public const string PolicyKeyPath = @"SOFTWARE\Policies\SystemBanner";
        // Local override key (NOT under Policies, so GPO processing never touches it).
        // The installer/ACL script grants SetValue on this key to the authorised group only.
        public const string OverrideKeyPath = @"SOFTWARE\SystemBanner";
        // Value names. "Classification" is deliberately different from upstream's "Simple" so that an
        // upstream binary never misinterprets an Australian preset number as a US marking (and vice versa).
        public const string PresetValueName = "Classification";
        public const string BannerPositionValueName = "BannerPosition";   //0 = top only, 1 = top and bottom
        public const string DebuggingEnabledValueName = "DebuggingEnabled";
        public const string OverrideValueName = "ClassificationOverride";
        public const string OverrideSetByValueName = "OverrideSetBy";
        public const string OverrideSetAtValueName = "OverrideSetAt";
        public const string FullscreenHideValueName = "FullscreenHide";
        public const string OverrideGroupsValueName = "OverrideGroups";
        public const string OverrideEnabledValueName = "OverrideEnabled";
        //Used when the override-groups policy is Not Configured: BUILTIN\Administrators
        public static readonly string[] DefaultOverrideGroups = { "S-1-5-32-544" };

        public enum BannerSource { Unconfigured, GroupPolicy, LocalOverride }

        //debug var for console and event log entries
        public static bool debug;
        public static bool bannerTop = false;
        public static bool bannerBottom = true;
        public static bool bottomBannerEnabled;
        public static bool fullscreenHide;           // default off: banners stay visible over full-screen apps
        public static string[] overrideGroups = new string[0];
        public static bool overrideAuthorised;       // current user is in an override group (evaluated at each refresh)
        public static BannerSource bannerSource = BannerSource.Unconfigured;
        public static int activeLevel;               // preset index in use, 0 if custom/unconfigured

        //storage for initial values to detect changes later
        public static int displayCount = System.Windows.Forms.Screen.AllScreens.Length;
        public static int displayPixelLength;
        public static int displayPixelHeight;
        public static int displayPixelHash;
        public static int[] oldBannerColor;
        public static string oldBannerText;
        public static bool oldBottomBannerEnabled;
        public static bool oldOverrideAuthorised;

        //--------------------------------------------------------------------
        // Australian Government PSPF presets.
        // Index 0 = unconfigured. Colours: { bgR, bgG, bgB, fgR, fgG, fgB }.
        // OS/PROTECTED/SECRET/TOP SECRET RGB values from the PSPF colour-marking table.
        // UNOFFICIAL/OFFICIAL have no PSPF colour; neutral white/black is used.
        // VERIFY against the current PSPF release before deployment.
        //--------------------------------------------------------------------
        public static readonly string[] presetTexts =
        {
            "Classification is not configured.",
            "UNOFFICIAL",
            "OFFICIAL",
            "OFFICIAL: Sensitive",
            "PROTECTED",
            "SECRET",
            "TOP SECRET"
        };
        public static readonly int[][] presetColors =
        {
            new int[6] { 255, 255, 255,   0,   0,   0 }, // unconfigured
            new int[6] { 255, 255, 255,   0,   0,   0 }, // UNOFFICIAL (no PSPF colour)
            new int[6] { 255, 255, 255,   0,   0,   0 }, // OFFICIAL (no PSPF colour)
            new int[6] { 255, 242, 204,   0,   0,   0 }, // OFFICIAL: Sensitive - Yellow
            new int[6] {  79, 129, 189,   0,   0,   0 }, // PROTECTED - Blue
            new int[6] { 229, 184, 183,   0,   0,   0 }, // SECRET - Pink/Salmon
            new int[6] { 255,   0,   0,   0,   0,   0 }  // TOP SECRET - Red
        };
        public static int PresetCount { get { return presetTexts.Length; } }

        //set default color and text as unconfigured in case first run/registry data does not exist
        public static int[] bannerColor = (int[])presetColors[0].Clone();
        public static string bannerText = presetTexts[0];

        private static Timer pendingRefreshTimer;

        //One SystemBanner per Windows session. "Local\" = per session, so each signed-in user
        //(including Remote Desktop sessions) still gets their own banner. Held for the app's lifetime.
        private const string SingleInstanceMutexName = @"Local\SystemBanner.SingleInstance";
        private static System.Threading.Mutex singleInstanceMutex;

        [STAThread]
        static int Main(string[] args)
        {
            //Elevated helper mode, launched by SetOverride via UAC: SystemBanner.exe /setoverride N
            if (args != null && args.Length == 2 && string.Equals(args[0], "/setoverride", StringComparison.OrdinalIgnoreCase))
            {
                int lvl;
                if (!int.TryParse(args[1], out lvl)) return ExitInvalid;
                string err;
                return SetOverrideCore(lvl, true, out err);
            }

            //Installer modes (run as SYSTEM by the MSI, and by the install/remove scripts):
            //  /install       write missing default settings + register the sign-in scheduled task
            //  /initdefaults  write missing default settings only
            //  /uninstall     remove the sign-in scheduled task
            if (args != null && args.Length == 1)
            {
                if (string.Equals(args[0], "/install", StringComparison.OrdinalIgnoreCase))
                {
                    InitDefaults();
                    RegisterLogonTask();
                    return 0;
                }
                if (string.Equals(args[0], "/initdefaults", StringComparison.OrdinalIgnoreCase))
                {
                    return InitDefaults();
                }
                if (string.Equals(args[0], "/uninstall", StringComparison.OrdinalIgnoreCase))
                {
                    RemoveLogonTask();
                    return 0;
                }
            }

            //Only one banner instance per session; a second launch exits silently.
            //(The /setoverride helper above is deliberately exempt.)
            bool createdNew;
            singleInstanceMutex = new System.Threading.Mutex(true, SingleInstanceMutexName, out createdNew);
            if (!createdNew)
            {
                singleInstanceMutex.Dispose();
                return 0;
            }

            //store initial display data
            displayCount = System.Windows.Forms.Screen.AllScreens.Length;
            for (int i = 0; i < System.Windows.Forms.Screen.AllScreens.Length; i++)
            {
                displayPixelLength += System.Windows.Forms.Screen.AllScreens[i].Bounds.Width;
                displayPixelHeight += System.Windows.Forms.Screen.AllScreens[i].Bounds.Height;
                displayPixelHash += System.Windows.Forms.Screen.AllScreens[i].Bounds.GetHashCode();
            }
            GetRegValues();
            RememberState();
            StartShutdownListener();
            BuildBanners();
            RefreshBannerPosTimer();
            RefreshBannerCosTimer();
            StartRegistryWatcher();
            Application.Run();
            singleInstanceMutex.ReleaseMutex();
            singleInstanceMutex.Dispose();
            return 0;
        }

        private static void RememberState()
        {
            oldBannerColor = (int[])bannerColor.Clone();
            oldBannerText = bannerText;
            oldBottomBannerEnabled = bottomBannerEnabled;
            oldOverrideAuthorised = overrideAuthorised;
        }

        private static int? ReadDword(RegistryKey key, string name)
        {
            object v = key.GetValue(name);
            if (v is int) return (int)v;
            return null;
        }

        //--------------------------------------------------------------------
        // Read configuration. Precedence:
        //   1. Group Policy classification (Classification 1..6)
        //   2. Group Policy custom colour/text
        //   3. Local override (HKLM\SOFTWARE\SystemBanner\ClassificationOverride) - only if no GPO classification
        //   4. Unconfigured
        // Always builds NEW arrays (upstream assigned preset arrays by reference and then mutated them).
        //--------------------------------------------------------------------
        private static void GetRegValues()
        {
            int[] newColor = (int[])presetColors[0].Clone();
            string newText = presetTexts[0];
            BannerSource newSource = BannerSource.Unconfigured;
            int newLevel = 0;
            bool newBottomBannerEnabled = false;
            bool newFullscreenHide = false;
            bool newDebug = false;
            string[] newGroups = DefaultOverrideGroups; //policy Not Configured => local Administrators

            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    using (var key = hklm.OpenSubKey(PolicyKeyPath, false))
                    {
                        if (key != null)
                        {
                            //Only predefined classifications are supported. Upstream custom colour (values "0"-"5")
                            //and "Text" policy values are deliberately ignored, so free-text or arbitrary colours
                            //can never be displayed, even if left over from an older deployment.
                            int? s = ReadDword(key, PresetValueName);
                            if (s.HasValue && s.Value > 0 && s.Value < PresetCount)
                            {
                                newColor = (int[])presetColors[s.Value].Clone();
                                newText = presetTexts[s.Value];
                                newLevel = s.Value;
                                newSource = BannerSource.GroupPolicy;
                            }

                            int? tb = ReadDword(key, BannerPositionValueName);
                            newBottomBannerEnabled = tb.HasValue && tb.Value == 1;

                            int? dbg = ReadDword(key, DebuggingEnabledValueName);
                            newDebug = dbg.HasValue && dbg.Value == 1;

                            int? fd = ReadDword(key, FullscreenHideValueName);
                            newFullscreenHide = fd.HasValue && fd.Value == 1;

                            //OverrideEnabled: 1 = Enabled (use OverrideGroups), 0 = Disabled (nobody), absent = Not Configured (default)
                            int? oe = ReadDword(key, OverrideEnabledValueName);
                            string g = key.GetValue(OverrideGroupsValueName) as string;
                            if (oe.HasValue && oe.Value == 0)
                            {
                                newGroups = new string[0];
                            }
                            else if (!string.IsNullOrWhiteSpace(g))
                            {
                                newGroups = g.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                            }
                        }
                    }

                    //local override only applies when GPO has not set a classification
                    if (newSource != BannerSource.GroupPolicy)
                    {
                        using (var okey = hklm.OpenSubKey(OverrideKeyPath, false))
                        {
                            if (okey != null)
                            {
                                int? o = ReadDword(okey, OverrideValueName);
                                if (o.HasValue && o.Value > 0 && o.Value < PresetCount)
                                {
                                    newColor = (int[])presetColors[o.Value].Clone();
                                    newText = presetTexts[o.Value];
                                    newLevel = o.Value;
                                    newSource = BannerSource.LocalOverride;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogEventString("SystemBanner: error reading registry: " + ex.Message);
            }

            bannerColor = newColor;
            bannerText = newText;
            bannerSource = newSource;
            activeLevel = newLevel;
            bottomBannerEnabled = newBottomBannerEnabled;
            fullscreenHide = newFullscreenHide;
            debug = newDebug;
            overrideGroups = newGroups;
            overrideAuthorised = IsUserAuthorised();
        }

        //--------------------------------------------------------------------
        // Group membership check. Accepts "DOMAIN\Group", "BUILTIN\Group" or SIDs ("S-1-5-...").
        // Fails closed: no groups, policy disabled, or any error => not authorised.
        // Under UAC, Administrators (and other privileged groups) are "deny-only" in a normal
        // session, so the elevated linked token is also checked. Such users are authorised, but
        // writing the override then requires a UAC prompt (see SetOverride).
        // NOTE: this only controls whether the menu is shown. Who can actually write the override
        // is enforced by the registry ACL on HKLM\SOFTWARE\SystemBanner.
        //--------------------------------------------------------------------
        public static bool IsUserAuthorised()
        {
            if (overrideGroups == null || overrideGroups.Length == 0) return false;
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    foreach (string raw in overrideGroups)
                    {
                        string name = raw.Trim();
                        if (name.Length == 0) continue;
                        try
                        {
                            SecurityIdentifier sid = name.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                                ? new SecurityIdentifier(name)
                                : (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier));
                            if (IsMember(id, sid)) return true;
                        }
                        catch (Exception)
                        {
                            //unresolvable group name/SID - ignore and continue
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogEventString("SystemBanner: group check failed: " + ex.Message);
            }
            return false;
        }

        private static bool IsMember(WindowsIdentity id, SecurityIdentifier sid)
        {
            if (new WindowsPrincipal(id).IsInRole(sid)) return true;
            //UAC split token: check the elevated (linked) token
            IntPtr linked = GetLinkedToken(id.Token);
            if (linked == IntPtr.Zero) return false;
            try
            {
                using (WindowsIdentity lid = new WindowsIdentity(linked))
                {
                    return new WindowsPrincipal(lid).IsInRole(sid);
                }
            }
            finally
            {
                CloseHandle(linked);
            }
        }

        //True when the user holds a UAC-limited token (i.e. elevation would give more rights).
        public static bool CanElevate()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                {
                    IntPtr linked = GetLinkedToken(id.Token);
                    if (linked == IntPtr.Zero) return false;
                    CloseHandle(linked);
                    return !new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch (Exception) { return false; }
        }

        private const int TokenLinkedToken = 19;
        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr tokenHandle, int tokenInformationClass, IntPtr tokenInformation, int tokenInformationLength, out int returnLength);
        [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private static IntPtr GetLinkedToken(IntPtr token)
        {
            IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(IntPtr.Size);
            try
            {
                int len;
                if (GetTokenInformation(token, TokenLinkedToken, buf, IntPtr.Size, out len))
                {
                    return System.Runtime.InteropServices.Marshal.ReadIntPtr(buf);
                }
                return IntPtr.Zero;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buf);
            }
        }

        //--------------------------------------------------------------------
        // Write (level 1..6) or clear (level 0) the local override.
        // If the direct write is denied and the user is a UAC-limited admin, SystemBanner.exe is
        // relaunched elevated ("/setoverride N") so the user gets a UAC prompt; that instance
        // performs the same checks and the write, then exits.
        //--------------------------------------------------------------------
        public const int ExitOk = 0, ExitNotAuthorised = 2, ExitGpoManaged = 3, ExitWriteFailed = 4, ExitInvalid = 5;

        public static bool SetOverride(int level, out string error)
        {
            return SetOverride(level, IntPtr.Zero, out error);
        }

        //owner = banner window the user clicked; used so the UAC prompt opens in front instead of flashing on the taskbar
        public static bool SetOverride(int level, IntPtr owner, out string error)
        {
            int code = SetOverrideCore(level, false, out error);
            if (code == ExitWriteFailed && CanElevate())
            {
                return RunElevatedSetOverride(level, owner, out error);
            }
            return code == ExitOk;
        }

        //--------------------------------------------------------------------
        // Default values, written at install time ONLY where a value is missing, so an upgrade
        // never overwrites settings applied by Group Policy/Intune or chosen locally.
        // These match the behaviour when a value is absent.
        //--------------------------------------------------------------------
        public const string DefaultOverrideGroup = "S-1-5-32-544"; //BUILTIN\Administrators

        private static int InitDefaults()
        {
            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    using (var pol = hklm.CreateSubKey(PolicyKeyPath, true))
                    {
                        SetIfMissing(pol, PresetValueName, 0);           //0 = no enforced classification
                        SetIfMissing(pol, BannerPositionValueName, 0);   //top only
                        SetIfMissing(pol, FullscreenHideValueName, 0);   //off
                        SetIfMissing(pol, OverrideEnabledValueName, 1);  //use OverrideGroups
                        SetIfMissing(pol, OverrideGroupsValueName, DefaultOverrideGroup);
                        SetIfMissing(pol, DebuggingEnabledValueName, 0); //off
                    }
                    using (var local = hklm.CreateSubKey(OverrideKeyPath, true))
                    {
                        SetIfMissing(local, OverrideValueName, 0);       //0 = not configured
                    }
                }
            }
            catch (Exception ex)
            {
                //Never fail the installation over defaults: every missing value already
                //behaves exactly like its default.
                LogAuditEvent("SystemBanner: could not write default settings: " + ex.Message);
            }
            return 0;
        }

        private static void SetIfMissing(RegistryKey key, string name, object value)
        {
            if (key.GetValue(name) != null) return;
            key.SetValue(name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
        }

        private static int SetOverrideCore(int level, bool elevatedHelper, out string error)
        {
            error = null;
            GetRegValues(); //re-read so we act on current GPO state and group config
            if (!overrideAuthorised)
            {
                error = "You are not a member of a group permitted to change the classification banner.";
                return ExitNotAuthorised;
            }
            if (bannerSource == BannerSource.GroupPolicy)
            {
                error = "The classification is managed by Group Policy and cannot be changed locally.";
                return ExitGpoManaged;
            }
            if (level < 0 || level >= PresetCount)
            {
                error = "Invalid classification level.";
                return ExitInvalid;
            }

            string user = "unknown";
            try { using (var id = WindowsIdentity.GetCurrent()) { user = id.Name; } } catch (Exception) { }

            try
            {
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                //CreateSubKey opens the key if it exists, or creates it if missing (needs rights to
                //HKLM\SOFTWARE: admins with a full token, or the elevated helper). Others get
                //UnauthorizedAccessException, handled below (and elevation is offered if available).
                using (var key = hklm.CreateSubKey(OverrideKeyPath, true))
                {
                    if (key == null)
                    {
                        error = "The override registry key HKLM\\" + OverrideKeyPath + " does not exist and you do not have rights to create it.";
                        return ExitWriteFailed;
                    }
                    //0 = "Remove classification" (back to the installed default of 0 = not configured)
                    key.SetValue(OverrideValueName, level, RegistryValueKind.DWord);
                    key.SetValue(OverrideSetByValueName, user, RegistryValueKind.String);
                    key.SetValue(OverrideSetAtValueName, DateTime.UtcNow.ToString("o"), RegistryValueKind.String);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                error = "Access denied writing HKLM\\" + OverrideKeyPath + ". " +
                        "Ask an administrator to grant your group write access (Set-SystemBannerOverrideAcl.ps1).";
                return ExitWriteFailed;
            }
            catch (Exception ex)
            {
                error = "Failed to write override: " + ex.Message;
                return ExitWriteFailed;
            }

            LogAuditEvent("SystemBanner local classification override " +
                          (level == 0 ? "CLEARED" : "SET to '" + presetTexts[level] + "'") +
                          " by " + user + (elevatedHelper ? " (elevated)" : "") + ".");
            return ExitOk;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private static bool RunElevatedSetOverride(int level, IntPtr owner, out string error)
        {
            error = null;
            try
            {
                //The banner never takes focus, so SystemBanner is normally a background process. Windows shows
                //UAC prompts from background processes as a flashing taskbar button instead of in front.
                //The user has just clicked our menu, so we are allowed to take the foreground briefly, and we
                //pass the banner as the parent window so the prompt opens directly.
                if (owner != IntPtr.Zero)
                {
                    SetForegroundWindow(owner);
                }
                var psi = new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath, "/setoverride " + level)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    ErrorDialogParentHandle = owner
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    p.WaitForExit();
                    switch (p.ExitCode)
                    {
                        case ExitOk: return true;
                        case ExitNotAuthorised: error = "You are not permitted to change the classification banner."; break;
                        case ExitGpoManaged: error = "The classification is managed by Group Policy and cannot be changed locally."; break;
                        case ExitInvalid: error = "Invalid classification level."; break;
                        default: error = "The elevated update failed (exit code " + p.ExitCode + ")."; break;
                    }
                    return false;
                }
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                error = "Administrator approval was cancelled. The classification was not changed.";
                return false;
            }
            catch (Exception ex)
            {
                error = "Could not request administrator approval: " + ex.Message;
                return false;
            }
        }

        //Audit event is always written (not gated on debug). Fails silently if the event source is unavailable.
        public static void LogAuditEvent(string msg)
        {
            string[] sources = { "SystemBanner", "Application" };
            foreach (string src in sources)
            {
                try
                {
                    System.Diagnostics.EventLog.WriteEntry(src, msg, System.Diagnostics.EventLogEntryType.Information, 1903);
                    return;
                }
                catch (Exception) { }
            }
        }

        //--------------------------------------------------------------------
        // Registry change watcher: refreshes the banner almost immediately when policy
        // (gpupdate, Intune, manual edits) or the local override changes, instead of
        // waiting for the 60-second timer (which is kept as a safety net).
        //--------------------------------------------------------------------
        private const int REG_NOTIFY_CHANGE_NAME = 0x1;
        private const int REG_NOTIFY_CHANGE_LAST_SET = 0x4;

        [System.Runtime.InteropServices.DllImport("advapi32.dll")]
        private static extern int RegNotifyChangeKeyValue(Microsoft.Win32.SafeHandles.SafeRegistryHandle hKey, bool bWatchSubtree,
            int dwNotifyFilter, Microsoft.Win32.SafeHandles.SafeWaitHandle hEvent, bool fAsynchronous);

        private static System.Threading.SynchronizationContext uiContext;

        private static void StartRegistryWatcher()
        {
            uiContext = System.Threading.SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            System.Threading.Thread t = new System.Threading.Thread(RegistryWatchLoop);
            t.IsBackground = true;
            t.Name = "SystemBanner registry watcher";
            t.Start();
        }

        private static void RegistryWatchLoop()
        {
            while (true)
            {
                try
                {
                    //event is created first so it outlives the key handles (closing a watched key signals it)
                    using (var changed = new System.Threading.AutoResetEvent(false))
                    using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                    using (var policies = hklm.OpenSubKey(@"SOFTWARE\Policies", false))     //GP recreates SystemBanner under here on refresh
                    using (var software = hklm.OpenSubKey(@"SOFTWARE", false))              //detects creation of SOFTWARE\SystemBanner
                    using (var overrideKey = hklm.OpenSubKey(OverrideKeyPath, false))       //override value changes (may not exist yet)
                    {
                        if (policies != null)
                            RegNotifyChangeKeyValue(policies.Handle, true, REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET, changed.SafeWaitHandle, true);
                        if (software != null)
                            RegNotifyChangeKeyValue(software.Handle, false, REG_NOTIFY_CHANGE_NAME, changed.SafeWaitHandle, true);
                        if (overrideKey != null)
                            RegNotifyChangeKeyValue(overrideKey.Handle, false, REG_NOTIFY_CHANGE_LAST_SET, changed.SafeWaitHandle, true);

                        changed.WaitOne();
                    }
                    //let Group Policy finish writing (it deletes and rewrites values in bursts)
                    System.Threading.Thread.Sleep(500);
                    uiContext.Post(delegate { ScheduleRefresh(); }, null);
                }
                catch (Exception ex)
                {
                    LogEventString("SystemBanner: registry watcher error: " + ex.Message);
                    System.Threading.Thread.Sleep(5000);
                }
            }
        }

        //Defer a refresh slightly so banners are not closed from inside their own menu/dialog handlers.
        public static void ScheduleRefresh()
        {
            if (pendingRefreshTimer != null) return;
            pendingRefreshTimer = new Timer();
            pendingRefreshTimer.Interval = 100;
            pendingRefreshTimer.Tick += (s, e) =>
            {
                pendingRefreshTimer.Stop();
                pendingRefreshTimer.Dispose();
                pendingRefreshTimer = null;
                RefreshBannerCosTick(null, EventArgs.Empty);
            };
            pendingRefreshTimer.Start();
        }

        private static void BuildBanners()
        //This initially builds the SystemBanner(s) on all available windows.
        {
            for (int i = 0; i < System.Windows.Forms.Screen.AllScreens.Length; i++)
            {
                Banner bannerTopInstance = new Banner(i, bannerTop);
                bannerTopInstance.Show();
                if (bottomBannerEnabled)
                {
                    Banner bannerBottomInstance = new Banner(i, bannerBottom);
                    bannerBottomInstance.Show();
                }
            }
        }
        //True while SystemBanner itself is closing banners (rebuild) or shutting down, so a banner
        //can tell an internal close from an external request (installer, taskkill without /F).
        public static bool closingInternally;
        public static bool shuttingDown;

        private static void KillBanners()
        {
            closingInternally = true;
            try
            {
                for (int i = Application.OpenForms.Count - 1; i >= 0; i--)
                {
                    if (Application.OpenForms[i] is Banner)
                    {
                        Application.OpenForms[i].Close();
                    }
                }
            }
            finally
            {
                closingInternally = false;
            }
        }

        //--------------------------------------------------------------------
        // Clean shutdown. Called when Windows (Restart Manager during install/uninstall/upgrade,
        // logoff, shutdown) or another tool asks SystemBanner to close. Releases every banner's
        // reserved screen space first, so no empty bars are left behind, then exits.
        // (A forced kill, e.g. TASKKILL /F, cannot be intercepted; restart Explorer to clear.)
        //--------------------------------------------------------------------
        //--------------------------------------------------------------------
        // Close-request listener.
        // Restart Manager (used by Windows Installer during uninstall/upgrade/repair) and
        // TASKKILL (without /F) send their close requests to an app's top-level windows. The
        // banner bars are owned tool windows, which Restart Manager skipped (seen in an MSI log:
        // "successfully shut down" but the app never received the request and kept running).
        // This hidden, unowned top-level window exists only to receive those requests.
        //--------------------------------------------------------------------
        private sealed class ShutdownListener : NativeWindow
        {
            private const int WM_CLOSE = 0x0010, WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016;

            public ShutdownListener()
            {
                //Visible-but-invisible: Restart Manager and TASKKILL only send close requests to
                //visible, unowned top-level windows. 1x1, off-screen, tool window (no taskbar/Alt-Tab),
                //never activates.
                CreateParams cp = new CreateParams();
                cp.Caption = "SystemBanner";
                cp.Style = unchecked((int)0x80000000) | 0x10000000; //WS_POPUP | WS_VISIBLE
                cp.ExStyle = 0x00000080 | 0x08000000;               //WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
                cp.X = -32000; cp.Y = -32000; cp.Width = 1; cp.Height = 1;
                CreateHandle(cp);
            }

            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case WM_QUERYENDSESSION:
                        LogEventString("SystemBanner listener: WM_QUERYENDSESSION lParam=0x" + m.LParam.ToInt64().ToString("X"));
                        m.Result = (IntPtr)1; //yes, we can close
                        return;
                    case WM_ENDSESSION:
                        LogEventString("SystemBanner listener: WM_ENDSESSION wParam=" + m.WParam.ToInt64() + " lParam=0x" + m.LParam.ToInt64().ToString("X"));
                        if (m.WParam != IntPtr.Zero) Shutdown("WM_ENDSESSION");
                        m.Result = IntPtr.Zero;
                        return;
                    case WM_CLOSE:
                        LogEventString("SystemBanner listener: WM_CLOSE");
                        Shutdown("WM_CLOSE");
                        m.Result = IntPtr.Zero;
                        return;
                }
                if (taskbarCreatedMessage != 0 && m.Msg == taskbarCreatedMessage)
                {
                    //Explorer (re)created the taskbar: at sign-in if we started before it, or after
                    //an Explorer restart/crash. Its AppBar list is new, so re-reserve our space.
                    LogEventString("SystemBanner listener: TaskbarCreated - re-registering banners");
                    ScheduleTaskbarRebuild();
                    return;
                }
                base.WndProc(ref m);
            }
        }
        private static ShutdownListener shutdownListener;

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string lpString);
        private static readonly int taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        private static Timer taskbarRebuildTimer;

        //Rebuild shortly after TaskbarCreated so the new taskbar has finished positioning itself.
        private static void ScheduleTaskbarRebuild()
        {
            if (shuttingDown) return;
            if (taskbarRebuildTimer == null)
            {
                taskbarRebuildTimer = new Timer();
                taskbarRebuildTimer.Interval = 1000;
                taskbarRebuildTimer.Tick += delegate
                {
                    taskbarRebuildTimer.Stop();
                    if (!shuttingDown) RebuildBanners();
                };
            }
            taskbarRebuildTimer.Stop();
            taskbarRebuildTimer.Start();
        }

        //--------------------------------------------------------------------
        // Sign-in scheduled task. Starts SystemBanner for every user at sign-in, typically
        // within a second or two and before programs in the Run key (which Windows delays).
        // Runs as the signed-in user (Users group, standard rights), normal priority, no time
        // limit. One instance per session is enforced by the app's mutex.
        //--------------------------------------------------------------------
        public const string LogonTaskName = "SystemBanner";

        private static string Schtasks
        {
            get { return System.IO.Path.Combine(Environment.SystemDirectory, "schtasks.exe"); }
        }

        private static int RunSchtasks(string arguments)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(Schtasks, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (var p = System.Diagnostics.Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0) LogAuditEvent("SystemBanner: schtasks " + arguments + " failed (" + p.ExitCode + "): " + output.Trim());
                return p.ExitCode;
            }
        }

        private static void RegisterLogonTask()
        {
            string xmlPath = null;
            try
            {
                string exe = System.Security.SecurityElement.Escape(Application.ExecutablePath);
                string xml =
"<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
"<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
"  <RegistrationInfo>\r\n" +
"    <Author>Applied Virtual Simulation Pty Ltd</Author>\r\n" +
"    <Description>Starts SystemBanner (security classification banner) at sign-in for every user.</Description>\r\n" +
"  </RegistrationInfo>\r\n" +
"  <Triggers><LogonTrigger><Enabled>true</Enabled></LogonTrigger></Triggers>\r\n" +
"  <Principals><Principal id=\"Users\"><GroupId>S-1-5-32-545</GroupId><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>\r\n" +
"  <Settings>\r\n" +
"    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>\r\n" +
"    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
"    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
"    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
"    <StartWhenAvailable>false</StartWhenAvailable>\r\n" +
"    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n" +
"    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n" +
"    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
"    <Enabled>true</Enabled>\r\n" +
"    <Hidden>false</Hidden>\r\n" +
"    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n" +
"    <WakeToRun>false</WakeToRun>\r\n" +
"    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n" +
"    <Priority>4</Priority>\r\n" +
"  </Settings>\r\n" +
"  <Actions Context=\"Users\"><Exec><Command>\"" + exe + "\"</Command></Exec></Actions>\r\n" +
"</Task>\r\n";
                xmlPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SystemBannerTask-" + Guid.NewGuid().ToString("N") + ".xml");
                System.IO.File.WriteAllText(xmlPath, xml, System.Text.Encoding.Unicode);
                RunSchtasks("/Create /TN \"" + LogonTaskName + "\" /XML \"" + xmlPath + "\" /F");
            }
            catch (Exception ex)
            {
                //Never fail the installation: the Run key still starts SystemBanner at sign-in.
                LogAuditEvent("SystemBanner: could not register sign-in task: " + ex.Message);
            }
            finally
            {
                try { if (xmlPath != null) System.IO.File.Delete(xmlPath); } catch (Exception) { }
            }
        }

        private static void RemoveLogonTask()
        {
            try
            {
                RunSchtasks("/Delete /TN \"" + LogonTaskName + "\" /F");
            }
            catch (Exception ex)
            {
                LogAuditEvent("SystemBanner: could not remove sign-in task: " + ex.Message);
            }
        }

        private static void StartShutdownListener()
        {
            try
            {
                shutdownListener = new ShutdownListener();
            }
            catch (Exception ex)
            {
                LogEventString("SystemBanner: could not create shutdown listener: " + ex.Message);
            }
            //Note: deliberately NOT using Microsoft.Win32.SystemEvents here. Its process-exit
            //handler can deadlock Environment.Exit when called from a background thread
            //(observed: shutdown logged "process exit" but the process lingered).
        }

        public static void Shutdown(string reason)
        {
            if (shuttingDown) return;
            shuttingDown = true;
            LogEventString("SystemBanner shutdown requested: " + reason);
            int released = 0;
            for (int i = Application.OpenForms.Count - 1; i >= 0; i--)
            {
                Banner b = Application.OpenForms[i] as Banner;
                if (b != null) { b.ReleaseAppBar(); released++; }
            }
            LogEventString("SystemBanner shutdown: released " + released + " banner(s); exiting");

            //Exit from a separate thread, NOT via the UI message loop: during Restart Manager /
            //end-session processing the UI thread can be stalled (observed in an MSI uninstall log:
            //app accepted the close request but was still running 30 s later).
            //Bars are already released, so nothing else needs an orderly shutdown.
            System.Threading.Thread exiter = new System.Threading.Thread(delegate ()
            {
                System.Threading.Thread.Sleep(150); //let the current message (e.g. WM_ENDSESSION) return
                LogEventString("SystemBanner shutdown: process exit");
                //last resort if a clean exit itself hangs
                System.Threading.Thread killer = new System.Threading.Thread(delegate ()
                {
                    System.Threading.Thread.Sleep(1000);
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                });
                killer.IsBackground = true;
                killer.Start();
                Environment.Exit(0);
            });
            exiter.IsBackground = true;
            exiter.Start();
        }
        private static void RebuildBanners()
        {
            KillBanners();
            BuildBanners();
        }

        private static void RefreshBannerPosTimer()
        {
            Timer bannerPosTimer = new Timer();
            bannerPosTimer.Interval = (5000); // 5 sec
            bannerPosTimer.Tick += new EventHandler(RefreshBannerPosTick);
            bannerPosTimer.Start();
        }
        private static void RefreshBannerPosTick(object sender, EventArgs e)
        {
            int currentDisplayPixelLength = 0;
            int currentDisplayPixelHeight = 0;
            int currentDisplayPixelHash = 0;
            for (int i = 0; i < System.Windows.Forms.Screen.AllScreens.Length; i++)
            {
                currentDisplayPixelLength += System.Windows.Forms.Screen.AllScreens[i].Bounds.Width;
                currentDisplayPixelHeight += System.Windows.Forms.Screen.AllScreens[i].Bounds.Height;
                currentDisplayPixelHash += System.Windows.Forms.Screen.AllScreens[i].Bounds.GetHashCode();
            }
            if (displayCount != System.Windows.Forms.Screen.AllScreens.Length ||
                displayPixelLength != currentDisplayPixelLength ||
                displayPixelHeight != currentDisplayPixelHeight ||
                displayPixelHash != currentDisplayPixelHash)
            {
                displayCount = System.Windows.Forms.Screen.AllScreens.Length;
                displayPixelLength = currentDisplayPixelLength;
                displayPixelHeight = currentDisplayPixelHeight;
                displayPixelHash = currentDisplayPixelHash;
                RebuildBanners();
            }
        }
        private static void RefreshBannerCosTimer()
        {
            Timer bannerCosTimer = new Timer();
            bannerCosTimer.Interval = (60000); // 60 sec
            bannerCosTimer.Tick += new EventHandler(RefreshBannerCosTick);
            bannerCosTimer.Start();
        }
        private static bool ColorsEqual(int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
        private static void RefreshBannerCosTick(object sender, EventArgs e)
        //Value comparison (upstream compared array references, so colour changes were missed).
        {
            GetRegValues();
            if (oldBottomBannerEnabled != bottomBannerEnabled)
            {
                //bars need to be added/removed - full rebuild
                RebuildBanners();
                RememberState();
            }
            else if (!ColorsEqual(oldBannerColor, bannerColor) ||
                     oldBannerText != bannerText ||
                     oldOverrideAuthorised != overrideAuthorised)
            {
                //same bars - update colours/text/menu in place (no flicker)
                for (int i = Application.OpenForms.Count - 1; i >= 0; i--)
                {
                    Banner b = Application.OpenForms[i] as Banner;
                    if (b != null) b.ApplyAppearance();
                }
                RememberState();
            }
        }
        public static void LogEventString(string msg)
        {
            if (debug != true) return;
            //Also log to a file: the event log can silently refuse writes when the "SystemBanner"
            //event source isn't registered (it is registered by Set-SystemBannerOverrideAcl.ps1).
            try
            {
                string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SystemBanner");
                System.IO.Directory.CreateDirectory(dir);
                System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "SystemBanner-debug.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + System.Diagnostics.Process.GetCurrentProcess().Id + "] " + msg + Environment.NewLine);
            }
            catch (Exception) { }
            foreach (string src in new[] { "SystemBanner", "Application" })
            {
                try
                {
                    System.Diagnostics.EventLog.WriteEntry(src, msg, System.Diagnostics.EventLogEntryType.Information, 1902);
                    return;
                }
                catch (Exception) { }
            }
        }
    }
}
