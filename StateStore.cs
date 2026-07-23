using System.IO;
using System.Text.Json;

namespace AnimalHospitalOverlay;

public sealed class StateStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path;

    public StateStore()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AnimalHospitalOverlay");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "state.json");
    }

    public OverlayState Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<OverlayState>(File.ReadAllText(_path), Options)
                       ?? new OverlayState();
        }
        catch
        {
            // A damaged state file should never prevent the overlay from starting.
        }

        return new OverlayState();
    }

    public void Save(OverlayState state)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(state, Options));
    }
}
