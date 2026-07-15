using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Main Camera를 소유하고 구동하는 Game feature root다.
/// 플레이어 leader를 고정 pitch/yaw orbit에서 SmoothDamp로 추적하고,
/// 플레이어 인원 수에 따라 거리를 조절하며, 플레이어 제거 시 독립 pose snapshot을 latch한다.
/// Bus handler는 field만 갱신하고 카메라 계산은 전부 LateUpdate에서 수행한다.
/// </summary>
public sealed class CameraRoot : MonoBehaviour
{
    private const string BuildingsChildName = "Buildings";
    private const int ExpectedBuildingCount = 37;
    private const int InitialOcclusionHitCapacity = 64;
    private const int MaxOcclusionHitCapacity = 512;
    private const float OcclusionTargetHeight = 0.9f;
    private const float OcclusionProbeRadius = 0.35f;

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

    // 건물별 collider -> 원본/투명 renderer 상태. 초기화 이후 매 프레임 재할당하지 않는다.
    private Dictionary<Collider, BuildingOccluderState> _buildingByCollider;
    private BuildingOccluderState[] _buildingStates;
    private HashSet<BuildingOccluderState> _activeOccluders;
    private HashSet<BuildingOccluderState> _nextOccluders;
    private RaycastHit[] _occlusionHits;
    private bool _occlusionBufferGrowthLogged;
    private bool _occlusionBufferOverflowLogged;
    private Material _runtimeOccludedMaterial;

    private sealed class BuildingOcclusionRegistration
    {
        internal Dictionary<Collider, BuildingOccluderState> ByCollider;
        internal BuildingOccluderState[] States;
        internal HashSet<BuildingOccluderState> Active;
        internal HashSet<BuildingOccluderState> Next;
        internal RaycastHit[] Hits;
    }

    private sealed class BuildingOccluderState
    {
        private readonly Renderer[] _renderers;
        private readonly Material[][] _originalMaterials;
        private readonly Material[][] _occludedMaterials;
        private bool _isOccluded;

        internal BuildingOccluderState(Renderer[] renderers, Collider[] colliders, Material occludedMaterial)
        {
            _renderers = renderers;
            Colliders = colliders;
            _originalMaterials = new Material[renderers.Length][];
            _occludedMaterials = new Material[renderers.Length][];

            for (int i = 0; i < renderers.Length; i++)
            {
                Material[] original = renderers[i].sharedMaterials;
                _originalMaterials[i] = original;

                Material[] occluded = new Material[original.Length];
                for (int m = 0; m < occluded.Length; m++)
                {
                    occluded[m] = occludedMaterial;
                }

                _occludedMaterials[i] = occluded;
            }
        }

        internal Collider[] Colliders { get; }

        internal void SetOccluded(bool occluded)
        {
            if (_isOccluded == occluded)
            {
                return;
            }

            _isOccluded = occluded;
            Material[][] materials = occluded ? _occludedMaterials : _originalMaterials;
            for (int i = 0; i < _renderers.Length; i++)
            {
                Renderer renderer = _renderers[i];
                if (renderer != null)
                {
                    // CameraRoot가 소유하는 runtime material과 원본 asset 참조만 교체한다.
                    renderer.sharedMaterials = materials[i];
                }
            }
        }
    }

    /// <summary>
    /// 카메라와 설정을 받아 초기화하고 두 bus 이벤트를 구독한다.
    /// 구독 delegate는 field에 보관해 Shutdown과 같은 lifecycle에서 해제한다.
    /// </summary>
    public void Initialize(
        Camera camera,
        GameConfigSO config,
        Transform cityRoot,
        Material buildingOccludedMaterial)
    {
        if (camera == null)
        {
            throw new ArgumentNullException(nameof(camera));
        }

        if (config == null)
        {
            throw new ArgumentNullException(nameof(config));
        }

        if (cityRoot == null)
        {
            throw new ArgumentNullException(nameof(cityRoot));
        }

        if (buildingOccludedMaterial == null)
        {
            throw new ArgumentNullException(nameof(buildingOccludedMaterial));
        }

        // 같은 component를 새 session에서 재사용할 수 있으므로 이전 material을 원복하고 runtime clone을 해제한다.
        ReleaseBuildingOcclusion();
        ResetSessionState();

        _runtimeOccludedMaterial = CreateRuntimeOccludedMaterial(buildingOccludedMaterial);
        BuildingOcclusionRegistration registration;
        try
        {
            registration = CreateBuildingOcclusionRegistration(cityRoot, _runtimeOccludedMaterial);
        }
        catch
        {
            DestroyRuntimeOccludedMaterial();
            throw;
        }

        _camera = camera;
        _cameraTransform = camera.transform;
        _config = config;

        _orbitRotation = Quaternion.Euler(config.CamPitchDeg, 0f, 0f);
        _orbitBack = _orbitRotation * Vector3.back;

        _buildingByCollider = registration.ByCollider;
        _buildingStates = registration.States;
        _activeOccluders = registration.Active;
        _nextOccluders = registration.Next;
        _occlusionHits = registration.Hits;

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
        if (_target != playerLeader)
        {
            RestoreAllBuildingMaterials();
        }

        _target = playerLeader;
    }

    /// <summary>
    /// 두 bus 구독을 해제한다. 여러 번 호출해도 안전하다.
    /// GameplayRoot.Shutdown이 호출하며 OnDestroy는 안전망으로만 쓴다.
    /// </summary>
    public void Shutdown()
    {
        ReleaseBuildingOcclusion();

        if (_subscribed)
        {
            EventManager.GetSubscriber<CrowdCountChangedEvent>().Unsubscribe(_onCrowdCountChanged);
            EventManager.GetSubscriber<CrowdEliminatedEvent>().Unsubscribe(_onCrowdEliminated);
        }

        _subscribed = false;
        _onCrowdCountChanged = null;
        _onCrowdEliminated = null;
        ResetSessionState();
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
            RestoreAllBuildingMaterials();
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
            RestoreAllBuildingMaterials();
            _cameraTransform.SetPositionAndRotation(_latchedPosition, _latchedRotation);
            return;
        }

        Transform target = _target;
        if (target == null || !target.gameObject.activeInHierarchy)
        {
            RestoreAllBuildingMaterials();
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

        UpdateBuildingOcclusion(target);
    }

    private static BuildingOcclusionRegistration CreateBuildingOcclusionRegistration(
        Transform cityRoot, Material occludedMaterial)
    {
        Transform buildingsRoot = cityRoot.Find(BuildingsChildName);
        if (buildingsRoot == null)
        {
            throw new InvalidOperationException($"CameraRoot: cityRoot 아래 '{BuildingsChildName}'이 없습니다.");
        }

        if (buildingsRoot.childCount != ExpectedBuildingCount)
        {
            throw new InvalidOperationException(
                $"CameraRoot: 개별 건물 수가 {buildingsRoot.childCount}개입니다. 기대값은 {ExpectedBuildingCount}개입니다.");
        }

        BuildingOcclusionRegistration registration = new BuildingOcclusionRegistration
        {
            States = new BuildingOccluderState[ExpectedBuildingCount],
            ByCollider = new Dictionary<Collider, BuildingOccluderState>(ExpectedBuildingCount),
            Active = new HashSet<BuildingOccluderState>(ExpectedBuildingCount),
            Next = new HashSet<BuildingOccluderState>(ExpectedBuildingCount),
            Hits = new RaycastHit[InitialOcclusionHitCapacity],
        };

        for (int i = 0; i < buildingsRoot.childCount; i++)
        {
            Transform building = buildingsRoot.GetChild(i);
            Renderer[] renderers = building.GetComponentsInChildren<Renderer>(true);
            Collider[] colliders = building.GetComponentsInChildren<Collider>(true);
            if (renderers.Length == 0 || colliders.Length == 0)
            {
                throw new InvalidOperationException(
                    $"CameraRoot: '{building.name}'에는 Renderer와 Collider가 모두 필요합니다.");
            }

            BuildingOccluderState state = new BuildingOccluderState(renderers, colliders, occludedMaterial);
            registration.States[i] = state;
            for (int c = 0; c < colliders.Length; c++)
            {
                Collider collider = colliders[c];
                if (collider == null || registration.ByCollider.ContainsKey(collider))
                {
                    throw new InvalidOperationException(
                        $"CameraRoot: '{building.name}'의 Collider 등록이 비어 있거나 중복됐습니다.");
                }

                registration.ByCollider.Add(collider, state);
            }
        }

        return registration;
    }

    private static Material CreateRuntimeOccludedMaterial(Material source)
    {
        Material runtimeMaterial = new Material(source)
        {
            name = source.name + " (CameraRoot Runtime)",
            hideFlags = HideFlags.DontSave,
        };
        runtimeMaterial.SetShaderPassEnabled("ShadowCaster", true);
        return runtimeMaterial;
    }

    private void UpdateBuildingOcclusion(Transform target)
    {
        if (_buildingByCollider == null || _buildingStates == null || _occlusionHits == null)
        {
            return;
        }

        Vector3 origin = target.position + Vector3.up * OcclusionTargetHeight;
        Vector3 toCamera = _cameraTransform.position - origin;
        float distance = toCamera.magnitude;
        if (distance <= OcclusionProbeRadius)
        {
            RestoreAllBuildingMaterials();
            return;
        }

        Vector3 direction = toCamera / distance;
        int hitCount = SphereCastWithOverflowHandling(origin, direction, distance);
        _nextOccluders.Clear();

        if (hitCount >= 0)
        {
            for (int i = 0; i < hitCount; i++)
            {
                BuildingOccluderState state;
                if (_buildingByCollider.TryGetValue(_occlusionHits[i].collider, out state))
                {
                    _nextOccluders.Add(state);
                }
            }
        }
        else
        {
            // 극단적인 collider 밀집으로 최대 buffer도 포화되면 등록 건물만 직접 raycast해 stale 상태를 남기지 않는다.
            Ray ray = new Ray(origin, direction);
            for (int i = 0; i < _buildingStates.Length; i++)
            {
                BuildingOccluderState state = _buildingStates[i];
                Collider[] colliders = state.Colliders;
                for (int c = 0; c < colliders.Length; c++)
                {
                    Collider collider = colliders[c];
                    RaycastHit ignored;
                    if (collider != null && collider.enabled && collider.gameObject.activeInHierarchy &&
                        collider.Raycast(ray, out ignored, distance))
                    {
                        _nextOccluders.Add(state);
                        break;
                    }
                }
            }
        }

        foreach (BuildingOccluderState state in _activeOccluders)
        {
            if (!_nextOccluders.Contains(state))
            {
                state.SetOccluded(false);
            }
        }

        foreach (BuildingOccluderState state in _nextOccluders)
        {
            if (!_activeOccluders.Contains(state))
            {
                state.SetOccluded(true);
            }
        }

        HashSet<BuildingOccluderState> previous = _activeOccluders;
        _activeOccluders = _nextOccluders;
        _nextOccluders = previous;
        _nextOccluders.Clear();
    }

    private int SphereCastWithOverflowHandling(Vector3 origin, Vector3 direction, float distance)
    {
        while (true)
        {
            int hitCount = Physics.SphereCastNonAlloc(
                origin,
                OcclusionProbeRadius,
                direction,
                _occlusionHits,
                distance,
                Physics.AllLayers,
                QueryTriggerInteraction.Ignore);

            if (hitCount < _occlusionHits.Length)
            {
                return hitCount;
            }

            if (_occlusionHits.Length >= MaxOcclusionHitCapacity)
            {
                if (!_occlusionBufferOverflowLogged)
                {
                    Debug.LogError(
                        $"[CameraRoot] 건물 차폐 physics query가 최대 {MaxOcclusionHitCapacity} hits를 초과했습니다. " +
                        "등록 건물 직접 raycast fallback을 사용합니다.",
                        this);
                    _occlusionBufferOverflowLogged = true;
                }

                return -1;
            }

            int nextCapacity = Mathf.Min(_occlusionHits.Length * 2, MaxOcclusionHitCapacity);
            Array.Resize(ref _occlusionHits, nextCapacity);
            if (!_occlusionBufferGrowthLogged)
            {
                Debug.LogWarning(
                    $"[CameraRoot] 건물 차폐 physics query buffer를 {nextCapacity} hits로 1회 확장했습니다.",
                    this);
                _occlusionBufferGrowthLogged = true;
            }
        }
    }

    private void RestoreAllBuildingMaterials()
    {
        if (_activeOccluders != null)
        {
            foreach (BuildingOccluderState state in _activeOccluders)
            {
                state.SetOccluded(false);
            }

            _activeOccluders.Clear();
        }

        if (_nextOccluders != null)
        {
            _nextOccluders.Clear();
        }
    }

    private void ClearBuildingOcclusion()
    {
        RestoreAllBuildingMaterials();
        _buildingByCollider = null;
        _buildingStates = null;
        _activeOccluders = null;
        _nextOccluders = null;
        _occlusionHits = null;
        _occlusionBufferGrowthLogged = false;
        _occlusionBufferOverflowLogged = false;
    }

    private void ReleaseBuildingOcclusion()
    {
        ClearBuildingOcclusion();
        DestroyRuntimeOccludedMaterial();
    }

    private void DestroyRuntimeOccludedMaterial()
    {
        Material material = _runtimeOccludedMaterial;
        _runtimeOccludedMaterial = null;
        if (material == null)
        {
            return;
        }

        if (Application.isPlaying)
        {
            Destroy(material);
        }
        else
        {
            DestroyImmediate(material);
        }
    }

    private void ResetSessionState()
    {
        _camera = null;
        _cameraTransform = null;
        _config = null;
        _target = null;
        _orbitRotation = Quaternion.identity;
        _orbitBack = Vector3.zero;
        _playerCount = 1;
        _latchRequested = false;
        _latched = false;
        _latchedPosition = Vector3.zero;
        _latchedRotation = Quaternion.identity;
        _followVelocity = Vector3.zero;
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
