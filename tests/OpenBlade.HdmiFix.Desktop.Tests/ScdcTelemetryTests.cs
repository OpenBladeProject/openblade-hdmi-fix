namespace OpenBlade.HdmiFix.Desktop.Tests;

using System.Runtime.InteropServices;
using Xunit;

public sealed class ScdcTelemetryTests
{
    [Fact]
    public void NativeLayout_MatchesPublishedNvApiV3Abi()
    {
        Assert.Equal((64, 0, 4, 8, 9, 16, 24, 32, 40, 44, 48, 52, 56), ScdcReader.I2cAbi);
        Assert.Equal(0x00030040u, ScdcReader.I2cInfoVersion);
    }

    [Fact]
    public void Read_UsesOnlyGetterInterfacesAndExactCombinedReadTransactions()
    {
        using var native = new FakeNvApi
        {
            Registers = { [0x01] = 1, [0x31] = 0xF4, [0x40] = 0x5E },
        };

        ScdcObservation result = new ScdcReader(native.Query).Read(0x1234);

        Assert.Equal([ScdcReader.GetGpuAndOutputIdInterfaceId, ScdcReader.I2cReadInterfaceId], native.Queries);
        Assert.Equal([0x01, 0x31, 0x40], native.ReadRegisters);
        Assert.All(native.Transactions, transaction =>
        {
            Assert.Equal(new IntPtr(0x123456), transaction.PhysicalGpu);
            Assert.Equal(0x00000008u, transaction.DisplayMask);
            Assert.Equal((byte)1, transaction.IsDdcPort);
            Assert.Equal((byte)0xA8, transaction.DeviceAddress);
            Assert.Equal(1u, transaction.RegisterAddressSize);
            Assert.Equal(1u, transaction.DataSize);
            Assert.Equal(0xFFFFu, transaction.DeprecatedSpeed);
            Assert.Equal(0u, transaction.SpeedKhz);
            Assert.Equal((byte)0, transaction.PortId);
            Assert.Equal(0u, transaction.IsPortIdSet);
        });
        Assert.Equal(2, native.MapCalls);
        Assert.Equal((byte)1, result.SinkVersion);
        Assert.Equal((byte)4, result.FrlRateCode);
        Assert.Equal(4, result.LaneCount);
        Assert.Equal(8, result.GbpsPerLane);
        Assert.Equal((byte)0x0F, result.LaneLockMask);
        Assert.True(result.TrainingReady);
        Assert.Equal("Unknown (not exposed by the public API)", result.ActiveDsc);
        Assert.Equal("Scdc:Available", result.Status);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 3, 3)]
    [InlineData(2, 3, 6)]
    [InlineData(3, 4, 6)]
    [InlineData(4, 4, 8)]
    [InlineData(5, 4, 10)]
    [InlineData(6, 4, 12)]
    public void Read_DecodesPublishedFrlRateCodes(byte code, int lanes, int gbpsPerLane)
    {
        using var native = FakeNvApi.WithScdc(1, (byte)(0xA0 | code), 0);

        ScdcObservation result = new ScdcReader(native.Query).Read(1);

        Assert.Equal(code, result.FrlRateCode);
        Assert.Equal(lanes, result.LaneCount);
        Assert.Equal(gbpsPerLane, result.GbpsPerLane);
        if (code == 0)
        {
            Assert.Equal("Scdc:RateZero", result.Status);
        }
        else
        {
            Assert.Equal("Scdc:Available", result.Status);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(255)]
    public void Read_UnknownSinkVersionDoesNotReadFrlRegistersOrMakeFrlClaims(byte version)
    {
        using var native = FakeNvApi.WithScdc(version, 6, 0x5E);

        ScdcObservation result = new ScdcReader(native.Query).Read(9);

        Assert.Equal([0x01], native.ReadRegisters);
        Assert.Equal(2, native.MapCalls);
        Assert.Equal(version, result.SinkVersion);
        Assert.Null(result.FrlRateCode);
        Assert.Null(result.LaneCount);
        Assert.Null(result.GbpsPerLane);
        Assert.Null(result.LaneLockMask);
        Assert.Null(result.TrainingReady);
        Assert.Equal("Unknown (not exposed by the public API)", result.ActiveDsc);
        Assert.Equal("Scdc:SinkVersionUnsupported", result.Status);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(15)]
    public void Read_ReservedLowNibbleRateClearsAllObservations(byte code)
    {
        using var native = FakeNvApi.WithScdc(1, (byte)(0xF0 | code), 0x5E);

        ScdcObservation result = new ScdcReader(native.Query).Read(1);

        Assert.Null(result.SinkVersion);
        Assert.Null(result.FrlRateCode);
        Assert.Null(result.LaneLockMask);
        Assert.Equal("Scdc:ReservedRate", result.Status);
    }

    [Fact]
    public void Unknown_ClearsObservationsAndNeverClaimsDscState()
    {
        ScdcObservation result = ScdcObservation.Unknown("Scdc:Unavailable");

        Assert.Null(result.SinkVersion);
        Assert.Null(result.FrlRateCode);
        Assert.Null(result.LaneCount);
        Assert.Null(result.GbpsPerLane);
        Assert.Null(result.LaneLockMask);
        Assert.Null(result.TrainingReady);
        Assert.Equal("Unknown (not exposed by the public API)", result.ActiveDsc);
        Assert.Equal("Scdc:Unavailable", result.Status);
    }

    [Fact]
    public void ActiveDsc_IsIncludedInPublicJsonReadout()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(ScdcObservation.Unknown("Scdc:Unavailable"));

        Assert.Contains("\"ActiveDsc\":\"Unknown (not exposed by the public API)\"", json,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    [InlineData(0x01000000u)]
    public void Read_RejectsInvalidOutputMasksBeforeI2c(uint outputMask)
    {
        using var native = FakeNvApi.WithScdc(1, 4, 0);
        native.OutputId = outputMask;

        ScdcObservation result = new ScdcReader(native.Query).Read(1);

        Assert.Empty(native.ReadRegisters);
        Assert.Null(result.SinkVersion);
        Assert.Equal("Scdc:InvalidMapping", result.Status);
    }

    [Fact]
    public void Read_DiscardsObservationWhenMappingChanges()
    {
        using var native = FakeNvApi.WithScdc(1, 4, 0x1E);
        native.ChangeMappingAfterRead = true;

        ScdcObservation result = new ScdcReader(native.Query).Read(1);

        Assert.Equal(2, native.MapCalls);
        Assert.Null(result.SinkVersion);
        Assert.Equal("Scdc:TargetChanged", result.Status);
    }

    [Fact]
    public void Read_MissingEntryPointReturnsUnknownWithoutCallingNativeRead()
    {
        var queries = new List<uint>();
        ScdcObservation result = new ScdcReader(id =>
        {
            queries.Add(id);
            return IntPtr.Zero;
        }).Read(1);

        Assert.Equal([ScdcReader.GetGpuAndOutputIdInterfaceId, ScdcReader.I2cReadInterfaceId], queries);
        Assert.Null(result.SinkVersion);
        Assert.Equal("Scdc:InterfaceUnavailable", result.Status);
    }

    [Fact]
    public void Read_NativeFailuresExposeOnlyFixedSafeStatus()
    {
        using var native = FakeNvApi.WithScdc(1, 4, 0);
        native.I2cStatus = -6;

        using var mappingNative = FakeNvApi.WithScdc(1, 4, 0);
        mappingNative.MapStatus = -14;

        ScdcObservation statusFailure = new ScdcReader(native.Query).Read(1);
        ScdcObservation mappingFailure = new ScdcReader(mappingNative.Query).Read(1);
        ScdcObservation exceptionFailure = new ScdcReader(
            _ => throw new InvalidOperationException("private-driver-message")).Read(1);

        Assert.Equal("Scdc:ReadFailed:-6", statusFailure.Status);
        Assert.Equal("Scdc:MappingFailed:-14", mappingFailure.Status);
        Assert.Equal("Scdc:Unavailable", exceptionFailure.Status);
        Assert.DoesNotContain("private-driver-message", exceptionFailure.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_DoesNotMaskFatalRuntimeFailures()
    {
        Assert.Throws<OutOfMemoryException>(() =>
            new ScdcReader(_ => throw new OutOfMemoryException()).Read(1));
        Assert.Throws<AccessViolationException>(() =>
            new ScdcReader(_ => throw new AccessViolationException()).Read(1));
    }

    [Fact]
    public void Read_RejectsSuccessfulCallThatMutatesBufferGeometry()
    {
        using var native = FakeNvApi.WithScdc(1, 4, 0);
        native.MutateMetadata = true;

        ScdcObservation result = new ScdcReader(native.Query).Read(1);

        Assert.Null(result.SinkVersion);
        Assert.Equal("Scdc:InvalidMetadata", result.Status);
    }

    [Fact]
    public void Read_AcceptsSuccessfulCallThatNormalizesOptionalSpeedAndPortFields()
    {
        using var native = FakeNvApi.WithScdc(1, 5, 0x5E);
        native.NormalizeOptionalMetadata = true;

        ScdcObservation result = new ScdcReader(native.Query).Read(1);

        Assert.Equal((byte)1, result.SinkVersion);
        Assert.Equal((byte)5, result.FrlRateCode);
        Assert.Equal(4, result.LaneCount);
        Assert.Equal(10, result.GbpsPerLane);
        Assert.Equal((byte)0x0F, result.LaneLockMask);
        Assert.True(result.TrainingReady);
        Assert.Equal("Scdc:Available", result.Status);
    }

    private sealed class FakeNvApi : IDisposable
    {
        private readonly ScdcReader.NvApiGetGpuAndOutputId _map;
        private readonly ScdcReader.NvApiI2cRead _read;
        private readonly IntPtr _mapPointer;
        private readonly IntPtr _readPointer;

        internal FakeNvApi()
        {
            _map = Map;
            _read = Read;
            _mapPointer = Marshal.GetFunctionPointerForDelegate(_map);
            _readPointer = Marshal.GetFunctionPointerForDelegate(_read);
        }

        internal Dictionary<byte, byte> Registers { get; } = [];
        internal List<uint> Queries { get; } = [];
        internal List<byte> ReadRegisters { get; } = [];
        internal List<Transaction> Transactions { get; } = [];
        internal IntPtr PhysicalGpu { get; set; } = new(0x123456);
        internal uint OutputId { get; set; } = 0x00000008;
        internal int MapStatus { get; set; }
        internal int I2cStatus { get; set; }
        internal bool ChangeMappingAfterRead { get; set; }
        internal bool MutateMetadata { get; set; }
        internal bool NormalizeOptionalMetadata { get; set; }
        internal int MapCalls { get; private set; }

        internal static FakeNvApi WithScdc(byte version, byte configuration, byte statusFlags) => new()
        {
            Registers =
            {
                [0x01] = version,
                [0x31] = configuration,
                [0x40] = statusFlags,
            },
        };

        internal IntPtr Query(uint id)
        {
            Queries.Add(id);
            return id switch
            {
                ScdcReader.GetGpuAndOutputIdInterfaceId => _mapPointer,
                ScdcReader.I2cReadInterfaceId => _readPointer,
                _ => throw new InvalidOperationException("Unexpected interface query."),
            };
        }

        private int Map(uint displayId, out IntPtr physicalGpu, out uint outputId)
        {
            _ = displayId;
            MapCalls++;
            physicalGpu = ChangeMappingAfterRead && MapCalls == 2
                ? new IntPtr(PhysicalGpu.ToInt64() + 1)
                : PhysicalGpu;
            outputId = OutputId;
            return MapStatus;
        }

        private int Read(IntPtr physicalGpu, ref ScdcReader.NvI2cInfoV3 info)
        {
            byte register = Marshal.ReadByte(info.RegisterAddress);
            ReadRegisters.Add(register);
            Transactions.Add(new(physicalGpu, info.DisplayMask, info.IsDdcPort, info.DeviceAddress,
                info.RegisterAddressSize, info.DataSize, info.DeprecatedSpeed, info.SpeedKhz,
                info.PortId, info.IsPortIdSet));
            if (I2cStatus == 0)
            {
                Marshal.WriteByte(info.Data, Registers[register]);
            }
            if (MutateMetadata)
            {
                info.DataSize = 2;
            }
            if (NormalizeOptionalMetadata)
            {
                info.DeprecatedSpeed = 0;
                info.SpeedKhz = 4;
                info.PortId = 7;
                info.IsPortIdSet = 1;
            }
            return I2cStatus;
        }

        public void Dispose()
        {
            GC.KeepAlive(_map);
            GC.KeepAlive(_read);
        }
    }

    private sealed record Transaction(
        IntPtr PhysicalGpu,
        uint DisplayMask,
        byte IsDdcPort,
        byte DeviceAddress,
        uint RegisterAddressSize,
        uint DataSize,
        uint DeprecatedSpeed,
        uint SpeedKhz,
        byte PortId,
        uint IsPortIdSet);
}
