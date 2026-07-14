using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// GameScene의 유일한 씬 배치 진입점이다.
/// 참조 검증, GameSession/GameplayRoot 조립, 재시작 요청 실행, 해체 순서만 담당한다.
/// </summary>
public sealed class GameSceneController : MonoBehaviour
{
    // Editor setup(GameSceneSetup)이 SerializedObject로 이름 기반 배선하므로 필드 이름을 바꾸지 않는다.
    [SerializeField] private GameConfigSO config;
    [SerializeField] private GameObject humanPrefab;    // Assets/@Project/Human/Prefabs/Human.prefab (editor setup이 구성된 씬 템플릿에서 생성)
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

        if (humanPrefab == null)
        {
            Debug.LogError("[GameSceneController] humanPrefab 참조가 비어 있습니다.", this);
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

        GameObject gameplayRootGo = new GameObject("GameplayRoot");
        gameplayRootGo.transform.SetParent(transform, false);
        _gameplayRoot = gameplayRootGo.AddComponent<GameplayRoot>();
        _gameplayRoot.Initialize(_session, config, humanPrefab, mainCamera, cityRoot);
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
