using NAudio.CoreAudioApi;

namespace Whispdows;

public sealed record AudioDeviceOption(string Id, string Name);

public static class AudioDeviceCatalog
{
    public static IReadOnlyList<AudioDeviceOption> GetCaptureDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var devices = new List<AudioDeviceOption>
        {
            new("default", "Default microphone")
        };

        foreach (var device in enumerator.EnumerateAudioEndPoints(
                     DataFlow.Capture,
                     DeviceState.Active))
        {
            using (device)
            {
                if (devices.Any(item => string.Equals(item.Id, device.ID, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                devices.Add(new AudioDeviceOption(device.ID, device.FriendlyName));
            }
        }

        return devices;
    }
}
