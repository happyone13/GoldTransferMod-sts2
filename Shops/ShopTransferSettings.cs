using System;
using System.Text.Json;
using Godot;

namespace GoldTransferMod.Shops;

public sealed class ShopTransferConfig
{
    public bool EnableFixedTransfer { get; set; }
    public int FixedTransferAmount { get; set; } = 50;
}

public static class ShopTransferSettings
{
    private const string ConfigPath = "user://gold_transfer_mod_config.json";
    public static ShopTransferConfig Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (!Godot.FileAccess.FileExists(ConfigPath))
            {
                Save();
                MainFile.Logger.Info("[ShopTransfer] Config created with defaults.");
                return;
            }

            using Godot.FileAccess file = Godot.FileAccess.Open(ConfigPath, Godot.FileAccess.ModeFlags.Read);
            string json = file.GetAsText();
            ShopTransferConfig? parsed = JsonSerializer.Deserialize<ShopTransferConfig>(json);
            if (parsed != null)
            {
                Current = parsed;
            }
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[ShopTransfer] Config load failed: {ex}");
        }
    }

    public static void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
            using Godot.FileAccess file = Godot.FileAccess.Open(ConfigPath, Godot.FileAccess.ModeFlags.Write);
            file.StoreString(json);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[ShopTransfer] Config save failed: {ex}");
        }
    }

    public static int GetFixedAmount()
    {
        return Math.Max(1, Current.FixedTransferAmount);
    }

    public static bool IsFreeTransferEnabled()
    {
        return !Current.EnableFixedTransfer;
    }
}
