using System.Reflection;
using GatherBuddy.AutoGather.Helpers;
using NAudio.Wave;
using Xunit;

namespace GatherBuddy.Compatibility.Tests;

public sealed class SoundWarningTests
{
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 0.25f)]
    [InlineData(1f, 0.5f)]
    [InlineData(-1f, 0f)]
    [InlineData(2f, 0.5f)]
    public void RealWavePipelineIsMonoWithClampedStreamLocalVolume(float volume, float expected)
    {
        using var stream = new MemoryStream();
        // Two stereo PCM16 frames at +0.5 and -0.5. No endpoint or OS mixer involved.
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write("RIFF"u8); writer.Write(44); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)2);
            writer.Write(44100); writer.Write(176400); writer.Write((short)4); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(8);
            writer.Write((short)16384); writer.Write((short)16384);
            writer.Write((short)-16384); writer.Write((short)-16384);
        }
        stream.Position = 0;
        using var reader = new WaveFileReader(stream);
        var samples = (ISampleProvider)typeof(SoundHelper)
            .GetMethod("CreateSamples", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [reader, volume])!;
        Assert.Equal(1, samples.WaveFormat.Channels);
        Span<float> output = stackalloc float[2];
        Assert.Equal(2, samples.Read(output));
        Assert.Equal(expected, output[0], 5);
        Assert.Equal(-expected, output[1], 5);
        Assert.Equal(0, samples.Read(output));
    }

    [Fact]
    public void DisposedHelperRejectsNewWorkWithoutReadingPluginServices()
    {
        var helper = new SoundHelper();
        helper.StartHonkSoundTask(0);
        helper.Dispose();
        helper.Dispose();
        helper.StartHonkSoundTask(3);
    }
}
