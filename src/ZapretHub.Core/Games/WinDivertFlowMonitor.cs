using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;

namespace ZapretHub.Core.Games;

/// <summary>
/// Observes which remote endpoints processes connect to, using the WinDivert driver Flowseal already ships.
/// Opens the FLOW layer (established TCP/UDP flows) and the SOCKET layer (connect attempts, which also
/// catches connections DPI kills before a flow is established) in sniff + receive-only mode: traffic is
/// never delayed or modified. Needs administrator rights.
/// </summary>
public sealed unsafe class WinDivertFlowMonitor : IDisposable
{
    internal const int LayerFlow = 2;
    internal const int LayerSocket = 3;
    private const ulong FlagSniff = 0x0001;
    private const ulong FlagRecvOnly = 0x0004;
    internal const int EventFlowEstablished = 1;
    internal const int EventSocketConnect = 4;
    private const int ShutdownBoth = 3;
    internal const byte ProtoTcp = 6;
    internal const byte ProtoUdp = 17;
    private static readonly IntPtr InvalidHandle = new(-1);
    private static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(2);

    private readonly IntPtr _library;
    private readonly delegate* unmanaged<byte*, int, short, ulong, IntPtr> _open;
    private readonly delegate* unmanaged<IntPtr, void*, uint, uint*, WinDivertAddress*, int> _recv;
    private readonly delegate* unmanaged<IntPtr, int, int> _shutdown;
    private readonly delegate* unmanaged<IntPtr, int> _close;
    private readonly List<(IntPtr Handle, Thread Thread)> _readers = new();
    private readonly Func<int, bool> _isWatchedProcess;
    private readonly Action<FlowObservation> _onFlow;
    private readonly Action<Exception>? _onError;
    private int _disposed;

    /// <param name="winDivertDll">Full path to WinDivert.dll (WinDivert64.sys must sit next to it).</param>
    /// <param name="isWatchedProcess">Called with a process id; only flows of matching processes are reported.</param>
    /// <param name="onError">Called if a reader thread fails; the monitor then stops reporting on that layer.</param>
    public WinDivertFlowMonitor(string winDivertDll, Func<int, bool> isWatchedProcess, Action<FlowObservation> onFlow, Action<Exception>? onError = null)
    {
        _isWatchedProcess = isWatchedProcess;
        _onFlow = onFlow;
        _onError = onError;
        _library = NativeLibrary.Load(winDivertDll);
        try
        {
            _open = (delegate* unmanaged<byte*, int, short, ulong, IntPtr>)NativeLibrary.GetExport(_library, "WinDivertOpen");
            _recv = (delegate* unmanaged<IntPtr, void*, uint, uint*, WinDivertAddress*, int>)NativeLibrary.GetExport(_library, "WinDivertRecv");
            _shutdown = (delegate* unmanaged<IntPtr, int, int>)NativeLibrary.GetExport(_library, "WinDivertShutdown");
            _close = (delegate* unmanaged<IntPtr, int>)NativeLibrary.GetExport(_library, "WinDivertClose");

            StartReader(LayerFlow, EventFlowEstablished);
            StartReader(LayerSocket, EventSocketConnect);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void StartReader(int layer, int wantedEvent)
    {
        IntPtr handle;
        int error;
        // Filter "true": selection happens in managed code because the watched process set changes over time.
        var filter = "true\0"u8;
        fixed (byte* f = filter)
        {
            handle = _open(f, layer, 0, FlagSniff | FlagRecvOnly);
            // Function-pointer calls do not capture the error like [DllImport(SetLastError)] does; read it at once.
            error = Marshal.GetLastSystemError();
        }
        if (handle == InvalidHandle || handle == IntPtr.Zero)
        {
            throw new Win32Exception(error, $"WinDivertOpen(layer {layer}) failed: {new Win32Exception(error).Message}");
        }

        var thread = new Thread(() => ReadLoop(handle, wantedEvent)) { IsBackground = true, Name = $"WinDivert layer {layer}" };
        lock (_readers) _readers.Add((handle, thread));
        thread.Start();
    }

    private void ReadLoop(IntPtr handle, int wantedEvent)
    {
        // An exception escaping a raw thread would terminate the elevated app (and winws with it).
        try
        {
            var addr = new WinDivertAddress();
            while (Volatile.Read(ref _disposed) == 0)
            {
                // FLOW and SOCKET layers carry no packet data, only the address/event record.
                if (_recv(handle, null, 0, null, &addr) == 0)
                {
                    var error = Marshal.GetLastSystemError();
                    // After Dispose the receive fails by design; anything else is a real failure worth reporting.
                    if (Volatile.Read(ref _disposed) == 0) _onError?.Invoke(new Win32Exception(error, "WinDivertRecv failed"));
                    return;
                }
                if (TryMap(addr, wantedEvent, _isWatchedProcess) is { } flow) _onFlow(flow);
            }
        }
        catch (Exception ex)
        {
            try
            {
                _onError?.Invoke(ex);
            }
            catch
            {
                // Nothing sensible left to do on this thread.
            }
        }
    }

    /// <summary>Decides whether a WinDivert event is a watched process's TCP/UDP connection to a remote host.</summary>
    internal static FlowObservation? TryMap(WinDivertAddress addr, int wantedEvent, Func<int, bool> isWatchedProcess)
    {
        if (addr.Event != wantedEvent || addr.Loopback) return null;
        if (addr.Data.Protocol is not (ProtoTcp or ProtoUdp)) return null;
        if (!isWatchedProcess((int)addr.Data.ProcessId)) return null;

        // addr is a by-value copy on this stack frame, so its fixed buffer cannot move.
        return new FlowObservation(
            ToAddress(addr.Data.RemoteAddr, addr.IPv6),
            addr.Data.RemotePort,
            addr.Data.Protocol == ProtoTcp ? FlowProtocol.Tcp : FlowProtocol.Udp);
    }

    // WinDivert stores addresses as four host-order UINT32 words, most significant word last;
    // an IPv4 address is the low word.
    internal static IPAddress ToAddress(uint* words, bool ipv6)
    {
        if (!ipv6)
        {
            var v = words[0];
            return new IPAddress(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        }
        var bytes = new byte[16];
        for (var i = 0; i < 4; i++)
        {
            var w = words[3 - i];
            bytes[i * 4] = (byte)(w >> 24);
            bytes[i * 4 + 1] = (byte)(w >> 16);
            bytes[i * 4 + 2] = (byte)(w >> 8);
            bytes[i * 4 + 3] = (byte)w;
        }
        return new IPAddress(bytes);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        List<(IntPtr Handle, Thread Thread)> readers;
        lock (_readers) readers = _readers.ToList();
        foreach (var (handle, _) in readers) _shutdown(handle, ShutdownBoth);

        var allStopped = true;
        foreach (var (_, thread) in readers) allStopped &= thread.Join(JoinTimeout);
        if (!allStopped)
        {
            // A reader is still inside the driver: closing its handle or unloading the DLL under it
            // could crash the process. Leak both instead; the OS reclaims them at exit.
            return;
        }
        foreach (var (handle, _) in readers) _close(handle);
        if (_library != IntPtr.Zero) NativeLibrary.Free(_library);
    }

    // Mirrors WINDIVERT_ADDRESS from windivert.h (WinDivert 2.x): 80 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 80)]
    internal struct WinDivertAddress
    {
        [FieldOffset(0)] public long Timestamp;
        [FieldOffset(8)] public uint Bits;
        [FieldOffset(12)] public uint Reserved2;
        [FieldOffset(16)] public WinDivertDataFlow Data;

        public int Layer => (int)(Bits & 0xFF);
        public int Event => (int)((Bits >> 8) & 0xFF);
        public bool Loopback => ((Bits >> 18) & 1) != 0;
        public bool IPv6 => ((Bits >> 20) & 1) != 0;
    }

    // WINDIVERT_DATA_FLOW and WINDIVERT_DATA_SOCKET share this layout.
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct WinDivertDataFlow
    {
        public ulong EndpointId;
        public ulong ParentEndpointId;
        public uint ProcessId;
        public fixed uint LocalAddr[4];
        public fixed uint RemoteAddr[4];
        public ushort LocalPort;
        public ushort RemotePort;
        public byte Protocol;
    }
}
