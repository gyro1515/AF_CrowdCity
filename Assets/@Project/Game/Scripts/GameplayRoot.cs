using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Input/Crowd/Camera/Hud 4개 feature root를 child GameObject로 생성해 연결하고,
/// 고정 스텝(0.02초) 시뮬레이션 tick을 단독으로 구동하는 gameplay root이다.
/// 초기화 순서와 tick 순서가 여기에 고정되어 있어 Unity script 실행 순서에 의존하지 않는다.
/// </summary>
public sealed class GameplayRoot : MonoBehaviour
{
    private const float FixedStepSeconds = 0.02f;
    private const int MaxStepsPerFrame = 4;

    private GameSession _session;
    private InputRoot _inputRoot;
    private CrowdRoot _crowdRoot;
    private CameraRoot _cameraRoot;
    private HudRoot _hudRoot;

    private float _accumulator;
    private bool _initialized;
    private bool _isShutdown;
    private bool _reloadRaised;

    /// <summary>
    /// session이 Finished 상태일 때 재시작 입력이 들어오면 발생한다.
    /// scene reload 실행은 구독자(GameSceneController)의 책임이며,
    /// 발생 이후의 Update는 고정 스텝 루프를 돌지 않고 즉시 반환한다.
    /// </summary>
    public event Action ReloadRequested;

    /// <summary>
    /// 4개 root를 생성하고 고정된 순서로 초기화한다.
    /// 순서: 4개 root 생성 -> inputRoot -> crowdRoot -> cameraRoot -> hudRoot Initialize
    /// -> session.Initialize -> C# event binding -> crowdRoot.SpawnInitial
    /// -> cameraRoot.SetTarget -> hudRoot.BindLeaderLabels.
    /// 모든 bus 구독이 첫 발행(SpawnInitial) 전에 등록되도록 보장한다.
    /// </summary>
    public void Initialize(
        GameSession session,
        GameConfigSO config,
        TMPTextStyleSO crowdCountTextStyle,
        GameObject humanPrefab,
        Camera mainCamera,
        Transform cityRoot,
        Material buildingOccludedMaterial)
    {
        if (_initialized)
        {
            Debug.LogError("[GameplayRoot] Initialize가 중복 호출되어 무시합니다.");
            return;
        }

        _initialized = true;
        _session = session;

        // ① child root GameObject 4개를 먼저 전부 생성한다.
        _inputRoot = CreateChildRoot<InputRoot>("InputRoot");
        _crowdRoot = CreateChildRoot<CrowdRoot>("CrowdRoot");
        _cameraRoot = CreateChildRoot<CameraRoot>("CameraRoot");
        _hudRoot = CreateChildRoot<HudRoot>("HudRoot");

        // ② 각 root를 고정된 순서로 초기화한다. bus 구독은 전부 여기서 등록된다.
        _inputRoot.Initialize(config);
        _crowdRoot.Initialize(config, humanPrefab, cityRoot);
        _cameraRoot.Initialize(mainCamera, config, cityRoot, buildingOccludedMaterial);
        _hudRoot.Initialize(_session, config, crowdCountTextStyle, mainCamera);

        // ③ session의 bus 구독을 등록한다.
        _session.Initialize();

        // ④ C# event binding (child -> parent 알림).
        _inputRoot.FirstDrag += OnFirstDrag;
        _inputRoot.RestartTapped += OnRestartTapped;
        _session.StateChanged += OnSessionStateChanged;

        // ⑤ 첫 스폰. 모든 구독자가 이미 듣고 있는 상태에서 첫 coalesced count 발행이 일어난다.
        _crowdRoot.SpawnInitial();

        // ⑥ presentation binding.
        _cameraRoot.SetTarget(_crowdRoot.PlayerLeaderTransform);
        _hudRoot.BindLeaderLabels(BuildLeaderLabelBindings());
    }

    /// <summary>
    /// 정리를 고정된 순서로 수행한다. 여러 번 호출해도 안전하다.
    /// 순서: 자신의 C# binding 해제 -> Hud -> Camera -> Crowd -> Input
    /// Shutdown(presentation -> sim -> input) -> 생성한 child root GameObject 파괴.
    /// </summary>
    public void Shutdown()
    {
        if (_isShutdown)
        {
            return;
        }

        _isShutdown = true;

        // ① 자신이 등록한 C# binding을 해제한다. delegate 필드는 순수 C#이므로
        //    Unity object 파괴 여부와 무관하게 ReferenceEquals 기준으로 안전하게 해제한다.
        if (!ReferenceEquals(_inputRoot, null))
        {
            _inputRoot.FirstDrag -= OnFirstDrag;
            _inputRoot.RestartTapped -= OnRestartTapped;
        }

        if (_session != null)
        {
            _session.StateChanged -= OnSessionStateChanged;
        }

        ReloadRequested = null;

        // ② presentation -> sim -> input 순서로 Shutdown한다. 이미 파괴된 component는
        //    건너뛴다(각 root의 OnDestroy 안전망이 자기 정리를 이미 수행했다).
        if (_hudRoot != null)
        {
            _hudRoot.Shutdown();
        }

        if (_cameraRoot != null)
        {
            _cameraRoot.Shutdown();
        }

        if (_crowdRoot != null)
        {
            _crowdRoot.Shutdown();
        }

        if (_inputRoot != null)
        {
            _inputRoot.Shutdown();
        }

        // ③ 생성한 child root GameObject를 파괴한다.
        DestroyChildRoot(_hudRoot);
        DestroyChildRoot(_cameraRoot);
        DestroyChildRoot(_crowdRoot);
        DestroyChildRoot(_inputRoot);
    }

    private void Update()
    {
        if (!_initialized || _isShutdown)
        {
            return;
        }

        // ① 입력 폴링. single-driver 규칙 — InputRoot는 자체 Update가 없다.
        _inputRoot.Poll();

        // ② 재시작 게이트. reload가 발생한 프레임부터는 고정 스텝 루프를 돌지 않는다.
        if (_reloadRaised)
        {
            return;
        }

        // ③ 고정 스텝 accumulator (dt=0.02초, 프레임당 최대 4스텝).
        _accumulator += Time.deltaTime;

        int steps = 0;
        while (_accumulator >= FixedStepSeconds && steps < MaxStepsPerFrame)
        {
            _accumulator -= FixedStepSeconds;
            steps++;

            _crowdRoot.SetPlayerHeading(_inputRoot.HeadingDir, _inputRoot.HasHeading);
            _session.Tick(FixedStepSeconds);
            _crowdRoot.SimTick(FixedStepSeconds);
        }

        // 4스텝을 넘는 초과분은 버린다(느린 프레임에서의 시뮬레이션 나선형 지연 방지).
        if (_accumulator >= FixedStepSeconds)
        {
            _accumulator %= FixedStepSeconds;
        }
    }

    private void OnDestroy()
    {
        // 명시적 Shutdown이 호출되지 않았을 때의 마지막 안전망이다.
        Shutdown();
    }

    private void OnFirstDrag()
    {
        // Ready -> Playing 전이는 session이 자체적으로 게이트한다.
        _session.Begin();
    }

    private void OnRestartTapped()
    {
        // 재시작 정책 게이트: Finished 상태에서만 reload를 올린다.
        if (_session == null || _session.State != MatchState.Finished)
        {
            return;
        }

        _reloadRaised = true;

        Action handler = ReloadRequested;
        if (handler != null)
        {
            handler.Invoke();
        }
    }

    private void OnSessionStateChanged(MatchState state)
    {
        // 재진입 계약: CrowdRoot는 pending flag만 설정하고 다음 SimTick에서 소비한다.
        if (_crowdRoot != null)
        {
            _crowdRoot.OnMatchStateChanged(state);
        }
    }

    private T CreateChildRoot<T>(string rootName)
        where T : Component
    {
        GameObject rootObject = new GameObject(rootName);
        rootObject.transform.SetParent(transform, false);
        return rootObject.AddComponent<T>();
    }

    private List<CrowdLabelBinding> BuildLeaderLabelBindings()
    {
        IReadOnlyList<CrowdModel> crowds = _crowdRoot.Crowds;
        List<CrowdLabelBinding> bindings = new List<CrowdLabelBinding>(crowds.Count);
        for (int i = 0; i < crowds.Count; i++)
        {
            CrowdModel crowd = crowds[i];
            bindings.Add(new CrowdLabelBinding(crowd.TeamId, crowd.Leader.transform));
        }

        return bindings;
    }

    private static void DestroyChildRoot(Component root)
    {
        if (root != null)
        {
            Destroy(root.gameObject);
        }
    }
}
