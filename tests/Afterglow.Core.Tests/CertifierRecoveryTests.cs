using Afterglow.Core.Interop.Nvapi;
using Afterglow.Core.Interop.Nvml;
using Afterglow.Core.Profiles;
using Afterglow.Core.Stress;
using Afterglow.Core.Tuning;

namespace Afterglow.Core.Tests;

public class CertifierRecoveryTests
{
    [Theory]
    [InlineData(CertificationModes.Sustained)]
    [InlineData(CertificationModes.Transitions)]
    [InlineData(CertificationModes.Excursions)]
    [InlineData(CertificationModes.Vram)]
    public void Unconfirmed_workload_stop_withholds_actual_failure_reset_callback(string failedMode)
    {
        var tuner = new FakeTuner();
        var result = RunFailure(tuner, failedMode, stopped: false);

        Assert.Equal("failed", result.Phase);
        Assert.False(result.Passed);
        Assert.Equal(failedMode, result.FailedMode);
        Assert.Null(result.ResetSucceeded);
        Assert.Equal(0, tuner.ResetCalls);
        Assert.Contains(result.Log, line => line.Contains("reset is withheld", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(CertificationModes.Sustained, true)]
    [InlineData(CertificationModes.Sustained, false)]
    [InlineData(CertificationModes.Vram, true)]
    [InlineData(CertificationModes.Vram, false)]
    public void Confirmed_workload_stop_preserves_actual_failure_reset_and_readback_result(
        string failedMode, bool resetSucceeds)
    {
        var tuner = new FakeTuner { ResetSucceeds = resetSucceeds };
        var result = RunFailure(tuner, failedMode, stopped: true);

        Assert.Equal("failed", result.Phase);
        Assert.False(result.Passed);
        Assert.Equal(failedMode, result.FailedMode);
        Assert.Equal(resetSucceeds, result.ResetSucceeded);
        Assert.Equal(1, tuner.ResetCalls);
        if (!resetSucceeds)
        {
            Assert.Contains(result.Log, line => line.Contains("reset did NOT fully succeed", StringComparison.Ordinal));
        }
    }

    private static CertifierStatus RunFailure(FakeTuner tuner, string failedMode, bool stopped)
    {
        string directory = Path.Combine(Path.GetTempPath(), "afterglow-certifier-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ProfileStore(directory);
            var profile = new TuningProfile { Name = "fake-certifier", GpuUuid = tuner.GpuUuid };
            store.Save(profile);
            int stopCalls = 0;
            var modesRun = new List<string>();
            bool FakeStopAndWait()
            {
                stopCalls++;
                return stopped;
            }

            // Start invokes the production Run and its real tuner reset callback.
            // Only native workload execution is substituted; no GPU is opened.
            var certifier = new ProfileCertifier(tuner, store, null, StressAdapter.NvidiaVendorId,
                (mode, _, _) =>
                {
                    modesRun.Add(mode);
                    return mode == failedMode
                        ? (false, string.Empty, "fake terminal failure", FakeStopAndWait())
                        : (true, "fake completed mode", null, true);
                });
            using var done = new ManualResetEventSlim();
            CertifierStatus? terminal = null;
            certifier.StatusChanged += status =>
            {
                if (!status.Running)
                {
                    terminal = status;
                    done.Set();
                }
            };
            certifier.Start(profile, new CertifierOptions { SecondsPerMode = 90 });

            Assert.True(done.Wait(TimeSpan.FromSeconds(5)), "certifier did not publish its fake failure");
            Assert.Equal(1, stopCalls);
            var completedModes = CertificationModes.All.TakeWhile(mode => mode != failedMode).ToArray();
            Assert.Equal(completedModes.Append(failedMode), modesRun);
            var saved = store.Load(profile.Name)!;
            Assert.False(saved.MarkedStable);
            Assert.Equal(completedModes, saved.Certifications.Select(certificate => certificate.Mode));
            return terminal!;
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private sealed class FakeTuner : IGpuTuner
    {
        public TuningCapabilities Capabilities { get; } = new();
        public string? GpuUuid => "FAKE-CERTIFIER";
        public uint? AppliedLockMHz => null;
        public bool ResetSucceeds { get; init; } = true;
        public int ResetCalls { get; private set; }
        public (int, int, double?, uint?, uint?) ReadCurrent() => (0, 0, 525, 0, null);
        public ApplyResult Apply(TuningProfile profile, bool reconcileVfPoints = true, bool releaseLock = false) =>
            new(true, [KnobResult.Ok("fake apply")]);
        public ApplyResult ResetToDefaults()
        {
            ResetCalls++;
            return new(ResetSucceeds, [ResetSucceeds ? KnobResult.Ok("fake reset") : KnobResult.Fail("fake reset", "refused")]);
        }
        public KnobResult ForceUnlock() => KnobResult.Ok("unused");
        public NvmlReturn RestoreTuningLock(uint lockMHz) => NvmlReturn.NotSupported;
        public NvmlReturn LockClockForProbe(uint clockMHz) => NvmlReturn.NotSupported;
        public NvmlReturn SetAllFansRaw(uint dutyPct) => NvmlReturn.NotSupported;
        public NvmlReturn SetFanRaw(uint coolerId, uint dutyPct) => NvmlReturn.NotSupported;
        public NvmlReturn RestoreAutoFansRaw() => NvmlReturn.NotSupported;
        public NvapiStatus TryReadVfPoints(out IReadOnlyList<NvapiGpu.VfpTablePoint> points)
        {
            points = [];
            return NvapiStatus.NotSupported;
        }
        public KnobResult SetVfPointOffsets(IReadOnlyDictionary<int, int> offsetsMHzByIndex) => KnobResult.Fail("unused", "unsupported");
        public KnobResult ClearVfPointOffsets() => KnobResult.Fail("unused", "unsupported");
    }
}
