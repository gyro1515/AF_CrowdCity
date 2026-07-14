/// <summary>
/// 특정 crowd의 인원 수가 실제로 바뀐 뒤 발행되는 과거형 사실이다.
/// CrowdRoot가 SimTick 마지막 단계에서 crowd당 tick마다 최대 1회, 값이 바뀐 경우에만 발행한다.
/// 구독자는 HudRoot, CameraRoot, GameSession 같은 boundary 객체로 한정한다.
/// </summary>
public readonly struct CrowdCountChangedEvent
{
    /// <summary>
    /// 인원 수가 바뀐 crowd의 team id다. 0은 player, 1..3은 rival이다.
    /// </summary>
    public readonly int CrowdId;

    /// <summary>
    /// 변경 후 인원 수다. leader를 포함하므로 leader 혼자인 crowd는 1이다.
    /// </summary>
    public readonly int MemberCount;

    /// <summary>
    /// 이미 확정된 인원 수 변경 사실로 payload를 생성한다.
    /// </summary>
    public CrowdCountChangedEvent(int crowdId, int memberCount)
    {
        CrowdId = crowdId;
        MemberCount = memberCount;
    }
}

/// <summary>
/// 특정 crowd가 제거된 뒤 발행되는 과거형 사실이다.
/// CrowdRoot가 SimTick 마지막 단계에서 모든 CrowdCountChangedEvent 발행 이후,
/// player 제거를 먼저 그리고 나머지를 team id 오름차순으로 발행한다.
/// 구독자는 GameSession, HudRoot, CameraRoot 같은 boundary 객체로 한정한다.
/// </summary>
public readonly struct CrowdEliminatedEvent
{
    /// <summary>
    /// 제거된 crowd의 team id다.
    /// </summary>
    public readonly int CrowdId;

    /// <summary>
    /// 제거를 확정한 crowd의 team id다. leader를 흡수한 더 큰 crowd를 가리킨다.
    /// </summary>
    public readonly int ByCrowdId;

    /// <summary>
    /// 이미 확정된 crowd 제거 사실로 payload를 생성한다.
    /// </summary>
    public CrowdEliminatedEvent(int crowdId, int byCrowdId)
    {
        CrowdId = crowdId;
        ByCrowdId = byCrowdId;
    }
}
