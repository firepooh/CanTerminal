using System.Runtime.InteropServices;
using static CanTerminal.Core.IcsNeo.IcsNeoNative;

namespace CanTerminal.Core.IcsNeo;

public sealed record IcsDeviceInfo(uint DeviceType, int SerialNumber, int NumberOfClients)
{
    public override string ToString() => $"ValueCAN/neoVI SN {SerialNumber}";
}

/// <summary>
/// Intrepid ValueCAN / neoVI adapter over icsneo40.dll.
/// Channel naming: CAN1=HSCAN, CAN2=HSCAN2, CAN3=HSCAN3, CAN4=HSCAN4, MSCAN, SWCAN.
/// (On a ValueCAN3 the second physical channel is MSCAN.)
/// </summary>
public sealed class IcsNeoAdapter : ICanAdapter
{
    private static readonly (string Name, byte NetId)[] ChannelMap =
    [
        ("CAN1", NETID_HSCAN),
        ("CAN2", NETID_HSCAN2),
        ("CAN3", NETID_HSCAN3),
        ("CAN4", NETID_HSCAN4),
        ("MSCAN", NETID_MSCAN),
        ("SWCAN", NETID_SWCAN),
    ];

    private readonly IcsDeviceInfo _info;
    private readonly object _txLock = new();
    private IntPtr _handle;
    private Thread? _rxThread;
    private volatile bool _running;
    private readonly Dictionary<byte, string> _netIdToChannel = [];
    private readonly Dictionary<string, byte> _channelToNetId = [];

    public IcsNeoAdapter(IcsDeviceInfo info) => _info = info;

    public string Name => _info.ToString();
    public bool IsOpen => _handle != IntPtr.Zero;
    public IReadOnlyList<string> Channels { get; private set; } = [];

    public event Action<CanFrame>? FrameReceived;
    public event Action<string>? ErrorOccurred;

    public static List<IcsDeviceInfo> FindDevices()
    {
        var devices = new NeoDevice[64];
        int count = devices.Length;
        int r = icsneoFindNeoDevices(NEODEVICE_ALL, devices, ref count);
        var result = new List<IcsDeviceInfo>();
        if (r == 0) return result;
        for (int i = 0; i < count; i++)
            result.Add(new IcsDeviceInfo(devices[i].DeviceType, devices[i].SerialNumber, devices[i].NumberOfClients));
        return result;
    }

    public void Open(IReadOnlyList<CanChannelConfig> channels)
    {
        if (IsOpen) throw new InvalidOperationException("Already open.");

        // re-find so the NeoDevice struct is fresh (Handle field is set by the driver)
        var devices = new NeoDevice[64];
        int count = devices.Length;
        if (icsneoFindNeoDevices(NEODEVICE_ALL, devices, ref count) == 0 || count == 0)
            throw new InvalidOperationException("No Intrepid device found.");

        int idx = -1;
        for (int i = 0; i < count; i++)
            if (devices[i].SerialNumber == _info.SerialNumber) { idx = i; break; }
        if (idx < 0) throw new InvalidOperationException($"Device SN {_info.SerialNumber} not found.");

        var dev = devices[idx];
        if (icsneoOpenNeoDevice(ref dev, out _handle, null, 1, 0) == 0 || _handle == IntPtr.Zero)
        {
            _handle = IntPtr.Zero;
            throw new InvalidOperationException(
                $"Failed to open device SN {_info.SerialNumber}. It may already be open in another application (Vehicle Spy, python_ics...).");
        }

        _netIdToChannel.Clear();
        _channelToNetId.Clear();
        var names = new List<string>();
        try
        {
            foreach (var cfg in channels)
            {
                byte netId = ChannelMap.FirstOrDefault(c => c.Name.Equals(cfg.Name, StringComparison.OrdinalIgnoreCase)).NetId;
                if (netId == 0)
                    throw new ArgumentException($"Unknown channel '{cfg.Name}'. Valid: {string.Join(", ", ChannelMap.Select(c => c.Name))}");

                // Bitrate <= 0 means "keep the device's stored settings"
                if (cfg.Bitrate > 0 && icsneoSetBitRate(_handle, cfg.Bitrate, netId) == 0)
                    ErrorOccurred?.Invoke($"SetBitRate({cfg.Bitrate}) failed on {cfg.Name} — using device settings.");
                if (cfg.Fd && cfg.FdBitrate > 0 && icsneoSetFDBitRate(_handle, cfg.FdBitrate, netId) == 0)
                    ErrorOccurred?.Invoke($"SetFDBitRate({cfg.FdBitrate}) failed on {cfg.Name} — using device settings.");

                _netIdToChannel[netId] = cfg.Name.ToUpperInvariant();
                _channelToNetId[cfg.Name.ToUpperInvariant()] = netId;
                names.Add(cfg.Name.ToUpperInvariant());
            }
            Channels = names;

            _running = true;
            _rxThread = new Thread(RxLoop) { IsBackground = true, Name = "icsneo-rx" };
            _rxThread.Start();
        }
        catch
        {
            CloseHandle();
            throw;
        }
    }

    public void Close()
    {
        _running = false;
        if (_rxThread is { } rx && !rx.Join(3000))
        {
            // The RX thread is stuck inside the driver; freeing the handle under it
            // risks a native crash, so leak the handle instead.
            ErrorOccurred?.Invoke("RX thread did not stop in time — leaking device handle.");
            _handle = IntPtr.Zero;
            _rxThread = null;
            Channels = [];
            return;
        }
        _rxThread = null;
        CloseHandle();
        Channels = [];
    }

    private void CloseHandle()
    {
        if (_handle == IntPtr.Zero) return;
        int errors = 0;
        icsneoClosePort(_handle, ref errors);
        icsneoFreeObject(_handle);
        _handle = IntPtr.Zero;
    }

    public unsafe void Send(string channel, uint arbId, byte[] data, bool extended = false, bool fd = false, bool brs = false, string? source = null)
    {
        if (!IsOpen) throw new InvalidOperationException("Device not open.");
        if (!_channelToNetId.TryGetValue(channel.ToUpperInvariant(), out byte netId))
            throw new ArgumentException($"Channel '{channel}' not configured.");
        if (data.Length > (fd ? 64 : 8)) throw new ArgumentException($"Data too long ({data.Length}).");

        var msg = new IcsSpyMessage
        {
            ArbIDOrHeader = (int)arbId,
            NumberBytesData = (byte)data.Length,
        };
        if (extended) msg.StatusBitField |= SPY_STATUS_XTD_FRAME;

        var pin = default(GCHandle);
        try
        {
            if (fd)
            {
                msg.Protocol = SPY_PROTOCOL_CANFD;
                msg.StatusBitField3 |= SPY_STATUS3_CANFD_FDF;
                if (brs) msg.StatusBitField3 |= SPY_STATUS3_CANFD_BRS;
                if (extended) msg.StatusBitField3 |= SPY_STATUS3_CANFD_IDE;
            }

            if (data.Length <= 8)
            {
                for (int i = 0; i < data.Length; i++) msg.Data[i] = data[i];
            }
            else
            {
                // FD payload > 8 bytes goes through ExtraDataPtr; the DLL copies it synchronously.
                pin = GCHandle.Alloc(data, GCHandleType.Pinned);
                msg.ExtraDataPtrEnabled = 1;
                msg.ExtraDataPtr = pin.AddrOfPinnedObject();
            }

            // Serialize transmits: Send is called from the UI thread and TCP client
            // tasks concurrently, and icsneo40 is not documented as thread-safe.
            lock (_txLock)
            {
                if (!IsOpen) throw new InvalidOperationException("Device not open.");
                if (icsneoTxMessages(_handle, ref msg, netId, 1) == 0)
                    throw new InvalidOperationException($"icsneoTxMessages failed on {channel} (id 0x{arbId:X}).");
            }
        }
        finally
        {
            if (pin.IsAllocated) pin.Free();
        }
        // TX confirmation comes back through GetMessages with SPY_STATUS_TX_MSG,
        // so we don't publish a local echo here (would duplicate).
    }

    private unsafe void RxLoop()
    {
        // Native memory, handed to the driver as a plain pointer. A managed IcsSpyMessage[]
        // parameter makes the runtime marshal the whole 20,000-element buffer on every call.
        var buffer = (IcsSpyMessage*)NativeMemory.Alloc(MaxRxBuffer, (nuint)sizeof(IcsSpyMessage));
        try
        {
            while (_running)
            {
                icsneoWaitForRxMessagesWithTimeOut(_handle, 50);
                if (!_running) break;
                // poll GetMessages even on wait timeout: error events are only surfaced here
                int count = 0, errors = 0;
                if (icsneoGetMessages(_handle, buffer, ref count, ref errors) == 0) continue;

                for (int i = 0; i < count; i++)
                {
                    IcsSpyMessage* m = buffer + i;
                    if (!_netIdToChannel.TryGetValue(m->NetworkID, out var channel)) continue;
                    // NOTE: SPY_STATUS_NETWORK_MESSAGE_TYPE (0x4000000) is set on ordinary bus
                    // frames from ValueCAN 4 devices — it must NOT be filtered out.
                    // Keep only CAN / CAN FD protocol messages.
                    if (m->Protocol != SPY_PROTOCOL_CAN && m->Protocol != SPY_PROTOCOL_CANFD) continue;

                    double ts = 0;
                    icsneoGetTimeStampForMsg(_handle, m, ref ts);

                    bool isFd = m->Protocol == SPY_PROTOCOL_CANFD || (m->StatusBitField3 & SPY_STATUS3_CANFD_FDF) != 0;
                    int len = m->NumberBytesData;
                    byte[] payload;
                    if (m->ExtraDataPtrEnabled != 0 && m->ExtraDataPtr != IntPtr.Zero && len > 8)
                    {
                        payload = new byte[Math.Min(len, 64)];
                        Marshal.Copy(m->ExtraDataPtr, payload, 0, payload.Length);
                    }
                    else
                    {
                        payload = new byte[Math.Min(len, 8)];
                        for (int b = 0; b < payload.Length; b++) payload[b] = m->Data[b];
                    }

                    FrameReceived?.Invoke(new CanFrame
                    {
                        Timestamp = ts,
                        Channel = channel,
                        ArbId = (uint)m->ArbIDOrHeader,
                        IsExtended = (m->StatusBitField & SPY_STATUS_XTD_FRAME) != 0 || (isFd && (m->StatusBitField3 & SPY_STATUS3_CANFD_IDE) != 0),
                        IsFd = isFd,
                        IsBrs = (m->StatusBitField3 & SPY_STATUS3_CANFD_BRS) != 0,
                        IsRemote = (m->StatusBitField & SPY_STATUS_REMOTE_FRAME) != 0,
                        IsError = (m->StatusBitField & SPY_STATUS_GLOBAL_ERR) != 0 || (m->StatusBitField2 & SPY_STATUS2_ERROR_FRAME) != 0,
                        Direction = (m->StatusBitField & SPY_STATUS_TX_MSG) != 0 ? FrameDirection.Tx : FrameDirection.Rx,
                        Data = payload,
                    });
                }

                if (errors > 0)
                {
                    int n = 0;
                    var errBuf = new int[600];
                    if (icsneoGetErrorMessages(_handle, errBuf, ref n) != 0 && n > 0)
                        ErrorOccurred?.Invoke($"{n} bus error event(s): [{string.Join(",", errBuf.Take(Math.Min(n, 8)))}]");
                }
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    public void Dispose() => Close();
}
