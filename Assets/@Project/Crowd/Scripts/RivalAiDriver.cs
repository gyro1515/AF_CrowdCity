using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

/// <summary>
/// rival crowd 하나의 heading을 결정하는 plain C# AI다.
/// CrowdRoot가 AiDecideInterval(시뮬레이션 시간) 주기로 호출하며,
/// 같은 seed로 생성하면 같은 세계 상태 순서에 대해 항상 같은 결정 열을 낸다.
/// </summary>
public sealed class RivalAiDriver
{
    // 좌우 probe의 중심 대비 각도(도). 계약(INTERFACES.md C2)에 ±35°로 고정되어 있다.
    private const float SideProbeAngleDeg = 35f;

    // Raycast 원점을 leader 위치에서 몸통 중심 높이로 올린다.
    // 설계의 배치 유효성 검사(pos + 0.9*up)와 같은 높이 규약이며, 지면 높이의 스침 판정을 피한다.
    private const float ProbeOriginHeight = 0.9f;

    // 방향 vector가 사실상 0인지 판별하는 임계값(제곱 크기).
    private const float DegenerateSqr = 1e-6f;

    // 벽 탐지 raycast용 레이어 마스크. IgnoreLayerCollision은 raycast에 영향을 주지 않으므로,
    // 유닛(크라우드 CharacterController, "Unit" 레이어)을 벽으로 오인하지 않도록 기본 raycast 레이어에서 Unit만 제외해
    // 타입 최초 사용 시 1회 계산한다(레이어당 NameToLayer 반복 호출 방지).
    private static readonly int WallProbeMask = Physics.DefaultRaycastLayers & ~(1 << LayerMask.NameToLayer("Unit"));

    private readonly int _teamId;
    private readonly GameConfigSO _config;
    private readonly System.Random _random;
    // QueryCircle 결과 재사용 buffer. 최악의 경우 시야 안에 전체 agent가 들어올 수 있어 전체 agent 수
    // (리더 1 + 라이벌 + 중립)로 사전할당해 tick 중 List 재할당(hot-path GC alloc)을 없앤다. 과거 상수 160은
    // 중립 800에서 시야 밀집 시 재할당을 유발했다. 용량만 정하며 질의 결과/순서/판정에는 영향이 없다.
    private readonly List<int> _neutralQuery;

    /// <summary>
    /// 담당 팀 id, 설정 SO, 결정적 seed로 driver를 생성한다.
    /// seed는 좌우 clearance가 정확히 동률일 때의 방향 선택에만 쓰이므로 결정성이 유지된다.
    /// </summary>
    public RivalAiDriver(int teamId, GameConfigSO config, int seed, int spawnCount)
    {
        _teamId = teamId;
        _config = config;
        _random = new System.Random(seed);
        _neutralQuery = new List<int>(1 + config.RivalCount + spawnCount);
    }

    /// <summary>
    /// 세계 상태를 읽어 이 rival crowd가 향할 heading(도, Unity yaw: 0도 = +Z)을 반환한다.
    /// 호출 중 heap 할당이 없다(질의 buffer 재사용).
    /// </summary>
    // Called by CrowdRoot every AiDecideInterval (sim time). Reads world, returns desired heading in degrees.
    // Policy: nearest crowd with MemberCount > FleeSizeRatio * own within AiVisionRadius -> flee (opposite dir);
    // else nearest crowd with MemberCount < HuntSizeRatio * own (own >= HuntMinCount) within AiVisionRadius -> chase;
    // else densest neutral direction (grid QueryCircle around self, neutral majority vector); fallback: keep heading.
    // Wall avoidance: Physics.Raycast forward/±35° at WallProbeDistance from leader position; blocked -> rotate toward clearest probe.
    public float DecideHeadingDeg(CrowdModel self, IReadOnlyList<CrowdModel> crowds, AgentBuffer buffer, NativeArray<Vector2> prevPos, SpatialGrid grid)
    {
        if (self.Eliminated || self.Leader == null || self.LeaderAgentIndex < 0)
        {
            // 탈락한 crowd는 더 결정할 것이 없다. 기존 heading을 그대로 돌려준다.
            return self.HeadingDeg;
        }

        Vector2 selfPos = prevPos[self.LeaderAgentIndex];
        int ownCount = self.MemberCount;
        float visionSqr = _config.AiVisionRadius * _config.AiVisionRadius;
        bool canHunt = ownCount >= _config.HuntMinCount;

        // 시야 안의 다른 crowd 중 도주 대상(자기보다 충분히 큰)과 추격 대상(자기보다 충분히 작은)의 최근접을 찾는다.
        // crowd 간 거리는 leader 위치(buffer snapshot) 기준이다.
        int fleeIndex = -1;
        float fleeBestSqr = float.MaxValue;
        int huntIndex = -1;
        float huntBestSqr = float.MaxValue;

        for (int i = 0; i < crowds.Count; i++)
        {
            CrowdModel other = crowds[i];
            if (other.TeamId == _teamId || other.Eliminated || other.LeaderAgentIndex < 0)
            {
                continue;
            }

            float distSqr = (prevPos[other.LeaderAgentIndex] - selfPos).sqrMagnitude;
            if (distSqr > visionSqr)
            {
                continue;
            }

            if (other.MemberCount > _config.FleeSizeRatio * ownCount)
            {
                if (distSqr < fleeBestSqr)
                {
                    fleeBestSqr = distSqr;
                    fleeIndex = i;
                }
            }
            else if (canHunt && other.MemberCount < _config.HuntSizeRatio * ownCount && distSqr < huntBestSqr)
            {
                huntBestSqr = distSqr;
                huntIndex = i;
            }
        }

        float desiredDeg = self.HeadingDeg;

        if (fleeIndex >= 0)
        {
            // 도주가 최우선: 가장 가까운 위협의 반대 방향.
            Vector2 away = selfPos - prevPos[crowds[fleeIndex].LeaderAgentIndex];
            if (away.sqrMagnitude > DegenerateSqr)
            {
                desiredDeg = ToHeadingDeg(away);
            }
        }
        else if (huntIndex >= 0)
        {
            // 추격: 가장 가까운 먹잇감을 향한 방향.
            Vector2 toward = prevPos[crowds[huntIndex].LeaderAgentIndex] - selfPos;
            if (toward.sqrMagnitude > DegenerateSqr)
            {
                desiredDeg = ToHeadingDeg(toward);
            }
        }
        else
        {
            // 중립 밀도: 시야 반경 질의 후 중립별 단위 offset을 합산해 '수가 많은 쪽' 방향을 얻는다.
            CrowdSimCounters.SetSource(CrowdSimCounters.QuerySource.RivalAi); // 무침습 계측(Enabled=false면 no-op).
            grid.QueryCircle(selfPos, _config.AiVisionRadius, _neutralQuery);
            Vector2 densitySum = Vector2.zero;
            for (int i = 0; i < _neutralQuery.Count; i++)
            {
                int agentIndex = _neutralQuery[i];
                if (buffer.Team[agentIndex] != AgentBuffer.NeutralTeam)
                {
                    continue;
                }

                Vector2 offset = prevPos[agentIndex] - selfPos;
                float magnitude = offset.magnitude;
                if (magnitude > 1e-3f)
                {
                    // 거리와 무관하게 개체당 동일 가중치(거리 편향 방지).
                    densitySum += offset / magnitude;
                }
            }

            if (densitySum.sqrMagnitude > DegenerateSqr)
            {
                desiredDeg = ToHeadingDeg(densitySum);
            }
            // 시야에 중립이 없으면 기존 heading 유지(fallback: keep heading).
        }

        float finalDeg = ApplyWallAvoidance(self.Leader.transform.position, desiredDeg);
        CrowdOracleRecorder.RecordRivalDecision(_teamId, desiredDeg, finalDeg); // 무침습 관찰(Enabled=false면 no-op).
        return finalDeg;
    }

    /// <summary>
    /// 원하는 heading 기준 정면/±35° 3방향 raycast로 벽을 살핀다.
    /// 정면이 막혀 있으면 clearance(막히지 않으면 probe 거리, 막히면 hit 거리)가 가장 큰 probe 방향으로 회전한다.
    /// 좌우 clearance가 정확히 동률이면 seed 기반 Random으로 한쪽을 고른다(같은 seed면 결정적).
    /// </summary>
    private float ApplyWallAvoidance(Vector3 leaderPosition, float desiredDeg)
    {
        Vector3 origin = leaderPosition + Vector3.up * ProbeOriginHeight;
        float probeDistance = _config.WallProbeDistance;

        float centerClear = ProbeClearance(origin, desiredDeg, probeDistance);
        if (centerClear >= probeDistance)
        {
            return desiredDeg;
        }

        float leftDeg = desiredDeg - SideProbeAngleDeg;
        float rightDeg = desiredDeg + SideProbeAngleDeg;
        float leftClear = ProbeClearance(origin, leftDeg, probeDistance);
        float rightClear = ProbeClearance(origin, rightDeg, probeDistance);

        if (leftClear > rightClear)
        {
            return leftClear > centerClear ? leftDeg : desiredDeg;
        }

        if (rightClear > leftClear)
        {
            return rightClear > centerClear ? rightDeg : desiredDeg;
        }

        // 좌우 동률. 정면이 그보다 낫거나 같으면 그대로 두고, 아니면 한쪽을 결정적 Random으로 고른다.
        if (centerClear >= leftClear)
        {
            return desiredDeg;
        }

        return _random.Next(2) == 0 ? leftDeg : rightDeg;
    }

    private static float ProbeClearance(Vector3 origin, float headingDeg, float maxDistance)
    {
        float rad = headingDeg * Mathf.Deg2Rad;
        Vector3 direction = new Vector3(Mathf.Sin(rad), 0f, Mathf.Cos(rad));
        RaycastHit hit;
        if (Physics.Raycast(origin, direction, out hit, maxDistance, WallProbeMask))
        {
            return hit.distance;
        }

        return maxDistance;
    }

    private static float ToHeadingDeg(Vector2 dirXz)
    {
        // world XZ 방향 -> Unity yaw(0도 = +Z). Human.SetHeadingAndSpeed의 Quaternion.Euler(0, deg, 0)와 같은 규약이다.
        return Mathf.Atan2(dirXz.x, dirXz.y) * Mathf.Rad2Deg;
    }
}
