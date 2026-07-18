using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

/// <summary>
/// M2-a3: SDF 이동 경로(<c>_useSdfSolver</c> ON + WallField 로드됨)에서 팔로워별 이동 해소를 계산하는 Mono(비-Burst) <see cref="IJobParallelFor"/>다.
/// <c>SteerFollowersAndNeutrals</c> 직렬 이동 루프의 SDF 분기(<see cref="WallSolver.Resolve"/> → walkable clamp → 실제 변위 기반 속도 재조정 → blocked-damping)와
/// <b>byte-identical</b>하게 산출한다: 동일한 <see cref="Vector2"/>/<see cref="Mathf"/> 연산을 동일 순서로, 동일한 clearance·clamp·rescale·damping 공식으로 수행한다.
/// <c>[BurstCompile]</c> 없음, <c>math</c>/<c>Mathf</c> 치환 없음, RNG 없음.
///
/// 각 <c>Execute(k)</c>는 read-only 입력(현재 위치 <see cref="PositionCurrent"/>, 명령 속도 <see cref="CommandedVelocity"/>, read-only <see cref="WallFieldView"/>, scale)만 읽고
/// 자기 슬롯 <see cref="PositionNext"/>[k]/<see cref="VelocityOut"/>[k]에만 쓴다(교차 agent 쓰기 없음 → parallel-for 인덱스 순서와 무관하게 결정적).
/// WallField 조회는 read-only이므로 안전하다. 이동의 <b>제시(presentation)</b>(transform.position 쓰기, <c>Human.SetHeadingAndSpeed</c>, 시각 배열)는
/// job 밖 직렬 main-thread 패스가 원래 팔로워 순서(team·f 오름차순)로 수행한다.
///
/// walkable clamp가 finalPos를 항상 <c>(Clamp(solved.x), groundY, Clamp(solved.z))</c>로 만든다는 점을 이용해(원본의 조건부 transform 쓰기 두 분기가 같은 위치 값을 낳음)
/// transform round-trip 없이 직접 clamp한다(월드 아이덴티티 전제 = M2-a2와 동일 근거). y는 항상 groundY라 XZ만 산출한다.
/// </summary>
public struct FollowerSdfMoveJob : IJobParallelFor
{
    // 슬롯 k → agent index(직렬 프리패스가 team·f 오름차순으로 평탄화). scale/출력 매핑에 쓴다.
    [ReadOnly] public NativeArray<int> FollowerList;

    // 슬롯 k → 이동 전 현재 world XZ(직렬 프리패스가 followerTransform.position에서 캡처 = 원본 직렬 루프의 current와 동일 원천).
    [ReadOnly] public NativeArray<Vector2> PositionCurrent;

    // 슬롯 k → SteeringForceJob이 산출한 이동 이전 명령 속도(직렬 적분 결과와 byte-identical).
    [ReadOnly] public NativeArray<Vector2> CommandedVelocity;

    // agent index → per-agent scale(clearance = WallClearance * Scale[index], 원본과 동일).
    [ReadOnly] public NativeArray<float> Scale;

    // read-only 벽 거리장. WallField.AsView()의 구성요소를 전달받아 Execute에서 뷰를 재구성한다(중첩 NativeContainer parallel-for 제약 회피).
    [ReadOnly] public NativeArray<float> WallDist;
    public float WallOriginX;
    public float WallOriginZ;
    public float WallCellSize;
    public float WallInvCellSize;
    public int WallCols;
    public int WallRows;
    public float WallMaxDistance;
    public float WallBilinearBias;

    // config/scene scalar snapshot(원본 직렬 루프의 로컬과 동일값).
    public float Dt;
    public float WallClearance;   // WallCollisionClearance 상수.
    public float RegionMinX;
    public float RegionMaxX;
    public float RegionMinZ;
    public float RegionMaxZ;
    public float BlockedDamping;  // _config.FollowerBlockedDamping.

    // 출력(슬롯 k): 해소+clamp 후 world XZ, 재조정+감쇠 후 팔로워 속도. 직렬 제시 패스가 같은 k 순서로 읽어 transform/애니메이션에 반영한다.
    public NativeArray<Vector2> PositionNext;
    public NativeArray<Vector2> VelocityOut;

    public void Execute(int k)
    {
        int index = FollowerList[k];
        Vector2 current = PositionCurrent[k];
        Vector2 commandedVel = CommandedVelocity[k];

        // ① 명령 변위로 SDF 이동 해소(원본 ApplyHorizontalMove SDF 분기: move = velocity*dt, delta로 Resolve).
        Vector2 move = commandedVel * Dt;
        float clearance = WallClearance * Scale[index];
        WallFieldView field = new WallFieldView(
            WallDist, WallOriginX, WallOriginZ, WallCellSize, WallInvCellSize, WallCols, WallRows, WallMaxDistance, WallBilinearBias);
        Vector2 solved = WallSolver.Resolve(in field, current, move, clearance);

        // ② walkable clamp. 원본은 read-back한 solved를 clamp하지만 월드 아이덴티티에서 read-back은 no-op이라 solved를 직접 clamp한다.
        //    두 clamp 분기(조건 참/거짓)가 낳는 finalPos 위치 값은 동일하므로 항상 (Clamp(x), groundY, Clamp(z))로 산출한다.
        float clampedX = Mathf.Clamp(solved.x, RegionMinX, RegionMaxX);
        float clampedZ = Mathf.Clamp(solved.y, RegionMinZ, RegionMaxZ);
        Vector2 finalXZ = new Vector2(clampedX, clampedZ);

        // ③ 실제 수평 변위/dt로 속도 재조정(원본과 동일: 명령 방향 분해 → 접선 유지 + 전진 성분 [0,|명령|] 상한, 역방향 제거).
        Vector2 actualVel = new Vector2(finalXZ.x - current.x, finalXZ.y - current.y) / Dt;
        Vector2 velocity;
        if (commandedVel.sqrMagnitude > 1e-6f)
        {
            Vector2 dir = commandedVel.normalized;
            float along = Vector2.Dot(actualVel, dir);
            Vector2 tangential = actualVel - along * dir;
            float alongKept = Mathf.Clamp(along, 0f, commandedVel.magnitude);
            velocity = tangential + alongKept * dir;
        }
        else
        {
            velocity = actualVel;
        }

        // ④ blocked-damping(원본과 동일): 명령 대비 실제 이동 비율 progress로 막힌 팔로워 속도만 감쇠.
        float commandedMag = commandedVel.magnitude;
        float progress = commandedMag > 1e-4f ? actualVel.magnitude / commandedMag : 1f;
        const float BlockedThreshold = 0.5f;
        float dampFactor = Mathf.Lerp(BlockedDamping, 1f, Mathf.Clamp01(progress / BlockedThreshold));
        velocity *= dampFactor;

        PositionNext[k] = finalXZ;
        VelocityOut[k] = velocity;
    }
}
