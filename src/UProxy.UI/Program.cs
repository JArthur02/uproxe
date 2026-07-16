using UProxy.Core.Windows;

namespace UProxy.UI;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // Crash-recovery: if a previous run left WinINET proxy modified, offer emergency restore.
        if (OperatingSystem.IsWindows())
        {
            var mgr = new WindowsProxyManager();
            if (mgr.HasPendingRestore)
            {
                var result = MessageBox.Show(
                    "μProxy Tool detected that a previous session may have left a temporary system proxy active.\n\n" +
                    "Restore your original Windows proxy settings now?",
                    "μProxy Tool — Emergency Proxy Reset",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (result == DialogResult.Yes)
                {
                    try { mgr.TryEmergencyRestore(); }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Restore failed: " + ex.Message, "μProxy Tool",
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        Application.Run(new MainForm());
    }
}
