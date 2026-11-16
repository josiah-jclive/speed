using Speed.Core;

namespace Speed.Devices;

public static class RecorderFactory
{
    public static Recorder Create(RecorderConfig config) => config.Kind switch
    {
        RecorderKind.Shogun => new ShogunRecorder(config),
        RecorderKind.HyperDeck => new HyperDeckRecorder(config),
        RecorderKind.KiPro => new KiProRecorder(config),
        _ => throw new ArgumentOutOfRangeException(nameof(config)),
    };
}
