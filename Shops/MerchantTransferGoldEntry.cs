using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Multiplayer;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace GoldTransferMod.Shops;

public sealed class MerchantTransferGoldEntry : MerchantEntry
{
    private readonly int _amount;

    public Vector2 TargetStartPosition { get; set; }

    public MerchantTransferGoldEntry(Player player, int amount)
        : base(player)
    {
        _amount = amount;
        CalcCost();
    }

    public override bool IsStocked =>
        _player.RunState.CurrentRoom is MerchantRoom &&
        _player.RunState.Players.Count > 1 &&
        (ShopTransferSettings.IsFreeTransferEnabled() || !TransferGoldState.IsUsed(_player.RunState));

    public override void CalcCost()
    {
        _cost = _amount;
    }

    protected override async Task<(bool, int)> OnTryPurchase(MerchantInventory? inventory, bool ignoreCost)
    {
        if (!IsStocked)
        {
            MainFile.Logger.Info("[ShopTransfer] Entry rejected: not stocked.");
            InvokePurchaseFailed(PurchaseStatus.FailureForbidden);
            return (false, 0);
        }

        Player? targetPlayer = await Targeting.SelectTargetPlayerAsync(TargetStartPosition);
        if (targetPlayer == null)
        {
            MainFile.Logger.Info("[ShopTransfer] Targeting canceled.");
            return (false, 0);
        }

        if (ReferenceEquals(targetPlayer, _player) || LocalContext.IsMe(targetPlayer) && LocalContext.IsMe(_player))
        {
            MainFile.Logger.Info("[ShopTransfer] Targeting rejected: self target.");
            InvokePurchaseFailed(PurchaseStatus.FailureForbidden);
            return (false, 0);
        }

        await PlayerCmd.LoseGold(_amount, _player);

        NetGameType? netType = RunManager.Instance?.NetService?.Type;
        if (netType is NetGameType.Host or NetGameType.Client)
        {
            ShopTransferNetwork.SendTransferMessage(targetPlayer, _amount);
            MainFile.Logger.Info($"[ShopTransfer] Transfer sent: {_player.NetId} -> {targetPlayer.NetId}, amount={_amount}");
        }
        else
        {
            await PlayerCmd.GainGold(_amount, targetPlayer);
            MainFile.Logger.Info($"[ShopTransfer] Transfer committed (single-player): {_player.NetId} -> {targetPlayer.NetId}, amount={_amount}");
        }

        if (!ShopTransferSettings.IsFreeTransferEnabled())
        {
            TransferGoldState.MarkUsed(_player.RunState);
        }
        return (true, _amount);
    }

    protected override void ClearAfterPurchase()
    {
    }

    protected override void RestockAfterPurchase(MerchantInventory? inventory)
    {
    }

    private static class Targeting
    {
        public static async Task<Player?> SelectTargetPlayerAsync(Vector2 startPosition)
        {
            NTargetManager targetManager = NTargetManager.Instance;
            if (targetManager == null)
            {
                return null;
            }

            bool isUsingController = NControllerManager.Instance.IsUsingController;
            targetManager.StartTargeting(
                TargetType.AnyPlayer,
                startPosition,
                isUsingController ? TargetMode.Controller : TargetMode.ClickMouseToTarget,
                ShouldCancelTargeting,
                null
            );

            if (isUsingController)
            {
                NMultiplayerPlayerStateContainer container = NRun.Instance.GlobalUi.MultiplayerPlayerContainer;
                container.FirstPlayerState?.Hitbox.GrabFocus();
                container.LockNavigation();
            }

            try
            {
                Node node = await targetManager.SelectionFinished();
                NRun.Instance.GlobalUi.MultiplayerPlayerContainer.UnlockNavigation();

                return node switch
                {
                    NMultiplayerPlayerState playerState => playerState.Player,
                    NCreature creature => creature.Entity.Player,
                    _ => null
                };
            }
            finally
            {
                NRun.Instance.GlobalUi.MultiplayerPlayerContainer.UnlockNavigation();
            }
        }

        private static bool ShouldCancelTargeting()
        {
            return NOverlayStack.Instance.ScreenCount > 0;
        }
    }
}

internal static class TransferGoldState
{
    private readonly struct ShopKey : IEquatable<ShopKey>
    {
        public readonly int ActIndex;
        public readonly int Row;
        public readonly int Col;

        public ShopKey(int actIndex, int row, int col)
        {
            ActIndex = actIndex;
            Row = row;
            Col = col;
        }

        public bool Equals(ShopKey other) =>
            ActIndex == other.ActIndex && Row == other.Row && Col == other.Col;

        public override bool Equals(object? obj) =>
            obj is ShopKey other && Equals(other);

        public override int GetHashCode() =>
            HashCode.Combine(ActIndex, Row, Col);
    }

    private static readonly ConcurrentDictionary<IRunState, ConcurrentDictionary<ShopKey, byte>> UsedByRun = new();

    public static bool IsUsed(IRunState runState)
    {
        if (!TryGetKey(runState, out ShopKey key))
        {
            return false;
        }

        return UsedByRun.TryGetValue(runState, out var set) && set.ContainsKey(key);
    }

    public static void MarkUsed(IRunState runState)
    {
        if (!TryGetKey(runState, out ShopKey key))
        {
            return;
        }

        var set = UsedByRun.GetOrAdd(runState, _ => new ConcurrentDictionary<ShopKey, byte>());
        set[key] = 0;
    }

    private static bool TryGetKey(IRunState runState, out ShopKey key)
    {
        if (runState is not RunState rs)
        {
            key = default;
            return false;
        }

        MapCoord? coord = rs.CurrentMapCoord;
        if (!coord.HasValue)
        {
            key = default;
            return false;
        }

        key = new ShopKey(rs.CurrentActIndex, coord.Value.row, coord.Value.col);
        return true;
    }
}
