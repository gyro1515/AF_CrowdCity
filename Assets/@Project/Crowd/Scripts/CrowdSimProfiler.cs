using System.Diagnostics;
using System.Runtime.CompilerServices;

/// <summary>
/// CrowdRoot.SimTick의 구간별 벽시계 시간을 누적하는 무침습 계측기다(Phase C 단계 0 baseline 전용).
/// 기본값(Enabled=false)에서는 Begin/End가 bool 분기 하나만 수행하고 즉시 반환하므로
/// 시뮬레이션 값·RNG 소비·판정 순서·이벤트 발행 순서에 어떤 영향도 주지 않는다(결정론 불변).
/// 계측은 별도 static long 배열(_accum/_start)에만 기록하며 커널/버퍼/그리드 상태를 절대 만지지 않는다.
/// Editor 헤드리스 harness(CrowdProfileHarness)만 Enabled를 켜고 결과를 읽는다.
/// </summary>
public static class CrowdSimProfiler
{
    /// <summary>
    /// SimTick 하위 구간 식별자다. Count는 배열 크기 산정용 sentinel이다.
    /// 구간은 상호 배타가 아니다(중첩·중복 존재) — 백분율/ms를 단순 합산하지 말고 항목별로 읽어야 한다.
    /// CCMove는 다섯 지점(리더 ApplyHorizontalMove, 팔로워 SDF 이동 job 대기, 팔로워 CC 폴백,
    /// 중립 SDF 이동 job 대기, 중립 CC 폴백)을 합산하는 단일 격리 버킷이며 LeaderMove/FollowerSteer/NeutralMove
    /// 안에 중첩되어 그 총합에도 포함된다. SDF ON 경로에서는 CharacterController.Move가 아니라
    /// WallSolver.Resolve(SDF 이동 해소)의 병렬 벽시계다(실제 CC.Move 호출 수는 0).
    /// Follower*/Neutral*는 FollowerSteer/NeutralMove의 하위 분해이며 SDF ON 경로만 계측한다(!sdfActive CC 폴백 미계측).
    /// FollowerJobWait/NeutralJobWait는 CCMove의 같은 구간을 다시 재는 inclusive 중복이다 — CCMove와 이중 차감 금지.
    /// </summary>
    public enum Seg
    {
        Total,          // SimTick 전체(restore~commit/publish)
        Restore,        // 렌더 보간 되돌림 패스(_transform.position = _visualCur)
        Heading,        // UpdateHeadings(player heading + rival AI 재결정)
        LeaderMove,     // MoveLeaders(리더 CC.Move + clamp 포함)
        FollowerSteer,  // 팔로워 조향 루프(QueryCircle + 분리 + CC.Move 포함)
        NeutralMove,    // 중립 배회 루프(repick Raycast + CC.Move 포함)
        Mirror,         // MirrorPositionsToBuffer(transform -> buffer.Pos)
        GridRebuild,    // SpatialGrid.Rebuild
        Recruit,        // RecruitResolver.Resolve
        Combat,         // CombatResolver.Resolve
        CommitPublish,  // CommitOutcomes + PublishTickEvents
        CCMove,         // 이동 격리 합산(위 다섯 지점; SDF ON에서는 CC.Move가 아니라 SDF 이동 해소의 병렬 벽시계)
        FollowerPrepass,        // 팔로워 직렬 프리패스(team arrive 중심/유효반경 + 작업리스트 평탄화 + MovePositionCurrent 캡처)
        FollowerGridSnapshot,   // SpatialGrid.CopyNativeSnapshot(NativeArray.Copy 5회)
        FollowerJobWait,        // 팔로워 이동 job .Complete() 대기(의존 force job의 잔여 실행 포함). CCMove와 inclusive 중복
        FollowerPresent,        // 팔로워 직렬 제시 패스(SDF 경로 전용: buffer.Pos/_visual*/_followerVelocity + Human 호출)
        NeutralPrepass,         // 중립 직렬 프리패스(타이머 감산 + RepickWanderHeading + Sin/Cos + 평탄화, SDF 경로 전용)
        NeutralJobWait,         // NeutralSdfMoveJob .Complete() 대기. CCMove와 inclusive 중복
        NeutralPresent,         // 중립 직렬 제시 패스(SDF 경로 전용)
        Count,
    }

    /// <summary>계측 활성화 플래그다. false(기본/프로덕션/테스트)면 Begin/End가 no-op다.</summary>
    public static bool Enabled;

    private static readonly long[] _accum = new long[(int)Seg.Count];
    private static readonly long[] _start = new long[(int)Seg.Count];

    /// <summary>구간별 누적 timestamp tick 배열(길이 = Seg.Count)이다. harness가 읽어 ms로 환산한다.</summary>
    public static long[] Accum => _accum;

    /// <summary>Stopwatch.GetTimestamp() 주기(초당 tick 수)다. ms 환산에 쓴다.</summary>
    public static long Frequency => Stopwatch.Frequency;

    /// <summary>모든 누적치를 0으로 초기화한다. harness가 각 측정 블록 시작 전에 호출한다.</summary>
    public static void Reset()
    {
        for (int i = 0; i < _accum.Length; i++)
        {
            _accum[i] = 0L;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Begin(Seg s)
    {
        if (Enabled)
        {
            _start[(int)s] = Stopwatch.GetTimestamp();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void End(Seg s)
    {
        if (Enabled)
        {
            _accum[(int)s] += Stopwatch.GetTimestamp() - _start[(int)s];
        }
    }

    /// <summary>지정 구간 이름을 반환한다(리포트 라벨용).</summary>
    public static string Name(Seg s)
    {
        return s.ToString();
    }
}
