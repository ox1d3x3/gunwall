using System.Runtime.InteropServices;
using GunWall.Services;

namespace GunWall;

/// <summary>
/// <c>GunWall.exe --service</c>: the background service (0.99.199). The same
/// executable as the window, so there is one thing to build, sign and install;
/// in this mode it never creates a window.
///
/// A hand-written service host (decision Q4 = A, no NuGet): StartServiceCtrlDispatcher
/// hands the SCM a ServiceMain, which reports RUNNING, starts the hand-over
/// service, and waits for Stop or Shutdown. Everything it does is in
/// GunWall.Core (HandoverService, ServiceEngineSession); this is only the shell.
/// Its log is service.log beside diagnostics.log.
/// </summary>
internal static class ServiceShell
{
    private const int SERVICE_WIN32_OWN_PROCESS = 0x10;
    private const int SERVICE_STOPPED = 1, SERVICE_START_PENDING = 2, SERVICE_STOP_PENDING = 3, SERVICE_RUNNING = 4;
    private const int SERVICE_ACCEPT_STOP = 0x1, SERVICE_ACCEPT_SHUTDOWN = 0x4;
    private const int SERVICE_CONTROL_STOP = 1, SERVICE_CONTROL_INTERROGATE = 4, SERVICE_CONTROL_SHUTDOWN = 5;
    private const int NO_ERROR = 0, ERROR_CALL_NOT_IMPLEMENTED = 120, ERROR_SERVICE_SPECIFIC_ERROR = 1066;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public int dwServiceType, dwCurrentState, dwControlsAccepted, dwWin32ExitCode,
                   dwServiceSpecificExitCode, dwCheckPoint, dwWaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SERVICE_TABLE_ENTRY
    {
        public string? lpServiceName;
        public IntPtr lpServiceProc;
    }

    private delegate void ServiceMainProc(int argc, IntPtr argv);
    private delegate int HandlerEx(int control, int eventType, IntPtr eventData, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartServiceCtrlDispatcherW([In] SERVICE_TABLE_ENTRY[] table);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterServiceCtrlHandlerExW(string name, HandlerEx handler, IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetServiceStatus(IntPtr handle, ref SERVICE_STATUS status);

    // Kept in static fields: the SCM calls these from native code long after
    // registration, and a collected delegate is a crash.
    private static ServiceMainProc? _main;
    private static HandlerEx? _handler;
    private static IntPtr _statusHandle;
    private static SERVICE_STATUS _status;
    private static readonly ManualResetEventSlim _stop = new(false);
    private static readonly object _statusLock = new();

    /// <summary>Runs the service until the SCM stops it. Returns the process exit code.</summary>
    public static int Run()
    {
        DiagnosticLog.Init(ProfilePaths.DataFolder, "service.log");
        DiagnosticLog.Log($"Service: GunWall {UpdateService.CurrentVersion} background service starting.");
        _main = ServiceMain;
        var table = new[]
        {
            new SERVICE_TABLE_ENTRY { lpServiceName = BackgroundServiceControl.ServiceName,
                                      lpServiceProc = Marshal.GetFunctionPointerForDelegate(_main) },
            new SERVICE_TABLE_ENTRY { lpServiceName = null, lpServiceProc = IntPtr.Zero },
        };
        if (!StartServiceCtrlDispatcherW(table))
        {
            int err = Marshal.GetLastWin32Error();
            // 1063: started from a console, not by the SCM.
            DiagnosticLog.Log($"Service: StartServiceCtrlDispatcher failed ({err}) - "
                            + (err == 1063 ? "--service is for Windows to start, not for running by hand." : "see the code."));
            return 2;
        }
        DiagnosticLog.Log("Service: stopped.");
        return 0;
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _handler = Handler;
        _statusHandle = RegisterServiceCtrlHandlerExW(BackgroundServiceControl.ServiceName, _handler, IntPtr.Zero);
        if (_statusHandle == IntPtr.Zero)
        {
            DiagnosticLog.Log($"Service: RegisterServiceCtrlHandlerEx failed ({Marshal.GetLastWin32Error()}).");
            return;
        }
        Report(SERVICE_START_PENDING, 0, 10000);

        HandoverService? handover = null;
        bool failed = false;
        try
        {
            handover = new HandoverService(
                ProfilePaths.FileIn(EngineOwnership.LockFileName), EngineOwnership.PipeName,
                () => new ServiceEngineSession(), DiagnosticLog.Log);
            handover.Start();
            Report(SERVICE_RUNNING, SERVICE_ACCEPT_STOP | SERVICE_ACCEPT_SHUTDOWN, 0);
            DiagnosticLog.Log("Service: running - it runs the engine whenever the GunWall window is closed.");
            _stop.Wait();
        }
        catch (Exception ex)
        {
            failed = true;
            DiagnosticLog.LogException("ServiceMain", ex);
        }
        finally
        {
            Report(SERVICE_STOP_PENDING, 0, 30000);
            try { handover?.Dispose(); } catch (Exception ex) { DiagnosticLog.LogException("ServiceStop", ex); }
            // A failure is reported as one, so Windows' recovery actions apply;
            // a clean stop is not.
            Report(SERVICE_STOPPED, 0, 0, failed ? ERROR_SERVICE_SPECIFIC_ERROR : NO_ERROR);
        }
    }

    private static int Handler(int control, int eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case SERVICE_CONTROL_STOP:
            case SERVICE_CONTROL_SHUTDOWN:
                DiagnosticLog.Log(control == SERVICE_CONTROL_STOP ? "Service: stop requested." : "Service: Windows is shutting down.");
                Report(SERVICE_STOP_PENDING, 0, 30000);
                _stop.Set();
                return NO_ERROR;
            case SERVICE_CONTROL_INTERROGATE:
                return NO_ERROR;
            default:
                return ERROR_CALL_NOT_IMPLEMENTED;
        }
    }

    // Called from the SCM's handler thread and from ServiceMain: one at a time.
    private static void Report(int state, int accepted, int waitHint, int exitCode = NO_ERROR)
    {
        lock (_statusLock)
        {
            _status.dwServiceType = SERVICE_WIN32_OWN_PROCESS;
            _status.dwCurrentState = state;
            _status.dwControlsAccepted = accepted;
            _status.dwWin32ExitCode = exitCode;
            _status.dwServiceSpecificExitCode = exitCode == ERROR_SERVICE_SPECIFIC_ERROR ? 1 : 0;
            _status.dwWaitHint = waitHint;
            _status.dwCheckPoint = state is SERVICE_RUNNING or SERVICE_STOPPED ? 0 : _status.dwCheckPoint + 1;
            if (_statusHandle != IntPtr.Zero) SetServiceStatus(_statusHandle, ref _status);
        }
    }
}
