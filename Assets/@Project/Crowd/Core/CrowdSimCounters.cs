using System.Runtime.CompilerServices;

/// <summary>
/// M-sim-0 진단용 무침습 work counter다(Crowd/Core, Editor 헤드리스 harness 전용 계측).
/// <see cref="CrowdSimProfiler"/>/<c>CrowdOracleRecorder</c>와 동일 계약: 기본값(Enabled=false)에서는 모든 Count* 호출이
/// bool 분기 하나만 수행하고 즉시 반환하므로 시뮬레이션 값·RNG 소비·판정/이동 순서·이벤트 발행 순서에
/// 어떤 영향도 주지 않는다(결정론 불변). 활성화되어도 이 클래스는 별도 static long 카운터에만 누적할 뿐
/// 커널/버퍼/그리드/RNG 상태를 절대 만지지 않는다.
///
/// timing run에서는 반드시 OFF로 둔다(계측이 hot-path 비용을 교란). counter run은 timing run과 분리해야 한다.
/// Core asmdef에 두어 커널(SpatialGrid/RecruitResolver/CombatResolver)과 호출부(CrowdRoot/RivalAiDriver)가
/// 모두 참조할 수 있게 한다(Core는 Assembly-CSharp를 참조할 수 없으므로 counter는 Core에 위치한다).
/// </summary>
public static class CrowdSimCounters
{
    /// <summary>QueryCircle candidate 방문을 호출부별로 귀속하기 위한 소스 태그다.</summary>
    public enum QuerySource
    {
        Separation, // 팔로워 분리(CrowdRoot.SteerFollowersAndNeutrals, per follower)
        Recruit,    // 영입(RecruitResolver.Resolve, per neutral)
        Combat,     // 전투 접촉(CombatResolver.Resolve 1단계, per non-neutral)
        Leader,     // 리더 제거 판정(CombatResolver.Resolve 4단계, per leader)
        RivalAi,    // 라이벌 AI 중립 밀도(RivalAiDriver.DecideHeadingDeg, per rival decision)
        Count,
    }

    /// <summary>계측 활성화 플래그다. false(기본/프로덕션/테스트/timing run)면 Count*가 no-op다.</summary>
    public static bool Enabled;

    // 다음 QueryCircle의 소스 태그. 각 QueryCircle 호출 직전에 호출부가 SetSource로 지정한다(Enabled일 때만).
    private static QuerySource _source;

    private static readonly long[] _queryCalls = new long[(int)QuerySource.Count];
    private static readonly long[] _candidateVisits = new long[(int)QuerySource.Count];
    private static readonly long[] _radiusQualifiers = new long[(int)QuerySource.Count];

    private static long _gridRebuilds;
    private static long _gridEntries;

    private static long _combatTouching;     // 적 접촉 관계 수(양방향, i↔j 각 1회 → 무순서 pair당 2).
    private static long _combatUniquePairs;  // 무순서 접촉 member pair 수(i<j 1회).

    private static long _victimTotal;        // 정렬에 투입된 victim 총수(모든 (winner,loser) pair 합산).
    private static long _victimComparisons;  // victim insertion sort 비교 횟수(O(v²) 확인용).

    private static long _sdfResolves;        // ApplyHorizontalMove SDF solver 경로 실행 수.
    private static long _ccMoveFallbacks;    // ApplyHorizontalMove CharacterController.Move 폴백 경로 실행 수.

    // ---- 읽기 접근자(harness 전용) ----
    public static long QueryCalls(QuerySource s) => _queryCalls[(int)s];
    public static long CandidateVisits(QuerySource s) => _candidateVisits[(int)s];
    public static long RadiusQualifiers(QuerySource s) => _radiusQualifiers[(int)s];
    public static long GridRebuilds => _gridRebuilds;
    public static long GridEntries => _gridEntries;
    public static long CombatTouching => _combatTouching;
    public static long CombatUniquePairs => _combatUniquePairs;
    public static long VictimTotal => _victimTotal;
    public static long VictimComparisons => _victimComparisons;
    public static long SdfResolves => _sdfResolves;
    public static long CcMoveFallbacks => _ccMoveFallbacks;

    /// <summary>모든 누적치를 0으로 초기화한다. harness가 각 counter 측정 블록 시작 전에 호출한다.</summary>
    public static void Reset()
    {
        for (int i = 0; i < (int)QuerySource.Count; i++)
        {
            _queryCalls[i] = 0L;
            _candidateVisits[i] = 0L;
            _radiusQualifiers[i] = 0L;
        }

        _gridRebuilds = 0L;
        _gridEntries = 0L;
        _combatTouching = 0L;
        _combatUniquePairs = 0L;
        _victimTotal = 0L;
        _victimComparisons = 0L;
        _sdfResolves = 0L;
        _ccMoveFallbacks = 0L;
    }

    /// <summary>다음 QueryCircle 결과를 귀속할 소스 태그를 지정한다(각 QueryCircle 호출 직전).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetSource(QuerySource s)
    {
        if (Enabled)
        {
            _source = s;
        }
    }

    /// <summary>QueryCircle 한 번의 결과를 현재 소스에 귀속한다. candidateVisits=cell 매칭 후보 수, qualifiers=반경 통과 수.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CountQuery(int candidateVisits, int qualifiers)
    {
        if (Enabled)
        {
            int s = (int)_source;
            _queryCalls[s]++;
            _candidateVisits[s] += candidateVisits;
            _radiusQualifiers[s] += qualifiers;
        }
    }

    /// <summary>grid rebuild 1회와 그때 삽입된 agent 수를 누적한다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CountGridRebuild(int entries)
    {
        if (Enabled)
        {
            _gridRebuilds++;
            _gridEntries += entries;
        }
    }

    /// <summary>적 접촉 관계 1건(방향 있음)을 누적한다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CountCombatTouch()
    {
        if (Enabled)
        {
            _combatTouching++;
        }
    }

    /// <summary>무순서 접촉 member pair 1건(i&lt;j)을 누적한다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CountCombatUniquePair()
    {
        if (Enabled)
        {
            _combatUniquePairs++;
        }
    }

    /// <summary>정렬에 투입된 victim 수를 누적한다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CountVictims(int count)
    {
        if (Enabled)
        {
            _victimTotal += count;
        }
    }

    /// <summary>victim insertion sort 비교 횟수를 누적한다(호출부에서 로컬 집계 후 1회 전달).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void AddVictimComparisons(long comparisons)
    {
        if (Enabled)
        {
            _victimComparisons += comparisons;
        }
    }

    /// <summary>ApplyHorizontalMove SDF solver 경로 실행 1회를 누적한다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CountSdfResolve()
    {
        if (Enabled)
        {
            _sdfResolves++;
        }
    }

    /// <summary>ApplyHorizontalMove CharacterController.Move 폴백 경로 실행 1회를 누적한다.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void CountCcMoveFallback()
    {
        if (Enabled)
        {
            _ccMoveFallbacks++;
        }
    }
}
