using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

/// <summary>
/// 리더 머리 위 월드 라벨 하나를 만들기 위한 팀 id와 리더 Transform의 불변 바인딩이다.
/// GameplayRoot가 CrowdRoot의 스폰 결과로 만들어 <see cref="HudRoot.BindLeaderLabels"/>에 전달한다.
/// </summary>
public readonly struct CrowdLabelBinding
{
    /// <summary>
    /// 라벨을 붙일 crowd의 팀 id다. 0은 플레이어, 1..3은 라이벌이다.
    /// </summary>
    public readonly int TeamId;

    /// <summary>
    /// 라벨이 따라다닐 리더의 Transform이다.
    /// </summary>
    public readonly Transform LeaderTransform;

    /// <summary>
    /// 팀 id와 리더 Transform으로 바인딩을 생성한다.
    /// </summary>
    public CrowdLabelBinding(int teamId, Transform leaderTransform)
    {
        TeamId = teamId;
        LeaderTransform = leaderTransform;
    }
}

/// <summary>
/// Hud feature의 root다. 런타임 uGUI Canvas(타이머, 순위표, 시작 힌트, 결과 오버레이)와
/// 리더 머리 위 월드 라벨(TextMesh)을 생성해 소유한다. EventSystem과 Button은 만들지 않는다.
/// bus 구독과 <see cref="IGameSessionReadOnly.StateChanged"/> 연결은 Initialize에서 등록하고
/// Shutdown에서 해제한다. 모든 handler는 UI 상태 갱신만 수행하며 절대 던지지 않는다
/// (플레이어 빌드의 bus dispatch는 예외 격리가 없다).
/// </summary>
public sealed class HudRoot : MonoBehaviour
{
    private const int MaxTeams = 4;
    private const float DimAlpha = 0.35f;

    // 순위표 layout(1080x1920 기준 해상도 좌표).
    private const float RowWidth = 260f;
    private const float RowStride = 70f;
    private const float RowHeight = 60f;
    private const float SwatchSize = 44f;

    // 월드 라벨: fontSize * characterSize * 0.1 ≈ 글자 높이 1.6m — 카메라 거리 16~40m에서 읽히는 크기다.
    private const int LabelFontSize = 64;
    private const float LabelCharacterSize = 0.25f;
    private static readonly Vector3 LabelOffset = new Vector3(0f, 2.2f, 0f);

    private static readonly Color WinTitleColor = new Color(1f, 0.84f, 0.3f);
    private static readonly Color LoseTitleColor = new Color(1f, 0.35f, 0.33f);

    private IGameSessionReadOnly _session;
    private GameConfigSO _config;
    private Camera _worldCamera;
    private Font _font;

    private GameObject _canvasRoot;
    private Text _timerText;
    private GameObject _hintGo;
    private GameObject _resultOverlayGo;
    private Text _resultTitleText;
    private Text _resultStandingsText;

    private readonly GameObject[] _rowGos = new GameObject[MaxTeams];
    private readonly Image[] _rowSwatches = new Image[MaxTeams];
    private readonly Text[] _rowCounts = new Text[MaxTeams];
    private readonly int[] _rowTeam = new int[MaxTeams];
    private readonly int[] _rowCount = new int[MaxTeams];
    private readonly bool[] _rowEliminated = new bool[MaxTeams];

    private readonly TextMesh[] _labels = new TextMesh[MaxTeams];
    private readonly Transform[] _labelTransforms = new Transform[MaxTeams];
    private readonly Transform[] _labelLeaders = new Transform[MaxTeams];

    private readonly List<CrowdStanding> _standings = new List<CrowdStanding>(MaxTeams);
    private readonly StringBuilder _stringBuilder = new StringBuilder(128);

    // static EventManager 채널의 구독 토큰이다. Shutdown에서 반드시 같은 delegate로 해제한다.
    private Action<CrowdCountChangedEvent> _onCountChanged;
    private Action<CrowdEliminatedEvent> _onEliminated;

    private int _lastTimerSecondsShown = int.MinValue;
    private bool _leaderboardDirty;
    private bool _resultDirty;
    private bool _initialized;
    private bool _isShutdown;

    /// <summary>
    /// Canvas(ScreenSpaceOverlay + CanvasScaler 1080x1920, match 0.5)와 모든 HUD 요소를 생성하고,
    /// 두 bus 이벤트와 <paramref name="session"/>.StateChanged를 구독한다. 중복 초기화는 무시한다.
    /// GameplayRoot의 pinned 초기화 순서에 따라 session.Initialize()와 SpawnInitial()보다 먼저 호출된다.
    /// </summary>
    public void Initialize(IGameSessionReadOnly session, GameConfigSO config, Camera worldCamera)
    {
        if (_initialized || _isShutdown)
        {
            return;
        }

        _initialized = true;
        _session = session;
        _config = config;
        _worldCamera = worldCamera;
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        BuildCanvas();

        _onCountChanged = OnCrowdCountChanged;
        _onEliminated = OnCrowdEliminated;
        EventManager.GetSubscriber<CrowdCountChangedEvent>().Subscribe(_onCountChanged);
        EventManager.GetSubscriber<CrowdEliminatedEvent>().Subscribe(_onEliminated);
        _session.StateChanged += OnStateChanged;

        // 초기 표시 상태를 현재 세션 값과 일치시킨다.
        _hintGo.SetActive(_session.State == MatchState.Ready);
        _leaderboardDirty = true;
        UpdateTimerText();
    }

    /// <summary>
    /// 각 리더 머리 위(+2.2m)에 월드 라벨(TextMesh)을 생성한다. 라벨은 LateUpdate에서 worldCamera를
    /// 향해 billboard 회전하고, 텍스트는 <see cref="CrowdCountChangedEvent"/>에서만 갱신되며,
    /// 해당 crowd의 <see cref="CrowdEliminatedEvent"/>에서 파괴된다(플레이어 라벨 포함).
    /// </summary>
    public void BindLeaderLabels(List<CrowdLabelBinding> bindings)
    {
        if (!_initialized || _isShutdown || bindings == null)
        {
            return;
        }

        for (int i = 0; i < bindings.Count; i++)
        {
            CrowdLabelBinding binding = bindings[i];
            if (binding.TeamId < 0 || binding.TeamId >= MaxTeams || binding.LeaderTransform == null)
            {
                continue;
            }

            // 재바인딩이면 이전 라벨을 먼저 정리한다.
            DestroyLabel(binding.TeamId);
            CreateLabel(binding.TeamId, binding.LeaderTransform);
        }

        // SpawnInitial의 첫 coalesced 발행은 바인딩 전에 끝났으므로 현재 순위 스냅샷으로 초기 텍스트를 맞춘다.
        if (_session != null)
        {
            _session.GetStandings(_standings);
            for (int i = 0; i < _standings.Count; i++)
            {
                CrowdStanding standing = _standings[i];
                if (standing.Team < 0 || standing.Team >= MaxTeams || standing.Eliminated)
                {
                    continue;
                }

                TextMesh label = _labels[standing.Team];
                if (label != null)
                {
                    label.text = standing.MemberCount.ToString();
                }
            }
        }
    }

    /// <summary>
    /// bus 구독 토큰을 해제하고 session.StateChanged 연결을 끊은 뒤 Canvas와 월드 라벨을 파괴한다.
    /// 여러 번 호출해도 안전하다. GameplayRoot.Shutdown이 정상 경로이고 OnDestroy는 안전망일 뿐이다.
    /// </summary>
    public void Shutdown()
    {
        if (_isShutdown)
        {
            return;
        }

        _isShutdown = true;

        if (_onCountChanged != null)
        {
            EventManager.GetSubscriber<CrowdCountChangedEvent>().Unsubscribe(_onCountChanged);
            _onCountChanged = null;
        }

        if (_onEliminated != null)
        {
            EventManager.GetSubscriber<CrowdEliminatedEvent>().Unsubscribe(_onEliminated);
            _onEliminated = null;
        }

        if (_session != null)
        {
            _session.StateChanged -= OnStateChanged;
            _session = null;
        }

        for (int team = 0; team < MaxTeams; team++)
        {
            DestroyLabel(team);
        }

        if (_canvasRoot != null)
        {
            Destroy(_canvasRoot);
            _canvasRoot = null;
        }
    }

    private void Update()
    {
        if (!_initialized || _isShutdown)
        {
            return;
        }

        UpdateTimerText();

        // handler는 dirty flag만 세우고 실제 재구성은 여기서 수행한다(재진입 계약).
        if (_leaderboardDirty)
        {
            _leaderboardDirty = false;
            RefreshLeaderboard();
        }

        if (_resultDirty)
        {
            _resultDirty = false;
            ShowResultOverlay();
        }
    }

    private void LateUpdate()
    {
        if (!_initialized || _isShutdown)
        {
            return;
        }

        bool hasCamera = _worldCamera != null;
        Quaternion billboard = hasCamera ? _worldCamera.transform.rotation : Quaternion.identity;

        for (int team = 0; team < MaxTeams; team++)
        {
            TextMesh label = _labels[team];
            if (label == null)
            {
                continue;
            }

            Transform leader = _labelLeaders[team];
            if (leader == null)
            {
                // 리더가 외부에서 파괴됐으면(정상 흐름에서는 elimination 이벤트가 먼저 온다) 라벨도 정리한다.
                DestroyLabel(team);
                continue;
            }

            Transform labelTransform = _labelTransforms[team];
            labelTransform.position = leader.position + LabelOffset;
            if (hasCamera)
            {
                labelTransform.rotation = billboard;
            }
        }
    }

    private void OnDestroy()
    {
        // 안전망이다. 정상 경로는 GameplayRoot.Shutdown -> HudRoot.Shutdown이다.
        Shutdown();
    }

    // bus handler: UI 상태 갱신만 수행하고 절대 던지지 않는다.
    private void OnCrowdCountChanged(CrowdCountChangedEvent e)
    {
        if (_isShutdown)
        {
            return;
        }

        _leaderboardDirty = true;

        if (e.CrowdId >= 0 && e.CrowdId < MaxTeams)
        {
            TextMesh label = _labels[e.CrowdId];
            if (label != null)
            {
                label.text = e.MemberCount.ToString();
            }
        }
    }

    // bus handler: 해당 crowd의 월드 라벨을 제거하고 순위표만 dirty 처리한다.
    private void OnCrowdEliminated(CrowdEliminatedEvent e)
    {
        if (_isShutdown)
        {
            return;
        }

        _leaderboardDirty = true;

        if (e.CrowdId >= 0 && e.CrowdId < MaxTeams)
        {
            DestroyLabel(e.CrowdId);
        }
    }

    // session handler: 힌트 표시 전환과 dirty flag만 수행한다.
    private void OnStateChanged(MatchState state)
    {
        if (_isShutdown)
        {
            return;
        }

        if (_hintGo != null)
        {
            _hintGo.SetActive(state == MatchState.Ready);
        }

        if (state == MatchState.Finished)
        {
            _resultDirty = true;
            _leaderboardDirty = true;
        }
    }

    private void UpdateTimerText()
    {
        if (_session == null || _timerText == null)
        {
            return;
        }

        // 표시 초가 바뀔 때만 문자열을 만들어 per-frame 할당을 피한다.
        int seconds = Mathf.CeilToInt(Mathf.Max(0f, _session.TimeRemaining));
        if (seconds == _lastTimerSecondsShown)
        {
            return;
        }

        _lastTimerSecondsShown = seconds;
        int minutes = seconds / 60;
        int remainder = seconds - minutes * 60;

        _stringBuilder.Length = 0;
        _stringBuilder.Append(minutes).Append(':');
        if (remainder < 10)
        {
            _stringBuilder.Append('0');
        }

        _stringBuilder.Append(remainder);
        _timerText.text = _stringBuilder.ToString();
    }

    private void RefreshLeaderboard()
    {
        if (_session == null)
        {
            return;
        }

        _session.GetStandings(_standings);

        for (int i = 0; i < MaxTeams; i++)
        {
            GameObject rowGo = _rowGos[i];
            if (rowGo == null)
            {
                continue;
            }

            if (i >= _standings.Count)
            {
                if (rowGo.activeSelf)
                {
                    rowGo.SetActive(false);
                }

                continue;
            }

            if (!rowGo.activeSelf)
            {
                rowGo.SetActive(true);
            }

            CrowdStanding standing = _standings[i];
            if (standing.Team == _rowTeam[i]
                && standing.MemberCount == _rowCount[i]
                && standing.Eliminated == _rowEliminated[i])
            {
                continue;
            }

            _rowTeam[i] = standing.Team;
            _rowCount[i] = standing.MemberCount;
            _rowEliminated[i] = standing.Eliminated;

            float alpha = standing.Eliminated ? DimAlpha : 1f;
            Color swatchColor = GetTeamColor(standing.Team);
            swatchColor.a = alpha;
            _rowSwatches[i].color = swatchColor;

            Color textColor = Color.white;
            textColor.a = alpha;
            _rowCounts[i].color = textColor;
            _rowCounts[i].text = standing.Eliminated ? "OUT" : standing.MemberCount.ToString();
        }
    }

    private void ShowResultOverlay()
    {
        if (_session == null || _resultOverlayGo == null)
        {
            return;
        }

        bool playerWon = _session.PlayerWon;
        _resultTitleText.text = playerWon ? "WIN!" : "LOSE";
        _resultTitleText.color = playerWon ? WinTitleColor : LoseTitleColor;

        _session.GetStandings(_standings);
        _stringBuilder.Length = 0;
        for (int i = 0; i < _standings.Count; i++)
        {
            CrowdStanding standing = _standings[i];
            if (i > 0)
            {
                _stringBuilder.Append('\n');
            }

            _stringBuilder.Append(i + 1).Append(". ").Append(GetTeamName(standing.Team)).Append("  ");
            if (standing.Eliminated)
            {
                _stringBuilder.Append("OUT");
            }
            else
            {
                _stringBuilder.Append(standing.MemberCount);
            }
        }

        _resultStandingsText.text = _stringBuilder.ToString();
        _resultOverlayGo.SetActive(true);
    }

    private void BuildCanvas()
    {
        _canvasRoot = new GameObject("HudCanvas");
        _canvasRoot.transform.SetParent(transform, false);

        Canvas canvas = _canvasRoot.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        CanvasScaler scaler = _canvasRoot.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080f, 1920f);
        scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
        scaler.matchWidthOrHeight = 0.5f;

        Transform canvasTransform = _canvasRoot.transform;

        // 타이머: 상단 중앙, "M:SS".
        _timerText = CreateText(canvasTransform, "Timer", 72, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
        SetRect((RectTransform)_timerText.transform,
            new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -36f), new Vector2(500f, 100f));

        BuildLeaderboard(canvasTransform);

        // 시작 힌트: Ready에서만 보인다.
        Text hintText = CreateText(canvasTransform, "StartHint", 56, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
        hintText.text = "DRAG TO START";
        SetRect((RectTransform)hintText.transform,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -360f), new Vector2(900f, 90f));
        _hintGo = hintText.gameObject;

        BuildResultOverlay(canvasTransform);
    }

    private void BuildLeaderboard(Transform canvasTransform)
    {
        GameObject leaderboardGo = new GameObject("Leaderboard", typeof(RectTransform));
        leaderboardGo.transform.SetParent(canvasTransform, false);
        SetRect((RectTransform)leaderboardGo.transform,
            new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-30f, -30f), new Vector2(RowWidth, RowStride * MaxTeams));

        for (int i = 0; i < MaxTeams; i++)
        {
            GameObject rowGo = new GameObject("Row" + i, typeof(RectTransform));
            rowGo.transform.SetParent(leaderboardGo.transform, false);
            SetRect((RectTransform)rowGo.transform,
                new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(0f, -i * RowStride), new Vector2(RowWidth, RowHeight));

            GameObject swatchGo = new GameObject("Swatch", typeof(RectTransform));
            swatchGo.transform.SetParent(rowGo.transform, false);
            Image swatch = swatchGo.AddComponent<Image>();
            swatch.raycastTarget = false;
            SetRect((RectTransform)swatchGo.transform,
                new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(SwatchSize, SwatchSize));

            Text countText = CreateText(rowGo.transform, "Count", 44, TextAnchor.MiddleRight, Color.white, FontStyle.Bold);
            RectTransform countRect = (RectTransform)countText.transform;
            countRect.anchorMin = new Vector2(0f, 0f);
            countRect.anchorMax = new Vector2(1f, 1f);
            countRect.offsetMin = new Vector2(SwatchSize + 12f, 0f);
            countRect.offsetMax = new Vector2(0f, 0f);
            countText.text = "-";

            _rowGos[i] = rowGo;
            _rowSwatches[i] = swatch;
            _rowCounts[i] = countText;
            _rowTeam[i] = int.MinValue; // 첫 갱신을 강제한다.
        }
    }

    private void BuildResultOverlay(Transform canvasTransform)
    {
        _resultOverlayGo = new GameObject("ResultOverlay", typeof(RectTransform));
        _resultOverlayGo.transform.SetParent(canvasTransform, false);

        Image dim = _resultOverlayGo.AddComponent<Image>();
        dim.color = new Color(0f, 0f, 0f, 0.72f);
        dim.raycastTarget = false;

        RectTransform overlayRect = (RectTransform)_resultOverlayGo.transform;
        overlayRect.anchorMin = Vector2.zero;
        overlayRect.anchorMax = Vector2.one;
        overlayRect.offsetMin = Vector2.zero;
        overlayRect.offsetMax = Vector2.zero;

        Transform overlayTransform = _resultOverlayGo.transform;

        _resultTitleText = CreateText(overlayTransform, "Title", 120, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
        SetRect((RectTransform)_resultTitleText.transform,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 420f), new Vector2(900f, 160f));

        _resultStandingsText = CreateText(overlayTransform, "Standings", 56, TextAnchor.MiddleCenter, Color.white, FontStyle.Normal);
        SetRect((RectTransform)_resultStandingsText.transform,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 40f), new Vector2(900f, 520f));

        Text restartText = CreateText(overlayTransform, "RestartHint", 48, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
        restartText.text = "R / TAP TO RESTART";
        SetRect((RectTransform)restartText.transform,
            new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, -420f), new Vector2(900f, 90f));

        _resultOverlayGo.SetActive(false);
    }

    private Text CreateText(Transform parent, string name, int fontSize, TextAnchor alignment, Color color, FontStyle style)
    {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);

        Text text = go.AddComponent<Text>();
        text.font = _font;
        text.fontSize = fontSize;
        text.alignment = alignment;
        text.color = color;
        text.fontStyle = style;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    private static void SetRect(RectTransform rect, Vector2 anchor, Vector2 pivot, Vector2 anchoredPosition, Vector2 size)
    {
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = pivot;
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;
    }

    private void CreateLabel(int teamId, Transform leader)
    {
        GameObject go = new GameObject("CrowdLabel_" + teamId);
        go.transform.SetParent(transform, false);

        TextMesh label = go.AddComponent<TextMesh>();
        label.font = _font;
        label.fontSize = LabelFontSize;
        label.characterSize = LabelCharacterSize;
        label.anchor = TextAnchor.LowerCenter;
        label.alignment = TextAlignment.Center;
        label.color = GetTeamColor(teamId);
        label.text = "1";

        MeshRenderer meshRenderer = go.GetComponent<MeshRenderer>();
        if (_font != null)
        {
            meshRenderer.sharedMaterial = _font.material;
        }

        meshRenderer.shadowCastingMode = ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;

        Transform labelTransform = go.transform;
        labelTransform.position = leader.position + LabelOffset;
        if (_worldCamera != null)
        {
            labelTransform.rotation = _worldCamera.transform.rotation;
        }

        _labels[teamId] = label;
        _labelTransforms[teamId] = labelTransform;
        _labelLeaders[teamId] = leader;
    }

    private void DestroyLabel(int teamId)
    {
        TextMesh label = _labels[teamId];
        _labels[teamId] = null;
        _labelTransforms[teamId] = null;
        _labelLeaders[teamId] = null;

        if (label != null)
        {
            Destroy(label.gameObject);
        }
    }

    private Color GetTeamColor(int team)
    {
        IReadOnlyList<Color> colors = _config != null ? _config.TeamColors : null;
        if (colors != null && team >= 0 && team < colors.Count)
        {
            return colors[team];
        }

        return Color.white;
    }

    private static string GetTeamName(int team)
    {
        switch (team)
        {
            case 0: return "PLAYER";
            case 1: return "RIVAL 1";
            case 2: return "RIVAL 2";
            case 3: return "RIVAL 3";
            default: return "TEAM";
        }
    }
}
