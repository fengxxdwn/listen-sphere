using ListenSphere.Controller;
using ListenSphere.Controller.Services;
using Xunit;

namespace ListenSphere.Windows.TechnicalTests;

public sealed class LocalSourceEqualizerViewModelTests
{
    [Fact]
    public void ApplySettings_ScalesPresetGainsByEffectStrength()
    {
        var integration = new RecordingEqualizerApoIntegration();
        using var viewModel = new LocalSourceEqualizerViewModel(
            (_, _) => { },
            () => { },
            integration);

        viewModel.ApplySettings(
            true,
            "低音增强",
            [5, 4, 3, 2, 0, -1, -1, 0, 1, 1],
            0.5f);

        Assert.Equal(50f, viewModel.EffectStrengthPercent);
        Assert.Equal(
            [2.5f, 2f, 1.5f, 1f, 0f, -0.5f, -0.5f, 0f, 0.5f, 0.5f],
            integration.LastGains);
    }

    private sealed class RecordingEqualizerApoIntegration : IEqualizerApoIntegration
    {
        public EqualizerApoIntegrationSnapshot Snapshot { get; } = new(
            true,
            true,
            "已同步",
            "#3DBA79",
            "选择生效设备",
            "C:\\EqualizerAPO");

        public float[] LastGains { get; private set; } = [];

        public Task ApplyAsync(
            bool enabled,
            IReadOnlyList<float> gains,
            CancellationToken cancellationToken)
        {
            LastGains = gains.ToArray();
            return Task.CompletedTask;
        }

        public void OpenManagement()
        {
        }
    }
}
