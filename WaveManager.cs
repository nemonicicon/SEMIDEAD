using System.Collections;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using Photon.Pun;
using UnityEngine;
using UnityEngine.Networking;

namespace SEMIDEAD;

public class WaveManager : MonoBehaviour
{
    public static WaveManager? Instance { get; private set; }

    private static ManualLogSource Logger => SEMIDEAD.Logger;

    private enum WaveState { GracePeriod, Intermission, WaveActive }

    private const float GraceDuration       = 15f;
    private const float IntermissionDuration = 30f;
    private const float WaveTimeoutDuration  = 150f;

    // Embedded resource name: assembly name + folder + filename (dots as separators).
    private const string JingleResourceName =
        "SEMIDEAD.soundsEXT.Zombies Ate My Neighbors OST - Konami Fanfare.mp3";

    private WaveState    _state            = WaveState.GracePeriod;
    private float        _timer            = GraceDuration;
    private int          _waveNumber       = 0;
    private float        _waveTimeoutTimer = 0f;
    private bool         _waveTimeoutFired = false;
    private AudioClip?   _jingle;
    private AudioSource? _audioSource;

    // ---------------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------------

    private void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        Logger.LogInfo("[WaveManager] Awake — instance initialised, DontDestroyOnLoad set.");
    }

    private void Start()
    {
        Logger.LogInfo("[WaveManager] Start — Update loop active.");

        _audioSource              = gameObject.AddComponent<AudioSource>();
        _audioSource.spatialBlend = 0f;  // 2D global
        _audioSource.volume       = 0.5f;
        _audioSource.playOnAwake  = false;

        // Host-only: load jingle from embedded resource for local playback only.
        if (SemiFunc.IsMasterClientOrSingleplayer())
            StartCoroutine(LoadJingleFromEmbeddedResource());
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public static WaveManager Create()
    {
        var go = new GameObject("SEMIDEAD_WaveManager");
        return go.AddComponent<WaveManager>();
    }

    // ---------------------------------------------------------------------------
    // Embedded resource loading — runs on every client at startup.
    // Writes to a temp file then loads via UnityWebRequest (MP3 requires this path).
    // ---------------------------------------------------------------------------

    private IEnumerator LoadJingleFromEmbeddedResource()
    {
        var asm    = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(JingleResourceName);
        if (stream == null)
        {
            Logger.LogWarning($"[WaveManager] Embedded resource not found: {JingleResourceName}");
            // Log available names to help diagnose name mismatches.
            foreach (var n in asm.GetManifestResourceNames())
                Logger.LogInfo($"[WaveManager]   available resource: {n}");
            yield break;
        }

        // Write to a temp file so UnityWebRequest can load it.
        byte[] bytes  = new byte[stream.Length];
        stream.Read(bytes, 0, bytes.Length);
        string tmpPath = Path.Combine(Path.GetTempPath(), "SEMIDEAD_jingle.mp3");
        File.WriteAllBytes(tmpPath, bytes);

        string uri = "file:///" + tmpPath.Replace("\\", "/");
        using var req = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.MPEG);
        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success)
        {
            _jingle = DownloadHandlerAudioClip.GetContent(req);
            Logger.LogInfo("[WaveManager] Jingle loaded from embedded resource.");
        }
        else
        {
            Logger.LogWarning($"[WaveManager] Jingle load failed: {req.error}");
        }
    }

    // ---------------------------------------------------------------------------
    // Photon event — host raises it; every client plays their local jingle clip.
    // ---------------------------------------------------------------------------

    private void PlayJingle()
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (_jingle == null || _audioSource == null) return;
        _audioSource.PlayOneShot(_jingle);
    }

    // ---------------------------------------------------------------------------
    // Level reset
    // ---------------------------------------------------------------------------

    public void ResetForNewLevel()
    {
        _state      = WaveState.GracePeriod;
        _timer      = GraceDuration;
        _waveNumber = 0;

        if (!IsGameplayLevel())
        {
            Logger.LogInfo($"[WaveManager] Skipping — '{RunManager.instance?.levelCurrent?.name}' is not a gameplay level.");
            return;
        }

        Logger.LogInfo($"[WaveManager] Level loaded — {GraceDuration}s grace period started.");
    }

    private static bool IsGameplayLevel()
    {
        var rm = RunManager.instance;
        if (rm == null || rm.levelCurrent == null || rm.levels == null) return false;
        return rm.levels.Contains(rm.levelCurrent);
    }

    // ---------------------------------------------------------------------------
    // Update loop
    // ---------------------------------------------------------------------------

    private void Update()
    {
        if (!IsGameplayLevel()) return;

        _timer -= Time.deltaTime;

        switch (_state)
        {
            case WaveState.GracePeriod:
            case WaveState.Intermission:
                if (_timer <= 0f) StartWave();
                break;

            case WaveState.WaveActive:
                if (!_waveTimeoutFired && SemiFunc.IsMasterClientOrSingleplayer())
                {
                    _waveTimeoutTimer += Time.deltaTime;
                    if (_waveTimeoutTimer >= WaveTimeoutDuration)
                    {
                        _waveTimeoutFired = true;
                        ForceCompleteWave();
                    }
                }
                break;
        }
    }

    private void ForceCompleteWave()
    {
        Logger.LogInfo($"[WaveManager] Wave {_waveNumber} timed out — rushing enemies at players.");
        WaveHUD.ShowPowerUpActivated("ENEMIES INCOMING", Color.red, 0f);
        WaveSpawner.Instance?.RushEnemiesAtPlayers();
        StartCoroutine(SilentKillAfterDelay(_waveNumber, 20f));
    }

    private IEnumerator SilentKillAfterDelay(int wave, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (_state != WaveState.WaveActive || _waveNumber != wave) yield break;
        Logger.LogInfo($"[WaveManager] Rush timeout expired — silently clearing wave {wave}.");
        WaveSpawner.Instance?.SilentKillAll();
    }

    private void StartWave()
    {
        _waveNumber++;
        _state = WaveState.WaveActive;
        _timer = 0f;

        _waveTimeoutTimer = 0f;
        _waveTimeoutFired = false;

        int monsterCount = GetMonsterCount(_waveNumber);
        Logger.LogInfo($"[WaveManager] ♪ Jingle ♪ — WAVE {_waveNumber} BEGINS ({monsterCount} monsters)");

        if (SemiFunc.IsMasterClientOrSingleplayer())
        {
            ReviveSystem.Instance?.OnWaveStart();
            AnnouncerSystem.Instance?.OnWaveStart(_waveNumber);
            if (_waveNumber == 1) CharacterSystem.Instance?.StartWaveOneSequence();
            WaveHUD.ShowWaveStart(_waveNumber, monsterCount);
            Announcer.SendBigMessage(
                $"WAVE {_waveNumber}",
                "",
                1f,
                Color.white,
                Color.yellow
            );
            PlayJingle();
            PlayAmbienceAnnouncementForClients();
            WaveSpawner.Instance?.SpawnWave(_waveNumber, monsterCount);
        }
    }

    /// <summary>
    /// Sends an AmbienceBreakers.PlaySoundRPC to all clients (not the host, who hears
    /// the jingle already). Uses a random level ambience preset so clients hear an
    /// atmospheric sound signal when each wave starts.
    /// AmbienceBreakers.instance is present in every gameplay level.
    /// </summary>
    private static void PlayAmbienceAnnouncementForClients()
    {
        var ab = AmbienceBreakers.instance;
        if (ab == null)
        {
            SEMIDEAD.Logger.LogWarning("[WaveManager] AmbienceBreakers.instance null — skipping client announcement.");
            return;
        }

        var audioMgr = AudioManager.instance;
        if (audioMgr?.levelAmbiences == null || audioMgr.levelAmbiences.Count == 0) return;

        // Collect presets that have at least one breaker sound.
        var valid = new System.Collections.Generic.List<LevelAmbience>();
        foreach (LevelAmbience la in audioMgr.levelAmbiences)
            if (la != null && la.breakers != null && la.breakers.Count > 0) valid.Add(la);
        if (valid.Count == 0) return;

        LevelAmbience preset = valid[Random.Range(0, valid.Count)];
        int breaker = Random.Range(0, preset.breakers.Count);

        // Originate the sound from the host player's position so it's always "in the level".
        Vector3 pos = Vector3.zero;
        var players = SemiFunc.PlayerGetList();
        if (players != null && players.Count > 0 && players[0] != null)
            pos = players[0].transform.position;

        var pv = ab.GetComponent<PhotonView>();
        if (SemiFunc.IsMultiplayer() && pv != null)
            // RpcTarget.Others: host already hears the jingle — avoid double-playing.
            pv.RPC("PlaySoundRPC", RpcTarget.Others, pos, preset.name, breaker);
        else
            ab.PlaySoundRPC(pos, preset.name, breaker);
    }

    public void OnWaveCleared()
    {
        Logger.LogInfo($"[WaveManager] ♪ Jingle ♪ — Wave {_waveNumber} cleared! Intermission: {IntermissionDuration}s");
        WaveHUD.ShowWaveCleared(_waveNumber, IntermissionDuration);
        Announcer.SendFocusText("WAVE COMPLETE", Color.green, Color.white, 3f);
        PlayJingle();

        // Max Ammo fires every time a wave ends so players are restocked for the next wave.
        if (SemiFunc.IsMasterClientOrSingleplayer())
            PowerUpManager.Instance?.ActivatePowerUp(PowerUpType.MaxAmmo);

        _state = WaveState.Intermission;
        _timer = IntermissionDuration;
    }

    private static int GetMonsterCount(int wave) => wave switch
    {
        1 => 6,
        2 => 10,
        3 => 14,
        _ => 14 + (wave - 4) * 2,
    };
}
