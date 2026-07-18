using System;
using Unity.Collections;
using UnityEngine;

/// <summary>
/// Simulation kernel이 읽는 agent 상태의 SoA(Structure of Arrays) buffer이다.
/// 스폰 완료 이후 population은 일정하며(전향은 agent를 제거하지 않는다), Team과 IsLeader의 최종 권한은 이 buffer에 있다.
/// Resolver는 이 buffer를 불변 snapshot으로만 읽고, 변경은 소유자(CrowdRoot)의 commit 단계에서만 수행한다.
/// 저장소는 <see cref="Allocator.Persistent"/> <see cref="NativeArray{T}"/>이며(M2-a1 storage 이관), 값/열거/산술은 관리형 배열과 byte-identical하다.
/// SoA 배열은 public field로 노출한다(NativeArray는 struct라 property 반환값에는 인덱서 대입이 불가 — CS1612).
/// 소유자는 이 인스턴스를 자신의 lifecycle에서 <see cref="Dispose"/>해야 한다(NativeArray 누수 방지). allowUnsafeCode=0을 유지하려고 안전 인덱싱만 쓴다.
/// </summary>
public sealed class AgentBuffer : IDisposable
{
    /// <summary>
    /// 중립 agent를 나타내는 team 값이다.
    /// </summary>
    public const int NeutralTeam = -1;

    /// <summary>
    /// agent 고유 id 배열이다. 삽입 순서 기준 오름차순이며 이후 변하지 않는다.
    /// </summary>
    public NativeArray<int> Id;

    /// <summary>
    /// agent 소속 team 배열이다. -1은 중립, 0은 player, 1..3은 rival이다.
    /// </summary>
    public NativeArray<int> Team;

    /// <summary>
    /// 해당 agent가 leader인지 여부이다. leader 탈락 commit 시 false로 내려간다.
    /// </summary>
    public NativeArray<bool> IsLeader;

    /// <summary>
    /// world XZ 평면 위치 배열이다. 매 sim step 시작 시 Unity transform에서 미러링된다.
    /// </summary>
    public NativeArray<Vector2> Pos;

    /// <summary>
    /// agent별 uniform 스케일 배열이다. 리더/비스케일 유닛은 1.0, 중립 스폰 시 varied scale로 채워지며 영입/전향 이후에도 불변이다.
    /// resolver가 스케일 인지 접촉/영입 반경을 pair 평균으로 계산하는 단일 진실 원천이다.
    /// </summary>
    public NativeArray<float> Scale;

    /// <summary>
    /// 지정한 capacity만큼 내부 NativeArray를 미리 할당한다(Persistent). 이후 재할당은 일어나지 않는다.
    /// </summary>
    public AgentBuffer(int capacity)
    {
        Id = new NativeArray<int>(capacity, Allocator.Persistent);
        Team = new NativeArray<int>(capacity, Allocator.Persistent);
        IsLeader = new NativeArray<bool>(capacity, Allocator.Persistent);
        Pos = new NativeArray<Vector2>(capacity, Allocator.Persistent);
        Scale = new NativeArray<float>(capacity, Allocator.Persistent);
        for (int i = 0; i < capacity; i++)
        {
            Scale[i] = 1f; // 배열 기본값 0을 1.0으로 채운다(Add 이전 읽기도 안전). 중립 스폰만 varied scale로 덮어쓴다.
        }
        Count = 0;
    }

    /// <summary>
    /// 현재 사용 중인 slot 수이다. 스폰 완료 이후에는 변하지 않는다.
    /// </summary>
    public int Count { get; private set; }

    /// <summary>
    /// 내부 NativeArray가 할당되어 조회 가능한 상태인지 여부이다(idempotent Dispose 가드).
    /// </summary>
    public bool IsCreated => Id.IsCreated;

    /// <summary>
    /// 새 agent를 다음 빈 slot에 기록하고 그 index를 반환한다.
    /// </summary>
    /// <exception cref="InvalidOperationException">buffer가 이미 capacity까지 찼으면 발생한다.</exception>
    public int Add(int id, int team, bool isLeader, Vector2 pos, float scale = 1f)
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
        Scale[index] = scale;
        Count = index + 1;
        return index;
    }

    /// <summary>
    /// 소유한 Persistent NativeArray를 모두 해제한다. 소유자 lifecycle에서 반드시 호출하며, 여러 번 호출해도 안전하다.
    /// </summary>
    public void Dispose()
    {
        if (Id.IsCreated)
        {
            Id.Dispose();
        }

        if (Team.IsCreated)
        {
            Team.Dispose();
        }

        if (IsLeader.IsCreated)
        {
            IsLeader.Dispose();
        }

        if (Pos.IsCreated)
        {
            Pos.Dispose();
        }

        if (Scale.IsCreated)
        {
            Scale.Dispose();
        }
    }
}
