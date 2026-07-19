using System;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// Dev-only overlay: a touch-friendly startup panel for choosing the neutral spawn count
/// plus a self-updating top-left FPS/frame-ms readout. Owned by GameSceneController; shown
/// only in interactive editor Play / development builds. Not present in production or batchmode.
/// </summary>
public sealed class DevHudRoot : MonoBehaviour
{
    [Header("Startup Panel")]
    [SerializeField] private GameObject _startupPanel;
    [SerializeField] private TextMeshProUGUI _countText;
    [SerializeField] private Button _decButton;
    [SerializeField] private Button _incButton;
    [SerializeField] private Button _startButton;
    [SerializeField] private Button[] _presetButtons;
    [SerializeField] private int[] _presetValues;

    [Header("FPS Readout")]
    [SerializeField] private TextMeshProUGUI _fpsText;

    private const int Step = 100;
    private const float FpsRefreshInterval = 0.25f;
    private const float FpsEmaAlpha = 0.1f;

    /// <summary>Raised (once, synchronously) when the user confirms the count via Start.</summary>
    public event Action<int> CountConfirmed;

    private int _count;
    private int _min;
    private int _max;
    private bool _initialized;

    private float _emaDelta;
    private float _fpsTimer;

    /// <summary>Wires the startup panel and seeds the count. Runs once, synchronously, after Instantiate.</summary>
    public void Init(int defaultCount, int min, int max)
    {
        if (_initialized) return;

        // Required serialized authoring refs are wired on this feature's own prefab. If any is missing,
        // hard-fail loudly instead of silently no-oping (§11.5). Guards run before _initialized flips so a
        // failed Init leaves the object un-initialized for the owner to roll back.
        if (_startupPanel == null) throw new InvalidOperationException("[DevHudRoot] _startupPanel not wired (prefab serialization missing).");
        if (_countText == null) throw new InvalidOperationException("[DevHudRoot] _countText not wired (prefab serialization missing).");
        if (_decButton == null) throw new InvalidOperationException("[DevHudRoot] _decButton not wired (prefab serialization missing).");
        if (_incButton == null) throw new InvalidOperationException("[DevHudRoot] _incButton not wired (prefab serialization missing).");
        if (_startButton == null) throw new InvalidOperationException("[DevHudRoot] _startButton not wired (prefab serialization missing).");
        if (_presetButtons == null) throw new InvalidOperationException("[DevHudRoot] _presetButtons not wired (prefab serialization missing).");
        if (_presetValues == null) throw new InvalidOperationException("[DevHudRoot] _presetValues not wired (prefab serialization missing).");
        if (_presetButtons.Length != _presetValues.Length)
            throw new InvalidOperationException($"[DevHudRoot] _presetButtons/_presetValues length mismatch ({_presetButtons.Length} vs {_presetValues.Length}); each preset button needs a matching value.");
        for (int i = 0; i < _presetButtons.Length; i++)
        {
            if (_presetButtons[i] == null) throw new InvalidOperationException($"[DevHudRoot] _presetButtons[{i}] not wired (prefab serialization missing).");
        }
        if (_fpsText == null) throw new InvalidOperationException("[DevHudRoot] _fpsText not wired (prefab serialization missing).");

        _initialized = true;

        _min = Mathf.Min(min, max);
        _max = Mathf.Max(min, max);
        SetCount(defaultCount);

        _decButton.onClick.AddListener(OnDec);
        _incButton.onClick.AddListener(OnInc);
        _startButton.onClick.AddListener(OnStart);
        if (_presetButtons != null)
        {
            for (int i = 0; i < _presetButtons.Length; i++)
            {
                Button preset = _presetButtons[i];
                if (preset == null) continue;
                int value = (_presetValues != null && i < _presetValues.Length) ? _presetValues[i] : _count;
                preset.onClick.AddListener(() => SetCount(value));
            }
        }

        // The new Input System needs UI actions assigned for the buttons to respond to clicks.
        // The EventSystem + InputSystemUIInputModule live on a prefab child; assign defaults at runtime
        // (self-contained; not an injected dependency).
        InputSystemUIInputModule module = GetComponentInChildren<InputSystemUIInputModule>(true);
        if (module != null) module.AssignDefaultActions();
    }

    /// <summary>Hides the count-picker panel while keeping the FPS overlay visible.</summary>
    public void HideStartupPanel()
    {
        if (_startupPanel != null) _startupPanel.SetActive(false);
    }

    private void OnDec() => SetCount(_count - Step);
    private void OnInc() => SetCount(_count + Step);
    private void OnStart() => CountConfirmed?.Invoke(_count);

    private void SetCount(int value)
    {
        _count = Mathf.Clamp(value, _min, _max);
        if (_countText != null) _countText.text = _count.ToString();
    }

    private void Update()
    {
        float dt = Time.unscaledDeltaTime;
        if (dt <= 0f) return;

        _emaDelta = _emaDelta <= 0f ? dt : Mathf.Lerp(_emaDelta, dt, FpsEmaAlpha);

        _fpsTimer += dt;
        if (_fpsTimer < FpsRefreshInterval) return;
        _fpsTimer = 0f;

        if (_fpsText == null || _emaDelta <= 0f) return;
        float ms = _emaDelta * 1000f;
        float fps = 1f / _emaDelta;
        _fpsText.text = $"{fps:0} FPS  {ms:0.0} ms";
    }

    private void OnDestroy()
    {
        RemoveClick(_decButton);
        RemoveClick(_incButton);
        RemoveClick(_startButton);
        if (_presetButtons != null)
        {
            foreach (Button preset in _presetButtons) RemoveClick(preset);
        }
        CountConfirmed = null;
    }

    private static void RemoveClick(Button button)
    {
        if (button != null) button.onClick.RemoveAllListeners();
    }
}
