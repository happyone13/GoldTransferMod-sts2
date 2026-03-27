using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

namespace GoldTransferMod.Shops;

[HarmonyPatch]
public static class ShopTransferButtonPatches
{
    private const string ButtonName = "GoldTransferMod_TransferGoldButton";

    private sealed class ButtonState
    {
        public required Player Player { get; init; }
        public required Action OnGoldChanged { get; init; }
    }

    private static readonly ConcurrentDictionary<ulong, ButtonState> States = new();

    [HarmonyPatch(typeof(NMerchantInventory), "Open")]
    [HarmonyPostfix]
    private static void OpenPostfix(NMerchantInventory __instance)
    {
        ShopTransferModConfigBridge.TryInitialize();
        ShopTransferNetwork.EnsureHandlersRegistered();

        if (__instance.Inventory == null)
        {
            MainFile.Logger.Info("[ShopTransfer] Open skipped: inventory is null.");
            return;
        }

        Player player = __instance.Inventory.Player;
        if (player.RunState.Players.Count <= 1)
        {
            MainFile.Logger.Info("[ShopTransfer] Open skipped: single-player run.");
            return;
        }

        Button button = __instance.GetNodeOrNull<Button>(ButtonName);
        if (button == null)
        {
            button = CreateButton();
            __instance.AddChild(button);
            MainFile.Logger.Info("[ShopTransfer] Button created.");
        }

        if (!States.ContainsKey(__instance.GetInstanceId()))
        {
            Action onGoldChanged = () => UpdateButton(button, player);
            player.GoldChanged += onGoldChanged;

            States[__instance.GetInstanceId()] = new ButtonState
            {
                Player = player,
                OnGoldChanged = onGoldChanged
            };

            button.Pressed += () => TaskHelper.RunSafely(OnTransferPressed(__instance, button));
            MainFile.Logger.Info("[ShopTransfer] Button handler registered.");
        }

        UpdateButton(button, player);
        MainFile.Logger.Info($"[ShopTransfer] Button visible. Gold={player.Gold} Used={TransferGoldState.IsUsed(player.RunState)}");
    }

    [HarmonyPatch(typeof(NMerchantInventory), "_ExitTree")]
    [HarmonyPrefix]
    private static void ExitTreePrefix(NMerchantInventory __instance)
    {
        if (!States.TryRemove(__instance.GetInstanceId(), out ButtonState? state))
        {
            return;
        }

        state.Player.GoldChanged -= state.OnGoldChanged;
        MainFile.Logger.Info("[ShopTransfer] Inventory exit: handler removed.");
    }

    private static Button CreateButton()
    {
        int fixedAmount = ShopTransferSettings.GetFixedAmount();
        return new Button
        {
            Name = ButtonName,
            Text = ShopTransferSettings.IsFreeTransferEnabled()
                ? "Transfer Gold"
                : $"Transfer {fixedAmount}g",
            Size = new Vector2(240f, 56f),
            Position = new Vector2(1220f, 840f),
            FocusMode = Control.FocusModeEnum.All
        };
    }

    private static async Task OnTransferPressed(NMerchantInventory inventoryUi, Button button)
    {
        if (inventoryUi.Inventory == null)
        {
            MainFile.Logger.Warn("[ShopTransfer] Press ignored: inventory is null.");
            return;
        }

        Player player = inventoryUi.Inventory.Player;
        MainFile.Logger.Info($"[ShopTransfer] Pressed. Gold={player.Gold}");

        int amount;
        if (ShopTransferSettings.IsFreeTransferEnabled())
        {
            int? chosen = await PromptTransferAmount(inventoryUi, player.Gold);
            if (!chosen.HasValue)
            {
                MainFile.Logger.Info("[ShopTransfer] Transfer amount selection canceled.");
                return;
            }
            amount = chosen.Value;
        }
        else
        {
            amount = ShopTransferSettings.GetFixedAmount();
        }

        MerchantTransferGoldEntry transferEntry = new(player, amount)
        {
            TargetStartPosition = button.GlobalPosition + button.Size * 0.5f
        };

        bool success = await transferEntry.OnTryPurchaseWrapper(inventoryUi.Inventory);
        if (success)
        {
            button.Disabled = true;
            button.Text = "Transferred";
            MainFile.Logger.Info("[ShopTransfer] Transfer success.");
            return;
        }

        MainFile.Logger.Info("[ShopTransfer] Transfer canceled or failed.");
        UpdateButton(button, player);
    }

    private static void UpdateButton(Button button, Player player)
    {
        bool used = TransferGoldState.IsUsed(player.RunState);
        int fixedAmount = ShopTransferSettings.GetFixedAmount();
        bool freeMode = ShopTransferSettings.IsFreeTransferEnabled();
        bool canAfford = freeMode ? player.Gold >= 1 : player.Gold >= fixedAmount;

        button.Disabled = (!freeMode && used) || !canAfford;
        button.Text = !freeMode && used
            ? "Transfer used"
            : (freeMode ? "Transfer Gold" : $"Transfer {fixedAmount}g");
    }

    private static Task<int?> PromptTransferAmount(Control owner, int maxGold)
    {
        TaskCompletionSource<int?> tcs = new();

        if (maxGold <= 0)
        {
            tcs.SetResult(null);
            return tcs.Task;
        }

        ConfirmationDialog dialog = new()
        {
            Title = "Transfer Gold",
            Name = "GoldTransferAmountDialog",
            Exclusive = true,
            Unresizable = true,
            Size = new Vector2I(420, 180)
        };

        VBoxContainer root = new();
        root.CustomMinimumSize = new Vector2(380f, 120f);
        root.AddThemeConstantOverride("separation", 10);

        Label label = new()
        {
            Text = $"Select amount (1 - {maxGold})"
        };

        HSlider slider = new()
        {
            MinValue = 1,
            MaxValue = maxGold,
            Step = 1,
            Value = Math.Min(maxGold, ShopTransferSettings.GetFixedAmount()),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill
        };

        SpinBox spin = new()
        {
            MinValue = 1,
            MaxValue = maxGold,
            Step = 1,
            Value = slider.Value
        };

        slider.ValueChanged += value => spin.Value = value;
        spin.ValueChanged += value => slider.Value = value;

        root.AddChild(label);
        root.AddChild(slider);
        root.AddChild(spin);

        dialog.AddChild(root);
        owner.AddChild(dialog);
        dialog.PopupCentered();

        void CompleteAndCleanup(int? value)
        {
            if (!tcs.Task.IsCompleted)
            {
                tcs.SetResult(value);
            }
            dialog.QueueFree();
        }

        dialog.Confirmed += () => CompleteAndCleanup((int)Math.Round(spin.Value));
        dialog.Canceled += () => CompleteAndCleanup(null);
        dialog.CloseRequested += () => CompleteAndCleanup(null);

        return tcs.Task;
    }
}
