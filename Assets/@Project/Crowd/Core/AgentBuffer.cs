using System;
using UnityEngine;

/// <summary>
/// Simulation kernel이 읽는 agent 상태의 SoA(Structure of Arrays) buffer이다.
/// 스폰 완료 이후 population은 일정하며(전향은 agent를 제거하지 않는다), Team과 IsLeader의 최종 권한은 이 buffer에 있다.
/// Resolver는 이 buffer를 불변 snapshot으로만 읽고, 변경은 소유자(CrowdRoot)의 commit 단계에서만 수행한다.
/// </summary>
public sealed class AgentBuffer
{
    /// <summary>
    /// 중립 agent를 나타내는 team 값이다.
    /// </summary>
    public const int NeutralTeam = -1;

    /// <summary>
    /// 지정한 capacity만큼 내부 배열을 미리 할당한다. 이후 재할당은 일어나지 않는다.
    /// </summary>
    public AgentBuffer(int capacity)
    {
        Id = new int[capacity];
        Team = new int[capacity];
        IsLeader = new bool[capacity];
        Pos = new Vector2[capacity];
        Count = 0;
    }

    /// <summary>
    /// 현재 사용 중인 slot 수이다. 스폰 완료 이후에는 변하지 않는다.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// agent 고유 id 배열이다. 삽입 순서 기준 오름차순이며 이후 변하지 않는다.
    /// </summary>
    public int[] Id { get; }

    /// <summary>
    /// agent 소속 team 배열이다. -1은 중립, 0은 player, 1..3은 rival이다.
    /// </summary>
    public int[] Team { get; }

    /// <summary>
    /// 해당 agent가 leader인지 여부이다. leader 탈락 commit 시 false로 내려간다.
    /// </summary>
    public bool[] IsLeader { get; }

    /// <summary>
    /// world XZ 평면 위치 배열이다. 매 sim step 시작 시 Unity transform에서 미러링된다.
    /// </summary>
    public Vector2[] Pos { get; }

    /// <summary>
    /// 새 agent를 다음 빈 slot에 기록하고 그 index를 반환한다.
    /// </summary>
    /// <exception cref="InvalidOperationException">buffer가 이미 capacity까지 찼으면 발생한다.</exception>
    public int Add(int id, int team, bool isLeader, Vector2 pos)
    {
        if (Count >= Id.Length)
        {
            throw new InvalidOperationException(
                $"AgentBuffer가 가득 찼습니다. capacity={Id.Length}");
        }

        int index = Count;
        Id[index] = id;
        Team[index] = team;
        IsLeader[index] = isLeader;
        Pos[index] = pos;
        Count = index + 1;
        return index;
    }
}
