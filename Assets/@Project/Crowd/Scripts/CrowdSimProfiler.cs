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
    /// CCMove는 리더/팔로워/중립 세 루프의 CharacterController.Move 벽시계를 합산한 격리 버킷이며,
    /// 각 이동 구간(LeaderMove/FollowerSteer/NeutralMove) 안에 중첩되어 그 총합에도 포함된다.
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
        CCMove,         // CharacterController.Move 격리 합산(위 세 구간에 중첩)
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
