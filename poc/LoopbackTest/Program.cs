using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

using var enumerator = new MMDeviceEnumerator();
var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
Console.WriteLine($"対象デバイス: {device.FriendlyName} (マスターミュート: {device.AudioEndpointVolume.Mute})");

using var capture = new WasapiLoopbackCapture(device);
Console.WriteLine($"キャプチャフォーマット: {capture.WaveFormat}");

var sw = Stopwatch.StartNew();
long lastLogMs = 0;
float windowPeak = 0f;
int bytesPerSample = capture.WaveFormat.BitsPerSample / 8;

capture.DataAvailable += (s, e) =>
{
    int sampleCount = e.BytesRecorded / bytesPerSample;
    for (int i = 0; i < sampleCount; i++)
    {
        float sample = Math.Abs(BitConverter.ToSingle(e.Buffer, i * bytesPerSample));
        if (sample > windowPeak) windowPeak = sample;
    }

    if (sw.ElapsedMilliseconds - lastLogMs >= 200)
    {
        Console.WriteLine($"[{sw.ElapsedMilliseconds,6}ms] peak={windowPeak:F4}");
        windowPeak = 0f;
        lastLogMs = sw.ElapsedMilliseconds;
    }
};

capture.StartRecording();
Console.WriteLine("20秒間監視します。");
await Task.Delay(20000);
capture.StopRecording();
Console.WriteLine("終了。");
