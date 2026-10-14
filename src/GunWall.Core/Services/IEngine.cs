// GENERATED from the members MainWindow and AppPropertiesWindow use (stage 4a,
// 0.99.206), then maintained by hand. Check `engine-interface` keeps it complete:
// every `_firewall.X` in the window must be declared here, and every member must
// carry its access tag.
using GunWall.Models;

namespace GunWall.Services;

/// <summary>
/// Everything the window asks of the firewall engine (service split stage 4a).
/// Two implementations: <see cref="FirewallManager"/> itself, in the same
/// process (the portable copy, and the window until 4b), and the remote engine,
/// which sends each call to the background service over the engine pipe.
///
/// Each member is tagged with what it does to protection (<see cref="Access"/>).
/// The service enforces the tag from stage 4c: with Protect settings on, a
/// Loosen call needs an administrator unlock; Read, Preference and Tighten never
/// do.
/// </summary>
public interface IEngine
{
    [EngineAccess(Access.Read)] int ActiveFilterCount { get; }
    [EngineAccess(Access.Read)] string ActiveRuleProfile { get; }
    [EngineAccess(Access.Loosen)] void AddCustomRule(CustomRule rule);
    [EngineAccess(Access.Tighten)] void AddEntityRule(EntityRule rule);
    [EngineAccess(Access.Tighten)] int AddToBlocklist(IEnumerable<string> addresses);
    [EngineAccess(Access.Read)] bool AlertsEnabled { get; }
    [EngineAccess(Access.Loosen)] void AllowApp(string exePath, string displayName);
    [EngineAccess(Access.Local)] bool AlwaysConfirmExit { get; }
    [EngineAccess(Access.Local)] bool AlwaysOnTop { get; }
    [EngineAccess(Access.Local)] int AppIconSize { get; }
    [EngineAccess(Access.Read)] int AppRuleCount { get; }
    [EngineAccess(Access.Loosen)] int ApplyRuleProfile(string name);
    [EngineAccess(Access.Read)] bool AutoAllowCoreWindows { get; }
    [EngineAccess(Access.Read)] bool AutoBackup { get; }
    [EngineAccess(Access.Read)] bool AutoUpdateCheck { get; }
    [EngineAccess(Access.Read)] bool AutoUpdateDownload { get; }
    [EngineAccess(Access.Tighten)] void BlockApp(string exePath, string displayName);
    [EngineAccess(Access.Loosen)] void BlockAppDirection(string exePath, string displayName, bool outbound);
    [EngineAccess(Access.Loosen)] DateTime BlockAppTemporarily(string exePath, string displayName, TimeSpan duration);
    [EngineAccess(Access.Read)] IReadOnlyCollection<string> BlockedServiceNames { get; }
    [EngineAccess(Access.ReadProfile)] IReadOnlyList<string> Blocklist { get; }
    [EngineAccess(Access.Read)] int BlocklistDomainCount(string key);
    [EngineAccess(Access.Read)] List<string> BlocklistDomains(string key);
    [EngineAccess(Access.Read)] int BlocklistExcludedCount(string key);
    [EngineAccess(Access.Read)] HashSet<string> BlocklistExclusions(string key);
    [EngineAccess(Access.ReadProfile)] bool BypassesBlocklists(string exePath);
    [EngineAccess(Access.Loosen)] void CancelSnooze();
    [EngineAccess(Access.Tighten)] FirewallManager.TamperReport CheckIntegrity(bool repair);
    [EngineAccess(Access.Preference)] void CleanUpdateFolder();
    [EngineAccess(Access.Loosen)] void ClearBlocklist();
    [EngineAccess(Access.Loosen)] int ClearDomainReactiveBlocks();
    [EngineAccess(Access.Loosen)] void ClearEntityReactiveBlocks();
    [EngineAccess(Access.Preference)] void ClearReviewWindowPending(string why);
    [EngineAccess(Access.Preference)] void ClearStalePendingUpdate();
    [EngineAccess(Access.Loosen)] void ClearStore();
    [EngineAccess(Access.Local)] bool ConfirmClearLogs { get; }
    [EngineAccess(Access.Preference)] string CreateBackup();
    [EngineAccess(Access.Read)] string CurrentDnsProvider { get; }
    [EngineAccess(Access.Read)] int CustomDomainCount { get; }
    [EngineAccess(Access.Read)] string CustomListPath { get; }
    [EngineAccess(Access.Read)] int CustomRuleCount { get; }
    [EngineAccess(Access.ReadProfile)] IReadOnlyList<CustomRule> CustomRules { get; }
    [EngineAccess(Access.Read)] bool DbAutoRefresh { get; }
    [EngineAccess(Access.Read)] bool DbRefreshDue(bool geo);
    [EngineAccess(Access.Read)] int DbRefreshHours { get; }
    [EngineAccess(Access.Loosen)] void DeleteProfile(string name);
    [EngineAccess(Access.Read)] bool DnsBlockCloakedCnames { get; }
    [EngineAccess(Access.Read)] bool DnsDohFallback { get; }
    [EngineAccess(Access.Read)] string DnsDohUrl { get; }
    [EngineAccess(Access.Read)] bool DnsObserveSystemLookups { get; }
    [EngineAccess(Access.Read)] bool DnsRedirectActive { get; }
    [EngineAccess(Access.Read)] IReadOnlyList<string> DnsResolverBlocklist { get; }
    [EngineAccess(Access.Read)] int DnsResolverPort { get; }
    [EngineAccess(Access.Read)] string DnsResolverUpstream { get; }
    [EngineAccess(Access.Read)] List<SavedAdapterDns> DnsSavedAdapters { get; }
    [EngineAccess(Access.Local)] int DownloadAndLoadGeoIp();
    [EngineAccess(Access.ReadProfile)] AppStatus EffectiveStatus(string exePath);
    [EngineAccess(Access.Tighten)] void EndSnooze();
    [EngineAccess(Access.Preference)] void EnsureSelfConnectivity();
    [EngineAccess(Access.Local)] void EnsureSettingsLoaded();
    [EngineAccess(Access.Read)] int EntityReactiveBlockCount { get; }
    [EngineAccess(Access.Read)] IReadOnlyList<EntityRule> EntityRules { get; }
    [EngineAccess(Access.Read)] bool EtwMeterEnabled { get; }
    [EngineAccess(Access.Preference)] void EventLog(string message);
    [EngineAccess(Access.Read)] bool EventLogEnabled { get; }
    [EngineAccess(Access.Read)] bool ExperimentalEvents { get; }
    [EngineAccess(Access.Read)] string ExplainVerdict(string exePath, string remoteAddress, int remotePort, string protocol, bool outbound, out bool blocked);
    [EngineAccess(Access.Local)] void ExportDiagnostics(string destZipPath);
    [EngineAccess(Access.Local)] void ExportProfile(string filePath);
    [EngineAccess(Access.Read)] bool FullscreenSilent { get; }
    [EngineAccess(Access.Local)] GeoIpService GeoIp { get; }
    [EngineAccess(Access.Read)] bool GeoIpActive { get; }
    [EngineAccess(Access.Read)] bool GeoIpApiActive { get; }
    [EngineAccess(Access.Read)] string GeoIpApiUrl { get; }
    [EngineAccess(Access.Read)] bool GeoIpDatabaseOnDisk { get; }
    [EngineAccess(Access.Read)] DateTime? GeoIpLastRefresh { get; }
    [EngineAccess(Access.Read)] string GeoIpLastResult { get; }
    [EngineAccess(Access.Read)] bool GeoIpLoaded { get; }
    [EngineAccess(Access.Read)] string GeoIpMode { get; }
    [EngineAccess(Access.Read)] int GeoIpRangeCount { get; }
    [EngineAccess(Access.Read)] int GeoIpRangeCountV6 { get; }
    [EngineAccess(Access.Read)] AppAccessPolicy? GetAccessPolicy(string exePath);
    [EngineAccess(Access.Read)] string GetDeviceNote(string? mac);
    [EngineAccess(Access.ReadProfile)] string GetHash(string exePath);
    [EngineAccess(Access.ReadProfile)] string GetNote(string exePath);
    [EngineAccess(Access.Local)] AppAccessPolicy GetOrCreateAccessPolicy(string exePath);
    [EngineAccess(Access.ReadProfile)] IReadOnlyList<FirewallRule> GetRules();
    [EngineAccess(Access.Read)] VtCacheEntry? GetVtCached(string sha256);
    [EngineAccess(Access.Read)] bool GroupPrompts { get; }
    [EngineAccess(Access.Read)] bool HashesEnabled { get; }
    [EngineAccess(Access.Local)] int ImportProfile(string filePath);
    [EngineAccess(Access.Loosen)] int ImportWindowsFirewallRules();
    [EngineAccess(Access.ReadProfile)] bool IsAllowed(string exePath);
    [EngineAccess(Access.ReadProfile)] bool IsBlocked(string exePath);
    [EngineAccess(Access.Read)] bool IsBlocklistOn(string key);
    [EngineAccess(Access.Read)] bool IsBlocklistViaWfp(string key);
    [EngineAccess(Access.Read)] bool IsFirstRun { get; }
    [EngineAccess(Access.Read)] bool IsKnownApp(string exePath);
    [EngineAccess(Access.Read)] bool IsP2pBlocked(string exePath);
    [EngineAccess(Access.Read)] bool IsScopeBlocked(string exePath, string scope);
    [EngineAccess(Access.Read)] bool IsServiceBlocked(string serviceName);
    [EngineAccess(Access.ReadProfile)] bool IsSilent(string exePath);
    [EngineAccess(Access.Read)] bool IsSnoozed { get; }
    [EngineAccess(Access.ReadProfile)] bool IsSystemRuleOn(string key);
    [EngineAccess(Access.Read)] bool KeepUnusedApps { get; }
    [EngineAccess(Access.Read)] FirewallManager.PurgeResult? LastPurge { get; }
    [EngineAccess(Access.Read)] DateTime? LastUpdateCheck { get; }
    [EngineAccess(Access.Read)] string LastUpdateCheckResult { get; }
    [EngineAccess(Access.Read)] List<(string Name, DateTime When)> ListBackups();
    [EngineAccess(Access.Read)] List<string> ListProfiles();
    [EngineAccess(Access.Local)] void LoadCategoryColors();
    [EngineAccess(Access.Local)] void LoadGeoIp();
    [EngineAccess(Access.Loosen)] int LoadProfile(string name);
    [EngineAccess(Access.Read)] bool LockdownEngaged { get; }
    [EngineAccess(Access.Preference)] void MarkFirstRunComplete(bool freshInstall = true);
    [EngineAccess(Access.Read)] int MaxLogEntries { get; }
    [EngineAccess(Access.Read)] int MaxLogFileMB { get; }
    [EngineAccess(Access.Local)] IReadOnlyList<string> MutedAlertCategories { get; }
    [EngineAccess(Access.Preference)] void NoteDbRefresh(bool geo, bool success, string message);
    [EngineAccess(Access.Preference)] void NoteUpdateCheck(string result, bool succeeded);
    [EngineAccess(Access.Local)] bool NotificationSound { get; }
    [EngineAccess(Access.Read)] DateTime? OuiLastRefresh { get; }
    [EngineAccess(Access.Read)] string OuiLastResult { get; }
    [EngineAccess(Access.Read)] bool PacketFileLogging { get; }
    [EngineAccess(Access.Read)] string PendingUpdatePath { get; }
    [EngineAccess(Access.Read)] string PendingUpdateVersion { get; }
    [EngineAccess(Access.Read)] bool PopupDefaultAllow { get; }
    [EngineAccess(Access.Read)] int PopupTimeoutSeconds { get; }
    [EngineAccess(Access.Read)] string ProfileFolder { get; }
    [EngineAccess(Access.Local)] event Action? ProtectionChanged;
    [EngineAccess(Access.Loosen)] int PurgeExpiredTimers();
    [EngineAccess(Access.Loosen)] int PurgeUnusedApps();
    [EngineAccess(Access.Tighten)] bool RebuildHostsBlock();
    [EngineAccess(Access.Loosen)] bool RemoveAllFiltering();
    [EngineAccess(Access.Loosen)] bool RemoveCustomRule(string id);
    [EngineAccess(Access.Loosen)] void RemoveEntityRule(string id);
    [EngineAccess(Access.Loosen)] int ResetSettingsToDefaults();
    [EngineAccess(Access.Loosen)] int RestoreBackup(string name);
    [EngineAccess(Access.Read)] bool ReviewWindowPending { get; }
    [EngineAccess(Access.Read)] IEnumerable<string> RuleProfileNames { get; }
    [EngineAccess(Access.ReadProfile)] string RulesetFingerprint { get; }
    [EngineAccess(Access.Read)] bool RunAtStartup { get; }
    [EngineAccess(Access.Loosen)] void SaveAccessPolicy(string exePath);
    [EngineAccess(Access.Loosen)] void SetAccessPolicy(string exePath, AppAccessPolicy policy);
    [EngineAccess(Access.Loosen)] int ImportProfileJson(string json);
    [EngineAccess(Access.Preference)] void ReloadGeoIpTables();
    [EngineAccess(Access.Preference)] void RecordRunAtStartup(bool enabled);
    [EngineAccess(Access.Preference)] void MarkVersionSeen();
    [EngineAccess(Access.Read)] string ServiceStatusReport();
    [EngineAccess(Access.Local)] void SaveCategoryColors();
    [EngineAccess(Access.TightenIfTrue)] void SaveDnsCloakConfig(bool enabled);
    [EngineAccess(Access.Loosen)] void SaveDnsDohConfig(string url, bool fallback);
    [EngineAccess(Access.Loosen)] void SaveDnsRedirectState(bool active, bool gaming, List<SavedAdapterDns>? saved);
    [EngineAccess(Access.Loosen)] void SaveDnsResolverConfig(int port, string upstream, IEnumerable<string> blocklist);
    [EngineAccess(Access.Preference)] void SaveProfile(string name);
    [EngineAccess(Access.Preference)] void SaveRuleProfile(string name);
    [EngineAccess(Access.Loosen)] void SaveVtResult(string sha256, bool found, int flagged, int total);
    [EngineAccess(Access.Local)] void SetAlertCategoryMuted(string cat, bool muted);
    [EngineAccess(Access.Preference)] void SetAlertsEnabled(bool enabled);
    [EngineAccess(Access.Local)] void SetAlwaysConfirmExit(bool v);
    [EngineAccess(Access.Local)] void SetAlwaysOnTop(bool v);
    [EngineAccess(Access.Local)] void SetAppIconSize(int px);
    [EngineAccess(Access.TightenIfFalse)] void SetAutoAllowCoreWindows(bool v);
    [EngineAccess(Access.Preference)] void SetAutoBackup(bool v);
    [EngineAccess(Access.Preference)] void SetAutoUpdateCheck(bool on);
    [EngineAccess(Access.Preference)] void SetAutoUpdateDownload(bool on);
    [EngineAccess(Access.TightenIfTrue)] bool SetBlocklistEnabled(string key, bool on);
    [EngineAccess(Access.Loosen)] bool SetBlocklistExclusions(string key, IEnumerable<string> excluded);
    [EngineAccess(Access.TightenIfFalse)] int SetBypassBlocklists(string exePath, bool bypass);
    [EngineAccess(Access.Local)] void SetConfirmClearLogs(bool v);
    [EngineAccess(Access.Loosen)] int SetCustomListDomains(string sourcePath, IReadOnlyList<string> domains, bool first, bool last);
    [EngineAccess(Access.Read)] DateTime CustomListImportedUtc { get; }
    [EngineAccess(Access.Loosen)] void SetCustomRuleProtected(string id, bool prot);
    [EngineAccess(Access.Preference)] void SetDbAutoRefresh(bool on);
    [EngineAccess(Access.Preference)] void SetDbRefreshHours(int hours);
    [EngineAccess(Access.Preference)] void SetDeviceNote(string? mac, string note);
    [EngineAccess(Access.TightenIfTrue)] void SetDnsObserveSystemLookups(bool on);
    [EngineAccess(Access.Loosen)] int SetDnsProvider(string key);
    [EngineAccess(Access.Preference)] void SetEtwMeterEnabled(bool v);
    [EngineAccess(Access.Preference)] void SetEventLogEnabled(bool v);
    [EngineAccess(Access.TightenIfTrue)] void SetExperimentalEvents(bool v);
    [EngineAccess(Access.Preference)] void SetFullscreenSilent(bool v);
    [EngineAccess(Access.Loosen)] void SetGeoIpSource(string mode, string url);
    [EngineAccess(Access.Preference)] void SetGroupPrompts(bool v);
    [EngineAccess(Access.TightenIfTrue)] void SetHashesEnabled(bool v);
    [EngineAccess(Access.Preference)] void SetKeepUnusedApps(bool v);
    [EngineAccess(Access.TightenIfTrue)] void SetLockdown(bool engaged);
    [EngineAccess(Access.Preference)] void SetMaxLogEntries(int v);
    [EngineAccess(Access.Preference)] void SetMaxLogFileMB(int v);
    [EngineAccess(Access.Preference)] void SetNote(string exePath, string note);
    [EngineAccess(Access.Local)] void SetNotificationSound(bool v);
    [EngineAccess(Access.TightenIfTrue)] void SetP2pBlock(string exePath, bool blocked);
    [EngineAccess(Access.Preference)] void SetPacketFileLogging(bool v);
    [EngineAccess(Access.Preference)] void SetPendingUpdate(string version, string path);
    [EngineAccess(Access.TightenIfFalse)] void SetPopupDefaultAllow(bool v);
    [EngineAccess(Access.Loosen)] void SetPopupTimeoutSeconds(int v);
    [EngineAccess(Access.Local)] void SetRunAtStartup(bool enabled);
    [EngineAccess(Access.TightenIfTrue)] void SetScopeBlock(string exePath, string scope, bool blocked);
    [EngineAccess(Access.TightenIfTrue)] bool SetServiceBlocked(string serviceName, bool blocked);
    [EngineAccess(Access.Loosen)] void SetSilent(string exePath, string displayName, bool silent);
    [EngineAccess(Access.Local)] void SetStartMinimized(bool v);
    [EngineAccess(Access.TightenIfTrue)] void SetStrictMode(bool enabled);
    [EngineAccess(Access.SystemRuleSwitch)] void SetSystemRule(string key, bool enabled);
    [EngineAccess(Access.TightenIfTrue)] void SetTamperWatch(bool on);
    [EngineAccess(Access.Local)] void SetThemeDark(bool v);
    [EngineAccess(Access.Local)] void SetTrayNotifications(bool v);
    [EngineAccess(Access.Local)] void SetTraySingleClick(bool v);
    [EngineAccess(Access.Local)] void SetUiFontFamily(string v);
    [EngineAccess(Access.Local)] void SetUiZoomPercent(int v);
    [EngineAccess(Access.Preference)] void SetUpdateCheckDays(int days);
    [EngineAccess(Access.Preference)] void SetVirusTotalApiKey(string v);
    [EngineAccess(Access.Loosen)] DateTime SnoozeProtection(TimeSpan duration);
    [EngineAccess(Access.Read)] DateTime SnoozeUntil { get; }
    [EngineAccess(Access.Local)] bool StartMinimized { get; }
    [EngineAccess(Access.Read)] bool StrictMode { get; }
    [EngineAccess(Access.Read)] int SystemRuleCount { get; }
    [EngineAccess(Access.Read)] bool TamperWatchEnabled { get; }
    [EngineAccess(Access.Local)] System.Threading.Tasks.Task<string> TestGeoIpApiAsync(string url);
    [EngineAccess(Access.Local)] bool ThemeDark { get; }
    [EngineAccess(Access.Local)] bool TrayNotifications { get; }
    [EngineAccess(Access.Local)] bool TraySingleClick { get; }
    [EngineAccess(Access.Local)] string UiFontFamily { get; }
    [EngineAccess(Access.Local)] int UiZoomPercent { get; }
    [EngineAccess(Access.Preference)] System.Threading.Tasks.Task<string> UpdateBlocklistsOnlineAsync();
    [EngineAccess(Access.Read)] int UpdateCheckDays { get; }
    [EngineAccess(Access.Read)] bool UpdateCheckDue();
    [EngineAccess(Access.Read)] string UpdateFolder { get; }
    [EngineAccess(Access.Read)] string UpgradedFrom { get; }
    [EngineAccess(Access.Read)] List<Wfp.WfpEngine.LayerProbe> VerifyKernelLayers();
    [EngineAccess(Access.Read)] bool VerifyRecoveryPath(out string detail);
    [EngineAccess(Access.Local)] string VirusTotalApiKey { get; }
    [EngineAccess(Access.Read)] FirewallManager.VtKeyState VirusTotalKeyState { get; }
    [EngineAccess(Access.Read)] int VtCacheCount { get; }
    [EngineAccess(Access.Local)] void WarmBlocklistDomains();

    // ---- stage 4c (0.99.229): the window no longer runs as administrator ----

    /// <summary>Protect settings: changes that loosen protection need an
    /// administrator unlock (STAGE-4 §2.6). Turning it on is stricter; off, looser.</summary>
    [EngineAccess(Access.Read)] bool ProtectSettings { get; }
    [EngineAccess(Access.TightenIfTrue)] void SetProtectSettings(bool on);
    /// <summary>A nonce for "GunWall.exe --authorize". Answered by the service's
    /// gate for the calling window (EngineServer); "" in-process - nothing to unlock.</summary>
    [EngineAccess(Access.Read)] string BeginSettingsUnlock();
    /// <summary>Seconds of unlock left for the calling window; -1 when nothing is
    /// locked here (in-process).</summary>
    [EngineAccess(Access.Read)] int SettingsUnlockSecondsLeft();
    [EngineAccess(Access.Preference)] void LockSettingsNow();

    /// <summary>Work that writes GunWall's data folder, which the window can no
    /// longer write: done by the service. Each fetches from a fixed address - nothing
    /// the window sends names a file or a URL.</summary>
    [EngineAccess(Access.Preference)] System.Threading.Tasks.Task<int> DownloadGeoIpInServiceAsync();
    [EngineAccess(Access.Preference)] System.Threading.Tasks.Task<string> DownloadUpdateInServiceAsync();
    [EngineAccess(Access.Tighten)] System.Threading.Tasks.Task<int> DownloadDnsPresetInServiceAsync();
    [EngineAccess(Access.Preference)] void ClearPendingPrompts();
    [EngineAccess(Access.Preference)] void ConsumeUpgradeMarker();
    /// <summary>The scheduled task older builds used to start elevated at sign-in;
    /// the installed window starts from the Run key now.</summary>
    [EngineAccess(Access.Preference)] void RemoveStartupTask();
    /// <summary>The ETW byte meter, run by the service for a window that may not
    /// start a kernel trace (RemoteByteMeter). Drain consumes, so it is not a Read.</summary>
    [EngineAccess(Access.Preference)] bool StartByteMeter();
    [EngineAccess(Access.Preference)] void StopByteMeter();
    [EngineAccess(Access.Preference)] ByteMeterReading DrainByteMeter();
    /// <summary>Closes one TCP connection (SetTcpEntry needs an administrator).
    /// Closing only ever cuts something off.</summary>
    [EngineAccess(Access.Tighten)] bool CloseTcpConnection(string localAddress, int localPort, string remoteAddress, int remotePort);
}
