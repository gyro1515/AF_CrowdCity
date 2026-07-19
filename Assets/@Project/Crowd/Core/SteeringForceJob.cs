using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

/// <summary>
/// M2-a3: 팔로워별 조향 FORCE(리더 뒤 중심으로의 arrive + 같은 팀 분리 + 가속 제한 적분)를 계산하는 Burst <see cref="IJobParallelFor"/>다(<c>[BurstCompile]</c>, FloatMode.Strict).
/// <c>SteerFollowersAndNeutrals</c> 직렬 루프의 FORCE 부분(중심 arrive, 분리 이웃 스캔+합, sepForce/desired 클램프, 가속 제한 적분)과
/// <b>동일한 산술</b>을 수행한다: 동일한 <see cref="Vector2"/>/<see cref="Mathf"/> 연산을 동일 순서로, grid 방출 순서(Y-major/X-major/LIFO 체인)와
/// 동일한 float 누적 순서로 산출한다. Burst(Strict)라 관리형 직렬 경로와 near-Mono지만 bit-identical하지는 않다. <c>math</c>/<c>Mathf</c> 치환 없음, RNG 없음.
///
/// 각 <c>Execute(k)</c>는 공유 상태를 read-only로만 읽고(직전 tick 미러 위치 <see cref="Pos"/>, 확정된 직전 grid snapshot, team/scale/속도),
/// 자기 팔로워 슬롯 <see cref="CommandedVelocity"/>[k]에만 쓴다(교차 agent 쓰기 없음 → parallel-for 인덱스 순서와 무관하게 결정적).
/// 이동/read-back/속도 재조정/SDF·CC는 job 밖 직렬 단계에서 그대로 수행한다(이 job은 이동 이전의 명령 속도까지만 만든다).
/// </summary>
[BurstCompile(FloatMode = FloatMode.Strict, FloatPrecision = FloatPrecision.Standard)]
public struct SteeringForceJob : IJobParallelFor
{
    // 이번 tick 팔로워 agent index를 team 오름차순·팀 내 f 오름차순으로 평탄화한 리스트. Execute(k) → index = FollowerList[k].
    [ReadOnly] public NativeArray<int> FollowerList;

    // team별 사전 계산값(직렬 프리패스에서 이번 tick 리더 위치로 계산). team id로 인덱싱한다.
    [ReadOnly] public NativeArray<Vector2> CenterPerTeam;
    [ReadOnly] public NativeArray<float> ArriveRadiusPerTeam;
    // LeaderRadial 전용: team별 무리 중심(직전 tick 멤버 평균 위치)이다. 직렬 프리패스에서 계산한다. Pairwise 경로에서는 읽지 않는다.
    [ReadOnly] public NativeArray<Vector2> CentroidPerTeam;

    // agent SoA(이 단계 동안 불변, read-only).
    [ReadOnly] public NativeArray<int> Team;
    [ReadOnly] public NativeArray<float> Scale;
    [ReadOnly] public NativeArray<Vector2> Pos;                 // buffer.Pos(직전 tick 미러). 팔로워 자기 위치와 이웃 위치의 단일 원천.
    [ReadOnly] public NativeArray<Vector2> FollowerVelocityIn;  // 직전 tick 최종 팔로워 속도(적분 시작값).
    [ReadOnly] public NativeArray<int> Id;                      // agent id(팔로워별). LeaderRadial 접선 성분 부호(짝/홀)에만 쓴다. Pairwise 경로에서는 읽지 않는다.

    // grid snapshot(read-only). QueryCircle/QueryCircleCapped 열거를 in-place로 재현한다.
    [ReadOnly] public NativeArray<int> BucketHead;
    [ReadOnly] public NativeArray<int> NextInBucket;
    [ReadOnly] public NativeArray<int> CellX;
    [ReadOnly] public NativeArray<int> CellY;
    [ReadOnly] public NativeArray<Vector2> GridPos;
    public int TableMask;
    public int GridCount;
    public float InvCellSize;
    // LeaderRadial 전용: bucket*TeamCount + team 인덱싱의 per-bucket per-team 팀원 점유 카운트다. 직렬 프리패스에서 채운다. Pairwise 경로에서는 읽지 않는다.
    [ReadOnly] public NativeArray<int> BucketTeamCount;
    public int TeamCount;

    // config scalar snapshot(직렬 루프의 로컬과 동일값).
    public float SepRadius;
    public float SepPush;
    public float MaxSpeed;
    public float CohesionGain;
    public float MaxAccel;
    public float QueryRadius; // sepRadius * Mathf.Max(1f, NeutralMaxScale)
    public int SepBudget;     // _config.Sim.SeparationVisitBudget
    public float Dt;

    // 분리 모드 스위치다. 0=Pairwise(기존 쌍별 분리, 기본값이자 동작 보존 경로), 1=LeaderRadial(밀도 게이트 centroid-radial 분리).
    public int SeparationMode;
    // LeaderRadial 전용 튜닝(Pairwise 경로에서는 읽지 않는다). OvercrowdThreshold=밀도 게이트 임계, RadialGain=반경 방향 세기, TangentialFraction=접선 성분 비율.
    public float OvercrowdThreshold;
    public float RadialGain;
    public float TangentialFraction;

    // 출력: 이동 이전 명령 속도(직렬 루프 velocity 적분 직후 값). 인덱스는 팔로워 슬롯 k(= parallel-for 인덱스)라 parallel-for 제약을 만족한다.
    public NativeArray<Vector2> CommandedVelocity;

    public void Execute(int k)
    {
        int index = FollowerList[k];
        int t = Team[index];
        Vector2 pos = Pos[index];
        Vector2 center = CenterPerTeam[t];
        float effectiveArriveRadius = ArriveRadiusPerTeam[t];

        // (a) 리더 뒤 단일 중심으로의 arrive.
        Vector2 toCenter = center - pos;
        float d = toCenter.magnitude;
        Vector2 desired = Vector2.zero;
        if (d > 0.0001f)
        {
            float arriveSpeed = MaxSpeed * Mathf.Min(1f, d / effectiveArriveRadius);
            desired = (toCenter / d) * (arriveSpeed * CohesionGain);
        }

        // (b) 같은 팀 분리. SeparationMode==0(Pairwise)은 기존 쌍별 분리를 그대로 수행하고, ==1(LeaderRadial)은 밀도 게이트 centroid-radial 분리를 쓴다.
        //     Pairwise 블록은 동작 보존을 위해 텍스트를 그대로 둔다(래핑만 추가). 아래 (c)~(d) blend/clamp/가속 적분은 두 모드가 공유한다.
        Vector2 separation = Vector2.zero;
        if (SeparationMode == 0 /*Pairwise*/)
        {
            // grid 열거를 QueryCircle(예산 없음)/QueryCircleCapped(예산>0)와 동일 순서로 재현하고,
            // 방출된 이웃마다 즉시 분리 기여를 누적한다(직렬의 "리스트 방출 → 순서대로 합"과 동일 결과).
            if (GridCount != 0 && QueryRadius >= 0f)
            {
                int minCellX = Mathf.FloorToInt((pos.x - QueryRadius) * InvCellSize);
                int maxCellX = Mathf.FloorToInt((pos.x + QueryRadius) * InvCellSize);
                int minCellY = Mathf.FloorToInt((pos.y - QueryRadius) * InvCellSize);
                int maxCellY = Mathf.FloorToInt((pos.y + QueryRadius) * InvCellSize);
                float radiusSq = QueryRadius * QueryRadius;
                bool capped = SepBudget > 0;
                int matched = 0;
                bool stop = false;

                for (int cellY = minCellY; cellY <= maxCellY && !stop; cellY++)
                {
                    for (int cellX = minCellX; cellX <= maxCellX && !stop; cellX++)
                    {
                        int agentIndex = BucketHead[SpatialGrid.HashCell(cellX, cellY) & TableMask];
                        while (agentIndex != -1)
                        {
                            if (CellX[agentIndex] == cellX && CellY[agentIndex] == cellY)
                            {
                                if (capped)
                                {
                                    matched++;
                                }

                                float dx = GridPos[agentIndex].x - pos.x;
                                float dy = GridPos[agentIndex].y - pos.y;
                                if (dx * dx + dy * dy <= radiusSq)
                                {
                                    // 방출된 이웃 → 직렬 분리 루프 본문과 동일하게 처리한다.
                                    int neighbor = agentIndex;
                                    if (neighbor != index && Team[neighbor] == t)
                                    {
                                        float pairSepRadius = SepRadius * 0.5f * (Scale[index] + Scale[neighbor]);
                                        Vector2 away = pos - Pos[neighbor];
                                        float dn = away.magnitude;
                                        if (dn < pairSepRadius)
                                        {
                                            if (dn > 0.0001f)
                                            {
                                                separation += away * ((1f - dn / pairSepRadius) / dn);
                                            }
                                            else
                                            {
                                                separation += new Vector2(index > neighbor ? 1f : -1f, 0f);
                                            }
                                        }
                                    }
                                }

                                // budget번째 매칭 후보까지 처리한 뒤 전체 열거를 멈춘다(QueryCircleCapped와 동일).
                                if (capped && matched >= SepBudget)
                                {
                                    stop = true;
                                    break;
                                }
                            }

                            agentIndex = NextInBucket[agentIndex];
                        }
                    }
                }
            }
        }
        else // (b') LeaderRadial: 자기 bucket의 같은 팀 점유가 임계를 넘을 때만, 팀 무리 중심에서 바깥(반경)으로 미는 분리를 준다(밀도 게이트).
        {
            int cx = Mathf.FloorToInt(pos.x * InvCellSize);
            int cy = Mathf.FloorToInt(pos.y * InvCellSize);
            int bucket = SpatialGrid.HashCell(cx, cy) & TableMask;
            int occ = BucketTeamCount[bucket * TeamCount + t];
            float over = occ - OvercrowdThreshold;
            if (over > 0f)
            {
                Vector2 fromCentroid = pos - CentroidPerTeam[t];
                float m = fromCentroid.magnitude;
                if (m >= 1e-4f) // 중심과 겹친 경우(방향 미정)는 분리를 0으로 둔다(zero-dist 가드).
                {
                    Vector2 dir = fromCentroid / m;
                    if (TangentialFraction != 0f)
                    {
                        // 접선 성분을 섞어 순수 반경 방출의 뭉침을 흩는다. 부호는 id 짝/홀로 결정해 결정적이다.
                        Vector2 perp = new Vector2(-dir.y, dir.x);
                        float sign = (Id[index] & 1) == 0 ? 1f : -1f;
                        dir = (dir + TangentialFraction * sign * perp).normalized;
                    }

                    separation = dir * (RadialGain * over);
                }
            }
        }

        // (c) 분리 기여를 maxSpeed로 상한(방향 보존)한 뒤 desired에 더하고, desired 전체를 maxSpeed로 클램프한다.
        Vector2 sepForce = separation * SepPush;
        float sepMag = sepForce.magnitude;
        if (sepMag > MaxSpeed)
        {
            sepForce *= MaxSpeed / sepMag;
        }

        desired += sepForce;

        float desiredSpeed = desired.magnitude;
        if (desiredSpeed > MaxSpeed)
        {
            desired *= MaxSpeed / desiredSpeed;
        }

        // (d) 가속 제한 적분: 현재 속도를 목표 속도 쪽으로 maxAccel*dt 이내에서만 이동시킨다.
        Vector2 velocity = FollowerVelocityIn[index];
        Vector2 dv = desired - velocity;
        float dvMag = dv.magnitude;
        float maxDelta = MaxAccel * Dt;
        if (dvMag > maxDelta)
        {
            dv *= maxDelta / dvMag;
        }

        velocity += dv;

        CommandedVelocity[k] = velocity; // 이동 이전 명령 속도. 직렬 이동 단계가 같은 k 순서로 읽어 CC/SDF 이동을 수행한다.
    }
}
