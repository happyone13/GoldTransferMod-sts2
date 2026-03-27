using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using GoldTransferMod.Shops.Network;

namespace GoldTransferMod.Shops;

public static class ShopTransferNetwork
{
    private static INetGameService? _registeredNetService;
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        RunManager.Instance.RunStarted += OnRunStarted;
        EnsureHandlersRegistered();
        MainFile.Logger.Info("[ShopTransfer] Network initialized.");
    }

    public static void EnsureHandlersRegistered()
    {
        INetGameService? netService = RunManager.Instance.NetService;
        if (netService == null || ReferenceEquals(netService, _registeredNetService))
        {
            return;
        }

        if (_registeredNetService != null)
        {
            try
            {
                _registeredNetService.UnregisterMessageHandler<GoldTransferMessage>(HandleGoldTransferMessage);
            }
            catch
            {
            }
        }

        netService.RegisterMessageHandler<GoldTransferMessage>(HandleGoldTransferMessage);
        _registeredNetService = netService;
        MainFile.Logger.Info("[ShopTransfer] GoldTransferMessage handler registered.");
    }

    public static void SendTransferMessage(Player targetPlayer, int amount)
    {
        EnsureHandlersRegistered();

        INetGameService? netService = RunManager.Instance.NetService;
        if (netService == null)
        {
            MainFile.Logger.Warn("[ShopTransfer] Send failed: NetService is null.");
            return;
        }

        GoldTransferMessage message = new()
        {
            TargetId = targetPlayer.NetId,
            Amount = amount
        };

        netService.SendMessage(message);
        MainFile.Logger.Info($"[ShopTransfer] Sent transfer message target={targetPlayer.NetId} amount={amount}");
    }

    private static void OnRunStarted(RunState _)
    {
        EnsureHandlersRegistered();
    }

    private static void HandleGoldTransferMessage(GoldTransferMessage message, ulong senderId)
    {
        INetGameService? netService = RunManager.Instance.NetService;
        if (netService == null)
        {
            return;
        }

        if (netService.NetId != message.TargetId)
        {
            return;
        }

        Player? localPlayer = GetLocalPlayer();
        if (localPlayer == null)
        {
            MainFile.Logger.Warn("[ShopTransfer] Receive failed: local player not found.");
            return;
        }

        MainFile.Logger.Info($"[ShopTransfer] Received transfer from {senderId} amount={message.Amount}");
        TaskHelper.RunSafely(ApplyTransfer(localPlayer, message.Amount));
    }

    private static async Task ApplyTransfer(Player localPlayer, int amount)
    {
        await PlayerCmd.GainGold(amount, localPlayer);
        MainFile.Logger.Info($"[ShopTransfer] Applied transfer gain for {localPlayer.NetId} amount={amount}");
    }

    private static Player? GetLocalPlayer()
    {
        try
        {
            INetGameService? netService = RunManager.Instance.NetService;
            if (netService == null)
            {
                return null;
            }

            PropertyInfo? runStateProp = typeof(RunManager).GetProperty(
                "State",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance
            );
            RunState? runState = runStateProp?.GetValue(RunManager.Instance) as RunState;
            if (runState == null)
            {
                return null;
            }

            return runState.Players.FirstOrDefault(p => p.NetId == netService.NetId);
        }
        catch (Exception ex)
        {
            MainFile.Logger.Error($"[ShopTransfer] GetLocalPlayer error: {ex}");
            return null;
        }
    }
}
