using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 전투 변환 phase에서 agent 하나가 다른 팀으로 넘어간 사실을 나타내는 결과 항목이다.
/// </summary>
public readonly struct CrowdConversion
{
    /// <summary>
    /// 변환된 agent의 buffer index다.
    /// </summary>
    public readonly int AgentIndex;

    /// <summary>
    /// agent가 새로 소속되는 팀 id다.
    /// </summary>
    public readonly int ToTeam;

    /// <summary>
    /// 변환 결과 항목을 생성한다.
    /// </summary>
    public CrowdConversion(int agentIndex, int toTeam)
    {
        AgentIndex = agentIndex;
        ToTeam = toTeam;
    }
}

/// <summary>
/// leader가 CombatRadius 안에서 국소 수적으로 열세라 더 큰 crowd에게 제거된 사실을 나타내는 결과 항목이다.
/// </summary>
public readonly struct CrowdElimination
{
    /// <summary>
    /// 제거된 팀 id다.
    /// </summary>
    public readonly int Team;

    /// <summary>
    /// 제거를 수행해 leader를 흡수하는 팀 id다.
    /// </summary>
    public readonly int ByTeam;

    /// <summary>
    /// 제거된 leader agent의 buffer index다. buffer의 IsLeader=false 적용은 caller 책임이다.
    /// </summary>
    public readonly int LeaderAgentIndex;

    /// <summary>
    /// 제거 결과 항목을 생성한다.
    /// </summary>
    public CrowdElimination(int team, int byTeam, int leaderAgentIndex)
    {
        Team = team;
        ByTeam = byTeam;
        LeaderAgentIndex = leaderAgentIndex;
    }
}

/// <summary>
/// 호출 사이에 유지되는 ordered (winner, loser) 팀 pair별 변환 누적값 저장소다.
/// resolver가 이번 호출에 접촉 pair가 없는 pair의 누적값만 0으로 되돌린다.
/// </summary>
public sealed class CombatState
{
    // [winner * teamCount + loser]로 접근하는 ordered pair 누적값이다.
    private readonly float[] _accumulators;

    internal float[] Accumulators
    {
        get { return _accumulators; }
    }

    /// <summary>
    /// 팀 수에 맞는 ordered pair 누적 저장소를 미리 할당한다.
    /// </summary>
    public CombatState(int teamCount)
    {
        _accumulators = new float[teamCount * teamCount];
    }

    /// <summary>
    /// 모든 pair 누적값을 0으로 되돌린다.
    /// </summary>
    public void Reset()
    {
        Array.Clear(_accumulators, 0, _accumulators.Length);
    }
}

/// <summary>
/// <see cref="CombatResolver.Resolve"/> 한 번이 방출하는 결과 배치다.
/// resolver는 buffer를 바꾸지 않으므로 caller가 이 결과를 읽어 buffer와 표현에 원자적으로 적용한다.
/// </summary>
public sealed class CombatOutcome
{
    /// <summary>
    /// 이번 호출에서 확정된 변환 목록이다. 제거 pass가 방출한 leader 변환도 뒤에 함께 담긴다.
    /// </summary>
    public List<CrowdConversion> Conversions { get; }

    /// <summary>
    /// 이번 호출에서 확정된 leader 제거 목록이다.
    /// </summary>
    public List<CrowdElimination> Eliminations { get; }

    /// <summary>
    /// 지정한 용량으로 결과 목록을 미리 할당한다.
    /// </summary>
    public CombatOutcome(int capacity)
    {
        Conversions = new List<CrowdConversion>(capacity);
        // 제거는 호출당 팀별 최대 1건이므로 작은 용량이면 충분하다.
        Eliminations = new List<CrowdElimination>(4);
    }

    /// <summary>
    /// 두 결과 목록을 비운다.
    /// </summary>
    public void Clear()
    {
        Conversions.Clear();
        Eliminations.Clear();
    }
}

/// <summary>
/// 한 sim step의 crowd 전투(member 변환 + lone leader 제거)를 스냅샷 순수(snapshot-pure) 방식으로 계산한다.
/// buffer는 읽기 전용 스냅샷으로만 사용하고 절대 변경하지 않으며, 결과는 <see cref="CombatOutcome"/>으로만 방출한다.
/// 결과는 pair 열거 순서와 agent 삽입 순서에 대해 불변이고, warmup 이후 heap 할당이 없다.
/// </summary>
public sealed class CombatResolver
{
    private readonly int _teamCount;

    // 시작 시점 팀별 member 수(leader 포함). neutral(-1)은 세지 않는다.
    private readonly int[] _startCounts;

    // 팀 pair별 접촉 member 쌍 수. [a * teamCount + b], 대칭으로 채운다.
    private readonly int[] _pairTouchCounts;

    // agent별 적 팀 최근접 접촉 거리 제곱. [agent * teamCount + enemyTeam], 접촉 없음 = float.MaxValue.
    private readonly float[] _nearestEnemyDistSq;

    // agent별 전역 중재 결과: 이 agent를 가져가는 winning 팀(-1 = 없음)과 그 팀까지의 거리 제곱.
    private readonly int[] _assignedTeam;
    private readonly float[] _assignedDistSq;

    // 시작 팀에 3단계 변환만 겹친 고정 가상 매핑. 4단계 제거 판정 전용.
    private readonly int[] _virtualTeam;

    // 4단계에서 leader 하나의 CombatRadius 안 가상 팀별 member 수를 세는 국소 tally scratch(leader마다 재사용).
    private readonly int[] _localTeamCounts;

    // pair별 victim 정렬용 scratch.
    private readonly int[] _victimBuffer;

    // grid 질의 결과 재사용 목록.
    private readonly List<int> _queryResults;

    /// <summary>
    /// 팀 수와 agent 최대 수에 맞춰 내부 scratch 저장소를 미리 할당한다.
    /// </summary>
    public CombatResolver(int teamCount, int agentCapacity)
    {
        _teamCount = teamCount;
        _startCounts = new int[teamCount];
        _pairTouchCounts = new int[teamCount * teamCount];
        _nearestEnemyDistSq = new float[agentCapacity * teamCount];
        _assignedTeam = new int[agentCapacity];
        _assignedDistSq = new float[agentCapacity];
        _virtualTeam = new int[agentCapacity];
        _localTeamCounts = new int[teamCount];
        _victimBuffer = new int[agentCapacity];
        _queryResults = new List<int>(agentCapacity);
    }

    /// <summary>
    /// 한 sim step의 전투를 계산해 <paramref name="outcome"/>에 한 배치로 방출한다. buffer는 변경하지 않는다.
    /// </summary>
    /// <remarks>
    /// 1단계: 팀이 서로 다른 non-neutral member 쌍 중 거리가 CombatRadius 이하인 접촉 pair 수를 팀 pair별로 센다.
    /// 2단계: 모든 crowd pair의 변환 방향과 예산은 변경 불가능한 시작 시점 count에서만 나온다(도중에 조정하지 않는다).
    ///   strictly 큰 쪽이 작은 쪽에서 ConvertPerSecond * clamp01(접촉 pair 수 / PairNormalizer) * dt 만큼을
    ///   ordered (winner, loser) pair 누적값(<see cref="CombatState"/>)에 호출을 넘어 누적한다.
    ///   이번 호출에 접촉 pair가 없는 pair는 누적값을 0으로 되돌린다. 시작 count가 같으면 변환하지 않고 누적값을 유지한다.
    /// 3단계: victim은 전역으로 중재한다. 후보는 지는 팀의 non-leader member 중 winning 팀 member와 접촉한 agent이고,
    ///   가장 가까운 접촉 member 순으로, 거리 동률은 낮은 agent Id 순으로 변환한다. leader는 이 단계에서 절대 변환되지 않고,
    ///   여러 winning 팀이 같은 victim을 노리면 가장 가까운 접촉 member를 가진 팀이 가져간다(거리 동률은 낮은 팀 id).
    ///   tuning.RateLimitConversion=false(기본)면 배정된 victim 전원을 이번 호출에 한 배치로 방출한다(접촉 즉시 전향).
    ///   true면 2단계 누적 예산 상한 min((int)accumulators, victim 수)만큼만 방출하고 그 방출분만 누적값을 소모하며,
    ///   밀린 pair의 미달 예산은 소모되지 않고 CombatState에 유지된다. 두 경로 모두 agent 하나는 호출당 최대 한 번만 변환된다.
    /// 4단계: 제거 pass는 시작 팀에 3단계 변환만 겹친 하나의 고정 가상 매핑(_virtualTeam)으로 모든 leader를 국소 판정한다.
    ///   각 leader는 자신을 중심으로 한 CombatRadius 원 안에서 가상 팀별 member 수를 세어, 자기 자신을 포함한 아군 국소
    ///   수(ownLocal)와 적 팀별 국소 수(enemyLocal)를 비교한다. tuning.LeaderProtection=true(기본)면 enemyLocal>ownLocal인
    ///   적 팀만, false면 enemyLocal>=ownLocal인 적 팀도 제거자 자격을 가진다. 자격 적 팀 중 국소 수가 가장 많은 팀이
    ///   제거자이며 동률은 낮은 팀 id다(정수만 쓰는 전순서 → 스냅샷/삽입 순서 불변, 거리 float 동률 없음). 전역 count가
    ///   아니라 국소 수로 판정하므로 map-separated straggler로 전역 count가 부풀어도 코너에 몰린 leader가 제거된다.
///   추가로 전역 가드가 있다: 시작 시점 전역 count가 strictly 더 큰 적 팀만 제거자 자격을 얻는다(3단계 전향 게이트와
///   동일한 strict >). 전역적으로 더 작지만 국소로 더 밀집한 crowd가 더 큰 crowd의 leader를 제거하는 것을 막으며,
///   이 가드는 LeaderProtection ON/OFF 바깥이라 두 모드 모두에 적용된다(국소 우세는 여전히 필요, 전역 동수는 inert).
    ///   이 pass가 방출하는 leader 변환은 _virtualTeam에 되먹이지 않아 같은 호출의 다른 팀 판정에 영향을 주지 않는다
    ///   (순서 의존성 없음). <see cref="CrowdElimination"/>과 함께 CrowdConversion(leaderIndex, killerTeam)을 방출하며
    ///   buffer의 IsLeader=false 적용은 caller 책임이다. 보호 ON에서 홀로 남은 leader 둘(각 ownLocal=1, enemyLocal=1,
    ///   1 대 1)은 strictly 비교라 서로 inert다.
    /// </remarks>
    public void Resolve(AgentBuffer buffer, SpatialGrid grid, in SimTuning tuning, float dt, CombatState state, CombatOutcome outcome)
    {
        outcome.Clear();

        int teamCount = _teamCount;
        int agentCount = buffer.Count;
        int[] teams = buffer.Team;
        bool[] isLeader = buffer.IsLeader;
        int[] ids = buffer.Id;
        Vector2[] positions = buffer.Pos;
        float[] scales = buffer.Scale;
        float radius = tuning.CombatRadius;
        // 스케일 인지: worst-case pair(둘 다 최대 스케일)까지 이웃이 잡히도록 질의 반경을 넓힌다.
        // MaxScale이 0(bare default)이면 1로 가드해 질의를 축소하지 않는다. 실제 접촉 판정은 아래 pair별 반경으로 건다.
        float queryRadius = radius * Mathf.Max(1f, tuning.MaxScale);
        bool leaderProtection = tuning.LeaderProtection;
        float[] accumulators = state.Accumulators;

        // ---- 1단계: 시작 count 스냅샷 + 팀 pair별 접촉 수 + agent별 적 팀 최근접 접촉 거리 ----
        for (int t = 0; t < teamCount; t++)
        {
            _startCounts[t] = 0;
        }

        int pairSlots = teamCount * teamCount;
        for (int p = 0; p < pairSlots; p++)
        {
            _pairTouchCounts[p] = 0;
        }

        for (int a = 0; a < agentCount; a++)
        {
            int rowStart = a * teamCount;
            for (int t = 0; t < teamCount; t++)
            {
                _nearestEnemyDistSq[rowStart + t] = float.MaxValue;
            }

            int team = teams[a];
            if (team >= 0)
            {
                _startCounts[team]++;
            }
        }

        for (int i = 0; i < agentCount; i++)
        {
            int teamI = teams[i];
            if (teamI < 0)
            {
                continue;
            }

            grid.QueryCircle(positions[i], queryRadius, _queryResults);
            int foundCount = _queryResults.Count;
            for (int q = 0; q < foundCount; q++)
            {
                int j = _queryResults[q];
                int teamJ = teams[j];
                if (teamJ < 0 || teamJ == teamI)
                {
                    continue;
                }

                // (a-b)^2 합은 계산 방향과 무관하게 같은 float 값이므로 순서 불변성이 유지된다.
                float dx = positions[i].x - positions[j].x;
                float dy = positions[i].y - positions[j].y;
                float distSq = dx * dx + dy * dy;
                // 스케일 인지 접촉: 두 유닛 스케일 평균으로 pair별 접촉 반경을 정한다(둘 다 1.0이면 radius와 동일해 기존 판정과 byte-identical).
                // Scale[i]+Scale[j]는 교환법칙이 성립해 열거/삽입 순서와 무관하게 같은 float 값이다.
                float pairR = radius * 0.5f * (scales[i] + scales[j]);
                if (distSq > pairR * pairR)
                {
                    continue;
                }

                int nearestIndex = i * teamCount + teamJ;
                if (distSq < _nearestEnemyDistSq[nearestIndex])
                {
                    _nearestEnemyDistSq[nearestIndex] = distSq;
                }

                // 접촉 pair는 unordered 쌍당 정확히 한 번만 센다(집합 크기라 열거 순서와 무관하다).
                if (i < j)
                {
                    _pairTouchCounts[teamI * teamCount + teamJ]++;
                    _pairTouchCounts[teamJ * teamCount + teamI]++;
                }
            }
        }

        // ---- 2단계: 시작 count 기준 방향 판정과 예산 누적 ----
        for (int w = 0; w < teamCount; w++)
        {
            for (int l = 0; l < teamCount; l++)
            {
                if (w == l)
                {
                    continue;
                }

                int pairIndex = w * teamCount + l;
                if (_pairTouchCounts[pairIndex] == 0)
                {
                    // 이번 호출에 접촉 pair가 없는 ordered pair는 누적값을 0으로 되돌린다.
                    accumulators[pairIndex] = 0f;
                    continue;
                }

                if (_startCounts[w] > _startCounts[l])
                {
                    float strength = Mathf.Clamp01(_pairTouchCounts[pairIndex] / (float)tuning.PairNormalizer);
                    accumulators[pairIndex] += tuning.ConvertPerSecond * strength * dt;
                }
                // 시작 count가 같거나 작은 쪽 방향이면 누적하지 않고 그대로 유지한다.
            }
        }

        // ---- 3단계: victim 전역 중재(agent별 배타 배정) 후 pair별 정렬 방출 ----
        for (int a = 0; a < agentCount; a++)
        {
            _assignedTeam[a] = -1;
            _assignedDistSq[a] = float.MaxValue;
            _virtualTeam[a] = teams[a];

            int team = teams[a];
            if (team < 0 || isLeader[a])
            {
                // neutral은 전투 대상이 아니고, leader는 이 단계에서 절대 변환되지 않는다.
                continue;
            }

            int rowStart = a * teamCount;
            for (int w = 0; w < teamCount; w++)
            {
                if (w == team)
                {
                    continue;
                }

                if (_startCounts[w] <= _startCounts[team])
                {
                    // 시작 count 기준 strictly 큰 팀만 변환 방향을 가진다.
                    continue;
                }

                float distSq = _nearestEnemyDistSq[rowStart + w];
                if (distSq == float.MaxValue)
                {
                    // 이 agent는 w 팀 member와 접촉하지 않았다.
                    continue;
                }

                // 더 가까운 접촉 member를 가진 팀이 victim을 가져간다.
                // 거리 동률은 낮은 팀 id 우선이며 w 오름차순 순회 + strict 비교로 보장된다.
                if (distSq < _assignedDistSq[a])
                {
                    _assignedDistSq[a] = distSq;
                    _assignedTeam[a] = w;
                }
            }
        }

        bool rateLimited = tuning.RateLimitConversion;

        for (int w = 0; w < teamCount; w++)
        {
            for (int l = 0; l < teamCount; l++)
            {
                if (w == l || _startCounts[w] <= _startCounts[l])
                {
                    continue;
                }

                int pairIndex = w * teamCount + l;
                // rate-limited 경로는 정수화된 누적 예산이 있어야 방출한다. instant 경로는 예산 게이트 없이 전원 방출한다.
                int budget = (int)accumulators[pairIndex];
                if (rateLimited && budget <= 0)
                {
                    continue;
                }

                int victimCount = 0;
                for (int a = 0; a < agentCount; a++)
                {
                    if (teams[a] == l && _assignedTeam[a] == w)
                    {
                        _victimBuffer[victimCount] = a;
                        victimCount++;
                    }
                }

                if (victimCount == 0)
                {
                    // rate-limited 경로의 미달 예산은 소모하지 않고 다음 호출을 위해 유지한다.
                    continue;
                }

                SortVictimsByDistanceThenId(victimCount, ids);

                // instant(RateLimitConversion=false): 배정된 victim 전원 방출.
                // rate-limited(true): 이번 tick 예산 상한 min(budget, victim 수)만큼만 방출.
                int emitCount = rateLimited ? (budget < victimCount ? budget : victimCount) : victimCount;
                for (int v = 0; v < emitCount; v++)
                {
                    int victim = _victimBuffer[v];
                    outcome.Conversions.Add(new CrowdConversion(victim, w));
                    _virtualTeam[victim] = w;
                }

                if (rateLimited)
                {
                    // 방출된 변환만 누적값을 소모한다. 나머지 미달분은 그대로 남는다.
                    accumulators[pairIndex] -= emitCount;
                }
            }
        }

        // ---- 4단계: 국소 수적 판정으로 leader 제거(map-separated straggler로 인한 전역 count 오판 제거) ----
        // 시작 팀 + 3단계 변환을 겹친 고정 가상 매핑(_virtualTeam)으로만 판정한다. 3단계에서 escort가 변환되어
        // 국소적으로 홀로 남은 leader는 같은 tick에 홀로 판정된다(instant-conversion 의도). 각 leader 판정은
        // 하나의 스냅샷(_virtualTeam)에 대해 독립적으로 이뤄지고 결과를 _virtualTeam에 되먹이지 않아 팀 간
        // 순서 의존성이 없다.
        for (int a = 0; a < agentCount; a++)
        {
            if (!isLeader[a])
            {
                continue;
            }

            int t = _virtualTeam[a];
            if (t < 0)
            {
                // leader는 3단계/4단계에서 _virtualTeam이 바뀌지 않으므로 t는 곧 이 leader의 시작 팀이다.
                continue;
            }

            Vector2 leaderPos = positions[a];
            grid.QueryCircle(leaderPos, queryRadius, _queryResults);

            for (int k = 0; k < teamCount; k++)
            {
                _localTeamCounts[k] = 0;
            }

            int foundCount = _queryResults.Count;
            for (int q = 0; q < foundCount; q++)
            {
                int j = _queryResults[q];
                if (j == a)
                {
                    // leader 자신은 아래에서 ownLocal에 명시적으로 1로 센다(QueryCircle self-inclusion 여부와 무관하게 정확).
                    continue;
                }

                int teamJ = _virtualTeam[j];
                if (teamJ < 0)
                {
                    continue;
                }

                float dx = leaderPos.x - positions[j].x;
                float dy = leaderPos.y - positions[j].y;
                float distSq = dx * dx + dy * dy;
                // 스케일 인지 국소 판정: 리더 a(스케일 1)와 이웃 j의 스케일 평균으로 pair별 반경을 정한다(둘 다 1.0이면 radius와 동일).
                float pairR = radius * 0.5f * (scales[a] + scales[j]);
                if (distSq > pairR * pairR)
                {
                    continue;
                }

                _localTeamCounts[teamJ]++;
            }

            // ownLocal은 leader 자신을 포함한 CombatRadius 안 아군 수다. QueryCircle의 self-inclusion 여부와 무관하게
            // 리더를 명시적으로 1로 세고(위 tally에서 리더 index는 건너뛰어 중복을 막았다) 국소 아군 수를 더한다.
            int ownLocal = _localTeamCounts[t] + 1;

            // 국소 수가 가장 많은 자격 적 팀이 제거자. 동률이면 낮은 팀 id(오름차순 순회 + strict 비교로 보장).
            // 정수만 쓰는 전순서라 스냅샷 순서/삽입 순서에 불변이다(거리 float 동률 없음).
            int killer = -1;
            int bestEnemyLocal = 0;
            for (int e = 0; e < teamCount; e++)
            {
                if (e == t)
                {
                    continue;
                }

                int enemyLocal = _localTeamCounts[e];

                // 전역 가드: 시작 시점 전역 count가 strictly 더 큰 적 팀만 이 leader를 제거할 수 있다.
                // 국소 우세만으로는 부족하다 — 전역적으로 더 작지만 국소로 더 밀집한 crowd가 더 큰 crowd의 leader를
                // 먹는 것을 막는다. 3단계 member 전향 게이트(_startCounts[w] > _startCounts[l])와 동일하게 strict >라
                // 전역 동수는 서로 inert다. 이 가드는 LeaderProtection ON/OFF 바깥이라 두 모드 모두에 적용되고,
                // 토글은 아래 국소 임계값(>, >=)만 제어한다. t는 leader의 시작 팀이라 _startCounts[t]가 그 팀의 전역 count다.
                if (_startCounts[e] <= _startCounts[t])
                {
                    continue;
                }

                bool qualifies = leaderProtection ? (enemyLocal > ownLocal) : (enemyLocal >= ownLocal);
                if (!qualifies)
                {
                    continue;
                }

                if (enemyLocal > bestEnemyLocal)
                {
                    bestEnemyLocal = enemyLocal;
                    killer = e;
                }
            }

            if (killer >= 0)
            {
                // leader 판정 결과는 _virtualTeam에 되먹이지 않는다(팀 간 판정 독립성/순서 무의존 유지).
                outcome.Eliminations.Add(new CrowdElimination(t, killer, a));
                outcome.Conversions.Add(new CrowdConversion(a, killer));
            }
        }
    }

    // _victimBuffer[0..count)를 (배정 거리 제곱 오름차순, 동률은 agent Id 오름차순)으로 삽입 정렬한다.
    // Id가 고유해 전순서가 되므로 결과가 agent 삽입 순서와 무관하게 결정된다. 할당 없음.
    private void SortVictimsByDistanceThenId(int count, int[] ids)
    {
        for (int i = 1; i < count; i++)
        {
            int candidate = _victimBuffer[i];
            float candidateDistSq = _assignedDistSq[candidate];
            int candidateId = ids[candidate];

            int j = i - 1;
            while (j >= 0)
            {
                int placed = _victimBuffer[j];
                float placedDistSq = _assignedDistSq[placed];
                if (placedDistSq < candidateDistSq
                    || (placedDistSq == candidateDistSq && ids[placed] < candidateId))
                {
                    break;
                }

                _victimBuffer[j + 1] = placed;
                j--;
            }

            _victimBuffer[j + 1] = candidate;
        }
    }
}
