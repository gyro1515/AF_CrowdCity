using System;
using System.Collections.Generic;
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

        int tableSize = 16;
        while (tableSize < capacity * 2)
        {
            tableSize <<= 1;
        }

        _tableMask = tableSize - 1;
        _bucketHead = new int[tableSize];
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
        }

        _count = buffer.Count;
        Vector2[] positions = buffer.Pos;
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

    private static int HashCell(int cellX, int cellY)
    {
        unchecked
        {
            return (cellX * HashPrimeX) ^ (cellY * HashPrimeY);
        }
    }
}
