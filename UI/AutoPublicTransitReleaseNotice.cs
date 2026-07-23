using ColossalFramework.UI;
using ScratchyBald.CitiesSkylines.UI;

namespace AutoPublicTransit
{
    internal static class AutoPublicTransitReleaseNotice
    {
        private static readonly ReleaseNoticeContent Content = new ReleaseNoticeContent(
            "AutoPublicTransit.ShownReleaseNoticeId",
            "v2.3.1",
            "Auto Public Transit Planner 2.3.1",
            "Scan freeze fix",
            "This update makes first-time Bus scans safer:",
            "APT",
            new[]
            {
                "Prevents the Bus overview from interrupting a scan while new lines are still being created.",
                "Adds safeguards so damaged bus data cannot trap the live status check.",
                "Keeps line creation, normal bus selection, dispatch monitoring, and guidance working as before."
            },
            string.Empty,
            null);

        public static void ShowIfNeeded(UIView view)
        {
            OneTimeUpdateNoticePanel.ShowIfNeeded(view, Content);
        }

        public static void DestroyInstance()
        {
            OneTimeUpdateNoticePanel.DestroyInstance();
        }
    }
}
