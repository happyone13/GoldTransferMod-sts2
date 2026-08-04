using System;
using System.Collections.Concurrent;
using System.Reflection;
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
    private const string UsedOverlayName = "GoldTransferMod_TransferGoldUsedOverlay";
    private const string TransferTexturePath = "res://GoldTransferMod/Transfer_Button.png";
    private static readonly Vector2 TransferButtonSize = new(276f, 252f);
    private static readonly Vector2 RemovalVisualOffset = new(125f, -170f);
    private static readonly Vector2 RemovalNodeOffset = new(150f, -120f);
    private static Texture2D? _transferTexture;

    private sealed class ButtonState
    {
        public required Player Player { get; init; }
        public required Action OnGoldChanged { get; init; }
        public required TextureButton Button { get; init; }
        public required Control UsedOverlay { get; init; }
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
            RemoveTransferButton(__instance);
            MainFile.Logger.Info("[ShopTransfer] Open skipped: single-player run.");
            return;
        }

        TextureButton? button = FindTransferButton(__instance);
        Control? usedOverlay = button?.GetNodeOrNull<Control>(UsedOverlayName);
        if (button == null)
        {
            button = CreateButton();
            usedOverlay = CreateUsedOverlay();
            button.AddChild(usedOverlay);
            MainFile.Logger.Info("[ShopTransfer] Button created.");
        }

        EnsureButtonParent(__instance, button);

        if (!States.ContainsKey(__instance.GetInstanceId()))
        {
            Control overlay = usedOverlay ?? CreateUsedOverlay();
            if (usedOverlay == null)
            {
                button.AddChild(overlay);
            }

            Action onGoldChanged = () => UpdateButton(__instance, button, overlay, player);
            player.GoldChanged += onGoldChanged;

            States[__instance.GetInstanceId()] = new ButtonState
            {
                Player = player,
                OnGoldChanged = onGoldChanged,
                Button = button,
                UsedOverlay = overlay
            };

            button.Pressed += () => TaskHelper.RunSafely(OnTransferPressed(__instance, button));
            MainFile.Logger.Info("[ShopTransfer] Button handler registered.");
        }

        usedOverlay ??= button.GetNodeOrNull<Control>(UsedOverlayName);
        if (usedOverlay != null)
        {
            UpdateButton(__instance, button, usedOverlay, player);
            TaskHelper.RunSafely(UpdateButtonDeferred(__instance, button, usedOverlay, player));
        }

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

    private static TextureButton CreateButton()
    {
        Texture2D texture = LoadTransferTexture() ?? CreateFallbackTexture();
        TextureButton button = new()
        {
            Name = ButtonName,
            TextureNormal = texture,
            TextureDisabled = texture,
            IgnoreTextureSize = true,
            StretchMode = TextureButton.StretchModeEnum.KeepAspectCentered,
            CustomMinimumSize = TransferButtonSize,
            Size = TransferButtonSize,
            PivotOffset = TransferButtonSize * 0.5f,
            TooltipText = "Transfer Gold",
            Visible = true,
            FocusMode = Control.FocusModeEnum.All,
            MouseDefaultCursorShape = Control.CursorShape.PointingHand,
            ZIndex = 100
        };

        button.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        return button;
    }

    private static TextureButton? FindTransferButton(NMerchantInventory inventoryUi)
    {
        return inventoryUi.FindChild(ButtonName, recursive: true, owned: false) as TextureButton;
    }

    private static void RemoveTransferButton(NMerchantInventory inventoryUi)
    {
        TextureButton? button = FindTransferButton(inventoryUi);
        if (button == null)
        {
            return;
        }

        button.QueueFree();
    }

    private static void EnsureButtonParent(NMerchantInventory inventoryUi, TextureButton button)
    {
        Node desiredParent = inventoryUi;
        if (TryGetCardRemovalNode(inventoryUi, out Control? cardRemovalNode) && cardRemovalNode?.GetParent() != null)
        {
            desiredParent = cardRemovalNode.GetParent();
        }

        if (button.GetParent() == desiredParent)
        {
            return;
        }

        button.GetParent()?.RemoveChild(button);
        desiredParent.AddChild(button);
        desiredParent.MoveChild(button, desiredParent.GetChildCount() - 1);
        MainFile.Logger.Info($"[ShopTransfer] Button parent set to {desiredParent.GetPath()}.");
    }

    private static async Task UpdateButtonDeferred(NMerchantInventory inventoryUi, TextureButton button, Control usedOverlay, Player player)
    {
        SceneTree? tree = Engine.GetMainLoop() as SceneTree;
        if (tree != null)
        {
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await tree.ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }

        if (!GodotObject.IsInstanceValid(inventoryUi) || !GodotObject.IsInstanceValid(button) || !GodotObject.IsInstanceValid(usedOverlay))
        {
            return;
        }

        EnsureButtonParent(inventoryUi, button);
        UpdateButton(inventoryUi, button, usedOverlay, player);
    }

    private static Control CreateUsedOverlay()
    {
        Control overlay = new()
        {
            Name = UsedOverlayName,
            MouseFilter = Control.MouseFilterEnum.Ignore,
            Position = Vector2.Zero,
            Size = TransferButtonSize,
            Visible = false,
            ZIndex = 10
        };

        AddCrossBar(overlay, 45f);
        AddCrossBar(overlay, -45f);
        return overlay;
    }

    private static void AddCrossBar(Control overlay, float rotationDegrees)
    {
        Vector2 barSize = new(TransferButtonSize.X * 1.25f, 15f);
        ColorRect bar = new()
        {
            Color = new Color(0.93f, 0.16f, 0.12f, 0.9f),
            Size = barSize,
            Position = (TransferButtonSize - barSize) * 0.5f,
            PivotOffset = barSize * 0.5f,
            RotationDegrees = rotationDegrees,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };

        overlay.AddChild(bar);
    }

    private static async Task OnTransferPressed(NMerchantInventory inventoryUi, TextureButton button)
    {
        if (inventoryUi.Inventory == null)
        {
            MainFile.Logger.Warn("[ShopTransfer] Press ignored: inventory is null.");
            return;
        }

        Player player = inventoryUi.Inventory.Player;
        if (player.RunState.Players.Count <= 1)
        {
            button.Hide();
            MainFile.Logger.Info("[ShopTransfer] Press ignored: single-player run.");
            return;
        }

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
            TransferGoldState.MarkUsed(player.RunState);
            if (States.TryGetValue(inventoryUi.GetInstanceId(), out ButtonState? state))
            {
                UpdateButton(inventoryUi, state.Button, state.UsedOverlay, player);
            }
            MainFile.Logger.Info("[ShopTransfer] Transfer success.");
            return;
        }

        MainFile.Logger.Info("[ShopTransfer] Transfer canceled or failed.");
        if (States.TryGetValue(inventoryUi.GetInstanceId(), out ButtonState? currentState))
        {
            UpdateButton(inventoryUi, currentState.Button, currentState.UsedOverlay, player);
        }
    }

    private static void UpdateButton(NMerchantInventory inventoryUi, TextureButton button, Control usedOverlay, Player player)
    {
        PositionTransferButton(inventoryUi, button);

        bool used = TransferGoldState.IsUsed(player.RunState);
        int fixedAmount = ShopTransferSettings.GetFixedAmount();
        bool freeMode = ShopTransferSettings.IsFreeTransferEnabled();
        bool canAfford = freeMode ? player.Gold >= 1 : player.Gold >= fixedAmount;
        bool multiplayer = player.RunState.Players.Count > 1;
        if (!multiplayer)
        {
            button.Hide();
            return;
        }

        button.Show();
        button.Disabled = used || !canAfford || !multiplayer;
        button.Modulate = canAfford || used ? Colors.White : new Color(0.55f, 0.55f, 0.55f, 0.75f);
        usedOverlay.Visible = used;
        button.TooltipText = used
            ? "Transfer used"
            : (!multiplayer ? "Transfer Gold - multiplayer only" : (freeMode ? "Transfer Gold" : $"Transfer {fixedAmount}g"));

        MainFile.Logger.Info(
            $"[ShopTransfer] Button updated. Players={player.RunState.Players.Count} Gold={player.Gold} Used={used} Disabled={button.Disabled} GlobalPosition={button.GlobalPosition} TextureLoaded={button.TextureNormal != null}"
        );
    }

    private static void PositionTransferButton(NMerchantInventory inventoryUi, TextureButton button)
    {
        if (TryGetRemovalVisualGlobalPosition(inventoryUi, out Vector2 globalPosition))
        {
            button.GlobalPosition = globalPosition + RemovalVisualOffset - TransferButtonSize * 0.5f;
            return;
        }

        if (TryGetCardRemovalNode(inventoryUi, out Control? cardRemovalNode) && cardRemovalNode != null)
        {
            Vector2 removalTopRight = cardRemovalNode.GlobalPosition + new Vector2(cardRemovalNode.Size.X, 0f);
            button.GlobalPosition = removalTopRight + RemovalNodeOffset - TransferButtonSize * 0.5f;
            return;
        }

        button.Position = new Vector2(1220f, 700f);
    }

    private static bool TryGetRemovalVisualGlobalPosition(NMerchantInventory inventoryUi, out Vector2 globalPosition)
    {
        if (TryGetCardRemovalNode(inventoryUi, out Control? cardRemovalNode) && cardRemovalNode != null)
        {
            FieldInfo? visualField = cardRemovalNode.GetType().GetField("_removalVisual", BindingFlags.NonPublic | BindingFlags.Instance);
            if (visualField?.GetValue(cardRemovalNode) is Node2D visual)
            {
                globalPosition = visual.GlobalPosition;
                return true;
            }
        }

        globalPosition = default;
        return false;
    }

    private static bool TryGetCardRemovalNode(NMerchantInventory inventoryUi, out Control? cardRemovalNode)
    {
        FieldInfo? field = typeof(NMerchantInventory).GetField("_cardRemovalNode", BindingFlags.NonPublic | BindingFlags.Instance);
        cardRemovalNode = field?.GetValue(inventoryUi) as Control;
        return cardRemovalNode != null;
    }

    private static Texture2D? LoadTransferTexture()
    {
        if (_transferTexture != null)
        {
            return _transferTexture;
        }

        _transferTexture = ResourceLoader.Load<Texture2D>(TransferTexturePath);
        if (_transferTexture != null)
        {
            return _transferTexture;
        }

        MainFile.Logger.Warn($"[ShopTransfer] Transfer texture not found in PCK: {TransferTexturePath}");
        return null;
    }

    private static Texture2D CreateFallbackTexture()
    {
        Image image = Image.CreateEmpty((int)TransferButtonSize.X, (int)TransferButtonSize.Y, false, Image.Format.Rgba8);
        image.Fill(new Color(0.95f, 0.74f, 0.12f, 0.95f));

        int border = 5;
        Color borderColor = new(0.35f, 0.22f, 0.02f, 1f);
        for (int x = 0; x < image.GetWidth(); x++)
        {
            for (int y = 0; y < image.GetHeight(); y++)
            {
                if (x < border || y < border || x >= image.GetWidth() - border || y >= image.GetHeight() - border)
                {
                    image.SetPixel(x, y, borderColor);
                }
            }
        }

        MainFile.Logger.Warn("[ShopTransfer] Using fallback transfer button texture.");
        return ImageTexture.CreateFromImage(image);
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
