using System.Text.Json;

namespace SteelBattalionMapper;

internal sealed class ControllerPreferences
{
    private readonly string _path =
        Path.Combine(AppContext.BaseDirectory, "SteelBattalionPreferences.json");

    public bool SwapMainWeaponAndTrigger { get; private set; }

    public ControllerPreferences()
    {
        Load();
    }

    public void ToggleMainWeaponTriggerSwap()
    {
        SwapMainWeaponAndTrigger = !SwapMainWeaponAndTrigger;
        Save();
    }

    public void ResetToDefaults()
    {
        SwapMainWeaponAndTrigger = false;

        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch
        {
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;

            Data? data =
                JsonSerializer.Deserialize<Data>(
                    File.ReadAllText(_path));

            if (data is not null)
                SwapMainWeaponAndTrigger = data.SwapMainWeaponAndTrigger;
        }
        catch
        {
            SwapMainWeaponAndTrigger = false;
        }
    }

    private void Save()
    {
        var data = new Data
        {
            Version = 1,
            SwapMainWeaponAndTrigger = SwapMainWeaponAndTrigger
        };

        File.WriteAllText(
            _path,
            JsonSerializer.Serialize(
                data,
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class Data
    {
        public int Version { get; set; }
        public bool SwapMainWeaponAndTrigger { get; set; }
    }
}
