using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace SEMIDEAD;

public enum SpeechTrigger
{
    WaveOneStart,
    Kill,
    RayGunPickup,
    ShotgunPickup,
    SwordPickup,
    MonkeyBombPickup,
    MonkeyBombExplosion,
    TeddyBear,
    MaxAmmo,
    DoublePoints,
    Nuke,
    InstaKill,
    Revived,
    LowAmmo,
    OutOfAmmo,
}

/// <summary>
/// Assigns each player a random CoD Zombies character at level load and fires
/// character-appropriate TTS lines at key game events.
///
/// Trigger routing:
///   WaveOneStart     — StartWaveOneSequence(), one per character type in order, 1.5s apart
///   Kill             — OnEnemyKilled(pos), nearest living player, 10s cooldown
///   RayGunPickup     — OnItemGrabbed(), one-shot per player per level
///   ShotgunPickup    — OnItemGrabbed(), one-shot per player per level
///   SwordPickup      — OnItemGrabbed(), one-shot per player per level
///   MonkeyBombPickup — OnItemGrabbed(), one-shot per player per level
///   MonkeyBombExplosion — MonkeyBombActivator.Detonate(), nearest player, 5s cooldown
///   TeddyBear        — MysteryBox.TeleportBox(), one random player, 5s cooldown
///   MaxAmmo/etc.     — PowerUpManager.ActivatePowerUp(), triggering player or random
///   Revived          — ReviveSystem, the revived player
///   LowAmmo/OutOfAmmo — OnItemGrabbed() battery check, 60s/45s cooldown
///
/// TTS delivery: ChatMessageSendRPC(RpcTarget.All) on the player's PhotonView so
/// every client hears the line through that player's own Photon Voice stream.
/// </summary>
public class CharacterSystem : MonoBehaviour
{
    public static CharacterSystem? Instance { get; private set; }
    private static ManualLogSource Logger => SEMIDEAD.Logger;

    public enum Character { Richtofen, Nikolai, Dempsey, Takeo }

    // One-shot triggers: fire at most once per player per level.
    private static readonly HashSet<SpeechTrigger> OneShotTriggers = new()
    {
        SpeechTrigger.WaveOneStart,
        SpeechTrigger.RayGunPickup,
        SpeechTrigger.ShotgunPickup,
        SpeechTrigger.SwordPickup,
        SpeechTrigger.MonkeyBombPickup,
    };

    // Cooldown durations (seconds) for repeatable triggers.
    private static readonly Dictionary<SpeechTrigger, float> Cooldowns = new()
    {
        { SpeechTrigger.Kill,               10f },
        { SpeechTrigger.MonkeyBombExplosion,  5f },
        { SpeechTrigger.TeddyBear,            5f },
        { SpeechTrigger.MaxAmmo,              5f },
        { SpeechTrigger.DoublePoints,         5f },
        { SpeechTrigger.Nuke,                 5f },
        { SpeechTrigger.InstaKill,            5f },
        { SpeechTrigger.Revived,              3f },
        { SpeechTrigger.LowAmmo,             60f },
        { SpeechTrigger.OutOfAmmo,           45f },
    };

    // ---------------------------------------------------------------------------
    // Quote table — sourced from SEMIDEAD_speech.txt.
    // Knife references → sword. All four characters × all applicable triggers.
    // ---------------------------------------------------------------------------
    private static readonly Dictionary<Character, Dictionary<SpeechTrigger, string[]>> Quotes = new()
    {
        [Character.Dempsey] = new()
        {
            [SpeechTrigger.WaveOneStart] = new[]
            {
                "Let's get this done!",
                "Imagine that! We need to turn the power on, how original!",
            },
            [SpeechTrigger.Kill] = new[]
            {
                "Hi I'm Dempsey, and you are... DEAD!",
                "K.I.A., Maggot sack!",
                "Oorah! They're droppin' like bad habits!",
                "EAT IT, FREAK BAGS!",
                "Chew on that, maggot sacks!",
                "Hell yeah, that was sweet!",
                "You don't mess with the marine.",
            },
            [SpeechTrigger.RayGunPickup] = new[]
            {
                "Looks like Santa got my letter.",
                "Where's this thing keep 160 rounds?",
            },
            [SpeechTrigger.ShotgunPickup] = new[]
            {
                "Blowin' off limbs is one of my specialties!",
                "Nothing says I love you quite like a shotgun.",
            },
            [SpeechTrigger.SwordPickup] = new[]
            {
                "Can't touch this!",
                "Slice and Dice baby!",
                "Time to gut these brainbags!",
                "Oh yeah, now it's gonna get personal!",
            },
            [SpeechTrigger.MonkeyBombPickup] = new[]
            {
                "Aww... isn't that so fucking cute?",
                "Hey look, it's Richtofen's brother.",
                "Monkey see, monkey go blow some shit up.",
                "What are the Krauts gonna think of next?!",
            },
            [SpeechTrigger.MonkeyBombExplosion] = new[]
            {
                "I'm gonna miss that little guy.",
                "Oorah, Monkey bones!",
                "See that little guy light em up!",
            },
            [SpeechTrigger.TeddyBear] = new[]
            {
                "You won't be giggling when I get through with you!",
                "You laughing at me? Not wise.",
                "I never win anything, except for WAR!",
            },
            [SpeechTrigger.MaxAmmo] = new[]
            {
                "JUUUIIICE!",
                "Much appreciated!",
                "Oh yeah! Bring it, motherfuckers!",
            },
            [SpeechTrigger.DoublePoints] = new[]
            {
                "Line em up!",
                "Rack em up!",
            },
            [SpeechTrigger.Nuke] = new[]
            {
                "OH YEAH!",
                "See ya, maggot bags!",
                "That just never gets old.",
            },
            [SpeechTrigger.InstaKill] = new[]
            {
                "Dempsey's butcher shop is open for business!",
            },
            [SpeechTrigger.Revived] = new[]
            {
                "Couldn't live without me, huh?",
                "I must have slipped, oorah.",
            },
            [SpeechTrigger.LowAmmo] = new[]
            {
                "Gun's gettin' hungry, time to feed.",
                "Gun juice is runnin' low.",
                "I'm gonna need to get some ammo soon.",
                "Shit. Running low on ammo!",
            },
            [SpeechTrigger.OutOfAmmo] = new[]
            {
                "I can't do my job if I ain't packin' heat!",
                "SHIT! Outta gun juice",
                "FUCK! Can't blow shit up without ammo!",
            },
        },

        [Character.Nikolai] = new()
        {
            [SpeechTrigger.WaveOneStart] = new[]
            {
                "I need a fucking perk. Let's go find the power.",
                "No power! Just like home!",
                "What the hell are you doing here you creepy fucks?",
            },
            [SpeechTrigger.Kill] = new[]
            {
                "Even drunk, I'm too strong for you.",
                "FUCK YOU!",
                "You fall like drunkards on way home.",
                "This is a happy massacre!",
                "You hurt me, I KILL you!",
                "Only man and wife should be so close!",
            },
            [SpeechTrigger.RayGunPickup] = new[]
            {
                "This will hurt like syphilis. Believe me, I know.",
                "Even I can hit something with this!",
            },
            [SpeechTrigger.ShotgunPickup] = new[]
            {
                "Ah, the same weapon I used to kill my third wife! She was bitch!",
                "Death is inevitable, like hangovers, I know.",
            },
            [SpeechTrigger.SwordPickup] = new[]
            {
                "Cold, hard, Russian steel!",
                "That's a big fucking knife!",
            },
            [SpeechTrigger.MonkeyBombPickup] = new[]
            {
                "Monkey knows Nikolai can't survive without him.",
                "Monkey is image of Nikolai's wife! But prettier.",
                "Use it wisely Tank. Use it wisely.",
            },
            [SpeechTrigger.MonkeyBombExplosion] = new[]
            {
                "Good, that fucking song was driving me nuts!",
                "Ha Ha, you just got fucked by a monkey!!",
            },
            [SpeechTrigger.TeddyBear] = new[]
            {
                "In Russia, I kill bears TEN TIMES your size!",
                "Ha ha, you make funny joke...",
                "Did someone make joke?",
            },
            [SpeechTrigger.MaxAmmo] = new[]
            {
                "Soviet supply routes, they're not this good.",
                "The killing can continue!",
            },
            [SpeechTrigger.DoublePoints] = new[]
            {
                "I'll drink to that! Hell, I'll drink to anything!",
                "Maybe I will share these points with State!",
            },
            [SpeechTrigger.Nuke] = new[]
            {
                "So much death, so quickly, like Eastern Front all over again!",
                "This is a happy massacre!",
            },
            [SpeechTrigger.InstaKill] = new[]
            {
                "Your touch is cold, mine is deadly!",
            },
            [SpeechTrigger.Revived] = new[]
            {
                "Perhaps you are of use.",
                "My ancestors will have to wait for my arrival!",
            },
            [SpeechTrigger.LowAmmo] = new[]
            {
                "Someone is not sharing their ammo...",
                "Don't make me fight with broken vodka bottle!",
                "I have vodka, BUT NO FUCKING AMMO!",
                "Soviet war machine grind to halt without ammo!",
            },
            [SpeechTrigger.OutOfAmmo] = new[]
            {
                "I feel empty... SHIT! I AM EMPTY!",
                "Where is ammo? Nikolai is in deep shit!",
                "I'm out of ammo! I'm going for a drink.",
            },
        },

        [Character.Takeo] = new()
        {
            [SpeechTrigger.WaveOneStart] = new[]
            {
                "Sega did what Nintendidn't.",
                "rofl rofl rofl rofl rofl rofl rofl",
                "John Myung is basically a genius.",
                "Yuzo Koshiro is my spirit animal.",
                "Where are we? Fukushima?",
                "Neko Neko!",
                "Fall seven times, stand up eight.",
                "Three years on top of a stone.",
                "Even monkeys fall from trees.",
                "One time, one encounter.",
                "Dumplings rather than flowers.",
                "A little knowledge is a dangerous thing.",
                "The nail that sticks out gets hammered down.",
                "Gold coins to a cat.",
                "A frog in a well does not know the vast ocean.",
                "It cannot be helped.",
            },
            [SpeechTrigger.Kill] = new[]
            {
                "I have drawn a line in the sand!",
                "Face me at your peril.",
                "Die, like the animal you are!",
                "All must fall!",
                "You bring about your own destruction!",
                "Now you sleep for eternity!",
                "Kneel before me!",
            },
            [SpeechTrigger.RayGunPickup] = new[]
            {
                "This is... interesting...",
                "An unnatural death, for an unnatural beast!",
            },
            [SpeechTrigger.ShotgunPickup] = new[]
            {
                "Simply bad-ass, just like me.",
                "See the error of your ways.",
            },
            [SpeechTrigger.SwordPickup] = new[]
            {
                "Shi-ne!",
                "Accept your death!",
                "A just kill!",
                "Advance and fall!",
            },
            [SpeechTrigger.MonkeyBombPickup] = new[]
            {
                "They deserved their fate...",
                "Monkey power!",
                "Ah, the little warrior.",
                "Even a monkey is smarter than Nikolai.",
            },
            [SpeechTrigger.MonkeyBombExplosion] = new[]
            {
                "To kill tree, you must destroy forest.",
                "Pain, in large numbers.",
                "I prefer them in smaller pieces.",
            },
            [SpeechTrigger.TeddyBear] = new[]
            {
                "Do not mock me, Bear.",
                "Don't drop limb, drop power up...",
            },
            [SpeechTrigger.MaxAmmo] = new[]
            {
                "The killing can continue!",
                "My ammunition supply is restored!",
            },
            [SpeechTrigger.DoublePoints] = new[]
            {
                "We have a chance to grow rich!",
            },
            [SpeechTrigger.Nuke] = new[]
            {
                "I scatter your remains across the land!",
                "Your limbs travel high into the air!",
                "I blow you away like a mighty wind!",
            },
            [SpeechTrigger.InstaKill] = new[]
            {
                "Accept your penalty!",
                "Your miserable existence is over!",
            },
            [SpeechTrigger.Revived] = new[]
            {
                "My ancestors will have to wait for my arrival!",
                "I will not give up!",
            },
            [SpeechTrigger.LowAmmo] = new[]
            {
                "My ammunition supply runs low!",
                "No bullets remain!",
                "I need ammunitions, quickly!",
            },
            [SpeechTrigger.OutOfAmmo] = new[]
            {
                "My weapons are like Dempsey's head. Empty.",
                "Without ammo, I cannot achieve victory.",
                "The emptiness of my weapons displeases me.",
            },
        },

        [Character.Richtofen] = new()
        {
            [SpeechTrigger.WaveOneStart] = new[]
            {
                "There is probably one big switch somewhere that powers everything. We Germans are very efficient.",
                "Power power power power! It's always the first priority!",
                "I think this is called the Mainframe. It must need power.",
            },
            [SpeechTrigger.Kill] = new[]
            {
                "His head EXPLODED. SUCH JOY!",
                "Do you know WHO I AM?!?!",
                "Rest in peace, my little undead friend.",
                "The Doctor prescribes you... PAIN!!",
                "Who's your daddy?",
                "KNEEL!",
            },
            [SpeechTrigger.RayGunPickup] = new[]
            {
                "Heeheeheee they cannot run from me now!!",
                "Do you see it? What lights!",
            },
            [SpeechTrigger.ShotgunPickup] = new[]
            {
                "So much lead, for the dead! Ahah! I'm a poet, and I didn't know it, Ha!",
                "To kill quickly, ja! With skill? No.",
            },
            [SpeechTrigger.SwordPickup] = new[]
            {
                "Blood, gore, sinew, bones, organs, pulmonary systems!",
                "Doctor recommends AMPUTATION!",
                "KNEEL!",
            },
            [SpeechTrigger.MonkeyBombPickup] = new[]
            {
                "Let Richtofen's carnival of horrors begin!",
                "Ooh, a cute little monkey bomb for the doctor",
                "Play the cymbals of death, my little monkey friend.",
            },
            [SpeechTrigger.MonkeyBombExplosion] = new[]
            {
                "Let us all have a sing-along for the corpses",
                "Oh, my! It kills people! Brilliant!",
                "No more monkey sing-along for the cadavers.",
            },
            [SpeechTrigger.TeddyBear] = new[]
            {
                "Maybe you move it because... you like me?",
                "Under what reich is that fair?",
            },
            [SpeechTrigger.MaxAmmo] = new[]
            {
                "No shortage on this front! Hahaha!",
            },
            [SpeechTrigger.DoublePoints] = new[]
            {
                "I am AROUSED!!",
                "Leave some for me!!",
            },
            [SpeechTrigger.Nuke] = new[]
            {
                "We're going to need more body bags.",
                "Look at all the body parts. Someone get me a bag!",
            },
            [SpeechTrigger.InstaKill] = new[]
            {
                "With a single strike, you DIE!",
            },
            [SpeechTrigger.Revived] = new[]
            {
                "Dunker, so good.",
                "I am not afraid, but I could use some help.",
            },
            [SpeechTrigger.LowAmmo] = new[]
            {
                "I am useless!",
                "I need bullets to further my work!",
                "AMMO FOR THE DOCTOR!",
                "No ammo....? NOOOOOO!",
            },
            [SpeechTrigger.OutOfAmmo] = new[]
            {
                "There is... NOTHING left!",
                "Scheisse, mein veapon, Scheisse!!",
                "Without bullets there can be no bullet wounds!",
            },
        },
    };

    // ---------------------------------------------------------------------------
    // State
    // ---------------------------------------------------------------------------

    // PlayerAvatar playerName → assigned character (keyed by name, not ViewID, which is 0 at level-load time)
    private readonly Dictionary<string, Character> _assignments = new();
    // One-shot guard: (playerName, trigger) pairs already used this level
    private readonly HashSet<(string, SpeechTrigger)> _oneShotUsed = new();
    // Cooldown: (playerName, trigger) → earliest Time.time the line can fire again
    private readonly Dictionary<(string, SpeechTrigger), float> _cooldownUntil = new();

    // ---------------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------------

    private void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public static CharacterSystem Create()
    {
        var go = new GameObject("SEMIDEAD_CharacterSystem");
        return go.AddComponent<CharacterSystem>();
    }

    // ---------------------------------------------------------------------------
    // Level setup
    // ---------------------------------------------------------------------------

    public void OnLevelSetup()
    {
        _oneShotUsed.Clear();
        _cooldownUntil.Clear();
        AssignCharacters();
    }

    private void AssignCharacters()
    {
        _assignments.Clear();
        var players = SemiFunc.PlayerGetList();
        if (players == null) return;

        var pool = new List<Character> { Character.Richtofen, Character.Nikolai, Character.Dempsey, Character.Takeo };

        // Fisher-Yates shuffle.
        for (int i = pool.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        for (int i = 0; i < players.Count; i++)
        {
            var player = players[i];
            if (player == null) continue;
            Character c = pool[i % pool.Count];
            _assignments[player.playerName] = c;
            Logger.LogInfo($"[CharacterSystem] {player.playerName} → {c}");
        }
    }

    // ---------------------------------------------------------------------------
    // Wave 1 start — Richtofen → Nikolai → Dempsey → Takeo, 1.5s between each.
    // Only one player per character type speaks.
    // ---------------------------------------------------------------------------

    public void StartWaveOneSequence()
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        StartCoroutine(WaveOneSequenceCoroutine());
    }

    private IEnumerator WaveOneSequenceCoroutine()
    {
        // Wait for the "WAVE 1" TTS announcement to finish before character lines fire.
        yield return new WaitForSeconds(2.5f);

        var order = new[] { Character.Richtofen, Character.Nikolai, Character.Dempsey, Character.Takeo };
        foreach (Character c in order)
        {
            PlayerAvatar? speaker = FindFirstPlayerOfCharacter(c);
            if (speaker != null)
            {
                TriggerSpeechDirect(speaker, SpeechTrigger.WaveOneStart);
                yield return new WaitForSeconds(1.5f);
            }
        }
    }

    private PlayerAvatar? FindFirstPlayerOfCharacter(Character c)
    {
        var players = SemiFunc.PlayerGetList();
        if (players == null) return null;
        foreach (var p in players)
        {
            if (p == null || p.isDisabled) continue;
            if (_assignments.TryGetValue(p.playerName, out Character assigned) && assigned == c)
                return p;
        }
        return null;
    }

    // ---------------------------------------------------------------------------
    // Core trigger methods
    // ---------------------------------------------------------------------------

    /// <summary>Trigger a speech event for a specific player, respecting cooldowns.</summary>
    public void TriggerSpeech(PlayerAvatar player, SpeechTrigger trigger)
    {
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;
        if (player == null) return;

        string name = player.playerName;
        if (!_assignments.TryGetValue(name, out Character c)) return;

        if (OneShotTriggers.Contains(trigger))
        {
            if (_oneShotUsed.Contains((name, trigger))) return;
            _oneShotUsed.Add((name, trigger));
        }
        else
        {
            var key = (name, trigger);
            if (_cooldownUntil.TryGetValue(key, out float until) && Time.time < until) return;
            if (Cooldowns.TryGetValue(trigger, out float cd))
                _cooldownUntil[key] = Time.time + cd;
        }

        if (!Quotes.TryGetValue(c, out var triggerMap)) return;
        if (!triggerMap.TryGetValue(trigger, out var lines) || lines.Length == 0) return;

        string line = lines[Random.Range(0, lines.Length)];
        Logger.LogInfo($"[CharacterSystem] {player.playerName} ({c}) [{trigger}]: \"{line}\"");
        SendTTS(player, line);
    }

    /// <summary>Internal — bypasses guards. Used by wave 1 sequence which manages its own flow.</summary>
    private void TriggerSpeechDirect(PlayerAvatar player, SpeechTrigger trigger)
    {
        if (player == null) return;
        if (!_assignments.TryGetValue(player.playerName, out Character c)) return;
        if (!Quotes.TryGetValue(c, out var triggerMap)) return;
        if (!triggerMap.TryGetValue(trigger, out var lines) || lines.Length == 0) return;

        string line = lines[Random.Range(0, lines.Length)];
        Logger.LogInfo($"[CharacterSystem] {player.playerName} ({c}) [{trigger}]: \"{line}\"");
        SendTTS(player, line);
    }

    /// <summary>Trigger speech for the nearest living player to a world position.</summary>
    public void TriggerSpeechNearestTo(Vector3 position, SpeechTrigger trigger)
    {
        PlayerAvatar? nearest = GetNearestLivingPlayer(position);
        if (nearest != null) TriggerSpeech(nearest, trigger);
    }

    /// <summary>Trigger speech for one random living player (e.g. power-up with no attribution).</summary>
    public void TriggerSpeechForOneRandom(SpeechTrigger trigger)
    {
        var players = SemiFunc.PlayerGetList();
        if (players == null || players.Count == 0) return;

        var living = new List<PlayerAvatar>();
        foreach (var p in players)
            if (p != null && !p.isDisabled) living.Add(p);
        if (living.Count == 0) return;

        TriggerSpeech(living[Random.Range(0, living.Count)], trigger);
    }

    /// <summary>Called from EnemyHealthPatch when any enemy dies.</summary>
    public void OnEnemyKilled(Vector3 position) =>
        TriggerSpeechNearestTo(position, SpeechTrigger.Kill);

    // ---------------------------------------------------------------------------
    // Item grab — called from GrabPlayerAddRPC patch (multiplayer) and
    // GrabStarted patch (singleplayer).
    // ---------------------------------------------------------------------------

    internal static void OnItemGrabbed(PhysGrabObject item, PhysGrabber grabber)
    {
        if (Instance == null) return;
        if (!SemiFunc.IsMasterClientOrSingleplayer()) return;

        var avatar = grabber?.playerAvatar;
        if (avatar == null) return;

        var attrs = item.GetComponent<ItemAttributes>();
        string name = attrs?.instanceName ?? string.Empty;
        Logger.LogInfo($"[CharacterSystem] OnItemGrabbed: player={avatar.playerName}, item={item.name}, instanceName=\"{name}\"");
        if (attrs == null) return;

        bool triggeredSpecific = false;

        if (name.StartsWith(StartingPistol.ShockwaveName))
        {
            Instance.TriggerSpeech(avatar, SpeechTrigger.RayGunPickup);
            triggeredSpecific = true;
        }
        else if (name.StartsWith(StartingPistol.ShotgunName))
        {
            Instance.TriggerSpeech(avatar, SpeechTrigger.ShotgunPickup);
            triggeredSpecific = true;
        }
        else if (name.StartsWith(StartingPistol.SwordName))
        {
            Instance.TriggerSpeech(avatar, SpeechTrigger.SwordPickup);
            triggeredSpecific = true;
        }
        else if (name == "MonkeyBomb")
        {
            Instance.TriggerSpeech(avatar, SpeechTrigger.MonkeyBombPickup);
            triggeredSpecific = true;
        }

        // Ammo check for any other gun with low or empty battery at pickup.
        if (!triggeredSpecific)
        {
            var battery = item.GetComponentInChildren<ItemBattery>();
            if (battery != null)
            {
                if (battery.batteryLife <= 0f)
                    Instance.TriggerSpeech(avatar, SpeechTrigger.OutOfAmmo);
                else if (battery.batteryLife <= 0.25f)
                    Instance.TriggerSpeech(avatar, SpeechTrigger.LowAmmo);
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static PlayerAvatar? GetNearestLivingPlayer(Vector3 pos)
    {
        var players = SemiFunc.PlayerGetList();
        if (players == null) return null;

        PlayerAvatar? nearest  = null;
        float         bestDist = float.MaxValue;
        foreach (var p in players)
        {
            if (p == null || p.isDisabled) continue;
            float d = (p.transform.position - pos).sqrMagnitude;
            if (d < bestDist) { bestDist = d; nearest = p; }
        }
        return nearest;
    }

    private static void SendTTS(PlayerAvatar player, string text)
    {
        if (SemiFunc.IsMultiplayer())
        {
            var pv = player.GetComponent<PhotonView>();
            if (pv != null)
                pv.RPC("ChatMessageSendRPC", RpcTarget.All, new object[] { text, false });
        }
        else
        {
            player.voiceChat?.ttsVoice?.TTSSpeakNow(text, false);
        }
    }
}

// ---------------------------------------------------------------------------
// Multiplayer: GrabPlayerAddRPC fires on all clients after a successful grab.
// Mod is host-only so this postfix effectively runs on host only.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(PhysGrabObject), "GrabPlayerAddRPC")]
static class PhysGrabObjectGrabAddPatch
{
    [HarmonyPostfix]
    private static void Postfix(PhysGrabObject __instance, int photonViewID)
    {
        if (!SemiFunc.IsMultiplayer()) return;
        var grabber = PhotonView.Find(photonViewID)?.GetComponent<PhysGrabber>();
        if (grabber != null)
            CharacterSystem.OnItemGrabbed(__instance, grabber);
    }
}

// ---------------------------------------------------------------------------
// Singleplayer: GrabPlayerAddRPC never fires; use GrabStarted directly.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(PhysGrabObject), "GrabStarted")]
static class PhysGrabObjectGrabStartedPatch
{
    [HarmonyPostfix]
    private static void Postfix(PhysGrabObject __instance, PhysGrabber player)
    {
        if (SemiFunc.IsMultiplayer()) return;
        CharacterSystem.OnItemGrabbed(__instance, player);
    }
}
