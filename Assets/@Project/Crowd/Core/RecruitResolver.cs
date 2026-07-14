using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 중립 agent 하나가 어느 팀으로 합류하는지 확정한 영입 결과다.
/// </summary>
public readonly struct RecruitAssignment
{
    /// <summary>
    /// 합류하는 중립 agent의 buffer index다.
    /// </summary>
    public readonly int AgentIndex;

    /// <summary>
    /// 합류할 팀 id다.
    /// </summary>
    public readonly int ToTeam;

    /// <summary>
    /// 확정된 영입 결과를 생성한다.
    /// </summary>
    public RecruitAssignment(int agentIndex, int toTeam)
    {
        AgentIndex = agentIndex;
        ToTeam = toTeam;
    }
}

/// <summary>
/// 경합 상태의 중립 영입을 결정적으로 판정한다.
/// buffer를 읽기 전용 snapshot으로만 사용하며 절대 변경하지 않는다. 실제 반영은 호출자가 수행한다.
/// </summary>
public sealed class RecruitResolver
{
    // QueryCircle 결과 재사용 buffer. warmup 이후 per-tick 할당이 생기지 않도록 미리 확보한다.
    private readonly List<int> _candidates = new List<int>(64);

    /// <summary>
    /// results를 먼저 비운 뒤 중립 agent마다 claim을 최대 하나 판정한다.
    /// 반경 안에서 가장 가까운 non-neutral agent의 팀으로 합류하며,
    /// 거리 동률이면 claimant agent Id가 낮은 쪽이 이긴다. 결과는 grid 순회 순서와 무관하게 결정적이다.
    /// </summary>
    public void Resolve(AgentBuffer buffer, SpatialGrid grid, float recruitRadius, List<RecruitAssignment> results)
    {
        results.Clear();

        int count = buffer.Count;
        int[] team = buffer.Team;
        int[] id = buffer.Id;
        Vector2[] pos = buffer.Pos;

        // 후보 buffer를 buffer capacity까지 한 번만 키운다(축소하지 않는다).
        // capacity는 스폰 이후 고정이고 QueryCircle은 agent를 중복 없이 최대 buffer 크기만큼 담으므로,
        // 첫 tick 이후에는 dense query에서도 재할당(per-tick GC)이 발생하지 않는다.
        if (_candidates.Capacity < id.Length)
        {
            _candidates.Capacity = id.Length;
        }

        for (int i = 0; i < count; i++)
        {
            if (team[i] != AgentBuffer.NeutralTeam)
            {
                continue;
            }

            Vector2 neutralPos = pos[i];
            grid.QueryCircle(neutralPos, recruitRadius, _candidates);

            int bestIndex = -1;
            float bestSqrDist = float.MaxValue;
            int bestId = int.MaxValue;

            for (int c = 0; c < _candidates.Count; c++)
            {
                int candidateIndex = _candidates[c];
                if (team[candidateIndex] == AgentBuffer.NeutralTeam)
                {
                    continue;
                }

                float dx = pos[candidateIndex].x - neutralPos.x;
                float dy = pos[candidateIndex].y - neutralPos.y;
                float sqrDist = dx * dx + dy * dy;
                int candidateId = id[candidateIndex];

                // 더 가까우면 교체하고, 거리 동률이면 낮은 Id가 이긴다.
                // (sqrDist, Id) 비교는 전순서이므로 grid가 어떤 순서로 후보를 주든 결과가 같다.
                if (sqrDist < bestSqrDist || (sqrDist == bestSqrDist && candidateId < bestId))
                {
                    bestIndex = candidateIndex;
                    bestSqrDist = sqrDist;
                    bestId = candidateId;
                }
            }

            if (bestIndex >= 0)
            {
                results.Add(new RecruitAssignment(i, team[bestIndex]));
            }
        }
    }
}
