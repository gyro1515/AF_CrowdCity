using System;
using Unity.Collections;
using UnityEngine;

/// <summary>
/// Crowd 시뮬레이션의 권한 있는(authoritative) agent 상태를 <see cref="Allocator.Persistent"/> <see cref="NativeArray{T}"/>로 소유하는 컨테이너다(M2-a1 storage 이관).
/// SoA agent buffer(<see cref="Agents"/>)와 CrowdRoot의 병렬 조향 상태(follower 속도, leader yaw, 배회 heading/timer)를 한곳에서 소유해
/// 소유자(CrowdRoot)가 init에서 1회 생성하고 shutdown에서 1회 해제하도록 lifecycle을 단일화한다. per-tick 할당은 없다.
///
/// 이 이관은 storage 전용이다: 값/열거 순서/산술/RNG/tick 순서를 바꾸지 않으며, 이동의 tick-time 권한은 여전히 Unity Transform에 있고
/// MirrorPositionsToBuffer가 그 결과를 <see cref="Agents"/>로 미러링한다. 저장소만 관리형 배열에서 NativeArray로 바뀔 뿐 결과는 byte-identical하다.
/// allowUnsafeCode=0을 유지하려고 안전 인덱싱만 쓴다.
/// </summary>
public sealed class CrowdSimState : IDisposable
{
    /// <summary>
    /// 권한 있는 SoA agent buffer(Id/Team/IsLeader/Pos/Scale)다. 이 인스턴스가 소유·해제한다.
    /// </summary>
    public readonly AgentBuffer Agents;

    /// <summary>
    /// 팔로워 조향의 현재 속도 상태(agent index별)다. 가속 제한 적분에 쓴다.
    /// </summary>
    public NativeArray<Vector2> FollowerVelocity;

    /// <summary>
    /// team별 리더의 현재 실제 yaw(도)다.
    /// </summary>
    public NativeArray<float> LeaderYawDeg;

    /// <summary>
    /// 중립 agent의 배회 heading(도)이다(agent index별).
    /// </summary>
    public NativeArray<float> WanderHeadingDeg;

    /// <summary>
    /// 중립 agent의 방향 재선택 잔여 시간(초)이다(agent index별).
    /// </summary>
    public NativeArray<float> WanderTimer;

    /// <summary>
    /// agent capacity와 team 수에 맞춰 모든 Persistent NativeArray를 미리 할당한다.
    /// 도중 할당이 실패하면 이미 만든 배열을 해제하고 예외를 다시 던진다(부분 할당 누수 방지).
    /// </summary>
    public CrowdSimState(int agentCapacity, int teamCount)
    {
        try
        {
            Agents = new AgentBuffer(agentCapacity);
            FollowerVelocity = new NativeArray<Vector2>(agentCapacity, Allocator.Persistent);
            LeaderYawDeg = new NativeArray<float>(teamCount, Allocator.Persistent);
            WanderHeadingDeg = new NativeArray<float>(agentCapacity, Allocator.Persistent);
            WanderTimer = new NativeArray<float>(agentCapacity, Allocator.Persistent);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// 내부 저장소가 할당되어 있는지 여부다(idempotent Dispose 가드).
    /// </summary>
    public bool IsCreated => Agents != null && Agents.IsCreated;

    /// <summary>
    /// 소유한 모든 Persistent 저장소를 해제한다. 소유자 lifecycle에서 반드시 호출하며, 여러 번 호출해도 안전하다.
    /// </summary>
    public void Dispose()
    {
        Agents?.Dispose();

        if (FollowerVelocity.IsCreated)
        {
            FollowerVelocity.Dispose();
        }

        if (LeaderYawDeg.IsCreated)
        {
            LeaderYawDeg.Dispose();
        }

        if (WanderHeadingDeg.IsCreated)
        {
            WanderHeadingDeg.Dispose();
        }

        if (WanderTimer.IsCreated)
        {
            WanderTimer.Dispose();
        }
    }
}
