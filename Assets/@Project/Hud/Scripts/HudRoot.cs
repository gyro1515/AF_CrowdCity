using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 리더 머리 위 Overlay 라벨 하나를 만들기 위한 팀 id와 리더 Transform의 불변 바인딩이다.
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
/// 리더 머리 위 Overlay TMP 라벨, 화면 밖 라이벌 방향/인원 마커를 생성해 소유한다.
/// EventSystem과 Button은 만들지 않는다.
/// bus 구독과 <see cref="IGameSessionReadOnly.StateChanged"/> 연결은 Initialize에서 등록하고
/// Shutdown에서 해제한다. 모든 handler는 UI 상태 갱신만 수행하며 절대 던지지 않는다
/// (플레이어 빌드의 bus dispatch는 예외 격리가 없다).
/// </summary>
[DefaultExecutionOrder(100)]
public sealed class HudRoot : MonoBehaviour
{
    private const int MaxTeams = 4;
    private const float DimAlpha = 0.35f;
    private const string FontResourcePath = "Fonts & Materials/LiberationSans SDF - Fallback";

    // Overlay 리더 라벨이 리더 머리 위로 떠서 투영될 월드 오프셋이다(프리팹 저작 대상 아님 — 런타임 투영 전용).
    private static readonly Vector3 LabelOffset = new Vector3(0f, 2.2f, 0f);

    // 화면 밖 마커: CanvasScaler의 로컬 좌표 기준. safe area를 다시 inset해 화면 비율과 notch에서도 잘리지 않게 한다.
    // (마커/라벨의 크기·폰트·색 등 정적 레이아웃은 프리팹에 저작되고, 아래 값은 런타임 edge clamp/카운트 위치 계산에만 쓰인다.)
    private const float MarkerEdgePadding = 60f;
    private const float MarkerCountInset = 58f;
    private const float MarkerDirectionEpsilon = 0.0001f;

    private static readonly Color WinTitleColor = new Color(1f, 0.84f, 0.3f);
    private static readonly Color LoseTitleColor = new Color(1f, 0.35f, 0.33f);

    private IGameSessionReadOnly _session;
    private GameConfigSO _config;
    private Camera _worldCamera;
    private TMP_FontAsset _fontAsset;

    // 리더 카운트 라벨의 face 색/아웃라인 폭 스타일 원본이다. HudRoot 프리팹에 직렬화 저작되며
    // (GameSceneSetup이 CrowdCountTextStyle.asset을 배선한다), CreateLabelMaterials가 읽기 전용으로만 소비한다.
    [SerializeField] private TMPTextStyleSO _crowdCountTextStyle;

    // 아래 참조들은 HudRoot 프리팹에 사전 저작된 정적 uGUI 트리(Canvas는 이 컴포넌트가 붙은 프리팹 root 자신)를 가리킨다.
    // 프리팹 인스펙터/Editor 저작(GameSceneSetup)에서 배선되는 자기 자식 뷰 참조이므로 SO 소비자 불변성과 무관하다.
    [SerializeField] private TextMeshProUGUI _timerText;
    [SerializeField] private GameObject _hintGo;
    [SerializeField] private GameObject _resultOverlayGo;
    [SerializeField] private TextMeshProUGUI _resultTitleText;
    [SerializeField] private TextMeshProUGUI _resultStandingsText;
    [SerializeField] private RectTransform _leaderLabelLayerRect;
    [SerializeField] private RectTransform _markerLayerRect;
    [SerializeField] private GameObject[] _rowGos;
    [SerializeField] private Image[] _rowSwatches;
    [SerializeField] private TextMeshProUGUI[] _rowCounts;

    // 동적 라벨/마커 템플릿 프리팹(개수가 팀 수에 따라 가변이라 정적화 불가). 런타임 Resources.Load + Instantiate로 복제한다.
    private GameObject _crowdLabelPrefab;
    private GameObject _rivalMarkerPrefab;

    private readonly int[] _rowTeam = new int[MaxTeams];
    private readonly int[] _rowCount = new int[MaxTeams];
    private readonly bool[] _rowEliminated = new bool[MaxTeams];

    private readonly TextMeshProUGUI[] _labels = new TextMeshProUGUI[MaxTeams];
    private readonly Material[] _labelMaterials = new Material[MaxTeams];
    private readonly Transform[] _labelLeaders = new Transform[MaxTeams];

    private readonly GameObject[] _markerGos = new GameObject[MaxTeams];
    private readonly TextMeshProUGUI[] _markerArrows = new TextMeshProUGUI[MaxTeams];
    private readonly TextMeshProUGUI[] _markerCounts = new TextMeshProUGUI[MaxTeams];

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
    public void Initialize(
        IGameSessionReadOnly session,
        GameConfigSO config,
        Camera worldCamera)
    {
        if (_initialized || _isShutdown)
        {
            return;
        }

        if (_crowdCountTextStyle == null)
        {
            throw new InvalidOperationException(
                "[HudRoot] _crowdCountTextStyle 직렬화 필드가 비어 있습니다(프리팹 배선 누락).");
        }

        TMP_FontAsset fontAsset = Resources.Load<TMP_FontAsset>(FontResourcePath);
        if (fontAsset == null)
        {
            throw new InvalidOperationException(
                "[HudRoot] TMP font asset을 찾을 수 없습니다: Resources/" + FontResourcePath);
        }

        GameObject crowdLabelPrefab = Resources.Load<GameObject>(HudResources.CrowdLabel);
        if (crowdLabelPrefab == null)
        {
            throw new InvalidOperationException(
                "[HudRoot] CrowdLabel 프리팹을 찾을 수 없습니다: Resources/" + HudResources.CrowdLabel);
        }

        GameObject rivalMarkerPrefab = Resources.Load<GameObject>(HudResources.RivalMarker);
        if (rivalMarkerPrefab == null)
        {
            throw new InvalidOperationException(
                "[HudRoot] RivalMarker 프리팹을 찾을 수 없습니다: Resources/" + HudResources.RivalMarker);
        }

        _initialized = true;
        _session = session;
        _config = config;
        _worldCamera = worldCamera;
        _fontAsset = fontAsset;
        _crowdLabelPrefab = crowdLabelPrefab;
        _rivalMarkerPrefab = rivalMarkerPrefab;
        CreateLabelMaterials();

        // 정적 uGUI 트리는 프리팹에 저작돼 있고 참조는 직렬화로 주입된다. 과거 BuildLeaderboard가 하던
        // "행별 첫 갱신 강제"만 런타임 상태로 남겨 재현한다(RefreshLeaderboard가 int.MinValue를 보면 반드시 갱신한다).
        for (int i = 0; i < MaxTeams; i++)
        {
            _rowTeam[i] = int.MinValue;
        }

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
    /// 각 리더 머리 위(+2.2m)를 Screen Space Overlay Canvas에 투영하는 TMP 라벨을 생성하고, 라이벌(1..3)은
    /// 화면 밖 방향/인원 마커도 Canvas 아래에 생성한다. 두 표시는 <see cref="CrowdCountChangedEvent"/>에서
    /// 같이 갱신되고 <see cref="CrowdEliminatedEvent"/>에서 파괴된다. 재바인딩은 기존 표시를 먼저 정리한다.
    /// </summary>
    public void BindLeaderLabels(List<CrowdLabelBinding> bindings)
    {
        if (!_initialized || _isShutdown || bindings == null)
        {
            return;
        }

        for (int team = 0; team < MaxTeams; team++)
        {
            DestroyCrowdVisuals(team);
        }

        // SpawnInitial의 첫 coalesced 발행은 바인딩 전에 끝났으므로 현재 스냅샷을 같이 읽는다.
        _standings.Clear();
        if (_session != null)
        {
            _session.GetStandings(_standings);
        }

        for (int i = 0; i < bindings.Count; i++)
        {
            CrowdLabelBinding binding = bindings[i];
            if (binding.TeamId < 0 || binding.TeamId >= MaxTeams || binding.LeaderTransform == null)
            {
                continue;
            }

            // 잘못된 중복 binding이 들어와도 해당 팀 표시는 하나만 유지한다.
            DestroyCrowdVisuals(binding.TeamId);
            CreateLabel(binding.TeamId, binding.LeaderTransform);
            if (binding.TeamId > MatchRules.PlayerTeam)
            {
                CreateRivalMarker(binding.TeamId);
            }
        }

        for (int i = 0; i < _standings.Count; i++)
        {
            CrowdStanding standing = _standings[i];
            if (standing.Team < 0 || standing.Team >= MaxTeams)
            {
                continue;
            }

            if (standing.Eliminated)
            {
                DestroyCrowdVisuals(standing.Team);
            }
            else
            {
                SetCrowdCount(standing.Team, standing.MemberCount);
            }
        }
    }

    /// <summary>
    /// bus 구독 토큰을 해제하고 session.StateChanged 연결을 끊은 뒤 Canvas와 Overlay 라벨을 파괴한다.
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
            DestroyCrowdVisuals(team);
        }

        DestroyLabelMaterials();

        // Canvas와 정적 uGUI 트리는 이 컴포넌트가 붙은 프리팹 root 자신이므로 별도 파괴 대상이 없다.
        // 정상 경로에서는 GameplayRoot.Shutdown이 곧바로 HudRoot GameObject를 파괴하고, OnDestroy가 최종 안전망이다.
        _leaderLabelLayerRect = null;
        _markerLayerRect = null;
        _worldCamera = null;
        _config = null;
        _fontAsset = null;
        _crowdLabelPrefab = null;
        _rivalMarkerPrefab = null;
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

        UpdateLeaderLabels();
        UpdateRivalMarkers();
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
            SetCrowdCount(e.CrowdId, e.MemberCount);
        }
    }

    // bus handler: 해당 crowd의 Overlay 라벨과 화면 밖 마커를 즉시 숨기고 제거한다.
    private void OnCrowdEliminated(CrowdEliminatedEvent e)
    {
        if (_isShutdown)
        {
            return;
        }

        _leaderboardDirty = true;

        if (e.CrowdId >= 0 && e.CrowdId < MaxTeams)
        {
            DestroyCrowdVisuals(e.CrowdId);
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

    private void UpdateLeaderLabels()
    {
        if (_worldCamera == null || _leaderLabelLayerRect == null)
        {
            HideLeaderLabels();
            return;
        }

        Rect layerRect = _leaderLabelLayerRect.rect;
        if (layerRect.width <= 0f || layerRect.height <= 0f)
        {
            HideLeaderLabels();
            return;
        }

        for (int team = 0; team < MaxTeams; team++)
        {
            TextMeshProUGUI label = _labels[team];
            if (label == null)
            {
                continue;
            }

            Transform leader = _labelLeaders[team];
            if (leader == null)
            {
                // 정상 흐름에서는 elimination 이벤트가 먼저 오지만 외부 파괴에도 표시가 남지 않게 한다.
                DestroyCrowdVisuals(team);
                continue;
            }

            Vector3 viewportPoint;
            if (!TryGetLeaderViewportPoint(leader, out viewportPoint)
                || !IsViewportPointOnScreen(viewportPoint))
            {
                SetLabelActive(label, false);
                continue;
            }

            Vector3 screenPoint = _worldCamera.ViewportToScreenPoint(viewportPoint);
            Vector2 anchoredPosition;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    _leaderLabelLayerRect, screenPoint, null, out anchoredPosition))
            {
                SetLabelActive(label, false);
                continue;
            }

            label.rectTransform.anchoredPosition = anchoredPosition;
            SetLabelActive(label, true);
        }
    }

    private void HideLeaderLabels()
    {
        for (int team = 0; team < MaxTeams; team++)
        {
            TextMeshProUGUI label = _labels[team];
            if (label != null)
            {
                SetLabelActive(label, false);
            }
        }
    }

    private static void SetLabelActive(TextMeshProUGUI label, bool active)
    {
        if (label.gameObject.activeSelf != active)
        {
            label.gameObject.SetActive(active);
        }
    }

    private bool TryGetLeaderViewportPoint(Transform leader, out Vector3 viewportPoint)
    {
        if (_worldCamera == null || leader == null)
        {
            viewportPoint = Vector3.zero;
            return false;
        }

        viewportPoint = _worldCamera.WorldToViewportPoint(leader.position + LabelOffset);
        return true;
    }

    private static bool IsViewportPointOnScreen(Vector3 viewportPoint)
    {
        return viewportPoint.z > 0f
            && viewportPoint.x >= 0f && viewportPoint.x <= 1f
            && viewportPoint.y >= 0f && viewportPoint.y <= 1f;
    }

    private void CreateRivalMarker(int teamId)
    {
        if (teamId <= MatchRules.PlayerTeam
            || teamId >= MaxTeams
            || _markerLayerRect == null)
        {
            return;
        }

        DestroyRivalMarker(teamId);

        // 정적 레이아웃(RectTransform 크기·폰트·정렬·화살표 글리프·카운트 초기 위치)은 RivalMarker 프리팹에 저작돼 있고,
        // 런타임에는 팀 색(화살표)과 카운트 텍스트만 주입한다. new GameObject+AddComponent 조립을 대체한다.
        GameObject markerGo = Instantiate(_rivalMarkerPrefab, _markerLayerRect, false);
        markerGo.name = "RivalMarker_" + teamId;

        TextMeshProUGUI arrow = FindMarkerText(markerGo, "Arrow");
        // 폰트를 먼저 지정한다(TMP는 font 지정 시 material을 폰트 기본값으로 되돌리므로 색 지정보다 앞선다).
        arrow.font = _fontAsset;
        arrow.color = GetTeamColor(teamId);

        TextMeshProUGUI count = FindMarkerText(markerGo, "Count");
        count.font = _fontAsset;
        count.text = "1";

        _markerGos[teamId] = markerGo;
        _markerArrows[teamId] = arrow;
        _markerCounts[teamId] = count;

        // LateUpdate가 첫 위치를 계산하기 전에 중앙에서 한 frame 보이지 않게 한다.
        markerGo.SetActive(false);
    }

    private static TextMeshProUGUI FindMarkerText(GameObject markerGo, string childName)
    {
        Transform child = markerGo.transform.Find(childName);
        if (child == null)
        {
            throw new InvalidOperationException(
                "[HudRoot] RivalMarker prefab에 '" + childName + "' 자식이 없습니다.");
        }

        TextMeshProUGUI text = child.GetComponent<TextMeshProUGUI>();
        if (text == null)
        {
            throw new InvalidOperationException(
                "[HudRoot] RivalMarker prefab의 '" + childName + "'에 TextMeshProUGUI가 없습니다.");
        }

        return text;
    }

    private void UpdateRivalMarkers()
    {
        if (_worldCamera == null || _markerLayerRect == null)
        {
            HideRivalMarkers();
            return;
        }

        Rect canvasRect = _markerLayerRect.rect;
        if (canvasRect.width <= 0f || canvasRect.height <= 0f)
        {
            HideRivalMarkers();
            return;
        }

        Rect safeArea = Screen.safeArea;
        float screenWidth = Mathf.Max(1f, Screen.width);
        float screenHeight = Mathf.Max(1f, Screen.height);
        if (safeArea.width <= 0f || safeArea.height <= 0f)
        {
            safeArea = new Rect(0f, 0f, screenWidth, screenHeight);
        }

        Rect markerBounds = Rect.MinMaxRect(
            Mathf.Lerp(canvasRect.xMin, canvasRect.xMax, safeArea.xMin / screenWidth) + MarkerEdgePadding,
            Mathf.Lerp(canvasRect.yMin, canvasRect.yMax, safeArea.yMin / screenHeight) + MarkerEdgePadding,
            Mathf.Lerp(canvasRect.xMin, canvasRect.xMax, safeArea.xMax / screenWidth) - MarkerEdgePadding,
            Mathf.Lerp(canvasRect.yMin, canvasRect.yMax, safeArea.yMax / screenHeight) - MarkerEdgePadding);
        Vector2 localOrigin = canvasRect.center;

        for (int team = MatchRules.PlayerTeam + 1; team < MaxTeams; team++)
        {
            GameObject markerGo = _markerGos[team];
            TextMeshProUGUI markerArrow = _markerArrows[team];
            TextMeshProUGUI markerCount = _markerCounts[team];
            Transform leader = _labelLeaders[team];
            if (markerGo == null || markerArrow == null || markerCount == null || leader == null)
            {
                if (markerGo != null && markerGo.activeSelf)
                {
                    markerGo.SetActive(false);
                }

                continue;
            }

            Vector3 viewportPoint;
            if (!TryGetLeaderViewportPoint(leader, out viewportPoint))
            {
                if (markerGo.activeSelf)
                {
                    markerGo.SetActive(false);
                }

                continue;
            }

            bool inFront = viewportPoint.z > 0f;
            bool onScreen = IsViewportPointOnScreen(viewportPoint);

            if (onScreen)
            {
                if (markerGo.activeSelf)
                {
                    markerGo.SetActive(false);
                }

                continue;
            }

            Vector2 direction = new Vector2(
                (viewportPoint.x - 0.5f) * canvasRect.width,
                (viewportPoint.y - 0.5f) * canvasRect.height);
            if (!inFront)
            {
                // WorldToViewportPoint는 camera 뒤 좌표를 반전해 돌려주므로 중앙 기준 방향을 다시 반전한다.
                direction = -direction;
            }

            if (direction.sqrMagnitude < MarkerDirectionEpsilon)
            {
                // camera 뒤 정중앙은 투영 방향이 정의되지 않으므로 화면 아래쪽을 기본으로 한다.
                direction = Vector2.down;
            }
            else
            {
                direction.Normalize();
            }

            float distanceX = Mathf.Infinity;
            if (Mathf.Abs(direction.x) > MarkerDirectionEpsilon)
            {
                distanceX = direction.x > 0f
                    ? (markerBounds.xMax - localOrigin.x) / direction.x
                    : (markerBounds.xMin - localOrigin.x) / direction.x;
            }

            float distanceY = Mathf.Infinity;
            if (Mathf.Abs(direction.y) > MarkerDirectionEpsilon)
            {
                distanceY = direction.y > 0f
                    ? (markerBounds.yMax - localOrigin.y) / direction.y
                    : (markerBounds.yMin - localOrigin.y) / direction.y;
            }

            float edgeDistance = Mathf.Min(distanceX, distanceY);
            Vector2 markerPosition = localOrigin + direction * edgeDistance;
            markerPosition.x = Mathf.Clamp(markerPosition.x, markerBounds.xMin, markerBounds.xMax);
            markerPosition.y = Mathf.Clamp(markerPosition.y, markerBounds.yMin, markerBounds.yMax);

            ((RectTransform)markerGo.transform).anchoredPosition = markerPosition;
            markerArrow.rectTransform.localRotation = Quaternion.Euler(
                0f, 0f, Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg - 90f);
            markerCount.rectTransform.anchoredPosition = -direction * MarkerCountInset;

            if (!markerGo.activeSelf)
            {
                markerGo.SetActive(true);
            }
        }
    }

    private void HideRivalMarkers()
    {
        for (int team = MatchRules.PlayerTeam + 1; team < MaxTeams; team++)
        {
            GameObject markerGo = _markerGos[team];
            if (markerGo != null && markerGo.activeSelf)
            {
                markerGo.SetActive(false);
            }
        }
    }

    private void SetCrowdCount(int teamId, int memberCount)
    {
        string value = memberCount.ToString();
        SetLabelText(teamId, value);

        TextMeshProUGUI markerCount = _markerCounts[teamId];
        if (markerCount != null)
        {
            markerCount.text = value;
        }
    }

    private void CreateLabel(int teamId, Transform leader)
    {
        // 정적 레이아웃(container RectTransform·폰트·크기·정렬·richText 등)은 CrowdLabel 프리팹에 저작돼 있고,
        // 런타임에는 팀 아웃라인 material과 텍스트만 주입한다. new GameObject+AddComponent 조립을 대체한다.
        GameObject go = Instantiate(_crowdLabelPrefab, _leaderLabelLayerRect, false);
        go.name = "CrowdLabel_" + teamId;

        TextMeshProUGUI label = go.GetComponent<TextMeshProUGUI>();
        if (label == null)
        {
            throw new InvalidOperationException("[HudRoot] CrowdLabel 프리팹 root에 TextMeshProUGUI가 없습니다.");
        }

        // 폰트를 먼저 지정한 뒤 팀 material을 지정한다(TMP는 font 지정 시 material을 폰트 기본값으로 되돌린다).
        label.font = _fontAsset;
        label.fontSharedMaterial = _labelMaterials[teamId];

        _labels[teamId] = label;
        _labelLeaders[teamId] = leader;

        SetLabelText(teamId, "1");
        go.SetActive(false);
    }

    private void CreateLabelMaterials()
    {
        for (int team = 0; team < MaxTeams; team++)
        {
            Material material = new Material(_fontAsset.material)
            {
                name = "CrowdLabelMaterial_Team" + team,
                hideFlags = HideFlags.DontSave
            };

            material.EnableKeyword(ShaderUtilities.Keyword_Outline);
            material.SetColor(ShaderUtilities.ID_FaceColor, _crowdCountTextStyle.FaceColor);
            material.SetColor(ShaderUtilities.ID_OutlineColor, GetTeamColor(team));
            material.SetFloat(ShaderUtilities.ID_OutlineWidth, _crowdCountTextStyle.OutlineWidth);
            _labelMaterials[team] = material;
        }
    }

    private void DestroyLabelMaterials()
    {
        for (int team = 0; team < MaxTeams; team++)
        {
            Material material = _labelMaterials[team];
            _labelMaterials[team] = null;

            if (material != null)
            {
                Destroy(material);
            }
        }
    }

    private void SetLabelText(int teamId, string value)
    {
        TextMeshProUGUI label = _labels[teamId];
        if (label != null)
        {
            label.text = GetLabelText(teamId, value);
        }
    }

    private string GetLabelText(int teamId, string value)
    {
        if (teamId != MatchRules.PlayerTeam)
        {
            return value;
        }

        string arrowColor = ColorUtility.ToHtmlStringRGBA(GetTeamColor(teamId));
        return value + "\n<color=#" + arrowColor + ">▼</color>";
    }

    private void DestroyLabel(int teamId)
    {
        TextMeshProUGUI label = _labels[teamId];
        _labels[teamId] = null;
        _labelLeaders[teamId] = null;

        if (label != null)
        {
            label.gameObject.SetActive(false);
            Destroy(label.gameObject);
        }
    }

    private void DestroyRivalMarker(int teamId)
    {
        GameObject markerGo = _markerGos[teamId];
        _markerGos[teamId] = null;
        _markerArrows[teamId] = null;
        _markerCounts[teamId] = null;

        if (markerGo != null)
        {
            markerGo.SetActive(false);
            Destroy(markerGo);
        }
    }

    private void DestroyCrowdVisuals(int teamId)
    {
        DestroyRivalMarker(teamId);
        DestroyLabel(teamId);
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
