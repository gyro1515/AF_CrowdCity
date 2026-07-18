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

    // ---- M2-a3 병렬 조향(SteeringForceJob) 전용 per-tick scratch. tick마다 채워 쓰고 재사용하며, 값은 job 이후에만 유효하다. ----

    /// <summary>
    /// 병렬 FORCE job이 팔로워별로 산출한 명령 속도(가속 제한 적분 직후 값)다. 인덱스는 팔로워 리스트 위치(k)이며, 직렬 이동 단계가 같은 순서로 읽는다.
    /// </summary>
    public NativeArray<Vector2> CommandedVelocity;

    /// <summary>
    /// 이번 tick의 팔로워 agent index를 team 오름차순·팀 내 f 오름차순으로 평탄화한 작업 리스트다(직렬 프리패스에서 채운다).
    /// </summary>
    public NativeArray<int> FollowerList;

    /// <summary>
    /// team별 arrive 중심점(리더 뒤 단일 중심)이다. 직렬 프리패스에서 이번 tick 리더 위치로 계산해 job에 전달한다.
    /// </summary>
    public NativeArray<Vector2> CenterPerTeam;

    /// <summary>
    /// team별 유효 arrive 반경(무리 크기에 √비례 확장 포함)이다. 직렬 프리패스에서 계산해 job에 전달한다.
    /// </summary>
    public NativeArray<float> ArriveRadiusPerTeam;

    // ---- M2-a3 SDF 이동 병렬화(FollowerSdfMoveJob) 전용 per-tick scratch. 팔로워 슬롯 k로 인덱싱하며, 직렬 제시 패스가 같은 순서로 읽는다. ----

    /// <summary>
    /// 이동 전 팔로워 현재 world XZ다(슬롯 k). 직렬 프리패스가 followerTransform.position에서 캡처해 이동 job에 전달한다(원본 직렬 루프의 current와 동일 원천).
    /// </summary>
    public NativeArray<Vector2> MovePositionCurrent;

    /// <summary>
    /// SDF 해소+walkable clamp 후 팔로워 world XZ다(슬롯 k). 직렬 제시 패스가 transform.position에 반영한다.
    /// </summary>
    public NativeArray<Vector2> MovePositionNext;

    /// <summary>
    /// 실제 변위 기반 재조정+blocked-damping 후 팔로워 속도다(슬롯 k). 직렬 제시 패스가 _followerVelocity/애니메이션에 반영한다.
    /// </summary>
    public NativeArray<Vector2> MoveVelocityOut;

    // grid의 관리형 내부 배열을 tick마다 복사해 두는 native snapshot. job이 QueryCircle/QueryCircleCapped 열거를 in-place로 재현한다.
    public NativeArray<int> GridBucketHead;
    public NativeArray<int> GridNext;
    public NativeArray<int> GridCellX;
    public NativeArray<int> GridCellY;
    public NativeArray<Vector2> GridPos;

    /// <summary>
    /// 밀도장(grid-averaged O(N)) 분리 전용 팀별 cell 격자다(길이 = teamCount * densityCellsPerTeam). 인덱스는 teamBase + cy*cols + cx.
    /// 밀도장 분리 ON일 때만 직렬 프리패스가 tick마다 0으로 비운 뒤 같은 팀 인원 수를 누적하고, SteeringForceJob이 read-only로 gradient를 읽는다.
    /// </summary>
    public NativeArray<int> DensityField;

    /// <summary>
    /// agent capacity와 team 수에 맞춰 모든 Persistent NativeArray를 미리 할당한다.
    /// 도중 할당이 실패하면 이미 만든 배열을 해제하고 예외를 다시 던진다(부분 할당 누수 방지).
    /// </summary>
    public CrowdSimState(int agentCapacity, int teamCount, int densityFieldLength)
    {
        try
        {
            Agents = new AgentBuffer(agentCapacity);
            FollowerVelocity = new NativeArray<Vector2>(agentCapacity, Allocator.Persistent);
            LeaderYawDeg = new NativeArray<float>(teamCount, Allocator.Persistent);
            WanderHeadingDeg = new NativeArray<float>(agentCapacity, Allocator.Persistent);
            WanderTimer = new NativeArray<float>(agentCapacity, Allocator.Persistent);

            // M2-a3 병렬 조향 scratch. GridBucketHead는 grid와 동일한 table 크기 공식을 단일 원천에서 가져온다.
            CommandedVelocity = new NativeArray<Vector2>(agentCapacity, Allocator.Persistent);
            MovePositionCurrent = new NativeArray<Vector2>(agentCapacity, Allocator.Persistent);
            MovePositionNext = new NativeArray<Vector2>(agentCapacity, Allocator.Persistent);
            MoveVelocityOut = new NativeArray<Vector2>(agentCapacity, Allocator.Persistent);
            FollowerList = new NativeArray<int>(agentCapacity, Allocator.Persistent);
            CenterPerTeam = new NativeArray<Vector2>(teamCount, Allocator.Persistent);
            ArriveRadiusPerTeam = new NativeArray<float>(teamCount, Allocator.Persistent);
            GridBucketHead = new NativeArray<int>(SpatialGrid.ComputeTableSize(agentCapacity), Allocator.Persistent);
            GridNext = new NativeArray<int>(agentCapacity, Allocator.Persistent);
            GridCellX = new NativeArray<int>(agentCapacity, Allocator.Persistent);
            GridCellY = new NativeArray<int>(agentCapacity, Allocator.Persistent);
            GridPos = new NativeArray<Vector2>(agentCapacity, Allocator.Persistent);
            DensityField = new NativeArray<int>(densityFieldLength, Allocator.Persistent);
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

        if (CommandedVelocity.IsCreated)
        {
            CommandedVelocity.Dispose();
        }

        if (MovePositionCurrent.IsCreated)
        {
            MovePositionCurrent.Dispose();
        }

        if (MovePositionNext.IsCreated)
        {
            MovePositionNext.Dispose();
        }

        if (MoveVelocityOut.IsCreated)
        {
            MoveVelocityOut.Dispose();
        }

        if (FollowerList.IsCreated)
        {
            FollowerList.Dispose();
        }

        if (CenterPerTeam.IsCreated)
        {
            CenterPerTeam.Dispose();
        }

        if (ArriveRadiusPerTeam.IsCreated)
        {
            ArriveRadiusPerTeam.Dispose();
        }

        if (GridBucketHead.IsCreated)
        {
            GridBucketHead.Dispose();
        }

        if (GridNext.IsCreated)
        {
            GridNext.Dispose();
        }

        if (GridCellX.IsCreated)
        {
            GridCellX.Dispose();
        }

        if (GridCellY.IsCreated)
        {
            GridCellY.Dispose();
        }

        if (GridPos.IsCreated)
        {
            GridPos.Dispose();
        }

        if (DensityField.IsCreated)
        {
            DensityField.Dispose();
        }
    }
}
