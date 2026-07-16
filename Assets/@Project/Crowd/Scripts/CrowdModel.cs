using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// crowd(팀) 하나의 현재 play session 상태를 담는 plain C# runtime model이다.
/// 생성과 변경 권한은 CrowdRoot가 단독으로 가지며, follower 목록은 AgentBuffer index와 병렬로 유지한다.
/// MemberCount는 leader를 포함한다(단독 leader = 1).
/// </summary>
public sealed class CrowdModel
{
    /// <summary>
    /// 현재 목표 heading(도)이다. Unity yaw 규약(0도 = +Z, Atan2(x, z))을 따른다.
    /// player crowd는 입력이, rival crowd는 RivalAiDriver의 결정이 기록한다.
    /// </summary>
    public float HeadingDeg;

    /// <summary>
    /// 팀 id, 팀 material, leader clone, leader의 AgentBuffer index, follower 사전할당 용량으로 model을 생성한다.
    /// follower 목록은 <paramref name="followerCapacity"/>만큼 미리 할당해, 전원이 한 crowd에 흡수되는
    /// 최악의 경우에도 tick 중 List 재할당(hot-path GC alloc)이 일어나지 않게 한다. CrowdRoot가 전체 agent
    /// 용량(리더+중립 총수)을 넘겨준다. 용량만 정하는 값이라 List 결과·순서·판정에는 영향이 없다.
    /// </summary>
    public CrowdModel(int teamId, Material teamMaterial, Human leader, int leaderAgentIndex, int followerCapacity)
    {
        TeamId = teamId;
        TeamMaterial = teamMaterial;
        Leader = leader;
        LeaderAgentIndex = leaderAgentIndex;
        Eliminated = false;
        Followers = new List<Human>(followerCapacity);
        FollowerAgentIndices = new List<int>(followerCapacity);
    }

    /// <summary>이 crowd의 팀 id다. 0은 player, 1..3은 rival이다.</summary>
    public int TeamId { get; }

    /// <summary>이 crowd 구성원 전원에게 적용하는 공유 material이다.</summary>
    public Material TeamMaterial { get; }

    /// <summary>leader clone이다. 탈락 후에는 null이다.</summary>
    public Human Leader { get; private set; }

    /// <summary>leader의 AgentBuffer index다. 탈락 후에는 -1이다.</summary>
    public int LeaderAgentIndex { get; private set; }

    /// <summary>이 crowd가 탈락했는지 여부다.</summary>
    public bool Eliminated { get; private set; }

    /// <summary>현재 인원 수다. follower 수 + (leader 생존 시 1)이며 단독 leader는 1이다.</summary>
    public int MemberCount
    {
        get { return Followers.Count + (Eliminated ? 0 : 1); }
    }

    /// <summary>follower clone 목록이다. <see cref="FollowerAgentIndices"/>와 병렬이다.</summary>
    public List<Human> Followers { get; }

    /// <summary>follower의 AgentBuffer index 목록이다. <see cref="Followers"/>와 병렬이다.</summary>
    public List<int> FollowerAgentIndices { get; }

    /// <summary>
    /// follower를 두 병렬 목록의 끝에 함께 추가한다.
    /// </summary>
    public void AddFollower(Human h, int agentIndex)
    {
        Followers.Add(h);
        FollowerAgentIndices.Add(agentIndex);
    }

    /// <summary>
    /// 지정한 AgentBuffer index의 follower를 swap-remove로 제거한다(목록 순서는 보존하지 않는다).
    /// 해당 index가 없으면 false를 반환하고 <paramref name="removed"/>는 null이다.
    /// </summary>
    public bool RemoveFollowerByAgentIndex(int agentIndex, out Human removed)
    {
        List<int> indices = FollowerAgentIndices;
        for (int i = 0; i < indices.Count; i++)
        {
            if (indices[i] != agentIndex)
            {
                continue;
            }

            removed = Followers[i];
            int last = indices.Count - 1;
            Followers[i] = Followers[last];
            indices[i] = indices[last];
            Followers.RemoveAt(last);
            indices.RemoveAt(last);
            return true;
        }

        removed = null;
        return false;
    }

    /// <summary>
    /// 이 crowd를 탈락 상태로 표시한다. Leader는 null, LeaderAgentIndex는 -1이 된다.
    /// ex-leader clone의 승자 crowd 편입 등 시각/구성 이전은 CrowdRoot의 commit 단계가 수행한다.
    /// </summary>
    public void MarkEliminated()
    {
        Leader = null;
        LeaderAgentIndex = -1;
        Eliminated = true;
    }
}
