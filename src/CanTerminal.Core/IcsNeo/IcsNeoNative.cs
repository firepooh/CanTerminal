using System.Runtime.InteropServices;

namespace CanTerminal.Core.IcsNeo;

/// <summary>
/// P/Invoke bindings for Intrepid Control Systems icsneo40.dll (neoVI API).
/// Struct layouts match icsnVC40.h (default packing).
/// </summary>
internal static class IcsNeoNative
{
    private const string Dll = "icsneo40.dll";

    // NETID values from icsnVC40.h
    public const byte NETID_DEVICE = 0;
    public const byte NETID_HSCAN = 1;
    public const byte NETID_MSCAN = 2;
    public const byte NETID_SWCAN = 3;
    public const byte NETID_HSCAN2 = 42;
    public const byte NETID_HSCAN3 = 44;
    public const byte NETID_HSCAN4 = 61;
    public const byte NETID_HSCAN5 = 62;
    public const byte NETID_HSCAN6 = 96;
    public const byte NETID_HSCAN7 = 97;

    // StatusBitField
    public const uint SPY_STATUS_GLOBAL_ERR = 0x01;
    public const uint SPY_STATUS_TX_MSG = 0x02;
    public const uint SPY_STATUS_XTD_FRAME = 0x04;
    public const uint SPY_STATUS_REMOTE_FRAME = 0x08;
    public const uint SPY_STATUS_CAN_BUS_OFF = 0x200;
    public const uint SPY_STATUS_NETWORK_MESSAGE_TYPE = 0x4000000;
    public const uint SPY_STATUS_EXTENDED = 0x80000000;

    // StatusBitField2
    public const uint SPY_STATUS2_ERROR_FRAME = 0x20000;

    // StatusBitField3 (CAN FD)
    public const uint SPY_STATUS3_CANFD_ESI = 0x01;
    public const uint SPY_STATUS3_CANFD_IDE = 0x02;
    public const uint SPY_STATUS3_CANFD_RTR = 0x04;
    public const uint SPY_STATUS3_CANFD_FDF = 0x08;
    public const uint SPY_STATUS3_CANFD_BRS = 0x10;

    public const byte SPY_PROTOCOL_CAN = 1;
    public const byte SPY_PROTOCOL_CANFD = 30;

    public const uint NEODEVICE_ALL = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    public struct NeoDevice
    {
        public uint DeviceType;
        public int Handle;
        public int NumberOfClients;
        public int SerialNumber;
        public int MaxAllowedClients;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct IcsSpyMessage
    {
        public uint StatusBitField;
        public uint StatusBitField2;
        public uint TimeHardware;
        public uint TimeHardware2;
        public uint TimeSystem;
        public uint TimeSystem2;
        public byte TimeStampHardwareID;
        public byte TimeStampSystemID;
        public byte NetworkID;
        public byte NodeID;
        public byte Protocol;
        public byte MessagePieceID;
        public byte ExtraDataPtrEnabled;
        public byte NumberBytesHeader;
        public byte NumberBytesData;
        public byte NetworkID2;
        public short DescriptionID;
        public int ArbIDOrHeader;
        public fixed byte Data[8];
        public uint StatusBitField3;
        public uint StatusBitField4;
        public IntPtr ExtraDataPtr;
        public byte MiscData;
        public fixed byte Reserved[3];
    }

    /// <summary>Max messages a single icsneoGetMessages call can return (per Intrepid docs).</summary>
    public const int MaxRxBuffer = 20_000;

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int icsneoFindNeoDevices(uint deviceTypes, [In, Out] NeoDevice[] devices, ref int count);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int icsneoOpenNeoDevice(ref NeoDevice device, out IntPtr handle, byte[]? networkIds, int configRead, int options);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int icsneoClosePort(IntPtr handle, ref int numErrors);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern void icsneoFreeObject(IntPtr handle);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int icsneoGetMessages(IntPtr handle, [Out] IcsSpyMessage[] messages, ref int count, ref int numErrors);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int icsneoTxMessages(IntPtr handle, ref IcsSpyMessage message, int networkId, int numMessages);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int icsneoWaitForRxMessagesWithTimeOut(IntPtr handle, uint timeoutMs);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int icsneoGetTimeStampForMsg(IntPtr handle, ref IcsSpyMessage message, ref double timestamp);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int icsneoSetBitRate(IntPtr handle, int bitRate, int networkId);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int icsneoSetFDBitRate(IntPtr handle, int bitRate, int networkId);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern int icsneoGetErrorMessages(IntPtr handle, [Out] int[] errors, ref int numErrors);
}
