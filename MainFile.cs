using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Helpers;
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
        TaskHelper.RunSafely(InitializeModConfigDeferred());
        Harmony harmony = new(ModId);
        harmony.PatchAll();
        ShopTransferNetwork.Initialize();
    }

    private static async System.Threading.Tasks.Task InitializeModConfigDeferred()
    {
        SceneTree? tree = Engine.GetMainLoop() as SceneTree;
        if (tree != null)
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }

        ShopTransferModConfigBridge.TryInitialize();
    }
}
