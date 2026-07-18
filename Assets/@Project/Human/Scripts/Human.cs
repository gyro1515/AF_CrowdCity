using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Crowd feature가 소유하는 완전 수동형 단위 leaf/view다.
/// CrowdRoot의 직접 호출로만 구동되며 상위 참조, 이벤트, bus 구독, Update를 전혀 갖지 않는다.
/// </summary>
public sealed class Human : MonoBehaviour
{
    private static readonly int WalkStateHash = Animator.StringToHash("HumanWalk");

    // Animator.speed clamp 상한. GPU 경로(CrowdRoot의 phase 적분)도 같은 상한으로 clamp해 CPU/GPU 재생 속도를 일치시킨다.
    public const float MaxAnimatorSpeed = 1.5f;

    private SkinnedMeshRenderer _renderer;
    private Animator _animator;

    /// <summary>
    /// clone이 활성화된 뒤 CrowdRoot가 호출한다.
    /// SkinnedMeshRenderer와 Animator를 자식에서 찾아 캐시하고 팀 material과 그림자 casting을 적용한 뒤
    /// 걷기 상태를 무작위 normalized time offset으로 재생한다.
    /// Animator는 editor setup이 아직 적용되지 않았을 수 있으므로 없으면 조용히 건너뛴다.
    /// </summary>
    public void Init(Material teamMaterial, bool isLeader)
    {
        _renderer = GetComponentInChildren<SkinnedMeshRenderer>();
        _animator = GetComponentInChildren<Animator>();

        SetTeamMaterial(teamMaterial);
        SetLeader(isLeader);

        if (_animator != null)
        {
            // Animator는 활성화 시점에 bind되므로 clone 활성화 이후에 Play해야 유효하다.
            _animator.Play(WalkStateHash, 0, Random.value);
            _animator.speed = 0f; // 첫 드래그(Playing) 전까지 정지 포즈; 첫 Playing 틱의 SetHeadingAndSpeed가 재개
        }
    }

    /// <summary>
    /// 팀 material을 sharedMaterial 교체로 적용한다.
    /// SRP batching이 깨지지 않도록 instance를 만드는 .material은 절대 사용하지 않는다.
    /// </summary>
    public void SetTeamMaterial(Material teamMaterial)
    {
        if (_renderer != null)
        {
            _renderer.sharedMaterial = teamMaterial;
        }
    }

    /// <summary>
    /// 리더 여부에 따라 그림자 casting을 갱신한다. 리더만 그림자를 드리운다.
    /// </summary>
    public void SetLeader(bool isLeader)
    {
        if (_renderer != null)
        {
            _renderer.shadowCastingMode = isLeader ? ShadowCastingMode.On : ShadowCastingMode.Off;
        }
    }

    /// <summary>
    /// GPU 크라우드 렌더러(CrowdRenderer)가 성공적으로 초기화된 뒤 CrowdRoot가 런타임에 호출한다.
    /// 이 clone의 죽은 rig(SkinnedMeshRenderer + Animator + 전체 본 계층을 담은 root의 유일한 자식)를 파괴한다
    /// (저작 프리팹은 건드리지 않는다 — 런타임 clone 한정). 스킨/애니메이터 비용(~4.6us/유닛)과 본 transform 부하를 없애고
    /// SMR/GPU 이중 렌더를 막는다. root transform(카메라/HUD가 읽는 리더 포함)과 CharacterController는 root에 있어 그대로
    /// 남으므로 CrowdRoot가 계속 위치/회전을 구동한다. 파괴 후 캐시된 _renderer/_animator는 Unity fake-null이 되지만
    /// Human이 모든 rig 접근을 null-guard하므로 안전하다. GPU 확정은 세션 영구(토글백 없음)라 되돌릴 수 없는 파괴가 안전하다.
    /// </summary>
    public void DestroyVisualRig()
    {
        // _animator.gameObject == root의 유일한 자식(rig 조상). 이 단일 자식을 파괴하면 SMR+Animator+전체 본이 함께 사라진다.
        if (_animator != null)
        {
            Destroy(_animator.gameObject);
        }
    }

    /// <summary>
    /// Y축 회전을 지정한 heading으로 즉시 맞추고 Animator 재생 속도를 speed01로 설정한다.
    /// speed01은 0..1.5로 clamp하며 Animator가 없으면 회전만 적용하고 조용히 넘어간다.
    /// </summary>
    public void SetHeadingAndSpeed(float headingDeg, float speed01)
    {
        transform.rotation = Quaternion.Euler(0f, headingDeg, 0f);

        if (_animator != null)
        {
            _animator.speed = Mathf.Clamp(speed01, 0f, MaxAnimatorSpeed);
        }
    }
}
