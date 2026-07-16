using System.Runtime.CompilerServices;

/// <summary>
/// Phase C 단계 0 oracle 스냅샷 전용 무침습 기록기다(Editor 헤드리스 harness 전용).
/// CrowdSimProfiler와 동일한 계약: 기본값(Enabled=false)에서는 Record* 호출이 bool 분기 하나만
/// 수행하고 즉시 반환하므로 시뮬레이션 값·RNG 소비·판정/이동 순서·이벤트 발행에 어떤 영향도 주지 않는다(결정론 불변).
/// 활성화되어도 이 클래스는 static 카운터에만 누적할 뿐 커널/버퍼/그리드/RNG 상태를 절대 만지지 않는다.
/// 즉 CC.Move 유지본의 "현행 동작 그대로"를 관찰만 한다(oracle의 정의).
///
/// 기록 대상(단계 0 요구):
///   - 중립 배회 repick 발생 수 / 수락 / 실패(모든 후보 막힘) 수
///   - _rng(중립 배회 전용) tick당 draw 수(= repick마다 timer 1 + heading 시도 수 합)
///   - 라이벌 AI 결정 수 / 벽 회피로 heading이 바뀐 수
/// per-agent 위치·실제 변위·이벤트 transcript는 harness가 (accessor·EventManager 구독으로) 직접 수집한다.
/// </summary>
public static class CrowdOracleRecorder
{
    /// <summary>기록 활성화 플래그다. false(기본/프로덕션/테스트)면 Record*가 no-op다.</summary>
    public static bool Enabled;

    // ---- 중립 배회(_rng) tick 누적 ----
    /// <summary>이번 tick에 repick이 호출된 중립 수다.</summary>
    public static int WanderRepickCount;

    /// <summary>이번 tick에 새 heading이 수락된 repick 수다(막히지 않은 후보를 찾음).</summary>
    public static int WanderAcceptedCount;

    /// <summary>이번 tick에 모든 후보(최대 8)가 막혀 기존 heading을 유지한 repick 수다.</summary>
    public static int WanderFailedCount;

    /// <summary>이번 tick에 _rng(중립 배회 전용)에서 소비된 draw 수다(repick마다 timer 1 + heading 시도 수).</summary>
    public static int RngDrawsThisTick;

    // ---- 라이벌 AI 결정 tick 누적 ----
    /// <summary>이번 tick에 내려진 라이벌 heading 결정 수다(AI decide interval에 걸린 라이벌 수).</summary>
    public static int RivalDecisions;

    /// <summary>이번 tick에 벽 회피로 원하는 heading과 다른 heading이 선택된 결정 수다.</summary>
    public static int RivalHeadingChanged;

    /// <summary>tick 누적치를 모두 0으로 초기화한다. harness가 각 SimTick 직전에 호출한다.</summary>
    public static void ResetTick()
    {
        WanderRepickCount = 0;
        WanderAcceptedCount = 0;
        WanderFailedCount = 0;
        RngDrawsThisTick = 0;
        RivalDecisions = 0;
        RivalHeadingChanged = 0;
    }

    /// <summary>
    /// 중립 배회 repick 1회를 기록한다(CrowdRoot.RepickWanderHeading 말미에서 호출).
    /// headingDraws는 이번 repick에서 소비한 heading 후보 draw 수다(수락 시 attempt+1, 전부 실패 시 8).
    /// 이 호출은 이미 소비된 값만 관찰하며 _rng를 추가로 소비하지 않는다.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordWanderRepick(int agentIndex, int headingDraws, bool accepted)
    {
        if (!Enabled)
        {
            return;
        }

        WanderRepickCount++;
        RngDrawsThisTick += 1 + headingDraws; // timer NextDouble 1회 + heading NextDouble headingDraws회.
        if (accepted)
        {
            WanderAcceptedCount++;
        }
        else
        {
            WanderFailedCount++;
        }
    }

    /// <summary>
    /// 라이벌 AI heading 결정 1회를 기록한다(RivalAiDriver가 ApplyWallAvoidance 결과 직후 호출).
    /// 벽 회피 raycast의 hit-miss 효과는 desiredDeg != finalDeg 여부로 관찰한다(내부 분기·_random 미변경).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void RecordRivalDecision(int teamId, float desiredDeg, float finalDeg)
    {
        if (!Enabled)
        {
            return;
        }

        RivalDecisions++;
        if (finalDeg != desiredDeg)
        {
            RivalHeadingChanged++;
        }
    }
}
