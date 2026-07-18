using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

/// <summary>
/// XZ 평면의 agent를 균일 격자 cell에 해싱해 원형 반경 질의를 가속하는 grid이다.
/// 생성자에서 모든 배열을 capacity에 맞춰 미리 할당하므로
/// <see cref="Rebuild"/>와 <see cref="QueryCircle"/>은 warmup 이후 heap 할당 없이 동작한다.
/// </summary>
public sealed class SpatialGrid
{
    // Real-Time Collision Detection(Ericson)의 정수 cell hash 상수.
    private const int HashPrimeX = unchecked((int)0x8DA6B343);
    private const int HashPrimeY = unchecked((int)0xD8163841);

    private readonly float _invCellSize;
    private readonly int _tableMask;
    private readonly int[] _bucketHead;   // bucket별 첫 agent index. 비어 있으면 -1.
    // bucket별 팀 존재 bitmask(1<<team). hash 충돌로 여러 cell이 한 bucket에 섞이면 그 팀들의 OR라 exact-cell 팀 집합의 superset이다.
    // 전투 1단계가 QueryCircle 전에 이 mask로 "footprint에 적 팀이 없으면" 스캔을 건너뛰는 데 쓴다(conservative → byte-identical).
    private readonly int[] _bucketTeamMask;
    private readonly int[] _nextInBucket; // agent index별 같은 bucket 안의 다음 agent index.
    private readonly int[] _cellX;        // agent index별 소속 cell 좌표. hash 충돌 구분에 쓴다.
    private readonly int[] _cellY;
    private readonly Vector2[] _pos;      // Rebuild 시점의 위치 snapshot.
    private int _count;

    /// <summary>
    /// 지정한 cell 크기와 최대 agent 수로 grid를 생성한다.
    /// 내부 hash table 크기는 capacity의 2배 이상이 되는 2의 거듭제곱으로 잡아 충돌을 줄인다.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="cellSize"/>가 0 이하이거나 <paramref name="capacity"/>가 0 이하이면 발생한다.
    /// </exception>
    public SpatialGrid(float cellSize, int capacity)
    {
        if (cellSize <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "cellSize는 0보다 커야 한다.");
        }

        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "capacity는 0보다 커야 한다.");
        }

        _invCellSize = 1f / cellSize;

        int tableSize = ComputeTableSize(capacity);
        _tableMask = tableSize - 1;
        _bucketHead = new int[tableSize];
        _bucketTeamMask = new int[tableSize]; // 기본값 0(팀 없음)이면 정확해 별도 초기화 불필요. Rebuild마다 0으로 되돌린다.
        _nextInBucket = new int[capacity];
        _cellX = new int[capacity];
        _cellY = new int[capacity];
        _pos = new Vector2[capacity];
        _count = 0;

        for (int i = 0; i < _bucketHead.Length; i++)
        {
            _bucketHead[i] = -1;
        }
    }

    /// <summary>
    /// 현재 채워진 agent 수(마지막 <see cref="Rebuild"/> 기준)다.
    /// </summary>
    public int Count => _count;

    /// <summary>
    /// bucket index 계산에 쓰는 hash table mask(tableSize-1)다.
    /// </summary>
    public int TableMask => _tableMask;

    /// <summary>
    /// cell 좌표 계산에 쓰는 1/cellSize다.
    /// </summary>
    public float InvCellSize => _invCellSize;

    /// <summary>
    /// 지정한 capacity에 대한 내부 hash table 크기(capacity*2 이상이 되는 2의 거듭제곱, 최소 16)를 계산한다.
    /// 생성자와 native snapshot buffer 크기 계산이 같은 값을 쓰도록 단일 원천으로 노출한다.
    /// </summary>
    public static int ComputeTableSize(int capacity)
    {
        int tableSize = 16;
        while (tableSize < capacity * 2)
        {
            tableSize <<= 1;
        }

        return tableSize;
    }

    /// <summary>
    /// 현재 grid 내부 상태(bucket head/체인/cell 좌표/위치 snapshot)를 호출자 소유 NativeArray로 복사한다(M2-a3 병렬 조향의 read-only 뷰).
    /// Mono <c>IJobParallelFor</c>의 각 <c>Execute</c>가 <see cref="QueryCircle"/>/<see cref="QueryCircleCapped"/> 열거를 in-place로
    /// 정확히 재현하도록, bucketHead는 tableSize 전체를, 나머지는 유효 agent 수(<see cref="Count"/>)만 복사한다. 값/순서를 바꾸지 않는 순수 복사다.
    /// </summary>
    public void CopyNativeSnapshot(
        NativeArray<int> bucketHead,
        NativeArray<int> nextInBucket,
        NativeArray<int> cellX,
        NativeArray<int> cellY,
        NativeArray<Vector2> pos)
    {
        NativeArray<int>.Copy(_bucketHead, bucketHead, _bucketHead.Length);
        if (_count > 0)
        {
            NativeArray<int>.Copy(_nextInBucket, nextInBucket, _count);
            NativeArray<int>.Copy(_cellX, cellX, _count);
            NativeArray<int>.Copy(_cellY, cellY, _count);
            NativeArray<Vector2>.Copy(_pos, pos, _count);
        }
    }

    /// <summary>
    /// buffer의 모든 agent 위치를 snapshot으로 읽어 grid를 다시 채운다.
    /// 이후의 <see cref="QueryCircle"/>은 이 시점의 위치를 기준으로 답한다. heap 할당은 없다.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/>가 null이면 발생한다.</exception>
    /// <exception cref="ArgumentException"><paramref name="buffer"/>의 agent 수가 생성 시 capacity를 넘으면 발생한다.</exception>
    public void Rebuild(AgentBuffer buffer)
    {
        if (buffer == null)
        {
            throw new ArgumentNullException(nameof(buffer));
        }

        if (buffer.Count > _nextInBucket.Length)
        {
            throw new ArgumentException(
                $"buffer의 agent 수({buffer.Count})가 grid capacity({_nextInBucket.Length})를 넘는다.",
                nameof(buffer));
        }

        for (int i = 0; i < _bucketHead.Length; i++)
        {
            _bucketHead[i] = -1;
            _bucketTeamMask[i] = 0; // 이번 스냅샷의 팀 존재 bitmask를 처음부터 다시 누적한다.
        }

        _count = buffer.Count;
        NativeArray<Vector2> positions = buffer.Pos;
        NativeArray<int> teams = buffer.Team; // per-bucket 팀 bitmask 누적용(전투 skip 필터 전용). 열거 순서/결과에는 영향 없다.
        for (int i = 0; i < _count; i++)
        {
            Vector2 pos = positions[i];
            int cellX = Mathf.FloorToInt(pos.x * _invCellSize);
            int cellY = Mathf.FloorToInt(pos.y * _invCellSize);

            _pos[i] = pos;
            _cellX[i] = cellX;
            _cellY[i] = cellY;

            int bucket = HashCell(cellX, cellY) & _tableMask;
            _nextInBucket[i] = _bucketHead[bucket];
            _bucketHead[bucket] = i;

            int team = teams[i];
            if (team >= 0)
            {
                // neutral(-1)은 적 팀이 아니라 mask에서 제외한다. 전투는 team>=0의 존재만 본다.
                _bucketTeamMask[bucket] |= TeamBit(team);
            }
        }

        CrowdSimCounters.CountGridRebuild(_count); // 무침습 계측(Enabled=false면 no-op).
    }

    /// <summary>
    /// center에서 radius 이내(경계 포함)인 agent index를 results에 담는다.
    /// results를 먼저 비운 뒤, cell 후보를 정확한 거리로 걸러서 추가하므로 false positive와 중복이 없다.
    /// 결과는 마지막 <see cref="Rebuild"/> 시점의 위치 기준이다.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="results"/>가 null이면 발생한다.</exception>
    public void QueryCircle(Vector2 center, float radius, List<int> results)
    {
        if (results == null)
        {
            throw new ArgumentNullException(nameof(results));
        }

        results.Clear();

        // 무침습 계측: 계측 여부를 루프 밖에서 한 번만 읽는다(Enabled=false면 아래 로컬 증가가 사실상 no-op).
        bool count = CrowdSimCounters.Enabled;

        if (_count == 0 || radius < 0f)
        {
            CrowdSimCounters.CountQuery(0, 0); // 빈 질의도 호출 수로 집계(Enabled=false면 no-op).
            return;
        }

        int minCellX = Mathf.FloorToInt((center.x - radius) * _invCellSize);
        int maxCellX = Mathf.FloorToInt((center.x + radius) * _invCellSize);
        int minCellY = Mathf.FloorToInt((center.y - radius) * _invCellSize);
        int maxCellY = Mathf.FloorToInt((center.y + radius) * _invCellSize);
        float radiusSq = radius * radius;

        int candidateVisits = 0; // cell 매칭 후보(거리 계산 대상) 수. count=false면 증가하지 않는다.
        for (int cellY = minCellY; cellY <= maxCellY; cellY++)
        {
            for (int cellX = minCellX; cellX <= maxCellX; cellX++)
            {
                int agentIndex = _bucketHead[HashCell(cellX, cellY) & _tableMask];
                while (agentIndex != -1)
                {
                    // hash 충돌로 다른 cell의 agent가 같은 bucket에 섞일 수 있어 cell 좌표로 거른다.
                    // agent는 자기 cell 좌표에서만 통과하므로 후보 cell끼리 충돌해도 중복 추가가 없다.
                    if (_cellX[agentIndex] == cellX && _cellY[agentIndex] == cellY)
                    {
                        if (count)
                        {
                            candidateVisits++;
                        }

                        float dx = _pos[agentIndex].x - center.x;
                        float dy = _pos[agentIndex].y - center.y;
                        if (dx * dx + dy * dy <= radiusSq)
                        {
                            results.Add(agentIndex);
                        }
                    }

                    agentIndex = _nextInBucket[agentIndex];
                }
            }
        }

        CrowdSimCounters.CountQuery(candidateVisits, results.Count); // 무침습 계측(Enabled=false면 no-op).
    }

    /// <summary>
    /// <see cref="QueryCircle"/>가 열거할 AABB cell 집합(동일한 floor-cell 공식)에 걸친 bucket 팀 bitmask의 OR를 반환한다.
    /// bucket mask는 exact-cell 팀 집합의 superset이므로(§ hash 충돌 시 colliding cell 팀까지 포함), 이 OR는
    /// QueryCircle이 그 반경에서 만날 수 있는 모든 팀의 superset이다. 결과에 적 팀 bit가 없으면 그 질의는 적을 만나지 않는다.
    /// 전투 1단계가 값비싼 스캔 전에 "footprint에 적 팀이 없으면" 건너뛰는 conservative gate로 쓴다(결과 byte-identical).
    /// results를 만들지 않고 mask만 계산하며 계측하지 않는다.
    /// </summary>
    public int QueryTeamMask(Vector2 center, float radius)
    {
        if (_count == 0 || radius < 0f)
        {
            return 0;
        }

        // QueryCircle과 동일한 AABB cell 범위. 같은 center/radius를 넘기면 열거 cell 집합이 정확히 일치한다.
        int minCellX = Mathf.FloorToInt((center.x - radius) * _invCellSize);
        int maxCellX = Mathf.FloorToInt((center.x + radius) * _invCellSize);
        int minCellY = Mathf.FloorToInt((center.y - radius) * _invCellSize);
        int maxCellY = Mathf.FloorToInt((center.y + radius) * _invCellSize);

        int mask = 0;
        for (int cellY = minCellY; cellY <= maxCellY; cellY++)
        {
            for (int cellX = minCellX; cellX <= maxCellX; cellX++)
            {
                mask |= _bucketTeamMask[HashCell(cellX, cellY) & _tableMask];
            }
        }

        return mask;
    }

    /// <summary>
    /// <see cref="QueryCircle"/>와 동일하게 center에서 radius 이내(경계 포함)인 agent index를 results에 담되,
    /// 논리 cell에 매칭된 후보를 budget개까지만 처리한 뒤 전체 열거를 멈춘다(밀집 cell에서 분리 조회 비용 상한).
    /// budget번째 매칭 후보는 정상 처리되고 그다음 후보는 방문하지 않는다. budget이 0 이하이면 <see cref="QueryCircle"/>와 동일하게 동작한다.
    /// 절단이 일어나지 않은 질의는 <see cref="QueryCircle"/>와 결과·순서가 완전히 같다.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="results"/>가 null이면 발생한다.</exception>
    public void QueryCircleCapped(Vector2 center, float radius, int budget, List<int> results)
    {
        if (budget <= 0)
        {
            QueryCircle(center, radius, results); // cap 비활성: 정확 조회 경로로 위임(결과·순서 동일).
            return;
        }

        if (results == null)
        {
            throw new ArgumentNullException(nameof(results));
        }

        results.Clear();

        // 무침습 계측: 계측 여부를 루프 밖에서 한 번만 읽는다(Enabled=false면 아래 로컬 증가가 사실상 no-op).
        bool count = CrowdSimCounters.Enabled;

        if (_count == 0 || radius < 0f)
        {
            CrowdSimCounters.CountQuery(0, 0); // 빈 질의도 호출 수로 집계(Enabled=false면 no-op).
            return;
        }

        int minCellX = Mathf.FloorToInt((center.x - radius) * _invCellSize);
        int maxCellX = Mathf.FloorToInt((center.x + radius) * _invCellSize);
        int minCellY = Mathf.FloorToInt((center.y - radius) * _invCellSize);
        int maxCellY = Mathf.FloorToInt((center.y + radius) * _invCellSize);
        float radiusSq = radius * radius;

        int candidateVisits = 0; // cell 매칭 후보(거리 계산 대상) 수. count=false면 증가하지 않는다.
        int matched = 0;         // 예산 대상 논리-cell 매칭 후보 수. 계측 flag와 무관하게 항상 증가한다.
        for (int cellY = minCellY; cellY <= maxCellY; cellY++)
        {
            for (int cellX = minCellX; cellX <= maxCellX; cellX++)
            {
                int agentIndex = _bucketHead[HashCell(cellX, cellY) & _tableMask];
                while (agentIndex != -1)
                {
                    // hash 충돌로 다른 cell의 agent가 같은 bucket에 섞일 수 있어 cell 좌표로 거른다.
                    // agent는 자기 cell 좌표에서만 통과하므로 후보 cell끼리 충돌해도 중복 추가가 없다.
                    if (_cellX[agentIndex] == cellX && _cellY[agentIndex] == cellY)
                    {
                        if (count)
                        {
                            candidateVisits++;
                        }

                        matched++;

                        float dx = _pos[agentIndex].x - center.x;
                        float dy = _pos[agentIndex].y - center.y;
                        if (dx * dx + dy * dy <= radiusSq)
                        {
                            results.Add(agentIndex);
                        }

                        // budget번째 매칭 후보까지 처리한 뒤 전체 열거를 멈춘다(그다음 후보는 방문하지 않는다).
                        if (matched >= budget)
                        {
                            CrowdSimCounters.CountSepCapHit();                           // 절단 발화 계측(Enabled=false면 no-op).
                            CrowdSimCounters.CountQuery(candidateVisits, results.Count); // 무침습 계측(Enabled=false면 no-op).
                            return;
                        }
                    }

                    agentIndex = _nextInBucket[agentIndex];
                }
            }
        }

        CrowdSimCounters.CountQuery(candidateVisits, results.Count); // 무침습 계측(Enabled=false면 no-op).
    }

    /// <summary>
    /// cell 좌표를 hash로 접는다. bucket index는 호출부에서 <see cref="TableMask"/>와 AND한다.
    /// 병렬 조향 job이 grid 열거 순서를 동일하게 재현하도록 단일 원천으로 노출한다(값 불변).
    /// </summary>
    public static int HashCell(int cellX, int cellY)
    {
        unchecked
        {
            return (cellX * HashPrimeX) ^ (cellY * HashPrimeY);
        }
    }

    /// <summary>
    /// team id(>=0)를 <see cref="_bucketTeamMask"/>/전투 skip 필터가 공유하는 bit로 접는다.
    /// 32팀 경계를 넘어도 안전하도록 team&gt;=31은 모두 최상위 bit(catch-all)에 모은다. 이러면 서로 다른 high-team이
    /// 한 bit로 별칭되지만, 전투 skip은 team&gt;=31 agent를 아예 건너뛰지 않으므로(항상 full 스캔) false skip이 생기지 않는다.
    /// 이 프로젝트의 teamCount는 1+RivalCount(&lt;=4)라 실제로는 team&lt;31 경로만 탄다.
    /// </summary>
    public static int TeamBit(int team)
    {
        return 1 << (team < 31 ? team : 31);
    }
}
