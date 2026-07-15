using System;

/// <summary>
/// Simulation kernel의 recruit/combat 규칙이 사용하는 튜닝 값 묶음이다.
/// 원본 balance 값은 GameConfigSO가 소유하며, kernel은 이 struct를 읽기 전용 snapshot으로 전달받는다.
/// </summary>
[Serializable]
public struct SimTuning
{
    /// <summary>
    /// 중립 agent가 crowd에 합류하는 판정 반경(m)이다. 기본값 1.2.
    /// </summary>
    [UnityEngine.Tooltip("중립 agent가 crowd에 합류(영입)되는 판정 반경(m). 기본값 1.2.")]
    public float RecruitRadius;

    /// <summary>
    /// 서로 다른 crowd의 member 쌍이 전투 접촉으로 판정되는 반경(m)이다. 기본값 1.0.
    /// </summary>
    [UnityEngine.Tooltip("서로 다른 crowd의 member 쌍이 전투 접촉으로 판정되는 반경(m). 기본값 1.0.")]
    public float CombatRadius;

    /// <summary>
    /// 리더가 국소적으로 홀로인지(주변 아군 호위 유무)를 판정하는 반경(m)이다. CombatRadius(전투/적 접촉)와 독립이다. 기본값 0.7.
    /// </summary>
    [UnityEngine.Tooltip("리더가 혼자인지(주변 아군 호위 유무) 판정하는 반경. CombatRadius(전투/적 접촉)와 별개.")]
    public float LeaderAloneRadius;

    /// <summary>
    /// 접촉이 최대일 때 큰 crowd가 작은 crowd에서 초당 전향시키는 member 수이다. 기본값 10.
    /// </summary>
    [UnityEngine.Tooltip("접촉이 최대일 때 큰 crowd가 작은 crowd에서 초당 전향시키는 member 수. 기본값 10.")]
    public float ConvertPerSecond;

    /// <summary>
    /// 전향 속도 배율 clamp01(touchingPairs / PairNormalizer)의 기준 접촉 pair 수이다. 기본값 8.
    /// </summary>
    [UnityEngine.Tooltip("전향 속도 배율 clamp01(접촉 pair 수 / PairNormalizer)의 기준이 되는 접촉 pair 수. 기본값 8.")]
    public int PairNormalizer;

    /// <summary>전투 전향의 rate limit(점진 전향) 토글이다. 기본값 false(접촉 즉시 전향).</summary>
    [UnityEngine.Tooltip("전투 전향 rate limit 토글. OFF(false)=접촉 즉시 전향(반경 안 더 작은 crowd의 non-leader member가 한 tick에 모두 전향). ON(true)=기존 점진 전향(ConvertPerSecond*clamp01(접촉 pair/PairNormalizer)*dt 예산만큼 tick당 전향).")]
    public bool RateLimitConversion;

    /// <summary>
    /// 중립이 스폰될 수 있는 최대 uniform 스케일이다(worst-case pair). resolver가 스케일 인지 접촉/영입 질의를 이 값만큼 넓혀
    /// 큰 유닛 쌍을 놓치지 않게 한다. source of truth는 GameConfigSO.NeutralMaxScale이며 CrowdRoot가 이 값을 채운다.
    /// bare default(SimTuning)는 0을 주지만 resolver는 Mathf.Max(1f, MaxScale)로 가드해 질의를 축소하지 않는다.
    /// </summary>
    [UnityEngine.Tooltip("중립 최대 스케일. resolver 질의 반경을 이 값만큼 넓혀 큰 유닛 쌍을 놓치지 않게 한다. CrowdRoot가 NeutralMaxScale로 채운다.")]
    public float MaxScale;

    /// <summary>리더 제거를 국소 수적 판정으로 게이트하는 리더 보호 토글이다. 기본값 true(리더 보호 ON).</summary>
    // source of truth는 GameConfigSO/asset(config.Sim)이다. bare default(SimTuning)은 false를 주지만,
    // 프로덕션은 항상 config.Sim으로 이 struct를 전달받으므로 실제 기본값(true)은 asset이 결정한다.
    [UnityEngine.Tooltip("리더 보호 토글. ON(true)=리더는 국소 수적으로 열세(CombatRadius 안 적 국소 수 > LeaderAloneRadius 안 아군 국소 수)일 때만 제거(map-separated straggler로 전역 count가 부풀어도 코너에 몰린 리더는 제거됨). OFF(false)=국소 동수(>=)에도 리더가 접촉 변환·제거됨.")]
    public bool LeaderProtection;
}
