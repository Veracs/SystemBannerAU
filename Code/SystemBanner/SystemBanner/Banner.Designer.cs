//Copyright (c) 2026, Aaron Wawrzyniak. MIT License Applies.
//Modified by Applied Virtual Simulation Pty Ltd (heavily customised from the original).
//Modified fork:
//  - Removed mouse-hover opacity change (banner colour is always what policy sets).
//  - Optional per-display hiding while a full-screen application is shown (FullscreenHide=1).
//  - Right-click "set classification" menu for members of policy-defined groups only.
//    For everyone else right-click does nothing. The banner is never click-through.
using System;
using System.ComponentModel;
using System.Windows.Forms;

namespace SystemBanner
{
    partial class Banner
    {
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style &= (~0x00C00000); // WS_CAPTION
                cp.Style &= (~0x00800000); // WS_BORDER
                //The banner is never click-through (no WS_EX_TRANSPARENT): clicks land on the banner
                //itself and never reach the desktop or windows behind it. WS_EX_NOACTIVATE stops a
                //click from taking focus. Only authorised users get a right-click menu; for everyone
                //else right-click does nothing.
                cp.ExStyle = WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_LAYERED | WS_EX_NOACTIVATE;
                //Unowned top-level window. WinForms normally makes ShowInTaskbar=false forms "owned"
                //by a hidden window; Restart Manager (MSI uninstall/upgrade) and TASKKILL skip owned
                //windows, so the app never received their close requests. WS_EX_TOOLWINDOW already
                //keeps the banner off the taskbar and Alt-Tab.
                cp.Parent = IntPtr.Zero;
                return cp;
            }
        }

        //never steal focus when shown
        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [System.Runtime.InteropServices.DllImport("USER32")]
        private static extern bool GetWindowRect(System.Runtime.InteropServices.HandleRef hWnd, [System.Runtime.InteropServices.In, System.Runtime.InteropServices.Out] ref RECT rect);

        [System.Runtime.InteropServices.DllImport("USER32")]
        private static extern System.IntPtr GetForegroundWindow();

        [System.Runtime.InteropServices.DllImport("USER32", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int GetClassName(System.IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [System.Runtime.InteropServices.DllImport("USER32")]
        private static extern uint GetWindowThreadProcessId(System.IntPtr hWnd, out uint lpdwProcessId);

        //Windows shell surfaces (taskbar, thumbnail previews, desktop, Alt-Tab, Start/flyouts, etc.).
        //Some are monitor-sized and were mistaken for fullscreen apps, which pushed the bottom
        //banner behind the taskbar (e.g. while taskbar thumbnail previews were shown).
        private static readonly System.Collections.Generic.HashSet<string> shellWindowClasses =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW",
            "TaskListThumbnailWnd", "TaskListOverlayWnd", "XamlExplorerHostIslandWindow",
            "Windows.UI.Core.CoreWindow", "MultitaskingViewFrame", "TaskSwitcherWnd",
            "ForegroundStaging", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
            "Shell_InputSwitchTopLevelWindow", "ApplicationFrameWindow_Shell"
        };

        private static string GetWindowClass(System.IntPtr hWnd)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(256);
            return GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
        }

        private bool lastFullscreen;
        private static readonly uint ownProcessId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;

        //--------------------------------------------------------------------
        // Per-display fullscreen detection.
        // Walks top-level windows in z-order (top first). For each display, the first eligible
        // window that overlaps it is what the user sees on top there; if that window covers the
        // whole display, the display is treated as showing a full-screen application.
        // This works on every display at once (e.g. dedicated display panels running full-screen
        // apps while the user works on another screen), not just for the active window.
        // Result is cached briefly and shared by all banners.
        //--------------------------------------------------------------------
        private delegate bool EnumWindowsProc(System.IntPtr hWnd, System.IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("USER32")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, System.IntPtr lParam);
        [System.Runtime.InteropServices.DllImport("USER32")]
        private static extern bool IsWindowVisible(System.IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("USER32")]
        private static extern bool IsIconic(System.IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("USER32", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(System.IntPtr hWnd, int nIndex);
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(System.IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);
        private const int GWL_EXSTYLE = -20;
        private const int DWMWA_CLOAKED = 14;

        private static bool[] fsCovered = new bool[0];
        private static string[] fsCoveringClass = new string[0];
        private static int fsCacheTick;

        private static bool IsDisplayFullscreen(int screenIndex, out string coveringClass)
        {
            System.Windows.Forms.Screen[] screens = System.Windows.Forms.Screen.AllScreens;
            int now = Environment.TickCount;
            if (fsCovered.Length != screens.Length || unchecked(now - fsCacheTick) > 100)
            {
                bool[] covered = new bool[screens.Length];
                bool[] decided = new bool[screens.Length];
                string[] classes = new string[screens.Length];
                int remaining = screens.Length;
                System.Collections.Generic.List<System.IntPtr> windows = new System.Collections.Generic.List<System.IntPtr>();
                EnumWindows(delegate (System.IntPtr h, System.IntPtr l) { windows.Add(h); return true; }, System.IntPtr.Zero);

                foreach (System.IntPtr h in windows)
                {
                    if (remaining == 0) break;
                    if (!IsWindowVisible(h) || IsIconic(h)) continue;
                    int cloaked;
                    if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out cloaked, sizeof(int)) == 0 && cloaked != 0) continue; //other virtual desktops, suspended UWP
                    if ((GetWindowLong32(h, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) != 0) continue; //small overlays/tool windows
                    uint pid;
                    GetWindowThreadProcessId(h, out pid);
                    if (pid == ownProcessId) continue;
                    string cls = GetWindowClass(h);
                    if (shellWindowClasses.Contains(cls)) continue;

                    RECT r = new RECT();
                    if (!GetWindowRect(new System.Runtime.InteropServices.HandleRef(null, h), ref r)) continue;
                    if (r.right <= r.left || r.bottom <= r.top) continue;

                    for (int i = 0; i < screens.Length; i++)
                    {
                        if (decided[i]) continue;
                        System.Drawing.Rectangle b = screens[i].Bounds;
                        bool overlaps = r.left < b.Right && r.right > b.Left && r.top < b.Bottom && r.bottom > b.Top;
                        if (!overlaps) continue;
                        decided[i] = true;
                        remaining--;
                        covered[i] = r.left <= b.Left && r.top <= b.Top && r.right >= b.Right && r.bottom >= b.Bottom;
                        classes[i] = cls;
                    }
                }
                fsCovered = covered;
                fsCoveringClass = classes;
                fsCacheTick = now;
            }
            if (screenIndex < 0 || screenIndex >= fsCovered.Length)
            {
                coveringClass = null;
                return false;
            }
            coveringClass = fsCoveringClass[screenIndex];
            return fsCovered[screenIndex];
        }

        private System.Windows.Forms.Label classificationLabel;
        private System.Windows.Forms.Label leftLabel;
        private System.Windows.Forms.Label rightLabel;
        private System.Windows.Forms.Timer bannerTimer;

        #region SystemBanner

        private void BuildBanner(int bannerScreen, bool bannerPosition)
        {
            int bannerWidth = System.Windows.Forms.Screen.AllScreens[bannerScreen].Bounds.Width;
            int bannerHeight;
            if (System.Windows.Forms.Screen.AllScreens[bannerScreen].Bounds.Height < 1441) //gross magic numbers that ought be fixed ASAP
            {
                bannerHeight = 17;
            }
            else
            {
                bannerHeight = 25;
            }
            //
            // Banner
            //
            this.ClientSize = new System.Drawing.Size(bannerWidth, bannerHeight);
            this.ControlBox = false;
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
            this.MaximizeBox = false;
            this.MinimizeBox = false;
            this.Name = "SystemBanner";
            this.ShowIcon = false;
            this.Left = System.Windows.Forms.Screen.AllScreens[bannerScreen].Bounds.X;
            this.Top = System.Windows.Forms.Screen.AllScreens[bannerScreen].Bounds.Y;
            if (bannerPosition == Program.bannerBottom)
            {
                this.Top = System.Windows.Forms.Screen.AllScreens[bannerScreen].WorkingArea.Bottom - bannerHeight;
            }
            this.Opacity = 0.99; //magic number, balance of readability between the banner text and lower window controls
            this.Text = "SystemBanner";
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
            this.BackColor = System.Drawing.Color.FromArgb(255, Program.bannerColor[0], Program.bannerColor[1], Program.bannerColor[2]);
            this.ForeColor = System.Drawing.Color.FromArgb(255, Program.bannerColor[3], Program.bannerColor[4], Program.bannerColor[5]);
            //Upstream used Magenta as TransparencyKey; removed so a policy colour can never be keyed out.
            //
            //Labels
            //
            this.classificationLabel = new System.Windows.Forms.Label();
            this.classificationLabel.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            this.classificationLabel.Size = new System.Drawing.Size(bannerWidth / 2, bannerHeight);
            this.classificationLabel.Text = Program.bannerText;
            this.classificationLabel.UseMnemonic = false; //don't treat '&' in custom text as an accelerator
            this.classificationLabel.Font = new System.Drawing.Font("Arial", bannerHeight - 3, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            this.classificationLabel.Top = 1;
            this.classificationLabel.Left = (bannerWidth / 4);
            this.Controls.Add(this.classificationLabel);

            if (Program.debug == true)
            {
                this.leftLabel = new System.Windows.Forms.Label();
                this.leftLabel.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
                this.leftLabel.Size = new System.Drawing.Size(160, bannerHeight);
                this.leftLabel.Text = "SCREEN: " + bannerScreen;
                this.leftLabel.Font = new System.Drawing.Font("Arial", bannerHeight - 1, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
                this.leftLabel.Top = 1;
                this.leftLabel.Left = 0;
                this.Controls.Add(this.leftLabel);

                this.rightLabel = new System.Windows.Forms.Label();
                this.rightLabel.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
                this.rightLabel.Size = new System.Drawing.Size(160, bannerHeight);
                this.rightLabel.Text = "DEBUG: " + Program.debug;
                this.rightLabel.Font = new System.Drawing.Font("Arial", bannerHeight - 1, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
                this.rightLabel.Top = 1;
                this.rightLabel.Left = bannerWidth - this.rightLabel.Width;
                this.Controls.Add(this.rightLabel);
            }

            //
            //Right-click menu (authorised users only)
            //
            this.DoubleBuffered = true; //smoother in-place repaint when the classification changes

            eventLogMessage += ("BANNER " + bannerScreen + " DISPLAY COORDINATES?" + System.Environment.NewLine);
            eventLogMessage += (this.Left + "L," + this.Top + "T," + this.Right + "R," + this.Bottom + "B" + System.Environment.NewLine);
            Program.LogEventString(eventLogMessage);

            RefreshBannerTimer();

            void RefreshBannerTimer()
            {
                bannerTimer = new System.Windows.Forms.Timer();
                bannerTimer.Interval = (50); // 0.05 sec
                bannerTimer.Tick += new System.EventHandler(RefreshBanner);
                bannerTimer.Start();
            }

            //Mouse-hover opacity change REMOVED (banner is always fully visible in its configured colour).
            //Full-screen handling, per display:
            //  FullscreenHide=1: banners on a display showing a full-screen app are hidden until it closes.
            //  otherwise: the bottom banner drops to the screen edge over the full-screen app (upstream behaviour).
            void RefreshBanner(object sender, System.EventArgs e)
            {
                if (this.IsDisposed) return;
                if (bannerScreen >= System.Windows.Forms.Screen.AllScreens.Length)
                {
                    //display removed: internal close (the 5 s display check rebuilds banners)
                    Program.closingInternally = true;
                    try { this.Close(); } finally { Program.closingInternally = false; }
                    return;
                }
                string coveringClass;
                bool fullscreen = IsDisplayFullscreen(bannerScreen, out coveringClass);
                if (this.IsDisposed) return;

                //DebuggingEnabled=1 in policy: log which window triggered fullscreen handling (helps tune shellWindowClasses)
                if (fullscreen != lastFullscreen)
                {
                    lastFullscreen = fullscreen;
                    if (Program.debug)
                    {
                        Program.LogEventString("SystemBanner screen " + bannerScreen + (bannerPosition ? " bottom" : " top") +
                            ": fullscreen=" + fullscreen + " top window class='" + coveringClass + "'");
                    }
                }

                bool hide = fullscreen && Program.fullscreenHide;
                if (hide)
                {
                    if (this.Visible) this.Hide();
                    return;
                }
                if (!this.Visible) this.Show(); //ShowWithoutActivation: never steals focus

                if (bannerPosition)
                {
                    //Use the rect the shell actually reserved for this AppBar (not the cached WorkingArea,
                    //which can be stale during a rebuild and caused a gap above the taskbar).
                    int targetTop = fullscreen
                        ? System.Windows.Forms.Screen.AllScreens[bannerScreen].Bounds.Bottom - bannerHeight
                        : (abRect.bottom != 0
                            ? abRect.top
                            : System.Windows.Forms.Screen.AllScreens[bannerScreen].WorkingArea.Bottom - bannerHeight); //fallback if AppBar registration failed
                    if (this.Top != targetTop)
                    {
                        this.Top = targetTop;
                    }
                }
            }
        }

        //Update colours, text and menu on the existing banner (no close/rebuild, so no flicker).
        //Called by Program when the classification or authorisation changes.
        public void ApplyAppearance()
        {
            if (this.IsDisposed) return;
            this.SuspendLayout();
            this.BackColor = System.Drawing.Color.FromArgb(255, Program.bannerColor[0], Program.bannerColor[1], Program.bannerColor[2]);
            this.ForeColor = System.Drawing.Color.FromArgb(255, Program.bannerColor[3], Program.bannerColor[4], Program.bannerColor[5]);
            if (this.classificationLabel != null)
            {
                this.classificationLabel.Text = Program.bannerText;
            }
            this.ResumeLayout();
            this.Invalidate(true);
        }

        //--------------------------------------------------------------------
        // Right-click menu (authorised users only).
        // Uses a native Win32 popup menu (TrackPopupMenuEx), the same kind the taskbar and
        // notification area use. A WinForms ContextMenuStrip opened from this never-activated
        // window created a stray taskbar button. The menu is rebuilt on every right-click so it
        // always reflects current policy/override state. Unauthorised users: right-click does nothing.
        //--------------------------------------------------------------------
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();
        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string lpNewItem);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hWnd, IntPtr lptpm);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const uint MF_STRING = 0x0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
        private const uint TPM_RIGHTBUTTON = 0x2, TPM_NONOTIFY = 0x80, TPM_RETURNCMD = 0x100;
        private const int MenuIdRemove = 100; //classification levels use their own number (1-6) as the menu id

        private void ShowClassificationMenu(int x, int y)
        {
            //re-check membership at open time; fail closed
            if (!Program.IsUserAuthorised()) return;

            bool gpoManaged = Program.bannerSource == Program.BannerSource.GroupPolicy;
            IntPtr menu = CreatePopupMenu();
            if (menu == IntPtr.Zero) return;
            int cmd = 0;
            IntPtr previous = GetForegroundWindow();
            try
            {
                //When Group Policy is enforcing, say so first so it's clear why the options are greyed out
                if (gpoManaged)
                {
                    AppendMenu(menu, MF_STRING | MF_GRAYED, UIntPtr.Zero, "Managed by Group Policy - local changes disabled");
                    AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                }
                for (int level = 1; level < Program.PresetCount; level++)
                {
                    uint flags = MF_STRING;
                    if (gpoManaged) flags |= MF_GRAYED;
                    if (Program.bannerSource == Program.BannerSource.LocalOverride && Program.activeLevel == level) flags |= MF_CHECKED;
                    AppendMenu(menu, flags, (UIntPtr)(uint)level, Program.presetTexts[level].Replace("&", "&&"));
                }
                AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                bool canRemove = !gpoManaged && Program.bannerSource == Program.BannerSource.LocalOverride;
                AppendMenu(menu, MF_STRING | (canRemove ? 0 : MF_GRAYED), (UIntPtr)(uint)MenuIdRemove, "Remove classification");

                //Required for the menu to close when the user clicks elsewhere (allowed: the user just right-clicked us)
                SetForegroundWindow(this.Handle);
                cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY, x, y, this.Handle, IntPtr.Zero);
                PostMessage(this.Handle, 0x0000 /*WM_NULL*/, IntPtr.Zero, IntPtr.Zero);
            }
            finally
            {
                DestroyMenu(menu);
            }

            if (cmd == MenuIdRemove)
            {
                ApplyClassification(0);
            }
            else if (cmd >= 1 && cmd < Program.PresetCount)
            {
                ApplyClassification(cmd);
            }

            //give focus back to whatever the user was working in
            if (previous != IntPtr.Zero && previous != this.Handle && !this.IsDisposed)
            {
                SetForegroundWindow(previous);
            }
        }

        //No confirmation dialog: the change applies immediately (admins under UAC still get the
        //Windows approval prompt). Every change is written to the Application event log (ID 1903).
        private void ApplyClassification(int level)
        {
            string error;
            if (!Program.SetOverride(level, this.Handle, out error))
            {
                MessageBox.Show(this, error, "SystemBanner", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Program.ScheduleRefresh();
        }

        #endregion

        #region APPBAR
        private string eventLogMessage;
        //courtesy of https://www.codeproject.com/Articles/6741/AppBar-using-C

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public int cbSize;
            public System.IntPtr hWnd;
            public int uCallbackMessage;
            public int uEdge;
            public RECT rc;
            public System.IntPtr lParam;
        }

        enum ABMsg : int
        {
            ABM_NEW = 0,
            ABM_REMOVE = 1,
            ABM_QUERYPOS = 2,
            ABM_SETPOS = 3,
            ABM_GETSTATE = 4,
            ABM_GETTASKBARPOS = 5,
            ABM_ACTIVATE = 6,
            ABM_GETAUTOHIDEBAR = 7,
            ABM_SETAUTOHIDEBAR = 8,
            ABM_WINDOWPOSCHANGED = 9,
            ABM_SETSTATE = 10,
            ABM_GETAUTOHIDEBAREX = 11,
            ABM_SETAUTOHIDEBAREX = 12
        }

        enum ABNotify : int
        {
            ABN_STATECHANGE = 0,
            ABN_POSCHANGED = 1,
            ABN_FULLSCREENAPP = 2,
            ABN_WINDOWARRANGE = 3
        }

        enum ABEdge : int
        {
            ABE_LEFT = 0,
            ABE_TOP = 1,
            ABE_RIGHT = 2,
            ABE_BOTTOM = 3
        }

        [System.Runtime.InteropServices.DllImport("SHELL32", CallingConvention = System.Runtime.InteropServices.CallingConvention.StdCall)]
        static extern uint SHAppBarMessage(int dwMessage, ref APPBARDATA pData);
        [System.Runtime.InteropServices.DllImport("USER32")]
        private static extern int RegisterWindowMessage(string msg);
        private int uCallBack;

        //State for this banner's AppBar
        private int abScreen;
        private bool abBottom;
        private int abHeight;
        private RECT abRect;
        private bool abRegistered;

        //Register the AppBar with the shell, then position it using the documented
        //ABM_QUERYPOS -> ABM_SETPOS protocol. Upstream computed the bottom position from
        //Screen.WorkingArea, which can be stale while the previous AppBar is being removed
        //during a rebuild, leaving a gap above the taskbar.
        private void BuildAppBar(int appBarScreen, bool appBarPosition)
        {
            abScreen = appBarScreen;
            abBottom = (appBarPosition == Program.bannerBottom);
            abHeight = System.Windows.Forms.Screen.AllScreens[appBarScreen].Bounds.Height < 1441 ? 17 : 25; //gross magic numbers kept from upstream

            uCallBack = RegisterWindowMessage("SystemBannerAppBarMessage");

            APPBARDATA abd = new APPBARDATA();
            abd.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(abd);
            abd.hWnd = this.Handle;
            abd.uCallbackMessage = uCallBack; //lets the shell tell us when the taskbar/other AppBars move
            abRegistered = SHAppBarMessage((int)ABMsg.ABM_NEW, ref abd) != 0;

            abRect = new RECT();
            PositionAppBar();

            eventLogMessage = ("APPBAR " + appBarScreen + (abBottom ? " BOTTOM" : " TOP") + " REGISTERED=" + abRegistered + System.Environment.NewLine);
            eventLogMessage += (abRect.left + "L," + abRect.top + "T," + abRect.right + "R," + abRect.bottom + "B" + System.Environment.NewLine);
            Program.LogEventString(eventLogMessage);
        }

        private void PositionAppBar()
        {
            if (!abRegistered || this.IsDisposed) return;
            if (abScreen >= System.Windows.Forms.Screen.AllScreens.Length) return;
            System.Drawing.Rectangle b = System.Windows.Forms.Screen.AllScreens[abScreen].Bounds;

            APPBARDATA abd = new APPBARDATA();
            abd.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(abd);
            abd.hWnd = this.Handle;
            abd.uEdge = abBottom ? (int)ABEdge.ABE_BOTTOM : (int)ABEdge.ABE_TOP;
            abd.rc.left = b.Left;
            abd.rc.right = b.Right;
            if (abBottom)
            {
                abd.rc.bottom = b.Bottom;
                abd.rc.top = b.Bottom - abHeight;
            }
            else
            {
                abd.rc.top = b.Top;
                abd.rc.bottom = b.Top + abHeight;
            }

            //Shell adjusts rc to avoid the taskbar and any other AppBars on this edge
            SHAppBarMessage((int)ABMsg.ABM_QUERYPOS, ref abd);
            if (abBottom)
            {
                abd.rc.top = abd.rc.bottom - abHeight;
            }
            else
            {
                abd.rc.bottom = abd.rc.top + abHeight;
            }

            //Only SETPOS when something changed (avoids notification ping-pong)
            if (abd.rc.left != abRect.left || abd.rc.top != abRect.top ||
                abd.rc.right != abRect.right || abd.rc.bottom != abRect.bottom)
            {
                SHAppBarMessage((int)ABMsg.ABM_SETPOS, ref abd);
                abRect = abd.rc;
            }
            this.SetBounds(abRect.left, abRect.top, abRect.right - abRect.left, abHeight);
        }

        //Reposition when the shell reports the taskbar or another AppBar on our edge changed
        protected override void WndProc(ref Message m)
        {
            const int WM_QUERYENDSESSION = 0x0011;
            const int WM_ENDSESSION = 0x0016;
            if (m.Msg == WM_QUERYENDSESSION)
            {
                Program.LogEventString("SystemBanner: WM_QUERYENDSESSION lParam=0x" + m.LParam.ToInt64().ToString("X"));
                //Restart Manager (Windows Installer) / logoff asks whether we can close: yes.
                m.Result = (IntPtr)1;
                return;
            }
            if (m.Msg == WM_ENDSESSION)
            {
                Program.LogEventString("SystemBanner: WM_ENDSESSION wParam=" + m.WParam.ToInt64() + " lParam=0x" + m.LParam.ToInt64().ToString("X"));
                if (m.WParam != IntPtr.Zero)
                {
                    //Release reserved screen space before Windows ends the process.
                    Program.Shutdown("WM_ENDSESSION");
                }
                m.Result = IntPtr.Zero;
                return;
            }
            const int WM_CONTEXTMENU = 0x007B;
            if (m.Msg == WM_CONTEXTMENU)
            {
                //right-clicks on the labels bubble up here too. Unauthorised users: do nothing.
                if (Program.overrideAuthorised)
                {
                    long lp = m.LParam.ToInt64();
                    int x = unchecked((short)(lp & 0xFFFF));
                    int y = unchecked((short)((lp >> 16) & 0xFFFF));
                    if (lp == -1) { x = Cursor.Position.X; y = Cursor.Position.Y; } //keyboard-invoked
                    ShowClassificationMenu(x, y);
                }
                m.Result = IntPtr.Zero;
                return;
            }
            const int WM_MOUSEACTIVATE = 0x0021;
            const int MA_NOACTIVATE = 3;
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                //clicking the banner never activates it or changes focus
                m.Result = (IntPtr)MA_NOACTIVATE;
                return;
            }
            if (uCallBack != 0 && m.Msg == uCallBack)
            {
                if ((int)m.WParam == (int)ABNotify.ABN_POSCHANGED)
                {
                    PositionAppBar();
                }
                return;
            }
            base.WndProc(ref m);
        }

        //Used by Program.Shutdown to release this banner's reserved screen space.
        public void ReleaseAppBar()
        {
            KillAppBar();
        }

        private void KillAppBar()
        {
            if (!abRegistered) return;
            APPBARDATA abd = new APPBARDATA();
            abd.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(abd);
            abd.hWnd = this.Handle;
            uint removed = SHAppBarMessage((int)ABMsg.ABM_REMOVE, ref abd);
            abRegistered = false;
            Program.LogEventString("REMOVING APPBAR WITH PTR " + abd.hWnd + " RESULT=" + removed);
        }
        #endregion
    }
}
