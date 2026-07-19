using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

/// <summary>
/// Stage A: SDF 이동 경로(<c>_useSdfSolver</c> ON + WallField 로드됨)에서 중립(neutral)별 배회 이동 해소를 계산하는 Burst <see cref="IJobParallelFor"/>다(<c>[BurstCompile]</c>, FloatMode.Strict).
/// <c>SteerFollowersAndNeutrals</c> 직렬 중립 배회 루프의 SDF 분기(<c>ApplyHorizontalMove</c> SDF 경로 → walkable clamp)와 <b>동일한</b> 산술을 수행한다:
/// 동일한 clearance(= WallClearance * scale) 공식으로 <see cref="WallSolver.Resolve"/>한 뒤 walkable 영역으로 clamp한다.
/// 중립은 조향 속도 상태가 없으므로(팔로워와 달리) 속도 재조정도 blocked-damping도 없다: job은 오직 <see cref="WallSolver.Resolve"/> + <see cref="Mathf.Clamp"/>만 한다.
/// <see cref="WallSolver.Resolve"/>(blittable 뷰 오버로드)는 이 job을 통해 Burst로 함께 컴파일된다. Burst(Strict)라 관리형 직렬 경로와 near-Mono지만 bit-identical하지는 않다. <c>math</c>/<c>Mathf</c> 치환 없음, RNG 없음.
///
/// 각 <c>Execute(k)</c>는 read-only 입력(현재 위치 <see cref="PositionCurrent"/>, 명령 변위 <see cref="CommandedDelta"/>, read-only <see cref="WallFieldView"/>, scale)만 읽고
/// 자기 슬롯 <see cref="PositionNext"/>[k]에만 쓴다(교차 agent 쓰기 없음 → parallel-for 인덱스 순서와 무관하게 결정적).
/// RNG draw(<c>_rng</c>)·<c>Physics.Raycast</c>(RepickWanderHeading)·<c>Mathf.Sin/Cos</c>·<c>CrowdSimCounters.CountSdfResolve</c>는 job 밖 직렬 프리패스가 원래 agent-index 오름차순으로 수행하고,
/// 이동의 <b>제시(presentation)</b>(transform.position 쓰기, <c>Human.SetHeadingAndSpeed</c>, 시각 배열)도 job 밖 직렬 main-thread 패스가 같은 순서로 수행한다.
///
/// walkable clamp가 finalPos를 항상 <c>(Clamp(solved.x), groundY, Clamp(solved.z))</c>로 만든다는 점을 이용해(원본의 조건부 transform 쓰기 두 분기가 같은 위치 값을 낳음)
/// transform round-trip 없이 직접 clamp한다(월드 아이덴티티 전제 = <see cref="FollowerSdfMoveJob"/>과 동일 근거). y는 항상 groundY라 XZ만 산출한다.
/// </summary>
[BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
public struct NeutralSdfMoveJob : IJobParallelFor
{
    // 슬롯 k → agent index(직렬 프리패스가 agent-index 오름차순으로 평탄화). scale/출력 매핑에 쓴다.
    [ReadOnly] public NativeArray<int> NeutralList;

    // 슬롯 k → 이동 전 현재 world XZ(직렬 프리패스가 neutralTransform.position에서 캡처 = 원본 직렬 루프의 이동 전 위치와 동일 원천).
    [ReadOnly] public NativeArray<Vector2> PositionCurrent;

    // 슬롯 k → 이번 tick 명령 배회 변위((sin,cos)*wanderSpeed*dt). Mathf.Sin/Cos는 프리패스(Mathf)가 계산하고 여기엔 결과만 담는다(job은 재계산하지 않는다).
    [ReadOnly] public NativeArray<Vector2> CommandedDelta;

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
    public float WallClearance;   // WallCollisionClearance 상수.
    public float RegionMinX;
    public float RegionMaxX;
    public float RegionMinZ;
    public float RegionMaxZ;

    // 출력(슬롯 k): 해소+clamp 후 world XZ. 직렬 제시 패스가 같은 k 순서로 읽어 transform/애니메이션에 반영한다.
    public NativeArray<Vector2> PositionNext;

    public void Execute(int k)
    {
        int index = NeutralList[k];
        Vector2 current = PositionCurrent[k];
        Vector2 move = CommandedDelta[k];

        // ① 명령 변위로 SDF 이동 해소(원본 ApplyHorizontalMove SDF 분기: (sin,cos)*wanderSpeed*dt를 delta로 Resolve, clearance = WallCollisionClearance * scale).
        float clearance = WallClearance * Scale[index];
        WallFieldView field = new WallFieldView(
            WallDist, WallOriginX, WallOriginZ, WallCellSize, WallInvCellSize, WallCols, WallRows, WallMaxDistance, WallBilinearBias);
        Vector2 solved = WallSolver.Resolve(in field, current, move, clearance);

        // ② walkable clamp. 원본은 read-back한 solved를 clamp하지만 월드 아이덴티티에서 read-back은 no-op이라 solved를 직접 clamp한다.
        //    두 clamp 분기(조건 참/거짓)가 낳는 finalPos 위치 값은 동일하므로 항상 (Clamp(x), groundY, Clamp(z))로 산출한다.
        float clampedX = Mathf.Clamp(solved.x, RegionMinX, RegionMaxX);
        float clampedZ = Mathf.Clamp(solved.y, RegionMinZ, RegionMaxZ);
        PositionNext[k] = new Vector2(clampedX, clampedZ);
    }
}
