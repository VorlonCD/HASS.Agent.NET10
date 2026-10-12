using System.Runtime.InteropServices;
using HASS.Agent.Companion.Logging;

namespace HASS.Agent.Companion.SystemStatus;

/// <summary>
/// The processes streaming from a camera right now, from the Media Foundation sensor
/// activity monitor (Windows 10 2004 and later). Windows sends a report whenever a camera
/// starts or stops; the latest one is kept and read on each sensor cycle.
/// </summary>
internal sealed class CameraActivityMonitor : IDisposable
{
    private const uint MfVersion = 0x00020070;
    private const uint MfStartupLite = 1;
    private static readonly TimeSpan FirstReportTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly FileLog _log;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _cameraInUseReported = new();
    private IMFSensorActivityMonitor? _monitor;
    private ReportCallback? _callback;
    private bool _mfStarted;
    private bool _unavailable;
    private bool _disposed;
    private volatile IReadOnlyList<int> _streaming = [];

    public CameraActivityMonitor(FileLog log)
    {
        _log = log;
    }

    /// <summary>
    /// The streaming process ids, or null when the monitor can't run on this PC (the caller
    /// falls back to the older source). Starts the monitor on first use.
    /// </summary>
    public IReadOnlyList<int>? GetStreamingProcessIds()
    {
        lock (_gate)
        {
            if (_disposed || _unavailable)
            {
                return null;
            }

            if (_monitor is null)
            {
                // The tray app's sensor cycle may run on an STA thread; the monitor and its
                // callbacks live in the MTA, so it is created there.
                var error = Task.Run(Start).GetAwaiter().GetResult();
                if (error is not null)
                {
                    _unavailable = true;
                    _log.Warning($"Camera activity monitor unavailable, using the privacy record instead: {error}");
                    Stop();
                    return null;
                }

                // The reports follow the start by a moment, and the first one can be empty even
                // with a camera in use. Waiting a little (once) keeps a camera that is already
                // in use from reading as free for a cycle.
                _cameraInUseReported.Wait(FirstReportTimeout);
            }

            return _streaming;
        }
    }

    private string? Start()
    {
        try
        {
            var hr = MFStartup(MfVersion, MfStartupLite);
            if (hr < 0)
            {
                return $"MFStartup 0x{hr:X8}";
            }

            _mfStarted = true;
            _callback = new ReportCallback(this);
            hr = MFCreateSensorActivityMonitor(_callback, out var monitor);
            if (hr < 0)
            {
                return $"MFCreateSensorActivityMonitor 0x{hr:X8}";
            }

            _monitor = monitor;
            hr = monitor.Start();
            return hr < 0 ? $"Start 0x{hr:X8}" : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or InvalidCastException)
        {
            return ex.Message;
        }
    }

    private void Stop()
    {
        if (_monitor is not null)
        {
            try
            {
                _monitor.Stop();
            }
            catch (COMException)
            {
            }

            Marshal.ReleaseComObject(_monitor);
            _monitor = null;
        }

        _callback = null;
        if (_mfStarted)
        {
            _ = MFShutdown();
            _mfStarted = false;
        }

        _streaming = [];
    }

    private void OnReport(IMFSensorActivitiesReport report)
    {
        // Every report lists each camera in use and the processes that hold it; one that
        // only opened the camera without streaming does not count.
        var processIds = new HashSet<int>();
        try
        {
            report.GetCount(out var deviceCount);
            for (uint device = 0; device < deviceCount; device++)
            {
                report.GetActivityReport(device, out var activity);
                try
                {
                    activity.GetProcessCount(out var processCount);
                    for (uint index = 0; index < processCount; index++)
                    {
                        activity.GetProcessActivity(index, out var process);
                        try
                        {
                            process.GetProcessId(out var processId);
                            process.GetStreamingState(out var streaming);
                            if (streaming != 0 && processId > 0)
                            {
                                processIds.Add((int)processId);
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(process);
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(activity);
                }
            }
        }
        catch (COMException ex)
        {
            _log.Warning($"Unable to read a camera activity report: {ex.Message}");
            return;
        }

        _streaming = processIds.ToList();
        if (processIds.Count > 0)
        {
            _cameraInUseReported.Set();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            // The event is not disposed: a report already on its way may still set it.
            Stop();
        }
    }

    private sealed class ReportCallback(CameraActivityMonitor owner) : IMFSensorActivitiesReportCallback
    {
        public int OnActivitiesReport(IMFSensorActivitiesReport report)
        {
            owner.OnReport(report);
            return 0;
        }
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFShutdown();

    [DllImport("mfsensorgroup.dll", ExactSpelling = true)]
    private static extern int MFCreateSensorActivityMonitor(
        IMFSensorActivitiesReportCallback callback,
        out IMFSensorActivityMonitor monitor);

    // mfidl.h. Only the slots that are called have real signatures; the others keep the vtable order.
    [ComImport]
    [Guid("DE5072EE-DBE3-46DC-8A87-B6F631194751")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSensorActivitiesReportCallback
    {
        [PreserveSig] int OnActivitiesReport(IMFSensorActivitiesReport report);
    }

    [ComImport]
    [Guid("D0CEF145-B3F4-4340-A2E5-7A5080CA05CB")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSensorActivityMonitor
    {
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
    }

    [ComImport]
    [Guid("683F7A5E-4A19-43CD-B1A9-DBF4AB3F7777")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSensorActivitiesReport
    {
        void GetCount(out uint count);
        void GetActivityReport(uint index, out IMFSensorActivityReport report);
    }

    [ComImport]
    [Guid("3E8C4BE1-A8C2-4528-90DE-2851BDE5FEAD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSensorActivityReport
    {
        void GetFriendlyName();
        void GetSymbolicLink();
        void GetProcessCount(out uint count);
        void GetProcessActivity(uint index, out IMFSensorProcessActivity activity);
    }

    [ComImport]
    [Guid("39DC7F4A-B141-4719-813C-A7F46162A2B8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSensorProcessActivity
    {
        void GetProcessId(out uint processId);
        void GetStreamingState(out int streaming);
    }
}
