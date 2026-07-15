using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Crowd feature가 소유하는 완전 수동형 단위 leaf/view다.
/// CrowdRoot의 직접 호출로만 구동되며 상위 참조, 이벤트, bus 구독, Update를 전혀 갖지 않는다.
/// </summary>
public sealed class Human : MonoBehaviour
{
    private static readonly int WalkStateHash = Animator.StringToHash("HumanWalk");

    private const float MaxAnimatorSpeed = 1.5f;

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
