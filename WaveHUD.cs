using System.Collections;
using BepInEx.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SEMIDEAD;

/// <summary>
/// Host-only screen overlay for wave and power-up announcements.
/// Built from vanilla UGUI — no client mod required.
/// All methods are safe to call from any thread; rendering is host-side only.
///
/// Layout (anchored to screen centre-top area):
///   Row 1 — wave banner  e.g. "WAVE 3"  or  "INTERMISSION — 12s"
///   Row 2 — power-up     e.g. "★ INSTA KILL ★  (28s)"
/// </summary>
public class WaveHUD : MonoBehaviour
{
    public static WaveHUD? Instance { get; private set; }
    private static ManualLogSource Logger => SEMIDEAD.Logger;

    private TextMeshProUGUI? _waveLine;
    private TextMeshProUGUI? _powerUpLine;
    private TextMeshProUGUI? _promptLine;

    private Coroutine? _waveCoroutine;
    private Coroutine? _powerUpCoroutine;

    // ---------------------------------------------------------------------------
    // Lifecycle
    // ---------------------------------------------------------------------------

    private void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        BuildCanvas();
        Logger.LogInfo("[WaveHUD] Initialised.");
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    public static WaveHUD Create()
    {
        var go = new GameObject("SEMIDEAD_WaveHUD");
        return go.AddComponent<WaveHUD>();
    }

    // ---------------------------------------------------------------------------
    // Canvas construction
    // ---------------------------------------------------------------------------

    private void BuildCanvas()
    {
        var canvasGo = new GameObject("SEMIDEAD_HUDCanvas");
        DontDestroyOnLoad(canvasGo);

        var canvas         = canvasGo.AddComponent<Canvas>();
        canvas.renderMode  = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;

        var scaler              = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode      = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);

        // --- wave line (upper centre) ---
        _waveLine = MakeLabel(canvasGo, "WaveLine",
            anchorMin: new Vector2(0.2f, 0.84f),
            anchorMax: new Vector2(0.8f, 0.93f),
            fontSize: 52f,
            color: Color.white);

        // --- power-up line (just below wave line) ---
        _powerUpLine = MakeLabel(canvasGo, "PowerUpLine",
            anchorMin: new Vector2(0.2f, 0.76f),
            anchorMax: new Vector2(0.8f, 0.85f),
            fontSize: 36f,
            color: Color.yellow);

        // --- buy prompt line (below power-up line) ---
        _promptLine = MakeLabel(canvasGo, "PromptLine",
            anchorMin: new Vector2(0.2f, 0.68f),
            anchorMax: new Vector2(0.8f, 0.77f),
            fontSize: 28f,
            color: new Color(0.6f, 1f, 0.6f));
    }

    private static TextMeshProUGUI MakeLabel(GameObject canvas, string name,
        Vector2 anchorMin, Vector2 anchorMax, float fontSize, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(canvas.transform, false);

        var rt        = go.AddComponent<RectTransform>();
        rt.anchorMin  = anchorMin;
        rt.anchorMax  = anchorMax;
        rt.offsetMin  = Vector2.zero;
        rt.offsetMax  = Vector2.zero;

        var tmp           = go.AddComponent<TextMeshProUGUI>();
        tmp.fontSize      = fontSize;
        tmp.fontStyle     = FontStyles.Bold;
        tmp.alignment     = TextAlignmentOptions.Center;
        tmp.color         = color;
        tmp.text          = "";
        tmp.gameObject.SetActive(false);
        return tmp;
    }

    // ---------------------------------------------------------------------------
    // Wave announcements
    // ---------------------------------------------------------------------------

    /// <summary>Show "WAVE N — X enemies" then fade after a few seconds.</summary>
    public static void ShowWaveStart(int wave, int count)
    {
        Instance?.ShowWaveBanner($"WAVE {wave}", Color.white, persistent: false);
    }

    /// <summary>Show "WAVE N CLEARED" then count down intermission.</summary>
    public static void ShowWaveCleared(int wave, float intermissionSeconds)
    {
        Instance?.StartWaveCoroutine(
            Instance.IntermissionCountdown($"WAVE {wave} CLEARED", intermissionSeconds));
    }

    private void ShowWaveBanner(string text, Color color, bool persistent)
    {
        if (_waveLine == null) return;
        if (_waveCoroutine != null) StopCoroutine(_waveCoroutine);
        _waveCoroutine = StartCoroutine(persistent
            ? ShowPersistent(_waveLine, text, color)
            : ShowThenHide(_waveLine, text, color, 4f));
    }

    private void StartWaveCoroutine(IEnumerator routine)
    {
        if (_waveCoroutine != null) StopCoroutine(_waveCoroutine);
        _waveCoroutine = StartCoroutine(routine);
    }

    private IEnumerator IntermissionCountdown(string prefix, float duration)
    {
        if (_waveLine == null) yield break;
        _waveLine.color = new Color(0.6f, 1f, 0.6f); // soft green
        _waveLine.gameObject.SetActive(true);

        float remaining = duration;
        while (remaining > 0f)
        {
            _waveLine.text = $"{prefix}\nINTERMISSION — {Mathf.CeilToInt(remaining)}s";
            remaining -= Time.deltaTime;
            yield return null;
        }

        _waveLine.gameObject.SetActive(false);
        _waveCoroutine = null;
    }

    // ---------------------------------------------------------------------------
    // Power-up announcements
    // ---------------------------------------------------------------------------

    public static void ShowPowerUpActivated(string label, Color color, float duration)
    {
        if (Instance == null || Instance._powerUpLine == null) return;
        if (Instance._powerUpCoroutine != null)
            Instance.StopCoroutine(Instance._powerUpCoroutine);
        Instance._powerUpCoroutine = Instance.StartCoroutine(
            duration > 0f
                ? Instance.PowerUpCountdown(label, color, duration)
                : Instance.ShowThenHide(Instance._powerUpLine, $"★ {label} ★", color, 3f));
    }

    public static void ShowBuyPrompt(string text)
    {
        if (Instance?._promptLine == null) return;
        Instance._promptLine.text = text;
        Instance._promptLine.gameObject.SetActive(true);
    }

    public static void ClearBuyPrompt()
    {
        if (Instance?._promptLine == null) return;
        Instance._promptLine.gameObject.SetActive(false);
    }

    public static void ShowPowerUpExpired(string label, Color color)
    {
        if (Instance == null || Instance._powerUpLine == null) return;
        if (Instance._powerUpCoroutine != null)
            Instance.StopCoroutine(Instance._powerUpCoroutine);
        Color dim = color * 0.65f; dim.a = 1f;
        Instance._powerUpCoroutine = Instance.StartCoroutine(
            Instance.ShowThenHide(Instance._powerUpLine, $"{label} expired", dim, 3f));
    }

    private IEnumerator PowerUpCountdown(string label, Color color, float duration)
    {
        if (_powerUpLine == null) yield break;
        _powerUpLine.color = color;
        _powerUpLine.gameObject.SetActive(true);

        float remaining = duration;
        while (remaining > 0f)
        {
            _powerUpLine.text = $"★ {label} ★  ({Mathf.CeilToInt(remaining)}s)";
            remaining -= Time.deltaTime;
            yield return null;
        }

        _powerUpLine.gameObject.SetActive(false);
        _powerUpCoroutine = null;
    }

    // ---------------------------------------------------------------------------
    // Shared helpers
    // ---------------------------------------------------------------------------

    private IEnumerator ShowThenHide(TextMeshProUGUI label, string text, Color color, float duration)
    {
        label.color = color;
        label.text  = text;
        label.gameObject.SetActive(true);
        yield return new WaitForSeconds(duration);
        label.gameObject.SetActive(false);
    }

    private IEnumerator ShowPersistent(TextMeshProUGUI label, string text, Color color)
    {
        label.color = color;
        label.text  = text;
        label.gameObject.SetActive(true);
        yield break;
    }
}
