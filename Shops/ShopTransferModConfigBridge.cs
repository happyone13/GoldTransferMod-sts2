using System;
using System.Linq;
using System.Reflection;

namespace GoldTransferMod.Shops;

internal static class ShopTransferModConfigBridge
{
    private const string KeyEnableFixedTransfer = "enable_fixed_transfer";
    private const string KeyEnableFreeTransferLegacy = "enable_free_transfer";
    private const string KeyFixedTransferAmount = "fixed_transfer_amount";

    private static bool _registered;
    private static bool _available;

    public static bool IsAvailable => _available;

    public static void TryInitialize()
    {
        if (_registered)
        {
            return;
        }

        try
        {
            Type? apiType = FindType("ModConfig.ModConfigApi");
            Type? entryType = FindType("ModConfig.ConfigEntry");
            Type? configTypeType = FindType("ModConfig.ConfigType");

            if (apiType == null || entryType == null || configTypeType == null)
            {
                MainFile.Logger.Info("[ShopTransfer] ModConfig not detected, using local JSON config.");
                return;
            }

            object toggleType = Enum.Parse(configTypeType, "Toggle");
            object sliderType = Enum.Parse(configTypeType, "Slider");

            object toggleEntry = Activator.CreateInstance(entryType)!;
            SetProp(toggleEntry, "Key", KeyEnableFixedTransfer);
            SetProp(toggleEntry, "Type", toggleType);
            SetProp(toggleEntry, "Label", "Enable Fixed Transfer");
            SetProp(toggleEntry, "Description", "Use a fixed amount and once-per-shop transfer limit.");
            SetProp(toggleEntry, "DefaultValue", ShopTransferSettings.Current.EnableFixedTransfer);
            SetOnChanged(toggleEntry, value =>
            {
                if (TryConvert(value, out bool parsed))
                {
                    ShopTransferSettings.Current.EnableFixedTransfer = parsed;
                    ShopTransferSettings.Save();
                }
            });

            object sliderEntry = Activator.CreateInstance(entryType)!;
            SetProp(sliderEntry, "Key", KeyFixedTransferAmount);
            SetProp(sliderEntry, "Type", sliderType);
            SetProp(sliderEntry, "Label", "Fixed Transfer Amount");
            SetProp(sliderEntry, "Description", "Amount used when free transfer is disabled.");
            SetProp(sliderEntry, "DefaultValue", (float)ShopTransferSettings.GetFixedAmount());
            SetProp(sliderEntry, "Min", 1f);
            SetProp(sliderEntry, "Max", 999f);
            SetProp(sliderEntry, "Step", 1f);
            SetOnChanged(sliderEntry, value =>
            {
                if (TryConvert(value, out int parsed))
                {
                    ShopTransferSettings.Current.FixedTransferAmount = Math.Max(1, parsed);
                    ShopTransferSettings.Save();
                }
            });

            Array entries = Array.CreateInstance(entryType, 2);
            entries.SetValue(toggleEntry, 0);
            entries.SetValue(sliderEntry, 1);

            MethodInfo register = apiType.GetMethod(
                "Register",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(string), entries.GetType() },
                null
            ) ?? throw new MissingMethodException("ModConfig.ModConfigApi.Register(string,string,ConfigEntry[]) not found.");

            register.Invoke(null, new object[] { MainFile.ModId, "Gold Transfer Mod", entries });

            bool legacyFreeTransfer = GetValue(apiType, KeyEnableFreeTransferLegacy, true);
            bool fallbackEnableFixed = !legacyFreeTransfer;
            ShopTransferSettings.Current.EnableFixedTransfer =
                GetValue(apiType, KeyEnableFixedTransfer, fallbackEnableFixed);
            ShopTransferSettings.Current.FixedTransferAmount =
                Math.Max(1, GetValue(apiType, KeyFixedTransferAmount, ShopTransferSettings.GetFixedAmount()));
            ShopTransferSettings.Save();

            _registered = true;
            _available = true;
            MainFile.Logger.Info("[ShopTransfer] ModConfig binding enabled.");
        }
        catch (Exception ex)
        {
            _available = false;
            MainFile.Logger.Warn($"[ShopTransfer] ModConfig binding failed, fallback to local JSON. {ex.Message}");
        }
    }

    private static Type? FindType(string fullName)
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = asm.GetType(fullName, throwOnError: false, ignoreCase: false);
            if (type != null)
            {
                return type;
            }
        }

        return null;
    }

    private static void SetProp(object instance, string name, object value)
    {
        PropertyInfo prop = instance.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException(instance.GetType().FullName, name);
        prop.SetValue(instance, value);
    }

    private static void SetOnChanged(object entry, Action<object?> callback)
    {
        PropertyInfo prop = entry.GetType().GetProperty("OnChanged", BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException(entry.GetType().FullName, "OnChanged");
        prop.SetValue(entry, callback);
    }

    private static T GetValue<T>(Type apiType, string key, T fallback)
    {
        MethodInfo? genericGet = apiType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "GetValue" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2);
        if (genericGet == null)
        {
            return fallback;
        }

        MethodInfo get = genericGet.MakeGenericMethod(typeof(T));
        object? value = get.Invoke(null, new object[] { MainFile.ModId, key });
        if (value is T typed)
        {
            return typed;
        }

        return fallback;
    }

    private static bool TryConvert(object? value, out bool result)
    {
        if (value is bool b)
        {
            result = b;
            return true;
        }

        if (value is string s && bool.TryParse(s, out bool parsed))
        {
            result = parsed;
            return true;
        }

        result = false;
        return false;
    }

    private static bool TryConvert(object? value, out int result)
    {
        switch (value)
        {
            case int i:
                result = i;
                return true;
            case long l:
                result = (int)l;
                return true;
            case float f:
                result = (int)Math.Round(f);
                return true;
            case double d:
                result = (int)Math.Round(d);
                return true;
            case decimal dec:
                result = (int)Math.Round(dec);
                return true;
            case string s when int.TryParse(s, out int parsed):
                result = parsed;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}
