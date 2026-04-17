using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using SEMIDEAD.Patches;
using UnityEngine;

namespace SEMIDEAD;

[BepInPlugin("Jacob.SEMIDEAD", "SEMIDEAD", "1.0")]
public class SEMIDEAD : BaseUnityPlugin
{
    internal static SEMIDEAD Instance { get; private set; } = null!;
    internal new static ManualLogSource Logger => Instance._logger;
    private ManualLogSource _logger => base.Logger;
    internal Harmony? Harmony { get; set; }

    private void Awake()
    {
        Instance = this;
        Patch();
        Logger.LogInfo($"{Info.Metadata.GUID} v{Info.Metadata.Version} has loaded!");
    }

    internal void Patch()
    {
        Harmony ??= new Harmony(Info.Metadata.GUID);
        Harmony.PatchAll();
    }

    internal void Unpatch()
    {
        Harmony?.UnpatchSelf();
    }


}