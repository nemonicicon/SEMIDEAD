using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using UnityEngine;

namespace SEMIDEAD;

/// <summary>
/// Broadcasts SemiFunc.UIBigMessage and UIFocusText to all clients
/// via PhotonNetwork.RaiseEvent — no PhotonView required.
/// Host calls Send*; the event fires on all clients including host.
/// </summary>
public static class Announcer
{
    // 0x5D = 93 — unlikely to collide with base game event codes.
    private const byte EventCode = 0x5D;

    private enum MsgType : byte { BigMessage = 0, FocusText = 1 }

    // Hashtable keys — short to keep payload small.
    private const string KType    = "t";
    private const string KMsg     = "m";
    private const string KEmoji   = "e";
    private const string KSize    = "s";
    private const string KR       = "r";
    private const string KG       = "g";
    private const string KB       = "b";
    private const string KFR      = "fr";
    private const string KFG      = "fg";
    private const string KFB      = "fb";
    private const string KTime    = "ti";

    private static bool _registered;

    // Call once from SEMIDEAD.Awake — safe to call before Photon connects.
    public static void Register()
    {
        if (_registered) return;
        PhotonNetwork.NetworkingClient.EventReceived += OnPhotonEvent;
        _registered = true;
    }

    // ---------------------------------------------------------------------------
    // Public API — host calls these
    // ---------------------------------------------------------------------------

    public static void SendBigMessage(string message, string emoji, float size,
                                      Color colorMain, Color colorFlash)
    {
        var data = new Hashtable
        {
            [KType]  = (byte)MsgType.BigMessage,
            [KMsg]   = message,
            [KEmoji] = emoji,
            [KSize]  = size,
            [KR]     = colorMain.r,  [KG]  = colorMain.g,  [KB]  = colorMain.b,
            [KFR]    = colorFlash.r, [KFG] = colorFlash.g, [KFB] = colorFlash.b,
        };
        Raise(data);
    }

    public static void SendFocusText(string message, Color colorMain, Color colorFlash,
                                     float time = 3f)
    {
        var data = new Hashtable
        {
            [KType]  = (byte)MsgType.FocusText,
            [KMsg]   = message,
            [KR]     = colorMain.r,  [KG]  = colorMain.g,  [KB]  = colorMain.b,
            [KFR]    = colorFlash.r, [KFG] = colorFlash.g, [KFB] = colorFlash.b,
            [KTime]  = time,
        };
        Raise(data);
    }

    // ---------------------------------------------------------------------------
    // Internals
    // ---------------------------------------------------------------------------

    private static void Raise(Hashtable data)
    {
        if (!SemiFunc.IsMultiplayer())
        {
            // Singleplayer — dispatch locally without Photon.
            Apply(data);
            return;
        }
        PhotonNetwork.RaiseEvent(
            EventCode,
            data,
            new RaiseEventOptions { Receivers = ReceiverGroup.All },
            SendOptions.SendReliable
        );
    }

    private static void OnPhotonEvent(EventData photonEvent)
    {
        if (photonEvent.Code != EventCode) return;
        if (photonEvent.CustomData is not Hashtable data) return;
        Apply(data);
    }

    private static void Apply(Hashtable data)
    {
        if (data[KMsg] is not string message) return;
        var type = (MsgType)(byte)data[KType];

        var colorMain  = new Color(F(data, KR),  F(data, KG),  F(data, KB));
        var colorFlash = new Color(F(data, KFR), F(data, KFG), F(data, KFB));

        switch (type)
        {
            case MsgType.BigMessage:
                string emoji = data[KEmoji] as string ?? "";
                float  size  = F(data, KSize);
                SemiFunc.UIBigMessage(message, emoji, size, colorMain, colorFlash);
                break;
            case MsgType.FocusText:
                float time = F(data, KTime);
                if (time <= 0f) time = 3f;
                SemiFunc.UIFocusText(message, colorMain, colorFlash, time);
                break;
        }
    }

    private static float F(Hashtable h, string key) =>
        h.ContainsKey(key) && h[key] is float v ? v : 0f;
}
