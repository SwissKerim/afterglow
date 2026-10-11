using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

public class PowerLimitReadbackTests
{
    [Fact]
    public void Immediate_matching_read_succeeds_without_waiting()
    {
        int reads = 0, waits = 0;
        uint requested = 0;
        var result = GpuTuner.SetPowerLimitVerified(550, "550 W",
            watts => { requested = watts; return NvmlReturn.Success; },
            () => { reads++; return (NvmlReturn.Success, 550000u); }, () => waits++);

        Assert.True(result.Applied);
        Assert.Equal("550 W (verified)", result.Detail);
        Assert.Equal(550000u, requested);
        Assert.Equal(1, reads);
        Assert.Equal(0, waits);
    }

    [Fact]
    public void Delayed_driver_convergence_succeeds_after_retrying()
    {
        int reads = 0, waits = 0;
        var result = GpuTuner.SetPowerLimitVerified(550, "550 W", _ => NvmlReturn.Success,
            () => (NvmlReturn.Success, ++reads < 3 ? 575000u : 550000u), () => waits++);

        Assert.True(result.Applied);
        Assert.Equal(3, reads);
        Assert.Equal(2, waits);
    }

    [Fact]
    public void Final_allowed_read_can_verify_the_cap()
    {
        int reads = 0, waits = 0;
        var result = GpuTuner.SetPowerLimitVerified(550, "550 W", _ => NvmlReturn.Success,
            () => (NvmlReturn.Success, ++reads < 21 ? 575000u : 550000u), () => waits++);

        Assert.True(result.Applied);
        Assert.Equal(21, reads);
        Assert.Equal(20, waits);
    }

    [Fact]
    public void Permanent_mismatch_fails_with_last_observed_cap_and_bounded_retries()
    {
        int reads = 0, waits = 0;
        var result = GpuTuner.SetPowerLimitVerified(550, "550 W", _ => NvmlReturn.Success,
            () => { reads++; return (NvmlReturn.Success, 575000u); }, () => waits++);

        Assert.False(result.Applied);
        Assert.Contains("last observed 575.0 W", result.Detail, StringComparison.Ordinal);
        Assert.Equal(21, reads);
        Assert.Equal(20, waits);
    }

    [Fact]
    public void Missing_reads_never_certify_even_if_the_out_value_matches()
    {
        int reads = 0, waits = 0;
        var result = GpuTuner.SetPowerLimitVerified(550, "550 W", _ => NvmlReturn.Success,
            () => { reads++; return (NvmlReturn.NotSupported, 550000u); }, () => waits++);

        Assert.False(result.Applied);
        Assert.Contains("last read NotSupported", result.Detail, StringComparison.Ordinal);
        Assert.Contains("enforced limit unavailable", result.Detail, StringComparison.Ordinal);
        Assert.Equal(21, reads);
        Assert.Equal(20, waits);
    }

    [Fact]
    public void Failed_final_read_reports_the_last_successful_mismatch_honestly()
    {
        int reads = 0;
        var result = GpuTuner.SetPowerLimitVerified(550, "550 W", _ => NvmlReturn.Success,
            () => ++reads == 1 ? (NvmlReturn.Success, 575000u) : (NvmlReturn.NotSupported, 0u), () => { });

        Assert.False(result.Applied);
        Assert.Contains("last read NotSupported", result.Detail, StringComparison.Ordinal);
        Assert.Contains("last observed 575.0 W", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Transient_missing_read_can_recover_to_successful_readback()
    {
        int reads = 0, waits = 0;
        var result = GpuTuner.SetPowerLimitVerified(550, "550 W", _ => NvmlReturn.Success,
            () => ++reads < 3 ? (NvmlReturn.NotSupported, 0u) : (NvmlReturn.Success, 550000u), () => waits++);

        Assert.True(result.Applied);
        Assert.Equal(3, reads);
        Assert.Equal(2, waits);
    }

    [Fact]
    public void Failed_write_never_reads_or_waits_or_reports_verified()
    {
        int reads = 0, waits = 0;
        var result = GpuTuner.SetPowerLimitVerified(550, "550 W", _ => NvmlReturn.NoPermission,
            () => { reads++; return (NvmlReturn.Success, 550000u); }, () => waits++);

        Assert.False(result.Applied);
        Assert.Equal("write failed: NoPermission", result.Detail);
        Assert.Equal(0, reads);
        Assert.Equal(0, waits);
    }

    [Theory]
    [InlineData(551500u, true)]
    [InlineData(548500u, true)]
    [InlineData(551501u, false)]
    [InlineData(548499u, false)]
    public void Enforced_read_preserves_the_existing_one_point_five_watt_tolerance(uint enforced, bool expected)
    {
        var result = GpuTuner.SetPowerLimitVerified(550, "550 W", _ => NvmlReturn.Success,
            () => (NvmlReturn.Success, enforced), () => { });

        Assert.Equal(expected, result.Applied);
    }

    [Fact]
    public void Stock_reset_cap_is_verified_after_delayed_recovery_from_reduced_cap()
    {
        int reads = 0;
        uint requested = 0;
        var result = GpuTuner.SetPowerLimitVerified(575, "575 W",
            watts => { requested = watts; return NvmlReturn.Success; },
            () => (NvmlReturn.Success, ++reads < 3 ? 550000u : 575000u), () => { });

        Assert.True(result.Applied);
        Assert.Equal(575000u, requested);
        Assert.Equal("575 W (verified)", result.Detail);
        Assert.Equal(3, reads);
    }
}
