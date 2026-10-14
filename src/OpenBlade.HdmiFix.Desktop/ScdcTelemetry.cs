using System.Runtime.InteropServices;

namespace OpenBlade.HdmiFix.Desktop;

internal sealed record ScdcObservation(
    byte? SinkVersion,
    byte? FrlRateCode,
    int? LaneCount,
    int? GbpsPerLane,
    byte? LaneLockMask,
    bool? TrainingReady,
    string Status)
{
    public string ActiveDsc => "Unknown (not exposed by the public API)";

    internal static ScdcObservation Unknown(string status) =>
        new(null, null, null, null, null, null, status);
}

internal sealed class ScdcReader
{
    // The native ABI and interface IDs are pinned to NVIDIA's public NVAPI SDK:
    // https://github.com/NVIDIA/nvapi/blob/70d337db9186e968eab622f7e786de7e437faf3d/nvapi.h
    // https://github.com/NVIDIA/nvapi/blob/70d337db9186e968eab622f7e786de7e437faf3d/nvapi_interface.h
    // NVIDIA publishes these read-only SCDC offsets at:
    // https://github.com/NVIDIA/open-gpu-kernel-modules/blob/main/src/common/sdk/nvidia/inc/ctrl/ctrl0073/ctrl0073specific.h
    // The FRL rate-code meanings are published at:
    // https://github.com/NVIDIA/open-gpu-kernel-modules/blob/main/src/common/inc/nvHdmiFrlCommon.h
    private const byte ScdcAddress = 0xA8;
    private const byte SinkVersionRegister = 0x01;
    private const byte Configuration1Register = 0x31;
    private const byte StatusFlags0Register = 0x40;

    internal const uint GetGpuAndOutputIdInterfaceId = 0x112BA1A5;
    internal const uint I2cReadInterfaceId = 0x2FDE12C5;
    internal const uint I2cInfoVersion = 0x00030040;

    private const uint MaximumDisplayMask = 1u << 24;
    private const uint DeprecatedI2cSpeed = 0xFFFF;
    private readonly Func<uint, IntPtr> _query;

    internal ScdcReader(Func<uint, IntPtr> query)
    {
        ArgumentNullException.ThrowIfNull(query);
        _query = query;
    }

    internal static (int Size, int Version, int DisplayMask, int IsDdcPort, int DeviceAddress,
        int RegisterAddress, int RegisterAddressSize, int Data, int DataSize, int DeprecatedSpeed,
        int SpeedKhz, int PortId, int IsPortIdSet) I2cAbi =>
        (Marshal.SizeOf<NvI2cInfoV3>(),
         OffsetOf(nameof(NvI2cInfoV3.Version)),
         OffsetOf(nameof(NvI2cInfoV3.DisplayMask)),
         OffsetOf(nameof(NvI2cInfoV3.IsDdcPort)),
         OffsetOf(nameof(NvI2cInfoV3.DeviceAddress)),
         OffsetOf(nameof(NvI2cInfoV3.RegisterAddress)),
         OffsetOf(nameof(NvI2cInfoV3.RegisterAddressSize)),
         OffsetOf(nameof(NvI2cInfoV3.Data)),
         OffsetOf(nameof(NvI2cInfoV3.DataSize)),
         OffsetOf(nameof(NvI2cInfoV3.DeprecatedSpeed)),
         OffsetOf(nameof(NvI2cInfoV3.SpeedKhz)),
         OffsetOf(nameof(NvI2cInfoV3.PortId)),
         OffsetOf(nameof(NvI2cInfoV3.IsPortIdSet)));

    internal ScdcObservation Read(uint displayId)
    {
        try
        {
            NvApiGetGpuAndOutputId? mapDisplay = GetDelegate<NvApiGetGpuAndOutputId>(
                GetGpuAndOutputIdInterfaceId);
            NvApiI2cRead? readI2c = GetDelegate<NvApiI2cRead>(I2cReadInterfaceId);
            if (mapDisplay is null || readI2c is null)
            {
                return ScdcObservation.Unknown("Scdc:InterfaceUnavailable");
            }

            DisplayMapping before = MapDisplay(mapDisplay, displayId);
            byte sinkVersion;
            byte configuration = 0;
            byte statusFlags = 0;

            IntPtr registerBuffer = IntPtr.Zero;
            IntPtr dataBuffer = IntPtr.Zero;
            try
            {
                registerBuffer = Marshal.AllocHGlobal(1);
                dataBuffer = Marshal.AllocHGlobal(1);
                sinkVersion = ReadRegister(readI2c, before, SinkVersionRegister, registerBuffer, dataBuffer);
                if (sinkVersion == 1)
                {
                    configuration = ReadRegister(readI2c, before, Configuration1Register, registerBuffer, dataBuffer);
                    statusFlags = ReadRegister(readI2c, before, StatusFlags0Register, registerBuffer, dataBuffer);
                }
            }
            finally
            {
                if (dataBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(dataBuffer);
                }
                if (registerBuffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(registerBuffer);
                }
            }

            DisplayMapping after = MapDisplay(mapDisplay, displayId);
            if (before != after)
            {
                return ScdcObservation.Unknown("Scdc:TargetChanged");
            }

            if (sinkVersion != 1)
            {
                return new(sinkVersion, null, null, null, null, null,
                    "Scdc:SinkVersionUnsupported");
            }

            byte rateCode = (byte)(configuration & 0x0F);
            (int LaneCount, int GbpsPerLane)? rate = DecodeRate(rateCode);
            if (rate is null)
            {
                return ScdcObservation.Unknown("Scdc:ReservedRate");
            }

            byte laneLocks = (byte)((statusFlags >> 1) & 0x0F);
            bool trainingReady = (statusFlags & 0x40) != 0;
            string readoutStatus = rateCode == 0 ? "Scdc:RateZero" : "Scdc:Available";

            return new(sinkVersion, rateCode, rate.Value.LaneCount, rate.Value.GbpsPerLane,
                laneLocks, trainingReady, readoutStatus);
        }
        catch (ScdcUnavailableException error)
        {
            return ScdcObservation.Unknown(error.Status);
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            return ScdcObservation.Unknown("Scdc:Unavailable");
        }
    }

    private T? GetDelegate<T>(uint interfaceId) where T : Delegate
    {
        IntPtr pointer = _query(interfaceId);
        return pointer == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    private static DisplayMapping MapDisplay(NvApiGetGpuAndOutputId mapDisplay, uint displayId)
    {
        int status = mapDisplay(displayId, out IntPtr physicalGpu, out uint outputId);
        if (status != 0)
        {
            throw new ScdcUnavailableException($"Scdc:MappingFailed:{status}");
        }
        if (physicalGpu == IntPtr.Zero || outputId == 0 || outputId >= MaximumDisplayMask
            || (outputId & (outputId - 1)) != 0)
        {
            throw new ScdcUnavailableException("Scdc:InvalidMapping");
        }
        return new(physicalGpu, outputId);
    }

    private static byte ReadRegister(NvApiI2cRead readI2c, DisplayMapping mapping, byte register,
        IntPtr registerBuffer, IntPtr dataBuffer)
    {
        Marshal.WriteByte(registerBuffer, register);
        Marshal.WriteByte(dataBuffer, 0);
        NvI2cInfoV3 info = new()
        {
            Version = I2cInfoVersion,
            DisplayMask = mapping.OutputId,
            IsDdcPort = 1,
            DeviceAddress = ScdcAddress,
            RegisterAddress = registerBuffer,
            RegisterAddressSize = 1,
            Data = dataBuffer,
            DataSize = 1,
            DeprecatedSpeed = DeprecatedI2cSpeed,
            SpeedKhz = 0,
            PortId = 0,
            IsPortIdSet = 0,
        };

        // NvAPI documents this as a combined transaction: write the one-byte register
        // address, then read the one-byte data buffer. No SCDC value is written.
        // https://docs.nvidia.com/nvapi/group__i2capi.html
        int status = readI2c(mapping.PhysicalGpu, ref info);
        if (status != 0)
        {
            throw new ScdcUnavailableException($"Scdc:ReadFailed:{status}");
        }

        if (info.Version != I2cInfoVersion || info.DisplayMask != mapping.OutputId
            || info.IsDdcPort != 1 || info.DeviceAddress != ScdcAddress
            || info.RegisterAddress != registerBuffer || info.RegisterAddressSize != 1
            || info.Data != dataBuffer || info.DataSize != 1
            || info.DeprecatedSpeed != DeprecatedI2cSpeed || info.SpeedKhz != 0
            || info.PortId != 0 || info.IsPortIdSet != 0
            || Marshal.ReadByte(registerBuffer) != register)
        {
            throw new ScdcUnavailableException("Scdc:InvalidMetadata");
        }

        return Marshal.ReadByte(dataBuffer);
    }

    private static (int LaneCount, int GbpsPerLane)? DecodeRate(byte code) => code switch
    {
        0 => (0, 0),
        1 => (3, 3),
        2 => (3, 6),
        3 => (4, 6),
        4 => (4, 8),
        5 => (4, 10),
        6 => (4, 12),
        _ => null,
    };

    private static int OffsetOf(string field) => checked((int)Marshal.OffsetOf<NvI2cInfoV3>(field));

    private static bool IsRecoverable(Exception error) =>
        error is not (OutOfMemoryException or StackOverflowException or AccessViolationException);

    private readonly record struct DisplayMapping(IntPtr PhysicalGpu, uint OutputId);

    private sealed class ScdcUnavailableException(string status) : Exception
    {
        internal string Status { get; } = status;
    }

    [StructLayout(LayoutKind.Explicit, Pack = 8, Size = 64)]
    internal struct NvI2cInfoV3
    {
        [FieldOffset(0)] internal uint Version;
        [FieldOffset(4)] internal uint DisplayMask;
        [FieldOffset(8)] internal byte IsDdcPort;
        [FieldOffset(9)] internal byte DeviceAddress;
        [FieldOffset(16)] internal IntPtr RegisterAddress;
        [FieldOffset(24)] internal uint RegisterAddressSize;
        [FieldOffset(32)] internal IntPtr Data;
        [FieldOffset(40)] internal uint DataSize;
        [FieldOffset(44)] internal uint DeprecatedSpeed;
        [FieldOffset(48)] internal uint SpeedKhz;
        [FieldOffset(52)] internal byte PortId;
        [FieldOffset(56)] internal uint IsPortIdSet;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int NvApiGetGpuAndOutputId(uint displayId, out IntPtr physicalGpu, out uint outputId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate int NvApiI2cRead(IntPtr physicalGpu, ref NvI2cInfoV3 info);
}
