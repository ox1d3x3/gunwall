namespace GunWall.Services;

/// <summary>
/// Bringing the engine up, shared by the window and the background service
/// (0.99.199) so the two cannot drift: what one restores at startup, the other
/// restores too.
/// </summary>
public static class EngineStartup
{
    /// <summary>After <c>Initialize</c>: timed blocks re-armed or expired, the launch
    /// backup, and the old-blocklist migration.</summary>
    public static void AfterInitialize(FirewallManager fw, bool launchBackup = true)
    {
        fw.ReconcileTempBlocks();     // re-arm or expire timed blocks after a restart
        if (launchBackup) fw.AutoBackupIfEnabled();     // snapshot the profile on launch (if enabled)
        fw.MigrateLegacyBlocklists(); // move v0.24 IP-filter blocklists to the hosts model
    }

    /// <summary>The background part: orphans removed, filtering restored after a
    /// reboot, GunWall's own permit and service blocks re-asserted, rules for
    /// deleted programs pruned. Returns how many rules were pruned.</summary>
    public static int RestoreAndReconcile(FirewallManager fw)
    {
        fw.ReconcileOrphanFilters();

        // Then the other direction. The reconcile removes filters the kernel has
        // and the store does not; this installs filters the store has and the
        // kernel does not.
        //
        // Since 0.99.143 filters do not survive a restart, so after every reboot
        // the kernel has none of them. Only the tamper watchdog was putting them
        // back, and that is a preference the user can switch off - which one did,
        // leaving 187 of 360 filters absent for eighteen hours while the window
        // read Protected. Restoring your own filtering is not tamper detection
        // and is not optional.
        int restored = fw.RestoreFilteringIfLost();

        // The repair rebuilds the self-permit and service blocks with everything
        // else. Refreshing them at the same time installed the self-permit twice -
        // the constant "4 superseded" in every restart log - and raced the repair
        // over BlockedServices. Now only when nothing needed restoring.
        if (restored == 0)
        {
            fw.EnsureSelfConnectivity(); // GunWall must not block its own update/list/VT traffic
            fw.ReapplyServiceBlocks();   // service rules survive an engine rebuild
        }

        // Dead rules go in the same pass: both are "things the store says that the
        // machine no longer agrees with".
        return fw.PruneDeadRules();
    }
}
