using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// GameScene의 유일한 씬 배치 진입점이다.
/// 참조 검증, GameSession/GameplayRoot 조립, 재시작 요청 실행, 해체 순서만 담당한다.
/// </summary>
public sealed class GameSceneController : MonoBehaviour
{
    // Editor setup(GameSceneSetup)이 SerializedObject로 이름 기반 배선하므로 필드 이름을 바꾸지 않는다.
    // 단일 소비자 asset(crowdCountTextStyle/buildingOccludedMaterial)은 각 feature root 프리팹에 직렬화 저작돼
    // 여기서 드릴링하지 않는다. 씬 오브젝트 참조(mainCamera/cityRoot)와 공용 config만 주입한다.
    [SerializeField] private GameConfigSO config;
    [SerializeField] private Camera mainCamera;
    [SerializeField] private Transform cityRoot;        // GameArea/City

    private GameSession _session;
    private GameplayRoot _gameplayRoot;

#if UNITY_EDITOR
    /// <summary>
    /// play-smoke 검증이 세션 상태를 읽기 위한 Editor 전용 hook이다.
    /// </summary>
    public GameSession DebugSession
    {
        get { return _session; }
    }
#endif

    private void Awake()
    {
        bool valid = true;

        if (config == null)
        {
            Debug.LogError("[GameSceneController] config 참조가 비어 있습니다.", this);
            valid = false;
        }

        if (mainCamera == null)
        {
            Debug.LogError("[GameSceneController] mainCamera 참조가 비어 있습니다.", this);
            valid = false;
        }

        if (cityRoot == null)
        {
            Debug.LogError("[GameSceneController] cityRoot 참조가 비어 있습니다.", this);
            valid = false;
        }

        if (!valid)
        {
            enabled = false;
            return;
        }
    }

    private void Start()
    {
        _session = new GameSession(config);

        // GameplayRoot는 스크립트 사전부착 프리팹을 ResourceLoader.LoadPrefab(클래스 이름) + Instantiate로 생성한다(소유권/조립 체인의 시작).
        // 프리팹은 active로 저작돼 있고 GameplayRoot.Awake/OnEnable은 의존성-free이므로, 배선(Initialize) 이전 활성 상태가 안전하다.
        // 프리팹/컴포넌트 취득 실패 시 ResourceLoader가 명확히 예외를 던진다(Instantiate 전 검사이므로 부분 clone이 남지 않는다).
        GameplayRoot prefabRoot = ResourceLoader.LoadPrefab<GameplayRoot>();
        _gameplayRoot = Instantiate(prefabRoot, transform, false);
        _gameplayRoot.gameObject.name = "GameplayRoot";
        GameObject gameplayRootGo = _gameplayRoot.gameObject;

        try
        {
            _gameplayRoot.Initialize(_session, config, mainCamera, cityRoot);
        }
        catch
        {
            // Initialize가 예외를 던지면 방금 만든 GameplayRoot clone을 파괴하고 다시 던진다.
            // GameplayRoot.Initialize는 실패 시 자기 child root를 이미 역순 정리하고 나오므로 여기서는 root clone만 파괴한다.
            Destroy(gameplayRootGo);
            _gameplayRoot = null;
            throw;
        }

        _gameplayRoot.ReloadRequested += OnReloadRequested;
    }

    private void OnDestroy()
    {
        // 고정된 해체 순서: ① 자신의 바인딩 해제 ② GameplayRoot 해체 ③ 세션 Dispose는 마지막.
        // 세션이 root 해체 중 발생하는 늦은 이벤트까지 처리할 수 있도록 root보다 오래 살린다.
        if (_gameplayRoot != null)
        {
            _gameplayRoot.ReloadRequested -= OnReloadRequested;
            _gameplayRoot.Shutdown();
        }

        if (_session != null)
        {
            _session.Dispose();
            _session = null;
        }
    }

    private void OnReloadRequested()
    {
        // 기계적인 씬 단위 재시작만 실행한다. 판정(Finished 게이트)은 GameplayRoot가 이미 끝냈다.
        SceneManager.LoadScene(gameObject.scene.buildIndex);
    }
}
