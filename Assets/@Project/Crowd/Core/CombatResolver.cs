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
/// 혼자 남은 leader가 strictly 더 큰 crowd에게 제거된 사실을 나타내는 결과 항목이다.
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
    private readonly int[] _virtualCounts;

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
        _virtualCounts = new int[teamCount];
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
    ///   가장 가까운 접촉 member 순으로, 거리 동률은 낮은 agent Id 순으로 변환한다. agent 하나는 호출당 최대 한 번만
    ///   변환되며 leader는 이 단계에서 절대 변환되지 않는다. 여러 winning 팀이 같은 victim을 노리면 가장 가까운 접촉
    ///   member를 가진 팀이 가져가고(거리 동률은 낮은 팀 id), 밀린 pair의 예산은 미달로 남는다. 미달 예산은 소모되지
    ///   않고 CombatState에 유지된다(방출된 변환만 누적값을 소모한다). 변환은 한 배치로 방출한다.
    /// 4단계: 제거 pass는 시작 팀에 3단계 변환만 겹친 하나의 고정 가상 매핑으로 모든 팀을 평가한다. 이 pass가 방출하는
    ///   leader 변환은 같은 호출의 다른 팀 판정에 반영되지 않는다(순서 의존성 없음). 가상 count가 1(lone leader)인 팀의
    ///   leader에서 CombatRadius 안에 strictly 더 큰(가상 count) 팀의 member가 있으면 그 팀이 제거된다. eliminator는
    ///   가장 가까운 그런 member를 가진 팀이고, 거리 동률은 낮은 팀 id다. <see cref="CrowdElimination"/>과 함께
    ///   CrowdConversion(leaderIndex, eliminatorTeam)을 방출하며 buffer의 IsLeader=false 적용은 caller 책임이다.
    ///   lone leader 둘(1 대 1)은 서로 inert다.
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
        float radius = tuning.CombatRadius;
        float radiusSq = radius * radius;
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

            grid.QueryCircle(positions[i], radius, _queryResults);
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
                if (distSq > radiusSq)
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

        for (int w = 0; w < teamCount; w++)
        {
            for (int l = 0; l < teamCount; l++)
            {
                if (w == l || _startCounts[w] <= _startCounts[l])
                {
                    continue;
                }

                int pairIndex = w * teamCount + l;
                int budget = (int)accumulators[pairIndex];
                if (budget <= 0)
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
                    // 미달 예산은 소모하지 않고 다음 호출을 위해 유지한다.
                    continue;
                }

                SortVictimsByDistanceThenId(victimCount, ids);

                int emitCount = budget < victimCount ? budget : victimCount;
                for (int v = 0; v < emitCount; v++)
                {
                    int victim = _victimBuffer[v];
                    outcome.Conversions.Add(new CrowdConversion(victim, w));
                    _virtualTeam[victim] = w;
                }

                // 방출된 변환만 누적값을 소모한다. 나머지 미달분은 그대로 남는다.
                accumulators[pairIndex] -= emitCount;
            }
        }

        // ---- 4단계: 고정 가상 매핑(시작 팀 + 3단계 변환)으로 lone leader 제거 판정 ----
        for (int t = 0; t < teamCount; t++)
        {
            _virtualCounts[t] = 0;
        }

        for (int a = 0; a < agentCount; a++)
        {
            int team = _virtualTeam[a];
            if (team >= 0)
            {
                _virtualCounts[team]++;
            }
        }

        for (int t = 0; t < teamCount; t++)
        {
            if (_virtualCounts[t] != 1)
            {
                continue;
            }

            // leader는 3단계에서 변환되지 않으므로 가상 count 1인 팀의 유일한 member가 곧 leader다.
            int leaderIndex = -1;
            for (int a = 0; a < agentCount; a++)
            {
                if (_virtualTeam[a] == t && isLeader[a])
                {
                    leaderIndex = a;
                    break;
                }
            }

            if (leaderIndex < 0)
            {
                continue;
            }

            Vector2 leaderPos = positions[leaderIndex];
            grid.QueryCircle(leaderPos, radius, _queryResults);

            float bestDistSq = float.MaxValue;
            int bestTeam = -1;
            int foundCount = _queryResults.Count;
            for (int q = 0; q < foundCount; q++)
            {
                int j = _queryResults[q];
                int enemyTeam = _virtualTeam[j];
                if (enemyTeam < 0 || enemyTeam == t)
                {
                    continue;
                }

                if (_virtualCounts[enemyTeam] <= 1)
                {
                    // strictly 더 큰 팀만 제거할 수 있다. lone leader 둘(1 대 1)은 서로 inert다.
                    continue;
                }

                float dx = leaderPos.x - positions[j].x;
                float dy = leaderPos.y - positions[j].y;
                float distSq = dx * dx + dy * dy;
                if (distSq > radiusSq)
                {
                    continue;
                }

                if (distSq < bestDistSq || (distSq == bestDistSq && enemyTeam < bestTeam))
                {
                    bestDistSq = distSq;
                    bestTeam = enemyTeam;
                }
            }

            if (bestTeam >= 0)
            {
                // 이 leader 변환은 가상 매핑에 반영하지 않는다.
                // 같은 호출 안 다른 팀의 제거 판정에 feedback되면 순서 의존성이 생기기 때문이다.
                outcome.Eliminations.Add(new CrowdElimination(t, bestTeam, leaderIndex));
                outcome.Conversions.Add(new CrowdConversion(leaderIndex, bestTeam));
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
