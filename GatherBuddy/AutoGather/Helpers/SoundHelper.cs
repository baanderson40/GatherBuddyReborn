using CSCore;
using CSCore.Codecs.WAV;
using CSCore.SoundOut;
using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CSCore.Streams;

namespace GatherBuddy.AutoGather.Helpers;

public sealed class SoundHelper : IDisposable
{
    private const string SoundResource = "GatherBuddy.CustomInfo.honk-sound.wav";
    private int _disposed;

    public void StartHonkSoundTask(int repeatCount)
    {
        if (repeatCount <= 0 || Volatile.Read(ref _disposed) != 0) return;
        _ = Task.Run(() => PlayHonkSound(repeatCount));
    }

    private static void PlayHonkSound(int repeatCount)
    {
        try
        {
            var assembly = Assembly.GetExecutingAssembly();
            using var resourceStream = assembly.GetManifestResourceStream(SoundResource);
            if (resourceStream == null)
            {
                throw new FileNotFoundException($"Embedded resource {SoundResource} not found.");
            }

            using var ms = new MemoryStream();
            resourceStream.CopyTo(ms);
            var soundData = ms.ToArray(); // Keep a copy of the sound's bytes

            for (var i = 0; i < repeatCount; i++)
            {
                using var audioStream = new MemoryStream(soundData); // Fresh each time
                using var reader = new WaveFileReader(audioStream);
                using var soundSource = CreateSamples(reader, GatherBuddy.Config.AutoGatherConfig.SoundPlaybackVolume / 100f);
                using var soundOut = new WasapiOut();
                soundOut.Initialize(soundSource.ToWaveSource());

                soundOut.Play();
                while (soundOut.PlaybackState == PlaybackState.Playing)
                {
                    Task.Delay(10).Wait();
                }

                Task.Delay(200).Wait();
            }
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Error($"Error during honk: {ex}");
        }
    }

    private static ISampleSource CreateSamples(WaveFileReader reader, float volume)
    {
        var samples = reader.ToSampleSource().ToMono();
        return new VolumeSource(samples) { Volume = Math.Clamp(volume, 0f, 1f) };
    }

    private static void PlaySound(Stream stream)
    { }

    public void Dispose()
        => Interlocked.Exchange(ref _disposed, 1);
}
