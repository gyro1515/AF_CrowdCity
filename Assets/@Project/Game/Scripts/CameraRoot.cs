using System;
using UnityEngine;

/// <summary>
/// Main Camera를 소유하고 구동하는 Game feature root다.
/// 플레이어 leader를 고정 pitch/yaw orbit에서 SmoothDamp로 추적하고,
/// 플레이어 인원 수에 따라 거리를 조절하며, 플레이어 제거 시 독립 pose snapshot을 latch한다.
/// Bus handler는 field만 갱신하고 카메라 계산은 전부 LateUpdate에서 수행한다.
/// </summary>
public sealed class CameraRoot : MonoBehaviour
{
    private Camera _camera;
    private Transform _cameraTransform;
    private GameConfigSO _config;
    private Transform _target;

    // 구독 token: 같은 delegate 인스턴스를 캐시해 Shutdown에서 Unsubscribe로 해제한다.
    private Action<CrowdCountChangedEvent> _onCrowdCountChanged;
    private Action<CrowdEliminatedEvent> _onCrowdEliminated;
    private bool _subscribed;

    // 고정 orbit 자세(pitch=CamPitchDeg, yaw=0). Initialize에서 한 번만 계산한다.
    private Quaternion _orbitRotation;
    private Vector3 _orbitBack;

    // Handler가 갱신하는 field. 카메라 계산에는 쓰지 않고 값만 저장한다.
    private int _playerCount = 1;
    private bool _latchRequested;

    // Latch 상태: 플레이어 제거 시점의 독립 pose snapshot이다(leader transform과 무관).
    private bool _latched;
    private Vector3 _latchedPosition;
    private Quaternion _latchedRotation;

    private Vector3 _followVelocity;

    /// <summary>
    /// 카메라와 설정을 받아 초기화하고 두 bus 이벤트를 구독한다.
    /// 구독 delegate는 field에 보관해 Shutdown과 같은 lifecycle에서 해제한다.
    /// </summary>
    public void Initialize(Camera camera, GameConfigSO config)
    {
        _camera = camera;
        _cameraTransform = camera.transform;
        _config = config;

        _orbitRotation = Quaternion.Euler(config.CamPitchDeg, 0f, 0f);
        _orbitBack = _orbitRotation * Vector3.back;

        if (!_subscribed)
        {
            _onCrowdCountChanged = OnCrowdCountChanged;
            _onCrowdEliminated = OnCrowdEliminated;
            EventManager.GetSubscriber<CrowdCountChangedEvent>().Subscribe(_onCrowdCountChanged);
            EventManager.GetSubscriber<CrowdEliminatedEvent>().Subscribe(_onCrowdEliminated);
            _subscribed = true;
        }
    }

    /// <summary>
    /// 추적할 플레이어 leader transform을 지정한다.
    /// null이거나 비활성인 동안 카메라는 현재 pose를 유지한다.
    /// </summary>
    public void SetTarget(Transform playerLeader)
    {
        _target = playerLeader;
    }

    /// <summary>
    /// 두 bus 구독을 해제한다. 여러 번 호출해도 안전하다.
    /// GameplayRoot.Shutdown이 호출하며 OnDestroy는 안전망으로만 쓴다.
    /// </summary>
    public void Shutdown()
    {
        if (!_subscribed)
        {
            return;
        }

        EventManager.GetSubscriber<CrowdCountChangedEvent>().Unsubscribe(_onCrowdCountChanged);
        EventManager.GetSubscriber<CrowdEliminatedEvent>().Unsubscribe(_onCrowdEliminated);
        _subscribed = false;
    }

    private void OnDestroy()
    {
        // 안전망: 정상 경로에서는 GameplayRoot.Shutdown이 이미 해제했다.
        Shutdown();
    }

    private void LateUpdate()
    {
        if (_camera == null)
        {
            return;
        }

        // 플레이어 제거가 handler에서 요청되면 다음 LateUpdate에서 현재 pose를 latch한다.
        if (_latchRequested && !_latched)
        {
            _latchedPosition = _cameraTransform.position;
            _latchedRotation = _cameraTransform.rotation;
            _latched = true;
            _target = null;
        }

        if (_latched)
        {
            // Leader transform은 eliminator를 따라 이동하므로 저장된 snapshot을 유지한다.
            _cameraTransform.SetPositionAndRotation(_latchedPosition, _latchedRotation);
            return;
        }

        Transform target = _target;
        if (target == null || !target.gameObject.activeInHierarchy)
        {
            return;
        }

        float distance = Mathf.Clamp(
            _config.CamBaseDistance + _config.CamDistancePerSqrtCount * Mathf.Sqrt(_playerCount),
            _config.CamBaseDistance,
            _config.CamMaxDistance);

        Vector3 desired = target.position + _orbitBack * distance;
        Vector3 smoothed = Vector3.SmoothDamp(
            _cameraTransform.position, desired, ref _followVelocity, _config.CamFollowSmoothTime);
        _cameraTransform.SetPositionAndRotation(smoothed, _orbitRotation);
    }

    private void OnCrowdCountChanged(CrowdCountChangedEvent e)
    {
        // Handler는 zoom 목표 field만 갱신한다. 거리 계산은 LateUpdate에서 한다.
        if (e.CrowdId == MatchRules.PlayerTeam)
        {
            _playerCount = e.MemberCount;
        }
    }

    private void OnCrowdEliminated(CrowdEliminatedEvent e)
    {
        // Handler는 latch flag만 세운다. Pose snapshot은 LateUpdate에서 저장한다.
        if (e.CrowdId == MatchRules.PlayerTeam)
        {
            _latchRequested = true;
        }
    }
}
