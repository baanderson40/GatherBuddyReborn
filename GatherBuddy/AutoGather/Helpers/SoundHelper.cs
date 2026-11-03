using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using GatherBuddy.Utility;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GatherBuddy.AutoGather.Helpers;

public sealed class SoundHelper : IDisposable
{
    private const string SoundResource = "GatherBuddy.CustomInfo.honk-sound.wav";
    private readonly object _lock = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DiagnosticThrottle<string> _diagnostics = new(TimeSpan.FromSeconds(30), 8);
    private bool _disposed;

    public void StartHonkSoundTask(int repeatCount)
    {
        lock (_lock)
        {
            if (_disposed || repeatCount <= 0) return;
            StartPlayback(repeatCount, _lifetime.Token);
        }
    }

    private void StartPlayback(int repeatCount, CancellationToken token)
    {
        var volume = System.Math.Clamp(GatherBuddy.Config.AutoGatherConfig.SoundPlaybackVolume, 0, 100) / 100f;
        // All failures are observed inside the task; never block the framework thread.
        _ = Task.Run(() => PlayHonkSoundAsync(repeatCount, volume, token));
    }

    private async Task PlayHonkSoundAsync(int repeatCount, float volume, CancellationToken token)
    {
        try
        {
            using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(SoundResource)
                ?? throw new FileNotFoundException($"Embedded resource {SoundResource} not found.");
            using var buffer = new MemoryStream();
            await resource.CopyToAsync(buffer, token).ConfigureAwait(false);
            var soundData = buffer.ToArray();

            for (var i = 0; i < repeatCount; i++)
            {
                token.ThrowIfCancellationRequested();
                using var enumerator = new MMDeviceEnumerator();
                if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia, out var device))
                {
                    if (_diagnostics.TryLog("endpoint", "missing", out var suppressed))
                        GatherBuddy.Log.Warning("Honk skipped: no default audio output device." + DiagnosticThrottle.Suffix(suppressed));
                    return; // Retry endpoint discovery on the next request, not every frame.
                }
                using (device)
                using (var stream = new MemoryStream(soundData, false))
                using (var reader = new WaveFileReader(stream))
                using (var output = new WasapiPlayerBuilder().WithDevice(device).WithSharedMode().WithEventSync().WithLatency(100).Build())
                {
                    var samples = CreateSamples(reader, volume);
                    var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    output.PlaybackStopped += (_, args) =>
                    {
                        stopped.TrySetResult(args.Exception);
                    };
                    output.Init(samples.ToWaveProvider());
                    token.ThrowIfCancellationRequested();
                    output.Play();
                    try
                    {
                        var error = await stopped.Task.WaitAsync(reader.TotalTime + TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
                        if (error != null) throw new IOException("Honk playback failed.", error);
                        _diagnostics.Forget("endpoint");
                    }
                    finally
                    {
                        output.Stop();
                    }
                }
                await Task.Delay(200, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && _diagnostics.TryLog("playback", $"{ex.GetType().FullName}:{ex.HResult}", out var suppressed))
                GatherBuddy.Log.Error($"Error during honk: {ex}" + DiagnosticThrottle.Suffix(suppressed));
        }
    }

    private static ISampleProvider CreateSamples(WaveFileReader reader, float volume)
    {
        var samples = reader.ToSampleProvider();
        if (samples.WaveFormat.Channels == 2) samples = samples.ToMono();
        // Scale this stream only; never change Windows master/session volume.
        return new VolumeSampleProvider(samples) { Volume = System.Math.Clamp(volume, 0, 1) };
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            _lifetime.Dispose();
        }
    }
}
