using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using GoldTransferMod.Shops;

namespace GoldTransferMod;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    public const string ModId = "GoldTransferMod";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } =
        new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        ShopTransferSettings.Load();
        ShopTransferModConfigBridge.TryInitialize();
        Harmony harmony = new(ModId);
        harmony.PatchAll();
        ShopTransferNetwork.Initialize();
    }
}
