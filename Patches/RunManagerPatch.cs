using HarmonyLib;
using UnityEngine;

namespace SEMIDEAD.Patches;

/// <summary>
/// Hooks R.E.P.O.'s RunManager to reset the wave cycle whenever a new level loads.
/// Uses lazy initialisation — recreates singletons if Unity destroyed them during
/// a scene transition before the patch fires.
/// </summary>
[HarmonyPatch(typeof(RunManager))]
static class RunManagerPatch
{
    [HarmonyPostfix, HarmonyPatch("ChangeLevel")]
    private static void ChangeLevel_Postfix()
    {
        SEMIDEAD.Logger.LogInfo("[RunManagerPatch] ChangeLevel_Postfix fired.");

        // Register Photon event listener on all clients — safe here since Photon is
        // connected by the time ChangeLevel fires (unlike Awake, which is pre-Photon).
        ShotgunExplosionPatch.RegisterListener();
        PowerUpManager.RegisterOrbListener();

        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

        // Only initialise wave systems for actual gameplay levels.
        // During the main menu load Photon is mid-connection and IsMasterClientOrSingleplayer()
        // returns true (not yet connected = treated as singleplayer). Creating singletons and
        // starting coroutines at that point disrupts Photon's region-selection flow.
        var rm = RunManager.instance;
        if (rm == null || rm.levelCurrent == null || rm.levels == null
            || !rm.levels.Contains(rm.levelCurrent))
        {
            SEMIDEAD.Logger.LogInfo("[RunManagerPatch] Non-gameplay level — skipping init.");
            return;
        }

        Announcer.Register();
        EnsureWaveHUD();
        EnsureWaveManager().ResetForNewLevel();
        EnsureWaveSpawner();
        EnsurePowerUpManager();
        EnsureStartingPistol().OnLevelSetup();
        EnsureMysteryBox().OnLevelSetup();
        EnsureMonkeyBomb().OnLevelSetup();
        EnsureWallBuy().OnLevelSetup();
        EnsureReviveSystem().OnLevelSetup();
        EnsureCharacterSystem().OnLevelSetup();
        EnsureAnnouncerSystem().OnLevelSetup();
    }

    private static void EnsureWaveHUD()
    {
        if (WaveHUD.Instance == null)
        {
            SEMIDEAD.Logger.LogInfo("[RunManagerPatch] WaveHUD null — recreating.");
            var go = new GameObject("SEMIDEAD_WaveHUD");
            go.AddComponent<WaveHUD>();
            GameObject.DontDestroyOnLoad(go);
        }
    }

    private static WaveManager EnsureWaveManager()
    {
        if (WaveManager.Instance == null)
        {
            SEMIDEAD.Logger.LogInfo("[RunManagerPatch] WaveManager null — recreating.");
            var go = new GameObject("SEMIDEAD_WaveManager");
            go.AddComponent<WaveManager>();
            GameObject.DontDestroyOnLoad(go);
        }
        return WaveManager.Instance!;
    }

    private static WaveSpawner EnsureWaveSpawner()
    {
        if (WaveSpawner.Instance == null)
        {
            SEMIDEAD.Logger.LogInfo("[RunManagerPatch] WaveSpawner null — recreating.");
            var go = new GameObject("SEMIDEAD_WaveSpawner");
            go.AddComponent<WaveSpawner>();
            GameObject.DontDestroyOnLoad(go);
        }
        return WaveSpawner.Instance!;
    }

    private static StartingPistol EnsureStartingPistol()
    {
        if (StartingPistol.Instance == null)
        {
            SEMIDEAD.Logger.LogInfo("[RunManagerPatch] StartingPistol null — recreating.");
            var go = new GameObject("SEMIDEAD_StartingPistol");
            go.AddComponent<StartingPistol>();
            GameObject.DontDestroyOnLoad(go);
        }
        return StartingPistol.Instance!;
    }

    private static MysteryBox EnsureMysteryBox()
    {
        if (MysteryBox.Instance == null)
        {
            SEMIDEAD.Logger.LogInfo("[RunManagerPatch] MysteryBox null — recreating.");
            var go = new GameObject("SEMIDEAD_MysteryBox");
            go.AddComponent<MysteryBox>();
            GameObject.DontDestroyOnLoad(go);
        }
        return MysteryBox.Instance!;
    }

    private static MonkeyBomb EnsureMonkeyBomb()
    {
        if (MonkeyBomb.Instance == null)
        {
            SEMIDEAD.Logger.LogInfo("[RunManagerPatch] MonkeyBomb null — recreating.");
            var go = new GameObject("SEMIDEAD_MonkeyBomb");
            go.AddComponent<MonkeyBomb>();
            GameObject.DontDestroyOnLoad(go);
        }
        return MonkeyBomb.Instance!;
    }

    private static PowerUpManager EnsurePowerUpManager()
    {
        if (PowerUpManager.Instance == null)
        {
            SEMIDEAD.Logger.LogInfo("[RunManagerPatch] PowerUpManager null — recreating.");
            var go = new GameObject("SEMIDEAD_PowerUpManager");
            go.AddComponent<PowerUpManager>();
            GameObject.DontDestroyOnLoad(go);
        }
        return PowerUpManager.Instance!;
    }

    private static WallBuy EnsureWallBuy()
    {
        if (WallBuy.Instance == null)
        {
            SEMIDEAD.Logger.LogInfo("[RunManagerPatch] WallBuy null — recreating.");
            var go = new GameObject("SEMIDEAD_WallBuy");
            go.AddComponent<WallBuy>();
            GameObject.DontDestroyOnLoad(go);
        }
        return WallBuy.Instance!;
    }

    private static CharacterSystem EnsureCharacterSystem()
    {
        if (CharacterSystem.Instance == null)
        {
            SEMIDEAD.Logger.LogInfo("[RunManagerPatch] CharacterSystem null — recreating.");
            var go = new GameObject("SEMIDEAD_CharacterSystem");
            go.AddComponent<CharacterSystem>();
            GameObject.DontDestroyOnLoad(go);
        }
        return CharacterSystem.Instance!;
    }

    private static AnnouncerSystem EnsureAnnouncerSystem()
    {
        if (AnnouncerSystem.Instance == null)
        {
            SEMIDEAD.Logger.LogInfo("[RunManagerPatch] AnnouncerSystem null — recreating.");
            var go = new GameObject("SEMIDEAD_AnnouncerSystem");
            go.AddComponent<AnnouncerSystem>();
            GameObject.DontDestroyOnLoad(go);
        }
        return AnnouncerSystem.Instance!;
    }

    private static ReviveSystem EnsureReviveSystem()
    {
        if (ReviveSystem.Instance == null)
        {
            SEMIDEAD.Logger.LogInfo("[RunManagerPatch] ReviveSystem null — recreating.");
            var go = new GameObject("SEMIDEAD_ReviveSystem");
            go.AddComponent<ReviveSystem>();
            GameObject.DontDestroyOnLoad(go);
        }
        return ReviveSystem.Instance!;
    }
}
