namespace CanTerminal.Core.Xcp;

/// <summary>
/// Code-to-name tables from ASAM MCD-1 XCP (protocol layer 1.0–1.4).
/// </summary>
internal static class XcpTables
{
    /// <summary>Lowest PID that identifies a command packet; below this a master frame is a STIM-DTO.</summary>
    public const byte CommandPidMin = 0xC0;

    public const byte PidRes = 0xFF;   // positive response
    public const byte PidErr = 0xFE;   // error
    public const byte PidEv = 0xFD;    // event
    public const byte PidServ = 0xFC;  // service request

    public static string? Command(byte pid) => pid switch
    {
        0xFF => "CONNECT",
        0xFE => "DISCONNECT",
        0xFD => "GET_STATUS",
        0xFC => "SYNCH",
        0xFB => "GET_COMM_MODE_INFO",
        0xFA => "GET_ID",
        0xF9 => "SET_REQUEST",
        0xF8 => "GET_SEED",
        0xF7 => "UNLOCK",
        0xF6 => "SET_MTA",
        0xF5 => "UPLOAD",
        0xF4 => "SHORT_UPLOAD",
        0xF3 => "BUILD_CHECKSUM",
        0xF2 => "TRANSPORT_LAYER_CMD",
        0xF1 => "USER_CMD",
        0xF0 => "DOWNLOAD",
        0xEF => "DOWNLOAD_NEXT",
        0xEE => "DOWNLOAD_MAX",
        0xED => "SHORT_DOWNLOAD",
        0xEC => "MODIFY_BITS",
        0xEB => "SET_CAL_PAGE",
        0xEA => "GET_CAL_PAGE",
        0xE9 => "GET_PAG_PROCESSOR_INFO",
        0xE8 => "GET_SEGMENT_INFO",
        0xE7 => "GET_PAGE_INFO",
        0xE6 => "SET_SEGMENT_MODE",
        0xE5 => "GET_SEGMENT_MODE",
        0xE4 => "COPY_CAL_PAGE",
        0xE3 => "CLEAR_DAQ_LIST",
        0xE2 => "SET_DAQ_PTR",
        0xE1 => "WRITE_DAQ",
        0xE0 => "SET_DAQ_LIST_MODE",
        0xDF => "GET_DAQ_LIST_MODE",
        0xDE => "START_STOP_DAQ_LIST",
        0xDD => "START_STOP_SYNCH",
        0xDC => "GET_DAQ_CLOCK",
        0xDB => "READ_DAQ",
        0xDA => "GET_DAQ_PROCESSOR_INFO",
        0xD9 => "GET_DAQ_RESOLUTION_INFO",
        0xD8 => "GET_DAQ_LIST_INFO",
        0xD7 => "GET_DAQ_EVENT_INFO",
        0xD6 => "FREE_DAQ",
        0xD5 => "ALLOC_DAQ",
        0xD4 => "ALLOC_ODT",
        0xD3 => "ALLOC_ODT_ENTRY",
        0xD2 => "PROGRAM_START",
        0xD1 => "PROGRAM_CLEAR",
        0xD0 => "PROGRAM",
        0xCF => "PROGRAM_RESET",
        0xCE => "GET_PGM_PROCESSOR_INFO",
        0xCD => "GET_SECTOR_INFO",
        0xCC => "PROGRAM_PREPARE",
        0xCB => "PROGRAM_FORMAT",
        0xCA => "PROGRAM_NEXT",
        0xC9 => "PROGRAM_MAX",
        0xC8 => "PROGRAM_VERIFY",
        0xC7 => "WRITE_DAQ_MULTIPLE",
        0xC6 => "TIME_CORRELATION_PROPERTIES",
        0xC5 => "DTO_CTR_PROPERTIES",
        0xC0 => "LEVEL1_CMD",
        _ => null,
    };

    /// <summary>Sub-command of the 0xC0 escape (XCP 1.3+).</summary>
    public static string Level1(byte sub) => sub switch
    {
        0x00 => "GET_VERSION",
        0x01 => "SET_DAQ_PACKED_MODE",
        0x02 => "GET_DAQ_PACKED_MODE",
        0xFC => "SW_DBG_OVER_XCP",
        0xFD => "POD_BTL",
        _ => $"SUB 0x{sub:X2}",
    };

    /// <summary>Sub-command of TRANSPORT_LAYER_CMD for the CAN transport layer.</summary>
    public static string TransportLayer(byte sub) => sub switch
    {
        0xFF => "GET_SLAVE_ID",
        0xFE => "GET_DAQ_ID",
        0xFD => "SET_DAQ_ID",
        _ => $"SUB 0x{sub:X2}",
    };

    public static string Error(byte code) => code switch
    {
        0x00 => "ERR_CMD_SYNCH",
        0x10 => "ERR_CMD_BUSY",
        0x11 => "ERR_DAQ_ACTIVE",
        0x12 => "ERR_PGM_ACTIVE",
        0x20 => "ERR_CMD_UNKNOWN",
        0x21 => "ERR_CMD_SYNTAX",
        0x22 => "ERR_OUT_OF_RANGE",
        0x23 => "ERR_WRITE_PROTECTED",
        0x24 => "ERR_ACCESS_DENIED",
        0x25 => "ERR_ACCESS_LOCKED",
        0x26 => "ERR_PAGE_NOT_VALID",
        0x27 => "ERR_MODE_NOT_VALID",
        0x28 => "ERR_SEGMENT_NOT_VALID",
        0x29 => "ERR_SEQUENCE",
        0x2A => "ERR_DAQ_CONFIG",
        0x30 => "ERR_MEMORY_OVERFLOW",
        0x31 => "ERR_GENERIC",
        0x32 => "ERR_VERIFY",
        0x33 => "ERR_RESOURCE_TEMPORARY_NOT_ACCESSIBLE",
        0x34 => "ERR_SUBCMD_UNKNOWN",
        0x35 => "ERR_TIMECORR_STATE_CHANGE",
        _ => $"ERR 0x{code:X2}",
    };

    public static string Event(byte code) => code switch
    {
        0x00 => "EV_RESUME_MODE",
        0x01 => "EV_CLEAR_DAQ",
        0x02 => "EV_STORE_DAQ",
        0x03 => "EV_STORE_CAL",
        0x05 => "EV_CMD_PENDING",
        0x06 => "EV_DAQ_OVERLOAD",
        0x07 => "EV_SESSION_TERMINATED",
        0x08 => "EV_TIME_SYNC",
        0x09 => "EV_STIM_TIMEOUT",
        0x0A => "EV_SLEEP",
        0x0B => "EV_WAKE_UP",
        0xFE => "EV_USER",
        0xFF => "EV_TRANSPORT",
        _ => $"EV 0x{code:X2}",
    };

    public static string Service(byte code) => code switch
    {
        0x00 => "SERV_RESET",
        0x01 => "SERV_TEXT",
        _ => $"SERV 0x{code:X2}",
    };

    public static string GetIdType(byte type) => type switch
    {
        0 => "ASCII text",
        1 => "ASAM-MC2 filename w/o path+ext",
        2 => "ASAM-MC2 filename with path+ext",
        3 => "ASAM-MC2 URL",
        4 => "ASAM-MC2 upload",
        5 => "ECU name",
        6 => "ECU-ID",
        _ => $"type {type}",
    };

    /// <summary>Human list of the RESOURCE bits reported by CONNECT / protected by GET_STATUS.</summary>
    public static string Resource(byte r)
    {
        var parts = new List<string>(5);
        if ((r & 0x01) != 0) parts.Add("CAL/PAG");
        if ((r & 0x04) != 0) parts.Add("DAQ");
        if ((r & 0x08) != 0) parts.Add("STIM");
        if ((r & 0x10) != 0) parts.Add("PGM");
        if ((r & 0x40) != 0) parts.Add("DBG");
        return parts.Count == 0 ? "none" : string.Join(",", parts);
    }

    public static string SessionStatus(byte s)
    {
        var parts = new List<string>(5);
        if ((s & 0x01) != 0) parts.Add("STORE_CAL_REQ");
        if ((s & 0x04) != 0) parts.Add("STORE_DAQ_REQ");
        if ((s & 0x08) != 0) parts.Add("CLEAR_DAQ_REQ");
        if ((s & 0x40) != 0) parts.Add("DAQ_RUNNING");
        if ((s & 0x80) != 0) parts.Add("RESUME");
        return parts.Count == 0 ? "idle" : string.Join(",", parts);
    }
}
