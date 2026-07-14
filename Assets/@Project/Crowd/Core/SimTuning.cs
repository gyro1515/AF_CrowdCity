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
    public float RecruitRadius;

    /// <summary>
    /// 서로 다른 crowd의 member 쌍이 전투 접촉으로 판정되는 반경(m)이다. 기본값 1.0.
    /// </summary>
    public float CombatRadius;

    /// <summary>
    /// 접촉이 최대일 때 큰 crowd가 작은 crowd에서 초당 전향시키는 member 수이다. 기본값 10.
    /// </summary>
    public float ConvertPerSecond;

    /// <summary>
    /// 전향 속도 배율 clamp01(touchingPairs / PairNormalizer)의 기준 접촉 pair 수이다. 기본값 8.
    /// </summary>
    public int PairNormalizer;
}
