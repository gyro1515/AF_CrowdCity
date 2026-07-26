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

    /// <summary>
    /// 팔로워 분리 이웃 조회의 논리-cell 후보 방문 예산(cap)이다. 0이면 비활성(정확한 QueryCircle 경로, 기본값).
    /// </summary>
    [UnityEngine.Tooltip("팔로워 분리 이웃 조회의 후보 방문 예산(cap). 0=비활성(정확 조회, 기본값). >0이면 밀집 조회를 스캔 순서 첫 예산개 매칭 후보로 절단.")]
    public int SeparationVisitBudget;

    /// <summary>
    /// 전투 전향율을 접촉 pair 수 가중 대신 접촉만 있으면 최대(ConvertPerSecond)로 고정하는 토글이다. 기본값 false(기존 접촉 pair 가중 규칙).
    /// </summary>
    [UnityEngine.Tooltip("전투 전향율 flat 토글. OFF(false)=기존 규칙(rate = ConvertPerSecond * clamp01(접촉 pair 수 / PairNormalizer)). ON(true)=접촉이 하나라도 있으면 clamp01=1.0로 보고 최대 전향율(ConvertPerSecond)을 적용하며 접촉 pair 수 계수를 계산하지 않는다.")]
    public bool CombatFlatConvertRate;

    /// <summary>
    /// 초당 전향 수를 고정값 대신 이긴 팀의 틱 시작 인원수에 비례시키는 동적 전향율 토글이다. 기본값 false(고정 ConvertPerSecond).
    /// </summary>
    [UnityEngine.Tooltip("동적 전향율 토글. OFF(false)=고정 전향율(ConvertPerSecond)을 쓴다. ON(true)=이긴 팀의 틱 시작 인원수 * ConvertPerSecondPerMember를 전향율로 쓰고 ConvertPerSecond는 쓰지 않는다(이긴 쪽은 항상 더 큰 팀이므로 커질수록 더 빨리 빼앗는 눈덩이 효과가 의도된 것이다).")]
    public bool UseDynamicConvertRate;

    /// <summary>
    /// UseDynamicConvertRate=true일 때 이긴 팀 member 1명당 붙는 초당 전향 수(나눗셈이 아니라 곱셈 계수)다. 기본값 0.1(인원수 ÷ 10과 같음).
    /// </summary>
    [UnityEngine.Tooltip("동적 전향율 계수(배수). 이긴 팀의 틱 시작 인원수 1명당 초당 전향 수이며, UseDynamicConvertRate=false면 쓰이지 않는다. 0이면 전향이 일어나지 않는다 — 단 이는 RateLimitConversion=ON일 때만 성립한다. RateLimitConversion=OFF면 전향 예산 게이트 자체가 없어(접촉 즉시 전향) 이 값도, UseDynamicConvertRate 토글도 아무 효과가 없다.")]
    public float ConvertPerSecondPerMember;
}
