//Copyright (c) 2023, Aaron Wawrzyniak. MIT License Applies.
//Modified by Applied Virtual Simulation Pty Ltd (heavily customised from the original).
//Modified fork: stops per-banner timer on close.
using System;
using System.Windows.Forms;

namespace SystemBanner
{
    public partial class Banner : Form
    {
        public Banner(int instanceScreen, bool instancePosition)
        {
            BuildBanner(instanceScreen, instancePosition);
            BuildAppBar(instanceScreen, instancePosition);
        }
        //A close request that SystemBanner didn't start itself (e.g. TASKKILL without /F, or the
        //installer closing the app) shuts the whole app down cleanly instead of leaving it running
        //with no banners.
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!Program.closingInternally && !Program.shuttingDown)
            {
                Program.Shutdown("external close request (" + e.CloseReason + ")");
            }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (bannerTimer != null)
            {
                bannerTimer.Stop();
                bannerTimer.Dispose();
                bannerTimer = null;
            }
            KillAppBar();
            base.OnFormClosed(e);
        }
    }
}
